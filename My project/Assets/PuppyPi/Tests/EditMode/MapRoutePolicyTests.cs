using System;
using NUnit.Framework;
using UnityEngine;

namespace PuppyPi.Tests
{
    public class MapRoutePolicyTests
    {
        private static MapGridGeometry Grid(int width = 40, int height = 40, float resolution = .05f) =>
            new MapGridGeometry { width = width, height = height, resolution = resolution,
                origin = new Vector2(-1, -1), frame = "map" };

        private static int CellAt(MapGridGeometry grid, Vector2 point) =>
            Mathf.FloorToInt((point.y - grid.origin.y) / grid.resolution) * grid.width +
            Mathf.FloorToInt((point.x - grid.origin.x) / grid.resolution);

        private static Vector2 Center(MapGridGeometry grid, int index) => grid.origin +
            new Vector2(index % grid.width + .5f, index / grid.width + .5f) * grid.resolution;

        [Test]
        public void UnknownSpaceAndCandidatesArePermittedWithoutErasingConfirmedObstacles()
        {
            var source = Grid(4, 2, .1f);
            var evidence = new[] { -1, 0, 11, 25, 26, 49, 50, 100 };
            var original = (int[])evidence.Clone();
            var origin = source.origin;
            Assert.That(MapRoutePolicy.TryBuild(source, evidence, new MapRobotFootprint(),
                out var projected, out var cells, out string error), Is.True, error);
            Assert.That(projected, Is.Not.SameAs(source));
            Assert.That(cells, Is.Not.SameAs(evidence));
            Assert.That(projected.frame, Is.EqualTo(source.frame));
            Assert.That(projected.resolution, Is.EqualTo(source.resolution));
            for (int i = 0; i < evidence.Length; i++)
                Assert.That(cells[CellAt(projected, Center(source, i))], Is.EqualTo(evidence[i] >= 50 ? 100 : 0),
                    "source cell " + i + " must retain its world position");
            CollectionAssert.AreEqual(original, evidence, "route permission must not rewrite measured evidence");
            Assert.That(source.origin, Is.EqualTo(origin));
            Assert.That(source.width, Is.EqualTo(4));
            Assert.That(source.height, Is.EqualTo(2));
        }

        [Test]
        public void MissingLaserLayerDoesNotCreateAPreflightKnownSpaceRequirement()
        {
            var source = Grid();
            var footprint = new MapRobotFootprint();
            Assert.That(MapRoutePolicy.TryBuild(source, null, footprint,
                out var projected, out var cells, out string error), Is.True, error);
            Assert.That(cells, Is.All.Zero);
            Assert.That(MapDetourPlanner.TryFindRouteBlock(projected, cells,
                new[] { Vector2.zero, new Vector2(.8f, 0) }, 0, footprint, out _), Is.False);
        }

        [Test]
        public void VisibleMapEdgeDoesNotActAsAWallButAConfirmedEdgeObstacleStillBlocks()
        {
            var source = Grid();
            var evidence = new int[source.width * source.height];
            var footprint = new MapRobotFootprint();
            var robot = new Vector2(.95f, .95f);
            var path = new[] { robot, new Vector2(1, 1) };
            Assert.That(MapDetourPlanner.TryFindRouteBlock(source, evidence, path, 0, footprint, out var original), Is.True);
            Assert.That(original.Kind, Is.EqualTo(MapPathBlockKind.Boundary));
            Assert.That(MapRoutePolicy.TryBuild(source, evidence, footprint,
                out var projected, out var cells, out string error), Is.True, error);
            Assert.That(MapDetourPlanner.TryFindRouteBlock(projected, cells, path, 0, footprint, out _), Is.False);
            evidence[CellAt(source, robot)] = 100;
            Assert.That(MapRoutePolicy.TryBuild(source, evidence, footprint,
                out projected, out cells, out error), Is.True, error);
            Assert.That(MapDetourPlanner.TryFindRouteBlock(projected, cells, path, 0, footprint, out var blocked), Is.True);
            Assert.That(blocked.Kind, Is.EqualTo(MapPathBlockKind.Occupied));
        }

        [Test]
        public void ConfirmedObstacleStillProducesACheckedBypassAcrossOtherwiseUnknownSpace()
        {
            var source = Grid();
            var evidence = new int[source.width * source.height];
            for (int i = 0; i < evidence.Length; i++) evidence[i] = -1;
            evidence[CellAt(source, new Vector2(.5f, 0))] = 100;
            var footprint = new MapRobotFootprint();
            var requested = new[] { Vector2.zero, new Vector2(.8f, 0) };
            Assert.That(MapDetourPlanner.TryPlan(source, evidence, Vector3.zero, requested, footprint,
                out _, out _), Is.False, "strict geometry remains unchanged");
            Assert.That(MapRoutePolicy.TryBuild(source, evidence, footprint,
                out var projected, out var cells, out string error), Is.True, error);
            Assert.That(MapDetourPlanner.TryFindRouteBlock(projected, cells, requested, 0, footprint, out var blocked), Is.True);
            Assert.That(blocked.Kind, Is.EqualTo(MapPathBlockKind.Occupied));
            Assert.That(MapDetourPlanner.TryPlan(projected, cells, Vector3.zero, requested, footprint,
                out var route, out error, requireDetour: true), Is.True, error);
            Assert.That(route.Length, Is.GreaterThan(2));
            Assert.That(route[route.Length - 1], Is.EqualTo(requested[1]));
            Assert.That(MapDetourPlanner.TryFindRouteBlock(projected, cells, route, 0, footprint, out _), Is.False);
            Assert.That(evidence[0], Is.EqualTo(-1), "permission is not newly measured free space");
        }

        [TestCase(-2)]
        [TestCase(101)]
        public void InvalidEvidenceCannotSilentlyBecomeAnEmptyPermissiveMap(int invalid)
        {
            var source = Grid(2, 2);
            var evidence = new[] { 0, 0, invalid, 100 };
            Assert.That(MapRoutePolicy.TryBuild(source, evidence, new MapRobotFootprint(),
                out var projected, out var cells, out string error), Is.False);
            Assert.That(projected, Is.Null);
            Assert.That(cells, Is.Null);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
            Assert.That(evidence[3], Is.EqualTo(100));
        }

        [Test]
        public void InvalidGeometryFootprintAndArrayLengthFailWithAnExplanation()
        {
            var source = Grid(2, 2);
            foreach (var input in new[] {
                (geometry: (MapGridGeometry)null, footprint: new MapRobotFootprint(), cells: new int[4]),
                (geometry: Grid(0, 2), footprint: new MapRobotFootprint(), cells: Array.Empty<int>()),
                (geometry: Grid(2, 2, float.NaN), footprint: new MapRobotFootprint(), cells: new int[4]),
                (geometry: source, footprint: (MapRobotFootprint)null, cells: new int[4]),
                (geometry: source, footprint: new MapRobotFootprint { marginMeters = -1 }, cells: new int[4]),
                (geometry: source, footprint: new MapRobotFootprint(), cells: new int[3]) })
            {
                Assert.That(MapRoutePolicy.TryBuild(input.geometry, input.cells, input.footprint,
                    out var projected, out var cells, out string error), Is.False);
                Assert.That(projected, Is.Null);
                Assert.That(cells, Is.Null);
                Assert.That(error, Is.Not.Null.And.Not.Empty);
            }
        }

        [Test]
        public void PaddedAllocationLimitIsCheckedEvenWhenNoScanArrayExists()
        {
            Assert.That(MapRoutePolicy.TryBuild(Grid(1024, 1024, 1), null, new MapRobotFootprint(),
                out var projected, out var cells, out string error), Is.False);
            Assert.That(projected, Is.Null);
            Assert.That(cells, Is.Null);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
        }
    }
}
