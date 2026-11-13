# MR Forward Detour Planning

The MR implementation can prepare a forward-only detour as a sequence of stopped turns and straight legs using the robot's existing `path` command. Reverse is omitted. No robot service or configuration is changed by this repository.

## Existing robot contract

Reviewed against [`Ksyear/PuppyPI_Cpp_ROS2`](https://github.com/Ksyear/PuppyPI_Cpp_ROS2/tree/3d29ef16cebd12bfe3edeeed6c6b7d064a4e8a80) on 2026-11-10:

- [`path_follower.py`](https://github.com/Ksyear/PuppyPI_Cpp_ROS2/blob/3d29ef16cebd12bfe3edeeed6c6b7d064a4e8a80/ros2_ws/src/puppy_vr_bridge/puppy_vr_bridge/path_follower.py), `PathFollower.update`: the robot rotates toward a waypoint, then advances. A waypoint behind the robot means turning and then driving forward; it does not request reverse.
- Default obstacle guard: two or more scan points inside robot coordinates `0.16 < x < 0.46 m` and `abs(y) < 0.17 m` stop forward travel. A clear second allows resumption. Pose/scan timeout defaults to 0.6 s. The deployed configuration remains authoritative.
- [`protocol_v2.py`](https://github.com/Ksyear/PuppyPI_Cpp_ROS2/blob/3d29ef16cebd12bfe3edeeed6c6b7d064a4e8a80/ros2_ws/src/puppy_vr_bridge/puppy_vr_bridge/protocol_v2.py) accepts 1–20 `[x,y]` points. Existing packet, ACK, neutral, ownership, watchdog, and emergency-stop formats remain unchanged.
- [`receiver_node.py`](https://github.com/Ksyear/PuppyPI_Cpp_ROS2/blob/3d29ef16cebd12bfe3edeeed6c6b7d064a4e8a80/ros2_ws/src/puppy_vr_bridge/puppy_vr_bridge/receiver_node.py): neutral heartbeats preserve a path; nonzero manual input, watchdog expiry, or emergency stop cancels it.

## Planner and clearance

[`MapDetourPlanner.cs`](../My%20project/Assets/PuppyPi/MapDetourPlanner.cs) exposes `TryPlan(...)`, `TryPlanDetailed(...)`, `TryFindRouteBlock(...)`, and `TryFindForwardGuardBlock(...)`. Inputs are the current metre-based map, collision occupancy, measured robot pose/yaw, requested route, and configurable `MapRobotFootprint`.

Collision occupancy and the sketch texture must come from `PuppyPiMapDataClient.Occupancy`, never the accumulated `BaseOccupancy`. Obstacles are only current timestamp-matched LiDAR endpoints. Verified free space can bridge transient missing rays for at most one second using original observation ages; a current obstacle invalidates prior free evidence. Missing measurements do not create free space, and older obstacle points are not retained. Free evidence expires without waiting for another scan and is cleared on coordinate/connection/calibration resets. This keeps the sketch and dashboard's recent-obstacle semantics aligned.

- Every straight leg uses the oriented rectangular body footprint. Its size and walking margin are independent of the displayed line thickness. Straight corridors are not inflated into a turning circle at every point.
- Every changed heading requires the same configured physical rotation footprint, matching direct-route preflight. Wider detours are preferred through soft clearance cost, without an extra hard turning radius that would make an otherwise verified narrow passage unusable.
- A separate frontal rectangle models the robot's default 0.16–0.46 m guard with half-width at least 0.17 m. It includes the space beyond each target, preventing a waypoint from being placed where the robot would stop before reaching its turn.
- Occupancy values below zero are unknown; values at least 50 are occupied. Both block the route. Boundary crossings also block movement. Clearance is never obtained by treating missing scan returns as free space or reducing the footprint.

## Bounded straight route generation

The planner first retains any already-clear straight sections. For a blocked section it searches an eight-direction lattice anchored at the exact robot position, with heading included in the search state. This allows aligned forward travel through a narrow corridor without forcing an initial move toward a grid-cell centre. Exact swept-footprint checks prevent diagonal corner cutting.

The result is shortened only with verified straight connections. It starts at the current robot position, retains each reachable intentional original corner, and ends at the exact requested endpoint. An obstructed intermediate corner can be bypassed only when its inscribed footprint disk is blocked, making that pose unusable at every heading. `TryPlanDetailed` reports skipped indices into the original input list, including any duplicated start element. The runtime commits that metadata with a successful route; it must not report those corners as physically reached. A free but unreachable intermediate goal and an unusable final endpoint still fail. Every resulting segment and turn is checked again before returning a route.

Limits are 131,072 lattice cells, 30,000 expanded states, a 100 ms search budget across route sections, and 20 final points. If a time/expansion limit is reached after a valid goal connection was found, the best verified candidate may be returned; this is not a guarantee of globally optimal cost. Otherwise exceeding a limit returns an explanation and no executable route. Raw backtraces are capped at 2,048 samples; shortcutting, cost profiling, and its final validation share a separate cumulative 40 ms allowance across route sections. Frequent deadline checks reject excessive postprocessing without producing an executable partial route. Each call creates fresh search state, so a second run does not reuse an earlier side choice or obstacle cache.

## Clearance and turn cost

The design follows primary-source guidance from [Nav2's tuning guide](https://docs.nav2.org/rolling/configuration_and_development/tuning_guide/), [Smac planner](https://github.com/ros-navigation/navigation2/tree/main/nav2_smac_planner), and [Smac State Lattice parameters](https://docs.nav2.org/rolling/configuration_and_development/configuration_guide/planners_plugins/smac/smac_lattice/configuring_smac_lattice/): use obstacle potentials alongside exact footprint checking, penalize unnecessary rotations, and prevent shortcuts from entering higher-cost space. This is a small independent C# implementation, not a Nav2 port or a claim of equivalent navigation capability.

The planner lazily builds a two-pass eight-neighbour chamfer field from occupied cells, unknown cells, and map boundaries. Scaled distances with cell-extent allowance conservatively estimate centreline clearance; they affect soft cost only. The preferred clearance is 0.80 m, with traversal density `1 + 5 * max(0, 1 - clearance / 0.80)^2`. These values favor a wider channel without making a narrower verified corridor impassable. Turns add a fixed 0.12 m-equivalent cost plus 0.15 per radian, discouraging short zigzags. These are project policy parameters requiring physical evaluation, not values supplied by Nav2 or the robot manufacturer.

Search compares complete goal-connection costs rather than stopping at the first visible goal. Shortcutting preserves the search path's minimum estimated clearance within half a cell (capped at 2.5 cm), and its integrated clearance-plus-turn cost within 2% plus 1 cm-equivalent. Thus reducing point count cannot simply erase the wider passage chosen by the search.

## Replanning and execution requirements

The MR client must keep the original user route separate from an active detour, and preserve explicit RUN/STOP behavior. A dynamic obstacle requires confirmed cancellation before sending a replacement path. [PuppyPiPathClient.cs](../My%20project/Assets/PuppyPi/PuppyPiPathClient.cs) uses an accepted cancel sequence followed by a fresh inactive HTTP status requested after that receipt; an unconfirmed stop requests emergency stop after 1.5 s.

Replanning uses a fresh map/pose, validates the proposed route again before sending, and must not replay an unchanged route while the robot reports blocked. Failed planning, unknown space, stale telemetry, session changes, or manual takeover leave the robot stopped. No route resumes automatically after reconnect or map reset. Current checks depend on the configured footprint, calibration, map quality, and the actual robot's independent sensor guards; software tests do not establish physical walking accuracy.

## Validation and remaining hardware checks

`MapDetourPlannerTests.cs` covers straight/L/Z routes, wide obstacle bypass, blocked sides, unknown space, exact endpoint/corner preservation, explicit skipped-corner metadata, rotated footprints, narrow aligned corridors, repeat execution, protocol/search/postprocessing bounds, precise shared-guard diagnostics, wider-versus-shorter passage selection, mirrored layouts, and clearance-preserving shortcuts. Oversized winding input is rejected before quadratic candidate scans, and an alternating-wall maze exercises bounded failure. Standalone tests use real Unity/NUnit assemblies without motor traffic. A representative 342×244 grid with an obstacle and L route produced five points in approximately 12 ms on the development Mac; concurrent development load raised observed times to roughly 46–114 ms. Quest timing remains a hardware check.

On Quest, test a short route twice, then a single newly introduced obstacle, and verify the robot stops before a new route is sent. Check mirrored left/right passages, both sides blocked, occupied original corners, STOP/manual grab during detour, and map reset. Compare visible blocked-cell markers with the actual collision source.

True straight reverse would require robot-maintainer work: an explicit backward primitive, fresh rear-observation coverage, swept reverse and turn checks, bounded retreat/progress limits, cancellation integration, and capability negotiation. It is not approximated with a rear waypoint or automatic manual-joystick commands here.
