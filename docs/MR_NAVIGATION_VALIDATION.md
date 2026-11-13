# MR Navigation Execution Validation

## Operator-supervised navigation is now the default — 2026-11-11

`PuppyPiPathClient.operatorSupervisedNavigation` defaults to `true`. The operator watches the robot; missing map observations authorize neither a wall nor a claim of measured free space. The displayed raw map remains unchanged. A separate padded planning grid retains current confirmed obstacles and permits unobserved cells, including space just beyond the displayed crop, consistently for preview, sending, body checks and bypass planning.

Removed default restrictions: full-route observed-free preflight, unknown/candidate-cell holds, the 0.30 m sketch-start limit, additional preflight obstacle-vote warmup and the three-bypass cap. The actual request still starts at the current robot position. Unknown space and an unconfirmed raw `blocked` report no longer cancel a running supervised route. Scan-only observation gaps retain the request; confirmed-obstacle decisions wait for fresh evidence. If an obstacle clears during a confirmed cancellation, a currently clear original route may be reused.

Preserved: explicit STOP/emergency stop, ownership, fresh neutral confirmation, manual-input precedence, control and execution-status watchdogs, current robot position and map identity, protocol point/coordinate limits, confirmed-body obstruction stops and failure to find a route around a real obstacle. Actual robot-side sensor waiting and timeouts remain independent. Automatic replacement still requires cancellation ACK and a later fresh inactive status; manual STOP never authorizes automatic resumption. Set the flag to `false` for the previous strict observed-space policy.

The first new PlayMode run passed all four actual-reference-robot scenarios: stationary RUN readiness and completion across unknown source cells; missing-scan startup and recovery with the same request; confirmed-obstacle bypass with explicit STOP; and a native sketch starting 0.45 m from the robot. Reports are `TestResults/PlayMode-supervised-focused.xml` and the runtime manifest `TestResults/supervised-navigation-sources.json`. Full regression/build verification is recorded separately. No physical-device result is claimed; Quest is disconnected.

## Previous live Quest findings and regression fixes — 2026-11-11

The user observed RUN repeatedly becoming unavailable while the physical robot was stationary, and some accepted starts stopping after about one second. Physical motion or localization drift is **not an established cause**. The four later read-only pose samples varied by 6.25 mm in X; those samples were not synchronized with a RUN failure.

Three concrete MR defects were reproduced and corrected:

- The mapper classifies probabilities `0..25` as known free; MR previously imported only exact `0`. Captured earlier blocking coordinates had source values `2` and `11`. Missing rays and the separate one-second measured-free expiry could therefore turn known free space into unknown space. Prior reuse now matches the producer classification. Unknown `-1`, values above `25`, confirmed-hit vetoes and direct-free clearing keep their existing behavior.
- A stored initial point only 4.81 cm from the current robot pose created a reverse connector and a rear-facing obstacle guard, although the robot follower consumes initial points within 10 cm. Preview, planning, transmission and replanning now consume that same initial prefix and anchor at the current pose. Later corners and returns remain ordered; short A* edges retain their full collision checks.
- A route invalidated before its first send entered remote cancellation and could escalate after 1.5 seconds. Only an operation with no send attempt and sequence zero now aborts locally and requests fresh neutral confirmation. Sent or uncertain deliveries still require the remote stop barrier.

The first complete PlayMode run of these changes recorded 70 passes, three failures and two optional skips (`PlayMode-live-map-fixes.xml`). One failure was an outdated UI assertion: an inactive command component correctly takes priority in RUN's block reason, while the planner's `NO ROUTE` explanation remains visible. Another exposed a close-obstacle reaction delay at the configured 50 mm walking margin; confirmed body intersections now use each new paired sensor frame instead of waiting for the existing 0.25 s route-check timer. Geometry, obstacle-confirmation votes and unknown-space handling are unchanged. The third was a real 0.301 s ACK timeout during a recovery test. Its cause is not established as a production defect; fixture frame-gap diagnostics were added and the existing 0.3 s timeout/order remains unchanged. These failed results are retained, not counted as passes.

The old APK recorded a route `Unknown` at 16:56:49.848 followed by an emergency ACK 1.608 seconds later. This does not establish which phase or cancellation branch caused the latest reported one-second stop. The new development build logs RUN gates, path token/phase, exact cancellation and finish reasons, source cell classification, map ages, ordinary ACK age and media metadata age at the event. Existing freshness and cancellation deadlines are unchanged.

Live evidence is retained under `.local/quest/live-map-capture*.json` and `quest-live-experiment-follow.log`; source hashes and limitations are in `TestResults/live-map-fixes-source.json`.

Final software checks: **445/445 EditMode**, **75 passed / zero failed / two optional skips** in the full 77-case PlayMode run. All 16 actual-robot-code reference cases recorded zero configured walking-margin contacts and emergency commands. A separate focused run passed eight cases; these are repeated cases, not additional unique coverage. The revised graded-map test also passed a continuous 2.2-second stationary RUN-readiness check before its reached-start challenge and actual forward completion. The failed initial results remain recorded. Final reports are `EditMode-live-fixes-final.xml`, `PlayMode-live-fixes-final.xml`, `PlayMode-live-fixes-focused.xml`, `PlayMode-stationary-graded.xml` and the independent `live-fixes-final-peer-review.json` under `TestResults/`.

The two optional skips require an explicitly configured physical read-only map probe or a separate study service. Physical Wi-Fi/ROS delays, real walking and the latest reported one-second stop remain unverified by these localhost simulations.

The replacement ARM64 development APK built successfully: `.local/quest/PuppyPiQuest.apk`, 158,178,833 bytes, SHA-256 `40e32a93a178087cdba19c5cb06eedf32263c56db9e0b64b5d426ebef576ef8f`. Compiled IL2CPP code includes prefix normalization, immediate confirmed-body checks, the corrected mapper threshold and event diagnostics; test peers are excluded and validated runtime source hashes match. Build verification is in `.local/quest/live-map-fixes-apk-verification.json`. Installation was attempted but ADB reported no Quest; the user confirmed it is disconnected. **This replacement is not installed, and its physical execution is not verified.** No remote emergency-stop clear or physical motion command was sent.

## Previous hard-scenario validation — 2026-11-11

The robot assumption remains the minimal [handoff](ROS2_PATH_RELIABILITY_HANDOFF.md): remove raw robot LiDAR blocked/clear-hold, let MR own obstacle cancellation/detours, and identify actual execution by owner UUID and path sequence. The robot patch was freshly applied to the pinned source; its bytes match the actual Python modules used in Unity. It has not been deployed to a robot.

| Final verification | Result |
| --- | --- |
| Unity EditMode, including nine mixed-cell regressions | 401 passed, 0 failed |
| Selected PlayMode: actual Python flows and UDP regressions | 17 passed, 0 failed |
| Actual Python coupled flows within those 17 tests | 10; zero unwanted emergency command events, zero configured walking-margin contacts |
| Separate pure actual-robot stress battery | 1,024 consecutive routes and 88 expected stop/rejection cases passed |

### Defects found and corrected

The first hard run reproduced a false emergency stop: one lost cancel packet plus a 350 ms cancel ACK delay caused MR to discard every receipt using the ordinary 200 ms ACK age. Known path-operation receipts now have a separate correlation window equal to the existing 1.5 s cancellation budget. Ordinary ACK freshness (0.2 s), connection timeout (0.3 s), cancellation deadline (1.5 s), and the fresh post-ACK inactive HTTP barrier are unchanged. Healthy duplicate out-of-order path receipts no longer override valid execution/session state; fresh applicable emergency/fault responses retain their original handling.

The next run exposed an intermittent second-obstacle failure: an unknown cell could be the first route block even while independent confirmed obstacle evidence was on the same corridor. MR still stops immediately at unknown space. With independent on-route obstacle evidence, it now retains the detour intent, confirms cancellation/inactive state/neutral, and applies the existing three-observation/0.4 s verification while stopped. Unknown-only, unknown body space, off-route obstacles, manual STOP, stale data and lost context cannot authorize a detour. A* still receives the unchanged occupancy grid with unknown cells blocked.

### Injected conditions

- Near two-point ghosts at 0.2 m for 0.1 s every 0.3 s, every third heartbeat dropped, 500 ms path HTTP delay and 1,000 ms path ACK delay; both runs finish.
- One cancel packet lost plus 350 ms cancel ACK delay; automatic replacement and explicit STOP complete without emergency fallback.
- A sudden circular obstacle centered 0.30 m ahead; the tested model stops normally before its configured walking rectangle contacts it.
- All laser beams missing during motion, or actual sensor publications paused; ordinary cancellation, retained editing and a new explicit RUN after recovery.
- A second obstacle placed 0.50 m ahead during the first bypass's drive phase. Three repetitions receive the second replacement: one reaches the original goal; two stop explicitly after the third path is received.
- Fast completion before the first usable status sample, stale identical-route completion, 350 ms/expired operation receipts, and fresh duplicate receipts carrying real emergency/fault state.

The pure robot battery separately uses 16 seeds × 64 same-session routes, bounded 12–28 ms tick jitter, sensor/heartbeat/ACK loss and expected safety faults. It executes actual protocol/server/follower/limiters with a virtual clock; these are not 1,024 complete MR input/UI runs.

Results, source hashes and the two pre-fix failures are preserved in [robot-hard-scenarios-summary.json](../TestResults/robot-hard-scenarios-summary.json). Raw final reports are `TestResults/EditMode-hard-final.xml` and `TestResults/PlayMode-robot-hard-final.xml`. Exact mixed-cell state invariants are tested independently in EditMode; operational repetitions are not exhaustive branch-incidence proof.

### Scope and build state

The initial occupancy grid is already observed free. Sensors, timing faults and circular obstacles are synthetic; movement is an ideal planar model. The contact oracle is an oriented rectangle including a 50 mm walking allowance, so its contacts describe configured model margins rather than measured body collisions. Physical Wi-Fi, ROS scheduling, Quest tracking, walking drift, braking distance and fresh-map startup remain unverified.

The hard-scenario source was subsequently built and installed on Quest serial `2G0YC5ZHBG029D`; the installed APK SHA-256 was `7a394b275566ea675623d0e0bcbf69e34eadbf5df0a25037b48945a05b71db87`. That installed version predates the live findings above. Simulation success did not establish physical operation. No robot repository or runtime configuration was modified by these MR simulations.

## Previous validation baseline

The counts, APK hash and recorded-scan results below describe the earlier source/build, before the changes above.


## Final software results — 2026-11-11

| Validation | Result |
| --- | --- |
| Unity EditMode | 348 passed, 0 failed, 0 skipped |
| Unity PlayMode | 53 passed, 0 failed; 2 optional tests skipped |
| Complete execution scenarios | All 9 passed; included in the PlayMode count |
| Fresh real-server ACK decode | 1 passed; a repeat of a case already in EditMode |
| Actual robot reference battery | 12 passed, including 20 consecutive same-session runs |
| Actual UDP wire replay | All 6 Unity-generated packets and ACK states passed |
| Recorded-scan stress | 36 scenarios / 3,492 frame evaluations |
| Short-stroke geometry audit | 512 combinations; no significant waypoint overshoot |

The complete bypass scenario ran for approximately 49 seconds at the model's default speed. It reached the original goal within 0.15 m, completed the next explicit RUN, emitted exactly three path packets with one automatic cancellation, maintained neutral heartbeats and recorded zero body/obstacle contacts. The final peer uses the same exact 13 missing beams for both sensor publications and its guard.

The two skipped tests are the opt-in physical robot map probe and the separate inspection-study Python service. Neither is silently counted as passed. Results are saved in `TestResults/EditMode-navigation-validation.xml`, `PlayMode-navigation-validation.xml`, `robot-v2-wire.json` and `robot-path-reference.json`.

The Quest ARM64 APK build succeeded. Its generated IL2CPP code contains the RUN synchronization fix, the tested source hashes match, and the localhost test peer is excluded from the APK. See the [detailed validation/build report](../.local/quest/navigation-validation-report.json). USB discovery returned no Quest, so this APK has not been installed or physically driven during validation.

## Reproduction environment

The PlayMode suite runs the actual `PuppyPiRobotController`, grip input, map client, map window, path client and UDP client in Unity 6000.0.62f1. Native `PointableCanvas` pointer events draw for approximately 0.4 seconds across multiple frames. The tests require real controller motion blocking while drawing and a new neutral ACK after release. Readiness, ownership, execution phases and freshness timestamps are not forced through reflection.

The peer binds only to localhost: HTTP 8090, WebSocket 9090 and an ephemeral UDP port. It publishes a calibrated 450-ray scan and exactly matched pose at approximately 10 Hz, including the recorded persistent missing bins 435–447. Those same missing bins are excluded from the peer's frontal guard. The free map has 0.05 m cells. Camera streams are disabled; media metadata and all route-permission channels remain active.

The peer uses an ideal planar plant at 0.125 m/s, 0.14 rad/s and a 0.10 m arrival radius. Passive contact detection checks the obstacle circle against an oriented body rectangle, including the configured walking allowance. This is not a physical tracking model.

## Complete execution scenarios

| Scenario | Required observation |
| --- | --- |
| First RUN and second RUN | Actual UDP paths reach the peer, HTTP confirms activity and completion, the original UUID is retained, and neutral heartbeats continue without path replay. Delayed and missing path ACKs are included. |
| Persistent obstacle and STOP | The current scan confirms a new obstacle, the old path is cancelled, and a replacement is withheld until cancellation ACK plus a later fresh inactive snapshot. STOP revokes further replacement. |
| Complete bypass and another RUN | The replacement reaches the original goal within 0.15 m, passive contact count remains zero, and another explicitly drawn path completes in the same session. |
| Ingress ACK without a follower | Acknowledgement alone never becomes running; bounded timeout requests cancellation and does not replay the path. |
| Entire scan missing | Known map free cells cannot bypass live-sensor requirements. The draft survives; explicit RUN works after observations recover. |
| Telemetry loss while running | The client cancels; restored telemetry does not automatically restart the old path. This injects loss between the bridge and MR, not a stopped physical sensor. |
| Arbiter permission loss | Real ROS status blocks and cancels execution. Permission recovery requires a new explicit RUN. |
| Already-arrived short stroke | Both 0.05 m and 0.10 m strokes produce no path datagram and explain the arrival tolerance immediately. Actual 6 mm input roughness is retained. |
| RUN before window LateUpdate | An update-order hook observes a newer map while the old bypass preview is stale. The public RUN action refreshes and validates that revision before sending. |

Source: `Assets/PuppyPi/Tests/PlayMode/PuppyPiPathEndToEndTests.cs` and its localhost-only `LocalPathRobot` peer.

## Additional checks

- `MapNavigationScenarioTests`: seeded first/second-run sensor traces, nonzero calibration, rotated headings, integer crops, transient/persistent returns, duplicate/stale observations and an independent circle/body collision oracle.
- Recorded-scan stress: 36 scenarios and 3,492 frame evaluations formed from the same 97 recorded pairs using rigid transforms and injected returns. These are not independent physical trials. The single mid-capture map snapshot and the post-map-timestamp subset are reported separately in `.local/quest/recorded-navigation-stress-summary.json`.
- Short-stroke audit: 512 geometry/sampling combinations check that input roughness does not create substantial waypoint overshoot. Five-centimetre strokes may contain multiple return supports; all remain inside the no-op threshold.
- Actual robot reference: the unchanged public `PathFollower`, protocol and limiters from revision `3d29ef16` run with a virtual clock. The battery includes straight/L/Z paths, angle wrap, bounded heartbeat loss and twenty consecutive routes. Source hashes and limitations are in `TestResults/robot-path-reference.json`.
- Actual UDP wire replay: six Unity-generated packets pass through that repository's real `V2UdpServer` on localhost. C# then decodes the returned ACKs.

## Repeating the checks

Run the Unity EditMode suite, the wire validator, the ACK decode test and the PlayMode suite. Keep one Unity Editor process active at a time. This Mac uses `BEE_BUILD_THREADS=2` for builds and batch test runs.

```sh
python3 tools/validate_ros2_wire.py --robot-src .local/robot-reference
python3 tools/validate_path_reference.py --robot-src .local/robot-reference \
  --report TestResults/robot-path-reference.json
```

The reference directory contains downloaded pure robot modules, not the installed robot's runtime configuration. Reports and downloaded captures under `TestResults/` and `.local/` are ignored by Git.

## Fixes and limits

RUN now synchronizes the current map before validating a displayed preview. Routes entirely inside the follower's arrival area are rejected before sending, with a 1 mm numerical allowance, rather than waiting for motion that the robot considers already complete.

Passing software tests does not establish Quest hand tracking, Wi-Fi performance, actual installed robot parameters, walking drift, stopping distance or physical corner clearance. The follower may start turning within its arrival radius. The [separate robot reference findings](MR_PATH_REFERENCE_FINDINGS.md) describe stale-scan rotation and the protocol's motion-permission/preemption interaction; MR permission and telemetry cancellation are tested independently. Robot code and configuration have not been modified.
