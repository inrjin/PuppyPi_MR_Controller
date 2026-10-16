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
