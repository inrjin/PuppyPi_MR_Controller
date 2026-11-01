using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace PuppyPi
{
    [Serializable]
    public sealed class RobotRuntimeStatus
    {
        public string engine;
        public string state;
        public bool estop;
        public string[] estop_sources;
        public bool action_running;
        public string current_action;
    }

    [Serializable]
    public sealed class RobotArbiterState
    {
        public string engine;
        public string owner;
        public bool estop;
        public string[] estop_sources;
        public bool vr_permitted;
        public string vr_block_reason;
        public bool action_running;
        public string current_action;
        public bool busy;
    }

    public sealed class RobotRosCommandResult
    {
        public string Id { get; internal set; }
        public string Operation { get; internal set; }
        public bool Success { get; internal set; }
        public bool Acknowledged { get; internal set; }
        public bool TimedOut { get; internal set; }
        public string Message { get; internal set; }
    }

    // Owns only the ROS status/pose/service channel. Movement and its ownership
    // remain on the existing PuppyPiV2Client UDP socket.
    public sealed class RobotRosBridge : MonoBehaviour
    {
        [Min(0.1f)] public float connectTimeoutSeconds = 5f;
        [Min(0.1f)] public float reconnectDelaySeconds = 2f;
        [Min(0.1f)] public float serviceTimeoutSeconds = 5f;
        [Min(0.1f)] public float actionTimeoutSeconds = 60f;
        [Min(0.1f)] public float statusFreshSeconds = 2.5f;

        public bool IsConnected => _transport != null && _transport.Connected;
        public string ConnectionState => _transport == null ? "Disconnected" : _transport.State;
        public string LastError { get; private set; } = "";
        public string LastCommandResult { get; private set; } = "";
        public bool LastCommandSucceeded { get; private set; }
        public bool HasPendingCommand => _pending.Count > 0;
        public RobotRuntimeStatus RobotStatus { get; private set; }
        public RobotArbiterState ArbiterState { get; private set; }
        public string LastRobotStatusJson { get; private set; }
        public string LastArbiterStateJson { get; private set; }
        public double LastRobotStatusTime { get; private set; } = double.NegativeInfinity;
        public double LastArbiterStateTime { get; private set; } = double.NegativeInfinity;
        public double LastStatusTime => Math.Max(LastRobotStatusTime, LastArbiterStateTime);
        public bool RobotStatusFresh => RobotStatus != null && IsConnected && _robotStatusEpoch == _transport.Epoch &&
            Time.realtimeSinceStartupAsDouble - LastRobotStatusTime <= statusFreshSeconds;
        public bool ArbiterStateFresh => ArbiterState != null && IsConnected && _arbiterStatusEpoch == _transport.Epoch &&
            Time.realtimeSinceStartupAsDouble - LastArbiterStateTime <= statusFreshSeconds;
        public event Action StateChanged;
        public event Action<RobotRosCommandResult> CommandCompleted;

        private const int MaximumMessageBytes = 65536;
        private const int MaximumQueuedFrames = 32;
        private const int MaximumPendingCommands = 8;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly Dictionary<string, Request> _pending = new Dictionary<string, Request>();
        private readonly List<string> _expired = new List<string>();
        private Transport _transport;
        private Uri _desiredEndpoint;
        private string _observedState;
        private bool _paused;
        private int _robotStatusEpoch = -1;
        private int _arbiterStatusEpoch = -1;

        private sealed class Request
        {
            public string Id, Operation, Service, Json;
            public int Epoch;
            public double Deadline;
            public int Cancelled;
            public bool IsPose;
            // Guarded by Transport.Gate. Calling SendAsync is the point after
            // which delivery cannot be revoked without aborting the connection.
            public bool SendStarted;
        }

        private sealed class Frame
        {
            public int Epoch;
            public string Json;
            public Request SentPose;
            public double ReceivedAt;
        }

        private sealed class Transport
        {
            public readonly Uri Endpoint;
            public readonly double ConnectTimeout, ReconnectDelay;
            public readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
            public readonly object Gate = new object();
            public readonly LinkedList<Request> Outgoing = new LinkedList<Request>();
            public readonly Queue<Frame> Incoming = new Queue<Frame>();
            public volatile bool Connected;
            public int Epoch;
            public volatile string State = "Connecting";
            public volatile string Error = "";
            public ClientWebSocket Socket;

            public Transport(Uri endpoint, double connectTimeout, double reconnectDelay)
            {
                Endpoint = endpoint;
                ConnectTimeout = connectTimeout;
                ReconnectDelay = reconnectDelay;
            }
        }

        [Serializable] private sealed class Header { public string op, id, level; }
        [Serializable] private sealed class StringMessage { public string data; }
        [Serializable] private sealed class Publication { public string topic; public StringMessage msg; }
        [Serializable] private sealed class ServiceValues { public bool success; public string message; }
        [Serializable] private sealed class ServiceResponse { public string id, service; public bool result; public ServiceValues values; }
        [Serializable] private sealed class BridgeError { public string id, msg, level; }

        public bool Connect(string host, int port = 9090)
        {
            if (string.IsNullOrWhiteSpace(host) || Uri.CheckHostName(host.Trim()) == UriHostNameType.Unknown || port < 1 || port > 65535)
            {
                LastError = "Enter a valid robot host and ROS bridge port.";
                return false;
            }
            var endpoint = new UriBuilder("ws", host.Trim(), port, "/").Uri;
            if (_transport != null && _desiredEndpoint == endpoint && _transport.State != "Disconnected") return true;
            StopTransport(false);
            _desiredEndpoint = endpoint;
            RobotStatus = null;
            ArbiterState = null;
            _robotStatusEpoch = _arbiterStatusEpoch = -1;
            LastRobotStatusTime = LastArbiterStateTime = double.NegativeInfinity;
            LastRobotStatusJson = LastArbiterStateJson = null;
            if (isActiveAndEnabled && !_paused) StartTransport();
            return true;
        }

        public void Disconnect() => StopTransport(false);

        public string Stand() => QueueService(RobotRosProtocol.StandService, "Stand (go_home)");
        public string RunAction(string name)
        {
            if (!RobotCommandCatalog.IsAction(name)) return Reject("Unknown action group.");
            return QueueService(RobotRosProtocol.ActionService, "Action " + name, name);
        }
        public string StopRobot() => QueueService(RobotRosProtocol.StopService, "Robot emergency stop", urgent: true);
        public string ResetRobotStop() => QueueService(RobotRosProtocol.ResetService, "Reset robot emergency stop");

        public string SetPose(string name)
        {
            if (!RobotCommandCatalog.TryGetPose(name, out _)) return Reject("Unknown pose.");
            string id = NewRequestId();
            return Enqueue(new Request { Id = id, Operation = "Pose " + name, Json = RobotRosProtocol.Pose(name), IsPose = true }, false);
        }

        private string QueueService(string service, string operation, string action = null, bool urgent = false)
        {
            string id = NewRequestId();
            return Enqueue(new Request { Id = id, Operation = operation, Service = service,
                Json = RobotRosProtocol.Service(service, id, action) }, urgent);
        }

        private string Enqueue(Request request, bool urgent)
        {
            Transport transport = _transport;
            if (transport == null || !transport.Connected) return Reject("ROS bridge is disconnected; command was not sent.");
            if (!urgent && _pending.Count >= MaximumPendingCommands) return Reject("Wait for pending robot requests.");
            request.Epoch = transport.Epoch;
            request.Deadline = Now + Math.Max(0.1, request.Service == RobotRosProtocol.ActionService ? actionTimeoutSeconds : serviceTimeoutSeconds);
            List<Request> superseded = null;
            lock (transport.Gate)
            {
                if (!transport.Connected || request.Epoch != transport.Epoch) return Reject("Connection changed; command was not sent.");
                if (urgent)
                {
                    superseded = new List<Request>();
                    foreach (Request pending in _pending.Values)
                    {
                        // Include a request already popped by the sender but not
                        // yet passed to SendAsync. A newer stop also replaces the
                        // tracking of an older stop, so a reset sent between two
                        // stop clicks cannot make deduplication swallow the latter.
                        if (pending.SendStarted && pending.Service != RobotRosProtocol.StopService) continue;
                        Interlocked.Exchange(ref pending.Cancelled, 1);
                        superseded.Add(pending);
                    }
                    for (LinkedListNode<Request> node = transport.Outgoing.First; node != null;)
                    {
                        LinkedListNode<Request> next = node.Next;
                        if (node.Value.Cancelled != 0) transport.Outgoing.Remove(node);
                        node = next;
                    }
                }
                if (transport.Outgoing.Count >= MaximumPendingCommands + 1) return Reject("Robot request queue is full.");
                _pending.Add(request.Id, request);
                if (urgent) transport.Outgoing.AddFirst(request);
                else transport.Outgoing.AddLast(request);
            }
            if (superseded != null)
                foreach (Request pending in superseded)
                    Finish(pending, false, false, false, pending.SendStarted ?
                        "A newer emergency stop was requested; the earlier stop outcome is unconfirmed." :
                        "Superseded by emergency stop before transmission.");
            LastCommandResult = request.Operation + ": pending";
            LastCommandSucceeded = false;
            StateChanged?.Invoke();
            return request.Id;
        }

        private string Reject(string message)
        {
            LastCommandResult = message;
            LastCommandSucceeded = false;
            StateChanged?.Invoke();
            return null;
        }

        private static string NewRequestId() => "quest-" + Guid.NewGuid().ToString("N");
        private static double Now => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

        private void StartTransport()
        {
            if (_desiredEndpoint == null || _transport != null) return;
            var transport = new Transport(_desiredEndpoint, Math.Max(0.1, connectTimeoutSeconds), Math.Max(0.1, reconnectDelaySeconds));
            _transport = transport;
            LastError = "";
            _observedState = null;
            _ = Task.Run(() => RunTransport(transport));
        }

        private void StopTransport(bool retainEndpoint)
        {
            Transport old = _transport;
            _transport = null;
            if (!retainEndpoint) _desiredEndpoint = null;
            if (old != null)
            {
                old.Connected = false;
                try { old.Cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
                lock (old.Gate)
                {
                    old.Outgoing.Clear();
                    old.Incoming.Clear();
                    old.Socket?.Abort();
                }
            }
            FailPending("Disconnected; pending command outcome is unconfirmed.");
            _observedState = "Disconnected";
            StateChanged?.Invoke();
        }

        private void OnEnable()
        {
            if (!_paused) StartTransport();
        }
        private void OnDisable() => StopTransport(false);
        private void OnApplicationPause(bool paused)
        {
            _paused = paused;
            if (paused) StopTransport(true);
            else if (isActiveAndEnabled) StartTransport();
        }

        private void Update()
        {
            Transport transport = _transport;
            if (transport == null) return;
            if (_observedState != transport.State)
            {
                _observedState = transport.State;
                LastError = transport.Error;
                StateChanged?.Invoke();
            }
            if (_transport != transport) return;
            for (int i = 0; i < MaximumQueuedFrames; i++)
            {
                if (_transport != transport) return;
                Frame frame;
                lock (transport.Gate)
                {
                    if (transport.Incoming.Count == 0) break;
                    frame = transport.Incoming.Dequeue();
                }
                if (!transport.Connected || frame.Epoch != transport.Epoch) continue;
                if (frame.SentPose != null)
                {
                    if (_pending.ContainsKey(frame.SentPose.Id) && frame.ReceivedAt <= frame.SentPose.Deadline)
                        Finish(frame.SentPose, true, false, false, "Published; robot execution is unconfirmed.");
                }
                else HandleMessage(frame.Json, frame.ReceivedAt, frame.Epoch);
            }
            _expired.Clear();
            foreach (var entry in _pending)
            {
                if (!transport.Connected || entry.Value.Epoch != transport.Epoch) _expired.Add(entry.Key);
                else if (Now >= entry.Value.Deadline) _expired.Add(entry.Key);
            }
            foreach (string id in _expired)
            {
                if (!_pending.TryGetValue(id, out Request request)) continue;
                bool timedOut = transport.Connected && request.Epoch == transport.Epoch;
                Finish(request, false, false, timedOut, timedOut ? "Response timed out; execution is unconfirmed." : "Connection lost; execution is unconfirmed.");
            }
        }

        private void HandleMessage(string json, double receivedAt, int epoch)
        {
            try
            {
                Header header = JsonUtility.FromJson<Header>(json);
                if (header == null) return;
                if (header.op == "publish")
                {
                    Publication publication = JsonUtility.FromJson<Publication>(json);
                    string data = publication.msg?.data;
                    if (string.IsNullOrEmpty(data) || data.Length > MaximumMessageBytes / 2) return;
                    if (publication.topic == RobotRosProtocol.StatusTopic)
                    {
                        RobotRuntimeStatus status = JsonUtility.FromJson<RobotRuntimeStatus>(data);
                        if (status == null || !Bounded(status.engine) || !Bounded(status.state) || !Bounded(status.current_action) || !Bounded(status.estop_sources) ||
                            (!HasTopLevelField(data, "engine") && !HasTopLevelField(data, "estop") && !HasTopLevelField(data, "state"))) return;
                        RobotStatus = status;
                        _robotStatusEpoch = epoch;
                        LastRobotStatusJson = data;
                        LastRobotStatusTime = Time.realtimeSinceStartupAsDouble - Math.Max(0, Now - receivedAt);
                    }
                    else if (publication.topic == RobotRosProtocol.ArbiterTopic)
                    {
                        RobotArbiterState arbiter = JsonUtility.FromJson<RobotArbiterState>(data);
                        if (arbiter == null || !Bounded(arbiter.engine) || !Bounded(arbiter.owner) || !Bounded(arbiter.current_action) ||
                            !Bounded(arbiter.estop_sources) || !Bounded(arbiter.vr_block_reason, 256) ||
                            (!HasTopLevelField(data, "owner") && !HasTopLevelField(data, "estop") && !HasTopLevelField(data, "vr_permitted"))) return;
                        ArbiterState = arbiter;
                        _arbiterStatusEpoch = epoch;
                        // Match Python's `a.vr_permitted === false`: missing is unknown,
                        // not an explicit denial from older robot status publishers.
                        if (ArbiterState != null && !HasTopLevelField(data, "vr_permitted")) ArbiterState.vr_permitted = true;
                        LastArbiterStateJson = data;
                        LastArbiterStateTime = Time.realtimeSinceStartupAsDouble - Math.Max(0, Now - receivedAt);
                    }
                    else return;
                    StateChanged?.Invoke();
                }
                else if (header.op == "service_response")
                {
                    ServiceResponse response = JsonUtility.FromJson<ServiceResponse>(json);
                    if (response.id == null || !_pending.TryGetValue(response.id, out Request request) || request.IsPose ||
                        request.Epoch != epoch || response.service != request.Service) return;
                    if (receivedAt > request.Deadline) return;
                    bool trigger = request.Service == RobotRosProtocol.StopService || request.Service == RobotRosProtocol.ResetService;
                    bool success = response.result && (!trigger || (response.values != null && response.values.success));
                    string detail = Truncate(response.values?.message);
                    if (string.IsNullOrEmpty(detail)) detail = success ? "Service accepted the request." : "Service rejected the request.";
                    if (success && request.Service == RobotRosProtocol.ActionService) detail += " Action completion is not confirmed by this response.";
                    Finish(request, success, true, false, detail);
                }
                else if (header.op == "status" && header.level == "error")
                {
                    BridgeError error = JsonUtility.FromJson<BridgeError>(json);
                    LastError = Truncate(error.msg) ?? "ROS bridge reported an error.";
                    if (error.id != null && _pending.TryGetValue(error.id, out Request request) && request.Epoch == epoch)
                        Finish(request, false, true, false, LastError);
                    StateChanged?.Invoke();
                }
            }
            catch (ArgumentException)
            {
                LastError = "Invalid ROS bridge JSON was ignored.";
            }
        }

        private void Finish(Request request, bool success, bool acknowledged, bool timedOut, string message)
        {
            if (!_pending.Remove(request.Id)) return;
            Interlocked.Exchange(ref request.Cancelled, 1);
            LastCommandSucceeded = success;
            LastCommandResult = request.Operation + ": " + message;
            CommandCompleted?.Invoke(new RobotRosCommandResult { Id = request.Id, Operation = request.Operation,
                Success = success, Acknowledged = acknowledged, TimedOut = timedOut, Message = message });
            StateChanged?.Invoke();
        }

        private void FailPending(string message)
        {
            var requests = new List<Request>(_pending.Values);
            foreach (Request request in requests) Finish(request, false, false, false, message);
        }

        private static bool Bounded(string value, int maximum = 128) => value == null || value.Length <= maximum;
        private static bool Bounded(string[] values)
        {
            if (values == null) return true;
            if (values.Length > 16) return false;
            foreach (string value in values) if (!Bounded(value)) return false;
            return true;
        }
        private static string Truncate(string value) => value == null || value.Length <= 512 ? value : value.Substring(0, 512);

        private static bool HasTopLevelField(string json, string name)
        {
            int depth = 0;
            bool quoted = false, escaped = false;
            int start = 0;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (quoted)
                {
                    if (escaped) { escaped = false; continue; }
                    if (c == '\\') { escaped = true; continue; }
                    if (c != '"') continue;
                    quoted = false;
                    if (depth == 1 && i - start == name.Length && string.CompareOrdinal(json, start, name, 0, name.Length) == 0)
                    {
                        int next = i + 1;
                        while (next < json.Length && char.IsWhiteSpace(json[next])) next++;
                        if (next < json.Length && json[next] == ':') return true;
                    }
                }
                else if (c == '"') { quoted = true; start = i + 1; }
                else if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
            }
            return false;
        }

        private static async Task RunTransport(Transport transport)
        {
            CancellationToken lifetime = transport.Cancellation.Token;
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    using (var socket = new ClientWebSocket())
                    {
                        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
                        lock (transport.Gate) transport.Socket = socket;
                        try
                        {
                            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime))
                            {
                                timeout.CancelAfter(TimeSpan.FromSeconds(transport.ConnectTimeout));
                                await socket.ConnectAsync(transport.Endpoint, timeout.Token).ConfigureAwait(false);
                            }
                            int epoch = Interlocked.Increment(ref transport.Epoch);
                            await SendText(socket, RobotRosProtocol.AdvertisePose(), lifetime).ConfigureAwait(false);
                            await SendText(socket, RobotRosProtocol.Subscribe(RobotRosProtocol.StatusTopic), lifetime).ConfigureAwait(false);
                            await SendText(socket, RobotRosProtocol.Subscribe(RobotRosProtocol.ArbiterTopic), lifetime).ConfigureAwait(false);
                            transport.Error = "";
                            transport.State = "Connected";
                            transport.Connected = true;
                            using (var cycle = CancellationTokenSource.CreateLinkedTokenSource(lifetime))
                            {
                                Task receiver = ReceiveLoop(transport, socket, epoch, cycle.Token);
                                Task sender = SendLoop(transport, socket, epoch, cycle.Token);
                                Task first = await Task.WhenAny(receiver, sender).ConfigureAwait(false);
                                cycle.Cancel();
                                socket.Abort();
                                try { await Task.WhenAll(receiver, sender).ConfigureAwait(false); }
                                catch { if (first.IsFaulted) await first.ConfigureAwait(false); }
                                if (!lifetime.IsCancellationRequested) throw new IOException("ROS bridge connection closed.");
                            }
                        }
                        catch (Exception error) when (error is WebSocketException || error is IOException || error is OperationCanceledException || error is InvalidOperationException || error is ObjectDisposedException)
                        {
                            if (!lifetime.IsCancellationRequested) transport.Error = error is OperationCanceledException ? "ROS bridge connection timed out." : error.Message;
                        }
                        finally
                        {
                            transport.Connected = false;
                            lock (transport.Gate)
                            {
                                // Actuator requests belong to one connection only. Reconnect
                                // restores subscriptions, never stale actions or reset commands.
                                foreach (Request request in transport.Outgoing) Interlocked.Exchange(ref request.Cancelled, 1);
                                transport.Outgoing.Clear();
                                transport.Socket = null;
                            }
                        }
                    }
                    if (lifetime.IsCancellationRequested) break;
                    transport.State = "Reconnecting";
                    await Task.Delay(TimeSpan.FromSeconds(transport.ReconnectDelay), lifetime).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { transport.Error = error.Message; }
            finally
            {
                transport.Connected = false;
                transport.State = "Disconnected";
                transport.Cancellation.Dispose();
            }
        }

        private static async Task SendLoop(Transport transport, ClientWebSocket socket, int epoch, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                Request request = null;
                lock (transport.Gate)
                {
                    if (transport.Outgoing.Count > 0)
                    {
                        request = transport.Outgoing.First.Value;
                        transport.Outgoing.RemoveFirst();
                    }
                }
                if (request == null) { await Task.Delay(10, token).ConfigureAwait(false); continue; }
                if (request.Cancelled != 0 || request.Epoch != epoch || Now >= request.Deadline) continue;
                using (var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    sendTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(0.01, request.Deadline - Now)));
                    Task send;
                    lock (transport.Gate)
                    {
                        // Stop enqueue and the actual start of a send share this
                        // lock: claimed-but-unsent requests remain cancellable.
                        if (request.Cancelled != 0 || request.Epoch != epoch || Now >= request.Deadline) continue;
                        request.SendStarted = true;
                        send = SendText(socket, request.Json, sendTimeout.Token);
                    }
                    await send.ConfigureAwait(false);
                }
                if (request.IsPose) PostFrame(transport, new Frame { Epoch = epoch, SentPose = request, ReceivedAt = Now });
            }
        }

        private static async Task ReceiveLoop(Transport transport, ClientWebSocket socket, int epoch, CancellationToken token)
        {
            byte[] buffer = new byte[4096];
            while (!token.IsCancellationRequested)
            {
                using (var message = new MemoryStream())
                {
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close) throw new IOException("ROS bridge closed the connection.");
                        if (result.MessageType != WebSocketMessageType.Text) throw new IOException("Unexpected binary ROS bridge message.");
                        if (message.Length + result.Count > MaximumMessageBytes) throw new IOException("ROS bridge message exceeded 64 KiB.");
                        message.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);
                    string text;
                    try { text = Utf8.GetString(message.GetBuffer(), 0, (int)message.Length); }
                    catch (DecoderFallbackException) { throw new IOException("ROS bridge message was not UTF-8."); }
                    PostFrame(transport, new Frame { Epoch = epoch, Json = text, ReceivedAt = Now });
                }
            }
        }

        private static void PostFrame(Transport transport, Frame frame)
        {
            lock (transport.Gate)
            {
                if (transport.Incoming.Count >= MaximumQueuedFrames) throw new IOException("ROS bridge receive queue overflow.");
                transport.Incoming.Enqueue(frame);
            }
        }

        private static Task SendText(ClientWebSocket socket, string text, CancellationToken token)
        {
            byte[] bytes = Utf8.GetBytes(text);
            return socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
        }
    }
}
