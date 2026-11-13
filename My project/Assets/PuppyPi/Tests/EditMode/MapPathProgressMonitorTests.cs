using System;
using NUnit.Framework;
using UnityEngine;

namespace PuppyPi.Tests
{
    public class MapPathProgressMonitorTests
    {
        private static readonly Vector2 ForwardTarget = new Vector2(5, 0);

        [Test] public void StalledDrivingAndTurningFailAfterTheirAllowancesAndRequireReset()
        {
            foreach (string state in new[] { "drive", "rotate" })
            {
                var monitor = new MapPathProgressMonitor();
                var pose = new Vector3(0, 0, -1);
                Assert.That(monitor.Check(pose, state, 0, ForwardTarget, 0, out _), Is.True);
                Assert.That(monitor.Check(pose, state, 0, ForwardTarget, 7.9, out _), Is.True);
                Assert.That(monitor.Check(pose, state, 0, ForwardTarget, 8, out string reason), Is.False);
                Assert.That(reason, Does.Contain(state == "drive" ? "forward progress" : "turning progress"));
                Assert.That(monitor.Check(new Vector3(1, 0, 0), state, 0, ForwardTarget, 9, out _), Is.False,
                    "late motion cannot silently revive a cancelled monitor");
                monitor.Reset();
                Assert.That(monitor.Check(pose, state, 0, ForwardTarget, 0, out _), Is.True, "new runs may start a new clock baseline");
            }
        }

        [Test] public void SlowContinuousTargetDirectedDriveMakesMeaningfulProgressBeforeTimeout()
        {
            var monitor = new MapPathProgressMonitor();
            for (int t = 0; t <= 30; t++)
                Assert.That(monitor.Check(new Vector3(t * .01f, 0, 0), "drive", 0, ForwardTarget, t, out string reason), Is.True, reason);
        }

        [Test] public void SlowContinuousYawTowardTheTargetCountsAsTurningProgress()
        {
            var monitor = new MapPathProgressMonitor();
            for (int t = 0; t <= 30; t++)
                Assert.That(monitor.Check(new Vector3(0, 0, -1 + t * .01f), "rotate", 0, ForwardTarget, t, out string reason), Is.True, reason);
        }

        [Test] public void YawOnlyDrivingAndTranslationOnlyTurningCannotResetProgress()
        {
            var drive = new MapPathProgressMonitor();
            var turn = new MapPathProgressMonitor();
            var turnTarget = new Vector2(5, 5);
            for (int t = 0; t < 8; t++)
            {
                Assert.That(drive.Check(new Vector3(0, 0, t * .1f), "drive", 0, ForwardTarget, t, out _), Is.True);
                Assert.That(turn.Check(new Vector3(t * .1f, 0, 0), "rotate", 0, turnTarget, t, out _), Is.True);
            }
            Assert.That(drive.Check(new Vector3(0, 0, .8f), "drive", 0, ForwardTarget, 8, out _), Is.False);
            Assert.That(turn.Check(new Vector3(.8f, 0, 0), "rotate", 0, turnTarget, 8, out _), Is.False);
        }

        [Test] public void SidewaysOrBackwardMovementDoesNotCountAsForwardGoalProgress()
        {
            foreach (bool backwards in new[] { false, true })
            {
                var monitor = new MapPathProgressMonitor();
                for (int t = 0; t < 8; t++)
                {
                    Vector3 pose = backwards ? new Vector3(-t * .1f, 0, 0) : new Vector3(0, t * .1f, 0);
                    Assert.That(monitor.Check(pose, "drive", 0, ForwardTarget, t, out _), Is.True);
                }
                Assert.That(monitor.Check(backwards ? new Vector3(-.8f, 0, 0) : new Vector3(0, .8f, 0),
                    "drive", 0, ForwardTarget, 8, out _), Is.False);
            }
        }

        [Test] public void BackAndForthJitterCannotKeepTheBaselineAlive()
        {
            var drive = new MapPathProgressMonitor();
            var rotate = new MapPathProgressMonitor();
            for (int t = 0; t < 8; t++)
            {
                float jitter = t % 2 == 0 ? .02f : -.02f;
                Assert.That(drive.Check(new Vector3(jitter, 0, 0), "drive", 0, ForwardTarget, t, out _), Is.True);
                Assert.That(rotate.Check(new Vector3(0, 0, -1 + jitter), "rotate", 0, ForwardTarget, t, out _), Is.True);
            }
            Assert.That(drive.Check(new Vector3(.02f, 0, 0), "drive", 0, ForwardTarget, 8, out _), Is.False);
            Assert.That(rotate.Check(new Vector3(0, 0, -.98f), "rotate", 0, ForwardTarget, 8, out _), Is.False);
        }

        [Test] public void PhaseAndWaypointChangesStartNewProgressAllowances()
        {
            var monitor = new MapPathProgressMonitor();
            var pose = new Vector3(0, 0, -1);
            Assert.That(monitor.Check(pose, "drive", 0, ForwardTarget, 0, out _), Is.True);
            Assert.That(monitor.Check(pose, "rotate", 0, ForwardTarget, 7, out _), Is.True);
            Assert.That(monitor.Check(pose, "rotate", 1, ForwardTarget, 14, out _), Is.True);
            Assert.That(monitor.Check(pose, "rotate", 1, ForwardTarget, 21, out _), Is.True);
            Assert.That(monitor.Check(pose, "rotate", 1, ForwardTarget, 22, out _), Is.False);
        }

        [Test] public void BlockedOrInactiveTimeDoesNotConsumeTheNextDriveAllowance()
        {
            var monitor = new MapPathProgressMonitor();
            Assert.That(monitor.Check(Vector3.zero, "drive", 0, ForwardTarget, 0, out _), Is.True);
            Assert.That(monitor.Check(Vector3.zero, "blocked", 0, ForwardTarget, 7, out _), Is.True);
            Assert.That(monitor.Check(Vector3.zero, "blocked", 0, ForwardTarget, 70, out _), Is.True);
            Assert.That(monitor.Check(Vector3.zero, "drive", 0, ForwardTarget, 71, out _), Is.True);
            Assert.That(monitor.Check(Vector3.zero, "drive", 0, ForwardTarget, 78, out _), Is.True);
            Assert.That(monitor.Check(Vector3.zero, "drive", 0, ForwardTarget, 79, out _), Is.False);
        }

        [Test] public void ReachedTargetDoesNotDemandAdditionalMotionBeforeDoneStatus()
        {
            foreach (string state in new[] { "drive", "rotate" })
            {
                var monitor = new MapPathProgressMonitor();
                var pose = new Vector3(4.95f, 0, -1);
                Assert.That(monitor.Check(pose, state, 0, ForwardTarget, 0, out _), Is.True);
                Assert.That(monitor.Check(pose, state, 0, ForwardTarget, 100, out _), Is.True);
            }
        }

        [Test] public void TurningAcrossPiUsesTheShortestAngularError()
        {
            var monitor = new MapPathProgressMonitor();
            var target = new Vector2(-5, -.1f);
            Assert.That(monitor.Check(new Vector3(0, 0, 2.9f), "rotate", 0, target, 0, out _), Is.True);
            Assert.That(monitor.Check(new Vector3(0, 0, -3.13f), "rotate", 0, target, 6, out _), Is.True);
            Assert.That(monitor.Check(new Vector3(0, 0, -3.13f), "rotate", 0, target, 13, out _), Is.True);
            Assert.That(monitor.Check(new Vector3(0, 0, -3.13f), "rotate", 0, target, 14, out _), Is.False);
        }

        [Test] public void InvalidInputBackwardsClockAndUnexpectedTargetChangesFailClosed()
        {
            foreach (int invalid in new[] { 0, 1, 2, 3, 4, 5 })
            {
                var monitor = new MapPathProgressMonitor();
                Vector3 pose = invalid == 0 ? new Vector3(float.NaN, 0, 0) : Vector3.zero;
                Vector2 target = invalid == 1 ? new Vector2(float.PositiveInfinity, 0) : ForwardTarget;
                Assert.That(monitor.Check(pose, invalid == 2 ? "future" : "drive", invalid == 3 ? -1 : 0, target,
                    invalid == 4 ? double.NaN : invalid == 5 ? -1 : 0, out string reason), Is.False);
                Assert.That(reason, Does.Contain("Invalid"));
            }
            var backwards = new MapPathProgressMonitor();
            backwards.Check(Vector3.zero, "drive", 0, ForwardTarget, 2, out _);
            Assert.That(backwards.Check(Vector3.zero, "drive", 0, ForwardTarget, 1, out _), Is.False);
            var changedTarget = new MapPathProgressMonitor();
            changedTarget.Check(Vector3.zero, "drive", 0, ForwardTarget, 0, out _);
            Assert.That(changedTarget.Check(Vector3.zero, "drive", 0, Vector2.one, 1, out _), Is.False);
        }

        [Test] public void ExplicitThresholdAndTimeoutConfigurationIsValidated()
        {
            var monitor = new MapPathProgressMonitor(.1, .1, 2, 3, .05);
            monitor.Check(Vector3.zero, "drive", 0, ForwardTarget, 0, out _);
            Assert.That(monitor.Check(Vector3.zero, "drive", 0, ForwardTarget, 2, out _), Is.False);
            Assert.Throws<ArgumentOutOfRangeException>(() => new MapPathProgressMonitor(movementMeters: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new MapPathProgressMonitor(turnRadians: double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => new MapPathProgressMonitor(driveTimeoutSeconds: double.PositiveInfinity));
            Assert.Throws<ArgumentOutOfRangeException>(() => new MapPathProgressMonitor(arrivalMeters: -1));
        }
    }
}
