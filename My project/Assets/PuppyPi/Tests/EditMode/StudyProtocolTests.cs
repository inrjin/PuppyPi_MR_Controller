using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace PuppyPi.Tests
{
    public class StudyProtocolTests
    {
        private static StudyStatus Running() => new StudyStatus {
            schema_version = 1, experiment_session_id = "session", control_client_id = "client",
            interface_id = "mr_ballgrip", state = "running", signal_id = "signal", motion_permitted = true
        };

        [Test]
        public void StudyGateRequiresMatchingTrialSignalAndHealthyChannels()
        {
            var status = Running();
            Assert.That(StudyMotionGate.BlockReason(status, "client", "session", true, true, true, "signal", true), Is.Null);
            Assert.That(StudyMotionGate.BlockReason(status, "client", "session", false, true, true, "signal", true), Is.EqualTo("study_channel_unavailable"));
            Assert.That(StudyMotionGate.BlockReason(status, "client", "other", true, true, true, "signal", true), Is.EqualTo("study_session_mismatch"));
            Assert.That(StudyMotionGate.BlockReason(status, "other", "session", true, true, true, "signal", true), Is.EqualTo("study_client_mismatch"));
            Assert.That(StudyMotionGate.BlockReason(status, "client", "session", true, true, true, "old", true), Is.EqualTo("start_signal_not_presented"));
            Assert.That(StudyMotionGate.BlockReason(status, "client", "session", true, false, true, "signal", true), Is.EqualTo("client_log_unavailable"));
            Assert.That(StudyMotionGate.BlockReason(status, "client", "session", true, true, false, "signal", true), Is.EqualTo("clock_alignment_unavailable"));
            status.mock_mode = true;
            Assert.That(StudyMotionGate.BlockReason(status, "client", "session", true, true, true, "signal", false), Is.EqualTo("mock_cannot_control_physical_robot"));
            status.state = "success";
            Assert.That(StudyMotionGate.BlockReason(status, "client", "session", true, true, true, "signal", true), Is.EqualTo("study_success"));
        }

        [Test]
        public void StartGrabObservationRequiresTheDisplayedCurrentSignal()
        {
            var status = Running();
            status.state = "countdown";
            status.show_start_signal = true;
            Assert.That(StudyMotionGate.CanObserveStartGrab(status, "study_countdown", "signal"), Is.True);
            Assert.That(StudyMotionGate.CanObserveStartGrab(status, "study_countdown", "previous-signal"), Is.False);
            Assert.That(StudyMotionGate.CanObserveStartGrab(status, "clock_alignment_unavailable", "signal"), Is.False);
            status.show_start_signal = false;
            Assert.That(StudyMotionGate.CanObserveStartGrab(status, "study_countdown", "signal"), Is.False);
        }

        [Test]
        public void ClockAlignmentKeepsTimebasesSeparateAndRejectsInvalidExchanges()
        {
            var clock = new StudyClockAlignment();
            Assert.That(clock.Observe("robot", 100000, 510100, 510200, 100300, 500), Is.True);
            Assert.That(clock.OffsetUs, Is.EqualTo(410000));
            Assert.That(clock.UncertaintyUs, Is.EqualTo(100));
            Assert.That(clock.IsFresh(100400, 1000), Is.True);
            Assert.That(clock.IsFresh(99999, 1000), Is.False);
            Assert.That(clock.IsFresh(102000, 1000), Is.False);
            Assert.That(clock.Observe("robot", 200000, 600000, 600100, 199999, 500), Is.False);
            Assert.That(clock.Valid, Is.False);
            Assert.That(clock.Observe("new-robot-epoch", 300000, 800100, 800200, 300300, 500), Is.True);
            Assert.That(clock.ReferenceClockId, Is.EqualTo("new-robot-epoch"));
            Assert.That(clock.OffsetUs, Is.EqualTo(500000));
        }

        [Test]
        public void SettingsRejectNonfiniteOrIncompatibleControlParameters()
        {
            var settings = new StudySettings { speed_percent = 50, max_tilt_degrees = 40, deadzone_degrees = 5, sensitivity = 1 };
            Assert.That(StudyMotionGate.ValidSettings(settings), Is.True);
            settings.sensitivity = float.NaN;
            Assert.That(StudyMotionGate.ValidSettings(settings), Is.False);
            settings.sensitivity = 1;
            settings.deadzone_degrees = settings.max_tilt_degrees;
            Assert.That(StudyMotionGate.ValidSettings(settings), Is.False);
            settings.deadzone_degrees = 5;
            settings.command_hz = 60;
            Assert.That(StudyMotionGate.ValidSettings(settings), Is.False);
        }

        [Test]
        public void JournalFlushesNewFileAndPreservesExistingLogs()
        {
            string path = Path.Combine(Path.GetTempPath(), "puppypi-study-" + Guid.NewGuid().ToString("N") + ".jsonl");
            try
            {
                using (var log = new StudyJournal(path, 16))
                {
                    Assert.That(log.Enqueue("{\"event_type\":\"trial_started\"}"), Is.True);
                    Assert.That(log.Enqueue("{\"event_type\":\"trial_finished\"}"), Is.True);
                    Assert.That(log.Close(1000), Is.True);
                    Assert.That(log.WrittenCount, Is.EqualTo(2));
                }
                Assert.That(File.ReadAllLines(path).Length, Is.EqualTo(2));
                Assert.Throws<IOException>(() => new StudyJournal(path));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Test]
        public void DestroyedUnityInputBecomesMissingAtTheTransportBoundary()
        {
            var root = new GameObject("Destroyed study input test");
            try
            {
                var input = root.AddComponent<BallGripInput>();
                var client = root.AddComponent<PuppyPiV2Client>();
                client.input = input;
                Assert.That(client.DriveInput, Is.SameAs(input));
                UnityEngine.Object.DestroyImmediate(input);
                Assert.That(client.DriveInput, Is.Null);
                Assert.DoesNotThrow(() => client.RequestNeutral());
                Assert.That(client.Session.Armed, Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
    }
}
