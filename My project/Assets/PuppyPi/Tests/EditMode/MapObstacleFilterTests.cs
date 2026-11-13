using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace PuppyPi.Tests
{
    public class MapObstacleFilterTests
    {
        private static MapGridGeometry Grid(int width = 40, int height = 40, float x = -1, float y = -1,
            float resolution = .05f, string frame = "map") => new MapGridGeometry {
                width = width, height = height, resolution = resolution, origin = new Vector2(x, y), frame = frame
            };
        private static int Cell(MapGridGeometry grid, float x, float y) =>
            Mathf.FloorToInt((y - grid.origin.y) / grid.resolution) * grid.width +
            Mathf.FloorToInt((x - grid.origin.x) / grid.resolution);
        private static int[] Frame(MapGridGeometry grid, params int[] occupied)
        {
            var cells = new int[grid.width * grid.height];
            foreach (int index in occupied) cells[index] = 100;
            return cells;
        }
        private static int[] Apply(MapObstacleFilter filter, MapGridGeometry grid, int[] cells, long stamp, double at,
            double? now = null)
        {
            int[] before = (int[])cells.Clone();
            Assert.That(filter.TryApply(grid, cells, stamp, at, now ?? at, out int[] result, out string error), Is.True, error);
            Assert.That(cells, Is.EqualTo(before), "filtering must not mutate the raw observation");
            return result;
        }
        private static int[] Cluster(MapGridGeometry grid) => new[] { Cell(grid, .025f, .025f), Cell(grid, .075f, .025f) };

        [Test] public void ClearFirstScanIsImmediatelyUsableWithoutGlobalWarmup()
        {
            var grid = Grid(); var filter = new MapObstacleFilter();
            var output = Apply(filter, grid, Frame(grid), 1, 0);
            Assert.That(output.All(value => value == 0), Is.True);
            Assert.That(filter.ConfirmedCellCount, Is.Zero);
            Assert.That(filter.CandidateCellCount, Is.Zero);
        }

        [Test] public void AOneFrameWallIsOnlyCandidateAndDisappearsOnTheNextClearScan()
        {
            var grid = Grid(); var filter = new MapObstacleFilter();
            int[] wall = Enumerable.Range(10, 18).Select(x => 25 * grid.width + x).ToArray();
            var pending = Apply(filter, grid, Frame(grid, wall), 1, 0);
            Assert.That(filter.RawOccupiedCount, Is.EqualTo(wall.Length));
            Assert.That(filter.CandidateIndices, Is.EqualTo(wall));
            Assert.That(wall.All(index => pending[index] == -1), Is.True,
                "an unconfirmed return is not a free-space assertion");
            var clear = Apply(filter, grid, Frame(grid), 2, .1);
            Assert.That(clear.All(value => value == 0), Is.True);
            Assert.That(filter.CandidateCellCount, Is.Zero);
            Assert.That(filter.ConfirmedCellCount, Is.Zero);
        }

        [Test] public void AConsistentCoherentObstacleConfirmsOnItsThirdTenHertzObservation()
        {
            var grid = Grid(); var filter = new MapObstacleFilter(); var cells = Cluster(grid);
            Assert.That(Apply(filter, grid, Frame(grid, cells), 1, 0)[cells[0]], Is.EqualTo(-1));
            Assert.That(Apply(filter, grid, Frame(grid, cells), 2, .1)[cells[0]], Is.EqualTo(-1));
            var output = Apply(filter, grid, Frame(grid, cells), 3, .2);
            Assert.That(cells.All(index => output[index] == 100), Is.True);
            Assert.That(filter.ConfirmedCellCount, Is.EqualTo(2));
            Assert.That(filter.CandidateCellCount, Is.Zero);
        }

        [Test] public void ThreeOfFourObservationsTolerateOneMissingReturn()
        {
            var grid = Grid(); var filter = new MapObstacleFilter(); var cells = Cluster(grid);
            Apply(filter, grid, Frame(grid, cells), 1, 0);
            Apply(filter, grid, Frame(grid), 2, .1);
            Apply(filter, grid, Frame(grid, cells), 3, .2);
            var output = Apply(filter, grid, Frame(grid, cells), 4, .3);
            Assert.That(output[cells[0]], Is.EqualTo(100));
        }

        [Test] public void AlternatingPeriodicWallsNeverBecomeThreeOfFourEvidence()
        {
            var grid = Grid(); var filter = new MapObstacleFilter(); var cells = Cluster(grid);
            for (int i = 0; i < 20; i++)
            {
                Apply(filter, grid, i % 2 == 0 ? Frame(grid, cells) : Frame(grid), i + 1, i * .1);
                Assert.That(filter.ConfirmedCellCount, Is.Zero, "periodic single-frame clutter must not trigger reroutes");
            }
        }

        [Test] public void RepeatedTwoHitTwoClearBurstsDoNotConfirm()
        {
            var grid = Grid(); var filter = new MapObstacleFilter(); var cells = Cluster(grid);
            for (int i = 0; i < 24; i++)
            {
                Apply(filter, grid, i % 4 < 2 ? Frame(grid, cells) : Frame(grid), i + 1, i * .1);
                Assert.That(filter.ConfirmedCellCount, Is.Zero);
            }
        }

        [Test] public void PersistentIsolatedThinObstacleEventuallyConfirmsInsteadOfBeingDiscardedForever()
        {
            var grid = Grid(); var filter = new MapObstacleFilter(); int cell = Cluster(grid)[0];
            for (int i = 0; i < 3; i++)
                Assert.That(Apply(filter, grid, Frame(grid, cell), i + 1, i * .1)[cell], Is.EqualTo(-1));
            Assert.That(Apply(filter, grid, Frame(grid, cell), 4, .3)[cell], Is.EqualTo(100));
        }

        [TestCase(false)] [TestCase(true)]
        public void HighRateScansDoNotDeadlockTheMinimumConfirmationSpan(bool isolated)
        {
            var grid = Grid(); var filter = new MapObstacleFilter();
            int[] cells = isolated ? new[] { Cluster(grid)[0] } : Cluster(grid);
            double firstConfirmed = double.NaN;
            int votes = 0;
            for (int i = 0; i <= 40; i++)
            {
                Apply(filter, grid, Frame(grid, cells), i + 1, i * .01);
                if (filter.LastObservationAddedVote) votes++;
                if (filter.ConfirmedCellCount > 0 && double.IsNaN(firstConfirmed)) firstConfirmed = i * .01;
            }
            Assert.That(firstConfirmed, Is.InRange(isolated ? .24 : .16, .31));
            Assert.That(votes, Is.LessThan(10), "fast duplicate-density must not multiply confidence votes");
        }

        [Test] public void SourceReplayDoesNotAddConfidenceOrRefreshItsOriginalReceiptTime()
        {
            var grid = Grid(); var filter = new MapObstacleFilter(); var cells = Cluster(grid);
            Apply(filter, grid, Frame(grid, cells), 10, 0);
            for (int i = 1; i < 6; i++)
            {
                Apply(filter, grid, Frame(grid, cells), 10, i * .1, i * .1);
                Assert.That(filter.LastObservationAddedVote, Is.False);
                Assert.That(filter.ConfirmedCellCount, Is.Zero);
            }
            Apply(filter, grid, Frame(grid, cells), 11, .8);
            Assert.That(filter.ConfirmedCellCount, Is.Zero, "the first source frame's original time has expired");
            Apply(filter, grid, Frame(grid, cells), 12, .9);
            Assert.That(filter.ConfirmedCellCount, Is.Zero);
            Assert.That(Apply(filter, grid, Frame(grid, cells), 13, 1.0)[cells[0]], Is.EqualTo(100));
        }

        [Test] public void NeighboringWorldCellJitterRetainsTheSameObstacleEvidence()
        {
            var grid = Grid(); var filter = new MapObstacleFilter(); int first = Cluster(grid)[0];
            Apply(filter, grid, Frame(grid, first, first + 1), 1, 0);
            Apply(filter, grid, Frame(grid, first + 1, first + 2), 2, .1);
            var output = Apply(filter, grid, Frame(grid, first, first + 1), 3, .2);
            Assert.That(output[first], Is.EqualTo(100));
            Assert.That(output[first + 1], Is.EqualTo(100));
        }

        [Test] public void ConfirmedObstacleDoesNotFloodAWholeNewWallWithinOneObservation()
        {
            var grid = Grid(); var filter = new MapObstacleFilter(); int first = Cluster(grid)[0];
            for (int i = 0; i < 3; i++) Apply(filter, grid, Frame(grid, first, first + 1), i + 1, i * .1);
            int[] wall = Enumerable.Range(first, 14).ToArray();
            var output = Apply(filter, grid, Frame(grid, wall), 4, .3);
            Assert.That(output[first + 10], Is.EqualTo(-1), "only nearby old confidence can support a newly seen hit");
            Assert.That(filter.CandidateCellCount, Is.GreaterThan(5));
        }

        [Test] public void MissingRaysRetainNeitherOldObstaclePixelsNorFabricatedFreeSpace()
        {
            var grid = Grid(); var filter = new MapObstacleFilter(); int[] cells = Cluster(grid);
            for (int i = 0; i < 3; i++) Apply(filter, grid, Frame(grid, cells), i + 1, i * .1);
            var missing = Frame(grid);
            foreach (int cell in cells) missing[cell] = -1;
            var output = Apply(filter, grid, missing, 4, .3);
            Assert.That(cells.All(cell => output[cell] == -1), Is.True);
            Assert.That(filter.ConfirmedCellCount, Is.Zero);
            Apply(filter, grid, missing, 5, .7);
            Assert.That(Apply(filter, grid, Frame(grid, cells), 6, 1.01)[cells[0]], Is.EqualTo(-1),
                "old confirmation expires at its original last-hit time");
        }

        [Test] public void OneMeasuredClearFrameAllowsRelatchButTwoSeparatedClearFramesReleaseConfidence()
        {
            var grid = Grid(); var filter = new MapObstacleFilter(); var cells = Cluster(grid);
            for (int i = 0; i < 3; i++) Apply(filter, grid, Frame(grid, cells), i + 1, i * .1);
            Assert.That(Apply(filter, grid, Frame(grid), 4, .3)[cells[0]], Is.Zero);
            Assert.That(Apply(filter, grid, Frame(grid, cells), 5, .4)[cells[0]], Is.EqualTo(100));
            Apply(filter, grid, Frame(grid), 6, .5);
            Apply(filter, grid, Frame(grid), 7, .61);
            Assert.That(Apply(filter, grid, Frame(grid, cells), 8, .7)[cells[0]], Is.EqualTo(-1));
        }

        [Test] public void DistantSingleFrameNoiseCannotInheritAnotherObstacleConfirmation()
        {
            var grid = Grid(); var filter = new MapObstacleFilter(); var cells = Cluster(grid);
            for (int i = 0; i < 3; i++) Apply(filter, grid, Frame(grid, cells), i + 1, i * .1);
            int distant = Cell(grid, .725f, .725f);
            Assert.That(Apply(filter, grid, Frame(grid, distant, distant + 1), 4, .3)[distant], Is.EqualTo(-1));
        }

        [Test] public void IntegerCropRebasePreservesWorldEvidenceWithoutAddingAnotherVote()
        {
            var grid = Grid(); var filter = new MapObstacleFilter(); var cells = Cluster(grid);
            Apply(filter, grid, Frame(grid, cells), 1, 0);
            Apply(filter, grid, Frame(grid, cells), 2, .1);
            var shifted = Grid(43, 42, -1.05f, -1);
            int[] moved = Cluster(shifted);
            var duplicate = Apply(filter, shifted, Frame(shifted, moved), 2, .1, .15);
            Assert.That(duplicate.Length, Is.EqualTo(43 * 42));
            Assert.That(duplicate[moved[0]], Is.EqualTo(-1));
            Assert.That(filter.LastObservationAddedVote, Is.False);
            Assert.That(Apply(filter, shifted, Frame(shifted, moved), 3, .2)[moved[0]], Is.EqualTo(100));
            var expanded = Grid(45, 45, -1.1f, -1.1f);
            var cached = Apply(filter, expanded, Frame(expanded, Cluster(expanded)), 3, .2, .3);
            Assert.That(cached[Cluster(expanded)[0]], Is.EqualTo(100));
            Assert.That(filter.LastObservationAddedVote, Is.False);
        }

        [TestCase("fractional")] [TestCase("resolution")] [TestCase("frame")]
        public void ARealCoordinateChangeDropsOldConfidence(string change)
        {
            var grid = Grid(); var filter = new MapObstacleFilter(); var cells = Cluster(grid);
            for (int i = 0; i < 3; i++) Apply(filter, grid, Frame(grid, cells), i + 1, i * .1);
            var next = change == "fractional" ? Grid(x: -.975f) : change == "resolution" ? Grid(resolution: .1f) : Grid(frame: "new-map");
            Assert.That(Apply(filter, next, Frame(next, Cluster(next)), 4, .3)[Cluster(next)[0]], Is.EqualTo(-1));
        }

        [Test] public void ResetAllowsANewSourceGenerationAndDoesNotKeepConfirmation()
        {
            var grid = Grid(); var filter = new MapObstacleFilter(); var cells = Cluster(grid);
            for (int i = 0; i < 3; i++) Apply(filter, grid, Frame(grid, cells), i + 10, i * .1);
            filter.Reset();
            Assert.That(filter.RawOccupiedCount, Is.Zero);
            Assert.That(filter.CandidateCellCount, Is.Zero);
            Assert.That(Apply(filter, grid, Frame(grid, cells), 1, .4)[cells[0]], Is.EqualTo(-1));
        }

        [Test] public void StaleNewObservationsAndBackwardsClocksCannotConfirmAnObstacle()
        {
            var grid = Grid(); var filter = new MapObstacleFilter(); var cells = Frame(grid, Cluster(grid));
            Assert.That(filter.TryApply(grid, cells, 1, 0, 1.51, out _, out _), Is.False);
            Assert.That(filter.LastSourceStamp, Is.EqualTo(-1));
            Apply(filter, grid, cells, 2, 1);
            Assert.That(filter.TryApply(grid, cells, 3, .5, .9, out _, out _), Is.False);
            Assert.That(filter.TryApply(grid, cells, 1, 1.1, 1.1, out _, out _), Is.False);
            Assert.That(filter.TryApply(grid, cells, 3, 2, 1.1, out _, out _), Is.False);
            Assert.That(filter.TryApply(grid, cells, 3, double.NaN, 1.1, out _, out _), Is.False);
        }

        [Test] public void ExcessiveEndpointsAndInvalidGridValuesAreRejectedWithoutChangingTheInput()
        {
            var grid = Grid(80, 80); var filter = new MapObstacleFilter();
            var crowded = new int[80 * 80];
            for (int i = 0; i < crowded.Length; i++) crowded[i] = 100;
            Assert.That(filter.TryApply(grid, crowded, 1, 0, 0, out _, out _), Is.False);
            Assert.That(crowded.All(value => value == 100), Is.True);
            crowded[0] = 101;
            Assert.That(filter.TryApply(grid, crowded, 1, 0, 0, out _, out _), Is.False);
            Assert.That(filter.TryApply(grid, new int[1], 1, 0, 0, out _, out _), Is.False);
        }
    }
}
