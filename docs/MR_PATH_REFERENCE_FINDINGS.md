# Robot Path Reference Validation

The motor-free validator imports the actual downloaded `path_follower.py` and `protocol_v2.py` from robot revision [`3d29ef16`](https://github.com/Ksyear/PuppyPI_Cpp_ROS2/tree/3d29ef16cebd12bfe3edeeed6c6b7d064a4e8a80). It opens no sockets, imports no ROS modules and changes no robot files. Source hashes and results are saved in the report.

```sh
python3 tools/validate_path_reference.py \
  --robot-src .local/robot-reference \
  --report TestResults/robot-path-reference.json
```

## Passing software scenarios

The 12 unittest methods exercise real protocol acceptance and following with a fake clock and an ideal planar plant:

- Straight, L and Z paths; four rotated/wrapped heading seeds with bounded tick jitter and dropped neutral heartbeats.
- Twenty consecutive paths using the same owner and increasing sequence numbers; other-owner and reordered-packet rejection.
- A 13-beam NaN front sector, isolated one-point noise, two-point braking, the one-second clear hold and original-route continuation, and a persistent obstacle.
- Watchdog/lease loss, manual takeover, emergency stop, path cancellation, pose/yaw jumps, stale pose/scan pauses, and the actual start-hold/slew limiters.

The robot arrival radius is 0.10 m. Targets 0.05 m and 0.10 m away correctly become `done` on the first 20 ms update with zero displacement. An MR client must distinguish this backend behavior from a command that failed to start.

## Separate external findings

These observations are reported **outside the passing-test count**. Passing this validator does not establish that every robot-side behavior is safe.

| Reference probe | Observed result | Scope |
| --- | --- | --- |
| Fresh pose, stale scan, initial turn toward a goal | `PathFollower.update()` returns `(0 cm/s, 0.14 rad/s)` before its scan-timeout check | The follower checks the scan after its turning branches. MR map/lifecycle cancellation remains a separate layer. |
| `motion_permitted=False`, neutral session, then a path; no preemption update | The pure protocol accepts the path and the adapted follower timer outputs `12.5 cm/s` | Path acceptance does not inspect this flag. Receiver preemption and the outer engine arbiter can independently prevent motion; they are not supplied in this probe. |

For maintainer review, check scan expiry before rotation output and verify that path admission and active-path updates honor the intended motion-permission/preemption contract. Preserve ownership, emergency-stop and neutral-ACK behavior. This is a diagnostic handoff, not a prerequisite or authorization to modify the robot for ordinary MR operation.

## Limits

The adapter mirrors receiver callback/timer ordering: a UDP operation receipt precedes its path callback, and neutral path heartbeats retain execution. It does not reproduce ROS scheduling, Wi-Fi delivery, Quest rendering, hand tracking or the complete engine arbiter. Drift-free kinematics do not verify physical tracking, stopping distances or corner clearance; the follower can turn within its 0.10 m arrival radius rather than exactly at a drawn corner. See the ignored [reference report](../TestResults/robot-path-reference.json) for measured simulation values and both external findings.
