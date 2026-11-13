using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace PuppyPi.Tests
{
    public class MapKnownFreeLayerTests
    {
        private static MapGridGeometry Grid(int width = 4, int height = 3, float x = 0,
            float y = 0, float resolution = .1f, string frame = "map") => new MapGridGeometry {
                width = width, height = height, resolution = resolution,
                origin = new Vector2(x, y), frame = frame
            };

        private static int[] Cells(MapGridGeometry grid, int value = -1) =>
            Enumerable.Repeat(value, grid.width * grid.height).ToArray();

        private static int Cell(MapGridGeometry grid, Vector2 world) =>
            (int)Math.Floor((world.y - grid.origin.y) / grid.resolution) * grid.width +
            (int)Math.Floor((world.x - grid.origin.x) / grid.resolution);

        private static void Observe(MapKnownFreeLayer layer, MapGridGeometry grid, int[] current)
        {
            int[] before = (int[])current.Clone();
            Assert.That(layer.Observe(grid, current, out string error), Is.True, error);
            CollectionAssert.AreEqual(before, current, "observing evidence must not edit the source scan");
        }

        private static int[] Merge(MapKnownFreeLayer layer, int[] map, int[] output)
        {
            int[] before = (int[])map.Clone();
            Assert.That(layer.TryMerge(map, output, out string error), Is.True, error);
            CollectionAssert.AreEqual(before, map, "map history remains an independent source");
            return output;
        }

        private static int[] Apply(MapKnownFreeLayer layer, MapGridGeometry grid, int[] map, int[] current)
        {
            Observe(layer, grid, current);
            return Merge(layer, map, (int[])current.Clone());
        }

        [Test]
        public void OnlyMapperFreeRangeFillsMissingScanEvidence()
        {
            var grid = Grid(12, 1);
            var layer = new MapKnownFreeLayer();
            int[] map = { 0, 1, 2, 11, 24, 25, -1, 26, 49, 50, 99, 100 };
            int[] output = Apply(layer, grid, map, Cells(grid));
            CollectionAssert.AreEqual(new[] { 0, 0, 0, 0, 0, 0, -1, -1, -1, -1, -1, -1 }, output);
            Assert.That(layer.ReusedCellCount, Is.EqualTo(6));
            Assert.That(layer.IsReusedCell(0), Is.True);
            Assert.That(layer.IsReusedCell(5), Is.True, "free_thresh=0.25 includes occupancy 25");
            Assert.That(layer.IsReusedCell(6), Is.False, "unobserved source cells stay unknown");
            Assert.That(layer.IsReusedCell(7), Is.False, "occupancy 26 is outside the producer's free class");
            Assert.That(layer.IsReusedCell(-1), Is.False);
            Assert.That(layer.IsReusedCell(output.Length), Is.False);
        }

        [Test]
        public void RecordedGradedFreeCellRemainsPermittedWhenItsCurrentLaserSectorIsMissing()
        {
            // Reduced regression from a read-only real scan/map capture: this
            // world cell had BaseOccupancy 11, while beams 405..409 were absent.
            // Preserve those coordinates/calibration without storing the room map.
            var grid = Grid(600, 600, -15, -15, .05f);
            var target = new Vector2(2.48f, 1.38f);
            int index = Cell(grid, target);
            var ranges = Enumerable.Repeat(float.NaN, 450).ToArray();
            ranges[0] = 6.32f;
            Assert.That(MapLaserLayer.TryBuildCurrent(grid,
                new Vector3(.971875f, -.45f, 1.475625f), Vector3.zero, ranges,
                0, .013962633907794952f, .12f, 11.5f, out int[] current,
                out string error), Is.True, error);
            Assert.That(current[index], Is.EqualTo(-1), "absent rays must never manufacture measured free");
            int[] map = Cells(grid);
            map[index] = 11;
            var layer = new MapKnownFreeLayer();
            int[] output = Apply(layer, grid, map, current);
            Assert.That(output[index], Is.Zero, "previously observed mapper-free value 11 remains usable");
            Assert.That(layer.IsReusedCell(index), Is.True, "permission retains map-prior provenance");
            Assert.That(current[index], Is.EqualTo(-1), "map permission does not restamp the sensor observation");
        }

        [TestCase(0)]
        [TestCase(2)]
        [TestCase(11)]
        [TestCase(25)]
        public void CurrentConfirmedObstacleWinsOverStoredFreeWithoutImportingOldMapObstacles(int mapFreeValue)
        {
            var grid = Grid();
            var layer = new MapKnownFreeLayer();
            int[] map = Cells(grid, mapFreeValue), current = Cells(grid);
            current[2] = 100;
            map[3] = 100;
            int[] output = Apply(layer, grid, map, current);
            Assert.That(output[2], Is.EqualTo(100), "current occupied evidence must win over old map free");
            Assert.That(output[3], Is.EqualTo(-1), "old map obstacles do not become current obstacles");
            Assert.That(layer.IsReusedCell(2), Is.False);
            Assert.That(layer.IsReusedCell(3), Is.False);
        }

        [TestCase(0)]
        [TestCase(2)]
        [TestCase(11)]
        [TestCase(25)]
        public void MissingBeamsAndRepeatedMapFreeCannotReviveAConfirmedObstacleCell(int mapFreeValue)
        {
            var grid = Grid();
            var layer = new MapKnownFreeLayer();
            int[] map = Cells(grid, mapFreeValue), hit = Cells(grid);
            hit[5] = 100;
            Apply(layer, grid, map, hit);
            for (int i = 0; i < 4; i++)
            {
                int[] output = Apply(layer, grid, (int[])map.Clone(), Cells(grid));
                Assert.That(output[5], Is.EqualTo(-1));
                Assert.That(layer.IsReusedCell(5), Is.False);
            }
        }

        [TestCase(0)]
        [TestCase(2)]
        [TestCase(11)]
        [TestCase(25)]
        public void ADirectMeasuredFreeObservationRestoresLaterUseOfMapFree(int mapFreeValue)
        {
            var grid = Grid();
            var layer = new MapKnownFreeLayer();
            int[] map = Cells(grid, mapFreeValue), hit = Cells(grid);
            hit[5] = 100;
            Apply(layer, grid, map, hit);
            int[] clear = Cells(grid);
            clear[5] = 0;
            int[] measured = Apply(layer, grid, map, clear);
            Assert.That(measured[5], Is.Zero);
            Assert.That(layer.IsReusedCell(5), Is.False, "direct evidence has priority over the map prior");
            int[] missingAgain = Apply(layer, grid, map, Cells(grid));
            Assert.That(missingAgain[5], Is.Zero);
            Assert.That(layer.IsReusedCell(5), Is.True);
        }

        [TestCase(0)]
        [TestCase(2)]
        [TestCase(11)]
        [TestCase(25)]
        public void CachedFreeInTheMergeOutputDoesNotClearTheDirectEvidenceVeto(int mapFreeValue)
        {
            var grid = Grid();
            var layer = new MapKnownFreeLayer();
            int[] map = Cells(grid, mapFreeValue), hit = Cells(grid);
            hit[5] = 100;
            Apply(layer, grid, map, hit);
            Observe(layer, grid, Cells(grid));
            int[] recentFreeOutput = Cells(grid);
            recentFreeOutput[5] = 0;
            Merge(layer, map, recentFreeOutput);
            Assert.That(recentFreeOutput[5], Is.EqualTo(-1), "cached free cannot override a newer confirmed obstacle");
            Assert.That(layer.IsReusedCell(5), Is.False);
            int[] nextMissing = Apply(layer, grid, map, Cells(grid));
            Assert.That(nextMissing[5], Is.EqualTo(-1), "only Observe receiving direct free may clear revocation");
        }

        [Test]
        public void RepeatedMergeOfTheSameOutputIsIdempotent()
        {
            var grid = Grid();
            var layer = new MapKnownFreeLayer();
            int[] map = Cells(grid, 0);
            int[] output = Apply(layer, grid, map, Cells(grid));
            int reused = layer.ReusedCellCount;
            for (int i = 0; i < 3; i++)
            {
                Merge(layer, map, output);
                Assert.That(output, Is.All.EqualTo(0));
                Assert.That(layer.ReusedCellCount, Is.EqualTo(reused));
                Assert.That(layer.IsReusedCell(5), Is.True);
            }
        }

        [Test]
        public void ObserveDoesNotLoseOwnershipNeededToWithdrawOldMapContributions()
        {
            var grid = Grid();
            var layer = new MapKnownFreeLayer();
            int[] current = Cells(grid);
            current[1] = 0;
            int[] output = Apply(layer, grid, Cells(grid, 0), current);
            Observe(layer, grid, Cells(grid));
            Merge(layer, Cells(grid), output);
            Assert.That(output[0], Is.EqualTo(-1), "old map-owned zero must be withdrawn");
            Assert.That(output[1], Is.Zero, "direct free was never owned by the map layer");
            Assert.That(layer.ReusedCellCount, Is.Zero);
        }

        [Test]
        public void ANewOutputArrayKeepsItsOwnDirectFreeEvenIfThePreviousArrayUsedMapFree()
        {
            var grid = Grid();
            var layer = new MapKnownFreeLayer();
            Apply(layer, grid, Cells(grid, 0), Cells(grid));
            int[] current = Cells(grid);
            current[5] = 0;
            Observe(layer, grid, current);
            int[] nextOutput = Merge(layer, Cells(grid), (int[])current.Clone());
            Assert.That(nextOutput[5], Is.Zero);
            Assert.That(layer.ReusedCellCount, Is.Zero);
        }

        [Test]
        public void ChangedSourceMapWithdrawsOnlyPreviouslyReusedCells()
        {
            var grid = Grid();
            var layer = new MapKnownFreeLayer();
            int[] current = Cells(grid);
            current[1] = 0;
            int[] output = Apply(layer, grid, Cells(grid, 0), current);
            int[] changedMap = Cells(grid);
            changedMap[2] = 100;
            Merge(layer, changedMap, output);
            Assert.That(output[0], Is.EqualTo(-1));
            Assert.That(output[1], Is.Zero);
            Assert.That(output[2], Is.EqualTo(-1), "stored occupied cells are not imported while withdrawing map free");
            Assert.That(layer.ReusedCellCount, Is.Zero);
        }

        [TestCase(0)]
        [TestCase(11)]
        [TestCase(25)]
        public void ClearingPresentationDoesNotForgetConfirmedObstacleRevocation(int mapFreeValue)
        {
            var grid = Grid();
            var layer = new MapKnownFreeLayer();
            int[] map = Cells(grid, mapFreeValue), hit = Cells(grid);
            hit[5] = 100;
            Apply(layer, grid, map, hit);
            layer.ClearPresentation();
            Assert.That(layer.ReusedCellCount, Is.Zero);
            Assert.That(layer.IsReusedCell(0), Is.False);
            int[] output = Apply(layer, grid, map, Cells(grid));
            Assert.That(output[5], Is.EqualTo(-1));
        }

        [TestCase(0)]
        [TestCase(11)]
        [TestCase(25)]
        public void AlignedCropResizePreservesRevocationAtTheSameWorldCell(int mapFreeValue)
        {
            var grid = Grid();
            var layer = new MapKnownFreeLayer();
            var world = new Vector2(.15f, .15f);
            int[] hit = Cells(grid);
            hit[Cell(grid, world)] = 100;
            Apply(layer, grid, Cells(grid, mapFreeValue), hit);
            var crop = Grid(6, 5, -.1f, -.1f);
            int index = Cell(crop, world);
            int[] output = Apply(layer, crop, Cells(crop, mapFreeValue), Cells(crop));
            Assert.That(output[index], Is.EqualTo(-1));
            Assert.That(layer.IsReusedCell(index), Is.False);
            Assert.That(output[0], Is.Zero, "other known-free cells remain usable after the viewport changes");
        }

        [TestCase(0)]
        [TestCase(11)]
        [TestCase(25)]
        public void CroppingARevokedCellOutAndBackInCannotRestoreOldMapFree(int mapFreeValue)
        {
            var grid = Grid();
            var layer = new MapKnownFreeLayer();
            int[] hit = Cells(grid);
            hit[5] = 100;
            Apply(layer, grid, Cells(grid, mapFreeValue), hit);
            var elsewhere = Grid(4, 3, 10, 10);
            Apply(layer, elsewhere, Cells(elsewhere, mapFreeValue), Cells(elsewhere));
            int[] returned = Apply(layer, grid, Cells(grid, mapFreeValue), Cells(grid));
            Assert.That(returned[5], Is.EqualTo(-1), "a nonoverlapping viewport must not discard the world-cell veto");
        }

        [TestCase("frame")]
        [TestCase("resolution")]
        [TestCase("fractional_origin")]
        public void AnIncompatibleCoordinateGenerationDoesNotReuseOldVetoIndices(string change)
        {
            var grid = Grid();
            var layer = new MapKnownFreeLayer();
            int[] hit = Cells(grid);
            hit[5] = 100;
            Apply(layer, grid, Cells(grid, 0), hit);
            var next = change == "frame" ? Grid(frame: "new_map") :
                change == "resolution" ? Grid(resolution: .2f) : Grid(x: .05f);
            int[] output = Apply(layer, next, Cells(next, 0), Cells(next));
            Assert.That(output, Is.All.EqualTo(0));
        }

        [Test]
        public void ResetStartsANewMapGenerationWithNoOldPresentationOrVeto()
        {
            var grid = Grid();
            var layer = new MapKnownFreeLayer();
            int[] hit = Cells(grid);
            hit[5] = 100;
            Apply(layer, grid, Cells(grid, 0), hit);
            layer.Reset();
            Assert.That(layer.ReusedCellCount, Is.Zero);
            Assert.That(layer.IsReusedCell(0), Is.False);
            Assert.That(Apply(layer, grid, Cells(grid, 0), Cells(grid))[5], Is.Zero);
        }

        [Test]
        public void InvalidObservationCannotPartiallyReleaseAConfirmedObstacleVeto()
        {
            var grid = Grid();
            var layer = new MapKnownFreeLayer();
            int[] hit = Cells(grid);
            hit[0] = 100;
            Apply(layer, grid, Cells(grid, 0), hit);
            int[] invalid = Cells(grid);
            invalid[0] = 0;
            invalid[1] = 101;
            Assert.That(layer.Observe(grid, invalid, out string error), Is.False);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
            int[] output = Apply(layer, grid, Cells(grid, 0), Cells(grid));
            Assert.That(output[0], Is.EqualTo(-1));
        }

        [Test]
        public void InvalidMapDoesNotPartiallyEditTheNavigationOutput()
        {
            var grid = Grid();
            var layer = new MapKnownFreeLayer();
            Observe(layer, grid, Cells(grid));
            int[] map = Cells(grid, 0), output = Cells(grid);
            map[map.Length - 1] = 101;
            int[] before = (int[])output.Clone();
            Assert.That(layer.TryMerge(map, output, out string error), Is.False);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
            CollectionAssert.AreEqual(before, output);
        }

        [Test]
        public void ExceedingTheVetoBudgetStaysClosedUntilExplicitReset()
        {
            var grid = Grid(257, 256);
            var layer = new MapKnownFreeLayer();
            int[] tooMany = Cells(grid);
            for (int i = 0; i < 65537; i++) tooMany[i] = 100;
            Assert.That(layer.Observe(grid, tooMany, out string error), Is.False);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
            Assert.That(layer.Observe(grid, Cells(grid, 0), out _), Is.False);
            Assert.That(layer.TryMerge(Cells(grid, 0), Cells(grid), out _), Is.False);
            layer.ClearPresentation();
            Assert.That(layer.Observe(Grid(frame: "another_map"), Cells(Grid(), 0), out _), Is.False,
                "changing the viewport cannot unlatch a capacity failure");
            layer.Reset();
            int[] afterReset = Apply(layer, grid, Cells(grid, 0), Cells(grid));
            Assert.That(afterReset, Is.All.EqualTo(0));
        }
    }
}
