# MR LiDAR Noise Filtering

Latest end-to-end and regression results: [navigation execution validation](MR_NAVIGATION_VALIDATION.md).

The MR client separates a raw sensor return, a confirmed obstacle, and a persistent route conflict. This prevents brief false walls from immediately changing the route. These navigation-policy changes affect MR only; robot-side watchdogs remain independent.

## Operator-supervised policy (default)

`PuppyPiPathClient.operatorSupervisedNavigation=true` allows the operator to select RUN without requiring every route/body cell to have measured-free permission. Unknown cells, missing rays, scan-filter startup and a drawn start more than 0.30 m from the robot do not independently block departure. Before RUN, confirmed obstacles can request a bypass immediately without the extra route-confirmation warm-up.

The raw display is unchanged: unknown remains light blue-grey and is never relabelled as a measured observation. `MapRoutePolicy` derives a separate padded planning grid containing current confirmed occupied cells; other cells and the crop's outer margin are traversable under operator supervision. It does not import accumulated occupied cells or invent sensor timestamps.

While moving, unknown cells or an unconfirmed robot `blocked` report retain the route. Missing/stale obstacle data pauses MR's bypass assessment instead of causing a scan-only cancellation. Persistent confirmed route conflicts still authorize a bypass, and confirmed occupancy in the robot's current body area still cancels immediately. There is no three-detour cap; an original route that becomes clear while stopping can be reused.

Manual STOP/emergency stop, ownership, neutral ACK, current position, coordinate-session identity and communication watchdogs remain active. Robot-side scan/pose freshness stops are not overridden. Set `operatorSupervisedNavigation=false` for strict behavior, including unknown-space rejection and scan-freshness cancellation. The evidence rules below remain applicable to the displayed map and strict navigation.

## Cell confirmation

`MapObstacleFilter` evaluates distinct source scans in map coordinates. Matching permits ±1 grid cell to tolerate endpoint jitter. Votes are spaced by at least 0.06 s and retain original observation times within a bounded 0.75 s history. Scan/pose pairs must satisfy the existing navigation deadline: 1.25 s by default, with a supported maximum of 1.5 s. A late but valid measured-free pair can therefore remain usable; older evidence cannot accumulate enough votes to bootstrap obstacle confirmation outside the 0.75 s history. Repeated scans never add votes or refresh their age.

| Current return | Required recent hits | Minimum hit span |
| --- | --- | --- |
| Coherent: at least two occupied cells in its 3×3 neighborhood | 3 of the last 4 sampled observations | 0.16 s |
| Isolated cell | 4 of the last 5 sampled observations | 0.24 s |

Recent confirmations can recognize a current return within ±1 cell. Two measured-free observations spanning at least 0.10 s release that confirmation track. Missing beams are not free votes. Only endpoints present in the current scan can appear as dark confirmed obstacles; the history does not draw old obstacle points.

Calibrated measured-free rays apply immediately. An unconfirmed candidate becomes unknown unless the cell has unexpired measured-free evidence, reusable for at most one second at its original observation time, or permission from the selected map-known-free layer. Only a confirmed occupied hit revokes older free evidence. Free evidence expires without new scans. Integer-cell viewport crops preserve overlapping history without renewing timestamps; connection, session, calibration and incompatible geometry changes reset it.

## Map-known-free evidence for display and strict navigation

`MapKnownFreeLayer` fills **only current unknown cells** where `BaseOccupancy` is `0`–`25`, inclusive. This matches the mapper's `free_thresh=0.25` free classification; the grid contains graded probabilities, so previously observed free space need not equal zero. Source values `-1`, `26`–`49`, and `50`–`100` never grant free permission. This is separate from the one-second measured-free cache and does not invent per-cell observation timestamps. Current confirmed occupancy always wins; old accumulated occupied points are not displayed or copied into collision checks.

A confirmed current hit revokes map-known-free permission at that world cell until a **direct measured-free observation** clears it. A later missing beam, cached free evidence or repeated map-free value `0`–`25` cannot clear that revocation, including after aligned viewport crops or reconnect presentation resets. In strict mode, remaining unknown cells still block the route. Supervised planning keeps confirmed obstacles but does not require these free-space sources for other cells.

The selected prior uses earlier mapped knowledge when a ray is missing; it is not live sensor coverage or a guarantee that a new obstacle will be detected. Calibration and exact scan/pose pairing still govern obstacle evidence; supervised route admission uses the policy above. Robot code and configuration remain unchanged by this MR mode.

## Route decisions

`MapObstacleReplanGate` requires a current confirmed occupied conflict on the same route segment, within 0.15 m of the initial conflict, in at least three distinct observations spanning 0.40 s. Accepted observations remain in its one-second history so ordinary delivery delay cannot prevent confirmation; every new observation must still be younger than 0.60 s. Duplicate stamps cannot advance the count or span.

- A brief candidate or unconfirmed robot `blocked` status keeps the original route. `blocked` duration alone never authorizes a bypass.
- Only in strict mode, three seconds of continuously unconfirmed `blocked` status cancels execution without a replacement route. Supervised mode retains the route.
- Confirmed occupancy in the robot's body area cancels immediately. In strict mode, remaining unknown cells and stale navigation data also stop. Supervised mode keeps the requested route while obstacle assessment is unavailable; lost control or current position still stops execution.
- A persistent confirmed route conflict permits a forward-only bypass after cancellation ACK, a later fresh inactive snapshot, neutral ACK, and current control/map checks. STOP and lifecycle changes revoke pending replacement.

Strict preview uses the same persistence gate before redrawing a blocked draft. Supervised preview skips that extra preflight gate because the obstacle cells are already temporally confirmed. `VERIFYING OBSTACLE` can still describe runtime persistence checking; supervised sensor gaps retain the requested route. The map status reports confirmed and checking counts; diagnostic logs expose `obstacles=raw/candidate/confirmed`.

## Earlier filter validation and limits

The recorded results below predate the default supervised policy. They validate evidence filtering and earlier strict navigation, not the changed route-admission policy. Current supervised-mode results are tracked in [navigation execution validation](MR_NAVIGATION_VALIDATION.md).

Filter/gate validation before known-free integration used an offline replay of 97 recorded scan/pose pairs with synthetic obstacles injected into those scans:

| Injected case | Raw-hit frames | Confirmed-hit frames | Replan decisions |
| --- | ---: | ---: | ---: |
| 15 separated one-frame ghosts | 15 | 0 | 0 |
| Four-frame burst lasting 0.296 s | 4 | 1 | 0 |

A sustained injected wall first confirmed at 0.296 s and first permitted replanning at 0.713 s. These are replay results, not physical stopping or driving measurements. No physical driving was performed in this verification. Regression tests cover transient, isolated and persistent hits, duplicate/stale observations, delayed delivery, free-space expiry and route-state barriers.

The [forward-stall diagnosis](MR_FORWARD_STALL_DIAGNOSIS.md) records the original unknown-sector failure and the implemented known-free policy. The earlier exact-zero policy replay permitted the 0.30 m and 0.60 m forward routes in 85/85 evaluated frames, retaining current confirmed obstacles; comparison restricted to scans after the saved map timestamp permitted 28/28. That version passed 328 EditMode and 44 PlayMode tests, with three optional external-service/hardware tests skipped. These are historical results, not validation of the expanded threshold.

A later read-only Quest capture found the UI-blocked source cells had values `11` and `2`, while the matched scan lacked rays toward the first cell. Regression tests now cover these graded-free values, the `25`/`26` boundary, absent rays, confirmed-hit revocation, direct-free restoration, and crop/reconnect retention. The map prior still has no per-cell observation ages; permission does not establish physical clearance or verified driving.

For device checks, inspect the map with emergency stop latched, then validate short routes on a clear floor under operator control. Horizontal LiDAR does not detect table edges or missing ground support. If raw robot braking remains after the MR fix, capture synchronized scans and path status before considering the [optional robot-maintainer follow-up](ROS2_LIDAR_FILTER_HANDOFF.md).
