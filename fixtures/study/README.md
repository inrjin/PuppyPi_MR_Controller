# Inspection-framing fixtures

These are synthetic demo/test data, not participant data or robot recordings.

- `success.jsonl`: twelve mock observations at 100 ms intervals, starting at
  `1_000_000` microseconds, with exact target text `KHCI2027:inspection` and a
  centered 100 x 100 pixel quadrilateral in a 640 x 480 image. Decoder is
  explicitly `fixture`; capture timestamps are absent. A demo hold succeeds once
  at frame 11; frame 12 must not emit another success.
- `target_qr.pgm` and `blank.pgm`: generated grayscale images used by an actual
  OpenCV decoder. They contain a real QR pattern and a negative control.
- `generate_qr_fixture.py`: reproducible QR generation with OpenCV's encoder,
  with no camera/network input. The generator is not required for JSONL replay.

The core is standard-library-only. Run the tests from the repository root:

```sh
python3 -m unittest discover -s tests/study -p 'test_framing.py' -v
python3 -m unittest discover -s tests/study -p 'test_vision.py' -v
```

The actual image/video decoder checks require the optional local dependency:

```sh
.local/experiments/venv/bin/python -m unittest discover -s tests/study -p 'test_vision.py' -v
```

Local verification environment: CPython 3.9.6, `opencv-python-headless==4.14.0.94`
(`cv2.__version__ == '4.14.0'`), `numpy==2.0.2`. This does not describe the
robot's installed packages; those have not been inspected.

## Frame contract and time policy

`FrameObservation.to_dict()` is the JSON contract. It preserves `frame_id`,
`clock_id`, `received_mono_us`, `decoded_mono_us`, optional `captured_mono_us`
and `capture_clock_id`, image dimensions, decoded text, four ordered corners,
decoder identity, data validity, and optional `processing_duration_us`.

The normalization is `(mean_corner_x - width/2)/width`,
`(mean_corner_y - height/2)/height`, and shoelace polygon area divided by
`width*height`. Pixel corners must form a convex, nondegenerate quadrilateral
inside `[0,width-1] x [0,height-1]`. Both winding directions are accepted.
Tolerance and area bounds are inclusive. Empty decoding is a valid observation
that fails the target condition, not a healthy target decode.

`FramingEvaluator.start(trial_id, start_mono_us, last_frame_id=...)` freezes the
trial start and optional latest pretrial frame ID cutoff. `observe(...)` needs a
new ID, increasing frame time, fresh data and passing target/geometry/optional
neutral criteria. A duplicate, reverse-time, stale, malformed, wrong/unread
marker, out-of-range frame, or gap exceeding `max_frame_gap` resets the hold.
A frame immediately following an excessive gap is rejected; subsequent new
frames can start a new hold. `tick()` can reset or time out, never succeed.

Durations in config are seconds, rounded up to integer microseconds. Success is
emitted only on a new qualifying frame with source/reception-time hold span
at least `hold_seconds`. Evaluation/completion at or after the exact deadline
times out, even when capture occurred before it. Terminals are emitted once.
A previous trial's hold, pretrial time, or cutoff frame cannot count toward a
new trial.

Only capture timestamps explicitly on the evaluator clock are used for capture
age and hold timing. Unaligned capture clocks are preserved but never
subtracted; the evaluation labels `freshness_basis: reception` and
`capture_age_known: false`. To reject buffered pretrial images without aligned
capture time, pass the stream's latest frame ID at start and ensure the adapter
supplies genuine new IDs. Cross-clock source age cannot be proved by reception
freshness alone. Foreign reception clocks are rejected.

`iter_image_observations` and `iter_video_observations` open only existing local
regular files. Their `replay_monotonic` reception/completion schedule is
synthetic and deterministic; capture timestamps remain absent. Real processing
duration is recorded separately. Production integrations call
`OpenCvQrDecoder.decode(image, frame_id=..., received_mono_us=...)` with an array
branched from the existing robot camera pipeline. These adapters never open a
second camera device, and contain no assumed ROS topic or robot installation.

## Existing-camera worker adapter

`LatestFrameQrWorker(decoder, on_observation, on_error=None, clock_id=...)`
provides one background decoding thread and one replaceable pending slot.
Call `submit(image, frame_id=..., received_mono_us=..., captured_mono_us=...,
capture_clock_id=...)` from the existing camera's branch callback. Submit copies
the array, preserves its ID/timestamps, and returns without QR decoding or disk
writes. The callback can be `runtime.ingest_observation`; the robot integrator
must verify image RGB/BGR/grayscale format, dimensions and actual camera clock.

`stats` reports received, processed, replaced, failed, discarded and rejected
counts, plus pending/in-flight/accepting/alive state and the last error. Queue
replacement is explicit data loss, not a fabricated uninterrupted frame stream.
`close(timeout_seconds=1)` discards pending work and waits only for the bounded
timeout. It returns False while an uninterruptible decoder/callback remains in
flight; do not report that result as a successful flush. Callbacks execute on
the worker thread; no device is opened or robot command is sent by this worker.
