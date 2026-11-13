#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace PuppyPi.Tests
{
    // This fixture enters the real component's revalidation method with live
    // map state, but creates no drive component, socket, or receive task.
    public class PathNoiseHandlingTests
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject _root;
        private PuppyPiMapDataClient _map;
        private PuppyPiPathClient _path;
        private readonly Vector2[] _original = { Vector2.zero, new Vector2(.30f, 0) };
        private static double Now => Time.realtimeSinceStartupAsDouble;
        private static readonly MapGridGeometry Grid = new MapGridGeometry {
            width = 40, height = 40, resolution = .05f, origin = new Vector2(-1, -1), frame = "map"
        };
        private static int ObstacleIndex => 20 * Grid.width + 31;
        private static int UnknownIndex => 20 * Grid.width + 27;

        private static void Set(object instance, string field, object value) =>
            instance.GetType().GetField(field, Hidden).SetValue(instance, value);
        private static T Get<T>(object instance, string field) =>
            (T)instance.GetType().GetField(field, Hidden).GetValue(instance);
        private static void Property(object instance, string property, object value) =>
            instance.GetType().GetProperty(property).SetValue(instance, value);
        private string Phase => Get<object>(_path, "_phase").ToString();

        [SetUp]
        public void Setup()
        {
            _root = new GameObject("path noise fixture; no transport");
            _root.SetActive(false);
            _map = _root.AddComponent<PuppyPiMapDataClient>();
            var controller = _root.AddComponent<PuppyPiRobotController>();
            _path = _root.AddComponent<PuppyPiPathClient>();
            // Historical strict-space regressions remain explicit coverage;
            // supervised cases below opt into the new default independently.
            _path.operatorSupervisedNavigation = false;
            controller.mapData = _map;
            _path.controller = controller;

            Property(_map, "Geometry", Grid);
            Property(_map, "Texture", new Texture2D(2, 2));
            Property(_map, "BaseOccupancy", new int[Grid.width * Grid.height]);
            Property(_map, "Occupancy", new int[Grid.width * Grid.height]);
            Property(_map, "SessionId", "noise-fixture");
            Property(_map, "RobotPose", Vector3.zero);
            var transportType = typeof(PuppyPiMapDataClient).GetNestedType("Transport", BindingFlags.NonPublic);
            object transport = Activator.CreateInstance(transportType, true);
            transportType.GetField("Connected").SetValue(transport, true);
            transportType.GetField("Epoch").SetValue(transport, 1);
            Set(_map, "_transport", transport);
            var calibrationType = typeof(PuppyPiMapDataClient).GetNestedType("Calibration", BindingFlags.NonPublic);
            object calibration = Activator.CreateInstance(calibrationType, true);
            calibrationType.GetField("MapFrame").SetValue(calibration, "map");
            calibrationType.GetField("BaseFrame").SetValue(calibration, "base_footprint");
            Set(_map, "_calibration", calibration);
            foreach (string field in new[] { "_gridEpoch", "_poseEpoch", "_calibrationEpoch", "_layerEpoch", "_activeEpoch" })
                Set(_map, field, 1);
            Set(_map, "_baseRevision", 1L);
            Set(_map, "_layerBaseRevision", 1L);
            Set(_map, "_gridAt", Now);
            Set(_map, "_hasLayer", true);
            Set(_map, "_confirmedObstacleEvidence", new int[Grid.width * Grid.height]);

            Type phaseType = typeof(PuppyPiPathClient).GetField("_phase", Hidden).FieldType;
            Set(_path, "_phase", Enum.Parse(phaseType, "Running"));
            Get<List<Vector2>>(_path, "_points").AddRange(_original);
            Get<List<Vector2>>(_path, "_remaining").AddRange(_original);
            Property(_path, "RemoteState", "drive");
            Set(_path, "_frame", "map");
            Set(_path, "_session", "noise-fixture");
            Observation(1, Now - .02);
            Assert.That(_map.NavigationLive, Is.True, "the production map freshness checks must be satisfied");
            Assert.That(Get<object>(_path, "_drive"), Is.Null, "this fixture has no command transport");
        }

        [TearDown]
        public void Cleanup()
        {
            if (_path != null)
            {
                Type phaseType = typeof(PuppyPiPathClient).GetField("_phase", Hidden).FieldType;
                Set(_path, "_phase", Enum.Parse(phaseType, "Idle"));
            }
            if (_map != null) _map.Disconnect();
            if (_root != null) UnityEngine.Object.DestroyImmediate(_root);
        }

        private void Observation(long stamp, double at)
        {
            Set(_map, "_layerScanStamp", stamp);
            Set(_map, "_poseStamp", stamp);
            foreach (string field in new[] { "_layerScanAt", "_layerPoseAt", "_poseAt", "_lastScanAt" })
                Set(_map, field, at);
        }

        private void Revalidate()
        {
            Assert.That(_map.NavigationLive, Is.True);
            typeof(PuppyPiPathClient).GetMethod("ObserveRemainingObstacles", Hidden).Invoke(_path, null);
        }

        private void AssertOriginalRouteIsRunning()
        {
            Assert.That(Phase, Is.EqualTo("Running"));
            CollectionAssert.AreEqual(_original, _path.Points);
            Assert.That(Get<bool>(_path, "_detourPending"), Is.False);
            Assert.That(Get<int>(_path, "_detoursUsed"), Is.Zero);
            Assert.That(Get<object>(_path, "_drive"), Is.Null);
        }

        [Test]
        public void VerifiedFreeRouteKeepsExistingFreshnessWindowDuringBriefScanDelay()
        {
            Observation(2, Now - .70);
            Assert.That(_map.NavigationAgeSeconds, Is.GreaterThan(.60));
            Assert.That(_map.BriefSensorGap, Is.True);
            Revalidate();
            AssertOriginalRouteIsRunning();
        }

        [Test]
        public void RawBlockedSnapshotWithoutConfirmedObstacleKeepsTheOriginalRoute()
        {
            Property(_path, "RemoteState", "blocked");
            double at = Now;
            Observation(2, at - .30);
            Revalidate();
            AssertOriginalRouteIsRunning();
            Observation(3, at - .03);
            Revalidate();
            AssertOriginalRouteIsRunning();
            Assert.That(_path.Status, Does.Contain("VERIFYING SENSOR"));
        }

        [Test]
        public void CandidateHitOverStillVerifiedFreeSpaceDoesNotCancelOrRedrawTheRoute()
        {
            Get<HashSet<int>>(_map, "_candidateObstacleCells").Add(ObstacleIndex);
            var candidate = new Vector2(.575f, .025f);
            Assert.That(_map.IsObstacleCandidate(candidate), Is.True);
            Assert.That(_map.Occupancy[ObstacleIndex], Is.Zero, "recent measured-free evidence is retained under this candidate");
            Observation(2, Now - .02);
            Revalidate();
            AssertOriginalRouteIsRunning();
        }

        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void SupervisionKeepsUnknownRouteAndRobotSurroundingsRunnable(bool unknownRoute, bool unknownBody)
        {
            _path.operatorSupervisedNavigation = true;
            int body = 20 * Grid.width + 20;
            if (unknownRoute) _map.Occupancy[UnknownIndex] = -1;
            if (unknownBody) _map.Occupancy[body] = -1;
            Observation(2, Now - .02);
            Revalidate();
            AssertOriginalRouteIsRunning();
            if (unknownRoute) Assert.That(_map.Occupancy[UnknownIndex], Is.EqualTo(-1));
            if (unknownBody) Assert.That(_map.Occupancy[body], Is.EqualTo(-1));
        }

        [Test]
        public void SupervisionDoesNotTurnAnUnconfirmedHitInUnknownSpaceIntoAStop()
        {
            _path.operatorSupervisedNavigation = true;
            _map.Occupancy[ObstacleIndex] = -1;
            Get<HashSet<int>>(_map, "_candidateObstacleCells").Add(ObstacleIndex);
            Observation(2, Now - .02);
            Revalidate();
            AssertOriginalRouteIsRunning();
            Assert.That(_map.Occupancy[ObstacleIndex], Is.EqualTo(-1));
            Assert.That(Get<int[]>(_map, "_confirmedObstacleEvidence")[ObstacleIndex], Is.Zero);
        }

        [Test]
        public void SupervisionDoesNotReplanForAnOffRouteObstacleBesideUnknownSpace()
        {
            _path.operatorSupervisedNavigation = true;
            _map.Occupancy[UnknownIndex] = -1;
            int offRoute = 32 * Grid.width + 31;
            _map.Occupancy[offRoute] = 100;
            Get<int[]>(_map, "_confirmedObstacleEvidence")[offRoute] = 100;
            Observation(2, Now - .02);
            Revalidate();
            AssertOriginalRouteIsRunning();
        }

        [TestCase(true, "Running")]
        [TestCase(false, "Cancelling")]
        public void OnlyStrictModeStopsForASustainedRemoteBlockedStateWithoutConfirmedEvidence(
            bool supervised, string expectedPhase)
        {
            _path.operatorSupervisedNavigation = supervised;
            Property(_path, "RemoteState", "blocked");
            double earlier = Now - MapObstacleReplanGate.MaximumUnconfirmedBlockedSeconds - .1;
            var gate = Get<MapObstacleReplanGate>(_path, "_obstacleGate");
            Assert.That(gate.Observe(1, earlier, earlier, null, true), Is.EqualTo(MapObstacleDecision.Verifying));
            Observation(2, Now - .02);
            Revalidate();
            Assert.That(Phase, Is.EqualTo(expectedPhase));
            Assert.That(Get<bool>(_path, "_detourPending"), Is.False);
            CollectionAssert.AreEqual(_original, _path.Points);
        }

        [Test]
        public void SupervisionStillConfirmsAndCancelsForARealRouteObstacleBehindUnknownSpace()
        {
            _path.operatorSupervisedNavigation = true;
            ConfirmedObstacleBehindUnknownRouteCell();
            double at = Now;
            Observation(2, at - .50);
            Revalidate();
            AssertOriginalRouteIsRunning();
            Observation(3, at - .27);
            Revalidate();
            AssertOriginalRouteIsRunning();
            Observation(4, at - .04);
            Revalidate();
            Assert.That(Phase, Is.EqualTo("Cancelling"));
            Assert.That(Get<bool>(_path, "_detourPending"), Is.True);
            Assert.That(Get<bool>(_path, "_verifyObstacleAfterStop"), Is.False,
                "unknown space must not introduce an extra stopped verification stage in supervised mode");
            Assert.That(_map.Occupancy[UnknownIndex], Is.EqualTo(-1));
            CollectionAssert.AreEqual(_original, _path.Points);
        }

        [Test]
        public void SupervisionStillStopsForAConfirmedObstacleInsideTheRobotBody()
        {
            _path.operatorSupervisedNavigation = true;
            int body = 20 * Grid.width + 20;
            _map.Occupancy[body] = 100;
            Get<int[]>(_map, "_confirmedObstacleEvidence")[body] = 100;
            Observation(2, Now - .02);
            Revalidate();
            Assert.That(Phase, Is.EqualTo("Cancelling"));
            Assert.That(Get<bool>(_path, "_detourPending"), Is.False);
            Assert.That(_path.Status, Does.Contain("Robot area"));
        }

        [Test]
        public void PersistentConfirmedRouteObstacleRequiresDistinctObservationsBeforeCancellation()
        {
            _map.Occupancy[ObstacleIndex] = 100;
            Get<int[]>(_map, "_confirmedObstacleEvidence")[ObstacleIndex] = 100;
            double at = Now;
            Observation(2, at - .50);
            Revalidate();
            AssertOriginalRouteIsRunning();
            Observation(3, at - .27);
            Revalidate();
            AssertOriginalRouteIsRunning();
            Observation(4, at - .04);
            Revalidate();
            Assert.That(Phase, Is.EqualTo("Cancelling"));
            Assert.That(Get<bool>(_path, "_detourPending"), Is.True);
            CollectionAssert.AreEqual(_original, _path.Points, "planning/replacement still waits for the stop barrier");
            Assert.That(Get<int>(_path, "_detoursUsed"), Is.Zero);
            Assert.That(Get<HashSet<long>>(_path, "_cancelSequences"), Is.Empty);
            Assert.That(Get<object>(_path, "_drive"), Is.Null, "cancellation cannot send without a drive transport");
        }

        private void ConfirmedObstacleBehindUnknownRouteCell()
        {
            _map.Occupancy[UnknownIndex] = -1;
            _map.Occupancy[ObstacleIndex] = 100;
            Get<int[]>(_map, "_confirmedObstacleEvidence")[ObstacleIndex] = 100;
            Assert.That(MapDetourPlanner.TryFindRouteBlock(Grid, _map.Occupancy, _original, 0,
                _path.Footprint, out var first), Is.True);
            Assert.That(first.Kind, Is.EqualTo(MapPathBlockKind.Unknown),
                "the mixed navigation mask must encounter the unknown cell before the real obstacle");
            Assert.That(_map.TryFindConfirmedRouteObstacle(_original, 0, _path.Footprint, out var confirmed), Is.True);
            Assert.That(confirmed.Kind, Is.EqualTo(MapPathBlockKind.Occupied),
                "independent current evidence must establish a real route obstacle");
        }

        [Test]
        public void UnknownCellBeforeConfirmedObstacleStopsImmediatelyAndVerifiesOnlyAfterStop()
        {
            ConfirmedObstacleBehindUnknownRouteCell();
            double at = Now;
            Observation(2, at - .50);
            Revalidate();
            Assert.That(Phase, Is.EqualTo("Cancelling"),
                "the robot must immediately stop instead of driving through unknown space while votes accumulate");
            Assert.That(Get<bool>(_path, "_detourPending"), Is.True);
            Assert.That(Get<bool>(_path, "_verifyObstacleAfterStop"), Is.True);
            Assert.That(VerifyStopped(), Is.False, "verification cannot run before the acknowledged stop phase");
            Assert.That(Get<List<double>>(Get<MapObstacleReplanGate>(_path, "_obstacleGate"), "_observedAt"), Is.Empty);
            EnterStoppedVerification(at - .60);
            Assert.That(VerifyStopped(), Is.False);
            Observation(3, at - .27);
            Assert.That(VerifyStopped(), Is.False);
            Observation(4, at - .04);
            Assert.That(VerifyStopped(), Is.True);
            Assert.That(Phase, Is.EqualTo("Replanning"), "verification alone must not submit a replacement datagram");
            Assert.That(Get<bool>(_path, "_detourPending"), Is.True);
            Assert.That(Get<bool>(_path, "_verifyObstacleAfterStop"), Is.False);
            Assert.That(_map.Occupancy[UnknownIndex], Is.EqualTo(-1),
                "recognizing a confirmed obstacle must not manufacture free-space evidence");
            CollectionAssert.AreEqual(_original, _path.Points);
        }

        [Test]
        public void UnknownRouteWithoutConfirmedObstacleStillStopsWithoutRequestingDetour()
        {
            _map.Occupancy[UnknownIndex] = -1;
            Observation(2, Now - .02);
            Revalidate();
            Assert.That(Phase, Is.EqualTo("Cancelling"));
            Assert.That(Get<bool>(_path, "_detourPending"), Is.False);
            Assert.That(_path.Status, Does.Contain("Route observation is incomplete"));
            Assert.That(_map.Occupancy[UnknownIndex], Is.EqualTo(-1));
        }

        [Test]
        public void AnOffRouteConfirmedObstacleCannotAuthorizeABypassForUnknownRouteSpace()
        {
            _map.Occupancy[UnknownIndex] = -1;
            int offRoute = 32 * Grid.width + 31;
            _map.Occupancy[offRoute] = 100;
            Get<int[]>(_map, "_confirmedObstacleEvidence")[offRoute] = 100;
            Assert.That(_map.TryFindConfirmedRouteObstacle(_original, 0, _path.Footprint, out _), Is.False);
            Observation(2, Now - .02);
            Revalidate();
            Assert.That(Phase, Is.EqualTo("Cancelling"));
            Assert.That(Get<bool>(_path, "_detourPending"), Is.False);
            Assert.That(_path.Status, Does.Contain("Route observation is incomplete"));
        }

        [Test]
        public void UnknownRobotBodyStillStopsBeforeConsideringAConfirmedRouteObstacle()
        {
            ConfirmedObstacleBehindUnknownRouteCell();
            _map.Occupancy[20 * Grid.width + 20] = -1;
            Observation(2, Now - .02);
            Revalidate();
            Assert.That(Phase, Is.EqualTo("Cancelling"));
            Assert.That(Get<bool>(_path, "_detourPending"), Is.False);
            Assert.That(_path.Status, Does.Contain("Robot area is not verified"));
        }

        [Test]
        public void MixedUnknownAndConfirmedEvidenceCannotBypassTheExistingFreshnessLimit()
        {
            ConfirmedObstacleBehindUnknownRouteCell();
            Observation(2, Now - .02);
            Revalidate();
            EnterStoppedVerification(Now - .70);
            Observation(3, Now - .65);
            Assert.That(VerifyStopped(), Is.False);
            Assert.That(Phase, Is.EqualTo("Idle"));
            Assert.That(Get<bool>(_path, "_detourPending"), Is.False);
            Assert.That(_path.Status, Does.Contain("Current obstacle evidence could not be verified"));
        }

        private void EnterStoppedVerification(double at)
        {
            Type phaseType = typeof(PuppyPiPathClient).GetField("_phase", Hidden).FieldType;
            Set(_path, "_phase", Enum.Parse(phaseType, "Replanning"));
            Set(_path, "_stopVerifiedAt", at);
        }

        private bool VerifyStopped() => (bool)typeof(PuppyPiPathClient)
            .GetMethod("VerifyStoppedObstacle", Hidden).Invoke(_path, null);

        [Test]
        public void PreStopAndDuplicateObservationsCannotCompleteStoppedVerification()
        {
            ConfirmedObstacleBehindUnknownRouteCell();
            Revalidate();
            EnterStoppedVerification(Now - .05);
            Observation(2, Now - .10);
            Assert.That(VerifyStopped(), Is.False);
            var gate = Get<MapObstacleReplanGate>(_path, "_obstacleGate");
            Assert.That(Get<List<double>>(gate, "_observedAt"), Is.Empty);
            EnterStoppedVerification(Now - .60);
            Observation(3, Now - .40);
            Assert.That(VerifyStopped(), Is.False);
            Assert.That(VerifyStopped(), Is.False);
            Assert.That(VerifyStopped(), Is.False);
            Assert.That(Get<List<double>>(gate, "_observedAt").Count, Is.EqualTo(1));
            Assert.That(Phase, Is.EqualTo("Replanning"));
            Assert.That(Get<bool>(_path, "_verifyObstacleAfterStop"), Is.True);
        }

        [Test]
        public void OperatorStopRevokesPendingVerificationWithoutRestartingTheStopTimer()
        {
            ConfirmedObstacleBehindUnknownRouteCell();
            Revalidate();
            double began = Get<double>(_path, "_cancelAt");
            _path.Cancel("operator STOP");
            Assert.That(Phase, Is.EqualTo("Cancelling"));
            Assert.That(Get<bool>(_path, "_verifyObstacleAfterStop"), Is.False);
            Assert.That(Get<bool>(_path, "_detourPending"), Is.False);
            Assert.That(Get<double>(_path, "_cancelAt"), Is.EqualTo(began));
        }

        [Test]
        public void StoppedVerificationRemainsStoppedWhenItsExistingThreeSecondBudgetExpires()
        {
            ConfirmedObstacleBehindUnknownRouteCell();
            Revalidate();
            EnterStoppedVerification(Now - 3.01);
            Assert.That(VerifyStopped(), Is.False);
            Assert.That(Phase, Is.EqualTo("Idle"));
            Assert.That(Get<bool>(_path, "_detourPending"), Is.False);
            Assert.That(Get<bool>(_path, "_verifyObstacleAfterStop"), Is.False);
            Assert.That(_path.Status, Does.Contain("could not be verified after stopping"));
        }

        [Test]
        public void AConfirmedObstacleDoesNotPermitPlanningThroughAnUnknownBarrier()
        {
            ConfirmedObstacleBehindUnknownRouteCell();
            for (int y = 0; y < Grid.height; y++) _map.Occupancy[y * Grid.width + 27] = -1;
            var route = new[] { Vector2.zero, new Vector2(.8f, 0) };
            Assert.That(MapDetourPlanner.TryPlanDetailed(Grid, _map.Occupancy, Vector3.zero,
                route, _path.Footprint, out _, out _, out string error), Is.False,
                "a complete unknown barrier cannot be crossed to reach the confirmed obstacle's far side");
            Assert.That(error, Is.Not.Null.And.Not.Empty);
            for (int y = 0; y < Grid.height; y++)
                Assert.That(_map.Occupancy[y * Grid.width + 27], Is.EqualTo(-1));
        }
    }
}
#endif
