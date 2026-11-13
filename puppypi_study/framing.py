"""Deterministic QR framing evaluation without cameras, ROS or wall clocks.

Frame times and evaluator ``now`` must use the named evaluator clock. Capture
timestamps from other clocks are retained but never subtracted. In that case
freshness is explicitly reception age, not a claim about original capture age.
The deadline is exclusive: observations completed at/after it time out.
"""

import math
from dataclasses import dataclass, field
from typing import Any, Dict, List, Mapping, Optional, Sequence, Tuple

from .config import FramingConfig


def _timestamp(value: Any) -> bool:
    return isinstance(value, int) and not isinstance(value, bool) and 0 <= value <= 2**63 - 1


def _finite(value: Any) -> bool:
    try:
        return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)
    except OverflowError:
        return False


@dataclass(frozen=True)
class FrameObservation:
    frame_id: int
    received_mono_us: int
    decoded_mono_us: int
    image_width: int
    image_height: int
    marker_text: Optional[str] = None
    corners: Optional[Sequence[Sequence[float]]] = None
    clock_id: str = "robot_monotonic"
    captured_mono_us: Optional[int] = None
    capture_clock_id: Optional[str] = None
    data_valid: bool = True
    invalid_reason: Optional[str] = None
    decoder: str = "fixture"
    processing_duration_us: Optional[int] = None
    schema_version: int = 1

    def geometry(self) -> Optional[Tuple[float, float, float]]:
        """Return corner-mean center deviations and shoelace polygon area.

        ex=(mean_x-width/2)/width; ey=(mean_y-height/2)/height.
        Corner order may be clockwise or counterclockwise, but must trace a
        convex, nondegenerate quadrilateral inside the image pixel bounds.
        """
        if not (_timestamp(self.image_width) and self.image_width > 0 and
                _timestamp(self.image_height) and self.image_height > 0):
            return None
        if not isinstance(self.corners, (tuple, list)) or len(self.corners) != 4:
            return None
        points = []
        for point in self.corners:
            if not isinstance(point, (tuple, list)) or len(point) != 2 or not all(_finite(value) for value in point):
                return None
            x, y = point
            if not (0 <= x <= self.image_width - 1 and 0 <= y <= self.image_height - 1):
                return None
            points.append((float(x), float(y)))
        crosses = []
        for index in range(4):
            a, b, c = points[index], points[(index + 1) % 4], points[(index + 2) % 4]
            crosses.append((b[0] - a[0]) * (c[1] - b[1]) - (b[1] - a[1]) * (c[0] - b[0]))
        if not (all(value > 0 for value in crosses) or all(value < 0 for value in crosses)):
            return None
        area = abs(sum(points[i][0] * points[(i + 1) % 4][1] -
                       points[(i + 1) % 4][0] * points[i][1] for i in range(4))) / 2
        cx = sum(point[0] for point in points) / 4
        cy = sum(point[1] for point in points) / 4
        return ((cx - self.image_width / 2) / self.image_width,
                (cy - self.image_height / 2) / self.image_height,
                area / (self.image_width * self.image_height))

    def validation_error(self) -> Optional[str]:
        if type(self.schema_version) is not int or self.schema_version != 1:
            return "unsupported_frame_schema"
        if not _timestamp(self.frame_id):
            return "invalid_frame_id"
        if not isinstance(self.clock_id, str) or not self.clock_id:
            return "invalid_clock_id"
        if not _timestamp(self.received_mono_us) or not _timestamp(self.decoded_mono_us):
            return "invalid_frame_timestamp"
        if self.decoded_mono_us < self.received_mono_us:
            return "decode_before_receive"
        if self.processing_duration_us is not None and not _timestamp(self.processing_duration_us):
            return "invalid_processing_duration"
        if self.captured_mono_us is not None:
            if not _timestamp(self.captured_mono_us) or not isinstance(self.capture_clock_id, str) or not self.capture_clock_id:
                return "invalid_capture_timestamp"
            if self.capture_clock_id == self.clock_id and self.captured_mono_us > self.received_mono_us:
                return "capture_after_receive"
        elif self.capture_clock_id is not None:
            return "capture_clock_without_timestamp"
        if not isinstance(self.data_valid, bool):
            return "invalid_data_valid_flag"
        if not self.data_valid:
            return self.invalid_reason or "decoder_invalid"
        if self.marker_text is not None and not isinstance(self.marker_text, str):
            return "invalid_marker_text"
        # Unread frames are valid observations, with no marker geometry.
        if self.marker_text in (None, "") and self.corners is None:
            if not (_timestamp(self.image_width) and self.image_width > 0 and
                    _timestamp(self.image_height) and self.image_height > 0):
                return "invalid_image_size"
            return None
        if self.geometry() is None:
            return "invalid_geometry"
        return None

    def to_dict(self) -> Dict[str, Any]:
        return {
            "schema_version": self.schema_version,
            "frame_id": self.frame_id,
            "clock_id": self.clock_id,
            "captured_mono_us": self.captured_mono_us,
            "capture_clock_id": self.capture_clock_id,
            "received_mono_us": self.received_mono_us,
            "decoded_mono_us": self.decoded_mono_us,
            "image_width": self.image_width,
            "image_height": self.image_height,
            "marker_text": self.marker_text,
            "corners": None if self.corners is None else [list(point) for point in self.corners],
            "data_valid": self.data_valid,
            "invalid_reason": self.invalid_reason,
            "decoder": self.decoder,
            "processing_duration_us": self.processing_duration_us,
        }

    @classmethod
    def from_dict(cls, data: Mapping) -> "FrameObservation":
        if not isinstance(data, Mapping):
            raise ValueError("frame observation must be an object")
        try:
            return cls(**data)
        except TypeError as error:
            raise ValueError("invalid frame observation fields: " + str(error)) from error


@dataclass(frozen=True)
class FramingEvent:
    event_type: str
    t_mono_us: int
    payload: Dict[str, Any] = field(default_factory=dict)

    def to_dict(self) -> Dict[str, Any]:
        return {"event_type": self.event_type, "t_mono_us": self.t_mono_us, "payload": dict(self.payload)}


class FramingEvaluator:
    def __init__(self, config: FramingConfig, clock_id: str = "robot_monotonic") -> None:
        if not isinstance(config, FramingConfig):
            raise ValueError("config must be FramingConfig")
        if not isinstance(clock_id, str) or not clock_id:
            raise ValueError("clock_id must be a nonempty string")
        self.config = config
        self.clock_id = clock_id
        self.outcome = None
        self.trial_id = None
        self.started_at_us = None
        self.deadline_us = None
        self._last_now_us = None
        self._last_frame_id = None
        self._last_frame_time_us = None
        self._hold_start_us = None
        self._inside = False
        self._first_decoded = False
        self.last_evaluation = None

    def start(self, trial_id: str, start_mono_us: int, last_frame_id: Optional[int] = None) -> None:
        if not isinstance(trial_id, str) or not trial_id:
            raise ValueError("trial_id must be a nonempty string")
        if not _timestamp(start_mono_us) or start_mono_us + self.config.time_limit_us > 2**63 - 1:
            raise ValueError("invalid trial start time")
        if last_frame_id is not None and not _timestamp(last_frame_id):
            raise ValueError("invalid last_frame_id cutoff")
        if self.trial_id == trial_id:
            if self.started_at_us != start_mono_us:
                raise ValueError("same trial cannot be restarted with a different timestamp")
            return
        if self.trial_id is not None and self.outcome is None:
            raise ValueError("cannot replace an active trial")
        self.trial_id = trial_id
        self.started_at_us = start_mono_us
        self.deadline_us = start_mono_us + self.config.time_limit_us
        self.outcome = None
        self._last_now_us = start_mono_us
        self._last_frame_id = last_frame_id
        self._last_frame_time_us = None
        self._hold_start_us = None
        self._inside = False
        self._first_decoded = False
        self.last_evaluation = None

    def _event(self, kind: str, now: int, **payload: Any) -> FramingEvent:
        return FramingEvent(kind, now, {"trial_id": self.trial_id, **payload})

    def _reset(self, now: int, reason: str, frame_id: Optional[int] = None) -> List[FramingEvent]:
        events = []
        if self._inside:
            events.append(self._event("framing_exited", now, frame_id=frame_id, reason=reason))
        if self._hold_start_us is not None:
            events.append(self._event("framing_hold_reset", now, frame_id=frame_id, reason=reason,
                                      hold_started_mono_us=self._hold_start_us))
        self._inside = False
        self._hold_start_us = None
        return events

    def tick(self, now_mono_us: int) -> List[FramingEvent]:
        if not _timestamp(now_mono_us):
            raise ValueError("now_mono_us must be nonnegative int64 microseconds")
        if self.trial_id is None or self.outcome is not None:
            return []
        if now_mono_us < self._last_now_us:
            return self._reset(self._last_now_us, "time_reversed")
        self._last_now_us = now_mono_us
        if now_mono_us >= self.deadline_us:
            events = self._reset(self.deadline_us, "timeout")
            self.outcome = "timeout"
            events.append(self._event("trial_timeout", self.deadline_us, reason="time_limit"))
            return events
        if self._last_frame_time_us is not None and now_mono_us - self._last_frame_time_us > self.config.max_frame_gap_us:
            return self._reset(now_mono_us, "frame_gap")
        return []

    def observe(self, frame: FrameObservation, now_mono_us: Optional[int] = None,
                neutral_axes: Optional[Sequence[float]] = None) -> List[FramingEvent]:
        if not isinstance(frame, FrameObservation):
            raise ValueError("frame must be FrameObservation")
        if now_mono_us is None and frame.clock_id != self.clock_id:
            raise ValueError("a foreign-clock observation requires an explicit local now_mono_us")
        now = frame.decoded_mono_us if now_mono_us is None else now_mono_us
        if not _timestamp(now):
            raise ValueError("observation time must be nonnegative int64 microseconds")
        if self.trial_id is None or self.outcome is not None:
            return []
        if now < self._last_now_us:
            return self._reset(self._last_now_us, "time_reversed", frame.frame_id)
        # A completed frame cannot retrospectively rescue an expired trial.
        if now >= self.deadline_us:
            return self.tick(now)
        self._last_now_us = now
        reason = frame.validation_error()
        capture_known = frame.captured_mono_us is not None and frame.capture_clock_id == self.clock_id
        frame_time = frame.captured_mono_us if capture_known else frame.received_mono_us
        age = now - frame_time if frame.clock_id == self.clock_id and _timestamp(frame_time) else None
        new_frame = _timestamp(frame.frame_id) and (self._last_frame_id is None or frame.frame_id > self._last_frame_id)
        geometry = frame.geometry()
        assessment = {
            "frame_id": frame.frame_id,
            "new_frame": new_frame,
            "frame_age_us": age,
            "freshness_basis": "capture" if capture_known else "reception",
            "capture_age_known": capture_known,
            "receive_to_decode_us": (frame.decoded_mono_us - frame.received_mono_us
                                   if _timestamp(frame.decoded_mono_us) and _timestamp(frame.received_mono_us) else None),
            "processing_duration_us": frame.processing_duration_us,
            "ex": geometry[0] if geometry is not None else None,
            "ey": geometry[1] if geometry is not None else None,
            "area_ratio": geometry[2] if geometry is not None else None,
        }
        if reason is None and frame.clock_id != self.clock_id:
            reason = "clock_mismatch"
        if reason is None and (frame.decoded_mono_us > now or age < 0):
            reason = "future_frame"
        if reason is None and not new_frame:
            reason = "duplicate_or_reversed_frame"
        if reason is None and frame_time < self.started_at_us:
            reason = "before_trial"
        if reason is None and age > self.config.max_frame_age_us:
            reason = "stale_frame"
        previous_time = self._last_frame_time_us
        if reason is None and previous_time is not None and frame_time <= previous_time:
            reason = "frame_time_reversed"
        if reason is None and previous_time is not None and frame_time - previous_time > self.config.max_frame_gap_us:
            reason = "frame_gap"
        # Advance high-water marks on validly timestamped, new records even if
        # their marker/geometry is bad. The next frame must still be genuinely new.
        if new_frame and frame.clock_id == self.clock_id:
            self._last_frame_id = frame.frame_id
            if _timestamp(frame_time) and frame_time <= now:
                self._last_frame_time_us = max(frame_time, previous_time or 0)
        events = []
        if reason is None and frame.marker_text == self.config.target_marker and not self._first_decoded:
            self._first_decoded = True
            events.append(self._event("first_marker_decoded", now, **assessment))
        if reason is None and frame.marker_text != self.config.target_marker:
            reason = "unread_marker" if frame.marker_text in (None, "") else "wrong_marker"
        if reason is None and geometry is None:
            reason = "invalid_geometry"
        if reason is None and (abs(geometry[0]) > self.config.center_tolerance_x or
                               abs(geometry[1]) > self.config.center_tolerance_y):
            reason = "center_outside"
        if reason is None and not self.config.area_ratio_min <= geometry[2] <= self.config.area_ratio_max:
            reason = "area_outside"
        if reason is None and self.config.require_neutral:
            neutral_valid = (isinstance(neutral_axes, (tuple, list)) and len(neutral_axes) == 2 and
                             all(_finite(axis) and abs(axis) <= self.config.neutral_threshold for axis in neutral_axes))
            if not neutral_valid:
                reason = "command_not_neutral"
        assessment["valid"] = reason is None
        assessment["reason"] = reason
        self.last_evaluation = assessment
        if reason is not None:
            return events + self._reset(now, reason, frame.frame_id)
        if not self._inside:
            self._inside = True
            events.append(self._event("framing_entered", now, **assessment))
        if self._hold_start_us is None:
            self._hold_start_us = frame_time
            events.append(self._event("framing_hold_started", now, hold_started_mono_us=frame_time, **assessment))
        if frame_time - self._hold_start_us >= self.config.hold_us:
            self.outcome = "success"
            events.append(self._event("trial_success", now, hold_started_mono_us=self._hold_start_us,
                                      hold_elapsed_us=frame_time - self._hold_start_us, **assessment))
        return events
