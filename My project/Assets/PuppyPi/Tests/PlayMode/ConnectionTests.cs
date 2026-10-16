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
    public class ConnectionTests
    {
        private GameObject _root;
        private readonly List<UdpClient> _sockets = new List<UdpClient>();

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            foreach (var socket in _sockets) socket.Close();
            _sockets.Clear();
            yield return null;
        }

        private UdpClient Server(int port = 0)
        {
            var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            _sockets.Add(socket);
            return socket;
        }

        private PuppyPiV2Client Client(out BallGripInput input)
        {
            _root = new GameObject("Connection test");
            _root.SetActive(false);
            var grip = new GameObject("Grip");
            grip.transform.SetParent(_root.transform);
            input = _root.AddComponent<BallGripInput>();
            input.grip = grip.transform;
            input.baseFrame = _root.transform;
            input.editorPreview = true;
            var client = _root.AddComponent<PuppyPiV2Client>();
            client.input = input;
            _root.SetActive(true);
            return client;
        }

        private static int Port(UdpClient socket) => ((IPEndPoint)socket.Client.LocalEndPoint).Port;

        private static IEnumerator Pump(UdpClient server, float duration, List<V2Event> seen = null, string reason = "ok")
        {
            double until = Time.realtimeSinceStartupAsDouble + duration;
            while (Time.realtimeSinceStartupAsDouble < until)
            {
                while (server.Available > 0)
                {
                    var peer = new IPEndPoint(IPAddress.Any, 0);
                    byte[] packet;
                    try { packet = server.Receive(ref peer); }
                    catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset) { continue; }
                    string json = Encoding.UTF8.GetString(packet);
                    var message = JsonUtility.FromJson<V2Event>(json);
                    seen?.Add(message);
                    byte[] reply = Encoding.UTF8.GetBytes(JsonUtility.ToJson(new V2Ack { protocol = 2, type = "ack",
                        client_id = message.client_id, sequence = message.sequence, accepted = reason == "ok",
                        reason = reason, state = "idle", emergency_stop = false }));
                    server.Send(reply, reply.Length, peer);
                }
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator EndpointSwitchAndReconnectAreNeutralUntilReleaseAndNewGrab()
        {
            var first = Server();
            var second = Server();
            var client = Client(out BallGripInput input);
            Assert.That(client.ConnectTo("127.0.0.1", Port(first), out _), Is.True);
            yield return Pump(first, 0.2f);
            input.previewGrabbed = true;
            yield return Pump(first, 0.2f);
            Assert.That(client.LastCommand.control_active, Is.True);
            string originalId = client.Session.ClientId;
            Assert.That(client.ConnectTo("127.0.0.1", Port(second), out _), Is.True);
            var departures = new List<V2Event>();
            yield return Pump(first, 0.03f, departures);
            Assert.That(departures.Exists(message => message.type == "disconnect" && message.client_id == originalId), Is.True);
            yield return Pump(second, 0.2f);
            Assert.That(client.Session.ClientId, Is.Not.EqualTo(originalId));
            Assert.That(client.Session.Connected, Is.True);
            Assert.That(client.LastCommand.control_active, Is.False, "held grip cannot resume after switching destination");
            input.previewGrabbed = false;
            yield return Pump(second, 0.1f);
            input.previewGrabbed = true;
            yield return Pump(second, 0.1f);
            Assert.That(client.LastCommand.control_active, Is.True);
            string secondId = client.Session.ClientId;
            client.EmergencyStop();
            client.DisconnectSession();
            Assert.That(client.ConnectTo("127.0.0.1", Port(second), out _), Is.True);
            yield return Pump(second, 0.2f);
            Assert.That(client.Session.ClientId, Is.Not.EqualTo(secondId));
            Assert.That(client.Session.Emergency, Is.True, "reconnect must not silently clear a local emergency latch");
            Assert.That(client.LastCommand.control_active, Is.False);
        }

        [UnityTest]
        public IEnumerator InvalidTargetsDoNotReplaceWorkingConnection()
        {
            var server = Server();
            var client = Client(out _);
            Assert.That(client.ConnectTo("127.0.0.1", Port(server), out _), Is.True);
            yield return Pump(server, 0.15f);
            string id = client.Session.ClientId;
            foreach (string invalid in new[] { "", "not-an-ip", "255.255.255.255", "0.0.0.0", "224.0.0.1", "::1", "192.168.1.123" })
                Assert.That(client.ConnectTo(invalid, Port(server), out _), Is.False, invalid);
            Assert.That(client.ConnectTo("127.0.0.1", 0, out _), Is.False);
            Assert.That(client.Session.ClientId, Is.EqualTo(id));
            Assert.That(client.networkEnabled, Is.True);
        }

        [UnityTest]
        public IEnumerator DiscoveryDeduplicatesAndExpiresWithoutSendingControl()
        {
            var server = Server(5002);
            var control = Server();
            using (var discovery = new PuppyPiDiscovery())
            {
                double start = Time.realtimeSinceStartupAsDouble;
                discovery.Start(start, true);
                double until = start + 0.25;
                while (Time.realtimeSinceStartupAsDouble < until)
                {
                    discovery.Tick(Time.realtimeSinceStartupAsDouble);
                    while (server.Available > 0)
                    {
                        var peer = new IPEndPoint(IPAddress.Any, 0);
                        string request = Encoding.ASCII.GetString(server.Receive(ref peer));
                        Assert.That(request, Does.StartWith(PuppyPiDiscoveryProtocol.RequestPrefix));
                        string nonce = request.Substring(PuppyPiDiscoveryProtocol.RequestPrefix.Length);
                        byte[] response = Encoding.ASCII.GetBytes($"PUPPYPI_HERE/1|{nonce}|0123456789abcdef0123456789abcdef|TestPi|{Port(control)}|2");
                        server.Send(response, response.Length, peer);
                        server.Send(response, response.Length, peer);
                    }
                    yield return null;
                }
                Assert.That(discovery.Devices.Count, Is.EqualTo(1));
                Assert.That(discovery.Devices[0].Port, Is.EqualTo(Port(control)));
                Assert.That(control.Available, Is.Zero, "searching cannot claim a robot's control lease");
                discovery.Tick(start + 4);
                Assert.That(discovery.Searching, Is.False);
                discovery.Tick(start + 11);
                Assert.That(discovery.Devices.Count, Is.Zero);
            }
        }

        [UnityTest]
        public IEnumerator UiDistinguishesNoAckBusyAndDisconnected()
        {
            var server = Server();
            var client = Client(out _);
            var connection = _root.AddComponent<PuppyPiConnection>();
            connection.client = client;
            connection.AddressText = "127.0.0.1";
            connection.PortText = Port(server).ToString();
            connection.ConnectManual();
            Assert.That(connection.Status, Is.EqualTo("연결 중"));
            yield return Pump(server, 0.2f, reason: "not_owner");
            Assert.That(connection.Status, Is.EqualTo("다른 조종기가 사용 중"));
            Assert.That(client.LastCommand.control_active, Is.False);
            connection.Disconnect();
            Assert.That(connection.Status, Is.EqualTo("연결 안 됨"));
            Assert.That(client.networkEnabled, Is.False);
            connection.AddressText = "localhost";
            connection.ConnectManual();
            connection.Disconnect();
            yield return Pump(server, 0.2f);
            Assert.That(client.networkEnabled, Is.False, "late DNS resolution must not reconnect after disconnect");
        }

        [UnityTest]
        public IEnumerator NoAckConnectionTimesOutAndOfflineEmergencySurvivesConnect()
        {
            var server = Server(); // Bound but deliberately silent.
            var client = Client(out _);
            client.EmergencyStop();
            Assert.That(client.Session.Emergency, Is.True);
            var connection = _root.AddComponent<PuppyPiConnection>();
            connection.client = client;
            connection.AddressText = "127.0.0.1";
            connection.PortText = Port(server).ToString();
            connection.ConnectManual();
            Assert.That(client.Session.Emergency, Is.True);
            yield return new WaitForSecondsRealtime(5.2f);
            Assert.That(client.networkEnabled, Is.False);
            Assert.That(connection.Status, Is.EqualTo("연결 안 됨"));
            Assert.That(connection.Notice, Does.StartWith("응답이 없습니다"));
        }
    }
}
#endif
