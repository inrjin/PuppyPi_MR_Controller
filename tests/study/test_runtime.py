import unittest
import uuid
import re
from pathlib import Path

from puppypi_study.config import demo_config
from puppypi_study.journal import MemoryJournal
from puppypi_study.runtime import RuntimePolicy, StudyError, StudyRuntime


class RuntimeFixture:
    def __init__(self, mock=True, neutral_callback=None):
        self.now = 1_000_000
        self.client = str(uuid.uuid4())
        self.journal = MemoryJournal()
        self.engine = StudyRuntime(demo_config(), self.journal, mock_mode=mock,
            clock=lambda: self.now, clock_id="test-service",
            policy=RuntimePolicy(countdown_seconds=.1, signal_ack_timeout_seconds=.3,
                                 client_timeout_seconds=2, robot_timeout_seconds=2),
            neutral_callback=neutral_callback)
        session = self.engine.create_session({"request_id": "create", "participant_id": "P001",
            "metadata": {"assignment_seed": 42}})
        self.base = {"experiment_session_id": session["experiment_session_id"],
                     "trial_id": "trial-1", "control_client_id": self.client}
        self.heartbeat()

    def heartbeat(self, **extra):
        body = {"client_id": self.client, "interface_id": "mr_ballgrip",
            "source_clock_id": "client-clock", "source_t_mono_us": self.now - 100,
            "engaged": False, "input_valid": True, "neutral_confirmed": True,
            "control_connected": True, "last_accepted": True, "emergency": False,
            "settings": {"speed_percent": 20, "layout_locked": True}}
        body.update(extra)
        return self.engine.register_client(body)

    def prepare(self):
        return self.engine.start_trial(dict(self.base, request_id="start",
            interface_id="mr_ballgrip", period_index=1, difficulty_id="demo-wide", practice=True))

    def events(self, *events, request_id=None):
        body = dict(self.base, request_id=request_id or str(uuid.uuid4()), events=list(events))
        return self.engine.client_events(body)

    def event(self, kind, payload=None, event_id=None):
        return {"event_id": event_id or str(uuid.uuid4()), "event_type": kind,
                "source_clock_id": "client-clock", "source_t_mono_us": self.now - 100,
                "payload": payload or {}}

    def running(self):
        status = self.prepare()
        self.now += 100_000
        self.events(self.event("clock_sync", {"offset_us": 100, "uncertainty_us": 50,
                    "valid": True, "reference_clock_id": "test-service"}))
        self.events(self.event("signal_shown", {"signal_id": status["signal_id"]}))
        return self.engine.status(self.client)

    def frame(self, frame_id, **extra):
        body = {"schema_version": 1, "frame_id": frame_id, "clock_id": "test-service",
            "received_mono_us": self.now, "decoded_mono_us": self.now,
            "image_width": 640, "image_height": 480,
            "marker_text": "KHCI2027:inspection",
            "corners": [[270,190],[370,190],[370,290],[270,290]], "data_valid": True,
            "captured_mono_us": None, "capture_clock_id": None, "decoder": "fixture"}
        body.update(extra)
        return body


class RuntimeTests(unittest.TestCase):
    def test_pending_event_delivery_blocks_new_trial_only(self):
        fixture = RuntimeFixture()
        fixture.heartbeat(event_delivery_ready=False)
        with self.assertRaisesRegex(StudyError, "client_event_delivery_pending"):
            fixture.prepare()
        fixture.heartbeat(event_delivery_ready=True)
        fixture.running()
        running = fixture.heartbeat(event_delivery_ready=False)
        self.assertEqual("running", running["state"])
        self.assertTrue(running["motion_permitted"])

    def test_real_csharp_payload_defaults_and_eight_event_batch(self):
        fixture = RuntimeFixture()
        prepared = fixture.prepare()
        fixture.now += 100000
        # A fresh grab may be reported after GO was submitted but before its HTTP ACK.
        waiting = fixture.heartbeat(engaged=True, neutral_confirmed=False)
        self.assertEqual("countdown", waiting["state"])
        self.assertFalse(waiting["motion_permitted"])
        protocol = Path(__file__).resolve().parents[2] / "My project/Assets/PuppyPi/StudyProtocol.cs"
        code = protocol.read_text().split("public sealed class StudyPayload", 1)[1].split("[Serializable]", 1)[0]
        defaults = {}
        for kind, name in re.findall(r"public (float|long|bool|string\[\]|string|StudySettings) (\w+);", code):
            defaults[name] = False if kind == "bool" else 0 if kind in {"float", "long"} else None
        names = ["clock_sync", "client_settings_snapshot", "ack_observed", "control_event_sent",
                 "logging_started", "logging_stopped", "logging_interrupted", "signal_shown"]
        batch = []
        for kind in names:
            payload = dict(defaults)
            if kind == "clock_sync":
                payload.update(source_clock_id="client-clock", reference_clock_id="test-service",
                               offset_us=100, uncertainty_us=50, valid=True)
            elif kind == "signal_shown":
                payload.update(signal_id=prepared["signal_id"], timestamp_basis="canvas_submission")
            batch.append(fixture.event(kind, payload))
        result = fixture.events(*batch)
        self.assertEqual(8, result["accepted_events"])
        self.assertEqual("running", result["state"])

    def test_early_grab_is_rejected_but_go_window_grab_is_allowed(self):
        early = RuntimeFixture()
        early.prepare()
        self.assertEqual("aborted", early.heartbeat(engaged=True)["state"])
        after_go = RuntimeFixture()
        after_go.prepare()
        after_go.now += 100000
        self.assertEqual("countdown", after_go.heartbeat(engaged=True)["state"])

    def test_phone_first_order_preserves_mr_second_period(self):
        fixture = RuntimeFixture()
        session = fixture.engine.create_session({"request_id": "two-conditions", "participant_id": "P001",
            "interface_order": ["smartphone_touch", "mr_ballgrip"]})
        fixture.base["experiment_session_id"] = session["experiment_session_id"]
        start = dict(fixture.base, request_id="period", interface_id="mr_ballgrip",
                     period_index=1, difficulty_id="demo-wide")
        with self.assertRaisesRegex(StudyError, "identify MR"):
            fixture.engine.start_trial(start)
        start["period_index"] = 2
        self.assertEqual(2, fixture.engine.start_trial(start)["period_index"])

    def test_signal_requires_clock_alignment(self):
        fixture = RuntimeFixture()
        prepared = fixture.prepare()
        fixture.now += 100000
        with self.assertRaisesRegex(StudyError, "clock alignment"):
            fixture.events(fixture.event("signal_shown", {"signal_id": prepared["signal_id"]}))
        self.assertFalse(fixture.engine.status()["motion_permitted"])

    def test_live_start_requires_adapter_not_client_claims(self):
        fixture = RuntimeFixture(mock=False)
        with self.assertRaisesRegex(StudyError, "adapter_unavailable"):
            fixture.prepare()
        fixture.engine.ingest_robot_status({"control_client_id": fixture.client,
            "controller_active": True, "neutral_confirmed": True, "emergency_stop": False})
        fixture.engine.ingest_observation(fixture.frame(1))
        self.assertEqual("countdown", fixture.prepare()["state"])

    def test_decoder_failure_aborts_but_no_qr_is_healthy(self):
        fixture = RuntimeFixture(mock=False)
        fixture.engine.ingest_robot_status({"control_client_id": fixture.client,
            "controller_active": True, "neutral_confirmed": True, "emergency_stop": False})
        fixture.engine.ingest_observation(fixture.frame(1, marker_text=None, corners=None))
        self.assertNotIn("decoder_unavailable", fixture.engine.status(fixture.client)["readiness_reasons"])
        self.assertEqual("countdown", fixture.prepare()["state"])
        fixture.now += 10000
        fixture.engine.ingest_observation(fixture.frame(2, data_valid=False, invalid_reason="decoder_error"))
        result = fixture.engine.status(fixture.client)
        self.assertEqual("aborted", result["state"])
        self.assertIn("decoder_unavailable", result["readiness_reasons"])

    def test_readiness_uses_framing_age_and_valid_geometry_failure_only_resets_hold(self):
        live = RuntimeFixture(mock=False)
        live.engine.ingest_robot_status({"control_client_id": live.client,
            "controller_active": True, "neutral_confirmed": True, "emergency_stop": False})
        live.engine.ingest_observation(live.frame(1))
        live.now += 300001
        self.assertIn("camera_stale", live.engine.status(live.client)["readiness_reasons"])
        fixture = RuntimeFixture()
        fixture.running()
        fixture.engine.ingest_observation(fixture.frame(1, corners=[[1,1],[2,2],[3,3],[4,4]]))
        self.assertEqual("running", fixture.engine.status()["state"])
        self.assertTrue(fixture.engine._decoder_healthy)

    def test_trusted_adapter_command_events_are_distinct_from_client_claims(self):
        fixture = RuntimeFixture()
        fixture.running()
        fixture.engine.ingest_control_event("robot_command_received", {
            "control_client_id": fixture.client, "sequence": 12, "forward": .1, "turn": 0})
        record = fixture.journal.records[-1]
        self.assertEqual("adapter", record["source"])
        self.assertEqual("adapter_report", record["payload"]["report_origin"])
        with self.assertRaisesRegex(StudyError, "safe nonnegative"):
            fixture.engine.ingest_control_event("robot_command_accepted", {
                "control_client_id": fixture.client, "sequence": -1})

    def test_start_signal_handshake_and_success_once(self):
        neutral = []
        fixture = RuntimeFixture(neutral_callback=lambda client, reason: neutral.append(client))
        self.assertTrue(fixture.running()["motion_permitted"])
        for index in range(1, 15):
            fixture.now += 100_000
            fixture.heartbeat()
            fixture.engine.ingest_observation(fixture.frame(index), neutral_axes=(0, 0))
        status = fixture.engine.status(fixture.client)
        self.assertEqual("success", status["outcome"])
        self.assertFalse(status["motion_permitted"])
        self.assertTrue(status["neutral_requested"])
        kinds = [record["event_type"] for record in fixture.journal.records]
        self.assertEqual(1, kinds.count("trial_success"))
        self.assertEqual([fixture.client], neutral)
        self.assertEqual(1, kinds.count("trial_ended"))

    def test_countdown_does_not_start_without_display_ack(self):
        fixture = RuntimeFixture()
        prepared = fixture.prepare()
        event = fixture.event("signal_shown", {"signal_id": prepared["signal_id"]})
        with self.assertRaisesRegex(StudyError, "countdown"):
            fixture.events(event)
        fixture.now += 400_000
        status = fixture.engine.status(fixture.client)
        self.assertEqual("aborted", status["state"])
        self.assertEqual("signal_ack_timeout", status["reason"])

    def test_idempotent_retry_never_returns_stale_motion_permission(self):
        fixture = RuntimeFixture()
        prepared = fixture.prepare()
        fixture.now += 100_000
        fixture.events(fixture.event("clock_sync", {"offset_us": 100, "uncertainty_us": 50,
                       "valid": True, "reference_clock_id": "test-service"}))
        event = fixture.event("signal_shown", {"signal_id": prepared["signal_id"]}, "shown")
        self.assertTrue(fixture.events(event, request_id="ack")["motion_permitted"])
        fixture.engine.abort_trial(dict(fixture.base, request_id="abort", reason="operator_abort"))
        retry = fixture.events(event, request_id="ack")
        self.assertEqual("aborted", retry["state"])
        self.assertFalse(retry["motion_permitted"])
        self.assertTrue(retry["idempotent_replay"])
        self.assertEqual(1, sum(r["event_type"] == "signal_shown" for r in fixture.journal.records))

    def test_request_reuse_with_different_payload_rejected(self):
        fixture = RuntimeFixture()
        fixture.prepare()
        body = dict(fixture.base, request_id="start", interface_id="mr_ballgrip",
                    period_index=1, difficulty_id="demo-narrow", practice=True)
        with self.assertRaisesRegex(StudyError, "request_id reused"):
            fixture.engine.start_trial(body)

    def test_wrong_session_client_and_robot_event_forgery_rejected(self):
        fixture = RuntimeFixture()
        fixture.running()
        for key, value in (("experiment_session_id", "wrong"), ("control_client_id", str(uuid.uuid4()))):
            body = dict(fixture.base, request_id=str(uuid.uuid4()), events=[fixture.event("grab")])
            body[key] = value
            with self.assertRaises(StudyError):
                fixture.engine.client_events(body)
        with self.assertRaisesRegex(StudyError, "cannot submit"):
            fixture.events(fixture.event("robot_command_accepted"))

    def test_client_heartbeat_stale_aborts_without_status_poll(self):
        fixture = RuntimeFixture()
        fixture.running()
        fixture.now += 2_000_001
        fixture.engine.tick()
        self.assertEqual("client_heartbeat_stale", fixture.engine.status()["reason"])

    def test_changed_client_clock_and_settings_abort(self):
        for changes, expected in (({"source_clock_id": "new-epoch"}, "client_clock_changed"),
                                  ({"settings": {"speed_percent": 90}}, "settings_changed")):
            fixture = RuntimeFixture()
            fixture.running()
            try:
                fixture.heartbeat(**changes)
            except StudyError:
                pass
            self.assertEqual(expected, fixture.engine.status()["reason"])

    def test_clock_alignment_requires_matching_reference_and_bound(self):
        fixture = RuntimeFixture()
        fixture.running()
        for reference, uncertainty, expected in (("other", 50, False),
                                                 ("test-service", 100001, False),
                                                 ("test-service", 50, True)):
            fixture.events(fixture.event("clock_sync", {"offset_us": 100,
                "uncertainty_us": uncertainty, "valid": True, "reference_clock_id": reference}))
            fixture.events(fixture.event("input_sample", {"normalized_forward": .1}))
            payload = fixture.journal.records[-1]["payload"]
            self.assertEqual(expected, payload["clock_alignment_valid"])
            self.assertEqual(fixture.now if expected else None, payload["aligned_t_mono_us"])

    def test_log_failure_aborts_and_duplicate_frames_cannot_refresh_live_readiness(self):
        fixture = RuntimeFixture()
        fixture.running()
        fixture.journal.healthy = False
        fixture.engine.tick()
        self.assertEqual("logging_unavailable", fixture.engine.status()["reason"])
        live = RuntimeFixture(mock=False)
        live.engine.ingest_robot_status({"control_client_id": live.client,
            "controller_active": True, "neutral_confirmed": True, "emergency_stop": False})
        live.engine.ingest_observation(live.frame(1))
        live.now += 2_000_001
        live.heartbeat()
        live.engine.ingest_observation(live.frame(1))
        self.assertIn("camera_stale", live.engine.status(live.client)["readiness_reasons"])

    def test_pretrial_frame_never_contributes_to_hold(self):
        fixture = RuntimeFixture()
        fixture.engine.ingest_observation(fixture.frame(99))
        fixture.running()
        fixture.now += 100000
        fixture.engine.ingest_observation(fixture.frame(99), neutral_axes=(0, 0))
        self.assertNotIn("framing_hold_started", [r["event_type"] for r in fixture.journal.records])


if __name__ == "__main__":
    unittest.main()
