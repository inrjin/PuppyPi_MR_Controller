#if UNITY_EDITOR
using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace PuppyPi.Tests
{
    public class MapDataClientTests
    {
        private GameObject _root;
        private PuppyPiMapDataClient _client;
        private const string Grid = "{\"op\":\"publish\",\"topic\":\"/map\",\"msg\":{\"header\":{\"frame_id\":\"map\"}," +
            "\"info\":{\"width\":3,\"height\":2,\"resolution\":0.5,\"origin\":{\"position\":{\"x\":-2,\"y\":1},\"orientation\":{\"w\":1}}}," +
            "\"data\":[0,100,-1,0,0,0]}}";
        private static string Pose(int seconds, string frame = "map") =>
            "{\"op\":\"publish\",\"topic\":\"/lidar_map/pose\",\"msg\":{\"header\":{\"frame_id\":\"" + frame +
            "\",\"stamp\":{\"sec\":" + seconds + ",\"nanosec\":0}},\"pose\":{\"position\":{\"x\":-1,\"y\":1.5},\"orientation\":{\"w\":1}}}}";

        [UnitySetUp] public IEnumerator Setup()
        {
            yield return WaitForNonnegativeHistoricalReceipts();
            _root = new GameObject("map data fixture");
            _client = _root.AddComponent<PuppyPiMapDataClient>();
        }
        [TearDown] public void Cleanup() => UnityEngine.Object.DestroyImmediate(_root);

        [Test] public void GridTextureAndGeometryShareOriginDimensionsAndBottomLeftOrder()
        {
            Assert.That(_client.ApplyGrid(Grid), Is.True);
            Assert.That(_client.Geometry.origin, Is.EqualTo(new Vector2(-2, 1)));
            Assert.That(_client.Geometry.width, Is.EqualTo(_client.Texture.width));
            Assert.That(_client.Geometry.height, Is.EqualTo(_client.Texture.height));
            Assert.That(_client.BaseOccupancy, Is.EqualTo(new[] { 0, 100, -1, 0, 0, 0 }));
            Assert.That(_client.Occupancy, Is.All.EqualTo(-1), "the accumulated map supplies coordinates, not current obstacle evidence");
            Assert.That(_client.Texture.GetPixel(0, 0), Is.EqualTo(_client.Texture.GetPixel(1, 0)),
                "a raw stored obstacle must not appear in the fresh scan image before a matched scan arrives");
            Assert.That(_client.Geometry.TryUvToMeters(Vector2.zero, out var meters), Is.True);
            Assert.That(meters, Is.EqualTo(new Vector2(-2, 1)));
            Assert.That(_client.Live, Is.False, "Parsing alone does not fabricate network freshness.");
        }

        [Test] public void RejectsBadGeometryAndCellsWithoutReplacingValidMap()
        {
            Assert.That(_client.ApplyGrid(Grid), Is.True);
            long revision = _client.Revision;
            Assert.That(_client.ApplyGrid(Grid.Replace("[0,100,-1,0,0,0]", "[0,100]")), Is.False);
            Assert.That(_client.ApplyGrid(Grid.Replace("[0,100,-1,0,0,0]", "[0,101,-1,0,0,0]")), Is.False);
            Assert.That(_client.ApplyGrid(Grid.Replace("\"w\":1", "\"z\":0.5,\"w\":0.866")), Is.False);
            Assert.That(_client.ApplyGrid(Grid.Replace("\"resolution\":0.5", "\"resolution\":0")), Is.False);
            Assert.That(_client.Revision, Is.EqualTo(revision));
        }

        [Test] public void PoseRequiresMatchingFrameAndIncreasingSourceStamp()
        {
            Assert.That(_client.ApplyGrid(Grid), Is.True);
            Assert.That(_client.ApplyPose(Pose(1, "odom")), Is.False);
            Assert.That(_client.ApplyPose(Pose(2)), Is.True);
            Assert.That(_client.ApplyPose(Pose(2)), Is.False);
            Assert.That(_client.ApplyPose(Pose(1)), Is.False);
            Assert.That(_client.RobotPose, Is.EqualTo(new Vector3(-1, 1.5f, 0)));
        }

        [Test] public void SessionChangeInvalidatesOldGridAndPose()
        {
            _client.SetSession("first");
            Assert.That(_client.ApplyGrid(Grid), Is.True);
            Assert.That(_client.ApplyPose(Pose(10)), Is.True);
            _client.SetSession("second");
            Assert.That(_client.Geometry, Is.Null);
            Assert.That(_client.Occupancy, Is.Null);
            Assert.That(_client.BaseOccupancy, Is.Null);
            Assert.That(_client.NavigationLive, Is.False);
            Assert.That(_client.Live, Is.False);
            Assert.That(_client.ApplyPose(Pose(11)), Is.False);
        }

        private static string Calibration(string id = "fixture-calibration", string x = "0") =>
            "{\"op\":\"service_response\",\"service\":\"/lidar_mapper/get_parameters\",\"id\":\"" + id +
            "\",\"result\":true,\"values\":{\"values\":[{\"type\":3,\"double_value\":" + x +
            "},{\"type\":3,\"double_value\":0},{\"type\":3,\"double_value\":0}," +
            "{\"type\":4,\"string_value\":\"map\"},{\"type\":4,\"string_value\":\"base_footprint\"}," +
            "{\"type\":3,\"double_value\":0.12},{\"type\":3,\"double_value\":11.5}]}}";

        private static string RawScan(int sec, string range, int nanos = 0, double angleMin = 0, double increment = .1) =>
            "{\"op\":\"publish\",\"topic\":\"/scan\",\"msg\":{\"header\":{\"frame_id\":\"laser\",\"stamp\":{\"sec\":" + sec +
            ",\"nanosec\":" + nanos + "}},\"angle_min\":" + angleMin.ToString("R", CultureInfo.InvariantCulture) +
            ",\"angle_max\":" + (angleMin + increment * (range.Split(',').Length - 1)).ToString("R", CultureInfo.InvariantCulture) +
            ",\"angle_increment\":" + increment.ToString("R", CultureInfo.InvariantCulture) + "," +
            "\"range_min\":0.02,\"range_max\":12,\"ranges\":[" + range + "]}}";

        // Map-client fixtures need enough real returns for the production
        // mapper to estimate pose. Keep the tested 0/.1 rad probe directions;
        // add narrow adjacent measured rays, never manufactured free cells.
        // Explicit degraded-scan tests use RawScan without these context rays.
        private static string CurrentScan(int sec, string range = "0.83", int nanos = 0, double angleMin = 0)
        {
            string[] probes = range.Split(',');
            int firstValid = Array.FindIndex(probes, value => double.TryParse(value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double number) && number >= .12 && number <= 11.5);
            if (firstValid < 0 || probes.Length > 2) return RawScan(sec, range, nanos, angleMin);
            var values = new string[Math.Max((probes.Length - 1) * 1000 + 1, firstValid * 1000 + 30)];
            for (int i = 0; i < values.Length; i++) values[i] = "null";
            for (int i = 0; i < probes.Length; i++) values[i * 1000] = probes[i];
            for (int i = 1; i < 30; i++) values[firstValid * 1000 + i] = probes[firstValid];
            return RawScan(sec, string.Join(",", values), nanos, angleMin, .0001);
        }

        private static string CurrentPose(int sec, int nanos = 0) =>
            "{\"op\":\"publish\",\"topic\":\"/lidar_map/pose\",\"msg\":{\"header\":{\"frame_id\":\"map\",\"stamp\":{\"sec\":" + sec +
            ",\"nanosec\":" + nanos + "}},\"pose\":{\"position\":{\"x\":0,\"y\":0},\"orientation\":{\"w\":1}}}}";

        private static string NavigationGrid(bool allOccupied = false)
        {
            var json = new StringBuilder("{\"op\":\"publish\",\"topic\":\"/map\",\"msg\":{\"header\":{\"frame_id\":\"map\"}," +
                "\"info\":{\"width\":25,\"height\":25,\"resolution\":0.1,\"origin\":{\"position\":{\"x\":-1.05,\"y\":-1.05},\"orientation\":{\"w\":1}}},\"data\":[");
            for (int y = 0; y < 25; y++)
            for (int x = 0; x < 25; x++)
            {
                if (x != 0 || y != 0) json.Append(',');
                json.Append(allOccupied ? 100 : x == 12 && (y == 10 || y == 12) ? 100 : x == 10 && y == 12 ? -1 : 0);
            }
            return json.Append("]}}").ToString();
        }

        private static string FullScan(int seconds, float range = 2)
        {
            double step = 2 * Math.PI / 360;
            var ranges = new StringBuilder();
            for (int i = 0; i < 360; i++)
            {
                if (i != 0) ranges.Append(',');
                ranges.Append(range.ToString("R", CultureInfo.InvariantCulture));
            }
            return RawScan(seconds, ranges.ToString(), increment: step);
        }

        private static string KnownFreeGrid(int value = 0, int width = 60, int height = 60,
            float originX = -1.5f, float originY = -1.5f)
        {
            var json = new StringBuilder("{\"op\":\"publish\",\"topic\":\"/map\",\"msg\":{\"header\":{\"frame_id\":\"map\"},\"info\":{\"width\":");
            json.Append(width).Append(",\"height\":").Append(height)
                .Append(",\"resolution\":0.05,\"origin\":{\"position\":{\"x\":")
                .Append(originX.ToString("R", CultureInfo.InvariantCulture)).Append(",\"y\":")
                .Append(originY.ToString("R", CultureInfo.InvariantCulture))
                .Append("},\"orientation\":{\"w\":1}}},\"data\":[");
            for (int i = 0; i < width * height; i++)
            { if (i != 0) json.Append(','); json.Append(value); }
            return json.Append("]}}").ToString();
        }

        // Reproduces the persistent missing-return sector in the stationary
        // robot capture, rather than selecting a different clear heading.
        private static string ForwardBlindScan(int seconds)
        {
            double step = 2 * Math.PI / 450;
            var ranges = new StringBuilder();
            for (int i = 0; i < 450; i++)
            {
                if (i != 0) ranges.Append(',');
                ranges.Append(i >= 434 && i <= 447 ? "null" : "3");
            }
            return RawScan(seconds, ranges.ToString(), increment: step);
        }

        private static string DenseWindowGrid(int width, int height, float originX, float originY, float resolution = .1f)
        {
            var json = new StringBuilder("{\"op\":\"publish\",\"topic\":\"/map\",\"msg\":{\"header\":{\"frame_id\":\"map\"},\"info\":{\"width\":");
            json.Append(width).Append(",\"height\":").Append(height)
                .Append(",\"resolution\":").Append(resolution.ToString("R", CultureInfo.InvariantCulture))
                .Append(",\"origin\":{\"position\":{\"x\":").Append(originX.ToString("R", CultureInfo.InvariantCulture))
                .Append(",\"y\":").Append(originY.ToString("R", CultureInfo.InvariantCulture))
                .Append("},\"orientation\":{\"w\":1}}},\"data\":[");
            for (int i = 0; i < width * height; i++)
            { if (i != 0) json.Append(','); json.Append(100); }
            return json.Append("]}}").ToString();
        }

        // Receipt times are deliberately in the past so temporal confirmation
        // exercises distinct real observations without sleeping in EditMode.
        private void PairAt(string scan, int seconds, int nanos, double receivedAt)
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            string stamped = scan.Replace("\"nanosec\":0", "\"nanosec\":" + nanos);
            Assert.That((bool)typeof(PuppyPiMapDataClient).GetMethod("ApplyPoseAt", flags)
                .Invoke(_client, new object[] { CurrentPose(seconds, nanos), receivedAt, -1 }), Is.True);
            Assert.That((bool)typeof(PuppyPiMapDataClient).GetMethod("ApplyScanAt", flags)
                .Invoke(_client, new object[] { stamped, receivedAt, -1 }), Is.True);
        }

        private void ConfirmScan(string scan, int seconds, double firstAt)
        {
            for (int i = 0; i < 4; i++) PairAt(scan, seconds, i * 100000000, firstAt + i * .08);
        }

        private static string UpwardScan(int seconds) => CurrentScan(seconds, angleMin: Math.PI / 2);

        private static string SparseScan(int seconds, int validRays, string invalidReturn = "null")
        {
            int count = Math.Max(64, validRays);
            var ranges = new string[count];
            for (int i = 0; i < count; i++) ranges[i] = i < validRays ? "0.83" : invalidReturn;
            return RawScan(seconds, string.Join(",", ranges), increment: 2 * Math.PI / count);
        }

        private bool HasVerifiedLayer => (bool)typeof(PuppyPiMapDataClient)
            .GetField("_hasLayer", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_client);

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(10)]
        [TestCase(29)]
        public void SparseExactPairsCannotAuthorizeNavigationUsingKnownMapFreeSpace(int validRays)
        {
            _client.SetSession("localization-quality");
            _client.ApplyGrid(KnownFreeGrid());
            _client.ApplyCalibration(Calibration(), "fixture-calibration");
            PairAt(SparseScan(10, validRays), 10, 0, Time.realtimeSinceStartupAsDouble - .02);
            Assert.That(HasVerifiedLayer, Is.False, "matching source stamps do not prove the mapper estimated this position");
            Assert.That(_client.NavigationLive, Is.False);
            Assert.That(_client.Occupancy, Is.All.EqualTo(-1));
            Assert.That(_client.MapFreeCellCount, Is.Zero, "a stored free map cannot replace the missing localization evidence");
            Assert.That(_client.NavigationStatus, Does.Contain(validRays + "/30"));
            Assert.That(_client.EditingAvailable, Is.True);
        }

        [TestCase(30)]
        [TestCase(360)]
        public void MapperSupportedRayCountsBuildVerifiedLayerWithoutExtraWarmup(int validRays)
        {
            _client.ApplyGrid(KnownFreeGrid());
            _client.ApplyCalibration(Calibration(), "fixture-calibration");
            PairAt(SparseScan(10, validRays), 10, 0, Time.realtimeSinceStartupAsDouble - .02);
            Assert.That(HasVerifiedLayer, Is.True);
            Assert.That(_client.FilterStatus, Does.StartWith("FILTER READY"));
            Assert.That(_client.NavigationObservationStamp, Is.EqualTo(10000000000L));
            Assert.That(_client.CurrentFreeCellCount, Is.GreaterThan(0));
            Assert.That(_client.NavigationLive, Is.False, "a component test still does not invent a connected transport");
        }

        [TestCase("0.05")]
        [TestCase("11.8")]
        [TestCase("0.01")]
        public void BodyEchoesAndOutOfCalibrationReturnsDoNotSatisfyLocalizationMinimum(string invalidReturn)
        {
            _client.ApplyGrid(KnownFreeGrid());
            _client.ApplyCalibration(Calibration(), "fixture-calibration");
            PairAt(SparseScan(10, 29, invalidReturn), 10, 0, Time.realtimeSinceStartupAsDouble - .02);
            Assert.That(HasVerifiedLayer, Is.False);
            Assert.That(_client.NavigationStatus, Does.Contain("29/30"));
            Assert.That(_client.Occupancy, Is.All.EqualTo(-1));
        }

        [TestCase("0.12", "11.5", "0.11999999731779099", false)]
        [TestCase("0.125", "11.5", "0.125", true)]
        [TestCase("0.1250000001", "11.5", "0.125", false)]
        [TestCase("0.12", "0.9999999999", "1.0", false)]
        [TestCase("0.12", "1.0", "1.0", true)]
        public void CalibratedDoubleBoundsMatchMapperComparisonsOnRosFloatRanges(
            string minimum, string maximum, string boundary, bool supported)
        {
            _client.ApplyGrid(KnownFreeGrid());
            _client.ApplyCalibration(Calibration().Replace("\"double_value\":0.12", "\"double_value\":" + minimum)
                .Replace("\"double_value\":11.5", "\"double_value\":" + maximum), "fixture-calibration");
            var ranges = new string[64];
            for (int i = 0; i < ranges.Length; i++) ranges[i] = i < 29 ? "0.5" : i == 29 ? boundary : "null";
            PairAt(RawScan(10, string.Join(",", ranges), increment: 2 * Math.PI / ranges.Length),
                10, 0, Time.realtimeSinceStartupAsDouble - .02);
            Assert.That(HasVerifiedLayer, Is.EqualTo(supported),
                "float32 casts must not change whether the mapper saw its thirtieth valid return");
            if (!supported) Assert.That(_client.NavigationStatus, Does.Contain("29/30"));
        }

        [TestCase(1)]
        [TestCase(10)]
        [TestCase(29)]
        public void LocalizationDropoutImmediatelyRevokesFreshLayerAndOnlyNewSupportedPairRecovers(int validRays)
        {
            _client.SetSession("localization-dropout");
            _client.ApplyGrid(KnownFreeGrid());
            _client.ApplyCalibration(Calibration(), "fixture-calibration");
            double now = Time.realtimeSinceStartupAsDouble;
            PairAt(FullScan(10, 3), 10, 0, now - .06);
            Assert.That(HasVerifiedLayer, Is.True);
            Assert.That(_client.NavigationAgeSeconds, Is.LessThan(.6), "the baseline has not timed out");
            var texture = _client.Texture;

            PairAt(SparseScan(11, validRays), 11, 0, now - .03);
            Assert.That(HasVerifiedLayer, Is.False, "an unusable localization pair must not keep the previous frame live for 1.25 s");
            Assert.That(_client.NavigationLive, Is.False);
            Assert.That(_client.CurrentFreeCellCount, Is.Zero);
            Assert.That(_client.Occupancy, Is.All.EqualTo(-1));
            Assert.That(_client.RecentFreeCellCount, Is.Zero);
            Assert.That(_client.NavigationStatus, Does.Contain(validRays + "/30"));
            Assert.That(_client.EditingAvailable, Is.True);
            Assert.That(_client.Texture, Is.SameAs(texture));
            _client.ApplyGrid(KnownFreeGrid());
            Assert.That(HasVerifiedLayer, Is.False, "grid publications cannot resurrect the pre-dropout exact pair");

            PairAt(FullScan(12, 3), 12, 0, now - .01);
            Assert.That(HasVerifiedLayer, Is.True);
            Assert.That(_client.FilterStatus, Does.StartWith("FILTER READY"));
            Assert.That(_client.NavigationObservationStamp, Is.EqualTo(12000000000L));
            Assert.That(_client.CurrentFreeCellCount, Is.GreaterThan(0));
        }

        private double[] PrepareRecentFreeFixture()
        {
            _client.ApplyGrid(NavigationGrid(allOccupied: true));
            _client.ApplyCalibration(Calibration(), "fixture-calibration");
            double now = Time.realtimeSinceStartupAsDouble;
            PairAt(FullScan(10), 10, 0, now - .45);
            ConfirmScan(UpwardScan(11), 11, now - .32);
            return RecentProof();
        }

        private static IEnumerator WaitForNonnegativeHistoricalReceipts()
        {
            var wallClock = System.Diagnostics.Stopwatch.StartNew();
            while (Time.realtimeSinceStartupAsDouble < .8 && wallClock.Elapsed.TotalSeconds < 1)
                yield return null;
            Assert.That(Time.realtimeSinceStartupAsDouble, Is.GreaterThanOrEqualTo(.8),
                "the EditMode runtime clock must allow positive original receipt times before subtracting sensor history");
        }

        private double[] RecentProof() => (double[])typeof(PuppyPiMapDataClient)
            .GetField("_recentFreeObservedAt", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_client);

        private int CellIndex(Vector2 world) =>
            Mathf.FloorToInt((world.y - _client.Geometry.origin.y) / _client.Geometry.resolution) * _client.Geometry.width +
            Mathf.FloorToInt((world.x - _client.Geometry.origin.x) / _client.Geometry.resolution);

        [Test] public void MapKnownFreePriorAllowsActualForwardRoutesAcrossPersistentMissingReturns()
        {
            Assert.That(_client.ApplyGrid(KnownFreeGrid()), Is.True);
            Assert.That(_client.ApplyCalibration(Calibration(), "fixture-calibration"), Is.True);
            double now = Time.realtimeSinceStartupAsDouble;
            for (int i = 0; i < 4; i++) PairAt(ForwardBlindScan(10), 10, i * 100000000, now - .35 + i * .08);
            var blindCell = new Vector2(.725f, -.075f);
            Assert.That(_client.MapFreeCellCount, Is.GreaterThan(0));
            Assert.That(_client.IsMapKnownFree(blindCell), Is.True);
            Assert.That(double.IsNegativeInfinity(RecentProof()[CellIndex(blindCell)]), Is.True,
                "a map prior must not fabricate a direct LiDAR observation time");
            foreach (float distance in new[] { .3f, .6f })
                Assert.That(MapDetourPlanner.TryFindRouteBlock(_client.Geometry, _client.Occupancy,
                    new[] { Vector2.zero, new Vector2(distance, 0) }, 0, new MapRobotFootprint(), out var block), Is.False,
                    "actual forward body and front guard must use known map free space: " + block?.Kind);
            Assert.That(_client.ConfirmedObstacleCellCount, Is.Zero);
            Assert.That(_client.NavigationLive, Is.False, "map free space must not invent a connected live sensor channel");
        }

        [TestCase(-1)]
        [TestCase(100)]
        public void MissingReturnsStayUnknownWhenMapHasNoExplicitFreeEvidence(int storedValue)
        {
            _client.ApplyGrid(KnownFreeGrid(storedValue));
            _client.ApplyCalibration(Calibration(), "fixture-calibration");
            PairAt(ForwardBlindScan(10), 10, 0, Time.realtimeSinceStartupAsDouble - .02);
            Assert.That(_client.MapFreeCellCount, Is.Zero);
            Assert.That(_client.IsMapKnownFree(new Vector2(.725f, -.075f)), Is.False);
            Assert.That(MapDetourPlanner.TryFindRouteBlock(_client.Geometry, _client.Occupancy,
                new[] { Vector2.zero, new Vector2(.3f, 0) }, 0, new MapRobotFootprint(), out var block), Is.True);
            Assert.That(block.Kind, Is.EqualTo(MapPathBlockKind.Unknown),
                "unknown map cells are not free, and accumulated occupied cells are not current obstacles");
            Assert.That(_client.ConfirmedObstacleCellCount, Is.Zero);
            Assert.That(_client.BaseOccupancy, Is.All.EqualTo(storedValue));
        }

        [Test] public void ConfirmedObstacleRevokesMapFreeAcrossCropAndCachedGridUntilDirectlyObservedClear()
        {
            _client.ApplyGrid(KnownFreeGrid());
            _client.ApplyCalibration(Calibration(), "fixture-calibration");
            double now = Time.realtimeSinceStartupAsDouble;
            PairAt(ForwardBlindScan(10), 10, 0, now - .55);
            // y=0 lies just below a grid edge at this float resolution.
            // Use the centre of the endpoint's actual half-open grid cell.
            var hit = new Vector2(.525f, -.025f);
            ConfirmScan(CurrentScan(11, "0.53"), 11, now - .32);
            Assert.That(_client.Occupancy[CellIndex(hit)], Is.EqualTo(100));
            Assert.That(_client.IsMapKnownFree(hit), Is.False);
            Assert.That(_client.DescribeNavigationCell(hit), Does.Contain("current LiDAR obstacle"));
            Assert.That(MapDetourPlanner.TryFindRouteBlock(_client.Geometry, _client.Occupancy,
                new[] { Vector2.zero, new Vector2(.3f, 0) }, 0, new MapRobotFootprint(), out var hitBlock), Is.True);
            Assert.That(hitBlock.Kind, Is.EqualTo(MapPathBlockKind.Occupied), "known free map cells cannot bypass a confirmed current obstacle");
            Assert.That(_client.Texture.GetPixel(CellIndex(hit) % _client.Geometry.width,
                CellIndex(hit) / _client.Geometry.width).r, Is.LessThan(.15));
            _client.ApplyGrid(KnownFreeGrid(width: 62, height: 61, originX: -1.55f));
            Assert.That(_client.Occupancy[CellIndex(hit)], Is.EqualTo(100), "an aligned crop must retain the current real obstacle");
            PairAt(UpwardScan(12), 12, 0, now - .03);
            Assert.That(_client.Occupancy[CellIndex(hit)], Is.EqualTo(-1));
            Assert.That(_client.IsMapKnownFree(hit), Is.False, "cached base0 must not undo a confirmed obstacle's revocation");
            _client.ApplyGrid(KnownFreeGrid(width: 62, height: 61, originX: -1.55f));
            Assert.That(_client.Occupancy[CellIndex(hit)], Is.EqualTo(-1), "repeated map messages cannot resurrect revoked map free space");
            PairAt(FullScan(13, 3), 13, 0, Time.realtimeSinceStartupAsDouble - .02);
            Assert.That(_client.Occupancy[CellIndex(hit)], Is.Zero, "a true current free ray may clear the obstacle revocation");
            RecentProof()[CellIndex(hit)] = Time.realtimeSinceStartupAsDouble - _client.RecentFreeWindowSeconds - .02;
            PairAt(UpwardScan(14), 14, 0, Time.realtimeSinceStartupAsDouble - .01);
            Assert.That(_client.Occupancy[CellIndex(hit)], Is.Zero);
            Assert.That(_client.IsMapKnownFree(hit), Is.True, "only the direct clear observation reauthorizes fallback to known map free space");
        }

        [Test] public void NewSessionDoesNotCarryMapKnownFreeIntoAnUnknownReplacementMap()
        {
            _client.SetSession("known-free-session");
            _client.ApplyGrid(KnownFreeGrid());
            _client.ApplyCalibration(Calibration(), "fixture-calibration");
            PairAt(ForwardBlindScan(10), 10, 0, Time.realtimeSinceStartupAsDouble - .03);
            Assert.That(_client.MapFreeCellCount, Is.GreaterThan(0));
            _client.SetSession("unknown-session");
            Assert.That(_client.MapFreeCellCount, Is.Zero);
            _client.ApplyGrid(KnownFreeGrid(-1));
            _client.ApplyCalibration(Calibration(), "fixture-calibration");
            PairAt(ForwardBlindScan(20), 20, 0, Time.realtimeSinceStartupAsDouble - .02);
            Assert.That(_client.IsMapKnownFree(new Vector2(.725f, -.075f)), Is.False);
            Assert.That(MapDetourPlanner.TryFindRouteBlock(_client.Geometry, _client.Occupancy,
                new[] { Vector2.zero, new Vector2(.3f, 0) }, 0, new MapRobotFootprint(), out var block), Is.True);
            Assert.That(block.Kind, Is.EqualTo(MapPathBlockKind.Unknown));
        }

        [Test] public void IntegerCellCropResizePreservesOriginalFreeTimesAndNeverCopiesOldObstacles()
        {
            double[] proof = PrepareRecentFreeFixture();
            var freeLocation = new Vector2(.2f, 0);
            double observedAt = Time.realtimeSinceStartupAsDouble - .5;
            proof[CellIndex(freeLocation)] = observedAt;
            Assert.That(_client.ApplyGrid(DenseWindowGrid(27, 26, -1.15f, -1.05f)), Is.True);
            int shifted = CellIndex(freeLocation);
            Assert.That(shifted, Is.EqualTo(10 * 27 + 13));
            Assert.That(_client.Occupancy[shifted], Is.Zero,
                "a growing viewport in the same world frame must not discard recently measured free space");
            Assert.That(RecentProof()[shifted], Is.EqualTo(observedAt), "copy the original observation time, never the rebase time");
            Assert.That(_client.Occupancy[0], Is.EqualTo(-1), "newly exposed cells have no recent proof, despite occupied raw source values");
            Assert.That(double.IsNegativeInfinity(RecentProof()[0]), Is.True);

            var hitLocation = new Vector2(0, .8f);
            Assert.That(_client.Occupancy[CellIndex(hitLocation)], Is.EqualTo(100));
            Assert.That(double.IsNegativeInfinity(RecentProof()[CellIndex(hitLocation)]), Is.True,
                "a current obstacle must retain its invalidation of any older free proof during rebase");
            _client.ApplyPose(CurrentPose(12));
            _client.ApplyScan(CurrentScan(12));
            Assert.That(_client.Occupancy[CellIndex(hitLocation)], Is.EqualTo(-1),
                "a later missing ray must not resurrect copied occupied data or pre-obstacle free space");
        }

        [Test] public void IntegerCellCropResizeDoesNotRefreshAlreadyExpiredFreeEvidence()
        {
            double[] proof = PrepareRecentFreeFixture();
            var location = new Vector2(.2f, 0);
            double expiredAt = Time.realtimeSinceStartupAsDouble - _client.RecentFreeWindowSeconds - .02;
            proof[CellIndex(location)] = expiredAt;
            _client.ApplyGrid(DenseWindowGrid(27, 26, -1.15f, -1.05f));
            Assert.That(RecentProof()[CellIndex(location)], Is.EqualTo(expiredAt));
            Assert.That(_client.Occupancy[CellIndex(location)], Is.EqualTo(-1),
                "an integer viewport rebase must not make an expired blind sector navigable");
        }

        [TestCase(-1.0f, .1f)]
        [TestCase(-1.05f, .2f)]
        public void FractionalCellShiftOrResolutionChangeDiscardsOldFreeProof(float originX, float resolution)
        {
            PrepareRecentFreeFixture();
            var location = new Vector2(.2f, 0);
            Assert.That(_client.Occupancy[CellIndex(location)], Is.Zero);
            _client.ApplyGrid(DenseWindowGrid(25, 25, originX, -1.05f, resolution));
            Assert.That(_client.Occupancy[CellIndex(location)], Is.EqualTo(-1),
                "evidence must not be resampled across fractional cells or a changed spatial resolution");
            Assert.That(double.IsNegativeInfinity(RecentProof()[CellIndex(location)]), Is.True);
        }

        [Test] public void AccumulatedGhostGridCannotBlockACurrentlyObservedRobotAndShortRoute()
        {
            _client.ApplyGrid(NavigationGrid(allOccupied: true));
            Assert.That(_client.BaseOccupancy, Is.All.EqualTo(100));
            Assert.That(_client.Occupancy, Is.All.EqualTo(-1));
            _client.ApplyCalibration(Calibration(), "fixture-calibration");
            _client.ApplyPose(CurrentPose(10));
            Assert.That(_client.ApplyScan(FullScan(10, 3)), Is.True);
            var route = new[] { Vector2.zero, new Vector2(.3f, 0) };
            Assert.That(MapDetourPlanner.TryFindRouteBlock(_client.Geometry, _client.Occupancy, route, 0,
                new MapRobotFootprint(), out var block), Is.False,
                "the current scan, including physical footprint and frontal guard, must clear a route despite accumulated ghosts: " + block?.Kind);
            Assert.That(_client.FilterStatus, Does.StartWith("FILTER READY"), "a clear first scan requires no obstacle warmup");
            Assert.That(_client.RawObstacleCellCount, Is.Zero);
            Assert.That(_client.CandidateObstacleCellCount, Is.Zero);
            Assert.That(_client.ConfirmedObstacleCellCount, Is.Zero);
            Assert.That(_client.BaseOccupancy, Is.All.EqualTo(100), "current navigation must not modify the diagnostic source map");
            Assert.That(_client.NavigationLive, Is.False, "pure parser tests must not fabricate a connected transport");

            _client.ApplyGrid(NavigationGrid(allOccupied: true));
            Assert.That(MapDetourPlanner.TryFindRouteBlock(_client.Geometry, _client.Occupancy, route, 0,
                new MapRobotFootprint(), out _), Is.False,
                "a repeated accumulated grid must not resurrect old obstacles over a fresh synchronized scan");
        }

        [Test] public void NextScanReplacesOldReturnsEvenWhenTheOldLocationIsNoLongerObserved()
        {
            _client.ApplyGrid(NavigationGrid(allOccupied: true));
            _client.ApplyCalibration(Calibration(), "fixture-calibration");
            double now = Time.realtimeSinceStartupAsDouble;
            // Three coherent two-cell observations span .20 s per direction.
            // Every original receipt is safely recent and strictly in the past.
            string rightward = CurrentScan(10, "0.83,0.83");
            for (int i = 0; i < 3; i++) PairAt(rightward, 10, i * 100000000, now - .52 + i * .1);
            Assert.That(_client.Occupancy[10 * 25 + 18], Is.EqualTo(100));
            string upward = CurrentScan(11, "0.83,0.83", angleMin: Math.PI / 2);
            for (int i = 0; i < 3; i++) PairAt(upward, 11, i * 100000000, now - .22 + i * .1);
            Assert.That(_client.Occupancy[10 * 25 + 18], Is.EqualTo(-1), "an old return must disappear instead of persisting as a stored obstacle");
            Assert.That(_client.Occupancy[18 * 25 + 10], Is.EqualTo(100), "the repeatedly measured new return remains an actual obstacle");
            Assert.That(_client.Texture.GetPixel(18, 10).r, Is.GreaterThan(.8));
            Assert.That(_client.Texture.GetPixel(10, 18).r, Is.LessThan(.15));
        }

        [Test] public void BriefMissingRaysReuseRecentMeasuredFreeSpaceButConfirmedHitsInvalidateThatProof()
        {
            _client.ApplyGrid(NavigationGrid(allOccupied: true));
            _client.ApplyCalibration(Calibration(), "fixture-calibration");
            double now = Time.realtimeSinceStartupAsDouble;
            PairAt(FullScan(10), 10, 0, now - .45);
            Assert.That(_client.Occupancy[10 * 25 + 12], Is.Zero);
            double originalFreeAt = RecentProof()[18 * 25 + 10];
            PairAt(UpwardScan(11), 11, 0, now - .32);
            Assert.That(_client.Occupancy[10 * 25 + 12], Is.Zero,
                "a temporary missing beam can use genuine recently observed free space, independently of occupied raw cells");
            Assert.That(_client.RecentFreeCellCount, Is.GreaterThan(0));
            Assert.That(_client.IsObstacleCandidate(new Vector2(0, .8f)), Is.True);
            Assert.That(_client.Occupancy[18 * 25 + 10], Is.Zero,
                "one unconfirmed hit must preserve the genuinely recent free observation");
            Assert.That(RecentProof()[18 * 25 + 10], Is.EqualTo(originalFreeAt));
            for (int i = 1; i < 4; i++) PairAt(UpwardScan(11), 11, i * 100000000, now - .32 + i * .08);
            Assert.That(_client.Occupancy[18 * 25 + 10], Is.EqualTo(100),
                "a consistently measured obstacle must override older free-space observations after confirmation");
            Assert.That(double.IsNegativeInfinity(RecentProof()[18 * 25 + 10]), Is.True);
            _client.ApplyPose(CurrentPose(12));
            _client.ApplyScan(CurrentScan(12));
            Assert.That(_client.Occupancy[18 * 25 + 10], Is.EqualTo(-1),
                "after a confirmed hit, a later missing ray must not revive pre-obstacle free proof or retain the old obstacle");
            Assert.That(_client.Occupancy[10 * 25 + 18], Is.Zero,
                "the first new hit still has valid free proof and has not yet been confirmed");
        }

        [Test] public void OneFrameGhostCannotBlockRecentFreeRouteButStableWallDoes()
        {
            _client.ApplyGrid(NavigationGrid(allOccupied: true));
            _client.ApplyCalibration(Calibration(), "fixture-calibration");
            double now = Time.realtimeSinceStartupAsDouble;
            PairAt(FullScan(10), 10, 0, now - .45);
            var route = new[] { Vector2.zero, new Vector2(.3f, 0) };
            var footprint = new MapRobotFootprint();
            string wall = CurrentScan(11, "0.33");
            var endpoint = new Vector2(.3f, 0);
            double freeAt = RecentProof()[CellIndex(endpoint)];
            PairAt(wall, 11, 0, now - .32);
            Assert.That(_client.RawObstacleCellCount, Is.EqualTo(1));
            Assert.That(_client.CandidateObstacleCellCount, Is.EqualTo(1));
            Assert.That(_client.ConfirmedObstacleCellCount, Is.Zero);
            Assert.That(_client.IsObstacleCandidate(endpoint), Is.True);
            Assert.That(RecentProof()[CellIndex(endpoint)], Is.EqualTo(freeAt), "an unconfirmed return must not revoke or refresh real free proof");
            Assert.That(MapDetourPlanner.TryFindRouteBlock(_client.Geometry, _client.Occupancy, route, 0,
                footprint, out _), Is.False, "one-frame garbage must not interrupt a recently measured clear route");
            Assert.That(_client.TryFindConfirmedRouteObstacle(route, 0, footprint, out _), Is.False);
            for (int i = 1; i < 4; i++) PairAt(wall, 11, i * 100000000, now - .32 + i * .08);
            Assert.That(_client.CandidateObstacleCellCount, Is.Zero);
            Assert.That(_client.ConfirmedObstacleCellCount, Is.EqualTo(1));
            Assert.That(double.IsNegativeInfinity(RecentProof()[CellIndex(endpoint)]), Is.True);
            Assert.That(MapDetourPlanner.TryFindRouteBlock(_client.Geometry, _client.Occupancy, route, 0,
                footprint, out var block), Is.True);
            Assert.That(block.Kind, Is.EqualTo(MapPathBlockKind.Occupied));
            Assert.That(_client.TryFindConfirmedRouteObstacle(route, 0, footprint, out _), Is.True);
        }

        [Test] public void FirstDelayedExactPairBuildsFreeLayerWithoutRefreshingItsOriginalReceipt()
        {
            _client.ApplyGrid(NavigationGrid(allOccupied: true));
            _client.ApplyCalibration(Calibration(), "fixture-calibration");
            double receivedAt = Time.realtimeSinceStartupAsDouble - .7;
            PairAt(FullScan(10, 3), 10, 0, receivedAt);
            Assert.That(_client.FilterStatus, Does.StartWith("FILTER READY"),
                "the existing navigation freshness window must admit a delayed exact scan/pose pair without a global filter warmup");
            Assert.That(_client.NavigationObservationStamp, Is.EqualTo(10000000000L));
            Assert.That(_client.NavigationObservationAt, Is.EqualTo(receivedAt),
                "pairing delayed observations must preserve their original receipt time");
            Assert.That(RecentProof()[CellIndex(new Vector2(.2f, 0))], Is.EqualTo(receivedAt));
            Assert.That(_client.NavigationAgeSeconds, Is.GreaterThanOrEqualTo(.7));
            Assert.That(_client.NavigationAgeSeconds, Is.LessThan(1.25));
            Assert.That(_client.RawObstacleCellCount, Is.Zero);
            Assert.That(MapDetourPlanner.TryFindRouteBlock(_client.Geometry, _client.Occupancy,
                new[] { Vector2.zero, new Vector2(.3f, 0) }, 0, new MapRobotFootprint(), out _), Is.False);
            Assert.That(_client.BaseOccupancy, Is.All.EqualTo(100));
            Assert.That(_client.NavigationLive, Is.False, "parser calls still cannot fabricate a network connection");
        }

        [Test] public void RepeatedRawGridCannotPromoteFirstHitOrInventFreeProof()
        {
            _client.ApplyGrid(NavigationGrid(allOccupied: true));
            _client.ApplyCalibration(Calibration(), "fixture-calibration");
            PairAt(CurrentScan(10), 10, 0, Time.realtimeSinceStartupAsDouble - .35);
            var endpoint = new Vector2(.8f, 0);
            Assert.That(_client.Occupancy[CellIndex(endpoint)], Is.EqualTo(-1), "a first hit in unobserved space remains unknown");
            Assert.That(_client.IsObstacleCandidate(endpoint), Is.True);
            double originalAt = (double)typeof(PuppyPiMapDataClient)
                .GetField("_layerScanAt", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_client);
            for (int i = 0; i < 8; i++) Assert.That(_client.ApplyGrid(NavigationGrid(allOccupied: true)), Is.True);
            Assert.That(_client.RawObstacleCellCount, Is.EqualTo(1));
            Assert.That(_client.CandidateObstacleCellCount, Is.EqualTo(1));
            Assert.That(_client.ConfirmedObstacleCellCount, Is.Zero, "grid refreshes replay one observation and cannot count as independent scans");
            Assert.That(_client.Occupancy[CellIndex(endpoint)], Is.EqualTo(-1));
            Assert.That(double.IsNegativeInfinity(RecentProof()[CellIndex(endpoint)]), Is.True);
            Assert.That((double)typeof(PuppyPiMapDataClient).GetField("_layerScanAt", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(_client), Is.EqualTo(originalAt));
        }

        [Test] public void RawGridRefreshDoesNotRestampFreeProofAndFusedCellsExpireWithoutANewScan()
        {
            _client.ApplyGrid(NavigationGrid(allOccupied: true));
            _client.ApplyCalibration(Calibration(), "fixture-calibration");
            _client.ApplyPose(CurrentPose(10));
            _client.ApplyScan(FullScan(10));
            _client.ApplyPose(CurrentPose(11));
            _client.ApplyScan(UpwardScan(11));
            const int index = 10 * 25 + 12;
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var proofField = typeof(PuppyPiMapDataClient).GetField("_recentFreeObservedAt", flags);
            var proof = (double[])proofField.GetValue(_client);
            double observedAt = Time.realtimeSinceStartupAsDouble - .5;
            proof[index] = observedAt;
            _client.ApplyGrid(NavigationGrid(allOccupied: true));
            proof = (double[])proofField.GetValue(_client);
            Assert.That(proof[index], Is.EqualTo(observedAt),
                "reapplying an old matched scan to a new accumulated grid must preserve the original free observation time");
            Assert.That(_client.Occupancy[index], Is.Zero);

            proof[index] = Time.realtimeSinceStartupAsDouble - _client.RecentFreeWindowSeconds - .02;
            typeof(PuppyPiMapDataClient).GetMethod("ExpireRecentFreeEvidence", flags).Invoke(_client, null);
            Assert.That(_client.Occupancy[index], Is.EqualTo(-1),
                "the update expiry path must remove aged fused free cells even if no new laser message arrives");
            Assert.That(_client.BaseOccupancy[index], Is.EqualTo(100));
            Assert.That(_client.Texture.GetPixel(12, 10).r, Is.GreaterThan(.8), "expiry never restores accumulated obstacle debris");
        }

        [Test] public void FreeEvidenceWindowNeverExceedsTheConfiguredNavigationFreshness()
        {
            _client.navigationFreshSeconds = .6f;
            Assert.That(_client.RecentFreeWindowSeconds, Is.LessThanOrEqualTo(.600001));
            _client.navigationFreshSeconds = 1.5f;
            Assert.That(_client.RecentFreeWindowSeconds, Is.EqualTo(1).Within(.000001));
        }

        [Test] public void CalibrationIsReadOnlyAndRequiresExpectedServiceIdTypesAndExplicitValues()
        {
            string request = PuppyPiMapDataClient.CalibrationRequest("fixture-calibration");
            Assert.That(request, Does.Contain("\"op\":\"call_service\"").And.Contain("/lidar_mapper/get_parameters"));
            Assert.That(request, Does.Not.Contain("set_parameters"));
            Assert.Throws<ArgumentException>(() => PuppyPiMapDataClient.CalibrationRequest("bad\"id"));
            Assert.That(_client.ApplyCalibration(Calibration(), "different-id"), Is.False);
            Assert.That(_client.ApplyCalibration(Calibration().Replace("/get_parameters", "/set_parameters"), "fixture-calibration"), Is.False);
            Assert.That(_client.ApplyCalibration(Calibration().Replace("\"double_value\":0.12", "\"other\":0.12"), "fixture-calibration"), Is.False);
            Assert.That(_client.ApplyCalibration(Calibration().Replace("\"double_value\":0.12", "\"double_value\":\"0.12\""), "fixture-calibration"), Is.False);
            Assert.That(_client.ApplyCalibration(Calibration(), "fixture-calibration"), Is.True);
            Assert.That(_client.NavigationLive, Is.False, "parser calls must not fabricate a live network connection");
        }

        [Test] public void CurrentScanAndMapFreePriorRequireTheExactMatchedPose()
        {
            Assert.That(_client.ApplyGrid(NavigationGrid()), Is.True);
            Assert.That(_client.ApplyCalibration(Calibration(), "fixture-calibration"), Is.True);
            double firstAt = Time.realtimeSinceStartupAsDouble - .32;
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Assert.That((bool)typeof(PuppyPiMapDataClient).GetMethod("ApplyScanAt", flags)
                .Invoke(_client, new object[] { CurrentScan(10), firstAt, -1 }), Is.True);
            Assert.That(_client.ApplyPose(CurrentPose(9, 900000000)), Is.True);
            Assert.That(_client.Occupancy[10 * 25 + 12], Is.EqualTo(-1), "a nearby timestamp must not fabricate observed free cells");
            Assert.That((bool)typeof(PuppyPiMapDataClient).GetMethod("ApplyPoseAt", flags)
                .Invoke(_client, new object[] { CurrentPose(10), firstAt, -1 }), Is.True);
            Assert.That(_client.Occupancy[10 * 25 + 18], Is.Zero, "the first unconfirmed return may retain explicit known-map free space");
            Assert.That(_client.IsMapKnownFree(new Vector2(.8f, 0)), Is.True);
            Assert.That(_client.IsObstacleCandidate(new Vector2(.8f, 0)), Is.True);
            for (int i = 1; i < 4; i++) PairAt(CurrentScan(10), 10, i * 100000000, firstAt + i * .08);
            Assert.That(_client.BaseOccupancy[10 * 25 + 12], Is.EqualTo(100));
            Assert.That(_client.Occupancy[10 * 25 + 12], Is.Zero, "only the measured free ray clears the old point");
            Assert.That(_client.Occupancy[12 * 25 + 12], Is.EqualTo(-1), "an old off-ray obstacle is not a current obstacle");
            Assert.That(_client.Occupancy[12 * 25 + 10], Is.EqualTo(-1), "unobserved unknown cells stay blocked");
            Assert.That(_client.Occupancy[10 * 25 + 18], Is.EqualTo(100), "the current endpoint is an obstacle");
            Assert.That(_client.Texture.width, Is.EqualTo(_client.Geometry.width));
            Assert.That(_client.Texture.GetPixel(18, 10).r, Is.LessThan(.15));
            Assert.That(_client.Texture.GetPixel(12, 12).r, Is.GreaterThan(.8), "stored obstacle debris must not be redrawn");
            Assert.That(_client.Texture.GetPixel(10, 12).b, Is.GreaterThan(_client.Texture.GetPixel(10, 12).r), "unobserved cells are visibly blue-grey");
            Assert.That(_client.DescribeNavigationCell(new Vector2(.8f, 0)), Does.Contain("current LiDAR obstacle"));
            Assert.That(_client.DescribeNavigationCell(new Vector2(.2f, .2f)), Does.Contain("not observed in current scan"));
            Assert.That(_client.DescribeNavigationCell(new Vector2(0, .2f)), Does.Contain("not observed in current scan"));
            Assert.That(_client.DescribeNavigationCell(new Vector2(.2f, 0)), Does.Contain("free space"));
        }

        [Test] public void NullRaysAndReturnsBelowMapperMinimumNeitherClearNorAddObstacles()
        {
            _client.ApplyGrid(NavigationGrid());
            _client.ApplyCalibration(Calibration(), "fixture-calibration");
            _client.ApplyPose(CurrentPose(10));
            Assert.That(_client.ApplyScan(CurrentScan(10, "null")), Is.True);
            Assert.That(_client.Occupancy, Is.All.EqualTo(-1));
            _client.ApplyPose(CurrentPose(11));
            Assert.That(_client.ApplyScan(CurrentScan(11, "0.05")), Is.True);
            Assert.That(_client.Occupancy, Is.All.EqualTo(-1),
                "a body echo below the mapper cutoff must neither add an obstacle nor fabricate free space");
        }

        [Test] public void MixedValidRaysAndBodyEchoesUseIdenticalMapperRangeFilterForNavigationAndDisplay()
        {
            _client.ApplyGrid(NavigationGrid());
            _client.ApplyCalibration(Calibration(), "fixture-calibration");
            string scan = CurrentScan(10, "0.05,0.83");
            ConfirmScan(scan, 10, Time.realtimeSinceStartupAsDouble - .32);
            Assert.That(_client.Occupancy[10 * 25 + 10], Is.Zero, "ignored near echo adds no obstacle");
            Assert.That(_client.Occupancy[10 * 25 + 12], Is.Zero, "the valid ray still clears measured free space");
            Assert.That(_client.Occupancy[11 * 25 + 18], Is.EqualTo(100), "valid external return remains occupied");
            Assert.That(_client.Texture.GetPixel(10, 10).r, Is.GreaterThan(.8), "ignored echo is absent from the recent-scan display too");
            Assert.That(_client.Texture.GetPixel(18, 11).r, Is.LessThan(.15));
        }

        [Test] public void MapperMaximumAndSensorMinimumBothConstrainScanEvidence()
        {
            _client.ApplyGrid(NavigationGrid());
            _client.ApplyCalibration(Calibration().Replace("\"double_value\":11.5", "\"double_value\":0.4"), "fixture-calibration");
            _client.ApplyPose(CurrentPose(10));
            _client.ApplyScan(CurrentScan(10, "0.83"));
            Assert.That(_client.Occupancy, Is.All.EqualTo(-1), "beyond-mapper-limit rays supply neither free nor occupied evidence");
            _client.ApplyCalibration(Calibration().Replace("\"double_value\":0.12", "\"double_value\":0.01"), "fixture-calibration");
            _client.ApplyPose(CurrentPose(11));
            _client.ApplyScan(CurrentScan(11, "0.015"));
            Assert.That(_client.Occupancy[10 * 25 + 10], Is.EqualTo(-1), "a mapper minimum below the sensor limit does not authorize invalid sensor returns");
        }

        [Test] public void ReplayedScanCannotRefreshEvidenceAndChangedCalibrationDiscardsLayer()
        {
            _client.ApplyGrid(NavigationGrid());
            _client.ApplyCalibration(Calibration(), "fixture-calibration");
            _client.ApplyPose(CurrentPose(10));
            _client.ApplyScan(CurrentScan(10));
            Assert.That(_client.Occupancy[10 * 25 + 12], Is.Zero);
            var layerAt = typeof(PuppyPiMapDataClient).GetField("_layerScanAt", BindingFlags.Instance | BindingFlags.NonPublic);
            double originalAt = (double)layerAt.GetValue(_client);
            Assert.That(_client.ApplyScan(CurrentScan(10)), Is.False);
            Assert.That((double)layerAt.GetValue(_client), Is.EqualTo(originalAt), "a duplicate never refreshes evidence age");
            Assert.That(_client.Occupancy[10 * 25 + 12], Is.Zero, "a duplicate does not delete still-recent verified evidence");
            _client.ApplyPose(CurrentPose(11)); _client.ApplyScan(CurrentScan(11));
            Assert.That(_client.Occupancy[10 * 25 + 12], Is.Zero);
            Assert.That(_client.ApplyCalibration(Calibration(x: "0.1"), "fixture-calibration"), Is.True);
            Assert.That(_client.Occupancy[10 * 25 + 12], Is.EqualTo(-1));
            _client.ApplyScan(CurrentScan(12));
            Assert.That(_client.Occupancy[10 * 25 + 12], Is.EqualTo(-1), "old pose history cannot cross a calibration change");
        }

        [Test] public void StaleReceiptAndWrongScanEpochCannotCreateNavigationLayer()
        {
            _client.ApplyGrid(NavigationGrid());
            _client.ApplyCalibration(Calibration(), "fixture-calibration");
            _client.ApplyPose(CurrentPose(10));
            var apply = typeof(PuppyPiMapDataClient).GetMethod("ApplyScanAt", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That((bool)apply.Invoke(_client, new object[] { CurrentScan(10), Time.realtimeSinceStartupAsDouble - 2, -1 }), Is.False);
            Assert.That((bool)apply.Invoke(_client, new object[] { CurrentScan(11), Time.realtimeSinceStartupAsDouble, 99 }), Is.True);
            Assert.That(_client.Occupancy[10 * 25 + 12], Is.EqualTo(-1));
            Assert.That(_client.NavigationLive, Is.False);
        }

        [Test] public void OneScanLatePosePairsPendingFrameBeforeTheNextScanReplacesIt()
        {
            _client.ApplyGrid(NavigationGrid());
            _client.ApplyCalibration(Calibration(), "fixture-calibration");
            _client.ApplyScan(CurrentScan(10));
            Assert.That(_client.Occupancy[10 * 25 + 12], Is.EqualTo(-1));
            for (int stamp = 10; stamp < 14; stamp++)
            {
                Assert.That(_client.ApplyPose(CurrentPose(stamp)), Is.True);
                Assert.That(_client.Occupancy[10 * 25 + 12], Is.Zero, "pose N must pair pending scan N before the batch's scan N+1");
                long matched = (long)typeof(PuppyPiMapDataClient).GetField("_layerScanStamp", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_client);
                Assert.That(matched, Is.EqualTo(stamp * 1000000000L), "continuous one-frame callback lag must advance the verified scan");
                Assert.That(_client.ApplyScan(CurrentScan(stamp + 1)), Is.True);
                Assert.That(_client.Occupancy[10 * 25 + 12], Is.Zero, "a newer waiting scan must not discard the still-fresh verified frame");
            }
        }

        [Test] public void SeveralNewerScansDoNotEraseAnOlderExactPairWaitingForItsPose()
        {
            _client.ApplyGrid(NavigationGrid());
            _client.ApplyCalibration(Calibration(), "fixture-calibration");
            for (int stamp = 10; stamp <= 13; stamp++) _client.ApplyScan(CurrentScan(stamp));
            Assert.That(_client.ApplyPose(CurrentPose(10)), Is.True);
            var paired = typeof(PuppyPiMapDataClient).GetField("_layerScanStamp", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That((long)paired.GetValue(_client), Is.EqualTo(10000000000L));
            _client.ApplyPose(CurrentPose(12));
            Assert.That((long)paired.GetValue(_client), Is.EqualTo(12000000000L));
            _client.ApplyPose(CurrentPose(13));
            Assert.That((long)paired.GetValue(_client), Is.EqualTo(13000000000L));
            Assert.That(_client.BaseOccupancy[10 * 25 + 12], Is.EqualTo(100));
            Assert.That(_client.Occupancy[10 * 25 + 12], Is.Zero);
        }

        [Test] public void NavigationExpiryClearsCurrentCellsWithoutRestoringStoredDebris()
        {
            _client.SetSession("cached-map-fixture");
            _client.ApplyGrid(NavigationGrid());
            _client.ApplyCalibration(Calibration(), "fixture-calibration");
            ConfirmScan(CurrentScan(10), 10, Time.realtimeSinceStartupAsDouble - .32);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            double old = Time.realtimeSinceStartupAsDouble - 1.5;
            typeof(PuppyPiMapDataClient).GetField("_layerScanAt", flags).SetValue(_client, old);
            typeof(PuppyPiMapDataClient).GetField("_layerPoseAt", flags).SetValue(_client, old);
            var picture = _client.Texture;
            Assert.That(picture.GetPixel(18, 10).r, Is.LessThan(.15));
            typeof(PuppyPiMapDataClient).GetMethod("RefreshNavigationFreshness", flags).Invoke(_client, null);
            Assert.That(_client.NavigationLive, Is.False);
            Assert.That(_client.Occupancy, Is.All.EqualTo(-1), "expired observations cannot turn into stored map obstacles");
            Assert.That(_client.BaseOccupancy[10 * 25 + 12], Is.EqualTo(100), "the diagnostic raw grid remains unchanged");
            Assert.That(_client.EditingAvailable, Is.True, "sensor expiry is not a map identity reset");
            Assert.That(_client.Texture, Is.SameAs(picture));
            Assert.That(_client.Texture.GetPixel(18, 10).r, Is.GreaterThan(.8), "expired scan endpoints must disappear");
            Assert.That(_client.Texture.GetPixel(12, 10).r, Is.GreaterThan(.8), "stored obstacles never reappear after expiry");
            Assert.That(_client.NavigationStatus, Does.Contain("NO SIGNAL"));
        }

        [Test] public void PendingScanStorageIsBoundedAndDroppedFramesCannotLaterBeMatched()
        {
            _client.ApplyGrid(NavigationGrid());
            _client.ApplyCalibration(Calibration(), "fixture-calibration");
            for (int stamp = 1; stamp <= 40; stamp++) _client.ApplyScan(CurrentScan(stamp));
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            object pending = typeof(PuppyPiMapDataClient).GetField("_pendingScans", flags).GetValue(_client);
            Assert.That((int)pending.GetType().GetProperty("Count").GetValue(pending), Is.LessThanOrEqualTo(16));
            _client.ApplyPose(CurrentPose(1));
            Assert.That(_client.Occupancy[10 * 25 + 12], Is.EqualTo(-1));
            _client.ApplyPose(CurrentPose(40));
            Assert.That(_client.Occupancy[10 * 25 + 12], Is.Zero);
        }
    }
}
#endif
