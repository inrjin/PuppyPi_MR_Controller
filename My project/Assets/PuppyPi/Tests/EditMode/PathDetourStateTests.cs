using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace PuppyPi.Tests
{
    // Received-state tests only: the inactive fixture has no controller, drive
    // client or transport. A confirmed stop therefore cannot submit a route.
    public class PathDetourStateTests
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject _root;
        private PuppyPiPathClient _path;
        private static double Now => Time.realtimeSinceStartupAsDouble;
        private static string Stopped => PathStatusTests.Sample.Replace("\"state\":\"drive\"", "\"state\":\"cancelled\"");

        [SetUp] public void Setup()
        {
            _root = new GameObject("detour received-state fixture");
            _root.SetActive(false);
            _path = _root.AddComponent<PuppyPiPathClient>();
            _path.operatorSupervisedNavigation = false; // Legacy limits remain covered as an explicit mode.
            Phase("Running");
            Set("_pathSent", true);
            Set("_sentAt", Now - 1);
            Set("_frame", "map");
            Set("_session", "origin-a");
            Points("_points").AddRange(new[] { new Vector2(.1234f, -.25f), new Vector2(1.5f, 2.75f) });
            Points("_intent").AddRange(Points("_points"));
            Invoke("RefreshRemaining");
        }

        [TearDown] public void Cleanup()
        {
            Phase("Idle");
            Get<PuppyPiV2Client>("_drive")?.SetPathCommandHold(false);
            UnityEngine.Object.DestroyImmediate(_root);
        }

        private FieldInfo Field(string name) => typeof(PuppyPiPathClient).GetField(name, Hidden);
        private T Get<T>(string name) => (T)Field(name).GetValue(_path);
        private void Set(string name, object value) => Field(name).SetValue(_path, value);
        private List<Vector2> Points(string name) => Get<List<Vector2>>(name);
        private object Invoke(string name, params object[] args) =>
            typeof(PuppyPiPathClient).GetMethod(name, Hidden).Invoke(_path, args);
        private void Phase(string value) => Set("_phase", Enum.Parse(Field("_phase").FieldType, value));
        private string PhaseName => Field("_phase").GetValue(_path).ToString();
        private bool Observe(string json, double at) => (bool)Invoke("ApplyStatus", json, at);
        private void Obstacle() => Invoke("RequestObstacleDetour", "test obstacle");
        private void Receipt(long sequence, bool accepted = true, string kind = "path_cancel", bool registered = true)
        {
            if (registered) Get<HashSet<long>>("_cancelSequences").Add(sequence);
            Invoke("OnPathAck", new V2Ack { protocol = 2, type = "ack", client_id = "fixture",
                sequence = sequence, accepted = accepted, state = "idle", reason = accepted ? "ok" : "rejected" }, kind);
        }

        [Test]
        public void IntentWithoutCurrentPoseAnchorAdvancesCornersInOrderAndKeepsALaterReturn()
        {
            var anchor = Vector2.zero;
            var corner = new Vector2(1, 0);
            var laterReturn = new Vector2(.02f, 0);
            var goal = new Vector2(1, 1);
            Points("_points").Clear();
            Points("_points").AddRange(new[] { anchor, corner, laterReturn, goal });
            Points("_intent").Clear();
            Points("_intent").AddRange(new[] { corner, laterReturn, goal });
            Invoke("AdvanceIntent", 1);
            Assert.That(Get<int>("_intentCursor"), Is.Zero, "the current pose anchor is not a user goal");
            Invoke("AdvanceIntent", 2);
            Assert.That(Get<int>("_intentCursor"), Is.EqualTo(1));
            Vector2[] pending = MapPathGeometry.NormalizeExecutionPrefix(Points("_intent"), corner,
                out int consumed, Get<int>("_intentCursor"));
            Assert.That(consumed, Is.Zero);
            Assert.That(pending, Is.EqualTo(new[] { corner, laterReturn, goal }),
                "replanning preserves the unfinished return, rather than reinstating the old drawing start");
            Invoke("AdvanceIntent", 3);
            Assert.That(Get<int>("_intentCursor"), Is.EqualTo(2));
        }

        [Test] public void RobotBlockedStatusRetainsOriginalRouteWhileVerifyingMapEvidence()
        {
            var original = new List<Vector2>(_path.Points);
            int generation = Get<int>("_generation");
            Assert.That(Observe(PathStatusTests.Sample.Replace("\"state\":\"drive\"", "\"state\":\"blocked\""), Now), Is.True);
            Assert.That(PhaseName, Is.EqualTo("Running"));
            Assert.That(Get<bool>("_detourPending"), Is.False);
            Assert.That(_path.Points, Is.EqualTo(original));
            Assert.That(Get<int>("_detoursUsed"), Is.Zero);
            Assert.That(Get<int>("_generation"), Is.EqualTo(generation), "unconfirmed raw braking cannot create a replacement route");
            Assert.That(_path.Status, Does.Contain("Keeping the original route").And.Contain("verified"));
            Assert.That(double.IsPositiveInfinity(Get<double>("_cancelConfirmedAt")), Is.True);
        }

        [Test] public void ObstacleCancellationRequiresItsAcceptedReceiptAndALaterFreshInactiveSnapshot()
        {
            Obstacle();
            Observe(Stopped, Now);
            Assert.That(PhaseName, Is.EqualTo("Cancelling"), "idle HTTP alone cannot exclude a delayed original path packet");
            Receipt(10, registered: false);
            Receipt(11, accepted: false);
            Receipt(12, kind: "path");
            Assert.That(double.IsPositiveInfinity(Get<double>("_cancelConfirmedAt")), Is.True);
            Receipt(13);
            double barrier = Get<double>("_cancelConfirmedAt");
            Assert.That(double.IsPositiveInfinity(barrier), Is.False);
            Observe(Stopped, barrier - .01);
            Assert.That(PhaseName, Is.EqualTo("Cancelling"), "a pre-ACK HTTP request cannot confirm stop");
            Observe(Stopped, Now);
            Assert.That(PhaseName, Is.EqualTo("Cancelling"), "a recently cached snapshot that predates the barrier cannot confirm stop");
            Set("_cancelConfirmedAt", Now - .3);
            Observe(PathStatusTests.Sample, Now);
            Assert.That(PhaseName, Is.EqualTo("Cancelling"), "even fresh active telemetry is not a stopped follower");
            Observe(Stopped, Now);
            Assert.That(PhaseName, Is.EqualTo("Replanning"));
            Assert.That(Get<bool>("_detourPending"), Is.True);
            Assert.That(Get<bool>("_pathSent"), Is.False, "the original datagram cannot be replayed while waiting for neutral ACK");
            Assert.That(_path.Status, Does.Contain("Waiting for neutral ACK"));
            Assert.That(Get<int>("_detoursUsed"), Is.Zero);
        }

        [Test] public void RetryReceiptsKeepTheEarliestCancellationBarrier()
        {
            Obstacle();
            Receipt(10);
            Set("_cancelConfirmedAt", Now - .2);
            double firstBarrier = Get<double>("_cancelConfirmedAt");
            Receipt(11);
            Assert.That(Get<double>("_cancelConfirmedAt"), Is.EqualTo(firstBarrier));
            Assert.That(Get<bool>("_detourPending"), Is.True);
        }

        [Test] public void StopDuringAnObstacleCancellationRevokesDetourWithoutRestartingTheCancelTimer()
        {
            Obstacle();
            double cancelAt = Get<double>("_cancelAt");
            _path.Cancel("operator STOP");
            Assert.That(Get<bool>("_detourPending"), Is.False);
            Assert.That(Get<double>("_cancelAt"), Is.EqualTo(cancelAt));
            Receipt(10);
            Set("_cancelConfirmedAt", Now - .3);
            Observe(Stopped, Now);
            Assert.That(_path.IsActive, Is.False);
            Assert.That(Get<int>("_detoursUsed"), Is.Zero);
            Assert.That(_path.Status, Does.StartWith("Cancelled:"));
        }

        [Test] public void ControlLossDuringCancellationPermanentlyRevokesAutomaticReplacement()
        {
            var drive = _root.AddComponent<PuppyPiV2Client>();
            var controller = _root.AddComponent<PuppyPiRobotController>();
            controller.driveClient = drive;
            _path.controller = controller;
            Set("_drive", drive);
            Set("_host", drive.robotAddress);
            Set("_clientId", drive.Session.ClientId);
            drive.networkEnabled = false;
            Obstacle();
            Set("_nextCancel", double.PositiveInfinity);
            Set("_nextPoll", double.PositiveInfinity);
            double began = Get<double>("_cancelAt");
            Invoke("Update");
            Assert.That(Get<bool>("_detourPending"), Is.False,
                "a loss while waiting for the stop barrier must revoke the intent even if telemetry later recovers");
            Assert.That(Get<double>("_cancelAt"), Is.EqualTo(began));
            drive.networkEnabled = true;
            Assert.That(Get<bool>("_detourPending"), Is.False, "restoring transport is not a new RUN authorization");
        }

        [Test] public void AConfirmedStopOnlyEntersNeutralAckWaitWithoutInvokingAPlannerOrDrive()
        {
            var original = new List<Vector2>(_path.Points);
            int generation = Get<int>("_generation");
            Obstacle();
            Receipt(10);
            Set("_cancelConfirmedAt", Now - .3);
            Observe(Stopped, Now);
            Assert.That(PhaseName, Is.EqualTo("Replanning"));
            Assert.That(Get<bool>("_detourPending"), Is.True);
            Assert.That(Get<bool>("_pathSent"), Is.False);
            Assert.That(_path.Points, Is.EqualTo(original));
            Assert.That(Get<int>("_generation"), Is.EqualTo(generation));
            Assert.That(Get<int>("_detoursUsed"), Is.Zero);
            Assert.That(Get<double>("_stopVerifiedAt"), Is.LessThanOrEqualTo(Now));
            Assert.That(Get<object>("_drive"), Is.Null, "this transition requires no transport and cannot issue motion");
        }

        [TestCase("stop")]
        [TestCase("focus")]
        [TestCase("pause")]
        [TestCase("map")]
        [TestCase("disable")]
        public void OperatorAndLifecycleChangesRevokeAStoppedRouteWaitingForNeutralAck(string change)
        {
            var original = new List<Vector2>(_path.Points);
            Obstacle();
            Receipt(10);
            Set("_cancelConfirmedAt", Now - .3);
            Observe(Stopped, Now);
            Assert.That(PhaseName, Is.EqualTo("Replanning"));
            if (change == "stop") _path.Cancel("operator STOP");
            else if (change == "focus") Invoke("OnApplicationFocus", false);
            else if (change == "pause") Invoke("OnApplicationPause", true);
            else if (change == "map") _path.ObserveMapIdentity("map", "origin-b");
            else Invoke("OnDisable");
            Assert.That(Get<bool>("_detourPending"), Is.False);
            Assert.That(PhaseName, Is.Not.EqualTo("Replanning").And.Not.EqualTo("Sending").And.Not.EqualTo("Running"));
            Assert.That(Get<int>("_detoursUsed"), Is.Zero);
            Assert.That(_path.Points, Is.EqualTo(original));
        }

        [TestCase("OnApplicationFocus")]
        [TestCase("OnApplicationPause")]
        public void FocusOrPauseRevokesPendingDetourAndRecoveryDoesNotResumeIt(string lifecycleMethod)
        {
            Obstacle();
            bool stopValue = lifecycleMethod == "OnApplicationPause";
            Invoke(lifecycleMethod, stopValue);
            Assert.That(_path.IsActive, Is.False);
            Assert.That(Get<bool>("_detourPending"), Is.False);
            Assert.That(_path.Status, Does.Contain("will not resume automatically"));
            Invoke(lifecycleMethod, !stopValue);
            Assert.That(_path.IsActive, Is.False);
            Assert.That(Get<int>("_detoursUsed"), Is.Zero);
        }

        [Test] public void AMapOriginChangeDuringCancellationRevokesThePendingDetour()
        {
            Obstacle();
            _path.ObserveMapIdentity("map", "origin-b");
            Assert.That(Get<bool>("_detourPending"), Is.False);
            Assert.That(PhaseName, Is.EqualTo("Cancelling"));
            Receipt(10);
            Set("_cancelConfirmedAt", Now - .3);
            Observe(Stopped, Now);
            Assert.That(_path.IsActive, Is.False);
            Assert.That(Get<int>("_detoursUsed"), Is.Zero);
        }

        [Test] public void ThreeUsedBypassesForceStopInsteadOfARepeatedAutomaticAttempt()
        {
            Set("_detoursUsed", 3);
            Obstacle();
            Assert.That(PhaseName, Is.EqualTo("Cancelling"));
            Assert.That(Get<bool>("_detourPending"), Is.False);
            Assert.That(Get<int>("_detoursUsed"), Is.EqualTo(3));
            Assert.That(_path.Status, Does.Contain("three bypass attempts"));
        }

        [Test] public void SupervisionAllowsAnotherConfirmedObstacleDetourAfterThreeUsedBypasses()
        {
            _path.operatorSupervisedNavigation = true;
            Set("_detoursUsed", 3);
            Obstacle();
            Assert.That(PhaseName, Is.EqualTo("Cancelling"));
            Assert.That(Get<bool>("_detourPending"), Is.True);
            Assert.That(Get<int>("_detoursUsed"), Is.EqualTo(3), "requesting a stop does not consume a bypass");
            Assert.That(double.IsPositiveInfinity(Get<double>("_cancelConfirmedAt")), Is.True,
                "a fourth attempt still requires its own accepted cancellation barrier");
            Assert.That(_path.Status, Does.Contain("Stopping before planning"));
        }

        [Test] public void AThirdBypassCanBeRequestedButAnUnconfirmedCancellationDoesNotConsumeIt()
        {
            Set("_detoursUsed", 2);
            Obstacle();
            Assert.That(Get<bool>("_detourPending"), Is.True);
            Assert.That(Get<int>("_detoursUsed"), Is.EqualTo(2));
            Observe(Stopped, Now);
            Assert.That(Get<int>("_detoursUsed"), Is.EqualTo(2));
            Assert.That(PhaseName, Is.EqualTo("Cancelling"));
        }

        [Test] public void OrderedRepeatedGoalsAdvanceOncePerNewCompletedPrefixAndNeverRewind()
        {
            Vector2 a = Vector2.zero, b = Vector2.right;
            Points("_points").Clear();
            Points("_points").AddRange(new[] { a, b, a });
            Points("_intent").Clear();
            Points("_intent").AddRange(new[] { a, b, a });
            Invoke("AdvanceIntent", 1);
            Assert.That(Get<int>("_intentCursor"), Is.EqualTo(1));
            Invoke("AdvanceIntent", 1);
            Assert.That(Get<int>("_intentCursor"), Is.EqualTo(1), "repeated HTTP status does not complete the later A goal");
            Invoke("AdvanceIntent", 2);
            Assert.That(Get<int>("_intentCursor"), Is.EqualTo(2));
            Invoke("AdvanceIntent", 1);
            Assert.That(Get<int>("_intentCursor"), Is.EqualTo(2), "an older prefix cannot revisit the first A");
            Assert.That(Get<int>("_intentRoutePrefix"), Is.EqualTo(2));
            Invoke("AdvanceIntent", 3);
            Assert.That(Get<int>("_intentCursor"), Is.EqualTo(3));
            Invoke("AdvanceIntent", 3);
            Assert.That(Get<int>("_intentCursor"), Is.EqualTo(3));
        }

        [Test] public void RemainingRouteExcludesCompletedPointsEvenIfALaterSnapshotHasAnOlderIndex()
        {
            Observe(PathStatusTests.Sample.Replace("\"index\":0", "\"index\":1"), Now);
            Assert.That(_path.RemainingPoints, Is.EqualTo(new[] { _path.Points[1] }));
            Observe(PathStatusTests.Sample, Now);
            Assert.That(_path.RemainingPoints, Is.EqualTo(new[] { _path.Points[1] }), "completed prefixes must not return to the collision checker or bypass planner");
            Assert.That(Get<int>("_intentCursor"), Is.EqualTo(1));
        }

        [Test] public void CompletedExecutionHasNoRemainingRouteToRecheckOrReplan()
        {
            Observe(PathStatusTests.Sample.Replace("\"index\":0", "\"index\":1"), Now);
            string done = PathStatusTests.Sample.Replace("\"state\":\"drive\"", "\"state\":\"done\"")
                .Replace("\"index\":0", "\"index\":2");
            Observe(done, Now);
            Assert.That(_path.IsActive, Is.False);
            Assert.That(_path.RemainingPoints, Is.Empty);
            Assert.That(Get<bool>("_detourPending"), Is.False);
        }

        [Test] public void DisabledAutomaticDetoursKeepBlockedExecutionWithoutSchedulingReplacement()
        {
            _path.automaticObstacleDetour = false;
            Observe(PathStatusTests.Sample.Replace("\"state\":\"drive\"", "\"state\":\"blocked\""), Now);
            Assert.That(PhaseName, Is.EqualTo("Running"));
            Assert.That(Get<bool>("_detourPending"), Is.False);
        }

        private PuppyPiV2Client AttachActiveDrive(bool supervised, bool acknowledged = true)
        {
            var drive = _root.AddComponent<PuppyPiV2Client>();
            drive.robotAddress = "127.0.0.1";
            drive.networkEnabled = true;
            var controller = _root.AddComponent<PuppyPiRobotController>();
            controller.driveClient = drive;
            controller.pathClient = _path;
            controller.showCameraScreenOnStart = false;
            _path.controller = controller;
            controller.ResolveComponents();
            _path.operatorSupervisedNavigation = supervised;
            Set("_drive", drive);
            Set("_host", drive.robotAddress);
            Set("_clientId", drive.Session.ClientId);
            Set("_nextPoll", double.PositiveInfinity);
            Set("_nextCancel", double.PositiveInfinity);
            Set("_nextObstacleCheck", double.PositiveInfinity);
            Set("_lastFreshAt", Now);
            Set("_startedAt", Now);
            drive.SetPathCommandHold(true, !supervised);
            _root.SetActive(true);
            if (acknowledged) AcknowledgeNeutral(drive);
            return drive;
        }

        private static void AcknowledgeNeutral(PuppyPiV2Client drive)
        {
            var neutral = drive.Session.Command(Now, false, 0, 0);
            Assert.That(drive.Session.AcceptAck(new V2Ack {
                protocol = 2, type = "ack", client_id = drive.Session.ClientId,
                sequence = neutral.sequence, accepted = true, state = "idle", reason = "ok"
            }, Now, .2), Is.True);
        }

        [Test]
        public void SupervisedRunControlAcceptsPendingHandshakeWithoutRosOrInputReadiness()
        {
            var drive = AttachActiveDrive(true, false);
            Phase("Idle");
            drive.SetPathCommandHold(false);
            drive.SetExternalMotionBlocked(true);
            Assert.That(drive.Session.Connected, Is.False);
            Assert.That(_path.controller.RosBridge.RobotStatusFresh, Is.False);
            Assert.That(_path.ControlBlockReason, Is.Null,
                "RUN may wait for its neutral handshake instead of becoming an auxiliary-status lock");
            _path.operatorSupervisedNavigation = false;
            Assert.That(_path.ControlBlockReason, Does.Contain("Connect"), "strict startup still requires an established session");
        }

        [Test]
        public void SupervisedTransmissionStillRequiresAcceptedNeutralAndNoStopOrSuspension()
        {
            var drive = AttachActiveDrive(true, false);
            Assert.That((bool)Invoke("CanTransmitPreparedPath"), Is.False);
            AcknowledgeNeutral(drive);
            Assert.That((bool)Invoke("CanTransmitPreparedPath"), Is.True);
            drive.SetPathHeartbeatSuspended(true);
            Assert.That((bool)Invoke("CanTransmitPreparedPath"), Is.False);
            drive.SetPathHeartbeatSuspended(false);
            drive.Session.LatchEmergency();
            Assert.That((bool)Invoke("CanTransmitPreparedPath"), Is.False);
            Phase("Idle");
            Assert.That(_path.ControlBlockReason, Does.Contain("VR emergency stop"));
        }

        [Test]
        public void SupervisedUnsentHandshakeTimeoutEndsLocallyWithoutGlobalStop()
        {
            var drive = AttachActiveDrive(true, false);
            Phase("Sending");
            Set("_pathSent", false);
            Set("_pathSequence", 0L);
            Set("_startedAt", Now - 4);
            Invoke("Update");
            Assert.That(_path.IsActive, Is.False);
            Assert.That(drive.PathCommandHeld, Is.False);
            Assert.That(drive.Session.Emergency, Is.False);
            Assert.That(_path.Status, Does.StartWith("Route not sent:"));
        }

        [Test]
        public void SupervisedSentPathWaitsForExecutionStatusWithoutReplayOrTimeoutCancellation()
        {
            var drive = AttachActiveDrive(true);
            Phase("Sending");
            Set("_pathSent", true);
            Set("_pathSequence", 42L);
            Set("_startedAt", Now - 4);
            int events = 0;
            drive.ControlEventSent += (kind, sequence) => events++;
            Invoke("Update");
            Assert.That(PhaseName, Is.EqualTo("Sending"));
            Assert.That(_path.Status, Does.Contain("waiting for execution status").And.Not.Contain("Following"));
            Assert.That(events, Is.Zero, "an unconfirmed send is neither replayed nor silently replaced");
            Assert.That(drive.Session.Emergency, Is.False);
        }

        [Test]
        public void SupervisedRunningPathIgnoresAuxiliaryStatusAndHttpAge()
        {
            var drive = AttachActiveDrive(true);
            _path.automaticObstacleDetour = false;
            Set("_lastFreshAt", Now - 10);
            drive.SetExternalMotionBlocked(true);
            Invoke("Update");
            Assert.That(PhaseName, Is.EqualTo("Running"));
            Assert.That(drive.PathCommandHeld, Is.True);
            Assert.That(drive.Session.Emergency, Is.False);
            Assert.That(_path.controller.RosBridge.RobotStatusFresh, Is.False);
            Assert.That(_path.controller.MapData.NavigationLive, Is.False,
                "missing sensor observations alone must not cancel an operator-supervised route");
        }

        [Test]
        public void SupervisedTransportLossSuspendsHeartbeatUntilReceiptAndStoppedSnapshot()
        {
            var drive = AttachActiveDrive(true);
            drive.Session.CheckTimeout(Now + 1, .3);
            Invoke("ObserveSupervisedControl");
            Assert.That(PhaseName, Is.EqualTo("Cancelling"));
            Set("_cancelAt", Now - 2);
            Set("_nextCancel", double.PositiveInfinity);
            Set("_nextPoll", double.PositiveInfinity);
            Invoke("Update");
            Assert.That(drive.PathHeartbeatSuspended, Is.True);
            Assert.That(drive.PathCommandHeld, Is.True, "uncertain cancellation cannot release manual control");
            Assert.That(drive.Session.Emergency, Is.False, "a transport gap must not create a latched global stop");
            Observe(Stopped, Now);
            Assert.That(PhaseName, Is.EqualTo("Cancelling"), "HTTP alone still cannot replace the cancellation receipt");
            Receipt(72);
            Set("_cancelConfirmedAt", Now - .3);
            Observe(Stopped, Now);
            Assert.That(_path.IsActive, Is.False);
            Assert.That(drive.PathCommandHeld, Is.False);
            Assert.That(drive.PathHeartbeatSuspended, Is.False);
            Assert.That(drive.Session.Emergency, Is.False);
        }

        [Test]
        public void StrictCancellationTimeoutRetainsItsExistingEmergencyEscalation()
        {
            var drive = AttachActiveDrive(false);
            Obstacle();
            Set("_cancelAt", Now - 2);
            Set("_nextCancel", double.PositiveInfinity);
            Set("_nextPoll", double.PositiveInfinity);
            Invoke("Update");
            Assert.That(_path.IsActive, Is.False);
            Assert.That(drive.Session.Emergency, Is.True);
            Assert.That(_path.Status, Does.StartWith("Stop unconfirmed; VR emergency stop requested."));
        }

        [TestCase("OnApplicationPause", true)]
        [TestCase("OnApplicationFocus", false)]
        public void SupervisedLifecycleKeepsAnUncertainRemoteStopWithoutCreatingEmergency(string method, bool stopValue)
        {
            var drive = AttachActiveDrive(true);
            Invoke(method, stopValue);
            Assert.That(PhaseName, Is.EqualTo("Cancelling"));
            Assert.That(drive.PathCommandHeld, Is.True);
            Assert.That(drive.PathHeartbeatSuspended, Is.True);
            Assert.That(drive.Session.Emergency, Is.False);
            Invoke(method, !stopValue);
            Assert.That(PhaseName, Is.EqualTo("Cancelling"), "focus recovery is not a route restart");
            Assert.That(Get<bool>("_detourPending"), Is.False);
        }

        [TestCase("OnApplicationPause", true)]
        [TestCase("OnApplicationFocus", false)]
        public void SupervisedLifecycleDiscardsGenuinelyUnsentRouteWithoutRemoteStopLock(string method, bool stopValue)
        {
            var drive = AttachActiveDrive(true);
            Phase("Sending");
            Set("_pathSent", false);
            Set("_pathSequence", 0L);
            Invoke(method, stopValue);
            Assert.That(_path.IsActive, Is.False);
            Assert.That(drive.PathCommandHeld, Is.False);
            Assert.That(drive.Session.Emergency, Is.False);
        }

        [Test]
        public void SupervisedManualTakeoverRequiresANewGripRatherThanAnAlreadyHeldOne()
        {
            var drive = AttachActiveDrive(true);
            var input = _root.AddComponent<BallGripInput>();
            drive.input = input;
            FieldInfo grabbed = typeof(BallGripInput).GetField("<Grabbed>k__BackingField", Hidden);
            grabbed.SetValue(input, true);
            Set("_lastManualEngaged", true);
            Invoke("ObserveSupervisedControl");
            Assert.That(PhaseName, Is.EqualTo("Running"), "the grip held before RUN is not a new takeover");
            grabbed.SetValue(input, false);
            Invoke("ObserveSupervisedControl");
            grabbed.SetValue(input, true);
            Invoke("ObserveSupervisedControl");
            Assert.That(PhaseName, Is.EqualTo("Cancelling"));
            Assert.That(Get<bool>("_detourPending"), Is.False);
            Assert.That(drive.Session.Emergency, Is.False);
        }

        [Test]
        public void SupervisedRejectedEmergencyCancelNeedsItsReceiptAndFreshEmergencyStopSnapshot()
        {
            var drive = AttachActiveDrive(true);
            Obstacle();
            drive.Session.LatchEmergency();
            Get<HashSet<long>>("_cancelSequences").Add(91);
            Invoke("OnPathAck", new V2Ack {
                protocol = 2, type = "ack", client_id = drive.Session.ClientId, sequence = 91,
                accepted = false, reason = "emergency_stop", emergency_stop = true, state = "emergency_stop"
            }, "path_cancel");
            Assert.That(double.IsPositiveInfinity(Get<double>("_cancelConfirmedAt")), Is.False);
            Assert.That(Get<bool>("_detourPending"), Is.False, "an emergency receipt can only finish stopping");
            Set("_cancelConfirmedAt", Now - .3);
            Observe(Stopped, Now);
            Assert.That(PhaseName, Is.EqualTo("Cancelling"), "a non-emergency snapshot is not this stop proof");
            string stoppedEmergency = Stopped.Replace("\"session_state\":\"idle\"", "\"session_state\":\"emergency_stop\"")
                .Replace("\"emergency_stop\":false", "\"emergency_stop\":true");
            Observe(stoppedEmergency, Now);
            Assert.That(_path.IsActive, Is.False);
            Assert.That(drive.PathCommandHeld, Is.False, "explicit CLEAR is no longer locked behind the stopped path");
            Assert.That(drive.Session.Emergency, Is.True, "the stopped snapshot must never automatically clear the emergency latch");
        }

        [TestCase(false, "emergency_stop", true, "emergency_stop")]
        [TestCase(true, "not_owner", true, "emergency_stop")]
        [TestCase(true, "fault", true, "emergency_stop")]
        [TestCase(true, "out_of_order", true, "emergency_stop")]
        [TestCase(true, "emergency_stop", false, "emergency_stop")]
        [TestCase(true, "emergency_stop", true, "idle")]
        public void UnrelatedRejectedCancelCannotEstablishAnEmergencyStopBarrier(bool supervised,
            string reason, bool emergency, string state)
        {
            var drive = AttachActiveDrive(supervised);
            Obstacle();
            Get<HashSet<long>>("_cancelSequences").Add(91);
            Invoke("OnPathAck", new V2Ack {
                protocol = 2, type = "ack", client_id = drive.Session.ClientId, sequence = 91,
                accepted = false, reason = reason, emergency_stop = emergency, state = state
            }, "path_cancel");
            Assert.That(double.IsPositiveInfinity(Get<double>("_cancelConfirmedAt")), Is.True);
            Assert.That(PhaseName, Is.EqualTo("Cancelling"));
            Assert.That(drive.PathCommandHeld, Is.True);
        }

        [TestCase("awaiting_neutral")]
        [TestCase("timeout")]
        public void SupervisedWatchdogCancellationReceiptReleasesHoldOnlyAfterItsFreshStoppedProof(string state)
        {
            var drive = AttachActiveDrive(true);
            Obstacle();
            drive.SetPathHeartbeatSuspended(true);
            Get<HashSet<long>>("_cancelSequences").Add(91);
            Invoke("OnPathAck", new V2Ack { protocol = 2, type = "ack", client_id = drive.Session.ClientId,
                sequence = 91, accepted = false, reason = "neutral_required", state = state }, "path_cancel");
            Assert.That(double.IsPositiveInfinity(Get<double>("_cancelConfirmedAt")), Is.False);
            Assert.That(Get<bool>("_detourPending"), Is.False);
            Set("_cancelConfirmedAt", Now - .3);
            Observe(Stopped, Now);
            Assert.That(PhaseName, Is.EqualTo("Cancelling"), "an ordinary idle report is not the watchdog stop proof");
            Observe(Stopped.Replace("\"session_state\":\"idle\"", "\"session_state\":\"" + state + "\""), Now);
            Assert.That(_path.IsActive, Is.False);
            Assert.That(drive.PathCommandHeld, Is.False);
            Assert.That(drive.PathHeartbeatSuspended, Is.False);
            Assert.That(drive.Session.Emergency, Is.False);
        }

        [Test]
        public void AcceptedCancellationAfterEmergencyRecoveryReplacesTheEmergencyOnlyProof()
        {
            var drive = AttachActiveDrive(true);
            Obstacle();
            Get<HashSet<long>>("_cancelSequences").Add(91);
            Invoke("OnPathAck", new V2Ack { sequence = 91, accepted = false, reason = "emergency_stop",
                emergency_stop = true, state = "emergency_stop" }, "path_cancel");
            Set("_cancelConfirmedAt", Now - .5);
            Receipt(92);
            Assert.That(Get<bool>("_cancelRequiresEmergencySnapshot"), Is.False);
            Assert.That(Get<bool>("_cancelRequiresWatchdogSnapshot"), Is.False);
            Observe(Stopped, Now - .1);
            Assert.That(PhaseName, Is.EqualTo("Cancelling"), "the new proof must still follow the newer receipt");
            Set("_cancelConfirmedAt", Now - .3);
            Observe(Stopped, Now);
            Assert.That(_path.IsActive, Is.False);
            Assert.That(drive.PathCommandHeld, Is.False);
        }

        [Test]
        public void DisabledPreviousDriverDoesNotPermanentlyBlockBindingItsReplacement()
        {
            var oldDrive = AttachActiveDrive(true);
            oldDrive.enabled = false;
            var replacementObject = new GameObject("replacement control channel");
            replacementObject.transform.SetParent(_root.transform);
            var replacement = replacementObject.AddComponent<PuppyPiV2Client>();
            _path.controller.driveClient = replacement;
            Invoke("Bind");
            Assert.That(Get<PuppyPiV2Client>("_drive"), Is.SameAs(replacement));
            Assert.That(_path.IsActive, Is.False);
            Assert.That(oldDrive.Session.Emergency, Is.False);
        }

        private static bool Equivalent(IReadOnlyList<Vector2> first, IReadOnlyList<Vector2> second) =>
            (bool)typeof(PuppyPiPathClient).GetMethod("EquivalentRoute", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { first, second });

        [Test] public void DuplicateOrCollinearWaypointInsertionCannotImpersonateADifferentBypass()
        {
            var interrupted = new[] { Vector2.zero, Vector2.right };
            var same = new[] { Vector2.zero, Vector2.zero, new Vector2(.3f, 0), new Vector2(.7f, 0), Vector2.right, Vector2.right };
            Assert.That(Equivalent(interrupted, same), Is.True, "a blocked route must not be replayed under a different waypoint count");
            Assert.That(Equivalent(same, interrupted), Is.True);
        }

        [Test] public void ARealBypassMayRejoinAndKeepTheUnfinishedOriginalSuffix()
        {
            var interrupted = new[] { Vector2.zero, new Vector2(2, 0), new Vector2(3, 0) };
            var bypass = new[] { Vector2.zero, new Vector2(0, .6f), new Vector2(2, .6f), new Vector2(2, 0), new Vector2(3, 0) };
            Assert.That(Equivalent(interrupted, bypass), Is.False,
                "the bypass may join the original route again; it need not alter every remaining leg");
        }

        [Test] public void RouteEquivalencePreservesDeliberateReversalsAndWaypointOrder()
        {
            var returnRoute = new[] { Vector2.zero, Vector2.right, Vector2.zero };
            Assert.That(Equivalent(returnRoute, new[] { Vector2.zero, Vector2.zero }), Is.False);
            Assert.That(Equivalent(returnRoute, new[] { Vector2.zero, new Vector2(.5f, 0), Vector2.right,
                new Vector2(.5f, 0), Vector2.zero }), Is.True, "sampling density may change without deleting the intentional return");
        }
    }
}
