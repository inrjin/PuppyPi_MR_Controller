#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace PuppyPi.Tests
{
    public class RobotRosBridgeTests
    {
        private GameObject _root;
        private LocalRosBridgeServer _server;

        [Serializable] private sealed class WireMessage { public string op, topic, service, id; }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            if (_root != null) Object.Destroy(_root);
            _server?.Dispose();
            yield return null;
        }

        private RobotRosBridge Connect()
        {
            _server = new LocalRosBridgeServer();
            _root = new GameObject("ROS bridge loopback fixture");
            var bridge = _root.AddComponent<RobotRosBridge>();
            bridge.connectTimeoutSeconds = 2f;
            bridge.reconnectDelaySeconds = 0.1f;
            bridge.serviceTimeoutSeconds = 1f;
            bridge.statusFreshSeconds = 0.1f;
            Assert.That(bridge.Connect("127.0.0.1", _server.Port), Is.True);
            return bridge;
        }

        private static IEnumerator Until(Func<bool> predicate, string message, float timeout = 5f)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + timeout;
            while (!predicate() && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.That(predicate(), Is.True, message);
        }

        private static IEnumerator Sent(Task send)
        {
            yield return Until(() => send.IsCompleted, "loopback WebSocket send timed out");
            send.GetAwaiter().GetResult();
        }

        private int Count(string operation, string serviceOrTopic)
        {
            int count = 0;
            foreach (string json in _server.Messages)
            {
                var message = JsonUtility.FromJson<WireMessage>(json);
                if (message.op == operation && (message.service == serviceOrTopic || message.topic == serviceOrTopic)) count++;
            }
            return count;
        }

        [UnityTest]
        public IEnumerator ResponsesRequireRequestCorrelationAndTriggerSuccessWhileStopBypassesPendingRequests()
        {
            RobotRosBridge bridge = Connect();
            var results = new Dictionary<string, RobotRosCommandResult>();
            bridge.CommandCompleted += result => results.Add(result.Id, result);
            yield return Until(() => bridge.IsConnected, "ROS bridge failed to connect: " + _server.Error);
            yield return Until(() => Count("subscribe", RobotRosProtocol.StatusTopic) == 1 &&
                Count("subscribe", RobotRosProtocol.ArbiterTopic) == 1, "ROS status subscriptions did not arrive");

            string stand = bridge.Stand();
            Assert.That(stand, Is.Not.Null);
            yield return Until(() => Count("call_service", RobotRosProtocol.StandService) == 1, "stand request did not arrive");
            yield return Sent(_server.Reply("wrong-id", RobotRosProtocol.StandService, true, true));
            yield return null;
            Assert.That(bridge.HasPendingCommand, Is.True, "unrelated service response must not complete a request");
            yield return Sent(_server.Reply(stand, RobotRosProtocol.ResetService, true, true));
            yield return null;
            Assert.That(bridge.HasPendingCommand, Is.True, "matching id with a different service must be ignored");
            yield return Sent(_server.Reply(stand, RobotRosProtocol.StandService, true, false));
            yield return Until(() => results.ContainsKey(stand), "matching Empty response was not delivered");
            Assert.That(results[stand].Success, Is.True, "std_srvs/Empty does not have a Trigger success field");
            Assert.That(results[stand].Acknowledged, Is.True);

            string unansweredStand = bridge.Stand();
            yield return Until(() => Count("call_service", RobotRosProtocol.StandService) == 2, "pending stand must be transmitted before requesting stop");
            string stop = bridge.StopRobot();
            Assert.That(stop, Is.Not.Null, "whole-robot stop must remain available during another service request");
            yield return Until(() => Count("call_service", RobotRosProtocol.StopService) == 1, "stop request was blocked by pending stand");
            yield return Sent(_server.Reply(stop, RobotRosProtocol.StopService, true, false, "robot refused stop"));
            yield return Until(() => results.ContainsKey(stop), "Trigger response was not delivered");
            Assert.That(results[stop].Success, Is.False, "rosbridge result:true is not Trigger values.success:true");
            Assert.That(results[stop].Acknowledged, Is.True);
            Assert.That(results[stop].Message, Does.Contain("refused"));
            yield return Until(() => results.ContainsKey(unansweredStand), "missing service response did not time out");
            Assert.That(results[unansweredStand].TimedOut, Is.True);
            Assert.That(results[unansweredStand].Success, Is.False);

            yield return Sent(_server.Publish(RobotRosProtocol.StatusTopic, "{\"engine\":\"test\",\"estop\":false}"));
            yield return Sent(_server.Publish(RobotRosProtocol.ArbiterTopic,
                "{\"owner\":\"action\",\"estop\":true,\"estop_sources\":[\"manual\"],\"vr_permitted\":false,\"vr_block_reason\":\"busy\"}"));
            yield return Until(() => bridge.RobotStatus != null && bridge.ArbiterState != null, "robot status publications were not decoded");
            Assert.That(bridge.RobotStatus.engine, Is.EqualTo("test"));
            Assert.That(bridge.ArbiterState.owner, Is.EqualTo("action"));
            Assert.That(bridge.ArbiterState.estop_sources, Is.EqualTo(new[] { "manual" }));
            Assert.That(bridge.ArbiterState.vr_permitted, Is.False);
            Assert.That(bridge.ArbiterStateFresh, Is.True);
            yield return new WaitForSeconds(0.2f);
            Assert.That(bridge.RobotStatusFresh, Is.False, "old status must not appear current");
            Assert.That(bridge.ArbiterStateFresh, Is.False);
        }

        [UnityTest]
        public IEnumerator EmergencyStopDiscardsOlderUnsentResetPoseAndActionRequests()
        {
            RobotRosBridge bridge = Connect();
            var results = new Dictionary<string, RobotRosCommandResult>();
            bridge.CommandCompleted += result => results.Add(result.Id, result);
            yield return Until(() => bridge.IsConnected, "ROS bridge failed to connect");

            // Hold the dispatcher at its send boundary so this ordering is
            // deterministic even if the sender's worker thread wakes mid-test.
            object transport = typeof(RobotRosBridge).GetField("_transport", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(bridge);
            object gate = transport.GetType().GetField("Gate").GetValue(transport);
            string reset, pose, action, earlierStop, laterReset, latestStop;
            lock (gate)
            {
                reset = bridge.ResetRobotStop();
                pose = bridge.SetPose("Stand");
                action = bridge.RunAction("bow");
                earlierStop = bridge.StopRobot();
                laterReset = bridge.ResetRobotStop();
                latestStop = bridge.StopRobot();
            }
            Assert.That(latestStop, Is.Not.Null);
            Assert.That(latestStop, Is.Not.EqualTo(earlierStop), "a new stop cannot be swallowed by deduplication of an earlier stop");
            foreach (string cancelled in new[] { reset, pose, action, earlierStop, laterReset })
            {
                Assert.That(results.ContainsKey(cancelled), Is.True, "an unsent superseded request must finish immediately");
                Assert.That(results[cancelled].Success, Is.False);
                Assert.That(results[cancelled].Acknowledged, Is.False);
                Assert.That(results[cancelled].TimedOut, Is.False);
                Assert.That(results[cancelled].Message, Does.Contain("Superseded"));
            }
            yield return Until(() => Count("call_service", RobotRosProtocol.StopService) == 1, "latest stop did not arrive");
            Assert.That(Count("call_service", RobotRosProtocol.ResetService), Is.Zero, "an older reset must not follow and undo the new stop");
            Assert.That(Count("call_service", RobotRosProtocol.ActionService), Is.Zero);
            Assert.That(Count("publish", RobotRosProtocol.PoseTopic), Is.Zero);
            yield return Sent(_server.Reply(latestStop, RobotRosProtocol.StopService, true, true));
            yield return Until(() => results.ContainsKey(latestStop), "latest stop response was not delivered");
            Assert.That(results[latestStop].Success, Is.True);
            Assert.That(bridge.HasPendingCommand, Is.False);
        }

        [UnityTest]
        public IEnumerator ReconnectRestoresSubscriptionsWithoutReplayingRobotCommands()
        {
            RobotRosBridge bridge = Connect();
            bridge.statusFreshSeconds = 10f;
            var results = new Dictionary<string, RobotRosCommandResult>();
            bridge.CommandCompleted += result => results.Add(result.Id, result);
            yield return Until(() => bridge.IsConnected, "ROS bridge failed to connect");
            yield return Sent(_server.Publish(RobotRosProtocol.StatusTopic, "{\"engine\":\"before-reconnect\"}"));
            yield return Sent(_server.Publish(RobotRosProtocol.ArbiterTopic, "{\"owner\":\"before-reconnect\",\"vr_permitted\":true}"));
            yield return Until(() => bridge.RobotStatusFresh && bridge.ArbiterStateFresh, "initial status did not become fresh");
            string oldStand = bridge.Stand();
            yield return Until(() => Count("call_service", RobotRosProtocol.StandService) == 1, "first request did not arrive");
            _server.DropConnection();
            yield return Until(() => results.ContainsKey(oldStand), "disconnect did not finish pending request");
            Assert.That(results[oldStand].Success, Is.False);
            Assert.That(results[oldStand].Acknowledged, Is.False);
            yield return Until(() => _server.ConnectionCount >= 2 && bridge.IsConnected, "ROS bridge did not reconnect");
            yield return Until(() => Count("subscribe", RobotRosProtocol.ArbiterTopic) == 2, "status subscription was not restored");
            Assert.That(bridge.RobotStatusFresh, Is.False, "a recent status from the previous connection is not current");
            Assert.That(bridge.ArbiterStateFresh, Is.False, "the arbiter must report in the new connection before it is trusted");
            yield return Sent(_server.Publish(RobotRosProtocol.StatusTopic, "{\"engine\":\"after-reconnect\"}"));
            yield return Until(() => bridge.RobotStatus?.engine == "after-reconnect", "new robot status was not delivered");
            Assert.That(bridge.RobotStatusFresh, Is.True);
            Assert.That(bridge.ArbiterStateFresh, Is.False, "one new status topic must not refresh the other");
            yield return Sent(_server.Publish(RobotRosProtocol.ArbiterTopic, "{\"owner\":\"after-reconnect\",\"vr_permitted\":true}"));
            yield return Until(() => bridge.RobotStatusFresh && bridge.ArbiterStateFresh, "both status topics must become fresh after reporting again");
            yield return new WaitForSeconds(0.2f);
            Assert.That(Count("call_service", RobotRosProtocol.StandService), Is.EqualTo(1), "reconnect must not replay an actuator request");

            string newStand = bridge.Stand();
            yield return Until(() => Count("call_service", RobotRosProtocol.StandService) == 2, "new request did not arrive after reconnect");
            yield return Sent(_server.Reply(oldStand, RobotRosProtocol.StandService, true, true));
            yield return null;
            Assert.That(bridge.HasPendingCommand, Is.True, "an old-connection response cannot complete a new request");
            yield return Sent(_server.Reply(newStand, RobotRosProtocol.StandService, true, true));
            yield return Until(() => results.ContainsKey(newStand), "new request did not complete");
            Assert.That(results[newStand].Success, Is.True);

            string pose = bridge.SetPose("StandLow");
            yield return Until(() => results.ContainsKey(pose), "pose publish did not finish sending");
            Assert.That(results[pose].Success, Is.True);
            Assert.That(results[pose].Acknowledged, Is.False, "publishing a pose is not confirmation of robot execution");
        }

        // A local RFC6455 endpoint; it never resolves or sends to a robot address.
        private sealed class LocalRosBridgeServer : IDisposable
        {
            private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
            private readonly SemaphoreSlim _send = new SemaphoreSlim(1, 1);
            private readonly List<string> _messages = new List<string>();
            private WebSocket _socket;
            public int Port { get; }
            public int ConnectionCount;
            public string Error = "";
            public string[] Messages { get { lock (_messages) return _messages.ToArray(); } }

            public LocalRosBridgeServer()
            {
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _ = Task.Run(Run);
            }

            public Task Reply(string id, string service, bool result, bool success, string message = "test response") =>
                Send("{\"op\":\"service_response\",\"id\":" + RobotRosProtocol.Quote(id) + ",\"service\":" + RobotRosProtocol.Quote(service) +
                    ",\"result\":" + (result ? "true" : "false") + ",\"values\":{\"success\":" + (success ? "true" : "false") +
                    ",\"message\":" + RobotRosProtocol.Quote(message) + "}}");

            public Task Publish(string topic, string data) => Send("{\"op\":\"publish\",\"topic\":" + RobotRosProtocol.Quote(topic) +
                ",\"msg\":{\"data\":" + RobotRosProtocol.Quote(data) + "}}");

            private async Task Send(string json)
            {
                await _send.WaitAsync(_cancellation.Token);
                try
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(json);
                    await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cancellation.Token);
                }
                finally { _send.Release(); }
            }

            public void DropConnection() => _socket?.Abort();

            private async Task Run()
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    try
                    {
                        using (TcpClient client = await _listener.AcceptTcpClientAsync())
                        using (NetworkStream stream = client.GetStream())
                        {
                            var header = new StringBuilder();
                            var one = new byte[1];
                            while (header.Length < 8192 && !header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                            {
                                if (await stream.ReadAsync(one, 0, 1, _cancellation.Token) == 0) throw new IOException("short handshake");
                                header.Append((char)one[0]);
                            }
                            string key = null;
                            foreach (string line in header.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None))
                                if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)) key = line.Substring(line.IndexOf(':') + 1).Trim();
                            if (key == null) throw new IOException("missing WebSocket key");
                            string accept;
                            using (SHA1 hash = SHA1.Create()) accept = Convert.ToBase64String(hash.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                            byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
                            await stream.WriteAsync(response, 0, response.Length, _cancellation.Token);
                            using (WebSocket socket = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(10)))
                            {
                                _socket = socket;
                                Interlocked.Increment(ref ConnectionCount);
                                var buffer = new byte[4096];
                                while (!_cancellation.IsCancellationRequested)
                                {
                                    using (var message = new MemoryStream())
                                    {
                                        WebSocketReceiveResult received;
                                        do
                                        {
                                            received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancellation.Token);
                                            if (received.MessageType == WebSocketMessageType.Close) throw new IOException("client closed");
                                            message.Write(buffer, 0, received.Count);
                                            if (message.Length > 65536) throw new IOException("oversize test request");
                                        } while (!received.EndOfMessage);
                                        lock (_messages) _messages.Add(Encoding.UTF8.GetString(message.ToArray()));
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception error) when (error is IOException || error is WebSocketException || error is SocketException ||
                        error is OperationCanceledException || error is ObjectDisposedException)
                    {
                        if (!_cancellation.IsCancellationRequested) Error = error.Message;
                    }
                }
            }

            public void Dispose()
            {
                _cancellation.Cancel();
                _socket?.Abort();
                _listener.Stop();
            }
        }
    }
}
#endif
