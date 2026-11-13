#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace PuppyPi.Tests
{
    public class ClientLoopbackTests
    {
        private GameObject _root;
        private UdpClient _server;
        private bool _emergency;
        private readonly List<V2Command> _commands = new List<V2Command>();
        private readonly List<string> _events = new List<string>();
        private int _sourcePort;
        private long _lastSequence;
        private string _uuid;

        [SetUp]
        public void ResetSessionFixture()
        {
            _sourcePort = 0;
            _lastSequence = 0;
            _uuid = null;
            _emergency = false;
            _commands.Clear();
            _events.Clear();
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _server?.Close();
            yield return null;
        }

        private IEnumerator Pump(float seconds, bool reply = true)
        {
            double until = Time.realtimeSinceStartupAsDouble + seconds;
            while (Time.realtimeSinceStartupAsDouble < until)
            {
                while (_server.Available > 0)
                {
                    var address = new IPEndPoint(IPAddress.Any, 0);
                    string json = Encoding.UTF8.GetString(_server.Receive(ref address));
                    var envelope = JsonUtility.FromJson<V2Event>(json);
                    if (_sourcePort == 0) { _sourcePort = address.Port; _uuid = envelope.client_id; }
                    Assert.That(address.Port, Is.EqualTo(_sourcePort), "all commands and events use one UDP socket");
                    Assert.That(envelope.client_id, Is.EqualTo(_uuid));
                    Assert.That(envelope.sequence, Is.GreaterThan(_lastSequence));
                    _lastSequence = envelope.sequence;
                    string state = "idle";
                    if (envelope.type == "command")
                    {
                        var command = JsonUtility.FromJson<V2Command>(json);
                        _commands.Add(command);
                        state = _emergency ? "emergency_stop" : command.control_active ? "active" : "idle";
                    }
                    else
                    {
                        _events.Add(envelope.type);
                        Assert.That(json, Does.Not.Contain("forward").And.Not.Contain("turn").And.Not.Contain("control_active"));
                        if (envelope.type == "emergency_stop") _emergency = true;
                        if (envelope.type == "clear_emergency") _emergency = false;
                        state = _emergency ? "emergency_stop" : "awaiting_neutral";
                    }
                    if (!reply) continue;
                    byte[] response = Encoding.UTF8.GetBytes(JsonUtility.ToJson(new V2Ack { protocol = 2, type = "ack",
                        client_id = envelope.client_id, sequence = envelope.sequence, accepted = true, reason = "ok",
                        state = state, emergency_stop = _emergency }));
                    _server.Send(response, response.Length, address);
                }
                yield return null;
            }
        }

        private PuppyPiV2Client CreatePathClient(out BallGripInput input)
        {
            _server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            _root = new GameObject("Path policy UDP loopback test");
            _root.SetActive(false);
            var grip = new GameObject("Grip");
            grip.transform.SetParent(_root.transform);
            input = _root.AddComponent<BallGripInput>();
            input.grip = grip.transform;
            input.baseFrame = _root.transform;
            input.editorPreview = true;
            var client = _root.AddComponent<PuppyPiV2Client>();
            client.input = input;
            client.robotAddress = "127.0.0.1";
            client.port = ((IPEndPoint)_server.Client.LocalEndPoint).Port;
            client.networkEnabled = true;
            _root.SetActive(true);
            return client;
        }

        [UnityTest]
        public IEnumerator SupervisedCancellationSuspendsAllCommandHeartbeatsButKeepsExplicitEvents()
        {
            var client = CreatePathClient(out _);
            yield return Pump(.2f);
            client.SetPathCommandHold(true, false);
            yield return Pump(.15f);
            Assert.That(client.Session.NeutralConfirmed, Is.True);
            int automaticCancellations = 0;
            client.PathCancellationRequested += _ => automaticCancellations++;
            client.SetPathHeartbeatSuspended(true);
            Assert.That(client.PathHeartbeatSuspended, Is.True);
            Assert.That(client.TrySendPath(new[] { Vector2.zero, Vector2.right }, out _, out _), Is.False,
                "an unresolved stop cannot transmit a replacement route");
            client.RequestNeutral();
            client.SetExternalMotionBlocked(true);
            yield return Pump(.1f); // Drain datagrams queued before suspension.
            int commandsAtSuspension = _commands.Count;
            long sequenceAtSuspension = client.Session.LastSequence;
            yield return Pump(.25f);
            Assert.That(_commands.Count, Is.EqualTo(commandsAtSuspension),
                "scheduled sends and timeout/neutral requests must not keep the robot path alive");
            Assert.That(client.Session.LastSequence, Is.EqualTo(sequenceAtSuspension));
            Assert.That(automaticCancellations, Is.Zero,
                "auxiliary input invalidation is not a supervised path cancellation");
            Assert.That(client.TryCancelPath(out _, out string error), Is.True, error);
            yield return Pump(.12f);
            Assert.That(_events, Does.Contain("path_cancel"));
            Assert.That(_commands.Count, Is.EqualTo(commandsAtSuspension));
            client.EmergencyStop();
            yield return Pump(.15f);
            Assert.That(_events, Does.Contain("emergency_stop"));
            Assert.That(client.Session.Emergency, Is.True, "explicit stop always remains available");
            client.SetPathCommandHold(false);
            Assert.That(client.PathHeartbeatSuspended, Is.False);
            client.ClearEmergency();
            yield return Pump(.2f);
            Assert.That(_events, Does.Contain("clear_emergency"));
            Assert.That(client.Session.Emergency, Is.False);
            Assert.That(client.Session.NeutralConfirmed, Is.True);
            Assert.That(_commands.Count, Is.GreaterThan(commandsAtSuspension));
            Assert.That(client.LastCommand.control_active, Is.False);
        }

        [UnityTest]
        public IEnumerator SupervisedPathSendIgnoresAuxiliaryLocksButPreservesExplicitStop()
        {
            var client = CreatePathClient(out var input);
            yield return Pump(.2f);
            client.SetPathCommandHold(true, false);
            yield return Pump(.15f);
            input.previewGrabbed = true;
            client.SetExternalMotionBlocked(true);
            yield return Pump(.2f);
            Assert.That(input.Grabbed, Is.True);
            Assert.That(client.ExternalMotionBlocked, Is.True);
            Assert.That(client.Session.NeutralConfirmed, Is.True);
            client.SetPathCommandHold(true); // The legacy API keeps strict policy.
            Assert.That(client.TrySendPath(new[] { Vector2.zero, Vector2.right }, out _, out _), Is.False);
            client.SetPathCommandHold(true, false);
            Assert.That(client.TrySendPath(new[] { Vector2.zero, Vector2.right }, out _, out string error), Is.True, error);
            yield return Pump(.1f);
            Assert.That(_events, Does.Contain("path"));
            client.EmergencyStop();
            Assert.That(client.TrySendPath(new[] { Vector2.zero, Vector2.right }, out _, out _), Is.False,
                "supervised motion never bypasses an explicit emergency stop");
        }

        private IEnumerator SupervisedLifecycleDoesNotLatchStop(int lifecycle)
        {
            var client = CreatePathClient(out _);
            yield return Pump(.2f);
            client.SetPathCommandHold(true, false);
            yield return Pump(.15f);
            if (lifecycle == 0) client.networkEnabled = false;
            else if (lifecycle == 1) client.enabled = false;
            else client.DisconnectSession();
            yield return Pump(.12f, false);
            Assert.That(_events, Does.Contain("path_cancel"), "closing a held path still requests cancellation");
            Assert.That(_events, Does.Not.Contain("emergency_stop"));
            Assert.That(client.Session.Emergency, Is.False);
            Assert.That(client.Session.Connected, Is.False);
            Assert.That(client.PathHeartbeatSuspended, Is.True,
                "a re-opened transport must not renew the unconfirmed previous path");
            Assert.That(_commands.TrueForAll(c => !c.control_active && c.forward == 0 && c.turn == 0), Is.True);
            if (lifecycle == 2)
            {
                Assert.That(_events, Does.Contain("disconnect"));
                Assert.That(client.Session.Retired, Is.True);
            }
        }

        [UnityTest]
        public IEnumerator SupervisedNetworkDisableCancelsWithoutGlobalEmergency()
        {
            yield return SupervisedLifecycleDoesNotLatchStop(0);
        }

        [UnityTest]
        public IEnumerator SupervisedComponentDisableCancelsWithoutGlobalEmergency()
        {
            yield return SupervisedLifecycleDoesNotLatchStop(1);
        }

        [UnityTest]
        public IEnumerator SupervisedDisconnectCancelsWithoutGlobalEmergency()
        {
            yield return SupervisedLifecycleDoesNotLatchStop(2);
        }

        [UnityTest]
        public IEnumerator StrictHeldPathStillLatchesEmergencyWhenDisabled()
        {
            var client = CreatePathClient(out _);
            yield return Pump(.2f);
            client.SetPathCommandHold(true);
            yield return Pump(.15f);
            client.enabled = false;
            yield return Pump(.12f, false);
            Assert.That(_events, Does.Contain("emergency_stop"));
            Assert.That(client.Session.Emergency, Is.True);
        }

        [UnityTest]
        public IEnumerator SpeedAndExternalOperationBlockPreserveNeutralAndRegrab()
        {
            _server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            _root = new GameObject("Dashboard drive loopback test");
            _root.SetActive(false);
            var grip = new GameObject("Grip");
            grip.transform.SetParent(_root.transform);
            var input = _root.AddComponent<BallGripInput>();
            input.grip = grip.transform;
            input.baseFrame = _root.transform;
            input.editorPreview = true;
            var client = _root.AddComponent<PuppyPiV2Client>();
            client.input = input;
            client.robotAddress = "127.0.0.1";
            client.port = ((IPEndPoint)_server.Client.LocalEndPoint).Port;
            client.networkEnabled = true;
            client.speedScale = 0.5f;
            _root.SetActive(true);
            yield return Pump(0.2f);
            grip.transform.localRotation = BallGripMath.Rotation(new Vector2(26, -15.5f));
            input.previewGrabbed = true;
            yield return Pump(0.2f);
            Assert.That(client.LastCommand.control_active, Is.True);
            Assert.That(client.LastCommand.forward, Is.EqualTo(0.3f).Within(0.01));
            Assert.That(client.LastCommand.turn, Is.EqualTo(-0.15f).Within(0.01));

            client.SetStudyCommandHold(true);
            input.previewGrabbed = false;
            yield return Pump(0.15f);
            input.previewGrabbed = true;
            yield return Pump(0.15f);
            Assert.That(client.Session.Armed, Is.True, "a real new grab after GO is observed during confirmation");
            Assert.That(client.LastCommand.control_active, Is.False, "confirmation hold only emits neutral commands");
            client.SetStudyCommandHold(false);
            yield return Pump(0.15f);
            Assert.That(client.LastCommand.control_active, Is.True, "current-trial permission releases output without losing the new grab");

            client.SetStudyMotionBlocked(true);
            client.SetExternalMotionBlocked(false);
            Assert.That(client.LastCommand.control_active, Is.False, "normal cockpit updates cannot clear the independent study gate");
            yield return Pump(0.15f);
            client.SetStudyMotionBlocked(false);
            yield return Pump(0.15f);
            Assert.That(client.LastCommand.control_active, Is.False, "ending a study gate must not restart a held grip");
            input.previewGrabbed = false;
            yield return Pump(0.15f);
            input.previewGrabbed = true;
            yield return Pump(0.15f);
            Assert.That(client.LastCommand.control_active, Is.True);

            client.SetExternalMotionBlocked(true);
            Assert.That(client.LastCommand.control_active, Is.False, "robot operations must neutralize immediately");
            Assert.That(client.LastCommand.forward, Is.Zero);
            Assert.That(client.LastCommand.turn, Is.Zero);
            yield return Pump(0.15f);
            client.SetExternalMotionBlocked(false);
            yield return Pump(0.15f);
            Assert.That(client.LastCommand.control_active, Is.False, "operation completion must not restart a held grip");
            input.previewGrabbed = false;
            yield return Pump(0.15f);
            input.previewGrabbed = true;
            yield return Pump(0.15f);
            Assert.That(client.LastCommand.control_active, Is.True);
            Assert.That(client.LastCommand.forward, Is.EqualTo(0.3f).Within(0.01));
        }

        [UnityTest]
        public IEnumerator RealClientSocketLossPauseEmergencyAndRegrab()
        {
            _server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            _root = new GameObject("Client loopback test");
            _root.SetActive(false);
            GameObject grip = new GameObject("Grip");
            grip.transform.SetParent(_root.transform);
            var input = _root.AddComponent<BallGripInput>();
            input.grip = grip.transform;
            input.baseFrame = _root.transform;
            input.editorPreview = true;
            var client = _root.AddComponent<PuppyPiV2Client>();
            client.input = input;
            client.robotAddress = "127.0.0.1";
            client.port = ((IPEndPoint)_server.Client.LocalEndPoint).Port;
            client.networkEnabled = true;
            _root.SetActive(true);
            yield return Pump(0.2f);
            Assert.That(client.Session.NeutralConfirmed, Is.True);
            Assert.That(_commands.TrueForAll(c => !c.control_active && c.forward == 0 && c.turn == 0), Is.True);
            grip.transform.localRotation = BallGripMath.Rotation(new Vector2(26, -15.5f));
            input.previewGrabbed = true;
            yield return Pump(0.2f);
            Assert.That(client.LastCommand.control_active, Is.True);
            Assert.That(client.LastCommand.forward, Is.EqualTo(0.6f).Within(0.01));
            Assert.That(client.LastCommand.turn, Is.EqualTo(-0.3f).Within(0.01));

            yield return Pump(0.5f, false);
            Assert.That(client.Session.Connected, Is.False);
            Assert.That(client.LastCommand.control_active, Is.False);
            yield return Pump(0.2f);
            Assert.That(client.Session.NeutralConfirmed, Is.True);
            Assert.That(client.LastCommand.control_active, Is.False, "ACK recovery while held cannot restart");
            input.previewGrabbed = false; yield return Pump(0.15f);
            input.previewGrabbed = true; yield return Pump(0.15f);
            Assert.That(client.LastCommand.control_active, Is.True);

            long sequenceBeforePause = client.Session.LastSequence;
            client.SendMessage("OnApplicationPause", true);
            Assert.That(client.Session.LastSequence, Is.GreaterThan(sequenceBeforePause), "pause immediately sends neutral");
            Assert.That(client.LastCommand.control_active, Is.False);
            yield return Pump(0.12f);
            client.SendMessage("OnApplicationPause", false);
            yield return Pump(0.15f);
            Assert.That(client.LastCommand.control_active, Is.False);
            input.previewGrabbed = false; yield return Pump(0.15f);
            input.previewGrabbed = true; yield return Pump(0.15f);
            Assert.That(client.LastCommand.control_active, Is.True);

            client.EmergencyStop(); yield return Pump(0.2f);
            Assert.That(client.Session.Emergency, Is.True);
            client.ClearEmergency(); yield return Pump(0.1f);
            Assert.That(_events.Contains("clear_emergency"), Is.False, "cannot clear while still holding grip");
            input.previewGrabbed = false; yield return Pump(0.15f);
            client.ClearEmergency(); yield return Pump(0.2f);
            Assert.That(client.Session.Emergency, Is.False);
            Assert.That(client.Session.NeutralConfirmed, Is.True);
            input.previewGrabbed = true; yield return Pump(0.15f);
            Assert.That(client.LastCommand.control_active, Is.True);
            client.DisconnectSession(); yield return Pump(0.1f, false);
            Assert.That(client.Session.Retired, Is.True);
            Assert.That(client.networkEnabled, Is.False);
            Assert.That(_events, Does.Contain("emergency_stop").And.Contain("clear_emergency").And.Contain("disconnect"));
        }
    }
}
#endif
