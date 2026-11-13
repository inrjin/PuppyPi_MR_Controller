import csv
import json
import math
import tempfile
import unittest
from pathlib import Path

from puppypi_study.analysis import (
    AnalysisConfig,
    count_direction_reversals,
    export_summary,
    load_records,
    summarize_records,
    verify_framing_replay,
)


def record(kind, t, payload=None, *, trial="trial-1", clock="robot-clock", event_id=None, **changes):
    result = {
        "schema_version": 1,
        "event_id": event_id or f"{trial}:{kind}:{t}",
        "experiment_session_id": "session-1",
        "trial_id": trial,
        "participant_id": "anonymous-01",
        "interface_id": "mr_ballgrip",
        "period_index": 2,
        "difficulty_id": "demo-wide",
        "source": "robot",
        "clock_id": clock,
        "t_mono_us": t,
        "record_type": "event",
        "event_type": kind,
        "payload": payload or {},
    }
    result.update(changes)
    return result


def trial_records(outcome="success", *, trial="trial-1", end=2_000_000):
    info = {
        "interface_order": ["smartphone_touch", "mr_ballgrip"],
        "practice": False,
        "config_version": "demo-v1",
        "config_hash": "example-hash",
        "config_snapshot": {"version": "demo-v1"},
    }
    return [
        record("logging_started", 0, trial=""),
        record("trial_prepared", 0, info, trial=trial),
        record("signal_shown", 0, trial=trial),
        record("trial_started", 0, info, trial=trial),
        record("first_marker_decoded", 500_000, trial=trial),
        record("framing_entered", 1_000_000, trial=trial),
        record("trial_" + outcome, end, {"outcome": outcome, "reason": "test"}, trial=trial),
        record("trial_ended", end, {"outcome": outcome, "reason": "test"}, trial=trial),
        record("logging_closed", end + 1, {"complete": True}, trial=""),
    ]


def input_sample(t, *, engaged=True, valid=True, forward=0.3, turn=0, blocked=False, **kwargs):
    payload = {"engaged": engaged, "input_valid": valid,
               "normalized_forward": forward, "normalized_turn": turn,
               "demanded": engaged, "blocked": blocked}
    payload.update(kwargs)
    return record("input_sample", t, payload)


class AnalysisTests(unittest.TestCase):
    def test_success_intervals_use_actual_signal_and_keep_ids(self):
        rows = summarize_records(trial_records() + [input_sample(200_000)])
        self.assertEqual(len(rows), 1)
        row = rows[0]
        self.assertEqual(row["interface_id"], "mr_ballgrip")
        self.assertEqual(row["period_index"], 2)
        self.assertEqual(row["signal_to_first_valid_input_s"], 0.2)
        self.assertEqual(row["signal_to_success_s"], 2)
        self.assertEqual(row["first_valid_decode_to_success_s"], 1.5)
        self.assertEqual(row["first_framing_entry_to_success_s"], 1)

    def test_timeout_and_aborted_have_observation_time_but_no_completion_time(self):
        records = trial_records("timeout", trial="timeout") + trial_records("aborted", trial="aborted")
        rows = summarize_records(records)
        self.assertEqual({r["outcome"] for r in rows}, {"timeout", "aborted"})
        for row in rows:
            self.assertIsNone(row["signal_to_success_s"])
            self.assertIsNone(row["first_valid_decode_to_success_s"])
            self.assertEqual(row["observed_duration_s"], 2)
            self.assertFalse(row["success"])

    def test_foreign_clocks_are_never_directly_subtracted(self):
        records = trial_records()
        for item in records:
            if item["event_type"] == "signal_shown":
                item["clock_id"] = "quest-clock"
                item["t_mono_us"] = 900_000_000
        rows = summarize_records(records)
        self.assertIsNone(rows[0]["signal_to_success_s"])
        self.assertFalse(rows[0]["clock_alignment_valid"])
        self.assertIn("clock_mismatch:signal_to_success", rows[0]["quality_flags"])

    def test_explicit_alignment_maps_actual_client_signal(self):
        records = trial_records()
        for item in records:
            if item["event_type"] == "signal_shown":
                item["t_mono_us"] = 150_000  # Arrival time is deliberately later.
                item["payload"] = {"source_t_mono_us": 900_000_000,
                    "source_clock_id": "quest-clock", "aligned_t_mono_us": 100_000,
                    "clock_alignment_valid": True, "clock_alignment_error_us": 10_000}
        rows = summarize_records(records)
        self.assertEqual(rows[0]["signal_to_success_s"], 1.9)

    def test_alignment_above_error_budget_does_not_make_a_duration(self):
        records = trial_records()
        records[2]["payload"] = {"source_t_mono_us": 900_000_000,
            "source_clock_id": "quest-clock", "aligned_t_mono_us": 100_000,
            "clock_alignment_valid": True, "clock_alignment_error_us": 100_000}
        row = summarize_records(records)[0]
        self.assertIsNone(row["signal_to_success_s"])
        self.assertIn("client_clock_unaligned", row["quality_flags"])

    def test_first_nonzero_input_is_primary_and_engagement_is_preserved_separately(self):
        records = trial_records() + [input_sample(100_000, forward=0), input_sample(500_000)]
        nonzero = summarize_records(records)[0]
        self.assertEqual(nonzero["signal_to_first_engaged_valid_s"], 0.1)
        self.assertEqual(nonzero["signal_to_first_nonzero_input_s"], 0.5)
        self.assertEqual(nonzero["signal_to_first_valid_input_s"], 0.5)
        with self.assertRaises(ValueError):
            summarize_records(records, analysis_config={"first_input_policy": "engaged_valid"})

    def test_overlapping_blocks_are_unioned_only_during_control_demand(self):
        records = trial_records()
        records += [input_sample(t) for t in range(0, 2_000_000, 100_000)]
        records += [record("block_started", 500_000, {"reason": "tracking"}),
                    record("block_started", 1_000_000, {"reason": "ack"}),
                    record("block_ended", 1_500_000, {"reason": "tracking"}),
                    record("block_ended", 1_800_000, {"reason": "ack"})]
        row = summarize_records(records)[0]
        self.assertAlmostEqual(row["control_demand_s"], 2)
        self.assertAlmostEqual(row["blocked_demand_s"], 1.3)
        self.assertEqual(json.loads(row["blocked_by_reason_json"]), {"ack": 0.8, "tracking": 1.0})
        self.assertEqual(row["reentry_count"], 1)

    def test_blocked_time_excludes_periods_without_demand(self):
        records = trial_records() + [input_sample(t, engaged=(t >= 1_000_000)) for t in range(0, 2_000_000, 100_000)]
        records += [record("block_started", 0, {"reason": "ack"}),
                    record("block_ended", 1_500_000, {"reason": "ack"})]
        row = summarize_records(records)[0]
        self.assertEqual(row["control_demand_s"], 1)
        self.assertEqual(row["blocked_demand_s"], 0.5)

    def test_input_gaps_are_bounded_and_reported(self):
        row = summarize_records(trial_records() + [input_sample(0)])[0]
        self.assertEqual(row["control_demand_s"], 0.25)
        self.assertIn("input_sample_gap", row["quality_flags"])

    def test_duplicate_event_ids_do_not_duplicate_trials(self):
        records = trial_records()
        rows = summarize_records(records + records)
        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0]["outcome"], "success")

    def test_conflicting_event_id_is_visible(self):
        records = trial_records()
        changed = dict(records[6], payload={"outcome": "timeout"})
        row = summarize_records(records + [changed])[0]
        self.assertIn("conflicting_event_id", row["quality_flags"])

    def test_incomplete_trial_keeps_an_empty_outcome(self):
        records = [r for r in trial_records() if r["event_type"] not in ("trial_success", "trial_ended", "logging_closed")]
        row = summarize_records(records)[0]
        self.assertEqual(row["outcome"], "")
        self.assertIn("terminal_outcome_missing", row["quality_flags"])
        self.assertIn("logging_close_missing", row["quality_flags"])

    def test_jsonl_partial_line_and_overflow_survive_csv_export(self):
        records = trial_records("timeout") + [record("logging_overflow", 100, {"dropped_records": 12}, trial="")]
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "raw.jsonl"
            path.write_text("".join(json.dumps(r) + "\n" for r in records) + '{"unfinished":', encoding="utf-8")
            loaded = load_records(path)
            self.assertEqual(loaded.invalid_lines, 1)
            self.assertIn("truncated_final_record", loaded.quality_flags)
            destination = Path(folder) / "summary.csv"
            export_summary(path, destination)
            with destination.open(newline="", encoding="utf-8") as stream:
                row = next(csv.DictReader(stream))
            self.assertEqual(row["outcome"], "timeout")
            self.assertEqual(row["signal_to_success_s"], "")
            self.assertEqual(row["dropped_log_records"], "12")
            self.assertIn("truncated_final_record", row["quality_flags"])

    def test_frame_delay_uses_one_clock_and_never_invents_capture_time(self):
        records = trial_records()
        for frame_id, received in ((5, 100_000), (7, 200_000)):
            records.append(record("detection_sample", received + 20_000, {"observation": {
                "frame_id": frame_id, "clock_id": "robot-clock", "received_mono_us": received,
                "decoded_mono_us": received + 20_000, "captured_mono_us": None,
                "capture_clock_id": None}}))
        row = summarize_records(records)[0]
        self.assertEqual(row["decode_latency_mean_ms"], 20)
        self.assertEqual(row["frame_update_interval_mean_ms"], 100)
        self.assertEqual(row["frame_id_gap_count"], 1)
        self.assertEqual(row["capture_timestamp_known_frames"], 0)
        self.assertIn("capture_timestamp_missing", row["quality_flags"])

    def test_interface_order_cannot_recode_the_mr_interface(self):
        records = trial_records()
        records[1]["payload"]["interface_order"] = ["mr_ballgrip", "smartphone_touch"]
        records[3]["payload"]["interface_order"] = ["mr_ballgrip", "smartphone_touch"]
        row = summarize_records(records)[0]
        self.assertEqual(row["interface_id"], "mr_ballgrip")
        self.assertIn("period_order_mismatch", row["quality_flags"])

    def test_same_trial_id_in_two_sessions_is_not_merged(self):
        first = trial_records()
        second = []
        for item in trial_records("aborted"):
            changed = dict(item, experiment_session_id="session-2", event_id=item["event_id"] + ":second")
            second.append(changed)
        rows = summarize_records(first + second)
        self.assertEqual(len(rows), 2)
        self.assertEqual({row["outcome"] for row in rows}, {"success", "aborted"})

    def test_late_and_pre_signal_input_do_not_replace_first_valid_input(self):
        records = trial_records()
        records[2]["t_mono_us"] = 100_000
        records += [input_sample(0), input_sample(300_000),
                    input_sample(2_100_000, late_after_terminal=True)]
        row = summarize_records(records)[0]
        self.assertEqual(row["signal_to_first_valid_input_s"], 0.2)

    def test_logger_total_loss_is_preserved_without_an_overflow_event(self):
        records = trial_records()
        records[-1]["payload"] = {"complete": False, "dropped_total": 7}
        row = summarize_records(records)[0]
        self.assertEqual(row["dropped_log_records"], 7)
        self.assertIn("logging_incomplete", row["quality_flags"])

    def test_release_during_one_block_does_not_create_multiple_reentries(self):
        records = trial_records()
        records += [input_sample(t, engaged=not 800_000 <= t < 1_200_000)
                    for t in range(0, 2_000_000, 100_000)]
        records += [record("block_started", 500_000, {"reason": "ack"}),
                    record("block_ended", 1_500_000, {"reason": "ack"})]
        row = summarize_records(records)[0]
        self.assertAlmostEqual(row["blocked_demand_s"], 0.6)
        self.assertEqual(row["reentry_count"], 1)

    def test_duplicate_json_keys_are_flagged_as_bad_records(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "raw.jsonl"
            path.write_text('{"schema_version":1,"payload":{"outcome":"success","outcome":"timeout"}}\n')
            loaded = load_records(path)
            self.assertEqual(loaded.invalid_lines, 1)
            self.assertEqual(loaded.records, [])

    def test_runtime_float_alignment_error_is_supported(self):
        records = trial_records()
        records[2]["payload"] = {"source_t_mono_us": 900_000_000,
            "source_clock_id": "quest-clock", "aligned_t_mono_us": 100_000,
            "clock_alignment_valid": True, "clock_alignment_error_us": 5000.0}
        row = summarize_records(records)[0]
        self.assertEqual(row["signal_to_success_s"], 1.9)

    def test_unity_default_payload_clock_field_does_not_hide_header_time(self):
        records = trial_records()
        sample = input_sample(200_000, source_clock_id="")
        # JsonUtility includes unused public fields in the typed StudyPayload.
        sample["payload"].update(raw_forward=12.0, raw_turn=0.0, forward=0.0,
            turn=0.0, control_active=False, accepted=False, applied=False,
            paused=False, focused=True, offset_us=0, uncertainty_us=0, valid=False)
        records.append(sample)
        row = summarize_records(records)[0]
        self.assertEqual(row["signal_to_first_valid_input_s"], 0.2)
        self.assertNotIn("invalid_source_timestamp", row["quality_flags"])

    def test_command_hold_between_display_and_server_start_is_counted(self):
        records = trial_records()
        records[3]["t_mono_us"] = 200_000
        records += [input_sample(t) for t in range(0, 2_000_000, 100_000)]
        records += [record("block_started", 0, {"reason": "start_confirmation_pending"}),
                    record("block_ended", 200_000, {"reason": "start_confirmation_pending"})]
        row = summarize_records(records)[0]
        self.assertEqual(row["blocked_demand_s"], 0.2)
        self.assertEqual(row["observed_duration_s"], 1.8)
        self.assertEqual(row["signal_to_success_s"], 2.0)


class FramingReplayTests(unittest.TestCase):
    def fixture(self, timeout=False):
        from puppypi_study.config import demo_config
        from puppypi_study.framing import FrameObservation, FramingEvaluator

        config = demo_config()
        snapshot = {"config_snapshot": config.to_dict(), "evaluator_start_mono_us": 1_000_000,
                    "last_frame_id": 0}
        records = [record("trial_started", 1_000_000, snapshot)]
        evaluator = FramingEvaluator(config.profile("demo-wide"), clock_id="robot-clock")
        evaluator.start("trial-1", 1_000_000, last_frame_id=0)
        for index in range(1, 12):
            t = 1_000_000 + index * 100_000
            frame = FrameObservation(index, t, t, 640, 480,
                marker_text=None if timeout else config.framing.target_marker,
                corners=None if timeout else [[270, 190], [370, 190], [370, 290], [270, 290]],
                clock_id="robot-clock", decoder="mock_fixture")
            records.append(record("detection_sample", t, {"observation": frame.to_dict(),
                "evaluation_now_mono_us": t, "neutral_axes": [0, 0]}))
            events = evaluator.observe(frame, now_mono_us=t, neutral_axes=[0, 0])
            for event in events:
                records.append(record(event.event_type, event.t_mono_us, event.payload))
        if timeout:
            deadline = 61_000_000
            records.append(record("framing_tick", deadline, {"evaluation_now_mono_us": deadline}))
            for event in evaluator.tick(deadline):
                records.append(record(event.event_type, event.t_mono_us, event.payload))
        return records

    def test_success_from_saved_observations_matches(self):
        report = verify_framing_replay(self.fixture())
        self.assertTrue(report["matched"])
        self.assertEqual(report["matches"], 1)
        self.assertEqual(report["trials"][0]["decoder_modes"], ["mock_fixture"])

    def test_timeout_from_saved_tick_matches(self):
        report = verify_framing_replay(self.fixture(timeout=True))
        self.assertTrue(report["matched"])
        self.assertEqual(report["trials"][0]["replayed"], [("trial_timeout", 61_000_000)])

    def test_modified_terminal_event_is_detected(self):
        records = self.fixture()
        records[-1]["t_mono_us"] += 1
        report = verify_framing_replay(records)
        self.assertFalse(report["matched"])
        self.assertEqual(report["mismatches"], 1)

    def test_modified_hold_transition_is_detected(self):
        records = self.fixture()
        first_hold = next(r for r in records if r["event_type"] == "framing_hold_started")
        first_hold["payload"]["frame_id"] = 999
        report = verify_framing_replay(records)
        self.assertFalse(report["matched"])
        self.assertFalse(report["trials"][0]["transitions_match"])

    def test_duplicate_identical_records_do_not_change_replay(self):
        records = self.fixture()
        report = verify_framing_replay(records + records)
        self.assertTrue(report["matched"])

    def test_aborted_trial_is_not_reported_as_framing_verified(self):
        records = self.fixture(timeout=True)
        records = [r for r in records if r["event_type"] not in ("trial_timeout", "framing_tick")]
        records.append(record("trial_aborted", 3_000_000, {"outcome": "aborted"}))
        report = verify_framing_replay(records)
        self.assertFalse(report["matched"])
        self.assertEqual(report["unverifiable"], 1)


class DirectionReversalTests(unittest.TestCase):
    def test_sign_changes_require_sustained_direction(self):
        samples = [(t, 0.3 if t < 350_000 else -0.3) for t in range(0, 700_000, 50_000)]
        self.assertEqual(count_direction_reversals(samples), 1)

    def test_brief_jitter_is_not_a_reversal(self):
        samples = [(t, -0.3 if t == 350_000 else 0.3) for t in range(0, 700_000, 50_000)]
        self.assertEqual(count_direction_reversals(samples), 0)

    def test_release_then_new_engagement_is_not_a_reversal(self):
        samples = [(t, 0.3 if t < 350_000 else 0 if t < 600_000 else -0.3)
                   for t in range(0, 1_000_000, 50_000)]
        self.assertEqual(count_direction_reversals(samples), 0)

    def test_missing_command_gap_breaks_episode(self):
        samples = [(t, 0.3) for t in range(0, 300_000, 50_000)]
        samples += [(t, -0.3) for t in range(1_000_000, 1_300_000, 50_000)]
        self.assertEqual(count_direction_reversals(samples), 0)

    def test_common_resampling_ignores_repeated_packets(self):
        sparse = [(t, 0.3 if t < 350_000 else -0.3) for t in range(0, 700_000, 50_000)]
        dense = [(t, 0.3 if t < 350_000 else -0.3) for t in range(0, 700_000, 10_000)]
        self.assertEqual(count_direction_reversals(sparse), count_direction_reversals(dense))

    def test_analysis_parameters_are_finite_and_versioned(self):
        for setting in ({"sample_period_s": 0}, {"deadzone": math.nan},
                        {"hysteresis": 0.9}, {"max_clock_error_us": -1},
                        {"min_hold_s": math.inf}, {"first_input_policy": "anything"}):
            with self.subTest(setting=setting), self.assertRaises(ValueError):
                AnalysisConfig.from_dict(setting)


if __name__ == "__main__":
    unittest.main()
