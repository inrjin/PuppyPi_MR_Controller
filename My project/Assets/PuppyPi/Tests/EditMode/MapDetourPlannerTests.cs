using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace PuppyPi.Tests
{
    public class MapDetourPlannerTests
    {
        private static MapGridGeometry Grid() => new MapGridGeometry {
            width = 120, height = 120, resolution = .05f, origin = new Vector2(-2, -2), frame = "map"
        };
        private static void Cell(MapGridGeometry grid, int[] cells, float x, float y, int value = 100)
        {
            int cx = Mathf.FloorToInt((x - grid.origin.x) / grid.resolution);
            int cy = Mathf.FloorToInt((y - grid.origin.y) / grid.resolution);
            cells[cy * grid.width + cx] = value;
        }
        private static void Box(MapGridGeometry grid, int[] cells, float x0, float y0, float x1, float y1, int value = 100)
        {
            for (int y = 0; y < grid.height; y++)
            for (int x = 0; x < grid.width; x++)
            {
                float mx = grid.origin.x + (x + .5f) * grid.resolution;
                float my = grid.origin.y + (y + .5f) * grid.resolution;
                if (mx >= x0 && mx <= x1 && my >= y0 && my <= y1) cells[y * grid.width + x] = value;
            }
        }
        private static void AssertSafe(MapGridGeometry grid, int[] cells, Vector3 pose, Vector2[] route)
        {
            Assert.That(route[0], Is.EqualTo(new Vector2(pose.x, pose.y)));
            Assert.That(route.Length, Is.LessThanOrEqualTo(20));
            Assert.That(MapPathGeometry.TryFindFootprintBlock(grid, cells, route, pose.z, new MapRobotFootprint(), out _), Is.False);
            for (int i = 1; i < route.Length; i++)
                Assert.That(MapDetourPlanner.ForwardGuardClear(grid, cells, route[i - 1], route[i], new MapRobotFootprint()), Is.True);
        }

        [Test] public void ClearStraightRouteRemainsExactlyOneStraightLeg()
        {
            var grid = Grid(); var cells = new int[grid.width * grid.height];
            var target = new Vector2(2, 0);
            Assert.That(MapDetourPlanner.TryPlan(grid, cells, Vector3.zero, new[] { Vector2.zero, target },
                new MapRobotFootprint(), out var route, out var error), Is.True, error);
            Assert.That(route, Is.EqualTo(new[] { Vector2.zero, target }));
        }

        [Test] public void ClearIntentionalLCornersArePreserved()
        {
            var grid = Grid(); var cells = new int[grid.width * grid.height];
            var input = new[] { Vector2.zero, new Vector2(2, 0), new Vector2(2, 2) };
            Assert.That(MapDetourPlanner.TryPlan(grid, cells, Vector3.zero, input,
                new MapRobotFootprint(), out var route, out var error), Is.True, error);
            Assert.That(route, Is.EqualTo(input));
            AssertSafe(grid, cells, Vector3.zero, route);
        }

        [Test] public void FrontalObstacleProducesForwardStraightBypassAndExactEndpoint()
        {
            var grid = Grid(); var cells = new int[grid.width * grid.height];
            Box(grid, cells, .4f, -.1f, .6f, .1f);
            var endpoint = new Vector2(2, .013f);
            Assert.That(MapDetourPlanner.TryPlan(grid, cells, Vector3.zero, new[] { Vector2.zero, endpoint },
                new MapRobotFootprint(), out var route, out var error), Is.True, error);
            Assert.That(route.Length, Is.GreaterThan(2));
            Assert.That(route[route.Length - 1], Is.EqualTo(endpoint));
            Assert.That(route.Any(p => Mathf.Abs(p.y) > .3f), Is.True);
            AssertSafe(grid, cells, Vector3.zero, route);
        }

        [Test] public void LRouteBypassesObstacleBeforeReturningToItsIntentionalCorner()
        {
            var grid = Grid(); var cells = new int[grid.width * grid.height];
            Box(grid, cells, .9f, -.2f, 1.1f, .2f);
            var corner = new Vector2(2, 0); var end = new Vector2(2, 2);
            Assert.That(MapDetourPlanner.TryPlan(grid, cells, Vector3.zero, new[] { Vector2.zero, corner, end },
                new MapRobotFootprint(), out var route, out var error), Is.True, error);
            Assert.That(route.Contains(corner), Is.True);
            Assert.That(route[route.Length - 1], Is.EqualTo(end));
            AssertSafe(grid, cells, Vector3.zero, route);
        }

        [Test] public void ZRouteKeepsEachOriginalCornerWhenAddingADetour()
        {
            var grid = Grid(); var cells = new int[grid.width * grid.height];
            Box(grid, cells, .9f, -.2f, 1.1f, .2f);
            var input = new[] { Vector2.zero, new Vector2(2, 0), new Vector2(0, 2), new Vector2(2, 2) };
            Assert.That(MapDetourPlanner.TryPlan(grid, cells, Vector3.zero, input,
                new MapRobotFootprint(), out var route, out var error), Is.True, error);
            foreach (var corner in input) Assert.That(route.Contains(corner), Is.True);
            AssertSafe(grid, cells, Vector3.zero, route);
        }

        [Test] public void BothSidesBlockedDoesNotInventAnEscape()
        {
            var grid = Grid(); var cells = new int[grid.width * grid.height];
            Box(grid, cells, .8f, -2, 1.1f, 4);
            Assert.That(MapDetourPlanner.TryPlan(grid, cells, Vector3.zero, new[] { Vector2.zero, new Vector2(2, 0) },
                new MapRobotFootprint(), out _, out var error), Is.False);
            Assert.That(error, Is.Not.Empty);
        }

        [Test] public void UnknownBarrierIsNeverCrossed()
        {
            var grid = Grid(); var cells = new int[grid.width * grid.height];
            Box(grid, cells, .8f, -2, 1.1f, 4, -1);
            Assert.That(MapDetourPlanner.TryPlan(grid, cells, Vector3.zero, new[] { Vector2.zero, new Vector2(2, 0) },
                new MapRobotFootprint(), out _, out _), Is.False);
        }

        [Test] public void BlockedIntermediateCornerIsReportedAndBypassedWithoutPretendingItWasReached()
        {
            var grid = Grid(); var cells = new int[grid.width * grid.height];
            Cell(grid, cells, 1.025f, .025f);
            var blocked = new Vector2(1.025f, .025f);
            Assert.That(MapDetourPlanner.TryPlanDetailed(grid, cells, Vector3.zero,
                new[] { Vector2.zero, blocked, new Vector2(2, 0) },
                new MapRobotFootprint(), out var route, out var skipped, out var error), Is.True, error);
            Assert.That(skipped, Is.EqualTo(new[] { 1 }));
            Assert.That(route.Contains(blocked), Is.False);
            Assert.That(route[route.Length - 1], Is.EqualTo(new Vector2(2, 0)));
            AssertSafe(grid, cells, Vector3.zero, route);
        }

        [Test] public void NarrowAlignedCorridorUsesBodyRectangleWithoutInflatingEveryPointToATurnCircle()
        {
            var grid = Grid(); var cells = new int[grid.width * grid.height];
            Box(grid, cells, -2, .20f, 4, 4);
            Box(grid, cells, -2, -2, 4, -.20f);
            Assert.That(MapDetourPlanner.TryPlan(grid, cells, Vector3.zero, new[] { Vector2.zero, new Vector2(2, 0) },
                new MapRobotFootprint(), out var route, out var error), Is.True, error);
            Assert.That(route.Length, Is.EqualTo(2));
        }

        [Test] public void RotatedRobotCanPivotAtStartAndThenUseStraightForwardSections()
        {
            var grid = Grid(); var cells = new int[grid.width * grid.height];
            Box(grid, cells, .9f, -.2f, 1.1f, .2f);
            var pose = new Vector3(0, 0, (float)Math.PI / 4);
            Assert.That(MapDetourPlanner.TryPlan(grid, cells, pose, new[] { Vector2.zero, new Vector2(2, 0) },
                new MapRobotFootprint(), out var route, out var error), Is.True, error);
            AssertSafe(grid, cells, pose, route);
        }

        [Test] public void SecondRunReplansFromNewGridWithNoCachedSearchOrSideState()
        {
            var grid = Grid(); var cells = new int[grid.width * grid.height];
            Box(grid, cells, .9f, -.2f, 1.1f, .2f);
            var input = new[] { Vector2.zero, new Vector2(2, 0) };
            Assert.That(MapDetourPlanner.TryPlan(grid, cells, Vector3.zero, input,
                new MapRobotFootprint(), out var first, out _), Is.True);
            var changed = (int[])cells.Clone();
            foreach (var point in first.Skip(1).Take(first.Length - 2)) Cell(grid, changed, point.x, point.y);
            Assert.That(MapDetourPlanner.TryPlan(grid, changed, Vector3.zero, input,
                new MapRobotFootprint(), out var second, out _), Is.True);
            Assert.That(second, Is.Not.EqualTo(first));
            AssertSafe(grid, changed, Vector3.zero, second);
            Assert.That(MapDetourPlanner.TryPlan(grid, cells, Vector3.zero, input,
                new MapRobotFootprint(), out var third, out _), Is.True);
            Assert.That(third, Is.EqualTo(first));
        }

        [Test] public void RequireDetourDoesNotReplayAnUnchangedRouteWhenRobotIsBlocked()
        {
            var grid = Grid(); var cells = new int[grid.width * grid.height];
            Assert.That(MapDetourPlanner.TryPlan(grid, cells, Vector3.zero, new[] { Vector2.zero, new Vector2(2, 0) },
                new MapRobotFootprint(), out _, out var error, requireDetour: true), Is.False);
            Assert.That(error, Does.Contain("different"));
        }

        [Test] public void FrontGuardIncludesSpaceBeyondGoalWhereRobotWouldStopBeforeTurning()
        {
            var grid = Grid(); var cells = new int[grid.width * grid.height];
            Cell(grid, cells, 1.3f, 0);
            var goal = new Vector2(1, 0);
            Assert.That(MapPathGeometry.TryFindFootprintBlock(grid, cells, new[] { Vector2.zero, goal }, 0,
                new MapRobotFootprint(), out _), Is.False);
            Assert.That(MapDetourPlanner.ForwardGuardClear(grid, cells, Vector2.zero, goal, new MapRobotFootprint()), Is.False);
        }

        [Test] public void BoundaryAndInvalidDataFailWithoutAPath()
        {
            var grid = Grid(); var cells = new int[grid.width * grid.height];
            Assert.That(MapDetourPlanner.TryPlan(grid, cells, Vector3.zero, new[] { new Vector2(4, 0) },
                new MapRobotFootprint(), out _, out _), Is.False);
            Assert.That(MapDetourPlanner.TryPlan(grid, cells, new Vector3(float.NaN, 0, 0), new[] { Vector2.one },
                new MapRobotFootprint(), out _, out _), Is.False);
            Assert.That(MapDetourPlanner.TryPlan(grid, new int[1], Vector3.zero, new[] { Vector2.one },
                new MapRobotFootprint(), out _, out _), Is.False);
        }

        [Test] public void RobotFootprintBlockedAtStartNeverEscapesThroughTheObstacle()
        {
            var grid = Grid(); var cells = new int[grid.width * grid.height];
            Cell(grid, cells, .1f, 0);
            Assert.That(MapDetourPlanner.TryPlan(grid, cells, Vector3.zero, new[] { new Vector2(2, 0) },
                new MapRobotFootprint(), out _, out var error), Is.False);
            Assert.That(error, Does.Contain("current robot footprint"));
        }

        [Test] public void TwentyPointProtocolBudgetIsEnforcedAcrossPreservedCorners()
        {
            var grid = Grid(); var cells = new int[grid.width * grid.height];
            // All twenty corners must remain outside the initial arrival
            // prefix; an already reached .10 m point consumes no wire slot.
            var requested = Enumerable.Range(1, 20).Select(i => new Vector2(i * .11f, 0)).ToArray();
            Assert.That(MapDetourPlanner.TryPlan(grid, cells, Vector3.zero, requested,
                new MapRobotFootprint(), out _, out var error), Is.False);
            Assert.That(error, Does.Contain("20-point"));
        }

        [Test] public void DuplicateStartDoesNotApplyAZeroYawFootprintToARotatedRobot()
        {
            var grid = Grid(); var cells = new int[grid.width * grid.height];
            Box(grid, cells, .2f, -2, 4, 4);
            Box(grid, cells, -2, -2, -.2f, 4);
            var body = new MapRobotFootprint { lengthMeters = .6f, widthMeters = .2f, marginMeters = 0 };
            Assert.That(MapDetourPlanner.TryFindRouteBlock(grid, cells,
                new[] { Vector2.zero, Vector2.zero, new Vector2(0, 2) }, (float)Math.PI / 2, body, out _), Is.False);
        }

        [Test] public void SharedGuardReportsTheActualBlockedCellAndRouteSection()
        {
            var grid = Grid(); var cells = new int[grid.width * grid.height];
            Cell(grid, cells, 1.325f, .025f);
            Assert.That(MapDetourPlanner.TryFindRouteBlock(grid, cells,
                new[] { Vector2.zero, new Vector2(1, 0) }, 0, new MapRobotFootprint(), out var block), Is.True);
            Assert.That(block.Kind, Is.EqualTo(MapPathBlockKind.Occupied));
            Assert.That(block.Position.x, Is.EqualTo(1.325f).Within(.001f));
            Assert.That(block.SegmentIndex, Is.EqualTo(0));
        }

        [Test] public void ClearanceCostChoosesWiderLowerPassageOverTheShorterUpperPassage()
        {
            var grid = Grid(); var cells = new int[grid.width * grid.height];
            // Both sides fit the physical footprint and future turning disk.
            // The upper opening is shorter but bounded by a nearby wall.
            Box(grid, cells, .85f, -.4f, 1.15f, .2f);
            Box(grid, cells, -.6f, 1.3f, 2.8f, 1.5f);
            Assert.That(MapDetourPlanner.TryPlan(grid, cells, Vector3.zero,
                new[] { Vector2.zero, new Vector2(2, 0) }, new MapRobotFootprint(), out var route, out var error), Is.True, error);
            Assert.That(route.Any(p => p.y < -.6f), Is.True, "The wider lower channel should beat the shorter confined upper channel.");
            Assert.That(route.Any(p => p.y > .3f), Is.False);
            AssertSafe(grid, cells, Vector3.zero, route);
        }

        [Test] public void MirroredNarrowWallSelectsTheMirroredWiderBypassDeterministically()
        {
            var grid = new MapGridGeometry { width = 120, height = 120, resolution = .05f,
                origin = new Vector2(-2, -3), frame = "map" };
            var upperWall = new int[grid.width * grid.height];
            var lowerWall = new int[upperWall.Length];
            Box(grid, upperWall, .85f, -.4f, 1.15f, .2f);
            Box(grid, upperWall, -.6f, 1.3f, 2.8f, 1.5f);
            Box(grid, lowerWall, .85f, -.2f, 1.15f, .4f);
            Box(grid, lowerWall, -.6f, -1.5f, 2.8f, -1.3f);
            var input = new[] { Vector2.zero, new Vector2(2, 0) };
            Assert.That(MapDetourPlanner.TryPlan(grid, upperWall, Vector3.zero, input,
                new MapRobotFootprint(), out var lower, out var lowerError), Is.True, lowerError);
            Assert.That(MapDetourPlanner.TryPlan(grid, lowerWall, Vector3.zero, input,
                new MapRobotFootprint(), out var upper, out var upperError), Is.True, upperError);
            Assert.That(lower.Any(p => p.y < -.6f), Is.True);
            Assert.That(upper.Any(p => p.y > .6f), Is.True);
            Assert.That(MapDetourPlanner.TryPlan(grid, upperWall, Vector3.zero, input,
                new MapRobotFootprint(), out var repeated, out _), Is.True);
            Assert.That(repeated, Is.EqualTo(lower));
            AssertSafe(grid, upperWall, Vector3.zero, lower);
            AssertSafe(grid, lowerWall, Vector3.zero, upper);
        }

        [Test] public void CostAwareShortcutDoesNotCutBackAcrossTheObstacleClearanceChannel()
        {
            var grid = Grid(); var cells = new int[grid.width * grid.height];
            Box(grid, cells, .85f, -.3f, 1.15f, .3f);
            Assert.That(MapDetourPlanner.TryPlan(grid, cells, Vector3.zero,
                new[] { Vector2.zero, new Vector2(2, 0) }, new MapRobotFootprint(), out var route, out var error), Is.True, error);
            float nearObstacleMinimum = float.PositiveInfinity;
            for (int leg = 1; leg < route.Length; leg++)
            for (int step = 0; step <= 100; step++)
            {
                var point = route[leg - 1] + (route[leg] - route[leg - 1]) * (step / 100f);
                if (point.x < .65f || point.x > 1.35f) continue;
                float dx = Math.Max(.85f - point.x, Math.Max(0, point.x - 1.15f));
                float dy = Math.Max(-.3f - point.y, Math.Max(0, point.y - .3f));
                nearObstacleMinimum = Math.Min(nearObstacleMinimum, (float)Math.Sqrt(dx * dx + dy * dy));
            }
            Assert.That(nearObstacleMinimum, Is.GreaterThan(.4f), "A collision-free shortcut must not hug the obstacle after cost-aware search chose a broad channel.");
            Assert.That(route.Length, Is.LessThanOrEqualTo(8), "Turn cost should prevent short oscillating zigzags.");
            AssertSafe(grid, cells, Vector3.zero, route);
        }

        [Test] public void OccupiedFinalEndpointStillFailsWithoutCommittingAnySkippedGoals()
        {
            var grid = Grid(); var cells = new int[grid.width * grid.height];
            Cell(grid, cells, 1.025f, .025f);
            var final = new Vector2(2.025f, .025f);
            Cell(grid, cells, final.x, final.y);
            Assert.That(MapDetourPlanner.TryPlanDetailed(grid, cells, Vector3.zero,
                new[] { Vector2.zero, new Vector2(1.025f, .025f), final },
                new MapRobotFootprint(), out var route, out var skipped, out var error), Is.False);
            Assert.That(route, Is.Empty);
            Assert.That(skipped, Is.Empty);
            Assert.That(error, Does.Contain("waypoint"));
        }

        [Test] public void FreeButUnreachableIntermediateGoalIsNeverSilentlySkipped()
        {
            var grid = Grid(); var cells = new int[grid.width * grid.height];
            Box(grid, cells, .8f, -2, 1.1f, 4);
            Assert.That(MapDetourPlanner.TryPlanDetailed(grid, cells, Vector3.zero,
                new[] { Vector2.zero, new Vector2(2, 0), new Vector2(0, 2) },
                new MapRobotFootprint(), out _, out var skipped, out _), Is.False);
            Assert.That(skipped, Is.Empty);
        }

        [Test] public void DetailedIndicesReferToTheOriginalListAfterDuplicateStartRemoval()
        {
            var grid = Grid(); var cells = new int[grid.width * grid.height];
            var blocked = new Vector2(1.025f, .025f);
            Cell(grid, cells, blocked.x, blocked.y);
            Assert.That(MapDetourPlanner.TryPlanDetailed(grid, cells, Vector3.zero,
                new[] { Vector2.zero, Vector2.zero, blocked, new Vector2(2, 0) },
                new MapRobotFootprint(), out _, out var skipped, out var error), Is.True, error);
            Assert.That(skipped, Is.EqualTo(new[] { 2 }));
        }

        [Test] public void ExcessiveWindingSamplesAreRejectedBeforeQuadraticPostprocessing()
        {
            var grid = Grid(); var cells = new int[grid.width * grid.height];
            var raw = new List<Vector2>();
            for (int i = 0; i < 10000; i++) raw.Add(new Vector2((i % 100) * .02f, ((i / 100) % 2) * 2));
            var searchType = typeof(MapDetourPlanner).GetNestedType("Search", BindingFlags.NonPublic);
            var search = Activator.CreateInstance(searchType, new object[] {
                grid, cells, new MapRobotFootprint(), Vector2.zero, 0f
            });
            var shortcut = searchType.GetMethod("Shortcut", BindingFlags.NonPublic | BindingFlags.Instance);
            object[] arguments = { raw, 0f, true, null };
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert.That((bool)shortcut.Invoke(search, arguments), Is.False);
            Assert.That(arguments[3], Is.Null);
            Assert.That(watch.ElapsedMilliseconds, Is.LessThan(1000), "Reject oversized raw routes before profiling/candidate scans.");
        }

        [Test] public void AdversarialAlternatingWallsReturnWithinTheBoundedPlanningBudget()
        {
            var grid = new MapGridGeometry { width = 400, height = 400, resolution = .05f,
                origin = new Vector2(-2, -2), frame = "map" };
            var cells = new int[grid.width * grid.height];
            for (int wall = 0; wall < 8; wall++)
            {
                float x = .8f + wall * 1.8f;
                if (wall % 2 == 0) Box(grid, cells, x, -2, x + .15f, 15.5f);
                else Box(grid, cells, x, .5f, x + .15f, 18);
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            bool success = MapDetourPlanner.TryPlan(grid, cells, Vector3.zero,
                new[] { Vector2.zero, new Vector2(16, 0) }, new MapRobotFootprint(), out var route, out var error);
            Assert.That(watch.ElapsedMilliseconds, Is.LessThan(1500), "A winding maze must not cause an unbounded main-thread search or shortcut scan.");
            if (success) AssertSafe(grid, cells, Vector3.zero, route);
            else { Assert.That(route, Is.Empty); Assert.That(error, Is.Not.Empty); }
        }

        [Test] public void EightyCentimeterCorridorShallowTurnMatchesDirectPreflightWithoutAnExtraHardTurningMask()
        {
            var grid = Grid(); var cells = new int[grid.width * grid.height];
            Box(grid, cells, -2, .4f, 4, 4);
            Box(grid, cells, -2, -2, 4, -.4f);
            var requested = new[] { Vector2.zero, new Vector2(.8f, 0), new Vector2(1.6f, .07f) };
            var footprint = new MapRobotFootprint();
            Assert.That(MapDetourPlanner.TryFindRouteBlock(grid, cells, requested, 0, footprint, out _), Is.False);
            Assert.That(MapDetourPlanner.TryPlan(grid, cells, Vector3.zero, requested, footprint,
                out var route, out var error), Is.True, error);
            Assert.That(route, Is.EqualTo(requested), "Soft clearance preference must not become an additional turning obstacle.");
            AssertSafe(grid, cells, Vector3.zero, route);
        }
    }
}
