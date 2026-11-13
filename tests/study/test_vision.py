import importlib.util
import json
import tempfile
import threading
import time
import unittest
from pathlib import Path

from puppypi_study.config import demo_config
from puppypi_study.framing import FrameObservation, FramingEvaluator
from puppypi_study.vision import LatestFrameQrWorker, OpenCvQrDecoder, iter_image_observations, iter_video_observations, load_observations


TARGET = "KHCI2027:inspection"


class Image:
    shape = (480, 640, 3)


class FakeDetector:
    texts = ("OTHER", TARGET)
    points = ([[1, 1], [21, 1], [21, 21], [1, 21]], [[270, 190], [370, 190], [370, 290], [270, 290]])

    def detectAndDecodeMulti(self, image):
        return True, self.texts, self.points, None


class FakeCv:
    QRCodeDetector = FakeDetector


class VisionAdapterTests(unittest.TestCase):
    def test_worker_copies_source_replaces_pending_and_preserves_frame_identity(self):
        entered, release, delivered = threading.Event(), threading.Event(), threading.Event()
        seen, observations = [], []

        class CopyImage:
            def __init__(self, value):
                self.value = value

            def copy(self):
                return CopyImage(self.value)

        class BlockingDecoder:
            def decode(self, image, **metadata):
                if metadata["frame_id"] == 1:
                    entered.set()
                    release.wait(2)
                seen.append((image.value, dict(metadata)))
                return FrameObservation(image_width=640, image_height=480, decoded_mono_us=metadata["received_mono_us"], **metadata)

        def collect(observation):
            observations.append(observation)
            if len(observations) == 2:
                delivered.set()

        worker = LatestFrameQrWorker(BlockingDecoder(), collect, clock_id="camera-branch-monotonic")
        try:
            source = CopyImage(10)
            self.assertTrue(worker.submit(source, frame_id=1, received_mono_us=100))
            self.assertTrue(entered.wait(1))
            source.value = 99
            worker.submit(CopyImage(20), frame_id=2, received_mono_us=200)
            worker.submit(CopyImage(30), frame_id=3, received_mono_us=300,
                          captured_mono_us=250, capture_clock_id="camera-branch-monotonic")
            self.assertEqual(worker.stats["pending"], 1)
            self.assertEqual(worker.stats["replaced"], 1)
            release.set()
            self.assertTrue(delivered.wait(1))
        finally:
            release.set()
            self.assertTrue(worker.close(1))
        self.assertEqual([item.frame_id for item in observations], [1, 3])
        self.assertEqual([item[0] for item in seen], [10, 30], "camera buffer reuse must not change queued input")
        self.assertEqual(observations[1].received_mono_us, 300)
        self.assertEqual(observations[1].captured_mono_us, 250)
        self.assertEqual(observations[1].clock_id, "camera-branch-monotonic")
        self.assertEqual(worker.stats["received"], 3)
        self.assertEqual(worker.stats["processed"], 2)
        self.assertEqual(worker.stats["failed"], 0)

    def test_worker_close_is_bounded_discards_pending_and_suppresses_late_delivery(self):
        entered, release = threading.Event(), threading.Event()
        observations = []

        class CopyImage:
            def copy(self):
                return CopyImage()

        class BlockingDecoder:
            def decode(self, image, **metadata):
                entered.set()
                release.wait(2)
                return FrameObservation(image_width=640, image_height=480, decoded_mono_us=metadata["received_mono_us"], **metadata)

        worker = LatestFrameQrWorker(BlockingDecoder(), observations.append)
        try:
            worker.submit(CopyImage(), frame_id=1, received_mono_us=100)
            self.assertTrue(entered.wait(1))
            worker.submit(CopyImage(), frame_id=2, received_mono_us=200)
            started = time.monotonic()
            self.assertFalse(worker.close(0.01))
            self.assertLess(time.monotonic() - started, 0.5)
            self.assertEqual(worker.stats["discarded"], 1)
            self.assertFalse(worker.submit(CopyImage(), frame_id=3, received_mono_us=300))
        finally:
            release.set()
            self.assertTrue(worker.close(1))
        self.assertEqual(observations, [])
        self.assertEqual(worker.stats["processed"], 1)
        self.assertFalse(worker.stats["alive"])

    def test_target_is_selected_instead_of_first_code(self):
        decoder = OpenCvQrDecoder(TARGET, FakeCv)
        observation = decoder.decode(Image(), frame_id=4, received_mono_us=100, decoded_mono_us=120)
        self.assertEqual(observation.marker_text, TARGET)
        self.assertEqual(observation.geometry()[:2], (0, 0))
        self.assertIsNone(observation.validation_error())
        self.assertIsNone(observation.captured_mono_us)
        self.assertGreaterEqual(observation.processing_duration_us, 0)

    def test_duplicate_target_and_decoder_exception_are_explicit_invalid_observations(self):
        decoder = OpenCvQrDecoder(TARGET, FakeCv)
        decoder._detector.texts = (TARGET, TARGET)
        observation = decoder.decode(Image(), frame_id=1, received_mono_us=100, decoded_mono_us=120)
        self.assertFalse(observation.data_valid)
        self.assertEqual(observation.invalid_reason, "ambiguous_target_marker")

        class Broken:
            def detectAndDecodeMulti(self, image):
                raise RuntimeError("test failure")

        decoder._detector = Broken()
        observation = decoder.decode(Image(), frame_id=2, received_mono_us=100, decoded_mono_us=120)
        self.assertFalse(observation.data_valid)
        self.assertEqual(observation.invalid_reason, "decoder_error:RuntimeError")

    def test_invalid_image_is_reported_without_calling_a_camera(self):
        decoder = OpenCvQrDecoder(TARGET, FakeCv)
        observation = decoder.decode(None, frame_id=1, received_mono_us=100, decoded_mono_us=120)
        self.assertFalse(observation.data_valid)
        self.assertEqual(observation.invalid_reason, "invalid_image_size")

    def test_single_code_api_without_multi_is_supported(self):
        class Single:
            def detectAndDecode(self, image):
                return TARGET, [[[270, 190], [370, 190], [370, 290], [270, 290]]], None

        decoder = OpenCvQrDecoder(TARGET, type("Cv", (), {"QRCodeDetector": Single}))
        result = decoder.decode(Image(), frame_id=1, received_mono_us=100, decoded_mono_us=120)
        self.assertEqual(result.marker_text, TARGET)
        self.assertTrue(result.data_valid)

    def test_replay_reads_same_observations_without_optional_dependencies(self):
        path = "fixtures/study/success.jsonl"
        first = [observation.to_dict() for observation in load_observations(path)]
        second = [observation.to_dict() for observation in load_observations(path)]
        self.assertEqual(first, second)
        self.assertEqual(len(first), 12)
        self.assertTrue(all(observation["decoder"] == "fixture" for observation in first))

    def test_nonfinite_or_malformed_json_reports_line_number(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "bad.jsonl"
            path.write_text('\n{"frame_id":NaN}\n', encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "line 2"):
                list(load_observations(path))

    def test_video_replay_rejects_device_and_network_input_before_open(self):
        decoder = OpenCvQrDecoder(TARGET, FakeCv)
        for source in (0, "/dev/video0", "http://robot/stream"):
            with self.subTest(source=source), self.assertRaises(ValueError):
                list(iter_video_observations(source, decoder))

    @unittest.skipUnless(importlib.util.find_spec("cv2"), "optional OpenCV dependency is not installed")
    def test_actual_qr_image_and_blank_are_decoded_by_opencv(self):
        decoder = OpenCvQrDecoder(TARGET)
        observations = list(iter_image_observations(
            ["fixtures/study/target_qr.pgm", "fixtures/study/blank.pgm"], decoder,
            start_mono_us=1_000_000, first_frame_id=20))
        self.assertEqual(observations[0].marker_text, TARGET)
        self.assertIsNotNone(observations[0].geometry())
        self.assertIsNone(observations[0].captured_mono_us)
        self.assertEqual(observations[0].clock_id, "replay_monotonic")
        self.assertTrue(observations[0].data_valid)
        self.assertIn(observations[1].marker_text, (None, ""))
        self.assertEqual(observations[1].received_mono_us, 1_100_000)

    @unittest.skipUnless(importlib.util.find_spec("cv2"), "optional OpenCV dependency is not installed")
    def test_actual_qr_file_replay_satisfies_hold_once_with_source_time_unknown(self):
        decoder = OpenCvQrDecoder(TARGET)
        evaluator = FramingEvaluator(demo_config().profile("demo-wide"), clock_id="replay_monotonic")
        evaluator.start("real-decoder-test", 1_000_000)
        transitions = []
        for observation in iter_image_observations(["fixtures/study/target_qr.pgm"] * 12, decoder,
                                                    start_mono_us=1_000_000):
            transitions.extend(event.event_type for event in evaluator.observe(observation))
        self.assertEqual(transitions.count("trial_success"), 1)
        self.assertEqual(evaluator.outcome, "success")
        self.assertFalse(evaluator.last_evaluation["capture_age_known"])

    @unittest.skipUnless(importlib.util.find_spec("cv2"), "optional OpenCV dependency is not installed")
    def test_recorded_video_is_decoded_on_deterministic_schedule(self):
        decoder = OpenCvQrDecoder(TARGET)
        cv = decoder.cv2
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "recorded-fixture.avi"
            writer = cv.VideoWriter(str(path), cv.VideoWriter_fourcc(*"MJPG"), 10, (640, 480))
            if not writer.isOpened():
                self.skipTest("local OpenCV MJPG file encoder is unavailable")
            try:
                writer.write(cv.imread("fixtures/study/target_qr.pgm"))
                writer.write(cv.imread("fixtures/study/blank.pgm"))
            finally:
                writer.release()
            first = list(iter_video_observations(path, decoder, start_mono_us=1_000_000, first_frame_id=4))
            second = list(iter_video_observations(path, decoder, start_mono_us=1_000_000, first_frame_id=4))
            self.assertEqual(len(first), 2)
            self.assertEqual(first[0].marker_text, TARGET)
            self.assertIn(first[1].marker_text, (None, ""))
            self.assertEqual([item.received_mono_us for item in first], [1_000_000, 1_100_000])
            self.assertEqual([item.frame_id for item in first], [4, 5])
            self.assertEqual([(item.frame_id, item.received_mono_us, item.marker_text, item.corners) for item in first],
                             [(item.frame_id, item.received_mono_us, item.marker_text, item.corners) for item in second])


if __name__ == "__main__":
    unittest.main()
