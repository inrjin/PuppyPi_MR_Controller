# MR Forward Stall Diagnosis

Recorded-scan replay reproduced a forward-route rejection caused by **unobserved cells**, without a confirmed obstacle in the rejected area. The user selected a map-known-free prior to permit previously mapped free space while current confirmed obstacles retain priority. A later read-only capture through the connected Quest confirmed an additional mismatch: the mapper classifies graded occupancy values through `25` as free, while the initial MR prior accepted only exact zero. The amber unknown marker clarifies the failure category; the free-space layer changes navigation permission. These diagnostics sent no robot motion commands and changed no robot driver/configuration.

## Reproduced evidence

The capture contains 97 exact source-stamp scan/pose pairs. The original-grid replay uses a 600×600 grid at 0.05 m resolution; the audit excluded the first 12 frames and evaluated the remaining 85. This analysis exclusion is not a runtime start delay.

| Check | Recorded result |
| --- | --- |
| Stationary body and turning area | Clear in 85/85 evaluated frames |
| Forward 0.30 m and 0.60 m body sweep | Unknown in 85/85 frames |
| Same routes including frontal guard | Unknown in 85/85 frames |
| First failing cell in the source accumulated grid | Exactly `0` (free) in 85/85 frames |
| Scan bins 435–447, approximately −12° to −2.4° | Null with intensity 0 in all 97 frames |

The failing area is roughly 0.45 m ahead and 0.05 m to the robot's right. Valid neighboring returns do not observe the interior of this persistent missing sector. A separate short-distance probe found 0.05 m body motion clear in 97/97 frames, but the forward guard rejected even a 0.001 m route. The guard extends beyond the physical body; longer routes also fail the actual body sweep, so relaxing only that extra guard would not solve the full stall.

A diagnostic replay replacing **only current unknown cells whose source accumulated value was exactly 0**, while retaining current confirmed obstacles, made both longer routes clear in 85/85 frames. That source snapshot has no per-cell observation times. This demonstrates dependence on the free-space policy, not proof that the old cells remain safe.

Local audit artifacts (ignored by Git):

- [Recorded scan/pose capture](../.local/quest/noise-sensor-samples.json)
- [Original-grid results and diagnostic fallback counts](../.local/quest/forward-stall-audit-summary.json)
- [Exported current, filtered and source occupancy layers](../.local/quest/forward-stall-grid.json)
- [Short-distance and heading probe output](../.local/quest/noise-forward-route-probe.txt), [probe source](../.local/map-geometry-check/NoiseForwardProbe.cs)

## Why CBOR alone does not fix this

In robot revision [`3d29ef16`](https://github.com/Ksyear/PuppyPI_Cpp_ROS2/tree/3d29ef16cebd12bfe3edeeed6c6b7d064a4e8a80), [`ScanAssembler._finish()`](https://github.com/Ksyear/PuppyPI_Cpp_ROS2/blob/3d29ef16cebd12bfe3edeeed6c6b7d064a4e8a80/ros2_ws/src/puppy_lidar/puppy_lidar/ld06.py#L191) initializes ranges as NaN and intensities as zero. Absent bins, near-filter rejection, self-filter rejection and invalid ranges all leave that same result. It never generates a positive-infinity clear-to-maximum return. The reviewed main-branch file had the identical blob. The recording cannot distinguish which rejection produced this sector.

Official rosbridge [JSON conversion](https://github.com/RobotWebTools/rosbridge_suite/blob/ros2/rosbridge_library/src/rosbridge_library/internal/message_conversion.py#L238) maps both NaN and infinity to null. Its [CBOR conversion](https://github.com/RobotWebTools/rosbridge_suite/blob/ros2/rosbridge_library/src/rosbridge_library/internal/cbor_conversion.py) preserves IEEE float arrays, as described in the [protocol](https://github.com/RobotWebTools/rosbridge_suite/blob/ros2/ROSBRIDGE_PROTOCOL.md#34-cbor-encoding--cbor-). Preserving the driver's existing NaN still supplies no measured-free evidence.

## Requested maintainer diagnostics

Request a read-only, timestamp-correlated capture before changing filters:

1. Active driver/calibration parameters and per-scan packet, CRC, angular-rejection and discarded-point counters.
2. For each affected bin, raw distance/confidence and reason: accepted, absent sample, near-filter rejection, self-filter rejection, below minimum, or above maximum. Multiple causes should remain distinguishable.
3. Optionally, mapper free-cell observation timestamps and source scan stamps, so MR can bound reuse of genuine free evidence instead of relying on an unaged accumulated value.

Exposing missing diagnostics may require a maintainer-owned code change. This document does not authorize or perform that change, disable filters, or reinterpret NaN as free.

## Selected navigation policy

The user selected **map-known-free prior plus confirmed current obstacle priority**. `MapKnownFreeLayer` fills only current unknown cells whose source `BaseOccupancy` is `0`–`25`, inclusive, matching the mapper's `free_thresh=0.25` classification. Source `-1`, `26`–`49`, and `50`–`100` remain ineligible. Confirmed current occupancy overrides this prior and revokes it at that world cell until a direct measured-free observation clears the revocation. A repeated map-free value, an older measured-free cache, an aligned crop, or a reconnect presentation reset cannot erase a confirmed conflict. Accumulated occupied cells are not restored to the display or collision mask.

Map-known-free permission stays separate from the at-most-one-second measured-free cache; it does not forge fresh per-cell timestamps. Existing current connection, calibration, map and matched scan/pose requirements remain. The selected prior assumes earlier free knowledge remains useful when current rays are absent; it does not certify live sensor coverage or new-obstacle detection.

## Implementation validation

The earlier exact-zero `MapKnownFreeLayer` replay passed both 0.30 m and 0.60 m forward route checks in **85/85 evaluated frames**, compared with 0/85 before the prior. In that replay current confirmed occupied cells remained occupied and every newly permitted cell required exact source-map `0`. The fixed map snapshot was recorded partway through the scan capture; a separate comparison restricted to the 28 scans at or after that map timestamp also improved from 0/28 to 28/28 for both distances. See the [production replay summary](../.local/quest/known-map-free-replay-summary.json).

That version passed 328 EditMode and 44 PlayMode tests, including persistent missing beams, obstacle revocation, crop/session changes, source-map refresh and sensor expiry; three optional external-service/hardware tests were skipped. Its Quest APK build succeeded. These historical results do not validate the later threshold adjustment or physical driving. Build details are in the [local build report](../.local/quest/known-map-free-build.json).

## Live graded-free mismatch

A bounded read-only capture through Quest started at **2026-11-11 08:07:05 UTC**, collecting one `/map`, four poses, four scans and calibration parameters in 4.6 seconds. The 600×600 grid at 0.05 m resolution contained 5,432 exact-zero cells and 8,884 cells valued `1`–`25`. The earlier UI-blocked coordinates `(2.48, 1.38)` and `(1.375, 3.975)` contained `11` and `2`; they were not source-unknown `-1`. In the exactly matched scan/pose pair, beams 405–409 toward the first cell were absent, so its prior free classification mattered. Another sampled occupied cell remained `100` and had a nearby current return; the threshold adjustment does not make that cell free.

The current predicate admits only source `0`–`25` into otherwise unknown cells. Regression tests cover the two captured graded-free values, inclusive `25` versus excluded `26`, a reduced real-coordinate missing-sector observation, and confirmed-hit veto retention across missing scans, cached free, crops and presentation resets. Only direct measured-free evidence releases a veto. No fresh laser timestamp is created by merging a prior.

The capture was later than the UI screenshot, and a diagnostic straight segment to that coordinate was not the recovered user sketch. It confirms the classification mismatch without proving the whole drawn route clear. Full private room data stays in ignored [capture artifacts](../.local/quest/live-map-capture-summary.json); hardware driving and the updated build require separate validation.
