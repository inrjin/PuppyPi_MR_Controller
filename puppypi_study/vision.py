"""Optional OpenCV QR decoding and file-only replay adapters.

No adapter opens a camera device or a network stream. Integrations should pass
arrays branched from the existing robot camera to ``OpenCvQrDecoder.decode``.
Image/video file replay uses an explicitly synthetic replay clock; absent source
capture timestamps remain absent. JSONL replay needs only the standard library.
"""

import json
import math
import threading
import time
from dataclasses import replace
from pathlib import Path
from typing import Any, Iterable, Iterator, Optional

from .framing import FrameObservation


class OpenCvUnavailable(RuntimeError):
    pass


class OpenCvQrDecoder:
    def __init__(self, target_marker: Optional[str] = None, cv2_module: Any = None) -> None:
        if target_marker is not None and (not isinstance(target_marker, str) or not target_marker):
            raise ValueError("target_marker must be a nonempty string or None")
        if cv2_module is None:
            try:
                import cv2 as cv2_module
            except ImportError as error:
                raise OpenCvUnavailable("QR decoding requires optional opencv-python-headless; JSONL replay does not") from error
        if not hasattr(cv2_module, "QRCodeDetector"):
            raise OpenCvUnavailable("installed OpenCV does not expose QRCodeDetector")
        self.cv2 = cv2_module
        self.target_marker = target_marker
        self._detector = cv2_module.QRCodeDetector()

    @staticmethod
    def _corners(points: Any) -> Optional[list]:
        if points is None:
            return None
        if hasattr(points, "tolist"):
            points = points.tolist()
        # Single-code API returns shape (1, 4, 2); multi-code entries (4, 2).
        if isinstance(points, (tuple, list)) and len(points) == 1:
            points = points[0]
        if not isinstance(points, (tuple, list)) or len(points) != 4:
            return None
        try:
            return [[float(x), float(y)] for x, y in points]
        except (TypeError, ValueError):
            return None

    def decode(self, image: Any, *, frame_id: int, received_mono_us: int,
               decoded_mono_us: Optional[int] = None, clock_id: str = "robot_monotonic",
               captured_mono_us: Optional[int] = None,
               capture_clock_id: Optional[str] = None) -> FrameObservation:
        started_ns = time.monotonic_ns()
        shape = getattr(image, "shape", ())
        width, height = (int(shape[1]), int(shape[0])) if len(shape) >= 2 else (0, 0)
        text, corners, valid, reason = None, None, True, None
        if width <= 0 or height <= 0:
            valid, reason = False, "invalid_image_size"
        else:
            try:
                candidates = []
                multi = getattr(self._detector, "detectAndDecodeMulti", None)
                if multi is not None:
                    found, texts, points, _ = multi(image)
                    if found and points is not None:
                        candidates = [(str(value), self._corners(point)) for value, point in zip(texts, points)]
                if not candidates:
                    value, points, _ = self._detector.detectAndDecode(image)
                    if value or points is not None:
                        candidates = [(str(value), self._corners(points))]
                candidates.sort(key=lambda item: (item[0], repr(item[1])))
                matches = [item for item in candidates if item[0] == self.target_marker] if self.target_marker is not None else []
                if len(matches) > 1:
                    valid, reason = False, "ambiguous_target_marker"
                elif candidates:
                    text, corners = matches[0] if matches else candidates[0]
            except Exception as error:
                # A decoder failure is an explicit invalid observation that resets
                # holds; it never masquerades as an empty, successful decode.
                valid, reason = False, "decoder_error:" + type(error).__name__
        finished_ns = time.monotonic_ns()
        completed = finished_ns // 1000 if decoded_mono_us is None else decoded_mono_us
        observation = FrameObservation(
            frame_id=frame_id, received_mono_us=received_mono_us, decoded_mono_us=completed,
            image_width=width, image_height=height, marker_text=text, corners=corners,
            clock_id=clock_id, captured_mono_us=captured_mono_us, capture_clock_id=capture_clock_id,
            data_valid=valid, invalid_reason=reason, decoder="opencv", processing_duration_us=(finished_ns - started_ns) // 1000,
        )
        problem = observation.validation_error()
        if problem is not None and observation.data_valid:
            return replace(observation, data_valid=False, invalid_reason=problem, corners=None)
        return observation


class LatestFrameQrWorker:
    """One in-flight decode plus at most one pending, copied camera frame.

    ``submit`` never decodes or writes files. It copies the caller's image before
    replacing the pending frame, so camera buffer reuse cannot mutate a queued
    image. Observation/error callbacks execute on this dedicated worker thread.
    Timestamps must already be in the supplied local ``clock_id``; a foreign
    capture clock can be retained separately. No camera device is opened.

    ``close`` discards pending work and suppresses callbacks not yet started.
    An in-flight native decoder or callback cannot be force-cancelled: close
    returns False if it is still running at the caller's bounded deadline.
    """

    def __init__(self, decoder: Any, on_observation: Any, on_error: Any = None,
                 *, clock_id: str = "robot_monotonic") -> None:
        if not callable(getattr(decoder, "decode", None)) or not callable(on_observation):
            raise ValueError("decoder.decode and on_observation must be callable")
        if on_error is not None and not callable(on_error):
            raise ValueError("on_error must be callable or None")
        if not isinstance(clock_id, str) or not clock_id:
            raise ValueError("clock_id must be a nonempty string")
        self.decoder = decoder
        self.clock_id = clock_id
        self._on_observation = on_observation
        self._on_error = on_error
        self._condition = threading.Condition()
        self._pending = None
        self._accepting = True
        self._in_flight = False
        self._counts = {"received": 0, "processed": 0, "replaced": 0, "failed": 0,
                        "discarded": 0, "rejected": 0, "callback_failed": 0}
        self._last_error = None
        self._thread = threading.Thread(target=self._run, name="study-qr-latest-frame", daemon=True)
        self._thread.start()

    @property
    def stats(self) -> dict:
        with self._condition:
            return {**self._counts, "pending": int(self._pending is not None), "in_flight": self._in_flight,
                    "accepting": self._accepting, "alive": self._thread.is_alive(), "last_error": self._last_error}

    def submit(self, image: Any, *, frame_id: int, received_mono_us: int,
               captured_mono_us: Optional[int] = None, capture_clock_id: Optional[str] = None) -> bool:
        with self._condition:
            if not self._accepting:
                self._counts["rejected"] += 1
                return False
            self._counts["received"] += 1
        try:
            copied = image.copy()
            if copied is image:
                raise ValueError("image.copy must return an independent buffer")
        except Exception as error:
            with self._condition:
                self._counts["failed"] += 1
                self._last_error = "image_copy_error:" + type(error).__name__
            return False
        job = (copied, {"frame_id": frame_id, "received_mono_us": received_mono_us,
                        "clock_id": self.clock_id, "captured_mono_us": captured_mono_us,
                        "capture_clock_id": capture_clock_id})
        with self._condition:
            if not self._accepting:
                self._counts["discarded"] += 1
                return False
            if self._pending is not None:
                self._counts["replaced"] += 1
            self._pending = job
            self._condition.notify()
        return True

    def _report_error(self, reason: str) -> None:
        with self._condition:
            self._last_error = reason
            notify = self._accepting and self._on_error is not None
        if notify:
            try:
                self._on_error(reason)
            except Exception as error:
                with self._condition:
                    self._counts["callback_failed"] += 1
                    self._last_error = "error_callback_failed:" + type(error).__name__

    def _run(self) -> None:
        while True:
            with self._condition:
                self._condition.wait_for(lambda: self._pending is not None or not self._accepting)
                if not self._accepting:
                    return
                image, metadata = self._pending
                self._pending = None
                self._in_flight = True
            failed = False
            try:
                observation = self.decoder.decode(image, **metadata)
                if not isinstance(observation, FrameObservation):
                    raise ValueError("decoder must return FrameObservation")
                failed = observation.validation_error() is not None
                with self._condition:
                    deliver = self._accepting
                if deliver:
                    try:
                        self._on_observation(observation)
                    except Exception as error:
                        failed = True
                        with self._condition:
                            self._counts["callback_failed"] += 1
                        self._report_error("observation_callback_failed:" + type(error).__name__)
                if failed:
                    self._report_error(observation.validation_error() or "observation_delivery_failed")
            except Exception as error:
                failed = True
                self._report_error("decoder_error:" + type(error).__name__)
            finally:
                with self._condition:
                    self._counts["processed"] += 1
                    self._counts["failed"] += int(failed)
                    self._in_flight = False

    def close(self, timeout_seconds: float = 1.0) -> bool:
        if isinstance(timeout_seconds, bool) or not isinstance(timeout_seconds, (int, float)) or not 0 <= timeout_seconds <= 60:
            raise ValueError("close timeout must be a finite number from 0 to 60 seconds")
        with self._condition:
            self._accepting = False
            if self._pending is not None:
                self._counts["discarded"] += 1
                self._pending = None
            self._condition.notify_all()
        if threading.current_thread() is self._thread:
            return False
        self._thread.join(timeout_seconds)
        return not self._thread.is_alive()


def _local_file(path: Any) -> Path:
    if isinstance(path, int):
        raise ValueError("camera devices are not accepted by file replay")
    result = Path(path)
    if not result.is_file():
        raise ValueError("replay input must be an existing local regular file: " + str(path))
    return result


def _replay_timing(start_mono_us: int, frame_interval_us: int, first_frame_id: int) -> None:
    for name, value in (("start_mono_us", start_mono_us), ("frame_interval_us", frame_interval_us), ("first_frame_id", first_frame_id)):
        if isinstance(value, bool) or not isinstance(value, int) or value < 0 or value > 2**63 - 1:
            raise ValueError(name + " must be nonnegative int64")
    if frame_interval_us <= 0:
        raise ValueError("frame_interval_us must be positive")


def load_observations(path: Any) -> Iterator[FrameObservation]:
    """Read deterministic mock or previously decoded frame observations."""
    with _local_file(path).open("r", encoding="utf-8") as stream:
        for line_number, line in enumerate(stream, 1):
            if not line.strip():
                continue
            try:
                data = json.loads(line, parse_constant=lambda value: (_ for _ in ()).throw(ValueError("nonfinite JSON number")))
                yield FrameObservation.from_dict(data)
            except (ValueError, TypeError) as error:
                raise ValueError("invalid observation at line " + str(line_number) + ": " + str(error)) from error


def iter_image_observations(paths: Iterable, decoder: OpenCvQrDecoder, *, start_mono_us: int = 0,
                            frame_interval_us: int = 100_000, first_frame_id: int = 0,
                            clock_id: str = "replay_monotonic") -> Iterator[FrameObservation]:
    """Decode still files on deterministic synthetic reception/completion times.

    Actual processing cost is recorded separately as processing_duration_us.
    Synthetic replay times never claim to be original camera capture timestamps.
    """
    _replay_timing(start_mono_us, frame_interval_us, first_frame_id)
    for index, path in enumerate(paths):
        image = decoder.cv2.imread(str(_local_file(path)))
        now = start_mono_us + index * frame_interval_us
        yield decoder.decode(image, frame_id=first_frame_id + index, received_mono_us=now,
                             decoded_mono_us=now, clock_id=clock_id)


def iter_video_observations(path: Any, decoder: OpenCvQrDecoder, *, start_mono_us: int = 0,
                            frame_interval_us: Optional[int] = None, first_frame_id: int = 0,
                            clock_id: str = "replay_monotonic") -> Iterator[FrameObservation]:
    video_path = _local_file(path)
    capture = decoder.cv2.VideoCapture(str(video_path))
    try:
        if not capture.isOpened():
            raise ValueError("unable to read video file: " + str(video_path))
        if frame_interval_us is None:
            fps = capture.get(decoder.cv2.CAP_PROP_FPS)
            if not isinstance(fps, (int, float)) or not math.isfinite(fps) or fps <= 0:
                raise ValueError("video has no valid FPS; provide frame_interval_us")
            frame_interval_us = max(1, round(1_000_000 / fps))
        _replay_timing(start_mono_us, frame_interval_us, first_frame_id)
        index = 0
        while True:
            ok, image = capture.read()
            if not ok:
                break
            now = start_mono_us + index * frame_interval_us
            yield decoder.decode(image, frame_id=first_frame_id + index, received_mono_us=now,
                                 decoded_mono_us=now, clock_id=clock_id)
            index += 1
    finally:
        capture.release()
