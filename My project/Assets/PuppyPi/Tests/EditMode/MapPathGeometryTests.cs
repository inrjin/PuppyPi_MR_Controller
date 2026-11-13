using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace PuppyPi.Tests
{
    public class MapPathGeometryTests
    {
        private static MapGridGeometry Grid(int width = 10, int height = 10, float resolution = 1) =>
            new MapGridGeometry { width = width, height = height, resolution = resolution, origin = Vector2.zero, frame = "map" };

        private static MapGridGeometry FootprintGrid() =>
            new MapGridGeometry { width = 400, height = 400, resolution = .01f, origin = new Vector2(-2, -2), frame = "map" };

        private static int CellAt(MapGridGeometry grid, Vector2 point) =>
            Mathf.FloorToInt((point.y - grid.origin.y) / grid.resolution) * grid.width +
            Mathf.FloorToInt((point.x - grid.origin.x) / grid.resolution);

        [Test] public void PuppyPiFootprintUsesPublishedBodyAndExplicitConfigurableWalkingAllowance()
        {
            var footprint = new MapRobotFootprint();
            Assert.That(footprint.IsValid, Is.True);
            Assert.That(footprint.LengthMeters, Is.EqualTo(.226f));
            Assert.That(footprint.WidthMeters, Is.EqualTo(.149f));
            Assert.That(footprint.MarginMeters, Is.EqualTo(.05f));
            Assert.That(footprint.HalfLength, Is.EqualTo(.163f).Within(.000001));
            Assert.That(footprint.HalfWidth, Is.EqualTo(.1245f).Within(.000001));
            Assert.That(footprint.RotationRadius, Is.EqualTo(Math.Sqrt(.163 * .163 + .1245 * .1245)).Within(.000001));
            footprint.marginMeters = .1f;
            Assert.That(footprint.HalfWidth, Is.EqualTo(.1745f).Within(.000001));
            footprint.marginMeters = -1;
            Assert.That(footprint.IsValid, Is.False);
        }

        [Test] public void StraightFootprintPassesSideSpaceThatTheOldFortyFourCentimeterCapsuleBlocked()
        {
            var grid = FootprintGrid();
            var cells = new int[grid.width * grid.height];
            var path = new[] { Vector2.zero, Vector2.right };
            cells[CellAt(grid, new Vector2(.501f, .181f))] = 100;
            Assert.That(MapPathGeometry.TryFindBlock(grid, cells, path, .22f, out _), Is.True);
            Assert.That(MapPathGeometry.TryFindFootprintBlock(grid, cells, path, 0, new MapRobotFootprint(), out _), Is.False);
            cells[CellAt(grid, new Vector2(.501f, .121f))] = 100;
            Assert.That(MapPathGeometry.TryFindFootprintBlock(grid, cells, path, 0, new MapRobotFootprint(), out var block), Is.True);
            Assert.That(block.Kind, Is.EqualTo(MapPathBlockKind.Occupied));
        }

        [Test] public void SweptFootprintRotatesWithVerticalAndDiagonalLegs()
        {
            var grid = FootprintGrid();
            var footprint = new MapRobotFootprint();
            foreach (float angle in new[] { Mathf.PI / 2, Mathf.PI / 4, -Mathf.PI / 3 })
            {
                var cells = new int[grid.width * grid.height];
                Vector2 forward = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                Vector2 side = new Vector2(-forward.y, forward.x);
                var path = new[] { Vector2.zero, forward };
                cells[CellAt(grid, forward * .5f + side * .18f)] = 100;
                Assert.That(MapPathGeometry.TryFindFootprintBlock(grid, cells, path, angle, footprint, out _), Is.False, "angle=" + angle);
                cells[CellAt(grid, forward * .5f + side * .10f)] = 100;
                Assert.That(MapPathGeometry.TryFindFootprintBlock(grid, cells, path, angle, footprint, out _), Is.True, "angle=" + angle);
            }
        }

        [Test] public void SharpCornerChecksTurningDiskButFinalEndpointDoesNotAddAnUnrequestedTurn()
        {
            var grid = FootprintGrid();
            var cells = new int[grid.width * grid.height];
            cells[CellAt(grid, new Vector2(1.151f, -.139f))] = 100;
            var footprint = new MapRobotFootprint();
            Assert.That(MapPathGeometry.TryFindFootprintBlock(grid, cells,
                new[] { Vector2.zero, Vector2.right }, 0, footprint, out _), Is.False, "straight arrival does not rotate at the end");
            Assert.That(MapPathGeometry.TryFindFootprintBlock(grid, cells,
                new[] { Vector2.zero, Vector2.right, Vector2.one }, 0, footprint, out var block), Is.True);
            Assert.That(block.SegmentIndex, Is.EqualTo(1));
            Assert.That(block.Kind, Is.EqualTo(MapPathBlockKind.Occupied));
        }

        [Test] public void InitialFootprintUsesCurrentYawAndFirstHeadingChangeNeedsTurnClearance()
        {
            var grid = FootprintGrid();
            var cells = new int[grid.width * grid.height];
            cells[CellAt(grid, new Vector2(.151f, .001f))] = 100;
            var footprint = new MapRobotFootprint();
            Assert.That(MapPathGeometry.TryFindFootprintBlock(grid, cells,
                new[] { Vector2.zero }, Mathf.PI / 2, footprint, out _), Is.False);
            Assert.That(MapPathGeometry.TryFindFootprintBlock(grid, cells,
                new[] { Vector2.zero }, 0, footprint, out _), Is.True, "front body extension is checked while stationary");
            Assert.That(MapPathGeometry.TryFindFootprintBlock(grid, cells,
                new[] { Vector2.zero, Vector2.right }, Mathf.PI / 2, footprint, out var block), Is.True);
            Assert.That(block.SegmentIndex, Is.Zero);
        }

        [Test] public void CollinearDuplicatePointsDoNotCreateTurnsButReversalsDo()
        {
            var grid = FootprintGrid();
            var cells = new int[grid.width * grid.height];
            cells[CellAt(grid, new Vector2(1.151f, -.139f))] = 100;
            var footprint = new MapRobotFootprint();
            Assert.That(MapPathGeometry.TryFindFootprintBlock(grid, cells,
                new[] { Vector2.zero, Vector2.zero, Vector2.right, Vector2.right }, 0, footprint, out _), Is.False);
            Assert.That(MapPathGeometry.TryFindFootprintBlock(grid, cells,
                new[] { Vector2.zero, Vector2.right, Vector2.zero }, 0, footprint, out var block), Is.True);
            Assert.That(block.SegmentIndex, Is.EqualTo(1));
        }

        [Test] public void SweptRectangleChecksCellEdgesEvenWhenTheirCentersAreOutsideTheBody()
        {
            var grid = Grid(50, 50, .1f);
            grid.origin = new Vector2(-2, -2);
            var cells = new int[2500];
            var footprint = new MapRobotFootprint { lengthMeters = .2f, widthMeters = .2f, marginMeters = .05f };
            // Cell y=[.1,.2] touches the body's top .15m although its center
            // is outside once the path center is offset by -.01m.
            cells[CellAt(grid, new Vector2(.55f, .15f))] = 100;
            Assert.That(MapPathGeometry.TryFindFootprintBlock(grid, cells,
                new[] { new Vector2(0, -.01f), new Vector2(1, -.01f) }, 0, footprint, out _), Is.True);
        }

        [Test] public void FootprintUnknownThresholdBoundaryAndInvalidDataUseExistingFailClosedPolicy()
        {
            var grid = FootprintGrid();
            var cells = new int[grid.width * grid.height];
            var path = new[] { Vector2.zero, Vector2.right };
            var footprint = new MapRobotFootprint();
            int index = CellAt(grid, new Vector2(.5f, .01f));
            foreach (int value in new[] { -1, 50, 100 })
            {
                cells[index] = value;
                Assert.That(MapPathGeometry.TryFindFootprintBlock(grid, cells, path, 0, footprint, out var block), Is.True);
                Assert.That(block.Kind, Is.EqualTo(value < 0 ? MapPathBlockKind.Unknown : MapPathBlockKind.Occupied));
                Assert.That(block.CellValue, Is.EqualTo(value));
            }
            cells[index] = 49;
            Assert.That(MapPathGeometry.TryFindFootprintBlock(grid, cells, path, 0, footprint, out _), Is.False);
            Assert.That(MapPathGeometry.TryFindFootprintBlock(grid, cells,
                new[] { new Vector2(-1.9f, 0), Vector2.zero }, 0, footprint, out var boundary), Is.True);
            Assert.That(boundary.Kind, Is.EqualTo(MapPathBlockKind.Boundary));
            Assert.That(MapPathGeometry.TryFindFootprintBlock(grid, cells, path, float.NaN, footprint, out var invalid), Is.True);
            Assert.That(invalid.Kind, Is.EqualTo(MapPathBlockKind.Invalid));
            footprint.widthMeters = 0;
            Assert.That(MapPathGeometry.TryFindFootprintBlock(grid, cells, path, 0, footprint, out invalid), Is.True);
            Assert.That(invalid.Kind, Is.EqualTo(MapPathBlockKind.Invalid));
        }

        [Test]
        public void RectangularGridUvRoundtripUsesBottomLeftAndActualResolution()
        {
            var grid = Grid(100, 40, 0.05f);
            grid.origin = new Vector2(-2, -3);
            Assert.That(grid.TryUvToMeters(new Vector2(.25f, .75f), out Vector2 meters), Is.True);
            Assert.That(meters.x, Is.EqualTo(-.75f).Within(.00001));
            Assert.That(meters.y, Is.EqualTo(-1.5f).Within(.00001));
            Vector2 uv = grid.MetersToUv(meters);
            Assert.That(uv.x, Is.EqualTo(.25f).Within(.00001));
            Assert.That(uv.y, Is.EqualTo(.75f).Within(.00001));
            Assert.That(grid.TryUvToMeters(Vector2.zero, out var bottom), Is.True);
            Assert.That(bottom, Is.EqualTo(grid.origin));
            Assert.That(grid.TryUvToMeters(Vector2.one, out var top), Is.True);
            Assert.That(top.x, Is.EqualTo(3).Within(.00001));
            Assert.That(top.y, Is.EqualTo(-1).Within(.00001));
            Assert.That(grid.MetersToUv(new Vector2(4, -3)).x, Is.GreaterThan(1), "out-of-map points are never clamped");
        }

        [Test]
        public void InvalidMapsAndNonfiniteOrOutsideUvFailClosed()
        {
            var grid = Grid();
            Assert.That(grid.TryUvToMeters(new Vector2(1.01f, .5f), out _), Is.False);
            Assert.That(grid.TryUvToMeters(new Vector2(float.NaN, .5f), out _), Is.False);
            grid.width = int.MaxValue;
            Assert.That(grid.IsValid, Is.False);
            grid = Grid();
            grid.resolution = 0;
            Assert.That(grid.IsValid, Is.False);
            Assert.That(float.IsNaN(grid.MetersToUv(Vector2.zero).x), Is.True);
            grid.resolution = float.PositiveInfinity;
            Assert.That(grid.IsValid, Is.False);
            grid = Grid();
            grid.origin = new Vector2(float.NaN, 0);
            Assert.That(grid.IsValid, Is.False);
            grid = Grid();
            grid.frame = " ";
            Assert.That(grid.IsValid, Is.False);
        }

        [Test]
        public void SmallHandJitterSimplifiesWithoutMovingEitherEndpoint()
        {
            var raw = new List<Vector2>();
            for (int i = 0; i <= 160; i++) raw.Add(new Vector2(i / 80f, .004f * Mathf.Sin(i * 17)));
            Assert.That(MapPathGeometry.TryPrepare(raw, out var path, out string error), Is.True, error);
            Assert.That(path.Length, Is.EqualTo(2));
            Assert.That(path[0], Is.EqualTo(raw[0]));
            Assert.That(path[path.Length - 1], Is.EqualTo(raw[raw.Count - 1]));
            AssertWithinTolerance(raw, path, .04f);
        }

        [Test]
        public void DenseRightAngleStrokePreservesTheCorner()
        {
            var raw = new List<Vector2>();
            for (int i = 0; i <= 100; i++) raw.Add(new Vector2(i / 100f, 0));
            for (int i = 1; i <= 100; i++) raw.Add(new Vector2(1, i / 100f));
            Assert.That(MapPathGeometry.TryPrepare(raw, out var path, out string error), Is.True, error);
            Assert.That(Array.Exists(path, p => p.x == 1 && p.y == 0), Is.True);
            Assert.That(path.Length, Is.LessThanOrEqualTo(5));
            AssertWithinTolerance(raw, path, .04f);
        }

        [Test]
        public void CollinearBacktrackingIsNotCollapsedIntoOneForwardSegment()
        {
            var raw = new[] { Vector2.zero, new Vector2(.5f, 0), new Vector2(1, 0),
                new Vector2(.75f, 0), new Vector2(.5f, 0), new Vector2(1.5f, 0) };
            Assert.That(MapPathGeometry.TryPrepare(raw, out var path, out string error), Is.True, error);
            int peak = Array.FindIndex(path, p => p.x == 1);
            Assert.That(peak, Is.GreaterThan(0));
            bool returnsAfterPeak = false;
            for (int i = peak + 1; i < path.Length; i++)
                if (path[i].x < .8f) returnsAfterPeak = true;
            Assert.That(returnsAfterPeak, Is.True, "a later visit to the same coordinate is still a distinct waypoint");
            Assert.That(path[path.Length - 1], Is.EqualTo(raw[raw.Length - 1]));
        }

        [Test]
        public void ClosedCircleAndNearClosedEndpointRemainOrderedLoops()
        {
            var raw = new List<Vector2>();
            for (int i = 0; i < 96; i++)
            {
                float angle = i * Mathf.PI * 2 / 96;
                raw.Add(new Vector2(.4f * Mathf.Cos(angle), .4f * Mathf.Sin(angle)));
            }
            raw.Add(raw[0]);
            Assert.That(MapPathGeometry.TryPrepare(raw, out var path, out string error), Is.True, error);
            Assert.That(path.Length, Is.InRange(4, 20));
            Assert.That(path[0], Is.EqualTo(path[path.Length - 1]));
            Assert.That(SignedArea(path), Is.GreaterThan(.25), "the loop may become a simple polygon but must retain its winding and area");
            raw[raw.Count - 1] += new Vector2(.005f, -.003f);
            Assert.That(MapPathGeometry.TryPrepare(raw, out path, out error), Is.True, error);
            Assert.That(path[path.Length - 1], Is.EqualTo(raw[raw.Count - 1]));
            Assert.That(SignedArea(path), Is.GreaterThan(.25));
        }

        [Test]
        public void SmallClosedStrokeRetainsNoncollinearSupportsEvenWithinTolerance()
        {
            var raw = new[] { Vector2.zero, new Vector2(.01f, 0), new Vector2(.01f, .01f), Vector2.zero };
            Assert.That(MapPathGeometry.TryPrepare(raw, out var path, out string error), Is.True, error);
            Assert.That(path.Length, Is.EqualTo(4));
            Assert.That(SignedArea(path), Is.GreaterThan(0));
        }

        [Test]
        public void TwoLoopsPreserveTheIntermediateReturnToTheirSharedStart()
        {
            var raw = new[] { Vector2.zero, new Vector2(-1, .5f), new Vector2(-1, 1),
                new Vector2(-.5f, 1), Vector2.zero, new Vector2(1, -.5f),
                new Vector2(1, -1), new Vector2(.5f, -1), Vector2.zero };
            Assert.That(MapPathGeometry.TryPrepare(raw, out var path, out string error), Is.True, error);
            Assert.That(Array.FindAll(path, p => p.x == 0 && p.y == 0).Length, Is.EqualTo(3));
            AssertWithinTolerance(raw, path, .04f);
        }

        [Test]
        public void OverBudgetStrokeReturnsAnErrorAndNeverTruncatesOrRaisesTolerance()
        {
            var raw = new List<Vector2>();
            for (int i = 0; i < 30; i++) raw.Add(new Vector2(i * .2f, i % 2 == 0 ? 0 : .3f));
            Assert.That(MapPathGeometry.TryPrepare(raw, out var path, out string error), Is.False);
            Assert.That(path, Is.Empty);
            Assert.That(error, Does.Contain("limit is 20"));
            Assert.That(raw.Count, Is.EqualTo(30));
            Assert.That(MapPathGeometry.TryPrepare(new[] { Vector2.zero, Vector2.right, Vector2.one },
                out path, out error, 2, .04f), Is.False);
            Assert.That(path, Is.Empty);
        }

        [Test]
        public void DuplicateOnlyInvalidAndOverlongStrokesAreRejected()
        {
            Assert.That(MapPathGeometry.TryPrepare(new[] { Vector2.one, Vector2.one }, out _, out _), Is.False);
            Assert.That(MapPathGeometry.TryPrepare(new[] { Vector2.zero, new Vector2(float.NaN, 0) }, out _, out _), Is.False);
            Assert.That(MapPathGeometry.TryPrepare(new[] { Vector2.zero, Vector2.one }, out _, out _, toleranceMeters: -1), Is.False);
            Assert.That(MapPathGeometry.TryPrepare(new[] { Vector2.zero, Vector2.one }, out _, out _, toleranceMeters: float.NaN), Is.False);
            Assert.That(MapPathGeometry.TryPrepare(new Vector2[8193], out _, out _), Is.False);
        }

        [Test]
        public void SegmentChecksEveryCrossedCellInsteadOfOnlyWaypoints()
        {
            var grid = Grid();
            var cells = new int[100];
            cells[5 * 10 + 5] = 100;
            Assert.That(MapPathGeometry.SegmentClear(grid, cells, new Vector2(1.5f, 1.5f), new Vector2(8.5f, 8.5f)), Is.False);
            Assert.That(MapPathGeometry.SegmentClear(grid, cells, new Vector2(1.5f, 2.5f), new Vector2(8.5f, 2.5f)), Is.True);
        }

        [Test]
        public void UnknownAndThresholdCellsBlockButLowerOccupancyValuesDoNot()
        {
            var grid = Grid();
            var cells = new int[100];
            Vector2 a = new Vector2(1.5f, 4.5f), b = new Vector2(8.5f, 4.5f);
            foreach (int value in new[] { -1, 50, 100 })
            {
                cells[4 * 10 + 5] = value;
                Assert.That(MapPathGeometry.SegmentClear(grid, cells, a, b), Is.False, "occupancy=" + value);
            }
            cells[4 * 10 + 5] = 49;
            Assert.That(MapPathGeometry.SegmentClear(grid, cells, a, b), Is.True);
        }

        [Test]
        public void FootprintClearanceUsesExactCapsuleDistanceAndBlocksTouching()
        {
            var grid = Grid();
            var cells = new int[100];
            cells[5 * 10 + 5] = 100;
            Vector2 a = new Vector2(1.5f, 4.75f), b = new Vector2(8.5f, 4.75f);
            Assert.That(MapPathGeometry.SegmentClear(grid, cells, a, b, .24f), Is.True);
            Assert.That(MapPathGeometry.SegmentClear(grid, cells, a, b, .25f), Is.False);
            cells[5 * 10 + 5] = 0;
            cells[8 * 10 + 1] = 100;
            Assert.That(MapPathGeometry.SegmentClear(grid, cells, new Vector2(1.5f, 1.5f), new Vector2(8.5f, 8.5f), .2f), Is.True,
                "a cell in the bounding box but far from the segment must not be a false collision");
        }

        [Test]
        public void NarrowPassageMustFitTheFootprintRadius()
        {
            var grid = Grid();
            var cells = new int[100];
            for (int x = 1; x < 9; x++) { cells[3 * 10 + x] = 100; cells[5 * 10 + x] = 100; }
            Vector2 a = new Vector2(1.5f, 4.5f), b = new Vector2(8.5f, 4.5f);
            Assert.That(MapPathGeometry.SegmentClear(grid, cells, a, b, .49f), Is.True);
            Assert.That(MapPathGeometry.SegmentClear(grid, cells, a, b, .51f), Is.False);
        }

        [Test]
        public void MapBoundsDataMismatchAndInvalidRadiusFailClosed()
        {
            var grid = Grid();
            var cells = new int[100];
            Assert.That(MapPathGeometry.SegmentClear(grid, cells, new Vector2(-.01f, 1), Vector2.one), Is.False);
            Assert.That(MapPathGeometry.SegmentClear(grid, cells, Vector2.one, new Vector2(10, 1)), Is.False);
            Assert.That(MapPathGeometry.SegmentClear(grid, cells, new Vector2(.2f, 1), Vector2.one, .3f), Is.False);
            Assert.That(MapPathGeometry.SegmentClear(grid, cells, Vector2.one, new Vector2(9.8f, 1), .3f), Is.False);
            Assert.That(MapPathGeometry.SegmentClear(grid, new int[99], Vector2.one, Vector2.one), Is.False);
            Assert.That(MapPathGeometry.SegmentClear(grid, cells, Vector2.one, Vector2.one, -.1f), Is.False);
            Assert.That(MapPathGeometry.PathClear(grid, cells, Array.Empty<Vector2>()), Is.False);
        }

        [Test]
        public void CollisionProjectionRespectsNegativeOriginAndSmallCellResolution()
        {
            var grid = Grid(80, 40, .05f);
            grid.origin = new Vector2(-2, -1);
            var cells = new int[80 * 40];
            cells[20 * 80 + 40] = 100;
            Assert.That(MapPathGeometry.SegmentClear(grid, cells, new Vector2(-.5f, .025f), new Vector2(.5f, .025f)), Is.False);
            Assert.That(MapPathGeometry.SegmentClear(grid, cells, new Vector2(-.5f, -.1f), new Vector2(.5f, -.1f), .02f), Is.True);
        }

        [Test]
        public void SimplificationShortcutIsCheckedAgainAgainstOccupancy()
        {
            var grid = Grid();
            var cells = new int[100];
            cells[4 * 10 + 4] = 100;
            var raw = new[] { new Vector2(2.5f, 4.1f), new Vector2(4.5f, 3.8f), new Vector2(6.5f, 4.1f) };
            Assert.That(MapPathGeometry.PathClear(grid, cells, raw), Is.True);
            Assert.That(MapPathGeometry.TryPrepare(raw, out var prepared, out string error, toleranceMeters: 2), Is.True, error);
            Assert.That(MapPathGeometry.PathClear(grid, cells, prepared), Is.False,
                "simplification never replaces the final collision validation");
        }

        [TestCase(-1, MapPathBlockKind.Unknown)]
        [TestCase(50, MapPathBlockKind.Occupied)]
        [TestCase(100, MapPathBlockKind.Occupied)]
        public void BlockDiagnosticNamesTheFirstFailedSegmentAndSourceCell(int value, MapPathBlockKind expected)
        {
            var grid = Grid();
            grid.origin = new Vector2(-3, -2);
            var cells = new int[100];
            cells[6 * 10 + 7] = value;
            var path = new[] { new Vector2(-1.5f, .5f), new Vector2(4.5f, .5f), new Vector2(4.5f, 6.5f) };
            Assert.That(MapPathGeometry.TryFindBlock(grid, cells, path, 0, out MapPathBlock block), Is.True);
            Assert.That(block.Kind, Is.EqualTo(expected));
            Assert.That(block.SegmentIndex, Is.EqualTo(1));
            Assert.That(block.Position, Is.EqualTo(new Vector2(4.5f, 4.5f)));
            Assert.That(block.CellValue, Is.EqualTo(value));
            Assert.That(MapPathGeometry.PathClear(grid, cells, path), Is.False);
            Assert.That(MapPathGeometry.SegmentClear(grid, cells, path[0], path[1]), Is.True);
            cells[6 * 10 + 7] = 49;
            Assert.That(MapPathGeometry.TryFindBlock(grid, cells, path, 0, out block), Is.False);
            Assert.That(block, Is.Null);
        }

        [Test]
        public void RobotFootprintDiagnosticFindsNearbyCellsEvenWhenTheCenterAndDrawnLineAreFree()
        {
            var grid = Grid(100, 100, .05f);
            grid.origin = new Vector2(-2.5f, -2.5f);
            var cells = new int[10000];
            cells[50 * 100 + 53] = 90;
            var disk = new[] { Vector2.zero, Vector2.zero };
            Assert.That(MapPathGeometry.SegmentClear(grid, cells, Vector2.zero, Vector2.zero), Is.True);
            Assert.That(MapPathGeometry.TryFindBlock(grid, cells, disk, .22f, out MapPathBlock block), Is.True);
            Assert.That(block.Kind, Is.EqualTo(MapPathBlockKind.Occupied));
            Assert.That(block.SegmentIndex, Is.Zero);
            Assert.That(block.Position.x, Is.EqualTo(.175f).Within(.00001));
            Assert.That(block.Position.y, Is.EqualTo(.025f).Within(.00001));
            Assert.That(block.CellValue, Is.EqualTo(90));
            cells[50 * 100 + 53] = -1;
            Assert.That(MapPathGeometry.TryFindBlock(grid, cells, disk, .22f, out block), Is.True);
            Assert.That(block.Kind, Is.EqualTo(MapPathBlockKind.Unknown));
            cells[50 * 100 + 53] = 0;
            Assert.That(MapPathGeometry.TryFindBlock(grid, cells, disk, .22f, out block), Is.False);
            Assert.That(block, Is.Null);
        }

        [Test]
        public void InvalidAndBoundaryDiagnosticsRemainFailClosedWithoutInventingAnObstacleCell()
        {
            var grid = Grid();
            var cells = new int[100];
            var path = new[] { new Vector2(.1f, 2), new Vector2(2, 2) };
            Assert.That(MapPathGeometry.TryFindBlock(grid, cells, path, .2f, out MapPathBlock block), Is.True);
            Assert.That(block.Kind, Is.EqualTo(MapPathBlockKind.Boundary));
            Assert.That(block.Position, Is.EqualTo(path[0]));
            Assert.That(MapPathGeometry.TryFindBlock(grid, new int[99], path, 0, out block), Is.True);
            Assert.That(block.Kind, Is.EqualTo(MapPathBlockKind.Invalid));
            Assert.That(MapPathGeometry.TryFindBlock(grid, cells, path, float.NaN, out block), Is.True);
            Assert.That(block.Kind, Is.EqualTo(MapPathBlockKind.Invalid));
            Assert.That(MapPathGeometry.TryFindBlock(grid, cells, Array.Empty<Vector2>(), 0, out block), Is.True);
            Assert.That(block.Kind, Is.EqualTo(MapPathBlockKind.Invalid));
            Assert.That(block.SegmentIndex, Is.EqualTo(-1));
        }

        [Test]
        public void WavyStraightStrokesBecomeExactlyOneStraightSegment()
        {
            foreach (float amplitude in new[] { .01f, .04f, .08f, .15f })
            foreach (bool highFrequency in new[] { false, true })
            foreach (int density in new[] { 50, 150, 500 })
            {
                var raw = WavyPolyline("line", amplitude, highFrequency, density);
                var original = raw.ToArray();
                Assert.That(MapPathGeometry.TryPrepare(raw, out var path, out string error, toleranceMeters: .08f), Is.True, error);
                Assert.That(path.Length, Is.EqualTo(2), "amplitude=" + amplitude + " density=" + density);
                Assert.That(path[0], Is.EqualTo(raw[0]));
                Assert.That(path[1], Is.EqualTo(raw[raw.Count - 1]));
                Assert.That(raw, Is.EqualTo(original));
            }
        }

        [Test]
        public void WavyLAndZStrokesBecomeOnlyTheirIntendedStraightLegs()
        {
            foreach (string shape in new[] { "L", "Z" })
            foreach (float amplitude in new[] { .01f, .04f, .08f, .15f })
            foreach (bool highFrequency in new[] { false, true })
            foreach (int density in new[] { 50, 150, 500 })
            {
                var raw = WavyPolyline(shape, amplitude, highFrequency, density);
                var original = raw.ToArray();
                Assert.That(MapPathGeometry.TryPrepare(raw, out var path, out string error, toleranceMeters: .08f), Is.True, error);
                Assert.That(path.Length, Is.EqualTo(shape == "L" ? 3 : 4),
                    shape + " amplitude=" + amplitude + " density=" + density + " high=" + highFrequency);
                Assert.That(path[0], Is.EqualTo(raw[0]));
                Assert.That(path[path.Length - 1], Is.EqualTo(raw[raw.Count - 1]));
                Assert.That(Vector2.Distance(path[1], new Vector2(1.5f, 0)), Is.LessThan(.35f));
                if (shape == "Z") Assert.That(Vector2.Distance(path[2], new Vector2(.5f, 1)), Is.LessThan(.35f));
                Assert.That(raw, Is.EqualTo(original));
            }
        }

        [Test]
        public void DiagonalTurnsAndRotatedCornersAreNotForcedToCoordinateAxes()
        {
            var diagonal = WavyPolyline("45", .04f, true, 150);
            Assert.That(MapPathGeometry.TryPrepare(diagonal, out var path, out string error, toleranceMeters: .08f), Is.True, error);
            Assert.That(path.Length, Is.EqualTo(3));
            Assert.That(Vector2.Distance(path[1], new Vector2(1.5f, 0)), Is.LessThan(.03f));
            Vector2 outgoing = (path[2] - path[1]).normalized;
            Assert.That(outgoing.x, Is.EqualTo(outgoing.y).Within(.025));
            var rotated = WavyPolyline("L", .02f, true, 150);
            float angle = 27 * Mathf.Deg2Rad;
            for (int i = 0; i < rotated.Count; i++)
            {
                Vector2 point = rotated[i];
                rotated[i] = new Vector2(Mathf.Cos(angle) * point.x - Mathf.Sin(angle) * point.y - 2,
                    Mathf.Sin(angle) * point.x + Mathf.Cos(angle) * point.y + 1);
            }
            Assert.That(MapPathGeometry.TryPrepare(rotated, out path, out error, toleranceMeters: .08f), Is.True, error);
            Assert.That(path.Length, Is.EqualTo(3));
            Vector2 incoming = (path[1] - path[0]).normalized;
            Assert.That(Vector2.Dot(incoming, (path[2] - path[1]).normalized), Is.EqualTo(0).Within(.035));
            Assert.That(Math.Abs(incoming.y), Is.GreaterThan(.3), "the fitted first run must retain its 27 degree orientation");
        }

        [Test]
        public void StraightLegFitIsStableAcrossSamplingDensities()
        {
            foreach (string shape in new[] { "L", "Z", "45" })
            foreach (bool highFrequency in new[] { false, true })
            {
                Vector2[] reference = null;
                foreach (int density in new[] { 50, 150, 500 })
                {
                    var raw = WavyPolyline(shape, .04f, highFrequency, density);
                    Assert.That(MapPathGeometry.TryPrepare(raw, out var path, out string error, toleranceMeters: .08f), Is.True, error);
                    if (reference == null) reference = path;
                    Assert.That(path.Length, Is.EqualTo(reference.Length));
                    for (int i = 0; i < path.Length; i++) Assert.That(Vector2.Distance(path[i], reference[i]), Is.LessThan(.04f));
                }
            }
        }

        [Test]
        public void BroadCurvesUseOnlyAFewStraightLegsInsteadOfTracingTheCurve()
        {
            foreach (float amplitude in new[] { 0f, .04f, .15f })
            foreach (int density in new[] { 50, 150, 500 })
            {
                var raw = WavyPolyline("curve", amplitude, true, density);
                Assert.That(MapPathGeometry.TryPrepare(raw, out var path, out string error, toleranceMeters: .08f), Is.True, error);
                Assert.That(path.Length, Is.InRange(2, 5));
                Assert.That(path[0], Is.EqualTo(raw[0]));
                Assert.That(path[path.Length - 1], Is.EqualTo(raw[raw.Count - 1]));
            }
        }

        [Test]
        public void MoreThanTwentyDeliberateCornersAreRejectedInsteadOfTruncated()
        {
            foreach (int density in new[] { 1, 20 })
            {
                var raw = new List<Vector2>();
                Vector2 previous = Vector2.zero;
                raw.Add(previous);
                for (int corner = 1; corner <= 25; corner++)
                {
                    var next = new Vector2(corner, corner % 2 == 0 ? 0 : 1);
                    for (int sample = 1; sample <= density; sample++) raw.Add(Vector2.Lerp(previous, next, sample / (float)density));
                    previous = next;
                }
                Assert.That(MapPathGeometry.TryPrepare(raw, out var path, out string error), Is.False);
                Assert.That(path, Is.Empty);
                Assert.That(error, Does.Contain("limit is 20"));
            }
        }

        [Test]
        public void EndpointCoordinatesRemainExactEvenWhenTheyContainNoise()
        {
            var raw = WavyPolyline("line", .08f, true, 150);
            raw[0] = new Vector2(-.035f, .09f);
            raw[raw.Count - 1] += new Vector2(.02f, -.07f);
            Assert.That(MapPathGeometry.TryPrepare(raw, out var path, out string error, toleranceMeters: .08f), Is.True, error);
            Assert.That(path.Length, Is.EqualTo(2));
            Assert.That(path[0], Is.EqualTo(raw[0]));
            Assert.That(path[1], Is.EqualTo(raw[raw.Count - 1]));
        }

        [Test]
        public void NoiseScaleIsNotAnOldRawDeviationFailureThreshold()
        {
            var raw = WavyPolyline("line", .15f, true, 150);
            Assert.That(MapPathGeometry.TryPrepare(raw, out var path, out string error), Is.True, error);
            Assert.That(path.Length, Is.EqualTo(2));
            Assert.That(raw.Exists(point => Math.Abs(point.y) > .10), Is.True);
        }

        private static List<Vector2> WavyPolyline(string shape, float amplitude, bool highFrequency, int density)
        {
            Vector2[] corners = shape == "L" ? new[] { Vector2.zero, new Vector2(1.5f, 0), new Vector2(1.5f, 1.5f) } :
                shape == "Z" ? new[] { Vector2.zero, new Vector2(1.5f, 0), new Vector2(.5f, 1), new Vector2(2, 1) } :
                shape == "45" ? new[] { Vector2.zero, new Vector2(1.5f, 0), new Vector2(2.5f, 1) } : new[] { Vector2.zero, new Vector2(3, 0) };
            var points = new List<Vector2>();
            for (int segment = 0; segment < corners.Length - 1; segment++)
            for (int i = segment == 0 ? 0 : 1; i <= density; i++)
            {
                double t = i / (double)density;
                Vector2 direction = corners[segment + 1] - corners[segment];
                Vector2 point = Vector2.Lerp(corners[segment], corners[segment + 1], (float)t);
                float noise = i == 0 || i == density ? 0 : amplitude * (float)Math.Sin((segment + t) * (highFrequency ? 255 : 12));
                if (shape == "curve")
                {
                    double angle = t * Math.PI / 2;
                    point = new Vector2((float)(1.5 * Math.Sin(angle)), (float)(1.5 * (1 - Math.Cos(angle))));
                    direction = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
                }
                points.Add(point + noise * new Vector2(-direction.y, direction.x).normalized);
            }
            return points;
        }

        private static double SignedArea(IReadOnlyList<Vector2> path)
        {
            double area = 0;
            for (int i = 0; i < path.Count; i++)
            {
                Vector2 a = path[i], b = path[(i + 1) % path.Count];
                area += (double)a.x * b.y - (double)b.x * a.y;
            }
            return area / 2;
        }

        private static void AssertWithinTolerance(IReadOnlyList<Vector2> raw, IReadOnlyList<Vector2> prepared, float tolerance)
        {
            foreach (Vector2 point in raw)
            {
                float best = float.PositiveInfinity;
                for (int i = 1; i < prepared.Count; i++)
                {
                    Vector2 a = prepared[i - 1], d = prepared[i] - a;
                    float t = d.sqrMagnitude > 0 ? Mathf.Clamp01(Vector2.Dot(point - a, d) / d.sqrMagnitude) : 0;
                    best = Mathf.Min(best, Vector2.Distance(point, a + t * d));
                }
                Assert.That(best, Is.LessThanOrEqualTo(tolerance + .00001f));
            }
        }
    }
}
