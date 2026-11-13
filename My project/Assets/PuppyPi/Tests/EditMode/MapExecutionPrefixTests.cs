using System;
using NUnit.Framework;
using UnityEngine;

namespace PuppyPi.Tests
{
    public class MapExecutionPrefixTests
    {
        private static MapGridGeometry Grid() => new MapGridGeometry {
            width = 120, height = 180, resolution = .05f,
            origin = new Vector2(-2.025f, -3.025f), frame = "map"
        };

        private static void Occupy(MapGridGeometry grid, int[] cells, Vector2 point)
        {
            int x = (int)Math.Floor((point.x - grid.origin.x) / grid.resolution);
            int y = (int)Math.Floor((point.y - grid.origin.y) / grid.resolution);
            cells[y * grid.width + x] = 100;
        }

        [TestCase(.005f)]
        [TestCase(.01f)]
        [TestCase(.03f)]
        [TestCase(.05f)]
        [TestCase(.09f)]
        public void PoseJitterDoesNotInspectRearGuardForAnAlreadyReachedStart(float jitter)
        {
            var grid = Grid();
            var cells = new int[grid.width * grid.height];
            Occupy(grid, cells, new Vector2(0, -.35f));
            var robot = new Vector2(0, jitter);
            var goal = new Vector2(0, 1);
            float yaw = (float)Math.PI / 2;
            var body = new MapRobotFootprint();
            var requested = new[] { Vector2.zero, goal };
            var raw = new[] { robot, requested[0], goal };
            Assert.That(MapPathGeometry.TryFindFootprintBlock(grid, cells, raw, yaw, body, out _), Is.False);
            Assert.That(MapDetourPlanner.TryFindRouteBlock(grid, cells, raw, yaw, body, out _), Is.True,
                "the unchanged primitive demonstrates the original phantom backward guard");
            Assert.That(MapDetourPlanner.TryPlan(grid, cells, new Vector3(robot.x, robot.y, yaw),
                requested, body, out var route, out string error), Is.True, error);
            Assert.That(route, Is.EqualTo(new[] { robot, goal }), "no invented detour or rear leg");
        }

        [Test]
        public void CapturedRobotAndDrawingCoordinatesProduceOneForwardLeg()
        {
            var grid = Grid();
            var cells = new int[grid.width * grid.height];
            Occupy(grid, cells, new Vector2(1.075f, -1.575f));
            var pose = new Vector3(.956f, -1.109f, 1.47f);
            var stored = new Vector2(.973f, -1.154f);
            var goal = new Vector2(1.459f, 3.691f);
            var robot = new Vector2(pose.x, pose.y);
            var body = new MapRobotFootprint();
            Assert.That(MapDetourPlanner.TryFindRouteBlock(grid, cells,
                new[] { robot, stored, goal }, pose.z, body, out _), Is.True);
            Assert.That(MapDetourPlanner.TryPlanDetailed(grid, cells, pose, new[] { stored, goal }, body,
                out var route, out var skipped, out string error), Is.True, error);
            Assert.That(route, Is.EqualTo(new[] { robot, goal }));
            Assert.That(skipped, Is.Empty, "arrival consumption is not an obstacle-bypassed corner");
        }

        [TestCase(.10f, 1)]
        [TestCase(.101f, 0)]
        [TestCase(.12f, 0)]
        public void ArrivalBoundaryDoesNotConsumeARealUnreachedLeg(float distance, int consumedExpected)
        {
            var robot = new Vector2(0, distance);
            var stored = Vector2.zero;
            var goal = new Vector2(0, 1);
            Vector2[] route = MapPathGeometry.NormalizeExecutionPrefix(new[] { stored, goal }, robot, out int consumed);
            Assert.That(consumed, Is.EqualTo(consumedExpected));
            Assert.That(route, Is.EqualTo(consumedExpected == 1 ? new[] { robot, goal } : new[] { robot, stored, goal }));
        }

        [TestCase(.101f)]
        [TestCase(.12f)]
        public void BeyondArrivalRadiusPlannerRetainsTheUnreachedStartingPoint(float distance)
        {
            var grid = Grid();
            var cells = new int[grid.width * grid.height];
            var pose = new Vector3(0, distance, (float)Math.PI / 2);
            var goal = new Vector2(0, 1);
            Assert.That(MapDetourPlanner.TryPlan(grid, cells, pose, new[] { Vector2.zero, goal },
                new MapRobotFootprint(), out var route, out string error), Is.True, error);
            Assert.That(route, Is.EqualTo(new[] { new Vector2(0, distance), Vector2.zero, goal }));
        }

        [Test]
        public void OnlyInitialPrefixIsConsumedAndLaterCornersAndReturnsRemainOrdered()
        {
            var robot = Vector2.zero;
            var corner = new Vector2(1, 0);
            var laterReturn = new Vector2(.02f, 0);
            var endpoint = new Vector2(1, 1);
            var requested = new[] { new Vector2(0, -.03f), new Vector2(.08f, 0), corner, laterReturn, endpoint };
            Vector2[] route = MapPathGeometry.NormalizeExecutionPrefix(requested, robot, out int consumed);
            Assert.That(consumed, Is.EqualTo(2));
            Assert.That(route, Is.EqualTo(new[] { robot, corner, laterReturn, endpoint }));
            Assert.That(MapPathGeometry.NormalizeExecutionPrefix(route, robot, out _), Is.EqualTo(route),
                "preview, planner and send normalization agree");
        }

        [Test]
        public void ReplanningConsumesOnlyReachedGoalsAfterTheIntentCursor()
        {
            var robot = new Vector2(1, 0);
            var intent = new[] { new Vector2(.5f, 0), new Vector2(1, .03f),
                new Vector2(1, 1), new Vector2(1, .04f), new Vector2(2, 1) };
            Vector2[] route = MapPathGeometry.NormalizeExecutionPrefix(intent, robot, out int consumed, 1);
            Assert.That(consumed, Is.EqualTo(1));
            Assert.That(route, Is.EqualTo(new[] { robot, intent[2], intent[3], intent[4] }),
                "a later near-robot return cannot disappear or rewind to an earlier completed goal");
        }

        [Test]
        public void ObstructedCornerIndicesStillReferToTheOriginalRequestAfterArrivalConsumption()
        {
            var grid = Grid();
            var cells = new int[grid.width * grid.height];
            Occupy(grid, cells, new Vector2(1, 0));
            var requested = new[] { Vector2.zero, new Vector2(0, -.03f), new Vector2(1, 0), new Vector2(2, 0) };
            Assert.That(MapDetourPlanner.TryPlanDetailed(grid, cells, Vector3.zero, requested,
                new MapRobotFootprint(), out var route, out var skipped, out string error), Is.True, error);
            Assert.That(skipped, Is.EqualTo(new[] { 2 }));
            Assert.That(route[route.Length - 1], Is.EqualTo(requested[3]));
        }

        [Test]
        public void ShortSearchEdgeStillChecksItsFullForwardGuard()
        {
            var grid = Grid();
            var cells = new int[grid.width * grid.height];
            Occupy(grid, cells, new Vector2(0, -.35f));
            Assert.That(MapDetourPlanner.TryFindForwardGuardBlock(grid, cells, Vector2.zero,
                new Vector2(0, -.05f), new MapRobotFootprint(), out var block), Is.True);
            Assert.That(block.Kind, Is.EqualTo(MapPathBlockKind.Occupied));
        }
    }
}
