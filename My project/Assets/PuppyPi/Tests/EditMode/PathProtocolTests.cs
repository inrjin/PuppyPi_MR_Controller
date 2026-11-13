using System;
using System.Globalization;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace PuppyPi.Tests
{
    public class PathProtocolTests
    {
        private static V2Ack Ack(V2Session s, long sequence, bool accepted = true, string reason = "ok") =>
            new V2Ack { protocol = 2, type = "ack", client_id = s.ClientId, sequence = sequence,
                accepted = accepted, reason = reason, state = "idle" };
        private static V2Session Ready()
        {
            var s = new V2Session();
            var c = s.Command(0, false, 0, 0);
            Assert.That(s.AcceptAck(Ack(s, c.sequence), .01, .2), Is.True);
            return s;
        }

        [Test]
        public void PathUsesOneSequenceAndNumericArraysAcrossCulturesAndCopiesItsInput()
        {
            var s = Ready();
            var points = new[] { new[] { .125, -.25 }, new[] { 1.5, 2.75 } };
            var message = s.Path(.02, points);
            points[0][0] = 99;
            var culture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                string json = message.ToJson();
                Assert.That(json, Does.Contain("\"points\":[[0.125,-0.25],[1.5,2.75]]"));
                Assert.That(json, Does.Contain("\"client_id\":\"" + s.ClientId + "\""));
                Assert.That(json, Does.Not.Contain("control_active"));
            }
            finally { CultureInfo.CurrentCulture = culture; }
            Assert.That(message.Sequence, Is.EqualTo(2));
            Assert.That(s.Command(.03, false, 0, 0).sequence, Is.EqualTo(3));
            Assert.That(s.PathCancel(.04).sequence, Is.EqualTo(4));
        }

        [Test]
        public void InvalidPathsDoNotConsumeASequenceAndUnreadySessionCannotSend()
        {
            var unready = new V2Session();
            Assert.Throws<InvalidOperationException>(() => unready.Path(0, new[] { new[] { 0.0, 0.0 } }));
            var s = Ready();
            foreach (var bad in new[] { new double[0][], new[] { new[] { double.NaN, 0.0 } },
                new[] { new[] { double.PositiveInfinity, 0.0 } }, new[] { new[] { 100.1, 0.0 } },
                new[] { new[] { 0.0 } }, new double[21][] })
                Assert.Throws<ArgumentException>(() => s.Path(.02, bad));
            Assert.That(s.LastSequence, Is.EqualTo(1));
        }

        [Test]
        public void AcceptedPathPreservesNeutralButCannotArmAHeldManualGrip()
        {
            var s = Ready();
            s.ObserveInput(true, true);
            Assert.That(s.Armed, Is.True);
            var path = s.Path(.02, new[] { new[] { 0.0, 0.0 } });
            Assert.That(s.AcceptAck(Ack(s, path.Sequence), .03, .2), Is.True);
            Assert.That(s.NeutralConfirmed, Is.True);
            s.ObserveInput(true, true);
            Assert.That(s.Command(.04, true, 1, 1).control_active, Is.False);
            s.ObserveInput(false, false);
            var neutral = s.Command(.05, false, 0, 0);
            s.AcceptAck(Ack(s, neutral.sequence), .06, .2);
            s.ObserveInput(true, true);
            Assert.That(s.Armed, Is.True);
        }

        [Test]
        public void CancellationAndRejectedPathRequireAnotherNeutralAck()
        {
            var s = Ready();
            var path = s.Path(.02, new[] { new[] { 0.0, 0.0 } });
            s.AcceptAck(Ack(s, path.Sequence, false, "not_owner"), .03, .2);
            Assert.That(s.NeutralConfirmed, Is.False);
            var neutral = s.Command(.04, false, 0, 0);
            s.AcceptAck(Ack(s, neutral.sequence), .05, .2);
            var cancel = s.PathCancel(.06);
            Assert.That(cancel.type, Is.EqualTo("path_cancel"));
            Assert.That(JsonUtility.ToJson(cancel), Does.Not.Contain("points").And.Not.Contain("forward"));
            s.AcceptAck(Ack(s, cancel.sequence), .07, .2);
            Assert.That(s.NeutralConfirmed, Is.False);
            Assert.That(s.Armed, Is.False);
        }

        [Test]
        public void ReorderedPathReceiptCanBeCorrelatedWithoutRollingBackHeartbeatState()
        {
            var s = Ready();
            var path = s.Path(.02, new[] { new[] { 0.0, 0.0 } });
            var heartbeat = s.Command(.03, false, 0, 0);
            s.AcceptAck(Ack(s, heartbeat.sequence), .04, .2);
            var receipt = Ack(s, path.Sequence);
            Assert.That(s.TryGetAckKind(receipt, .05, .2, out string kind), Is.True);
            Assert.That(kind, Is.EqualTo("path"));
            Assert.That(s.AcceptAck(receipt, .05, .2), Is.False);
            Assert.That(s.LastAckSequence, Is.EqualTo(heartbeat.sequence));
            Assert.That(s.LastAckTime, Is.EqualTo(.04));
            s.ForgetUnsent(path.Sequence);
            Assert.That(s.TryGetAckKind(receipt, .06, .2, out _), Is.False);
        }
    }

    public class PathStatusTests
    {
        public const string Sample = "{\"media\":\"puppypi-media/1\",\"path\":{\"state\":\"drive\",\"waiting\":null,\"reason\":null," +
            "\"points\":[[0.123,-0.25],[1.5,2.75]],\"index\":0,\"count\":2,\"remaining_m\":3.2,\"blocked_count\":0,\"elapsed_s\":0.3}," +
            "\"pose\":{\"x_m\":0.1,\"y_m\":-0.2},\"session_state\":\"idle\",\"emergency_stop\":false,\"age_s\":0.1}";

        [Test]
        public void RealRobotShapeSupportsRoundedPointsAndNullWaiting()
        {
            Assert.That(PuppyPiPathStatus.TryParse(Sample, out var status), Is.True);
            Assert.That(status.Active, Is.True);
            Assert.That(status.AgeSeconds, Is.EqualTo(.1));
            Assert.That(status.ElapsedSeconds, Is.EqualTo(.3));
            Assert.That(status.Matches(new[] { new Vector2(.1234f, -.25f), new Vector2(1.5f, 2.75f) }), Is.True);
            Assert.That(status.Matches(new[] { Vector2.zero, Vector2.one }), Is.False);
            Assert.That(status.Matches(new[] { new Vector2(float.NaN, -.25f), new Vector2(1.5f, 2.75f) }), Is.False);
        }

        [Test]
        public void MissingCoercedDuplicateNonfiniteAndMalformedStatusCannotConfirmMotionOrStop()
        {
            foreach (string invalid in new[] {
                Sample.Replace(",\"age_s\":0.1", ""),
                Sample.Replace("\"age_s\":0.1", "\"age_s\":\"0.1\""),
                Sample.Replace("\"age_s\":0.1", "\"age_s\":0.1,\"age_s\":0"),
                Sample.Replace("\"age_s\":0.1", "\"age_s\":-1"),
                Sample.Replace("\"age_s\":0.1", "\"age_s\":NaN"),
                Sample.Replace("\"age_s\":0.1", "\"age_s\":1e999"),
                Sample.Replace("\"emergency_stop\":false", "\"emergency_stop\":0"),
                Sample.Replace("\"count\":2", "\"count\":1"),
                Sample.Replace("[0.123,-0.25]", "[0.123,-0.25,1]"),
                Sample.Replace("[0.123,-0.25]", "[101,-0.25]"),
                Sample.Replace("\"index\":0", "\"index\":2"),
                Sample.Replace("\"waiting\":null", "\"waiting\":\"future\""),
                Sample.Replace("\"state\":\"drive\"", "\"state\":\"unknown\""),
                Sample + "{}", Sample.Replace("\"age_s\":0.1}", "\"age_s\":0.1,}") })
                Assert.That(PuppyPiPathStatus.TryParse(invalid, out _), Is.False, invalid);
        }

        [Test]
        public void BlockedAndDoneRemainDistinctFromControlSessionIdle()
        {
            string blocked = Sample.Replace("\"state\":\"drive\"", "\"state\":\"blocked\"")
                .Replace("\"reason\":null", "\"reason\":\"obstacle \\uac10\\uc9c0\"");
            Assert.That(PuppyPiPathStatus.TryParse(blocked, out var report), Is.True);
            Assert.That(report.Active, Is.True);
            Assert.That(report.SessionState, Is.EqualTo("idle"));
            Assert.That(report.Reason, Is.EqualTo("obstacle 감지"));
            Assert.That(PuppyPiPathStatus.TryParse(Sample.Replace("\"state\":\"drive\"", "\"state\":\"done\"")
                .Replace("\"index\":0", "\"index\":2"), out report), Is.True);
            Assert.That(report.Active, Is.False);
        }

        [TestCase(0L)]
        [TestCase(42L)]
        [TestCase(9007199254740991L)]
        public void ExecutionIdentityPreservesSafeIntegerSequenceAndCanonicalOwner(long sequence)
        {
            const string owner = "A92E1842-7EF1-4D1F-BC14-2A04DCD0DA87";
            string sample = Sample.Replace("\"state\":\"drive\"", "\"request_client_id\":\"" + owner +
                "\",\"request_sequence\":" + sequence.ToString(CultureInfo.InvariantCulture) + ",\"state\":\"drive\"");
            Assert.That(PuppyPiPathStatus.TryParse(sample, out var report), Is.True);
            Assert.That(report.HasRequestIdentity, Is.True);
            Assert.That(report.RequestClientId, Is.EqualTo(owner.ToLowerInvariant()));
            Assert.That(report.RequestSequence, Is.EqualTo(sequence));
        }

        [Test]
        public void MissingOrNullExecutionIdentityRemainsAnExplicitLegacyReport()
        {
            foreach (string sample in new[] { Sample, Sample.Replace("\"state\":\"drive\"",
                "\"request_client_id\":null,\"request_sequence\":null,\"state\":\"drive\"") })
            {
                Assert.That(PuppyPiPathStatus.TryParse(sample, out var report), Is.True);
                Assert.That(report.HasRequestIdentity, Is.False);
                Assert.That(report.RequestClientId, Is.Null);
                Assert.That(report.RequestSequence, Is.Null);
            }
        }

        [TestCase("\"request_client_id\":null")]
        [TestCase("\"request_sequence\":42")]
        [TestCase("\"request_client_id\":null,\"request_sequence\":42")]
        [TestCase("\"request_client_id\":\"a92e1842-7ef1-4d1f-bc14-2a04dcd0da87\",\"request_sequence\":null")]
        [TestCase("\"request_client_id\":\"not-a-uuid\",\"request_sequence\":42")]
        [TestCase("\"request_client_id\":\"00000000-0000-0000-0000-000000000000\",\"request_sequence\":42")]
        [TestCase("\"request_client_id\":\"a92e1842-7ef1-4d1f-bc14-2a04dcd0da87\",\"request_sequence\":-1")]
        [TestCase("\"request_client_id\":\"a92e1842-7ef1-4d1f-bc14-2a04dcd0da87\",\"request_sequence\":0.5")]
        [TestCase("\"request_client_id\":\"a92e1842-7ef1-4d1f-bc14-2a04dcd0da87\",\"request_sequence\":9007199254740992")]
        [TestCase("\"request_client_id\":\"a92e1842-7ef1-4d1f-bc14-2a04dcd0da87\",\"request_sequence\":\"42\"")]
        [TestCase("\"request_client_id\":\"a92e1842-7ef1-4d1f-bc14-2a04dcd0da87\",\"request_sequence\":true")]
        public void PartialOrInvalidExecutionIdentityCannotBecomeLegacyConfirmation(string fields)
        {
            Assert.That(PuppyPiPathStatus.TryParse(Sample.Replace("\"state\":\"drive\"", fields +
                ",\"state\":\"drive\""), out _), Is.False);
        }

        [Test]
        public void DoneRequiresEveryWaypointToHaveCompleted()
        {
            Assert.That(PuppyPiPathStatus.TryParse(Sample.Replace("\"state\":\"drive\"", "\"state\":\"done\""), out _), Is.False);
        }
    }

    public class PathControlReadinessTests
    {
        [Test] public void RejectedEmergencyAckShowsClearStopThenSupervisedRunWaitsForNeutralInternally()
        {
            var root = new GameObject("read-only path readiness fixture");
            var inactiveController = new GameObject("inactive controller reference");
            inactiveController.SetActive(false);
            var controller = inactiveController.AddComponent<PuppyPiRobotController>();
            var input = root.AddComponent<BallGripInput>();
            var drive = root.AddComponent<PuppyPiV2Client>();
            drive.input = input;
            drive.networkEnabled = true;
            controller.driveClient = drive;
            var path = root.AddComponent<PuppyPiPathClient>();
            path.controller = controller;
            typeof(PuppyPiPathClient).GetField("_drive", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(path, drive);
            try
            {
                // EditMode never runs Update here; no transport is opened.
                var session = drive.Session;
                var command = session.Command(0, false, 0, 0);
                Assert.That(session.AcceptAck(new V2Ack { protocol = 2, type = "ack", client_id = session.ClientId,
                    sequence = command.sequence, accepted = false, reason = "emergency_stop",
                    state = "emergency_stop", emergency_stop = true }, .01, .2), Is.True);
                Assert.That(path.ControlBlockReason, Does.Contain("CLEAR STOP"));
                Assert.That(path.ControlBlockReason, Does.Not.Contain("own"));

                var clear = session.Event(.02, "clear_emergency");
                Assert.That(session.AcceptAck(new V2Ack { protocol = 2, type = "ack", client_id = session.ClientId,
                    sequence = clear.sequence, accepted = true, reason = "ok", state = "idle" }, .03, .2), Is.True);
                Assert.That(path.ControlBlockReason, Is.Null,
                    "supervised RUN may be accepted while neutral confirmation is pending");
                var canTransmit = typeof(PuppyPiPathClient).GetMethod("CanTransmitPreparedPath",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That((bool)canTransmit.Invoke(path, null), Is.False,
                    "accepting RUN must not bypass the real transmission handshake");
                path.operatorSupervisedNavigation = false;
                Assert.That(path.ControlBlockReason, Does.Contain("neutral ACK"));
                path.operatorSupervisedNavigation = true;

                var next = session.Command(.04, false, 0, 0);
                session.AcceptAck(new V2Ack { protocol = 2, type = "ack", client_id = session.ClientId,
                    sequence = next.sequence, accepted = false, reason = "not_owner", state = "idle" }, .05, .2);
                Assert.That(path.ControlBlockReason, Is.Null);
                Assert.That((bool)canTransmit.Invoke(path, null), Is.False,
                    "a rejected session must never transmit a queued path");
                path.operatorSupervisedNavigation = false;
                Assert.That(path.ControlBlockReason, Does.Contain("own"));
            }
            finally
            {
                drive.networkEnabled = false;
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(inactiveController);
            }
        }
    }

    public class PathConfirmationTests
    {
        private GameObject _root;
        private PuppyPiPathClient _path;
        private PuppyPiV2Client _drive;
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static double Now => Time.realtimeSinceStartupAsDouble;

        [SetUp]
        public void Setup()
        {
            _root = new GameObject("path confirmation fixture");
            _root.SetActive(false); // These are received-state tests; never open a transport.
            _drive = _root.AddComponent<PuppyPiV2Client>();
            _path = _root.AddComponent<PuppyPiPathClient>();
            _path.operatorSupervisedNavigation = false; // Preserve the historical strict-mode status contract.
            Set("_drive", _drive);
            Set("_clientId", _drive.Session.ClientId);
            Set("_pathSequence", 42L);
            var phase = typeof(PuppyPiPathClient).GetField("_phase", Hidden);
            phase.SetValue(_path, Enum.Parse(phase.FieldType, "Sending"));
            Set("_pathSent", true);
            Set("_sentAt", Now - 1);
            Set("_frame", "map"); Set("_session", "origin-a");
            var points = (List<Vector2>)typeof(PuppyPiPathClient).GetField("_points", Hidden).GetValue(_path);
            points.Add(new Vector2(.1234f, -.25f)); points.Add(new Vector2(1.5f, 2.75f));
            _drive.SetPathCommandHold(true);
        }
        [TearDown] public void Cleanup() => UnityEngine.Object.DestroyImmediate(_root);
        private void Set(string name, object value) => typeof(PuppyPiPathClient).GetField(name, Hidden).SetValue(_path, value);
        private bool Observe(string json, double requestedAt) =>
            (bool)typeof(PuppyPiPathClient).GetMethod("ApplyStatus", Hidden).Invoke(_path, new object[] { json, requestedAt });
        private string Identified(string json, long sequence = 42, string owner = null) =>
            json.Replace("\"state\":", "\"request_client_id\":\"" + (owner ?? _drive.Session.ClientId) +
                "\",\"request_sequence\":" + sequence.ToString(CultureInfo.InvariantCulture) + ",\"state\":");
        private static string Done => PathStatusTests.Sample.Replace("\"state\":\"drive\"", "\"state\":\"done\"")
            .Replace("\"index\":0", "\"index\":2");
        private void CancellationReceipt(long sequence, bool registered = true)
        {
            if (registered)
                ((HashSet<long>)typeof(PuppyPiPathClient).GetField("_cancelSequences", Hidden).GetValue(_path)).Add(sequence);
            typeof(PuppyPiPathClient).GetMethod("OnPathAck", Hidden).Invoke(_path, new object[] {
                new V2Ack { protocol = 2, type = "ack", client_id = _drive.Session.ClientId, sequence = sequence,
                    accepted = true, state = "idle", reason = "ok" }, "path_cancel" });
        }

        [Test]
        public void PreviousIdenticalDoneDoesNotCompleteUntilFreshActivePathWasObserved()
        {
            string done = PathStatusTests.Sample.Replace("\"state\":\"drive\"", "\"state\":\"done\"")
                .Replace("\"index\":0", "\"index\":2");
            Observe(done, Now);
            Assert.That(_path.IsActive, Is.True, "an old identical route's result is not execution confirmation");
            Observe(PathStatusTests.Sample, Now);
            Assert.That(_path.Status, Does.Contain("Following path"));
            Observe(done, Now);
            Assert.That(_path.IsActive, Is.False);
            Assert.That(_drive.PathCommandHeld, Is.False);
            Assert.That(_drive.Session.Armed, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DuplicateRejectionCannotOverrideCurrentExecutionIdentity(bool runningObserved)
        {
            if (runningObserved) Assert.That(Observe(Identified(PathStatusTests.Sample), Now), Is.True);
            typeof(PuppyPiPathClient).GetMethod("OnPathAck", Hidden).Invoke(_path, new object[] {
                new V2Ack { protocol = 2, type = "ack", client_id = _drive.Session.ClientId,
                    sequence = 42, accepted = false, state = "idle", reason = "out_of_order" }, "path" });
            Assert.That(_path.Status, Does.Not.Contain("Cancelling"));
            Assert.That(_path.IsActive, Is.True);
            Assert.That((bool)typeof(PuppyPiPathClient).GetField("_observedRunning", Hidden).GetValue(_path),
                Is.EqualTo(runningObserved), "a receipt must not manufacture execution confirmation");
            if (!runningObserved)
            {
                Assert.That(Observe(Identified(Done, 41), Now), Is.False);
                Assert.That(_path.IsActive, Is.True, "old done plus duplicate receipt is still unconfirmed");
            }
            Assert.That(Observe(Identified(Done), Now), Is.True);
            Assert.That(_path.Status, Does.StartWith("Path completed"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void GenuineOwnershipRejectionStillCancelsBeforeOrAfterObservedExecution(bool runningObserved)
        {
            if (runningObserved) Observe(Identified(PathStatusTests.Sample), Now);
            typeof(PuppyPiPathClient).GetMethod("OnPathAck", Hidden).Invoke(_path, new object[] {
                new V2Ack { protocol = 2, type = "ack", client_id = _drive.Session.ClientId,
                    sequence = 42, accepted = false, state = "idle", reason = "not_owner" }, "path" });
            Assert.That(_path.Status, Does.Contain("Cancelling").And.Contain("Path rejected: not_owner"));
        }

        [Test]
        public void FastIdentifiedCompletionDoesNotRequireAnActiveSampleOrUdpReceipt()
        {
            var points = (List<Vector2>)typeof(PuppyPiPathClient).GetField("_points", Hidden).GetValue(_path);
            points.Clear();
            points.Add(Vector2.zero); points.Add(new Vector2(.12f, 0));
            Set("_sentAt", Now - .25);
            string done = Identified(Done.Replace("[[0.123,-0.25],[1.5,2.75]]", "[[0,0],[0.12,0]]")
                .Replace("\"elapsed_s\":0.3", "\"elapsed_s\":0.16"));
            Assert.That(Observe(done, Now), Is.True);
            Assert.That(_path.IsActive, Is.False, "an identified 160 ms execution can finish before a 250 ms HTTP poll");
            Assert.That(_path.Status, Does.StartWith("Path completed"));
            Assert.That(_drive.PathCommandHeld, Is.False);
            Assert.That(_drive.Session.Armed, Is.False);
        }

        [TestCase(41L)]
        [TestCase(43L)]
        public void IdenticalRouteFromAnotherSequenceCannotCompleteThisExecutionEvenAfterAck(long otherSequence)
        {
            typeof(PuppyPiPathClient).GetMethod("OnPathAck", Hidden).Invoke(_path, new object[] {
                new V2Ack { protocol = 2, type = "ack", client_id = _drive.Session.ClientId,
                    sequence = 42, accepted = true, state = "idle", reason = "ok" }, "path" });
            Assert.That(Observe(Identified(Done, otherSequence), Now), Is.False);
            Assert.That(_path.IsActive, Is.True);
            Assert.That(_drive.PathCommandHeld, Is.True);
            Assert.That(Observe(Identified(Done), Now), Is.True);
            Assert.That(_path.Status, Does.StartWith("Path completed"));
        }

        [Test]
        public void AnotherOwnersIdenticalSequenceCannotConfirmExecution()
        {
            Assert.That(Observe(Identified(Done, owner: "a92e1842-7ef1-4d1f-bc14-2a04dcd0da87"), Now), Is.False);
            Assert.That(_path.IsActive, Is.True);
        }

        [Test]
        public void IdentifiedExecutionCannotDowngradeToAReportWithoutIdentity()
        {
            Assert.That(Observe(Identified(PathStatusTests.Sample), Now), Is.True);
            Assert.That(Observe(Done, Now), Is.False);
            Assert.That(_path.IsActive, Is.True, "a fresh legacy-shaped snapshot cannot finish an identified execution");
            Assert.That(Observe(Identified(Done), Now), Is.True);
            Assert.That(_path.IsActive, Is.False);
        }

        [Test]
        public void ChangedExecutionIdentityStopsAnAlreadyObservedPath()
        {
            Observe(Identified(PathStatusTests.Sample), Now);
            Assert.That(Observe(Identified(PathStatusTests.Sample, 43), Now), Is.False);
            Assert.That(_path.Status, Does.Contain("Cancelling").And.Contain("execution changed"));
        }

        [Test]
        public void LegacyDoneWithoutObservedStartRemainsUnverified()
        {
            Observe(Done, Now);
            Assert.That(_path.IsActive, Is.True);
            Assert.That(_path.Status, Does.Contain("without an execution identity").And.Contain("unverified"));
        }

        [Test]
        public void StaleMismatchedAndPreSubmissionSnapshotsCannotConfirmExecution()
        {
            Assert.That(Observe(PathStatusTests.Sample.Replace("\"age_s\":0.1", "\"age_s\":2"), Now), Is.False);
            Assert.That(Observe(PathStatusTests.Sample.Replace("[1.5,2.75]", "[1.6,2.75]"), Now), Is.False);
            Assert.That(Observe(PathStatusTests.Sample, Now - 2), Is.False);
            Assert.That(_path.Status, Does.Not.Contain("Following path"));
            Assert.That(_path.IsActive, Is.True);
        }

        [Test]
        public void MapChangeCancelsOnceAndOnlyPostCancelFreshInactiveStatusConfirmsStop()
        {
            _path.ObserveMapIdentity("map", "origin-a");
            _path.ObserveMapIdentity("map", "origin-b");
            Assert.That(_path.Status, Does.Contain("Cancelling"));
            double cancelAt = (double)typeof(PuppyPiPathClient).GetField("_cancelAt", Hidden).GetValue(_path);
            _path.Cancel("another reason");
            Assert.That((double)typeof(PuppyPiPathClient).GetField("_cancelAt", Hidden).GetValue(_path), Is.EqualTo(cancelAt));
            string stopped = PathStatusTests.Sample.Replace("\"state\":\"drive\"", "\"state\":\"cancelled\"");
            Observe(stopped, cancelAt - .2);
            Assert.That(_path.IsActive, Is.True);
            Set("_cancelAt", Now - .3);
            CancellationReceipt(10);
            Set("_cancelConfirmedAt", Now - .3);
            Observe(stopped, Now);
            Assert.That(_path.IsActive, Is.False);
            Assert.That(_path.Status, Does.Contain("Map origin/session changed"));
        }

        [Test]
        public void IdleHttpCannotConfirmStopBeforeCancelAckAndPollStartedAfterItsBarrier()
        {
            _path.Cancel("operator stop");
            string stopped = PathStatusTests.Sample.Replace("\"state\":\"drive\"", "\"state\":\"cancelled\"");
            Observe(stopped, Now);
            Assert.That(_path.IsActive, Is.True, "an idle HTTP snapshot cannot exclude a delayed path datagram");
            CancellationReceipt(99, false);
            Observe(stopped, Now);
            Assert.That(_path.IsActive, Is.True, "unrelated cancellation receipts do not establish this operation's barrier");
            CancellationReceipt(10);
            double barrier = (double)typeof(PuppyPiPathClient).GetField("_cancelConfirmedAt", Hidden).GetValue(_path);
            Observe(stopped, barrier - .01);
            Assert.That(_path.IsActive, Is.True, "a request already in flight before the receipt is not confirmation");
            // Simulate elapsed polling time without sleeping in an EditMode test.
            Set("_cancelConfirmedAt", Now - .3);
            double confirmed = (double)typeof(PuppyPiPathClient).GetField("_cancelConfirmedAt", Hidden).GetValue(_path);
            CancellationReceipt(11);
            Assert.That((double)typeof(PuppyPiPathClient).GetField("_cancelConfirmedAt", Hidden).GetValue(_path), Is.EqualTo(confirmed),
                "later retry ACKs must retain the first confirmed barrier");
            Observe(stopped, Now);
            Assert.That(_path.IsActive, Is.False);
        }

        [TestCase(true, false)]
        [TestCase(false, false)]
        [TestCase(true, true)]
        public void UnsentRouteCancelsLocallyAndRequiresFreshNeutral(bool held, bool replacement)
        {
            Set("_pathSent", false);
            Set("_pathSequence", 0L);
            Set("_usingDetour", replacement);
            _drive.SetPathCommandHold(held);
            var session = _drive.Session;
            var neutral = session.Command(Now, false, 0, 0);
            Assert.That(session.AcceptAck(new V2Ack { protocol = 2, type = "ack", client_id = session.ClientId,
                sequence = neutral.sequence, accepted = true, state = "idle", reason = "ok" }, Now, .2), Is.True);
            Assert.That(session.NeutralConfirmed, Is.True);

            _path.Cancel("Map changed before transmission");

            Assert.That(_path.IsActive, Is.False, "an operation never sent does not need a remote stop barrier");
            Assert.That(_path.Status, Does.StartWith("Route not sent:").And.Contain("Map changed before transmission"));
            Assert.That(_drive.PathCommandHeld, Is.False);
            Assert.That(session.NeutralConfirmed, Is.False, "leaving a route still requires a new neutral ACK");
            Assert.That(session.Armed, Is.False);
            Assert.That(session.Emergency, Is.False);
            Assert.That((HashSet<long>)typeof(PuppyPiPathClient).GetField("_cancelSequences", Hidden).GetValue(_path), Is.Empty);

            var fresh = session.Command(Now, false, 0, 0);
            Assert.That(session.AcceptAck(new V2Ack { protocol = 2, type = "ack", client_id = session.ClientId,
                sequence = fresh.sequence, accepted = true, state = "idle", reason = "ok" }, Now, .2), Is.True);
            Assert.That(session.NeutralConfirmed, Is.True);
            Assert.That(session.Armed, Is.False, "a neutral ACK must not automatically arm manual driving");
        }

        [TestCase("Sending", false, 42L)]
        [TestCase("Sending", true, 42L)]
        [TestCase("Sending", true, 0L)]
        [TestCase("Running", false, 0L)]
        [TestCase("Replanning", false, 0L)]
        public void SentUncertainOrPreviouslyRunningRoutesStillRequireRemoteCancellation(string phaseName, bool sent, long sequence)
        {
            var phase = typeof(PuppyPiPathClient).GetField("_phase", Hidden);
            phase.SetValue(_path, Enum.Parse(phase.FieldType, phaseName));
            Set("_pathSent", sent);
            Set("_pathSequence", sequence);

            _path.Cancel("operator stop");

            Assert.That(_path.IsActive, Is.True);
            Assert.That(_path.Status, Does.StartWith("Cancelling:"));
            Assert.That(_drive.PathCommandHeld, Is.True, "manual control remains held until the remote stop is confirmed");
            Assert.That(_drive.Session.Emergency, Is.False, "the ordinary cancellation barrier remains in control");
        }

        [Test]
        public void UnsentRouteStillPreservesExplicitEmergencyStopOnApplicationPause()
        {
            Set("_pathSent", false);
            Set("_pathSequence", 0L);

            typeof(PuppyPiPathClient).GetMethod("OnApplicationPause", Hidden).Invoke(_path, new object[] { true });

            Assert.That(_drive.Session.Emergency, Is.True, "application suspension preserves its explicit safety stop");
            Assert.That(_path.IsActive, Is.False);
            Assert.That(_path.Status, Does.Contain("will not resume automatically"));
        }

        [Test]
        public void PauseCannotLeaveNeutralHeartbeatsKeepingThePathAlive()
        {
            var pause = typeof(PuppyPiPathClient).GetMethod("OnApplicationPause", Hidden);
            pause.Invoke(_path, new object[] { true });
            Assert.That(_drive.Session.Emergency, Is.True, "suspension cannot wait for HTTP stop confirmation");
            Assert.That(_path.IsActive, Is.False);
            Assert.That(_path.Status, Does.Contain("will not resume automatically"));
            pause.Invoke(_path, new object[] { false });
            Assert.That(_path.IsActive, Is.False);
            Assert.That(_drive.Session.Emergency, Is.True);
        }
    }
}
