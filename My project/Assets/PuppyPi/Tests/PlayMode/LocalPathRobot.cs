#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PuppyPi.Tests
{
    // A socket peer, never a robot adapter. All listeners bind exclusively to
    // 127.0.0.1. The simplified kinematics are not a physical tracking model.
    internal sealed class LocalPathRobot : IDisposable
    {
        private sealed class Peer
        {
            public WebSocket Socket;
            public readonly SemaphoreSlim Send = new SemaphoreSlim(1, 1);
            public readonly HashSet<string> Topics = new HashSet<string>();
            public bool GridSent;
        }

        private sealed class Receipt
        {
            public byte[] Bytes;
            public IPEndPoint Endpoint;
        }

        private readonly object _gate = new object();
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private readonly TcpListener _http = new TcpListener(IPAddress.Loopback, 8090);
        private readonly TcpListener _ros = new TcpListener(IPAddress.Loopback, 9090);
        private readonly UdpClient _udp;
        private readonly ReferencePathRobotBackend _reference;
        private ReferencePathRobotSnapshot _referenceSnapshot;
        private readonly List<Peer> _peers = new List<Peer>();
        private readonly List<Receipt> _cancelReceipts = new List<Receipt>();
        private readonly List<string> _events = new List<string>();
        private readonly List<string> _pathReports = new List<string>();
        private readonly Dictionary<string, long> _sequences = new Dictionary<string, long>();
        private readonly Dictionary<string, int> _ports = new Dictionary<string, int>();
        private readonly string _grid;
        private List<double[]> _points = new List<double[]>();
        private string _state = "idle", _error = "";
        private double _x, _y, _yaw, _started, _lastTick, _blockedUntil;
        private double _obstacleX, _obstacleY, _obstacleRadius = .07;
        private double _secondObstacleX, _secondObstacleY, _secondObstacleRadius = .07;
        private bool _secondObstacle;
        private bool _obstacle, _active, _emergency, _motionPermitted = true;
        private int _index, _paths, _cancels, _heartbeats, _nonNeutral, _snapshots, _postAckSnapshots, _collisionSamples, _activePathSnapshots;
        private double _cancelAckAt = double.PositiveInfinity;
        private long _scanNumber;
        private string _requestClientId;
        private long _requestSequence;
        public volatile bool PauseSensors, AllBeamsMissing, IgnoreFollowerStart, DropNextPathAck, HoldCancelAck;
        public volatile bool PauseCommandStatus;
        public volatile bool PeriodicGhosts;
        public volatile bool ResetReferencePose;
        public double ResetReferencePoseX, ResetReferencePoseY, ResetReferencePoseYaw;
        public volatile int CallbackDelayMilliseconds, PathStatusResponseDelayMilliseconds;
        public bool IncludeExecutionIdentity = true;
        public volatile int PathAckDelayMilliseconds;
        public volatile int HeartbeatDropEvery, PathCancelAckDelayMilliseconds, DropPathCancelCount;
        public volatile float HeartbeatBlackoutSeconds;
        public volatile float GhostPeriodSeconds = .8f, GhostDurationSeconds = .1f, GhostRangeMeters = .3f;
        public int UdpPort { get; }
        public string Error { get { lock (_gate) return _error; } }
        public int PathCount { get { lock (_gate) return _paths; } }
        public int CancelCount { get { lock (_gate) return _cancels; } }
        public int HeartbeatCount { get { lock (_gate) return _heartbeats; } }
        public int NonNeutralPathHeartbeats { get { lock (_gate) return _nonNeutral; } }
        public int InactiveSnapshots { get { lock (_gate) return _snapshots; } }
        public int ActivePathSnapshots { get { lock (_gate) return _activePathSnapshots; } }
        public string[] PathReports { get { lock (_gate) return _pathReports.ToArray(); } }
        public int PostCancelAckSnapshots { get { lock (_gate) return _postAckSnapshots; } }
        public string State { get { lock (_gate) return _state; } }
        public double X { get { lock (_gate) return _x; } }
        public double Y { get { lock (_gate) return _y; } }
        public double Yaw { get { lock (_gate) return _yaw; } }
        public string[] Events { get { lock (_gate) return _events.ToArray(); } }
        public int LastPathPointCount { get { lock (_gate) return _points.Count; } }
        public int CollisionSamples { get { lock (_gate) return _collisionSamples; } }
        public int ReferenceGhostScans { get { lock (_gate) return _referenceSnapshot?.ghostScans ?? 0; } }
        public int ReferenceBlockedCount { get { lock (_gate) return _referenceSnapshot?.blockedCount ?? 0; } }
        public int ReferenceValidRays { get { lock (_gate) return _referenceSnapshot?.validRays ?? 0; } }
        public int ReferenceDroppedHeartbeats { get { lock (_gate) return _referenceSnapshot?.droppedHeartbeats ?? 0; } }
        public int ReferenceDroppedCancels { get { lock (_gate) return _referenceSnapshot?.droppedCancels ?? 0; } }
        public int ReferenceEmergencyCommands { get { lock (_gate) return _referenceSnapshot?.emergencyCommands ?? 0; } }
        public int ReferencePathDatagrams { get { lock (_gate) return _referenceSnapshot?.pathDatagrams ?? 0; } }
        public int ReferenceCancelDatagrams { get { lock (_gate) return _referenceSnapshot?.cancelDatagrams ?? 0; } }
        public int ReferenceEmergencyDatagrams { get { lock (_gate) return _referenceSnapshot?.emergencyDatagrams ?? 0; } }
        public double[][] LastPathPoints { get { lock (_gate) return _points.ConvertAll(point => (double[])point.Clone()).ToArray(); } }
        public bool MotionPermitted { get { lock (_gate) return _motionPermitted; } set { lock (_gate) _motionPermitted = value; } }
        public volatile string MapSession = "full-flow-map-a";
        private static double Now => System.Diagnostics.Stopwatch.GetTimestamp() /
            (double)System.Diagnostics.Stopwatch.Frequency;
        private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        private static string Quote(string text) => "\"" + (text ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        private static string Field(string json, string field) => Regex.Match(json,
            "\\\"" + field + "\\\"\\s*:\\s*\\\"([^\\\"]*)\\\"").Groups[1].Value;

        public LocalPathRobot(bool useRobotReference = false, int baseFreeProbability = 0)
        {
            if (baseFreeProbability < -1 || baseFreeProbability > 100)
                throw new ArgumentOutOfRangeException(nameof(baseFreeProbability));
            // If an application already owns either fixed API port, fail the
            // test visibly. Never stop or replace somebody else's service.
            try
            {
                _http.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _ros.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _http.Start();
                _ros.Start();
                if (useRobotReference)
                {
                    _reference = new ReferencePathRobotBackend();
                    UdpPort = _reference.UdpPort;
                }
                else
                {
                    _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                    UdpPort = ((IPEndPoint)_udp.Client.LocalEndPoint).Port;
                }
                _grid = GridJson(baseFreeProbability);
                _lastTick = Now;
                _ = Task.Run(AcceptHttp);
                _ = Task.Run(AcceptRos);
                if (_udp != null) _ = Task.Run(ReceiveUdp);
                _ = Task.Run(Publish);
            }
            catch
            {
                _http.Stop(); _ros.Stop(); _udp?.Close(); _reference?.Dispose();
                throw;
            }
        }

        public void AddObstacleAhead(double distance = .44, double radius = .07)
        {
            lock (_gate)
            {
                _obstacle = true;
                _obstacleX = _x + Math.Cos(_yaw) * distance;
                _obstacleY = _y + Math.Sin(_yaw) * distance;
                _obstacleRadius = radius;
            }
        }

        public void RemoveObstacle() { lock (_gate) _obstacle = false; }

        public void AddSecondObstacleAhead(double distance = .5, double radius = .07)
        {
            lock (_gate)
            {
                _secondObstacle = true;
                _secondObstacleX = _x + Math.Cos(_yaw) * distance;
                _secondObstacleY = _y + Math.Sin(_yaw) * distance;
                _secondObstacleRadius = radius;
            }
        }

        public void ApplyReferenceSettings()
        {
            // A fault must be armed in the pure Python peer before the next
            // datagram; waiting for its periodic publisher can miss that packet.
            lock (_gate)
            {
                if (_reference == null) throw new InvalidOperationException("The actual reference peer is required.");
                RefreshReference();
            }
        }

        public void ReleaseCancelReceipts()
        {
            if (_reference != null)
            {
                HoldCancelAck = false;
                _reference.ReleaseCancelReceipts();
                return;
            }
            Receipt[] pending;
            lock (_gate)
            {
                HoldCancelAck = false;
                pending = _cancelReceipts.ToArray();
                _cancelReceipts.Clear();
            }
            foreach (Receipt receipt in pending) _ = SendReceipt(receipt, 0, true);
        }

        private void RecordError(Exception error)
        {
            if (_cancel.IsCancellationRequested) return;
            lock (_gate) _error = error.GetType().Name + ": " + error.Message;
        }

        private async Task ReceiveUdp()
        {
            try
            {
                while (!_cancel.IsCancellationRequested)
                {
                    UdpReceiveResult packet = await _udp.ReceiveAsync();
                    string json = Encoding.UTF8.GetString(packet.Buffer);
                    string type = Field(json, "type"), uuid = Field(json, "client_id");
                    long sequence = long.Parse(Regex.Match(json, "\\\"sequence\\\"\\s*:\\s*(\\d+)").Groups[1].Value,
                        CultureInfo.InvariantCulture);
                    int delay = 0;
                    bool drop = false, hold = false, cancelReceipt = false;
                    string ackState;
                    lock (_gate)
                    {
                        if (_sequences.TryGetValue(uuid, out long previous) && sequence <= previous)
                            _error = "Non-monotonic sequence on the actual shared UDP socket.";
                        if (_ports.TryGetValue(uuid, out int port) && port != packet.RemoteEndPoint.Port)
                            _error = "Path and heartbeat used different UDP source ports.";
                        _sequences[uuid] = sequence;
                        _ports[uuid] = packet.RemoteEndPoint.Port;
                        if (type == "path")
                        {
                            _paths++;
                            _events.Add("path:" + _paths);
                            if (_paths > 1 && _cancelAckAt < double.PositiveInfinity && _postAckSnapshots == 0)
                                _error = "Replacement arrived before a post-ACK inactive HTTP snapshot.";
                            _points = ParsePoints(json);
                            _requestClientId = uuid; _requestSequence = sequence;
                            _index = 0; _started = Now; _blockedUntil = 0;
                            _active = !IgnoreFollowerStart;
                            _state = _active ? "drive" : "idle";
                            drop = DropNextPathAck; DropNextPathAck = false;
                            delay = PathAckDelayMilliseconds;
                        }
                        else if (type == "path_cancel")
                        {
                            _cancels++;
                            _events.Add("cancel:" + _cancels);
                            if (_state != "cancelled") { _cancelAckAt = double.PositiveInfinity; _postAckSnapshots = 0; }
                            _active = false; _state = "cancelled";
                            hold = HoldCancelAck;
                            cancelReceipt = true;
                        }
                        else if (type == "command")
                        {
                            _heartbeats++;
                            if (_active && json.Contains("\"control_active\":true")) _nonNeutral++;
                        }
                        else if (type == "emergency_stop")
                        {
                            _emergency = true; _active = false; _state = "cancelled";
                            _events.Add("emergency_stop");
                        }
                        else if (type == "clear_emergency") _emergency = false;
                        else if (type == "disconnect") { _active = false; _state = "cancelled"; }
                        ackState = _emergency ? "emergency_stop" : type == "path_cancel" ? "awaiting_neutral" : "idle";
                    }
                    var receipt = new Receipt { Endpoint = packet.RemoteEndPoint,
                        Bytes = Encoding.UTF8.GetBytes("{\"protocol\":2,\"type\":\"ack\",\"client_id\":" + Quote(uuid) +
                            ",\"sequence\":" + sequence + ",\"accepted\":true,\"reason\":\"ok\",\"emergency_stop\":" +
                            (_emergency ? "true" : "false") + ",\"state\":" + Quote(ackState) + "}") };
                    if (hold) { lock (_gate) _cancelReceipts.Add(receipt); }
                    else if (!drop) _ = SendReceipt(receipt, delay, cancelReceipt);
                }
            }
            catch (Exception error) when (error is SocketException || error is ObjectDisposedException || error is OperationCanceledException)
            { RecordError(error); }
            catch (Exception error) { RecordError(error); }
        }

        private async Task SendReceipt(Receipt receipt, int milliseconds, bool cancellation)
        {
            try
            {
                if (milliseconds > 0) await Task.Delay(milliseconds, _cancel.Token);
                if (cancellation) lock (_gate) { _cancelAckAt = Now; _events.Add("cancel_ack"); }
                await _udp.SendAsync(receipt.Bytes, receipt.Bytes.Length, receipt.Endpoint);
            }
            catch (Exception error) when (error is SocketException || error is ObjectDisposedException || error is OperationCanceledException)
            { RecordError(error); }
        }

        private static List<double[]> ParsePoints(string json)
        {
            var points = new List<double[]>();
            const string numeric = @"(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)";
            foreach (Match match in Regex.Matches(json, @"\[\s*" + numeric + @"\s*,\s*" + numeric + @"\s*\]"))
                points.Add(new[] { double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                    double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) });
            if (points.Count == 0 || points.Count > 20) throw new InvalidDataException("Invalid path waypoint count.");
            return points;
        }

        private void Tick()
        {
            if (_reference != null)
            {
                RefreshReference();
                return;
            }
            double now = Now, dt = Math.Min(.1, now - _lastTick);
            _lastTick = now;
            try
            {
                if (!_active) return;
                // Model the outer physical arbiter stopping output independently
                // of the follower, whose path state may still report active.
                if (!_motionPermitted) return;
                while (_index < _points.Count && Distance(_x, _y, _points[_index][0], _points[_index][1]) <= .10) _index++;
                if (_index >= _points.Count) { _active = false; _state = "done"; return; }
                double target = Math.Atan2(_points[_index][1] - _y, _points[_index][0] - _x);
                double angle = Wrap(target - _yaw);
                if (Math.Abs(angle) > .08)
                {
                    _yaw += Math.Sign(angle) * Math.Min(Math.Abs(angle), .14 * dt);
                    _state = "rotate";
                    return;
                }
                int frontPoints = 0;
                if (_obstacle)
                {
                    for (int i = 0; i < 450; i++)
                    {
                        if (MissingBeam(i)) continue;
                        double ray = i * 2 * Math.PI / 450;
                        double range = ObstacleRange(_yaw + ray);
                        if (!double.IsNaN(range) && range * Math.Cos(ray) > .16 && range * Math.Cos(ray) < .46 &&
                            Math.Abs(range * Math.Sin(ray)) < .17) frontPoints++;
                    }
                }
                if (frontPoints >= 2) _blockedUntil = now + 1;
                if (now < _blockedUntil) { _state = "blocked"; return; }
                _state = "drive";
                _x += Math.Cos(_yaw) * .125 * dt;
                _y += Math.Sin(_yaw) * .125 * dt;
            }
            finally { SampleBodyContact(); }
        }

        private void RefreshReference()
        {
            _reference.Configure(new ReferencePathRobotConfig
            {
                pauseSensors = PauseSensors, allBeamsMissing = AllBeamsMissing,
                ignoreFollowerStart = IgnoreFollowerStart, dropNextPathAck = DropNextPathAck,
                holdCancelAck = HoldCancelAck, pathAckDelayMilliseconds = PathAckDelayMilliseconds,
                motionPermitted = _motionPermitted, obstacle = _obstacle,
                obstacleX = _obstacleX, obstacleY = _obstacleY, obstacleRadius = _obstacleRadius,
                periodicGhosts = PeriodicGhosts, callbackDelayMilliseconds = CallbackDelayMilliseconds,
                resetPose = ResetReferencePose,
                resetPoseX = ResetReferencePoseX, resetPoseY = ResetReferencePoseY,
                resetPoseYaw = ResetReferencePoseYaw,
                ghostPeriodSeconds = GhostPeriodSeconds, ghostDurationSeconds = GhostDurationSeconds,
                ghostRangeMeters = GhostRangeMeters,
                secondObstacle = _secondObstacle, secondObstacleX = _secondObstacleX,
                secondObstacleY = _secondObstacleY, secondObstacleRadius = _secondObstacleRadius,
                heartbeatDropEvery = HeartbeatDropEvery, heartbeatBlackoutSeconds = HeartbeatBlackoutSeconds,
                pathCancelAckDelayMilliseconds = PathCancelAckDelayMilliseconds, dropPathCancelCount = DropPathCancelCount
            });
            DropNextPathAck = false;
            ResetReferencePose = false;
            DropPathCancelCount = 0;
            HeartbeatBlackoutSeconds = 0;
            _referenceSnapshot = _reference.Snapshot();
            _x = _referenceSnapshot.x; _y = _referenceSnapshot.y; _yaw = _referenceSnapshot.yaw;
            _state = _referenceSnapshot.state; _active = _referenceSnapshot.active;
            _index = _referenceSnapshot.index;
            _paths = _referenceSnapshot.paths; _cancels = _referenceSnapshot.cancels;
            _heartbeats = _referenceSnapshot.heartbeats; _nonNeutral = _referenceSnapshot.nonNeutral;
            _collisionSamples = _referenceSnapshot.collisionSamples;
            _scanNumber = _referenceSnapshot.scanNumber;
            _cancelAckAt = _referenceSnapshot.cancelAckAt >= 0 ? _referenceSnapshot.cancelAckAt : double.PositiveInfinity;
            _error = _referenceSnapshot.error ?? "";
            _events.Clear();
            if (_referenceSnapshot.events != null) _events.AddRange(_referenceSnapshot.events);
            if (!string.IsNullOrEmpty(_referenceSnapshot.pointsJson) && _referenceSnapshot.pointsJson != "[]")
                _points = ParsePoints(_referenceSnapshot.pointsJson);
        }

        // Passive oracle: inspect the actual simulated pose, independently of
        // waypoint or occupancy validation. It never alters movement or brakes.
        private void SampleBodyContact()
        {
            if (!_obstacle) return;
            double dx = _obstacleX - _x, dy = _obstacleY - _y;
            double forward = dx * Math.Cos(_yaw) + dy * Math.Sin(_yaw);
            double lateral = -dx * Math.Sin(_yaw) + dy * Math.Cos(_yaw);
            double closestForward = Math.Max(-.163, Math.Min(.163, forward));
            double closestLateral = Math.Max(-.1245, Math.Min(.1245, lateral));
            double distanceSquared = (forward - closestForward) * (forward - closestForward) +
                (lateral - closestLateral) * (lateral - closestLateral);
            if (distanceSquared <= _obstacleRadius * _obstacleRadius) _collisionSamples++;
        }

        private static double Wrap(double angle) => Math.Atan2(Math.Sin(angle), Math.Cos(angle));
        private static double Distance(double x, double y, double targetX, double targetY) =>
            Math.Sqrt((targetX - x) * (targetX - x) + (targetY - y) * (targetY - y));

        private double ObstacleRange(double angle)
        {
            if (!_obstacle) return double.NaN;
            double dx = _obstacleX - _x, dy = _obstacleY - _y;
            double projection = dx * Math.Cos(angle) + dy * Math.Sin(angle);
            double perpendicularSquared = dx * dx + dy * dy - projection * projection;
            if (projection <= 0 || perpendicularSquared > _obstacleRadius * _obstacleRadius) return double.NaN;
            double range = projection - Math.Sqrt(Math.Max(0, _obstacleRadius * _obstacleRadius - perpendicularSquared));
            return range >= .12 ? range : double.NaN;
        }

        private async Task AcceptHttp()
        {
            while (!_cancel.IsCancellationRequested)
            {
                try { TcpClient client = await _http.AcceptTcpClientAsync(); _ = Task.Run(() => ServeHttp(client)); }
                catch (Exception error) when (error is ObjectDisposedException || error is SocketException)
                { RecordError(error); break; }
            }
        }

        private async Task ServeHttp(TcpClient client)
        {
            try
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    string header = await ReadHeader(stream);
                    string path = header.Split(' ')[1];
                    if (path == "/api/path" && PathStatusResponseDelayMilliseconds > 0)
                        await Task.Delay(PathStatusResponseDelayMilliseconds, _cancel.Token);
                    string json;
                    lock (_gate)
                    {
                        if (_reference != null) RefreshReference();
                        if (path == "/api/path")
                        {
                            if (_active) _activePathSnapshots++;
                            if (!_active)
                            {
                                _snapshots++;
                                if (Now >= _cancelAckAt) { _postAckSnapshots++; _events.Add("inactive_after_ack"); }
                            }
                            json = PathJson();
                            _pathReports.Add(json);
                        }
                        else if (path == "/api/map") json = MapJson();
                        else if (path == "/api/status") json = "{\"media\":\"puppypi-media/1\",\"arbiter\":{\"engine\":\"ready\",\"age_s\":0,\"vr_permitted\":" + (_motionPermitted ? "true" : "false") + "}}";
                        else json = "{\"media\":\"puppypi-media/1\",\"camera\":{},\"map\":{}}";
                    }
                    byte[] body = Encoding.UTF8.GetBytes(json);
                    byte[] reply = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(reply, 0, reply.Length, _cancel.Token);
                    await stream.WriteAsync(body, 0, body.Length, _cancel.Token);
                }
            }
            catch (Exception error) when (error is IOException || error is SocketException || error is OperationCanceledException || error is ObjectDisposedException)
            {
                // Generation changes abort otherwise valid read-only requests.
                if (error is InvalidDataException) RecordError(error);
            }
        }

        private string MapJson() => "{\"media\":\"puppypi-media/1\",\"frame\":\"map\",\"session\":" + Quote(MapSession) +
            ",\"width_px\":120,\"height_px\":120,\"left_m\":-3,\"bottom_m\":-3,\"px_per_m\":20,\"age_s\":0,\"robot\":{\"x_m\":" +
            Number(_x) + ",\"y_m\":" + Number(_y) + ",\"yaw_rad\":" + Number(_yaw) + "}}";

        private string PathJson()
        {
            if (_reference != null) return _referenceSnapshot.pathJson;
            var json = new StringBuilder("{\"media\":\"puppypi-media/1\",\"age_s\":0,\"session_state\":");
            json.Append(Quote(_active ? "active" : "idle")).Append(",\"emergency_stop\":").Append(_emergency ? "true" : "false")
                .Append(",\"path\":{\"state\":").Append(Quote(_state)).Append(",\"waiting\":null,\"reason\":null,\"index\":")
                .Append(_index).Append(",\"count\":").Append(_points.Count).Append(",\"elapsed_s\":").Append(Number(_started == 0 ? 0 : Now - _started)).Append(",\"points\":[");
            for (int i = 0; i < _points.Count; i++)
            {
                if (i != 0) json.Append(',');
                json.Append('[').Append(Number(_points[i][0])).Append(',').Append(Number(_points[i][1])).Append(']');
            }
            json.Append(']');
            if (IncludeExecutionIdentity && _requestClientId != null)
                json.Append(",\"request_client_id\":").Append(Quote(_requestClientId))
                    .Append(",\"request_sequence\":").Append(_requestSequence);
            return json.Append("}}").ToString();
        }

        private async Task AcceptRos()
        {
            while (!_cancel.IsCancellationRequested)
            {
                try { TcpClient client = await _ros.AcceptTcpClientAsync(); _ = Task.Run(() => ServeRos(client)); }
                catch (Exception error) when (error is ObjectDisposedException || error is SocketException)
                { RecordError(error); break; }
            }
        }

        private async Task ServeRos(TcpClient client)
        {
            Peer peer = null;
            try
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    string header = await ReadHeader(stream), key = null;
                    foreach (string line in header.Split(new[] { "\r\n" }, StringSplitOptions.None))
                        if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)) key = line.Substring(line.IndexOf(':') + 1).Trim();
                    if (key == null) throw new InvalidDataException("Missing WebSocket key.");
                    string accept;
                    using (SHA1 sha = SHA1.Create()) accept = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                    byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
                    await stream.WriteAsync(response, 0, response.Length, _cancel.Token);
                    using (WebSocket socket = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(10)))
                    {
                        peer = new Peer { Socket = socket };
                        lock (_gate) _peers.Add(peer);
                        var buffer = new byte[4096];
                        while (!_cancel.IsCancellationRequested)
                        {
                            using (var message = new MemoryStream())
                            {
                                WebSocketReceiveResult part;
                                do
                                {
                                    part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancel.Token);
                                    if (part.MessageType != WebSocketMessageType.Text) return;
                                    message.Write(buffer, 0, part.Count);
                                    if (message.Length > 65536) throw new InvalidDataException("Oversize test request.");
                                } while (!part.EndOfMessage);
                                string json = Encoding.UTF8.GetString(message.ToArray());
                                if (Field(json, "op") == "subscribe") lock (_gate) peer.Topics.Add(Field(json, "topic"));
                                else if (Field(json, "service") == "/lidar_mapper/get_parameters")
                                    await Send(peer, CalibrationJson(Field(json, "id")));
                            }
                        }
                    }
                }
            }
            catch (Exception error) when (error is IOException || error is WebSocketException || error is SocketException || error is OperationCanceledException || error is ObjectDisposedException)
            {
                // Map session setup intentionally replaces an early socket.
                if (error is InvalidDataException) RecordError(error);
            }
            finally { if (peer != null) lock (_gate) _peers.Remove(peer); }
        }

        private async Task Publish()
        {
            try
            {
                while (!_cancel.IsCancellationRequested)
                {
                    Peer[] peers;
                    string pose, scan, arbiterStatus;
                    bool publishSensors;
                    long number;
                    lock (_gate)
                    {
                        Tick();
                        number = _reference != null ? _scanNumber : ++_scanNumber;
                        publishSensors = !PauseSensors;
                        peers = _peers.ToArray();
                        pose = _reference != null ? _referenceSnapshot.poseJson : PoseJson(number);
                        scan = _reference != null ? _referenceSnapshot.scanJson : ScanJson(number);
                        arbiterStatus = StatusJson(RobotRosProtocol.ArbiterTopic,
                            "{\"engine\":\"ready\",\"owner\":null,\"estop\":false,\"vr_permitted\":" +
                            (_motionPermitted ? "true" : "false") + ",\"busy\":false}");
                    }
                    foreach (Peer peer in peers)
                    {
                        bool map, position, laser, status, arbiter, sendGrid;
                        lock (_gate)
                        {
                            map = peer.Topics.Contains("/map"); position = peer.Topics.Contains("/lidar_map/pose"); laser = peer.Topics.Contains("/scan");
                            status = peer.Topics.Contains(RobotRosProtocol.StatusTopic); arbiter = peer.Topics.Contains(RobotRosProtocol.ArbiterTopic);
                            sendGrid = map && (!peer.GridSent || number % 5 == 0); peer.GridSent |= sendGrid;
                        }
                        try
                        {
                            if (sendGrid) await Send(peer, _grid);
                            if (publishSensors && position) await Send(peer, pose);
                            if (publishSensors && laser) await Send(peer, scan);
                            if (status && !PauseCommandStatus) await Send(peer, StatusJson(RobotRosProtocol.StatusTopic, "{\"engine\":\"ready\",\"state\":\"ready\",\"estop\":false}"));
                            if (arbiter && !PauseCommandStatus) await Send(peer, arbiterStatus);
                        }
                        catch (Exception error) when (error is WebSocketException || error is IOException || error is ObjectDisposedException)
                        { /* A replaced map session closes its old read-only socket. */ }
                    }
                    await Task.Delay(100, _cancel.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { RecordError(error); }
        }

        private async Task Send(Peer peer, string json)
        {
            await peer.Send.WaitAsync(_cancel.Token);
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await peer.Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cancel.Token);
            }
            finally { peer.Send.Release(); }
        }

        private async Task<string> ReadHeader(NetworkStream stream)
        {
            var header = new StringBuilder();
            var one = new byte[1];
            while (header.Length < 8192 && !header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(one, 0, 1, _cancel.Token) == 0) throw new IOException("Short request header.");
                header.Append((char)one[0]);
            }
            if (header.Length >= 8192) throw new InvalidDataException("Oversize request header.");
            return header.ToString();
        }

        private string PoseJson(long number)
        {
            return "{\"op\":\"publish\",\"topic\":\"/lidar_map/pose\",\"msg\":{\"header\":{\"frame_id\":\"map\",\"stamp\":" + Stamp(number) +
                "},\"pose\":{\"position\":{\"x\":" + Number(_x) + ",\"y\":" + Number(_y) + "},\"orientation\":{\"z\":" +
                Number(Math.Sin(_yaw / 2)) + ",\"w\":" + Number(Math.Cos(_yaw / 2)) + "}}}}";
        }

        private string ScanJson(long number)
        {
            double increment = 2 * Math.PI / 450;
            var json = new StringBuilder("{\"op\":\"publish\",\"topic\":\"/scan\",\"msg\":{\"header\":{\"frame_id\":\"laser\",\"stamp\":");
            json.Append(Stamp(number)).Append("},\"angle_min\":0,\"angle_max\":").Append(Number(increment * 449))
                .Append(",\"angle_increment\":").Append(Number(increment)).Append(",\"range_min\":0.02,\"range_max\":12,\"ranges\":[");
            for (int i = 0; i < 450; i++)
            {
                if (i != 0) json.Append(',');
                double range = ObstacleRange(_yaw + i * increment);
                if (MissingBeam(i)) json.Append("null");
                else json.Append(Number(double.IsNaN(range) ? 3 : range));
            }
            return json.Append("]}}").ToString();
        }

        // The same 13 source bins are absent from both the MR wire scan and
        // the local follower's frontal-point check; no phantom braking points.
        private bool MissingBeam(int index) => AllBeamsMissing || index >= 435 && index <= 447;

        private static string Stamp(long number) => "{\"sec\":" + (1000 + number / 10) + ",\"nanosec\":" + number % 10 * 100000000 + "}";
        private static string StatusJson(string topic, string payload) =>
            "{\"op\":\"publish\",\"topic\":" + Quote(topic) + ",\"msg\":{\"data\":" + Quote(payload) + "}}";
        private static string CalibrationJson(string id) => "{\"op\":\"service_response\",\"service\":\"/lidar_mapper/get_parameters\",\"id\":" + Quote(id) +
            ",\"result\":true,\"values\":{\"values\":[{\"type\":3,\"double_value\":0},{\"type\":3,\"double_value\":0},{\"type\":3,\"double_value\":0}," +
            "{\"type\":4,\"string_value\":\"map\"},{\"type\":4,\"string_value\":\"base_footprint\"},{\"type\":3,\"double_value\":0.12},{\"type\":3,\"double_value\":11.5}]}}";
        internal static string GridJson(int value = 0)
        {
            if (value < -1 || value > 100) throw new ArgumentOutOfRangeException(nameof(value));
            var json = new StringBuilder("{\"op\":\"publish\",\"topic\":\"/map\",\"msg\":{\"header\":{\"frame_id\":\"map\"},\"info\":{\"width\":120,\"height\":120,\"resolution\":0.05,\"origin\":{\"position\":{\"x\":-3,\"y\":-3},\"orientation\":{\"w\":1}}},\"data\":[");
            for (int i = 0; i < 120 * 120; i++) { if (i != 0) json.Append(','); json.Append(value); }
            return json.Append("]}}").ToString();
        }

        public void Dispose()
        {
            _cancel.Cancel();
            _http.Stop(); _ros.Stop(); _udp?.Close();
            lock (_gate) foreach (Peer peer in _peers) peer.Socket.Abort();
            _reference?.Dispose();
        }
    }
}
#endif
