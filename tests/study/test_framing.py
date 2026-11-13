import copy
import json
import math
import tempfile
import unittest
from dataclasses import replace
from pathlib import Path

from puppypi_study.config import FramingConfig, StudyConfig, demo_config, load_config, seconds_to_us
from puppypi_study.framing import FrameObservation, FramingEvaluator


TARGET = "KHCI2027:inspection"


def frame(frame_id, stamp, **changes):
    values = dict(frame_id=frame_id, received_mono_us=stamp, decoded_mono_us=stamp,
                  image_width=640, image_height=480, marker_text=TARGET,
                  corners=[[270, 190], [370, 190], [370, 290], [270, 290]])
    values.update(changes)
    return FrameObservation(**values)


def event_names(events):
    return [event.event_type for event in events]


class StudyConfigTests(unittest.TestCase):
    def test_demo_file_matches_factory_and_profiles_differ_only_horizontally(self):
        config = load_config("experiments/configs/demo.json")
        self.assertEqual(config.to_dict(), demo_config().to_dict())
        self.assertEqual(config.profile_status, "demo")
        wide, narrow = config.profile("demo-wide").to_dict(), config.profile("demo-narrow").to_dict()
        self.assertGreater(wide.pop("center_tolerance_x"), narrow.pop("center_tolerance_x"))
        self.assertEqual(wide, narrow)
        self.assertEqual(len(config.sha256), 64)
        self.assertEqual(config.sha256, StudyConfig.from_dict(config.to_dict()).sha256)

    def test_config_cannot_change_through_caller_or_returned_dictionary(self):
        values = demo_config().to_dict()
        config = StudyConfig.from_dict(values)
        original = config.sha256
        values["difficulties"]["demo-wide"]["center_tolerance_x"] = 0.4
        config.to_dict()["framing"]["hold_seconds"] = 99
        self.assertEqual(config.sha256, original)
        with self.assertRaises(TypeError):
            config.difficulties["demo-wide"]["center_tolerance_x"] = 0.4

    def test_nonfinite_bool_negative_and_inconsistent_thresholds_are_rejected(self):
        config = demo_config().framing
        invalid = [
            {"center_tolerance_x": float("nan")}, {"center_tolerance_y": float("inf")},
            {"area_ratio_min": 0}, {"area_ratio_min": 0.3}, {"area_ratio_max": 1.1},
            {"hold_seconds": 0}, {"max_frame_gap": -1}, {"max_frame_age": True},
            {"time_limit": config.hold_seconds}, {"neutral_threshold": -0.1},
            {"require_neutral": 1}, {"target_marker": ""}, {"hold_seconds": 1e308},
        ]
        for changes in invalid:
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                replace(config, **changes)
        self.assertEqual(seconds_to_us(0.0000001), 1)

    def test_difficulty_cannot_modify_uncontrolled_parameters_or_implement_phone(self):
        values = demo_config().to_dict()
        values["difficulties"]["demo-narrow"]["hold_seconds"] = 2
        with self.assertRaises(ValueError):
            StudyConfig.from_dict(values)
        values = demo_config().to_dict()
        values["implemented_interfaces"] = ["smartphone_touch", "mr_ballgrip"]
        with self.assertRaises(ValueError):
            StudyConfig.from_dict(values)

    def test_canonical_comparison_order_is_separate_from_implemented_clients(self):
        values = demo_config().to_dict()
        self.assertEqual(values["interface_order"], ["smartphone_touch", "mr_ballgrip"])
        self.assertEqual(values["implemented_interfaces"], ["mr_ballgrip"])
        values["interface_order"] = ["mr_ballgrip", "smartphone_touch"]
        self.assertEqual(StudyConfig.from_dict(values).interface_order.index("mr_ballgrip") + 1, 1)
        values["interface_order"] = ["mr_ballgrip"]
        self.assertEqual(StudyConfig.from_dict(values).interface_order, ("mr_ballgrip",))
        values["interface_order"] = ["mr_ballgrip", "mr_ballgrip"]
        with self.assertRaises(ValueError):
            StudyConfig.from_dict(values)
        values = demo_config().to_dict()
        values["schema_version"] = True
        with self.assertRaises(ValueError):
            StudyConfig.from_dict(values)

    def test_unknown_or_duplicate_json_keys_fail_instead_of_silent_override(self):
        with self.assertRaises(ValueError):
            FramingConfig.from_dict({**demo_config().framing.to_dict(), "hold_second": 1})
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "bad.json"
            path.write_text('{"schema_version":1,"schema_version":2}', encoding="utf-8")
            with self.assertRaises(ValueError):
                load_config(path)


class FramingEvaluatorTests(unittest.TestCase):
    def make_evaluator(self, **changes):
        config = replace(demo_config().framing, hold_seconds=0.2, max_frame_gap=0.15,
                         max_frame_age=0.1, time_limit=1, **changes)
        evaluator = FramingEvaluator(config)
        evaluator.start("trial-1", 1_000_000)
        return evaluator

    def test_geometry_uses_image_dimensions_and_accepts_both_windings(self):
        observation = frame(1, 1_000_000)
        self.assertEqual(observation.geometry()[:2], (0, 0))
        self.assertAlmostEqual(observation.geometry()[2], 10000 / (640 * 480))
        reversed_observation = replace(observation, corners=list(reversed(observation.corners)))
        self.assertEqual(observation.geometry(), reversed_observation.geometry())
        self.assertEqual(observation.to_dict(), FrameObservation.from_dict(observation.to_dict()).to_dict())

    def test_success_requires_new_frames_and_emits_only_once(self):
        evaluator = self.make_evaluator()
        self.assertEqual(event_names(evaluator.observe(frame(1, 1_010_000))),
                         ["first_marker_decoded", "framing_entered", "framing_hold_started"])
        self.assertNotIn("trial_success", event_names(evaluator.observe(frame(2, 1_110_000))))
        events = evaluator.observe(frame(3, 1_210_000))
        self.assertEqual(event_names(events), ["trial_success"])
        self.assertEqual(events[0].payload["hold_elapsed_us"], 200_000)
        self.assertEqual(evaluator.outcome, "success")
        self.assertEqual(evaluator.observe(frame(4, 1_310_000)), [])
        self.assertEqual(evaluator.tick(3_000_000), [])

    def test_time_alone_never_completes_a_hold(self):
        evaluator = self.make_evaluator()
        evaluator.observe(frame(1, 1_010_000))
        events = evaluator.tick(1_250_000)
        self.assertEqual(event_names(events), ["framing_exited", "framing_hold_reset"])
        self.assertEqual(events[-1].payload["reason"], "frame_gap")
        self.assertIsNone(evaluator.outcome)

    def test_wrong_unread_outside_and_malformed_observations_reset_hold(self):
        changes = [
            {"marker_text": "OTHER"}, {"marker_text": "", "corners": None},
            {"corners": [[1, 1], [101, 1], [101, 101], [1, 101]]},
            {"corners": [[320, 230], [330, 230], [330, 240], [320, 240]]},
            {"image_width": 0}, {"image_height": True},
            {"corners": [[0, 0], [100, 100], [0, 100], [100, 0]]},
            {"corners": [[270, 190], [370, 190], [float("nan"), 290], [270, 290]]},
            {"corners": [[270, 190], [370, 190], [700, 290], [270, 290]]},
            {"data_valid": False, "invalid_reason": "decoder_error"},
        ]
        for change in changes:
            with self.subTest(change=change):
                evaluator = self.make_evaluator()
                evaluator.observe(frame(1, 1_010_000))
                events = evaluator.observe(frame(2, 1_110_000, **change))
                self.assertIn("framing_hold_reset", event_names(events))
                self.assertFalse(evaluator.last_evaluation["valid"])
                self.assertNotIn("trial_success", event_names(evaluator.observe(frame(3, 1_210_000))))

    def test_tolerance_and_area_bounds_are_inclusive(self):
        observation = frame(1, 1_010_000, corners=[[290, 190], [390, 190], [390, 290], [290, 290]])
        ex, ey, area = observation.geometry()
        config = replace(demo_config().framing, center_tolerance_x=abs(ex), center_tolerance_y=abs(ey),
                         area_ratio_min=area, area_ratio_max=area)
        evaluator = FramingEvaluator(config)
        evaluator.start("bounds", 1_000_000)
        evaluator.observe(observation)
        self.assertTrue(evaluator.last_evaluation["valid"])

    def test_duplicate_and_reversed_ids_cannot_fill_hold(self):
        for second in (frame(1, 1_110_000), frame(0, 1_110_000)):
            with self.subTest(frame_id=second.frame_id):
                evaluator = self.make_evaluator()
                evaluator.observe(frame(1, 1_010_000))
                self.assertIn("framing_hold_reset", event_names(evaluator.observe(second)))
                self.assertFalse(evaluator.last_evaluation["new_frame"])
                self.assertNotIn("trial_success", event_names(evaluator.observe(frame(2, 1_210_000))))

    def test_stale_capture_rejected_even_when_recently_received(self):
        evaluator = self.make_evaluator()
        evaluator.observe(frame(1, 1_010_000))
        events = evaluator.observe(frame(2, 1_210_000, captured_mono_us=1_050_000,
                                         capture_clock_id="robot_monotonic"))
        self.assertEqual(events[-1].payload["reason"], "stale_frame")
        self.assertEqual(evaluator.last_evaluation["freshness_basis"], "capture")
        self.assertEqual(evaluator.last_evaluation["frame_age_us"], 160_000)

    def test_unaligned_capture_is_preserved_and_never_subtracted(self):
        evaluator = self.make_evaluator()
        observation = frame(1, 1_010_000, captured_mono_us=9_000_000_000, capture_clock_id="camera_clock")
        evaluator.observe(observation)
        self.assertTrue(evaluator.last_evaluation["valid"])
        self.assertEqual(evaluator.last_evaluation["frame_age_us"], 0)
        self.assertFalse(evaluator.last_evaluation["capture_age_known"])
        self.assertEqual(evaluator.last_evaluation["freshness_basis"], "reception")
        self.assertEqual(observation.to_dict()["captured_mono_us"], 9_000_000_000)

    def test_trial_cutoff_rejects_old_time_and_last_frame_id(self):
        evaluator = self.make_evaluator()
        evaluator.observe(frame(1, 999_000, decoded_mono_us=1_010_000))
        self.assertEqual(evaluator.last_evaluation["reason"], "before_trial")
        evaluator = FramingEvaluator(demo_config().framing)
        evaluator.start("cutoff", 1_000_000, last_frame_id=42)
        evaluator.observe(frame(42, 1_010_000))
        self.assertEqual(evaluator.last_evaluation["reason"], "duplicate_or_reversed_frame")
        evaluator.observe(frame(43, 1_110_000))
        self.assertTrue(evaluator.last_evaluation["valid"])

    def test_reversed_times_and_frame_gap_require_a_new_hold(self):
        evaluator = self.make_evaluator()
        evaluator.observe(frame(1, 1_010_000))
        events = evaluator.observe(frame(2, 1_005_000), now_mono_us=1_050_000)
        self.assertEqual(events[-1].payload["reason"], "frame_time_reversed")
        evaluator = self.make_evaluator()
        evaluator.observe(frame(1, 1_010_000))
        events = evaluator.observe(frame(2, 1_210_001))
        self.assertEqual(events[-1].payload["reason"], "frame_gap")
        evaluator.observe(frame(3, 1_310_000))
        self.assertNotEqual(evaluator.outcome, "success")

    def test_reversed_evaluator_clock_resets_without_backdated_events(self):
        evaluator = self.make_evaluator()
        evaluator.observe(frame(1, 1_100_000))
        events = evaluator.tick(1_090_000)
        self.assertEqual(events[-1].payload["reason"], "time_reversed")
        self.assertEqual(events[-1].t_mono_us, 1_100_000)

    def test_foreign_observation_clock_is_not_used_for_age_or_timeout(self):
        evaluator = self.make_evaluator()
        evaluator.observe(frame(1, 1_010_000))
        events = evaluator.observe(frame(2, 99_000_000, clock_id="other"), now_mono_us=1_100_000)
        self.assertEqual(events[-1].payload["reason"], "clock_mismatch")
        self.assertIsNone(evaluator.last_evaluation["frame_age_us"])
        self.assertIsNone(evaluator.outcome)
        with self.assertRaises(ValueError):
            evaluator.observe(frame(3, 99_000_000, clock_id="other"))

    def test_timeout_wins_at_exact_deadline_and_late_decodes_never_rescue(self):
        evaluator = self.make_evaluator()
        evaluator.observe(frame(1, 1_800_000))
        evaluator.observe(frame(2, 1_900_000))
        events = evaluator.observe(frame(3, 2_000_000))
        self.assertEqual(event_names(events)[-1], "trial_timeout")
        self.assertEqual(evaluator.outcome, "timeout")
        self.assertEqual(events[-1].t_mono_us, 2_000_000)
        self.assertEqual(evaluator.tick(2_000_001), [])
        evaluator = self.make_evaluator()
        evaluator.observe(frame(1, 1_800_000))
        evaluator.observe(frame(2, 1_900_000))
        events = evaluator.observe(frame(3, 1_999_999, decoded_mono_us=2_100_000))
        self.assertIn("trial_timeout", event_names(events))
        self.assertNotIn("trial_success", event_names(events))

    def test_new_trial_cannot_reuse_previous_hold_or_previous_frame(self):
        evaluator = self.make_evaluator()
        for index, stamp in enumerate((1_010_000, 1_110_000, 1_210_000), 1):
            evaluator.observe(frame(index, stamp))
        evaluator.start("trial-2", 3_000_000, last_frame_id=3)
        evaluator.observe(frame(3, 1_210_000, decoded_mono_us=3_010_000))
        self.assertFalse(evaluator.last_evaluation["valid"])
        events = evaluator.observe(frame(4, 3_020_000))
        self.assertIn("framing_hold_started", event_names(events))
        self.assertNotIn("trial_success", event_names(events))

    def test_neutral_requirement_fails_closed_and_uses_inclusive_threshold(self):
        evaluator = self.make_evaluator(require_neutral=True)
        evaluator.observe(frame(1, 1_010_000), neutral_axes=(0.02, -0.02))
        self.assertTrue(evaluator.last_evaluation["valid"])
        events = evaluator.observe(frame(2, 1_110_000), neutral_axes=(0, float("nan")))
        self.assertEqual(events[-1].payload["reason"], "command_not_neutral")
        evaluator.observe(frame(3, 1_210_000))
        self.assertEqual(evaluator.last_evaluation["reason"], "command_not_neutral")

    def test_start_is_idempotent_but_cannot_replace_an_active_trial(self):
        evaluator = self.make_evaluator()
        evaluator.observe(frame(1, 1_010_000))
        evaluator.start("trial-1", 1_000_000)
        evaluator.observe(frame(2, 1_110_000))
        self.assertIn("trial_success", event_names(evaluator.observe(frame(3, 1_210_000))))
        with self.assertRaises(ValueError):
            evaluator.start("trial-1", 1_000_001)
        evaluator = self.make_evaluator()
        with self.assertRaises(ValueError):
            evaluator.start("another", 1_000_000)


if __name__ == "__main__":
    unittest.main()
