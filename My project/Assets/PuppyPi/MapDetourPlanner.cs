using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace PuppyPi
{
    // Plans only. Existing robot path execution performs stopped turns followed
    // by forward straight legs; this planner never sends motor commands.
    public static class MapDetourPlanner
    {
        private const int MaximumCells = 131072;
        private const int MaximumExpanded = 30000;
        private const int MaximumPoints = 20;
        private const int MaximumRawDetourPoints = 2048;
        private const double MaximumPostprocessingSeconds = .040;
        private const float FrontGuardStart = .16f, FrontGuardEnd = .46f, FrontGuardHalfWidth = .17f;
        // Cost-aware search and broad obstacle potentials follow the design
        // advice in Nav2's Smac planner / inflation tuning guide (own C#
        // implementation, no Nav2 runtime dependency or copied planner code):
        // https://docs.nav2.org/rolling/configuration_and_development/tuning_guide/
        // .80 m is a SOFT centreline preference, never an extra collision mask.
        private const float PreferredClearanceMeters = .80f, ClearanceCostWeight = 5f;
        private const float FixedTurnCostMeters = .12f, TurnCostMetersPerRadian = .15f;
        private static readonly int[] StepX = { 1, 1, 0, -1, -1, -1, 0, 1 };
        private static readonly int[] StepY = { 0, 1, 1, 1, 0, -1, -1, -1 };

        public static bool TryPlan(MapGridGeometry geometry, int[] occupancy, Vector3 robotPose,
            IReadOnlyList<Vector2> requestedPath, MapRobotFootprint footprint, out Vector2[] planned,
            out string error, bool requireDetour = false) =>
            TryPlanDetailed(geometry, occupancy, robotPose, requestedPath, footprint,
                out planned, out _, out error, requireDetour);

        // Skipped indices refer to requestedPath, including any duplicated robot
        // start element. They are committed only with a complete verified plan;
        // callers must not report those obstructed corners as physically reached.
        public static bool TryPlanDetailed(MapGridGeometry geometry, int[] occupancy, Vector3 robotPose,
            IReadOnlyList<Vector2> requestedPath, MapRobotFootprint footprint, out Vector2[] planned,
            out int[] skippedIntentIndices, out string error, bool requireDetour = false)
        {
            planned = Array.Empty<Vector2>();
            skippedIntentIndices = Array.Empty<int>();
            error = null;
            if (geometry == null || !geometry.IsValid || occupancy == null ||
                occupancy.Length != (long)geometry.width * geometry.height || footprint == null || !footprint.IsValid ||
                !Finite(robotPose.x) || !Finite(robotPose.y) || !Finite(robotPose.z) ||
                requestedPath == null || requestedPath.Count == 0 || requestedPath.Count > MaximumPoints)
                return Fail("Invalid map, footprint, pose or requested route.", out error);
            var robot = new Vector2(robotPose.x, robotPose.y);
            foreach (Vector2 point in requestedPath)
                if (!Finite(point.x) || !Finite(point.y) || Mathf.Abs(point.x) > 100 || Mathf.Abs(point.y) > 100)
                    return Fail("The requested route contains an invalid map coordinate.", out error);
            int consumedPrefix = MapPathGeometry.ReachedExecutionPrefix(requestedPath, robot);
            var requested = new List<Vector2> { robot };
            var sourceIndices = new List<int> { -1 };
            for (int sourceIndex = consumedPrefix; sourceIndex < requestedPath.Count; sourceIndex++)
            {
                Vector2 point = requestedPath[sourceIndex];
                if ((point - requested[requested.Count - 1]).sqrMagnitude > 1e-10f)
                { requested.Add(point); sourceIndices.Add(sourceIndex); }
            }
            if (requested.Count < 2) return Fail("The route has no remaining movement.", out error);
            if (MapPathGeometry.TryFindFootprintBlock(geometry, occupancy, new[] { robot }, robotPose.z, footprint, out _))
                return Fail("The current robot footprint is blocked or unobserved; no detour can start.", out error);
            var search = new Search(geometry, occupancy, footprint, robot, robotPose.z);
            var result = new List<Vector2> { robot };
            var skipped = new List<int>();
            float yaw = robotPose.z;
            bool changed = false;
            for (int i = 1; i < requested.Count; i++)
            {
                Vector2 start = result[result.Count - 1], goal = requested[i];
                // The inscribed disk lies within the rectangular footprint at
                // EVERY orientation. An obstruction here makes this corner
                // unusable, rather than merely requiring a different approach.
                // Only intermediate corners may be bypassed; never erase a free
                // but disconnected goal or the user's final endpoint.
                if (i < requested.Count - 1 && !MapPathGeometry.SegmentClear(geometry, occupancy,
                    goal, goal, Math.Min(footprint.HalfLength, footprint.HalfWidth)))
                { skipped.Add(sourceIndices[i]); changed = true; continue; }
                if (search.LegClear(start, goal, yaw, result.Count == 1))
                {
                    result.Add(goal);
                    yaw = Heading(start, goal);
                }
                else
                {
                    if (!search.Find(start, goal, yaw, result.Count == 1, out List<Vector2> leg, out error))
                    { error = "Route section " + i + ": " + error; return false; }
                    for (int j = 1; j < leg.Count; j++) result.Add(leg[j]);
                    yaw = Heading(result[result.Count - 2], result[result.Count - 1]);
                    changed = true;
                }
                if (result.Count > MaximumPoints)
                    return Fail("The safe detour exceeds the robot's 20-point limit. Draw a shorter route.", out error);
            }
            if (requireDetour && !changed)
                return Fail("The current map shows no different verified detour. Wait for the obstacle scan or draw another route.", out error);
            // A* may use short edges, but the real follower skips initial
            // points within arrival range. Verify the route it will execute.
            Vector2[] execution = MapPathGeometry.NormalizeExecutionPrefix(result, robot, out _);
            if (TryFindRouteBlock(geometry, occupancy, execution, robotPose.z, footprint, out _))
                return Fail("The planned route failed its final footprint check.", out error);
            planned = execution;
            skippedIntentIndices = skipped.ToArray();
            return true;
        }

        // Public to let runtime revalidation use the same robot frontal guard
        // as the planner. The physical footprint check remains separate.
        public static bool ForwardGuardClear(MapGridGeometry geometry, int[] occupancy,
            Vector2 start, Vector2 end, MapRobotFootprint footprint) =>
            !TryFindForwardGuardBlock(geometry, occupancy, start, end, footprint, out _);

        public static bool TryFindRouteBlock(MapGridGeometry geometry, int[] occupancy,
            IReadOnlyList<Vector2> path, float initialYawRadians, MapRobotFootprint footprint, out MapPathBlock block)
        {
            if (MapPathGeometry.TryFindFootprintBlock(geometry, occupancy, path, initialYawRadians, footprint, out block)) return true;
            for (int i = 1; i < path.Count; i++)
                if (TryFindForwardGuardBlock(geometry, occupancy, path[i - 1], path[i], footprint, out block))
                { block.SegmentIndex = i - 1; return true; }
            return false;
        }

        public static bool TryFindForwardGuardBlock(MapGridGeometry geometry, int[] occupancy,
            Vector2 start, Vector2 end, MapRobotFootprint footprint, out MapPathBlock block)
        {
            block = null;
            if (geometry == null || !geometry.IsValid || occupancy == null ||
                occupancy.Length != (long)geometry.width * geometry.height ||
                footprint == null || !footprint.IsValid || !Finite(start.x) || !Finite(start.y) ||
                !Finite(end.x) || !Finite(end.y))
            { block = new MapPathBlock { Kind = MapPathBlockKind.Invalid }; return true; }
            Vector2 difference = end - start;
            if (difference.sqrMagnitude < 1e-12f) return false;
            Vector2 heading = difference.normalized;
            float mid = (FrontGuardStart + FrontGuardEnd) * .5f;
            var guard = new MapRobotFootprint {
                lengthMeters = FrontGuardEnd - FrontGuardStart,
                widthMeters = Mathf.Max(FrontGuardHalfWidth, footprint.HalfWidth) * 2,
                marginMeters = 0
            };
            return MapPathGeometry.TryFindFootprintBlock(geometry, occupancy,
                new[] { start + heading * mid, end + heading * mid }, Heading(start, end), guard, out block);
        }

        private sealed class Search
        {
            private readonly MapGridGeometry _geometry;
            private readonly int[] _occupancy;
            private readonly MapRobotFootprint _body;
            private readonly Stopwatch _budget = Stopwatch.StartNew();
            private ClearanceField _clearance;
            private int _expanded;
            private long _postprocessingTicks;

            public Search(MapGridGeometry geometry, int[] occupancy, MapRobotFootprint body, Vector2 robot, float yaw)
            { _geometry = geometry; _occupancy = occupancy; _body = body; }

            public bool LegClear(Vector2 start, Vector2 goal, float incomingYaw, bool originalStart)
            {
                if ((goal - start).sqrMagnitude < 1e-12f) return true;
                float outgoingYaw = Heading(start, goal);
                if (Turns(incomingYaw, outgoingYaw) && !TurnClear(start, originalStart)) return false;
                return !MapPathGeometry.TryFindFootprintBlock(_geometry, _occupancy,
                           new[] { start, goal }, incomingYaw, _body, out _) &&
                       ForwardGuardClear(_geometry, _occupancy, start, goal, _body);
            }

            private bool TurnClear(Vector2 position, bool originalStart) => MapPathGeometry.SegmentClear(
                _geometry, _occupancy, position, position, _body.RotationRadius);

            public bool Find(Vector2 start, Vector2 goal, float incomingYaw, bool originalStart,
                out List<Vector2> leg, out string error)
            {
                leg = null;
                error = null;
                if (_budget.ElapsedMilliseconds >= 100)
                    return Fail("The detour planning time budget expired. The robot will stay stopped.", out error);
                if (!MapPathGeometry.SegmentClear(_geometry, _occupancy, goal, goal))
                    return Fail("An original waypoint is occupied, unobserved or outside the map. Move that corner.", out error);
                if (_clearance == null && !ClearanceField.TryBuild(_geometry, _occupancy, _budget, out _clearance))
                    return Fail("The clearance field exceeded the detour planning budget. The robot will stay stopped.", out error);
                // Anchor the lattice at the exact start. This preserves an
                // aligned passage through narrow straight corridors without
                // forcing a tiny initial rotation to a grid cell centre.
                float spacing = Mathf.Max(.10f, _geometry.resolution);
                double left = _geometry.origin.x, bottom = _geometry.origin.y;
                double right = left + (double)_geometry.width * _geometry.resolution;
                double top = bottom + (double)_geometry.height * _geometry.resolution;
                int x0 = (int)Math.Ceiling((left - start.x) / spacing);
                int x1 = (int)Math.Floor((right - start.x) / spacing);
                int y0 = (int)Math.Ceiling((bottom - start.y) / spacing);
                int y1 = (int)Math.Floor((top - start.y) / spacing);
                long wide = (long)x1 - x0 + 1, high = (long)y1 - y0 + 1;
                if (wide <= 0 || high <= 0 || wide > MaximumCells || high > MaximumCells || wide * high > MaximumCells)
                    return Fail("The map exceeds the bounded detour search area.", out error);
                int width = (int)wide, height = (int)high, cells = width * height;
                var context = new Lattice(start, spacing, x0, y0, width, height);
                int firstCell = (-y0) * width - x0;
                if (firstCell < 0 || firstCell >= cells)
                    return Fail("The robot lies outside the map.", out error);
                const int headings = 9;
                var cost = new float[cells * headings];
                var parent = new int[cost.Length];
                var seen = new bool[cost.Length];
                var closed = new bool[cost.Length];
                var edges = new sbyte[cells * 8];
                var edgeCosts = new float[edges.Length];
                var turns = new sbyte[cells];
                var open = new Heap();
                int first = firstCell * headings + 8;
                seen[first] = true;
                parent[first] = -1;
                open.Add(first, Vector2.Distance(start, goal));
                int bestGoalNode = -1;
                float bestGoalCost = float.PositiveInfinity;
                while (open.Count > 0)
                {
                    Entry entry = open.Take();
                    // Euclidean distance is a lower bound on our nonnegative
                    // distance + clearance + turn objective. Unlike an early
                    // line-of-sight exit, this does not discard a wider route.
                    if (entry.Score >= bestGoalCost) break;
                    int current = entry.Node;
                    if (closed[current]) continue;
                    closed[current] = true;
                    if ((_expanded & 63) == 0 && _budget.ElapsedMilliseconds >= 100)
                    {
                        if (bestGoalNode >= 0) break;
                        return Fail("The detour planning time budget expired. The robot will stay stopped.", out error);
                    }
                    if (++_expanded > MaximumExpanded)
                    {
                        if (bestGoalNode >= 0) break;
                        return Fail("No verified detour within the search budget. Draw a shorter route or wait for a clearer map.", out error);
                    }
                    int cell = current / headings, direction = current % headings;
                    Vector2 here = context.Position(cell);
                    float yaw = direction == 8 ? incomingYaw : (float)(direction * Math.PI / 4);
                    float distance = Vector2.Distance(here, goal);
                    if ((distance <= .75f || (_expanded & 31) == 0) &&
                        LegClear(here, goal, yaw, originalStart && current == first))
                    {
                        Profile(here, goal, out float connectionCost, out _);
                        float candidateCost = cost[current] + connectionCost +
                            (distance > 1e-6f ? TurnCost(yaw, Heading(here, goal)) : 0);
                        if (candidateCost < bestGoalCost)
                        { bestGoalCost = candidateCost; bestGoalNode = current; }
                    }
                    int x = cell % width, y = cell / width;
                    for (int outgoing = 0; outgoing < 8; outgoing++)
                    {
                        int nextX = x + StepX[outgoing], nextY = y + StepY[outgoing];
                        if (nextX < 0 || nextY < 0 || nextX >= width || nextY >= height) continue;
                        int nextCell = nextY * width + nextX;
                        int next = nextCell * headings + outgoing;
                        if (closed[next]) continue;
                        float nextYaw = (float)(outgoing * Math.PI / 4);
                        if (Turns(yaw, nextYaw))
                        {
                            if (originalStart && current == first)
                            { if (!TurnClear(here, true)) continue; }
                            else
                            {
                                if (turns[cell] == 0) turns[cell] = (sbyte)(TurnClear(here, false) ? 1 : -1);
                                if (turns[cell] < 0) continue;
                            }
                        }
                        int edge = cell * 8 + outgoing;
                        Vector2 there = context.Position(nextCell);
                        if (edges[edge] == 0)
                        {
                            edges[edge] = (sbyte)(!MapPathGeometry.TryFindFootprintBlock(_geometry, _occupancy,
                                new[] { here, there }, nextYaw, _body, out _) &&
                                ForwardGuardClear(_geometry, _occupancy, here, there, _body) ? 1 : -1);
                            if (edges[edge] > 0) Profile(here, there, out edgeCosts[edge], out _);
                        }
                        if (edges[edge] < 0) continue;
                        float proposed = cost[current] + edgeCosts[edge] + TurnCost(yaw, nextYaw);
                        if (seen[next] && proposed >= cost[next] - 1e-6f) continue;
                        seen[next] = true;
                        cost[next] = proposed;
                        parent[next] = current;
                        open.Add(next, proposed + Vector2.Distance(there, goal));
                    }
                }
                if (bestGoalNode >= 0)
                {
                    var raw = new List<Vector2>();
                    for (int node = bestGoalNode; node >= 0; node = parent[node])
                    {
                        if (raw.Count >= MaximumRawDetourPoints)
                            return Fail("The detour exceeds the bounded postprocessing sample budget. The robot will stay stopped.", out error);
                        raw.Add(context.Position(node / headings));
                    }
                    raw.Reverse();
                    if ((raw[raw.Count - 1] - goal).sqrMagnitude > 1e-12f) raw.Add(goal);
                    if (!Shortcut(raw, incomingYaw, originalStart, out leg))
                        return Fail("No verified route within the detour postprocessing time/point budget. The robot will stay stopped.", out error);
                    return true;
                }
                return Fail("No forward detour has verified body, turning and frontal clearance. The robot will stay stopped.", out error);
            }

            private bool Profile(Vector2 start, Vector2 end, out float cost, out float minimumClearance,
                Stopwatch postprocessing = null)
            {
                cost = float.PositiveInfinity;
                float length = Vector2.Distance(start, end);
                int samples = Math.Max(1, Math.Min(4096, (int)Math.Ceiling(length / Math.Min(.05f, _geometry.resolution * .5f))));
                double weighted = 0;
                minimumClearance = float.PositiveInfinity;
                for (int i = 0; i <= samples; i++)
                {
                    if ((i & 31) == 0 && postprocessing != null && PostprocessingExpired(postprocessing)) return false;
                    Vector2 point = start + (end - start) * (i / (float)samples);
                    float clearance = _clearance.At(point);
                    minimumClearance = Math.Min(minimumClearance, clearance);
                    float relative = Math.Max(0, 1 - clearance / PreferredClearanceMeters);
                    double density = 1 + ClearanceCostWeight * relative * relative;
                    weighted += density * (i == 0 || i == samples ? .5 : 1);
                }
                cost = (float)(length * weighted / samples);
                return true;
            }

            private bool Shortcut(List<Vector2> raw, float initialYaw, bool originalStart, out List<Vector2> simplified)
            {
                var postprocessing = Stopwatch.StartNew();
                try { return ShortcutWithinBudget(raw, initialYaw, originalStart, postprocessing, out simplified); }
                finally { _postprocessingTicks += postprocessing.ElapsedTicks; }
            }

            private bool PostprocessingExpired(Stopwatch active) =>
                _postprocessingTicks + active.ElapsedTicks >= Stopwatch.Frequency * MaximumPostprocessingSeconds;

            private bool ShortcutWithinBudget(List<Vector2> raw, float initialYaw, bool originalStart,
                Stopwatch postprocessing, out List<Vector2> simplified)
            {
                simplified = null;
                if (raw == null || raw.Count < 2 || raw.Count > MaximumRawDetourPoints || PostprocessingExpired(postprocessing)) return false;
                simplified = new List<Vector2> { raw[0] };
                var travelPrefix = new float[raw.Count];
                var turnPrefix = new float[raw.Count + 1];
                var minimums = new float[raw.Count - 1];
                for (int i = 1; i < raw.Count; i++)
                {
                    if (PostprocessingExpired(postprocessing) ||
                        !Profile(raw[i - 1], raw[i], out float travel, out minimums[i - 1], postprocessing)) return false;
                    travelPrefix[i] = travelPrefix[i - 1] + travel;
                }
                for (int i = 0; i < raw.Count; i++)
                {
                    if ((i & 31) == 0 && PostprocessingExpired(postprocessing)) return false;
                    turnPrefix[i + 1] = turnPrefix[i] + (i > 0 && i < raw.Count - 1 ?
                        TurnCost(Heading(raw[i - 1], raw[i]), Heading(raw[i], raw[i + 1])) : 0);
                }
                int at = 0;
                float yaw = initialYaw;
                while (at < raw.Count - 1)
                {
                    int selected = -1;
                    for (int next = raw.Count - 1; next > at; next--)
                    {
                        if (PostprocessingExpired(postprocessing)) return false;
                        if (!LegClear(raw[at], raw[next], yaw, originalStart && at == 0)) continue;
                        // Only use a new bend where the entire turn is known
                        // clear. Collinear raw nodes can remain in a corridor.
                        if (next < raw.Count - 1 && !TurnClear(raw[next], false) &&
                            Turns(Heading(raw[at], raw[next]), Heading(raw[next], raw[next + 1]))) continue;
                        if (!Profile(raw[at], raw[next], out float candidateTravel, out float candidateMinimum, postprocessing)) return false;
                        float originalMinimum = float.PositiveInfinity;
                        for (int j = at; j < next; j++)
                        {
                            if ((j & 31) == 0 && PostprocessingExpired(postprocessing)) return false;
                            originalMinimum = Math.Min(originalMinimum, minimums[j]);
                        }
                        // A binary-clear shortcut can otherwise erase the wide
                        // channel selected by cost-aware A*. Retain its minimum
                        // clearance and integrated cost, including changed turns.
                        if (candidateMinimum + Math.Min(.025f, _geometry.resolution * .5f) < originalMinimum) continue;
                        float originalCost = travelPrefix[next] - travelPrefix[at] +
                            turnPrefix[next] - turnPrefix[at + 1] + TurnCost(yaw, Heading(raw[at], raw[at + 1]));
                        float candidateCost = candidateTravel + TurnCost(yaw, Heading(raw[at], raw[next]));
                        if (next < raw.Count - 1)
                        {
                            float onward = Heading(raw[next], raw[next + 1]);
                            originalCost += TurnCost(Heading(raw[next - 1], raw[next]), onward);
                            candidateCost += TurnCost(Heading(raw[at], raw[next]), onward);
                        }
                        if (candidateCost > originalCost * 1.02f + .01f) continue;
                        selected = next;
                        break;
                    }
                    if (selected < 0) return false;
                    yaw = Heading(raw[at], raw[selected]);
                    simplified.Add(raw[selected]);
                    at = selected;
                    if (simplified.Count > MaximumPoints) return false;
                }
                if (PostprocessingExpired(postprocessing)) return false;
                bool verified = !TryFindRouteBlock(_geometry, _occupancy, simplified, initialYaw, _body, out _);
                return verified && !PostprocessingExpired(postprocessing);
            }
        }

        // Two-pass 8-neighbour chamfer potential. The .923879 factor bounds
        // octile distance below Euclidean distance; subtracting both cell
        // half-diagonals accounts for obstacle extent and query offset. This
        // conservative estimate is used only for SOFT cost. Exact footprint
        // geometry remains authoritative for all safety decisions.
        private sealed class ClearanceField
        {
            private readonly MapGridGeometry _geometry;
            private readonly float[] _distance;
            private ClearanceField(MapGridGeometry geometry, float[] distance)
            { _geometry = geometry; _distance = distance; }

            public static bool TryBuild(MapGridGeometry geometry, int[] occupancy, Stopwatch budget, out ClearanceField field)
            {
                field = null;
                if (occupancy.Length > 1048576) return false;
                int width = geometry.width, height = geometry.height;
                var distance = new float[occupancy.Length];
                const float diagonal = 1.41421356f;
                for (int y = 0; y < height; y++)
                {
                    if ((y & 31) == 0 && budget.ElapsedMilliseconds >= 100) return false;
                    for (int x = 0; x < width; x++)
                    {
                        int i = y * width + x;
                        if (occupancy[i] < 0 || occupancy[i] >= 50) { distance[i] = 0; continue; }
                        float d = Math.Min(Math.Min(x + .5f, width - x - .5f), Math.Min(y + .5f, height - y - .5f));
                        if (x > 0) d = Math.Min(d, distance[i - 1] + 1);
                        if (y > 0)
                        {
                            d = Math.Min(d, distance[i - width] + 1);
                            if (x > 0) d = Math.Min(d, distance[i - width - 1] + diagonal);
                            if (x + 1 < width) d = Math.Min(d, distance[i - width + 1] + diagonal);
                        }
                        distance[i] = d;
                    }
                }
                for (int y = height - 1; y >= 0; y--)
                {
                    if ((y & 31) == 0 && budget.ElapsedMilliseconds >= 100) return false;
                    for (int x = width - 1; x >= 0; x--)
                    {
                        int i = y * width + x;
                        float d = distance[i];
                        if (x + 1 < width) d = Math.Min(d, distance[i + 1] + 1);
                        if (y + 1 < height)
                        {
                            d = Math.Min(d, distance[i + width] + 1);
                            if (x > 0) d = Math.Min(d, distance[i + width - 1] + diagonal);
                            if (x + 1 < width) d = Math.Min(d, distance[i + width + 1] + diagonal);
                        }
                        distance[i] = d;
                    }
                }
                field = new ClearanceField(geometry, distance);
                return true;
            }

            public float At(Vector2 point)
            {
                int x = (int)Math.Floor(((double)point.x - _geometry.origin.x) / _geometry.resolution);
                int y = (int)Math.Floor(((double)point.y - _geometry.origin.y) / _geometry.resolution);
                if (x < 0 || y < 0 || x >= _geometry.width || y >= _geometry.height) return 0;
                return Math.Max(0, (_distance[y * _geometry.width + x] * .9238795f - 1.41421356f) * _geometry.resolution);
            }
        }

        private sealed class Lattice
        {
            private readonly Vector2 _start;
            private readonly float _spacing;
            private readonly int _x0, _y0, _width;
            public Lattice(Vector2 start, float spacing, int x0, int y0, int width, int height)
            { _start = start; _spacing = spacing; _x0 = x0; _y0 = y0; _width = width; }
            public Vector2 Position(int cell) => _start + new Vector2((cell % _width + _x0) * _spacing,
                (cell / _width + _y0) * _spacing);
        }

        private struct Entry
        {
            public int Node;
            public float Score;
            public long Order;
        }

        private sealed class Heap
        {
            private readonly List<Entry> _items = new List<Entry>();
            private long _order;
            public int Count => _items.Count;
            private static bool Before(Entry a, Entry b) => a.Score < b.Score || a.Score == b.Score && a.Order < b.Order;
            public void Add(int node, float score)
            {
                var entry = new Entry { Node = node, Score = score, Order = ++_order };
                int child = _items.Count;
                _items.Add(entry);
                while (child > 0)
                {
                    int parent = (child - 1) / 2;
                    if (!Before(entry, _items[parent])) break;
                    _items[child] = _items[parent];
                    child = parent;
                }
                _items[child] = entry;
            }
            public Entry Take()
            {
                Entry first = _items[0], last = _items[_items.Count - 1];
                _items.RemoveAt(_items.Count - 1);
                if (_items.Count == 0) return first;
                int parent = 0;
                while (parent * 2 + 1 < _items.Count)
                {
                    int child = parent * 2 + 1;
                    if (child + 1 < _items.Count && Before(_items[child + 1], _items[child])) child++;
                    if (!Before(_items[child], last)) break;
                    _items[parent] = _items[child];
                    parent = child;
                }
                _items[parent] = last;
                return first;
            }
        }

        private static bool Turns(float a, float b) => Math.Abs(Math.Atan2(Math.Sin(a - b), Math.Cos(a - b))) > 1e-5;
        private static float TurnCost(float a, float b)
        {
            float angle = (float)Math.Abs(Math.Atan2(Math.Sin(a - b), Math.Cos(a - b)));
            return angle > 1e-5f ? FixedTurnCostMeters + TurnCostMetersPerRadian * angle : 0;
        }
        private static float Heading(Vector2 a, Vector2 b) => (float)Math.Atan2((double)b.y - a.y, (double)b.x - a.x);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Fail(string message, out string error) { error = message; return false; }
    }
}
