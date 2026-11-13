using System;
using NUnit.Framework;
using UnityEngine;

namespace PuppyPi.Tests
{
    public class MapLaserLayerTests
    {
        private static MapGridGeometry Grid(int width = 40, int height = 40, float resolution = .05f) =>
            new MapGridGeometry { width = width, height = height, resolution = resolution, origin = new Vector2(-1, -1), frame = "map" };
        private static int[] Unknown(MapGridGeometry grid)
        {
            var cells = new int[grid.width * grid.height];
            for (int i = 0; i < cells.Length; i++) cells[i] = -1;
            return cells;
        }
        private static bool Apply(MapGridGeometry grid, int[] cells, float[] ranges, out int[] result, out string error,
            float increment = .01f, Vector3? pose = null, Vector3? extrinsic = null) =>
            MapLaserLayer.TryApply(grid, cells, pose ?? new Vector3(.025f, .025f, 0), extrinsic ?? Vector3.zero,
                ranges, 0, increment, .12f, 11.5f, out result, out error);

        [Test]
        public void OnlyObservedRayCellsAreFreedAndEndpointMarginRemainsUntouched()
        {
            var grid = Grid();
            int[] baseline = Unknown(grid);
            Assert.That(Apply(grid, baseline, new[] { .8f }, out var cells, out string error), Is.True, error);
            Assert.That(cells[20 * 40 + 26], Is.EqualTo(0), "observed point 0.325 m on the beam is free");
            Assert.That(cells[20 * 40 + 35], Is.EqualTo(-1), "the final 0.10 m before the endpoint stays conservative");
            Assert.That(cells[20 * 40 + 36], Is.EqualTo(100));
            Assert.That(cells[26 * 40 + 20], Is.EqualTo(-1), "unobserved perpendicular cells retain the original value");
            Assert.That(baseline[20 * 40 + 26], Is.EqualTo(-1), "base grid must not be mutated");
            Assert.That(cells, Is.Not.SameAs(baseline));
        }

        [Test]
        public void EndpointPostpassWinsOverLongerCrossingRayInEitherInputOrder()
        {
            var grid = Grid(60, 60);
            grid.origin = new Vector2(-1.5f, -1.5f);
            int[] baseline = Unknown(grid);
            foreach (var ranges in new[] { new[] { .5f, 1f }, new[] { 1f, .5f } })
            {
                Assert.That(Apply(grid, baseline, ranges, out var cells, out string error, increment: .000001f), Is.True, error);
                Assert.That(cells[30 * 60 + 40], Is.EqualTo(100), "near endpoint cannot be cleared by the longer ray");
                Assert.That(cells[30 * 60 + 50], Is.EqualTo(100));
            }
        }

        [Test]
        public void RobotYawAndTranslatedRotatedLaserCalibrationAreBothApplied()
        {
            var grid = Grid(40, 40, .1f);
            grid.origin = Vector2.zero;
            int[] baseline = Unknown(grid);
            Assert.That(Apply(grid, baseline, new[] { .6f }, out var cells, out string error,
                pose: new Vector3(1.05f, 1.05f, Mathf.PI / 2), extrinsic: new Vector3(.2f, 0, Mathf.PI / 2)), Is.True, error);
            Assert.That(cells[12 * 40 + 8], Is.EqualTo(0));
            Assert.That(cells[12 * 40 + 4], Is.EqualTo(100), "sensor (1.05,1.25), facing left, sees endpoint (0.45,1.25)");
            Assert.That(cells[12 * 40 + 5], Is.EqualTo(-1), "two-cell safety margin remains untouched");
            Assert.That(cells[10 * 40 + 16], Is.EqualTo(-1), "uncalibrated forward endpoint must not be fabricated");
        }

        [Test]
        public void InvalidNonreturningAndOutOfRangeBeamsProvideNoFreeEvidence()
        {
            var grid = Grid();
            int[] baseline = Unknown(grid);
            Assert.That(Apply(grid, baseline, new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -.1f, .1f, 12f },
                out var none, out string error), Is.False);
            Assert.That(none, Is.Null);
            Assert.That(error, Does.Contain("no valid"));
            Assert.That(Apply(grid, baseline, new[] { .6f, float.PositiveInfinity, float.NaN, -.5f },
                out var cells, out error, increment: Mathf.PI / 2), Is.True, error);
            Assert.That(cells[25 * 40 + 20], Is.EqualTo(-1));
            Assert.That(cells[20 * 40 + 15], Is.EqualTo(-1));
            Assert.That(cells[15 * 40 + 20], Is.EqualTo(-1));
        }

        [Test]
        public void ShortMeasuredRayMarksAnObstacleWithoutClearingItsSafetyMargin()
        {
            var grid = Grid(40, 40, .1f);
            grid.origin = Vector2.zero;
            var baseline = Unknown(grid);
            Assert.That(Apply(grid, baseline, new[] { .15f }, out var cells, out string error,
                pose: new Vector3(.05f, .05f, 0)), Is.True, error);
            Assert.That(cells[0], Is.EqualTo(-1), "ray shorter than two-cell margin must not clear even the sensor cell");
            Assert.That(cells[2], Is.EqualTo(100));
        }

        [Test]
        public void RaysAreClippedToGridWithoutClampingTheirEndpointsIntoTheBoundary()
        {
            var grid = Grid(10, 10, .1f);
            grid.origin = Vector2.zero;
            var baseline = Unknown(grid);
            Assert.That(Apply(grid, baseline, new[] { 3f }, out var cells, out string error,
                pose: new Vector3(.55f, .55f, 0)), Is.True, error);
            Assert.That(cells[5 * 10 + 9], Is.EqualTo(0));
            Assert.That(Array.Exists(cells, value => value == 100), Is.False, "out-of-grid endpoint must not become an edge obstacle");
            Assert.That(cells[6 * 10 + 9], Is.EqualTo(-1));
        }

        [Test]
        public void VeryLongRaysCostOnlyTheirClippedGridTraversal()
        {
            var grid = Grid(10, 10, .1f);
            grid.origin = Vector2.zero;
            var baseline = Unknown(grid);
            Assert.That(MapLaserLayer.TryApply(grid, baseline, new Vector3(.55f, .55f, 0), Vector3.zero,
                new[] { 1000000f }, 0, .1f, .12f, 2000000f, out var cells, out string error), Is.True, error);
            Assert.That(cells[5 * 10 + 9], Is.EqualTo(0));
        }

        [Test]
        public void MeasuredGhostCoordinatesClearOnlyWithAnIllustrativeObservingScan()
        {
            // The four coordinates/values came from a device diagnostic. Its
            // original scan was not retained: these full-circle 2 m ranges are
            // explicitly a synthetic regression fixture, not a device replay.
            var grid = Grid(600, 600);
            grid.origin = new Vector2(-15, -15);
            var baseline = new int[600 * 600];
            int[] indices = { 294 * 600 + 302, 293 * 600 + 302, 293 * 600 + 301, 292 * 600 + 301 };
            int[] values = { 82, 96, 82, 95 };
            for (int i = 0; i < indices.Length; i++) baseline[indices[i]] = values[i];
            var pose = new Vector3(.23125f, -.203125f, 0);
            var position = new Vector2(pose.x, pose.y);
            Assert.That(MapPathGeometry.SegmentClear(grid, baseline, position, position, .22f), Is.False);
            var ranges = new float[450];
            for (int i = 0; i < ranges.Length; i++) ranges[i] = 2;
            Assert.That(Apply(grid, baseline, ranges, out var cells, out string error,
                increment: 2 * Mathf.PI / ranges.Length, pose: pose), Is.True, error);
            foreach (int index in indices) Assert.That(cells[index], Is.EqualTo(0));
            Assert.That(MapPathGeometry.SegmentClear(grid, cells, position, position, .22f), Is.True);
            for (int i = 0; i < indices.Length; i++) Assert.That(baseline[indices[i]], Is.EqualTo(values[i]));
            Assert.That(Apply(grid, baseline, new[] { 2f }, out var onlyAhead, out error, pose: pose), Is.True, error);
            foreach (int index in indices) Assert.That(onlyAhead[index], Is.EqualTo(baseline[index]), "unobserved rear ghosts are retained");
        }

        [Test]
        public void FreshObstacleStillBlocksTheExistingRobotFootprintCollisionCheck()
        {
            var grid = Grid();
            var baseline = new int[grid.width * grid.height];
            var pose = new Vector3(.025f, .025f, 0);
            Assert.That(Apply(grid, baseline, new[] { .15f }, out var cells, out string error, pose: pose), Is.True, error);
            var position = new Vector2(pose.x, pose.y);
            Assert.That(MapPathGeometry.SegmentClear(grid, cells, position, position, .22f), Is.False);
        }

        [Test]
        public void WorkBudgetRejectsRepeatedLongNeedleGridRaysWithoutPartialOutput()
        {
            var grid = Grid(1024 * 1024, 1, .01f);
            grid.origin = Vector2.zero;
            var baseline = new int[grid.width];
            baseline[0] = 75;
            Assert.That(MapLaserLayer.TryApply(grid, baseline, new Vector3(.005f, .005f, 0), Vector3.zero,
                new[] { 12000f, 12000f, 12000f }, 0, float.Epsilon, .12f, 20000f,
                out var cells, out string error), Is.False);
            Assert.That(cells, Is.Null, "a work-budget failure must not expose a partially-cleared layer");
            Assert.That(error, Does.Contain("budget"));
            Assert.That(baseline[0], Is.EqualTo(75));
        }

        [Test]
        public void InvalidConfigurationAndUnboundedInputsFailWithoutMutatingTheBase()
        {
            var grid = Grid();
            var baseline = Unknown(grid);
            Assert.That(Apply(grid, baseline, new[] { .8f }, out _, out _, increment: 0), Is.False);
            Assert.That(Apply(grid, baseline, new[] { .8f }, out _, out _, pose: new Vector3(float.NaN, 0, 0)), Is.False);
            Assert.That(Apply(grid, baseline, new[] { .8f }, out _, out _, extrinsic: new Vector3(0, 0, float.PositiveInfinity)), Is.False);
            Assert.That(Apply(grid, baseline, new float[2049], out _, out _), Is.False);
            Assert.That(MapLaserLayer.TryApply(grid, baseline, Vector3.zero, Vector3.zero, new[] { .8f }, 0, .1f,
                .12f, .12f, out _, out _), Is.False);
            var tooLarge = Grid(1025, 1024);
            Assert.That(Apply(tooLarge, new int[1025 * 1024], new[] { .8f }, out _, out _), Is.False);
            baseline[0] = 101;
            Assert.That(Apply(grid, baseline, new[] { .8f }, out var cells, out _), Is.False);
            Assert.That(cells, Is.Null);
            Assert.That(baseline[0], Is.EqualTo(101));
        }

        private static bool Current(MapGridGeometry grid, float[] ranges, out int[] cells, out string error,
            float increment = .01f, float angleMin = 0, Vector3? pose = null, Vector3? extrinsic = null) =>
            MapLaserLayer.TryBuildCurrent(grid, pose ?? new Vector3(.025f, .025f, 0), extrinsic ?? Vector3.zero,
                ranges, angleMin, increment, .12f, 11.5f, out cells, out error);

        private static int At(MapGridGeometry grid, int[] cells, float x, float y) =>
            cells[Mathf.FloorToInt((y - grid.origin.y) / grid.resolution) * grid.width +
                Mathf.FloorToInt((x - grid.origin.x) / grid.resolution)];

        [Test] public void CurrentScanRebuildClearsAVanishedObstacleOnlyWhenNowObservedFree()
        {
            var grid = Grid();
            Assert.That(Current(grid, new[] { .5f }, out var previous, out string error), Is.True, error);
            Assert.That(At(grid, previous, .525f, .025f), Is.EqualTo(100));
            Assert.That(Current(grid, new[] { .8f }, out var next, out error), Is.True, error);
            Assert.That(At(grid, next, .525f, .025f), Is.Zero);
            Assert.That(At(grid, next, .825f, .025f), Is.EqualTo(100));
            Assert.That(At(grid, previous, .525f, .025f), Is.EqualTo(100), "building the next scan must not mutate an older frame");
        }

        [Test] public void CurrentScanNeverCarriesAnOldOffRayEndpointIntoTheNextFrame()
        {
            var grid = Grid();
            Current(grid, new[] { .5f }, out var previous, out _);
            Assert.That(Current(grid, new[] { .8f }, out var next, out string error, angleMin: Mathf.PI / 2), Is.True, error);
            Assert.That(At(grid, next, .525f, .025f), Is.EqualTo(-1), "unobserved old points become unknown rather than retained occupied or fabricated free");
            Assert.That(At(grid, next, .025f, .825f), Is.EqualTo(100));
            Assert.That(Current(grid, new[] { .8f }, out var independent, out error, angleMin: Mathf.PI / 2), Is.True, error);
            Assert.That(next, Is.EqualTo(independent), "the same current scan has no dependence on earlier runs");
        }

        [Test] public void CurrentAdjacentValidRaysFillTheirObservedInteriorButNotMarginOrBlindSide()
        {
            var grid = Grid(400, 400, .01f);
            var pose = new Vector3(.005f, .005f, 0);
            Assert.That(Current(grid, new[] { 2f, 2f }, out var cells, out string error,
                increment: 2 * Mathf.Deg2Rad, pose: pose), Is.True, error);
            Assert.That(At(grid, cells, 1.505f, .025f), Is.Zero, "the cell fully between adjacent measured rays is observed free");
            Assert.That(At(grid, cells, 1.505f, .075f), Is.EqualTo(-1), "cells outside the fan remain unknown");
            Assert.That(At(grid, cells, 1.955f, .025f), Is.EqualTo(-1), "the endpoint safety margin is not filled");
            Assert.That(At(grid, cells, 2.005f, .005f), Is.EqualTo(100));
        }

        [Test] public void CurrentInvalidRayGapAndLargeRangeDiscontinuityAreNotBridged()
        {
            var grid = Grid(400, 400, .01f);
            var pose = new Vector3(.005f, .005f, 0);
            Assert.That(Current(grid, new[] { 2f, float.NaN, 2f }, out var gap, out string error,
                increment: Mathf.Deg2Rad, pose: pose), Is.True, error);
            Assert.That(At(grid, gap, 1.505f, .035f), Is.EqualTo(-1));
            Assert.That(Current(grid, new[] { 1f, 3f }, out var edge, out error,
                increment: 2 * Mathf.Deg2Rad, pose: pose), Is.True, error);
            Assert.That(At(grid, edge, .755f, .015f), Is.EqualTo(-1), "near-object/far-wall discontinuities cannot assert free gaps");
            Assert.That(Current(grid, new[] { 2f, 2f }, out var sparse, out error,
                increment: 10 * Mathf.Deg2Rad, pose: pose), Is.True, error);
            Assert.That(At(grid, sparse, 1.505f, .105f), Is.EqualTo(-1), "large angular gaps are not interpolated");
        }

        [Test] public void CurrentFullCircleClosesOnlyTheRealLastFirstMeasurementSeam()
        {
            var grid = Grid(400, 400, .01f);
            var ranges = new float[450];
            for (int i = 0; i < ranges.Length; i++) ranges[i] = 2;
            float increment = 2 * Mathf.PI / ranges.Length;
            Assert.That(Current(grid, ranges, out var cells, out string error, increment: increment,
                angleMin: increment * .5f, pose: new Vector3(.005f, .005f, 0)), Is.True, error);
            Assert.That(At(grid, cells, 1.505f, .005f), Is.Zero, "the genuine 360-degree seam is not an unobserved stripe");
            ranges[ranges.Length - 1] = float.NaN;
            Assert.That(Current(grid, ranges, out var missing, out error, increment: increment,
                angleMin: increment * .5f, pose: new Vector3(.005f, .005f, 0)), Is.True, error);
            Assert.That(At(grid, missing, 1.505f, .005f), Is.EqualTo(-1), "an invalid seam beam is not replaced with free space");
        }

        [Test] public void CurrentFullScanAllowsObservedRobotFootprintAndShortForwardCorridor()
        {
            var grid = Grid(200, 200);
            grid.origin = new Vector2(-5, -5);
            var ranges = new float[450];
            for (int i = 0; i < ranges.Length; i++) ranges[i] = 3;
            Assert.That(Current(grid, ranges, out var cells, out string error,
                increment: 2 * Mathf.PI / ranges.Length), Is.True, error);
            var route = new[] { new Vector2(.025f, .025f), new Vector2(1.525f, .025f) };
            Assert.That(MapPathGeometry.TryFindFootprintBlock(grid, cells, route, 0,
                new MapRobotFootprint(), out var block), Is.False,
                block != null ? $"unexpected {block.Kind} at {block.Position}" : "");
            Assert.That(At(grid, cells, 4.025f, .025f), Is.EqualTo(-1), "beyond the measured endpoints remains unknown");
        }

        [Test] public void CurrentLayerAppliesPoseAndSensorCalibrationAndEndpointsStillWin()
        {
            var grid = Grid(40, 40, .1f);
            grid.origin = Vector2.zero;
            Assert.That(Current(grid, new[] { .6f }, out var cells, out string error,
                pose: new Vector3(1.05f, 1.05f, Mathf.PI / 2),
                extrinsic: new Vector3(.2f, 0, Mathf.PI / 2)), Is.True, error);
            Assert.That(cells[12 * 40 + 4], Is.EqualTo(100));
            Assert.That(cells[12 * 40 + 8], Is.Zero);
            var crossing = Grid(60, 60); crossing.origin = new Vector2(-1.5f, -1.5f);
            Assert.That(Current(crossing, new[] { .5f, 1f }, out cells, out error, increment: .000001f), Is.True, error);
            Assert.That(cells[30 * 60 + 40], Is.EqualTo(100), "endpoint postpass must survive any later free traversal");
        }

        [Test] public void CurrentLayerRejectsInvalidOrUnboundedObservationWithoutPartialOutput()
        {
            var grid = Grid();
            Assert.That(Current(grid, new[] { float.NaN, float.PositiveInfinity, .1f, 12f }, out var cells, out _), Is.False);
            Assert.That(cells, Is.Null);
            Assert.That(Current(grid, new[] { .8f }, out _, out _, increment: 0), Is.False);
            Assert.That(Current(grid, new float[2049], out _, out _), Is.False);
            Assert.That(Current(Grid(1025, 1024), new[] { .8f }, out _, out _), Is.False);
            var needle = Grid(1024 * 1024, 1, .01f); needle.origin = Vector2.zero;
            Assert.That(MapLaserLayer.TryBuildCurrent(needle, new Vector3(.005f, .005f, 0), Vector3.zero,
                new[] { 12000f, 12000f, 12000f }, 0, float.Epsilon, .12f, 20000,
                out cells, out string error), Is.False);
            Assert.That(cells, Is.Null);
            Assert.That(error, Does.Contain("budget"));
        }
    }
}
