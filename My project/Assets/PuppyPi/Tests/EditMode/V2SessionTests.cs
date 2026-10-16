using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace PuppyPi.Tests
{
    public class V2SessionTests
    {
        [Test]
        public void AckParserRejectsMissingCoercedDuplicateAndTrailingFields()
        {
            var s = new V2Session();
            string json = JsonUtility.ToJson(Ack(s, 1));
            Assert.That(V2AckParser.TryParse(json, out var parsed), Is.True);
            Assert.That(parsed.client_id, Is.EqualTo(s.ClientId));
            foreach (string invalid in new[] {
                json.Replace("\"accepted\":true,", ""),
                json.Replace("\"accepted\":true", "\"accepted\":1"),
                json.Replace("\"sequence\":1", "\"sequence\":\"1\""),
                json.Replace("\"protocol\":2", "\"protocol\":2,\"protocol\":2"),
                json.Replace("\"emergency_stop\":false,", ""),
                json.Substring(0, json.Length - 1) + ",}", json + "{}" })
                Assert.That(V2AckParser.TryParse(invalid, out _), Is.False, invalid);
        }

        [Test]
        public void RealPythonServerAcksDecodeWhenAvailable()
        {
            string file = Path.GetFullPath(Path.Combine(Application.dataPath, "../../TestResults/robot-v2-acks.jsonl"));
            if (!File.Exists(file)) Assert.Ignore("Run tools/validate_v2.py first to generate real server ACKs");
            foreach (string line in File.ReadAllLines(file))
                Assert.That(V2AckParser.TryParse(line, out _), Is.True, line);
        }
        private static V2Ack Ack(V2Session s, long seq, string state = "idle", bool accepted = true, string reason = "ok") =>
            new V2Ack { protocol = 2, type = "ack", client_id = s.ClientId, sequence = seq,
                accepted = accepted, reason = reason, state = state, emergency_stop = state == "emergency_stop" };

        private static void Ready(V2Session s, double time = 0)
        {
            s.ObserveInput(false, false);
            var cmd = s.Command(time, false, 0, 0);
            Assert.That(s.AcceptAck(Ack(s, cmd.sequence), time + 0.01, 0.2), Is.True);
        }

        [Test]
        public void StartupRequiresNeutralAckThenNewGrab()
        {
            var s = new V2Session();
            s.ObserveInput(true, true);
            Assert.That(s.Command(0, true, 1, 1).control_active, Is.False);
            var neutral = s.Command(0.01, false, 0, 0);
            s.AcceptAck(Ack(s, neutral.sequence), 0.02, 0.2);
            s.ObserveInput(true, true);
            Assert.That(s.Armed, Is.False);
            s.ObserveInput(false, false);
            Ready(s, 0.03);
            s.ObserveInput(true, true);
            Assert.That(s.Command(0.05, true, 0.6f, -0.3f).control_active, Is.True);
        }

        [Test]
        public void LossRecoveryWhileHeldCannotRestartAndOldNeutralCannotRearm()
        {
            var s = new V2Session(); Ready(s);
            s.ObserveInput(true, true);
            var old = s.Command(0.02, false, 0, 0);
            Assert.That(s.CheckTimeout(0.4, 0.3), Is.True);
            Assert.That(s.AcceptAck(Ack(s, old.sequence), 0.41, 0.5), Is.True);
            Assert.That(s.NeutralConfirmed, Is.False);
            var fresh = s.Command(0.42, false, 0, 0);
            s.AcceptAck(Ack(s, fresh.sequence), 0.43, 0.2);
            s.ObserveInput(true, true);
            Assert.That(s.Command(0.44, true, 1, 1).control_active, Is.False);
            s.ObserveInput(false, false); Ready(s, 0.45); s.ObserveInput(true, true);
            Assert.That(s.Armed, Is.True);
        }

        [Test]
        public void StaleDuplicateWrongClientUnsentAndOutOfOrderAcksDoNotRefreshConnection()
        {
            var s = new V2Session(); var cmd = s.Command(0, false, 0, 0);
            Assert.That(s.AcceptAck(Ack(s, cmd.sequence), 0.3, 0.2), Is.False);
            var wrong = Ack(s, cmd.sequence); wrong.client_id = "other";
            Assert.That(s.AcceptAck(wrong, 0.1, 0.2), Is.False);
            Assert.That(s.AcceptAck(Ack(s, 90), 0.1, 0.2), Is.False);
            var next = s.Command(0.05, false, 0, 0);
            s.AcceptAck(Ack(s, next.sequence), 0.06, 0.2);
            Assert.That(s.AcceptAck(Ack(s, cmd.sequence), 0.07, 0.2), Is.False);
            Assert.That(s.AcceptAck(Ack(s, next.sequence), 0.08, 0.2), Is.False);
            Assert.That(s.LastAckTime, Is.EqualTo(0.06));
            var unsent = s.Command(0.09, false, 0, 0); s.ForgetUnsent(unsent.sequence);
            Assert.That(s.AcceptAck(Ack(s, unsent.sequence), 0.1, 0.2), Is.False);
        }

        [TestCase("timeout", "neutral_required")] [TestCase("awaiting_neutral", "neutral_required")]
        [TestCase("fault", "fault")] [TestCase("emergency_stop", "emergency_stop")]
        public void ServerStopRequiresFreshNeutralAndRegrab(string state, string reason)
        {
            var s = new V2Session(); Ready(s); s.ObserveInput(true, true);
            var active = s.Command(0.1, true, 1, 1);
            s.AcceptAck(Ack(s, active.sequence, state, false, reason), 0.11, 0.2);
            Assert.That(s.Armed, Is.False);
            Assert.That(s.NeutralConfirmed, Is.False);
            Assert.That(s.Command(0.12, true, 1, 1).forward, Is.Zero);
        }

        [Test]
        public void TrackingLossAndReleaseNeutralizeImmediately()
        {
            var s = new V2Session(); Ready(s); s.ObserveInput(true, true);
            s.ObserveInput(true, false);
            Assert.That(s.Command(0.1, true, 1, 1).control_active, Is.False);
            s.ObserveInput(true, true);
            Assert.That(s.Armed, Is.False);
            s.ObserveInput(false, false); Ready(s, 0.2); s.ObserveInput(true, true);
            Assert.That(s.Armed, Is.True);
            s.ObserveInput(false, false);
            Assert.That(s.Command(0.3, false, 1, 1).forward, Is.Zero);
        }

        [Test]
        public void ClearEmergencyNeedsNewNeutralAckAndGrab()
        {
            var s = new V2Session(); Ready(s); s.ObserveInput(true, true);
            var stop = s.Event(0.1, "emergency_stop");
            s.AcceptAck(Ack(s, stop.sequence, "emergency_stop"), 0.11, 0.2);
            s.ObserveInput(false, false);
            var clear = s.Event(0.12, "clear_emergency");
            s.AcceptAck(Ack(s, clear.sequence, "awaiting_neutral"), 0.13, 0.2);
            Assert.That(s.Emergency, Is.False);
            Assert.That(s.NeutralConfirmed, Is.False);
            Ready(s, 0.14); s.ObserveInput(true, true);
            Assert.That(s.Armed, Is.True);
        }

        [Test]
        public void DisconnectRetiresIdAndEventsHaveOnlyCommonFields()
        {
            var s = new V2Session(); Ready(s);
            var message = s.Event(0.1, "disconnect");
            string json = JsonUtility.ToJson(message);
            Assert.That(json, Does.Not.Contain("forward").And.Not.Contain("turn").And.Not.Contain("control_active"));
            Assert.That(message.sequence, Is.EqualTo(2));
            Assert.That(s.Retired, Is.True);
            s.ObserveInput(true, true);
            Assert.That(s.Command(0.2, true, 1, 1).control_active, Is.False);
        }

        [Test]
        public void ExportUnitySerializedPacketsForRealPythonParser()
        {
            var s = new V2Session(); Ready(s); s.ObserveInput(true, true);
            var lines = new[] {
                JsonUtility.ToJson(new V2Command { client_id = s.ClientId, sequence = 1 }),
                JsonUtility.ToJson(s.Command(0.1, true, 0.6f, -0.3f)),
                JsonUtility.ToJson(s.Event(0.11, "emergency_stop")),
                JsonUtility.ToJson(s.Event(0.12, "clear_emergency")),
                JsonUtility.ToJson(s.Command(0.13, false, 0, 0)),
                JsonUtility.ToJson(s.Event(0.14, "disconnect"))
            };
            string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../TestResults"));
            Directory.CreateDirectory(folder);
            File.WriteAllLines(Path.Combine(folder, "unity-v2-packets.jsonl"), lines);
        }
    }
}
