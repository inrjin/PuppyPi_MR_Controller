using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace PuppyPi.Tests
{
    // Sensor-space scenarios use the real projection, confirmation, prior and
    // planner classes. They do not stand in for transport/readiness tests or
    // for physical driving. The missing sector matches the captured LD06 trace.
    public class MapNavigationScenarioTests
    {
        private const int RayCount = 450;
        private const float Resolution = .05f;
        private static readonly float RayStep = (float)(2 * Math.PI / RayCount);
        private static readonly MapRobotFootprint Body = new MapRobotFootprint();

        private sealed class Circle
        {
            public Vector2 Center;
            public float Radius;
        }

        private sealed class Frame
        {
            public MapGridGeometry Grid;
            public Vector3 Pose;
            public int[] Raw, Confirmed, Cells;
            public long Stamp;
            public double At, Now;
        }

        private sealed class Pipeline
        {
            public readonly MapObstacleFilter Filter = new MapObstacleFilter();
            public readonly MapKnownFreeLayer Prior = new MapKnownFreeLayer();
            public readonly MapObstacleReplanGate Gate = new MapObstacleReplanGate();
            private readonly Dictionary<long, double> _freeAt = new Dictionary<long, double>();
            private long _stamp;
            private double _at = 10, _now = 10;

            public Frame Next(MapGridGeometry grid, int[] map, Vector3 pose, Vector3 calibration,
                float[] ranges, bool duplicate = false)
            {
                if (duplicate) _now += .02;
                else
                {
                    _stamp++;
                    _at = Math.Max(_at + .1, _now + .001);
                    _now = _at + .03;
                }
                Assert.That(MapLaserLayer.TryBuildCurrent(grid, pose, calibration, ranges, 0,
                    RayStep, .12f, 11.5f, out int[] raw, out string error), Is.True, error);
                Assert.That(Filter.TryApply(grid, raw, _stamp, _at, _now, out int[] confirmed,
                    out error), Is.True, error);
                Assert.That(Prior.Observe(grid, confirmed, out error), Is.True, error);
                int[] output = (int[])confirmed.Clone();
                for (int i = 0; i < confirmed.Length; i++)
                {
                    long key = Key(grid, i);
                    if (confirmed[i] >= 50) _freeAt.Remove(key);
                    else if (confirmed[i] == 0)
                    {
                        if (!_freeAt.TryGetValue(key, out double previous) || previous < _at) _freeAt[key] = _at;
                    }
                    else if (_freeAt.TryGetValue(key, out double at) && _now - at < 1) output[i] = 0;
                }
                foreach (long key in _freeAt.Where(pair => _now - pair.Value >= 1).Select(pair => pair.Key).ToArray())
                    _freeAt.Remove(key);
                int[] sourceBefore = (int[])map.Clone();
                Assert.That(Prior.TryMerge(map, output, out error), Is.True, error);
                CollectionAssert.AreEqual(sourceBefore, map, "running the navigation pipeline must not edit the source map");
                for (int i = 0; i < output.Length; i++)
                {
                    if (confirmed[i] >= 50)
                        Assert.That(output[i], Is.EqualTo(100), "confirmed obstacle erased at cell " + i);
                    if (Prior.IsReusedCell(i))
                    {
                        Assert.That(map[i], Is.Zero, "only exactly-zero mapped cells may supply prior permission");
                        Assert.That(output[i], Is.Zero);
                    }
                }
                return new Frame { Grid = grid, Pose = pose, Raw = raw, Confirmed = confirmed,
                    Cells = output, Stamp = _stamp, At = _at, Now = _now };
            }
        }

        private static MapGridGeometry Grid(int offsetX = 0, int offsetY = 0) => new MapGridGeometry {
            width = 160, height = 160, resolution = Resolution,
            origin = new Vector2(-4 + offsetX * Resolution, -4 + offsetY * Resolution), frame = "map"
        };

        private static int[] Map(MapGridGeometry grid, int value = 0) =>
            Enumerable.Repeat(value, grid.width * grid.height).ToArray();

        private static Vector2 Position(Vector3 pose) => new Vector2(pose.x, pose.y);

        private static Vector2 Local(Vector3 pose, float x, float y) => Position(pose) +
            new Vector2(Mathf.Cos(pose.z) * x - Mathf.Sin(pose.z) * y,
                Mathf.Sin(pose.z) * x + Mathf.Cos(pose.z) * y);

        private static long Key(MapGridGeometry grid, int index)
        {
            // Test-only free evidence is keyed by the original world lattice,
            // independent of a viewport crop and without refreshing its age.
            double x = grid.origin.x + (index % grid.width + .5) * grid.resolution;
            double y = grid.origin.y + (index / grid.width + .5) * grid.resolution;
            int ix = (int)Math.Floor(x / grid.resolution + 1e-5);
            int iy = (int)Math.Floor(y / grid.resolution + 1e-5);
            return ((long)ix << 32) | (uint)iy;
        }

        private static Vector3 SeedPose(int seed)
        {
            var random = new System.Random(seed + 401);
            return new Vector3((float)(random.NextDouble() - .5), (float)(random.NextDouble() - .5),
                (float)(random.NextDouble() * 2 * Math.PI - Math.PI));
        }

        private static Vector3 Jitter(Vector3 pose, int seed, int frame)
        {
            var random = new System.Random(seed * 997 + frame * 17 + 29);
            return pose + new Vector3((float)(random.NextDouble() - .5) * .006f,
                (float)(random.NextDouble() - .5) * .006f, (float)(random.NextDouble() - .5) * .003f);
        }

        private static float[] Scan(Vector3 pose, Vector3 calibration, Circle obstacle,
            int seed, bool blind = true)
        {
            var random = new System.Random(seed);
            var ranges = new float[RayCount];
            Vector2 origin = Local(pose, calibration.x, calibration.y);
            for (int i = 0; i < ranges.Length; i++)
            {
                float sensorAngle = i * RayStep;
                double degrees = Math.Atan2(Math.Sin(sensorAngle), Math.Cos(sensorAngle)) * 180 / Math.PI;
                if (blind && degrees >= -12.8001 && degrees <= -2.3999 || random.Next(100) == 0)
                { ranges[i] = float.NaN; continue; }
                float angle = pose.z + calibration.z + sensorAngle;
                var direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                float range = 8;
                if (obstacle != null)
                {
                    Vector2 offset = origin - obstacle.Center;
                    float b = Vector2.Dot(offset, direction);
                    float discriminant = b * b - offset.sqrMagnitude + obstacle.Radius * obstacle.Radius;
                    if (discriminant >= 0)
                    {
                        float distance = -b - Mathf.Sqrt(discriminant);
                        if (distance > .12f) range = distance;
                    }
                }
                ranges[i] = range;
            }
            return ranges;
        }

        private static MapObstacleDecision Decision(Pipeline pipeline, Frame frame,
            IReadOnlyList<Vector2> route, bool robotBlocked = false)
        {
            bool blocked = MapDetourPlanner.TryFindRouteBlock(frame.Grid, frame.Cells, route,
                frame.Pose.z, Body, out MapPathBlock block);
            if (blocked && block.Kind != MapPathBlockKind.Occupied) return MapObstacleDecision.UnconfirmedStop;
            if (!blocked && !robotBlocked) { pipeline.Gate.Reset(); return MapObstacleDecision.Clear; }
            return pipeline.Gate.Observe(frame.Stamp, frame.At, frame.Now, blocked ? block : null, robotBlocked);
        }

        private static void AssertSafe(Frame frame, Vector2[] route, Vector2 endpoint)
        {
            Assert.That(route.Length, Is.InRange(2, 20));
            Assert.That(route[0], Is.EqualTo(Position(frame.Pose)));
            Assert.That(route[route.Length - 1], Is.EqualTo(endpoint));
            Assert.That(MapPathGeometry.TryFindFootprintBlock(frame.Grid, frame.Cells, route,
                frame.Pose.z, Body, out _), Is.False, "planned body/turn sweep must be clear");
            for (int i = 1; i < route.Length; i++)
                Assert.That(MapDetourPlanner.ForwardGuardClear(frame.Grid, frame.Cells, route[i - 1],
                    route[i], Body), Is.True, "planned leg violates the real follower's frontal guard");
        }

        private static void AssertPhysicalCircleClear(Vector2[] route, Circle obstacle)
        {
            // An independent geometric oracle checks the synthetic physical
            // obstacle, rather than asking only the planner's own grid checker.
            for (int i = 1; i < route.Length; i++)
            {
                Vector2 delta = route[i] - route[i - 1];
                float length = delta.magnitude;
                if (length <= 1e-6f) continue;
                Vector2 forward = delta / length;
                Vector2 side = new Vector2(-forward.y, forward.x);
                Vector2 relative = obstacle.Center - route[i - 1];
                float x = Vector2.Dot(relative, forward), y = Vector2.Dot(relative, side);
                float dx = Mathf.Max(0, Mathf.Max(-Body.HalfLength - x, x - length - Body.HalfLength));
                float dy = Mathf.Max(0, Mathf.Abs(y) - Body.HalfWidth);
                Assert.That(dx * dx + dy * dy, Is.GreaterThan(obstacle.Radius * obstacle.Radius),
                    "a grid-safe detour intersects the independent physical circle on leg " + i);
            }
            for (int i = 1; i < route.Length - 1; i++)
                Assert.That(Vector2.Distance(route[i], obstacle.Center),
                    Is.GreaterThan(Body.RotationRadius + obstacle.Radius), "a stopped turn sweeps the physical obstacle");
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
        [TestCase(4)] [TestCase(5)] [TestCase(6)] [TestCase(7)]
        public void BlindSectorAndOneFrameGhostsLeaveFirstAndSecondRoutesUsable(int seed)
        {
            var pipeline = new Pipeline();
            var calibration = new Vector3(.035f, .012f, .015f);
            Vector3 anchor = SeedPose(seed);
            int checkedFrames = 0;
            for (int run = 0; run < 2; run++)
            {
                if (run != 0)
                {
                    Vector2 moved = Local(anchor, .9f, 0);
                    anchor = new Vector3(moved.x, moved.y, anchor.z + .55f);
                    pipeline.Gate.Reset();
                }
                for (int i = 0; i < 24; i++)
                {
                    var grid = i >= 8 && i < 16 ? Grid(-1, 2) : Grid();
                    Vector3 pose = Jitter(anchor, seed + run * 19, i);
                    Circle ghost = i % 8 == 4 ? new Circle { Center = Local(anchor, .72f, .025f), Radius = .055f } : null;
                    Frame frame = pipeline.Next(grid, Map(grid), pose, calibration,
                        Scan(pose, calibration, ghost, seed * 500 + run * 50 + i));
                    var route = new[] { Position(pose), Local(anchor, .9f, 0) };
                    Assert.That(MapDetourPlanner.TryFindRouteBlock(grid, frame.Cells, route, pose.z,
                        Body, out var block), Is.False, "seed=" + seed + " run=" + run + " frame=" + i + " block=" + block?.Kind);
                    Assert.That(Decision(pipeline, frame, route), Is.EqualTo(MapObstacleDecision.Clear));
                    Assert.That(pipeline.Filter.ConfirmedCellCount, Is.Zero, "isolated one-frame ghosts cannot become a wall");
                    checkedFrames++;
                }
            }
            Assert.That(checkedFrames, Is.EqualTo(48));
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
        public void PersistentObstacleConfirmsAndProducesCheckedDetourWithNonzeroCalibration(int seed)
        {
            var pipeline = new Pipeline();
            var grid = Grid();
            Vector3 anchor = SeedPose(seed + 31);
            var calibration = new Vector3(.04f, -.012f, .025f);
            var obstacle = new Circle { Center = Local(anchor, .82f, .025f), Radius = .13f };
            Vector2 goal = Local(anchor, 1.65f, 0);
            Frame last = null;
            bool replan = false;
            for (int i = 0; i < 12; i++)
            {
                Vector3 pose = Jitter(anchor, seed + 77, i);
                last = pipeline.Next(grid, Map(grid), pose, calibration,
                    Scan(pose, calibration, obstacle, seed * 37 + i));
                if (Decision(pipeline, last, new[] { Position(pose), goal }) == MapObstacleDecision.Replan) replan = true;
            }
            Assert.That(pipeline.Filter.ConfirmedCellCount, Is.GreaterThan(0));
            Assert.That(replan, Is.True, "a stable visible obstacle must not wait forever for confirmation");
            Assert.That(MapDetourPlanner.TryFindRouteBlock(grid, last.Cells,
                new[] { Position(last.Pose), goal }, last.Pose.z, Body, out var straightBlock), Is.True);
            Assert.That(straightBlock.Kind, Is.EqualTo(MapPathBlockKind.Occupied));
            Assert.That(MapDetourPlanner.TryPlan(grid, last.Cells, last.Pose,
                new[] { Position(last.Pose), goal }, Body, out Vector2[] route, out string error), Is.True, error);
            Assert.That(route.Length, Is.GreaterThan(2));
            AssertSafe(last, route, goal);
            AssertPhysicalCircleClear(route, obstacle);
        }

        [Test]
        public void ASecondVisibleObstacleOnTheFirstBypassNeedsANewCheckedRoute()
        {
            var pipeline = new Pipeline();
            var grid = Grid();
            var pose = new Vector3(.13f, -.17f, .09f);
            var calibration = new Vector3(.035f, .012f, .015f);
            var firstObstacle = new Circle { Center = Local(pose, .82f, .025f), Radius = .13f };
            Vector2 goal = Local(pose, 1.65f, 0);
            Frame first = null;
            for (int i = 0; i < 12; i++) first = pipeline.Next(grid, Map(grid), pose, calibration,
                Scan(pose, calibration, firstObstacle, 120 + i));
            Assert.That(MapDetourPlanner.TryPlan(grid, first.Cells, pose, new[] { Position(pose), goal },
                Body, out Vector2[] firstRoute, out string error), Is.True, error);
            AssertSafe(first, firstRoute, goal);
            AssertPhysicalCircleClear(firstRoute, firstObstacle);
            Vector2 secondCenter = default;
            float chosenLength = -1;
            for (int i = 1; i < firstRoute.Length; i++)
            {
                Vector2 middle = (firstRoute[i - 1] + firstRoute[i]) * .5f;
                float length = Vector2.Distance(firstRoute[i - 1], firstRoute[i]);
                if (Vector2.Distance(middle, Position(pose)) > .5f && Vector2.Distance(middle, goal) > .45f && length > chosenLength)
                { secondCenter = middle; chosenLength = length; }
            }
            Assert.That(chosenLength, Is.GreaterThan(0), "fixture requires a visible bypass section away from both endpoints");
            var secondObstacle = new Circle { Center = secondCenter, Radius = .13f };
            Frame second = null;
            pipeline.Gate.Reset();
            for (int i = 0; i < 14; i++) second = pipeline.Next(grid, Map(grid), pose, calibration,
                Scan(pose, calibration, secondObstacle, 220 + i));
            Assert.That(MapDetourPlanner.TryFindRouteBlock(grid, second.Cells, firstRoute,
                pose.z, Body, out _), Is.True, "the previously planned route cannot remain valid through a new obstacle");
            Assert.That(MapDetourPlanner.TryPlan(grid, second.Cells, pose, new[] { Position(pose), goal },
                Body, out Vector2[] secondRoute, out error), Is.True, error);
            Assert.That(secondRoute, Is.Not.EqualTo(firstRoute));
            AssertSafe(second, secondRoute, goal);
            AssertPhysicalCircleClear(secondRoute, secondObstacle);
        }

        [Test]
        public void RepeatedMapReprojectionOfOneGhostCannotSupplyNewObstacleVotes()
        {
            var pipeline = new Pipeline();
            var pose = new Vector3(.17f, -.23f, .37f);
            var calibration = new Vector3(.03f, .01f, -.015f);
            var ghost = new Circle { Center = Local(pose, .7f, .02f), Radius = .06f };
            float[] ranges = Scan(pose, calibration, ghost, 91);
            var route = new[] { Position(pose), Local(pose, .9f, 0) };
            Frame frame = pipeline.Next(Grid(), Map(Grid()), pose, calibration, ranges);
            for (int i = 0; i < 10; i++)
            {
                var grid = i % 2 == 0 ? Grid(-1, 1) : Grid();
                frame = pipeline.Next(grid, Map(grid), pose, calibration, ranges, duplicate: true);
                Assert.That(pipeline.Filter.LastObservationAddedVote, Is.False);
                Assert.That(pipeline.Filter.ConfirmedCellCount, Is.Zero);
                Assert.That(Decision(pipeline, frame, route), Is.EqualTo(MapObstacleDecision.Clear));
            }
            Assert.That(frame.Stamp, Is.EqualTo(1));
            Assert.That(frame.Now - frame.At, Is.GreaterThan(.2));
        }

        [Test]
        public void UnknownCellInsideThePersistentBlindSectorRemainsBlocked()
        {
            var pipeline = new Pipeline();
            var grid = Grid();
            int[] map = Map(grid);
            var missing = new Vector2(.725f, -.075f);
            int ix = Mathf.FloorToInt((missing.x - grid.origin.x) / grid.resolution);
            int iy = Mathf.FloorToInt((missing.y - grid.origin.y) / grid.resolution);
            int index = iy * grid.width + ix;
            map[index] = -1;
            Frame frame = pipeline.Next(grid, map, Vector3.zero, Vector3.zero, Scan(Vector3.zero, Vector3.zero, null, 14));
            Assert.That(frame.Cells[index], Is.EqualTo(-1));
            Assert.That(pipeline.Prior.IsReusedCell(index), Is.False);
            var route = new[] { Vector2.zero, new Vector2(.3f, 0) };
            Assert.That(MapDetourPlanner.TryFindRouteBlock(grid, frame.Cells, route, 0, Body, out var block), Is.True);
            Assert.That(block.Kind, Is.EqualTo(MapPathBlockKind.Unknown));
            Assert.That(Decision(pipeline, frame, route), Is.EqualTo(MapObstacleDecision.UnconfirmedStop));
        }

        [Test]
        public void ConfirmedNearObstacleCannotStartABypassThroughTheRobotBody()
        {
            var pipeline = new Pipeline();
            var grid = Grid();
            var obstacle = new Circle { Center = new Vector2(.245f, .025f), Radius = .08f };
            Frame frame = null;
            for (int i = 0; i < 6; i++)
                frame = pipeline.Next(grid, Map(grid), Vector3.zero, Vector3.zero, Scan(Vector3.zero, Vector3.zero, obstacle, 330 + i));
            Assert.That(MapPathGeometry.TryFindFootprintBlock(grid, frame.Cells, new[] { Vector2.zero },
                0, Body, out var block), Is.True);
            Assert.That(block.Kind, Is.EqualTo(MapPathBlockKind.Occupied));
            Assert.That(MapDetourPlanner.TryPlan(grid, frame.Cells, Vector3.zero,
                new[] { Vector2.zero, new Vector2(1.5f, 0) }, Body, out _, out string error), Is.False);
            Assert.That(error, Does.Contain("current robot footprint"));
        }

        [Test]
        public void RejectedStaleAndBackwardsScanCannotClearTheConfirmedObstaclePriorVeto()
        {
            var pipeline = new Pipeline();
            var grid = Grid();
            var obstacle = new Circle { Center = new Vector2(.75f, .025f), Radius = .12f };
            Frame frame = null;
            for (int i = 0; i < 7; i++)
                frame = pipeline.Next(grid, Map(grid), Vector3.zero, Vector3.zero, Scan(Vector3.zero, Vector3.zero, obstacle, 81 + i));
            int occupied = Array.FindIndex(frame.Confirmed, value => value >= 50);
            Assert.That(occupied, Is.GreaterThanOrEqualTo(0));
            Assert.That(pipeline.Filter.TryApply(grid, frame.Raw, frame.Stamp + 1,
                frame.Now - 1.51, frame.Now, out _, out _), Is.False);
            Assert.That(pipeline.Filter.TryApply(grid, frame.Raw, frame.Stamp - 1,
                frame.At, frame.Now, out _, out _), Is.False);
            // Missing input is not a clear observation. A cached map refresh
            // after rejected telemetry must retain the existing veto.
            int[] missing = Map(grid, -1);
            Assert.That(pipeline.Prior.Observe(grid, missing, out string error), Is.True, error);
            Assert.That(pipeline.Prior.TryMerge(Map(grid), missing, out error), Is.True, error);
            Assert.That(missing[occupied], Is.EqualTo(-1));
        }

        [TestCase(19)] [TestCase(37)]
        public void RoughIntentionalCornerStaysUsableThroughTheKnownFreeSensorPipeline(int seed)
        {
            var random = new System.Random(seed);
            Vector3 pose = new Vector3(.23f, -.31f, .63f);
            var stroke = new List<Vector2>();
            for (int i = 0; i <= 60; i++)
            {
                float x = i <= 30 ? i * .03f : .9f;
                float y = i <= 30 ? 0 : (i - 30) * .03f;
                if (i != 0 && i != 30 && i != 60)
                { x += (float)(random.NextDouble() - .5) * .008f; y += (float)(random.NextDouble() - .5) * .008f; }
                stroke.Add(Local(pose, x, y));
            }
            Assert.That(MapPathGeometry.TryPrepare(stroke, out Vector2[] intent, out string error), Is.True, error);
            Assert.That(intent.Length, Is.InRange(3, 4));
            Assert.That(intent.Any(point => Vector2.Distance(point, Local(pose, .9f, 0)) < .06f), Is.True,
                "the deliberate corner must survive straightening");
            var pipeline = new Pipeline();
            var grid = Grid();
            Frame frame = pipeline.Next(grid, Map(grid), pose, new Vector3(.03f, -.01f, .02f),
                Scan(pose, new Vector3(.03f, -.01f, .02f), null, seed));
            Assert.That(MapDetourPlanner.TryPlan(grid, frame.Cells, pose, intent, Body,
                out Vector2[] route, out error), Is.True, error);
            AssertSafe(frame, route, stroke[stroke.Count - 1]);
            foreach (Vector2 corner in intent) Assert.That(route.Contains(corner), Is.True);
        }
    }
}
