"""Study state machine. This module never sends locomotion packets.

Client reports and adapter reports are distinct. A non-mock deployment remains
unready until an operator supplies a verified robot-side ingress adapter. The
adapter's terminal-neutral callback must cooperate with the existing v2 server;
it must never clear emergency-stop latches or impersonate a second UDP client.
"""

from collections import OrderedDict
import copy
from dataclasses import dataclass
import hashlib
import json
import math
import re
import threading
import time
import uuid


class StudyError(Exception):
    def __init__(self, code, message, http_status=400):
        super().__init__(message)
        self.code = code
        self.http_status = http_status


def identifier(value, name="id", maximum=128):
    if not isinstance(value, str) or not re.fullmatch(r"[A-Za-z0-9_.:-]{1,%d}" % maximum, value):
        raise StudyError("invalid_id", "invalid " + name)
    return value


def client_uuid(value):
    try:
        parsed = uuid.UUID(value)
        if str(parsed) != value.lower():
            raise ValueError()
    except (TypeError, AttributeError, ValueError):
        raise StudyError("invalid_client_id", "client_id must be the actual v2 UUID")
    return str(parsed)


def finite_number(value, name, minimum=None, maximum=None):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        raise StudyError("invalid_number", name + " must be finite")
    if minimum is not None and value < minimum or maximum is not None and value > maximum:
        raise StudyError("invalid_number", name + " is out of range")
    return value


def bounded_object(value, limit=16384):
    if not isinstance(value, dict):
        raise StudyError("invalid_object", "expected JSON object")
    try:
        raw = json.dumps(value, allow_nan=False, sort_keys=True)
    except (TypeError, ValueError):
        raise StudyError("invalid_json", "non-finite or non-JSON data")
    if len(raw.encode("utf-8")) > limit:
        raise StudyError("payload_too_large", "object exceeds bounds", 413)
    return copy.deepcopy(value)


@dataclass(frozen=True)
class RuntimePolicy:
    countdown_seconds: float = 3.0
    signal_ack_timeout_seconds: float = 5.0
    client_timeout_seconds: float = 2.0
    robot_timeout_seconds: float = 2.0
    clock_max_error_us: int = 100000
    clock_sync_max_age_seconds: float = 30.0
    max_requests: int = 20000
    max_events: int = 200000

    def __post_init__(self):
        for name in ("countdown_seconds", "signal_ack_timeout_seconds",
                     "client_timeout_seconds", "robot_timeout_seconds",
                     "clock_sync_max_age_seconds"):
            finite_number(getattr(self, name), name, 0 if name == "countdown_seconds" else .001, 86400)
        if self.clock_max_error_us < 0 or self.max_requests < 1 or self.max_events < 1:
            raise ValueError("invalid runtime policy")


CLIENT_EVENTS = frozenset({"signal_shown", "clock_sync", "input_sample", "command_sent",
                           "client_ack", "ack_received", "block_started", "block_ended",
                           "ack_observed", "control_event_sent", "client_settings_snapshot",
                           "logging_started", "logging_stopped", "logging_interrupted",
                           "neutral_requested", "neutral_confirmed", "tracking_lost",
                           "tracking_recovered", "grab", "release", "focus_changed",
                           "pause_changed", "settings_violation", "client_error"})
TERMINAL = frozenset({"success", "timeout", "aborted"})


class StudyRuntime:
    def __init__(self, config, journal, *, mock_mode=False, clock=None,
                 clock_id=None, policy=None, neutral_callback=None):
        self.config = config
        self.journal = journal
        self.mock_mode = bool(mock_mode)
        self.clock = clock or (lambda: time.monotonic_ns() // 1000)
        self.clock_id = clock_id or "study_monotonic_" + uuid.uuid4().hex
        self.policy = policy or RuntimePolicy()
        self.neutral_callback = neutral_callback
        self._lock = threading.RLock()
        self._requests = OrderedDict()
        self._event_ids = set()
        self._trial_ids = set()
        self._clients = {}
        self._alignments = {}
        self._session = None
        self._trial = None
        self._state = "setup"
        self._reason = "no_session"
        self._evaluator = None
        self._last_frame_id = None
        self._robot_status = None
        self._robot_status_at = None
        self._camera_at = None
        self._decoder_healthy = False
        self._closed = False
        self._record("logging_started", {"mock_mode": self.mock_mode,
                     "code_version": "puppypi_study/0.1.0", "clock_id": self.clock_id})

    def _record(self, event_type, payload=None, *, source="runtime", record_type="event", at=None):
        session = self._session or {}
        trial = self._trial or {}
        record = {"schema_version": 1, "event_id": str(uuid.uuid4()),
                  "experiment_session_id": session.get("experiment_session_id"),
                  "trial_id": trial.get("trial_id"), "participant_id": session.get("participant_id"),
                  "interface_id": trial.get("interface_id"), "period_index": trial.get("period_index"),
                  "difficulty_id": trial.get("difficulty_id"), "source": source,
                  "clock_id": self.clock_id, "t_mono_us": self.clock() if at is None else at,
                  "record_type": record_type, "event_type": event_type,
                  "payload": bounded_object(payload or {}, 60000)}
        self.journal.emit(record)
        return record

    def _once(self, operation, body, action):
        request_id = identifier(body.get("request_id"), "request_id")
        fingerprint = hashlib.sha256(json.dumps(body, sort_keys=True, allow_nan=False).encode()).hexdigest()
        key = operation + ":" + request_id
        if key in self._requests:
            old_hash, result = self._requests[key]
            if old_hash != fingerprint:
                raise StudyError("idempotency_conflict", "request_id reused with different content", 409)
            if (result.get("experiment_session_id") != (self._session or {}).get("experiment_session_id") or
                    result.get("trial_id") and result.get("trial_id") != (self._trial or {}).get("trial_id")):
                raise StudyError("stale_request", "request belongs to an earlier session/trial", 409)
            # Never replay an old running permission after the trial has ended.
            current = self.status((self._trial or {}).get("control_client_id"))
            current["idempotent_replay"] = True
            current["accepted_events"] = 0
            return current
        if len(self._requests) >= self.policy.max_requests:
            raise StudyError("request_capacity", "start a new service journal; idempotency capacity reached", 503)
        result = action()
        self._requests[key] = (fingerprint, {"experiment_session_id": result.get("experiment_session_id"),
                                           "trial_id": result.get("trial_id")})
        return result

    def create_session(self, body):
        body = bounded_object(body)
        with self._lock:
            def action():
                if self._trial and self._state not in TERMINAL:
                    raise StudyError("trial_active", "abort or finish the current trial first", 409)
                order = body.get("interface_order", ["mr_ballgrip"])
                if (not isinstance(order, list) or not order or len(order) > 2 or
                        len(set(order)) != len(order) or "mr_ballgrip" not in order or any(
                            item not in {"smartphone_touch", "mr_ballgrip"} for item in order)):
                    raise StudyError("unsupported_interface", "interface_order must contain MR and optionally smartphone_touch")
                self._session = {"experiment_session_id": str(uuid.uuid4()),
                    "participant_id": identifier(body.get("participant_id"), "participant_id"),
                    "interface_order": order, "metadata": bounded_object(body.get("metadata", {}))}
                self._trial = None
                self._event_ids.clear()
                self._state = "setup"
                self._reason = "awaiting_trial"
                self._record("session_started", dict(self._session, mock_mode=self.mock_mode,
                             config_snapshot=self.config.to_dict(), config_hash=self.config.sha256))
                return self.status()
            return self._once("session", body, action)

    def register_client(self, body):
        body = bounded_object(body)
        with self._lock:
            client_id = client_uuid(body.get("client_id"))
            if body.get("interface_id") != "mr_ballgrip":
                raise StudyError("unsupported_interface", "only mr_ballgrip is implemented")
            source_clock = identifier(body.get("source_clock_id"), "source_clock_id")
            source_time = finite_number(body.get("source_t_mono_us"), "source_t_mono_us", 0)
            for field in ("engaged", "input_valid", "neutral_confirmed", "control_connected",
                          "last_accepted", "emergency"):
                if type(body.get(field)) is not bool:
                    raise StudyError("invalid_client_state", field + " must be boolean")
            if self._trial and self._state not in TERMINAL and client_id != self._trial["control_client_id"]:
                raise StudyError("wrong_client", "another v2 client owns this trial", 409)
            old = self._clients.get(client_id)
            if old is None and len(self._clients) >= 8:
                raise StudyError("client_capacity", "restart service to clear inactive client registrations", 503)
            if old and old["source_clock_id"] != source_clock and self._trial and self._state not in TERMINAL:
                self._finish("aborted", "client_clock_changed")
                raise StudyError("client_epoch_changed", "client clock changed during trial", 409)
            if old and old["source_clock_id"] == source_clock and source_time < old["source_t_mono_us"]:
                raise StudyError("client_time_reversed", "heartbeat source time reversed", 409)
            body["client_id"] = client_id
            body["received_mono_us"] = self.clock()
            body["settings"] = bounded_object(body.get("settings", {}))
            self._clients[client_id] = body
            self._record("client_heartbeat", dict(body, report_origin="client_claim"),
                         source="client", record_type="sample")
            if self._trial and self._state in {"countdown", "running"}:
                if body["emergency"] or not body["control_connected"] or not body["last_accepted"]:
                    self._finish("aborted", "client_control_unavailable")
                elif self._trial["settings"] != body["settings"]:
                    self._finish("aborted", "settings_changed")
                elif (self._state == "countdown" and self.clock() < self._trial["signal_due_mono_us"] and
                      (body["engaged"] or not body["neutral_confirmed"])):
                    self._finish("aborted", "release_and_neutral_required")
            return self.status(client_id)

    def readiness(self, client_id):
        now = self.clock()
        reasons = []
        client = self._clients.get(client_id)
        if not client or now - client["received_mono_us"] > self.policy.client_timeout_seconds * 1e6:
            reasons.append("client_heartbeat_stale")
        else:
            if not client["control_connected"] or not client["last_accepted"]:
                reasons.append("client_control_unavailable")
            if not client["neutral_confirmed"] or client["engaged"]:
                reasons.append("release_and_neutral_required")
            if client["emergency"]:
                reasons.append("emergency_stop")
            if client.get("event_delivery_ready", True) is not True:
                reasons.append("client_event_delivery_pending")
        if not self.journal.healthy:
            reasons.append("logging_unavailable")
        if not self.mock_mode:
            robot = self._robot_status
            if not robot or self._robot_status_at is None:
                reasons.append("adapter_unavailable")
            elif now - self._robot_status_at > self.policy.robot_timeout_seconds * 1e6:
                reasons.append("robot_status_stale")
            else:
                if robot.get("control_client_id") != client_id or not robot.get("controller_active"):
                    reasons.append("robot_client_mismatch")
                if not robot.get("neutral_confirmed"):
                    reasons.append("robot_neutral_unconfirmed")
                if robot.get("emergency_stop"):
                    reasons.append("robot_emergency_stop")
            if self._camera_at is None or now - self._camera_at > self.config.framing.max_frame_age_us:
                reasons.append("camera_stale")
            if not self._decoder_healthy:
                reasons.append("decoder_unavailable")
        return reasons

    def _check_trial(self, body):
        if not self._session or body.get("experiment_session_id") != self._session["experiment_session_id"]:
            raise StudyError("wrong_session", "experiment session mismatch", 409)
        if not self._trial or body.get("trial_id") != self._trial["trial_id"]:
            raise StudyError("wrong_trial", "trial mismatch", 409)
        if client_uuid(body.get("control_client_id")) != self._trial["control_client_id"]:
            raise StudyError("wrong_client", "v2 client mismatch", 409)

    def start_trial(self, body):
        body = bounded_object(body)
        with self._lock:
            def action():
                if not self._session or body.get("experiment_session_id") != self._session["experiment_session_id"]:
                    raise StudyError("wrong_session", "create or select the experiment session first", 409)
                if self._trial and self._state not in TERMINAL:
                    raise StudyError("trial_active", "trial is already active", 409)
                trial_id = identifier(body.get("trial_id"), "trial_id")
                if trial_id in self._trial_ids:
                    raise StudyError("trial_id_reused", "retry must use a new trial_id", 409)
                client_id = client_uuid(body.get("control_client_id"))
                if body.get("interface_id") != "mr_ballgrip":
                    raise StudyError("unsupported_interface", "only MR is implemented")
                period = body.get("period_index", 1)
                if type(period) is not int or not 1 <= period <= len(self._session["interface_order"]):
                    raise StudyError("invalid_period", "period_index is outside interface_order")
                if self._session["interface_order"][period - 1] != "mr_ballgrip":
                    raise StudyError("invalid_period", "period_index must identify MR in interface_order")
                difficulty = identifier(body.get("difficulty_id"), "difficulty_id")
                try:
                    profile = self.config.profile(difficulty)
                except (ValueError, KeyError) as error:
                    raise StudyError("invalid_difficulty", str(error))
                reasons = self.readiness(client_id)
                if reasons:
                    raise StudyError("not_ready", ",".join(reasons), 409)
                settings = bounded_object(body.get("settings", self._clients[client_id].get("settings", {})))
                # Freeze the settings actually reported by the client; never silently apply guessed values.
                if settings != self._clients[client_id].get("settings", {}):
                    raise StudyError("settings_mismatch", "client must apply and acknowledge trial settings first", 409)
                self._trial = {"trial_id": trial_id, "control_client_id": client_id,
                    "interface_id": "mr_ballgrip", "period_index": period,
                    "difficulty_id": difficulty, "practice": bool(body.get("practice", False)),
                    "settings": settings, "signal_id": str(uuid.uuid4()),
                    "prepared_mono_us": self.clock(), "profile": profile,
                    "retry_of_trial_id": body.get("retry_of_trial_id"), "outcome": None}
                self._trial_ids.add(trial_id)
                self._state = "ready"
                self._reason = "ready"
                self._record("trial_prepared", self._snapshot())
                self._state = "countdown"
                self._reason = "countdown"
                self._trial["signal_due_mono_us"] = self.clock() + math.ceil(self.policy.countdown_seconds * 1e6)
                self._record("countdown_started", {"signal_id": self._trial["signal_id"],
                    "signal_due_mono_us": self._trial["signal_due_mono_us"]})
                return self.status(client_id)
            return self._once("start", body, action)

    def _snapshot(self):
        return {"control_client_id": self._trial["control_client_id"],
                "config_snapshot": self.config.to_dict(), "config_hash": self.config.sha256,
                "config_version": self.config.to_dict().get("config_version"),
                "interface_order": self._session["interface_order"],
                "assignment_seed": self._session["metadata"].get("assignment_seed"),
                "assignment_method": self._session["metadata"].get("assignment_method", "operator_selected"),
                "practice": self._trial["practice"], "settings": self._trial["settings"],
                "difficulty_id": self._trial["difficulty_id"],
                "retry_of_trial_id": self._trial["retry_of_trial_id"], "mock_mode": self.mock_mode}

    def abort_trial(self, body):
        body = bounded_object(body)
        with self._lock:
            def action():
                self._check_trial(body)
                self._finish("aborted", identifier(body.get("reason", "operator_abort"), "reason"))
                return self.status(self._trial["control_client_id"])
            return self._once("abort", body, action)

    def client_events(self, body):
        body = bounded_object(body, 60000)
        with self._lock:
            self._check_trial(body)
            events = body.get("events")
            if not isinstance(events, list) or not 1 <= len(events) <= 128:
                raise StudyError("invalid_events", "events must contain 1..128 entries")
            # Validate the entire batch before accepting any entry.
            validated = []
            for event in events:
                event = bounded_object(event)
                identifier(event.get("event_id"), "event_id")
                if event.get("event_type") not in CLIENT_EVENTS:
                    raise StudyError("invalid_event_type", "client cannot submit robot/runtime events")
                identifier(event.get("source_clock_id"), "source_clock_id")
                finite_number(event.get("source_t_mono_us"), "source_t_mono_us", 0)
                bounded_object(event.get("payload", {}))
                validated.append(event)
            if len(self._event_ids) + len(validated) > self.policy.max_events:
                self._finish("aborted", "event_capacity")
                raise StudyError("event_capacity", "event deduplication capacity reached", 503)
            def action():
                accepted = 0
                for event in validated:
                    key = (self._trial["trial_id"], event["event_id"])
                    if key in self._event_ids:
                        continue
                    self._handle_client_event(event)
                    self._event_ids.add(key)
                    accepted += 1
                result = self.status(self._trial["control_client_id"])
                result["accepted_events"] = accepted
                return result
            return self._once("events", body, action)

    def _handle_client_event(self, event):
        kind = event["event_type"]
        now = self.clock()
        payload = bounded_object(event.get("payload", {}))
        if kind == "clock_sync":
            offset = finite_number(payload.get("offset_us"), "offset_us", -1e18, 1e18)
            error = finite_number(payload.get("uncertainty_us"), "uncertainty_us", 0, 1e12)
            valid = (payload.get("valid") is True and error <= self.policy.clock_max_error_us and
                     payload.get("reference_clock_id") == self.clock_id and
                     payload.get("source_clock_id", event["source_clock_id"]) == event["source_clock_id"])
            self._alignments[event["source_clock_id"]] = (offset, error, valid, now)
        alignment = self._alignments.get(event["source_clock_id"])
        valid = bool(alignment and alignment[2] and
                     now - alignment[3] <= self.policy.clock_sync_max_age_seconds * 1e6)
        aligned = round(event["source_t_mono_us"] + alignment[0]) if valid else None
        payload.update(source_t_mono_us=event["source_t_mono_us"],
                       source_clock_id=event["source_clock_id"],
                       source_event_id=event["event_id"], aligned_t_mono_us=aligned,
                       clock_alignment_error_us=alignment[1] if alignment else None,
                       clock_alignment_valid=valid, report_origin="client_claim")
        if kind == "signal_shown":
            if payload.get("signal_id") != self._trial["signal_id"]:
                raise StudyError("wrong_signal", "signal_id mismatch", 409)
            if self._state == "countdown":
                due = self._trial["signal_due_mono_us"]
                if now < due:
                    raise StudyError("signal_early", "countdown has not completed", 409)
                if now >= due + self.policy.signal_ack_timeout_seconds * 1e6:
                    self._finish("aborted", "signal_ack_timeout")
                    raise StudyError("signal_expired", "start signal expired", 409)
                if not valid or aligned is None:
                    raise StudyError("signal_clock_unaligned", "valid bounded clock alignment is required before GO", 409)
                if aligned < self._trial["prepared_mono_us"] - alignment[1] or aligned > now + alignment[1]:
                    raise StudyError("signal_clock_out_of_range", "signal timestamp is outside the current trial", 409)
                from .framing import FramingEvaluator
                self._record(kind, payload, source="client")
                # No pre-trial or network-delayed frame may contribute to the hold.
                self._evaluator = FramingEvaluator(self._trial["profile"], clock_id=self.clock_id)
                self._evaluator.start(self._trial["trial_id"], now, last_frame_id=self._last_frame_id)
                self._trial["started_mono_us"] = now
                self._state = "running"
                self._reason = "running"
                snapshot = self._snapshot()
                snapshot.update(evaluator_start_mono_us=now,
                                signal_alignment_valid=valid, last_frame_id=self._last_frame_id)
                self._record("trial_started", snapshot)
                return
            if self._state != "running":
                raise StudyError("trial_not_startable", "trial is terminal", 409)
        if self._state in TERMINAL:
            payload["late_after_terminal"] = True
        self._record(kind, payload, source="client",
                     record_type="sample" if kind == "input_sample" else "event")
        if (kind in {"settings_violation", "client_error"} or
                kind == "logging_interrupted" and payload.get("dropped_count", 0) > 0) and self._state not in TERMINAL:
            self._finish("aborted", kind)

    def clock_exchange(self, body):
        recv = self.clock()
        client_uuid(body.get("client_id"))
        identifier(body.get("source_clock_id"), "source_clock_id")
        finite_number(body.get("source_t_mono_us"), "source_t_mono_us", 0)
        return {"schema_version": 1, "clock_id": self.clock_id,
                "server_recv_us": recv, "server_send_us": self.clock(),
                "source_t_mono_us": body["source_t_mono_us"]}

    def ingest_robot_status(self, status):
        """Trusted in-process/stdin adapter boundary, never exposed as an HTTP route."""
        status = bounded_object(status)
        client_uuid(status.get("control_client_id"))
        for field in ("controller_active", "neutral_confirmed", "emergency_stop"):
            if type(status.get(field)) is not bool:
                raise StudyError("invalid_robot_status", field + " must be boolean")
        with self._lock:
            self._robot_status = status
            self._robot_status_at = self.clock()
            self._record("robot_status", dict(status, report_origin="adapter_report"), source="adapter")
            if self._trial and self._state not in TERMINAL and (
                    status["control_client_id"] != self._trial["control_client_id"] or
                    not status["controller_active"] or status["emergency_stop"]):
                self._finish("aborted", "robot_control_unavailable")

    def ingest_observation(self, observation, neutral_axes=None):
        from .framing import FrameObservation
        if isinstance(observation, dict):
            observation = FrameObservation.from_dict(observation)
        with self._lock:
            now = self.clock()
            if observation.clock_id != self.clock_id:
                raise StudyError("wrong_frame_clock", "adapter frame must use this service clock")
            if observation.data_valid is not True:
                self._record("decoder_invalid_frame", {"observation": observation.to_dict(),
                             "reason": observation.invalid_reason or "decoder_invalid"},
                             source="adapter", record_type="sample")
                self.adapter_failed(observation.invalid_reason or "decoder_invalid")
                return []
            # Duplicate/reversed frames never refresh readiness freshness.
            if (type(observation.frame_id) is int and
                    (self._last_frame_id is None or observation.frame_id > self._last_frame_id) and
                    type(observation.received_mono_us) is int and
                    0 <= now - observation.received_mono_us <= self.policy.robot_timeout_seconds * 1e6):
                self._last_frame_id = observation.frame_id
                self._camera_at = observation.received_mono_us
                self._decoder_healthy = True
            if self._state != "running":
                self._record("frame_received", {"observation": observation.to_dict(),
                             "outside_running_trial": True}, source="adapter", record_type="sample")
                return []
            payload = {"observation": observation.to_dict(), "evaluation_now_mono_us": now,
                       "neutral_axes": list(neutral_axes) if neutral_axes is not None else None}
            self._record("detection_sample", payload, source="adapter", record_type="sample")
            events = self._evaluator.observe(observation, now_mono_us=now, neutral_axes=neutral_axes)
            self._consume_framing(events)
            return events

    def ingest_control_event(self, event_type, payload):
        """Trusted adapter hook for actual v2 receive/accept/ACK and optional pose.

        This is an embedding/stdin contract, not an installed robot integration.
        The caller must observe the existing v2 handler, never create another
        motion client. Client HTTP events cannot call this hook.
        """
        if event_type not in {"robot_command_received", "robot_command_accepted", "robot_ack_sent", "robot_pose"}:
            raise StudyError("invalid_adapter_event", "unsupported robot adapter event")
        payload = bounded_object(payload)
        client = client_uuid(payload.get("control_client_id"))
        if event_type != "robot_pose":
            sequence = payload.get("sequence")
            if type(sequence) is not int or not 0 <= sequence <= 9007199254740991:
                raise StudyError("invalid_sequence", "sequence must be a safe nonnegative integer")
        with self._lock:
            if self._trial and self._state not in TERMINAL and client != self._trial["control_client_id"]:
                raise StudyError("wrong_client", "adapter event belongs to another control client", 409)
            payload["report_origin"] = "adapter_report"
            self._record(event_type, payload, source="adapter", record_type="sample")

    def adapter_failed(self, reason="adapter_failed"):
        with self._lock:
            self._decoder_healthy = False
            self._record("adapter_error", {"reason": str(reason)[:256]}, source="adapter")
            if self._trial and self._state not in TERMINAL:
                self._finish("aborted", "adapter_failed")

    def _consume_framing(self, events):
        for event in events:
            if event.event_type in {"trial_success", "trial_timeout"}:
                self._finish("success" if event.event_type == "trial_success" else "timeout",
                             event.payload.get("reason", event.event_type), at=event.t_mono_us,
                             terminal_payload=event.payload)
            else:
                self._record(event.event_type, event.payload, source="framing", at=event.t_mono_us)

    def tick(self):
        with self._lock:
            if self._closed or not self._trial or self._state in TERMINAL:
                return
            now = self.clock()
            client = self._clients.get(self._trial["control_client_id"])
            if not self.journal.healthy:
                self._finish("aborted", "logging_unavailable")
            elif not client or now - client["received_mono_us"] > self.policy.client_timeout_seconds * 1e6:
                self._finish("aborted", "client_heartbeat_stale")
            elif not self.mock_mode and (self._robot_status_at is None or self._camera_at is None or
                    now - self._robot_status_at > self.policy.robot_timeout_seconds * 1e6 or
                    now - self._camera_at > self.policy.robot_timeout_seconds * 1e6):
                self._finish("aborted", "robot_or_camera_stale")
            elif self._state == "countdown":
                due = self._trial["signal_due_mono_us"]
                if now >= due + self.policy.signal_ack_timeout_seconds * 1e6:
                    self._finish("aborted", "signal_ack_timeout")
                elif now >= due and not self._trial.get("signal_requested"):
                    self._trial["signal_requested"] = True
                    self._record("start_signal_requested", {"signal_id": self._trial["signal_id"]})
            elif self._state == "running":
                events = self._evaluator.tick(now)
                if events:
                    self._record("framing_tick", {"evaluation_now_mono_us": now}, record_type="sample")
                    self._consume_framing(events)

    def _finish(self, outcome, reason, at=None, terminal_payload=None):
        if not self._trial or self._state in TERMINAL:
            return
        self._state = outcome
        self._reason = reason
        self._trial["outcome"] = outcome
        self._trial["ended_mono_us"] = self.clock() if at is None else at
        payload = dict(terminal_payload or {}, outcome=outcome, reason=reason,
                       control_client_id=self._trial["control_client_id"],
                       mock_mode=self.mock_mode, physical_stop_measured=False)
        self._record("trial_" + outcome, payload, at=at)
        self._record("trial_ended", payload, at=at)
        self._record("terminal_neutral_requested", {"control_client_id": self._trial["control_client_id"],
                     "clear_emergency": False, "requires_release_neutral_and_new_engagement": True})
        if self.neutral_callback:
            # Adapter callbacks must enqueue work, never block or do device I/O here.
            try:
                self.neutral_callback(self._trial["control_client_id"], reason)
            except Exception as error:
                self._record("adapter_neutral_error", {"error": type(error).__name__})

    def status(self, client_id=None):
        with self._lock:
            self.tick()
            trial = self._trial or {}
            session = self._session or {}
            matching = client_id is None or client_id == trial.get("control_client_id")
            reasons = self.readiness(client_id or trial.get("control_client_id"))
            settings = trial.get("settings", {})
            now = self.clock()
            state = self._state
            if not trial and session and not reasons:
                state = "ready"
            result = {"schema_version": 1, "clock_id": self.clock_id, "server_t_mono_us": now,
                "experiment_session_id": session.get("experiment_session_id", ""),
                "participant_id": session.get("participant_id", ""),
                "interface_order": session.get("interface_order", []),
                "trial_id": trial.get("trial_id", ""), "interface_id": trial.get("interface_id", "mr_ballgrip"),
                "control_client_id": trial.get("control_client_id", ""),
                "period_index": trial.get("period_index", 0), "difficulty_id": trial.get("difficulty_id", ""),
                "state": state, "ready": not reasons, "readiness_reasons": reasons,
                "signal_id": trial.get("signal_id", ""),
                "show_start_signal": matching and state == "countdown" and now >= trial.get("signal_due_mono_us", now + 1),
                "motion_permitted": matching and state == "running" and self.journal.healthy,
                "neutral_requested": bool(trial) and state != "running",
                "outcome": trial.get("outcome") or "", "reason": self._reason,
                "mock_mode": self.mock_mode, "settings_present": bool(settings),
                "settings": copy.deepcopy(settings), "journal": self.journal.status()}
            result["clients"] = [{"client_id": cid, "interface_id": item["interface_id"],
                "source_clock_id": item["source_clock_id"],
                "heartbeat_age_seconds": max(0, now - item["received_mono_us"]) / 1e6,
                "ready": not self.readiness(cid), "readiness_reasons": self.readiness(cid)}
                for cid, item in self._clients.items()]
            for field, default in (("speed_percent", -1), ("max_tilt_degrees", -1),
                    ("deadzone_degrees", -1), ("sensitivity", -1), ("camera_visible", False),
                    ("map_visible", False), ("layout_locked", True)):
                result[field] = settings.get(field, default)
            return result

    def close(self):
        with self._lock:
            if self._trial and self._state not in TERMINAL:
                self._finish("aborted", "service_shutdown")
            self._closed = True
        return self.journal.close(timeout=2.0)
