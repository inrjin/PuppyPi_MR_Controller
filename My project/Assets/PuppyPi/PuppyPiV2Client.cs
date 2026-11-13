using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace PuppyPi
{
    [DefaultExecutionOrder(100)]
    public sealed class PuppyPiV2Client : MonoBehaviour
    {
        public BallGripInput input;
        [Tooltip("Optional component implementing IRobotDriveInput. The existing BallGrip reference is retained for scene compatibility.")]
        public MonoBehaviour inputOverride;
        public bool networkEnabled;
        [Tooltip("Current resolved IPv4 address. Use the Game view connection panel to find/connect to a Pi.")]
        public string robotAddress = "";
        public int port = 5001;
        [Tooltip("Provisional test value; not an agreed robot/Quest policy.")]
        [Range(0.1f, 2f)] public float ackTimeoutSeconds = 0.3f;
        [Range(0.05f, 1f)] public float maxAckAgeSeconds = 0.2f;
        [Range(0.1f, 1f)] public float speedScale = 1f;
        public bool ExternalMotionBlocked { get; private set; }
        public bool StudyMotionBlocked { get; private set; }
        public bool StudyCommandHeld { get; private set; }
        public bool PathCommandHeld { get; private set; }
        public bool PathHeartbeatSuspended { get; private set; }
        public IRobotDriveInput DriveInput
        {
            get
            {
                // Keep Unity's destroyed-object null semantics before converting
                // the component to a plain C# interface.
                MonoBehaviour candidate = inputOverride != null ? inputOverride : input;
                return candidate != null ? candidate as IRobotDriveInput : null;
            }
        }
        public event Action<V2Command> CommandSent;
        public event Action<V2Ack, bool> AckObserved;
        public event Action<string, long> ControlEventSent;
        public event Action<string> PathCancellationRequested;
        public event Action<V2Ack, string> PathAckObserved;
        public V2Session Session { get; private set; } = new V2Session();
        public V2Command LastCommand { get; private set; }
        public long LastSentSequence { get; private set; }
        public string TransportStatus { get; private set; } = "local preview";
        public double AckAge => Now - Session.LastAckTime;
        public string RemoteEndpoint => _remote == null ? "" : _remote.ToString();
        private static double Now => Time.realtimeSinceStartupAsDouble;
        // Operation receipts confirm ingress only; they do not refresh the
        // control session. Bound correlation by the existing cancellation window.
        private const double PathOperationAckMaxAgeSeconds = 1.5;
        private UdpClient _socket;
        private IPEndPoint _remote;
        private double _nextSend;
        private double _retryOpen;
        private bool _paused;
        private bool _focused = true;
        private string _pendingEvent;
        private string _lastAckDiagnostic;
        private IRobotDriveInput _boundInput;
        private bool _suppressPathCancellation;
        private bool _allowAutomaticPathEmergency = true;

        // Only an explicit Connect action starts a new session. Automatic transport
        // recovery keeps the existing UUID and the server's ownership lease rules.
        public bool ConnectTo(string addressText, int targetPort, out string error)
        {
            error = null;
            if (!IPAddress.TryParse(addressText, out IPAddress address) ||
                address.AddressFamily != AddressFamily.InterNetwork || targetPort < 1 || targetPort > 65535 ||
                address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast) || address.GetAddressBytes()[0] >= 224)
            { error = "유효한 IPv4 주소와 포트(1–65535)를 입력하세요."; return false; }
#if UNITY_EDITOR
            if (DriveInput != null && DriveInput.IsSimulation && !IPAddress.IsLoopback(address))
            { error = "실제 Pi 연결 전 BallGripInput의 Editor Preview를 꺼 주세요."; return false; }
#endif
            bool emergency = Session.Emergency || _pendingEvent == "emergency_stop";
            DisconnectSession();
            Session = new V2Session(emergency);
            _pendingEvent = emergency ? "emergency_stop" : null;
            LastCommand = null;
            LastSentSequence = 0;
            _lastAckDiagnostic = null;
            robotAddress = address.ToString();
            port = targetPort;
            _retryOpen = 0;
            _nextSend = Now;
            DriveInput?.Invalidate();
            networkEnabled = true;
            TransportStatus = "waiting for ACK";
            return true;
        }

        private void OnEnable()
        {
            BindInput();
        }

        private void Update()
        {
            BindInput();
            if (!networkEnabled)
            {
                if (_socket != null) { StopHeldPathForTransportClose(); RequestNeutral(); CloseTransport(); }
                TransportStatus = "local preview (network disabled)";
                return;
            }
            if (_socket == null && Now >= _retryOpen) OpenTransport();
            if (_socket == null) return;
            // Expire connectivity BEFORE processing queued ACKs or a new input edge.
            if (Session.CheckTimeout(Now, ackTimeoutSeconds)) RequestNeutral();
            ReceiveAcks();
            Session.ObserveInput(DriveInput != null && DriveInput.Engaged, InputValid);
            if (Now >= _nextSend)
            {
                _nextSend += 0.05; // Preserve average 20 Hz across render-frame rounding.
                if (_nextSend <= Now) _nextSend = Now + 0.05; // Drop missed frames; never burst them.
                if (_pendingEvent != null) SendEvent(_pendingEvent);
                else SendCommand();
            }
        }

        private bool InputValid => !_paused && _focused && !ExternalMotionBlocked && !StudyMotionBlocked && !PathCommandHeld &&
            DriveInput != null && DriveInput.ControlActive;

        private void BindInput()
        {
            var source = DriveInput;
            if (ReferenceEquals(source, _boundInput)) return;
            if (_boundInput != null) _boundInput.NeutralRequested -= RequestNeutral;
            _boundInput = source;
            if (_boundInput != null) _boundInput.NeutralRequested += RequestNeutral;
            Session.Block();
        }

        public void SetStudyMotionBlocked(bool blocked)
        {
            if (StudyMotionBlocked == blocked) return;
            StudyMotionBlocked = blocked;
            if (!blocked) return;
            DriveInput?.Invalidate();
            RequestNeutral();
        }

        // After the displayed GO cue a genuine new grab may arm the session,
        // while the separate trial handshake still holds all motor output at zero.
        public void SetStudyCommandHold(bool held)
        {
            if (StudyCommandHeld == held) return;
            StudyCommandHeld = held;
            if (held && PathCommandHeld) PathCancellationRequested?.Invoke("Study command hold");
            if (held) SendCommand();
        }

        public void SetPathCommandHold(bool held, bool allowAutomaticEmergency = true)
        {
            _allowAutomaticPathEmergency = held ? allowAutomaticEmergency : true;
            if (!held) PathHeartbeatSuspended = false;
            if (PathCommandHeld == held) return;
            PathCommandHeld = held;
            // Enter/leave path control with the ordinary neutral + new-grab rules,
            // but this intentional transition must not cancel the new operation.
            _suppressPathCancellation = true;
            try { DriveInput?.Invalidate(); RequestNeutral(); }
            finally { _suppressPathCancellation = false; }
        }

        public void SetPathHeartbeatSuspended(bool suspended)
        {
            // Suppress heartbeats only while an existing path stop is being
            // resolved. Explicit stop/cancel events still use the same socket.
            PathHeartbeatSuspended = PathCommandHeld && suspended;
        }

        public void SetExternalMotionBlocked(bool blocked)
        {
            if (ExternalMotionBlocked == blocked) return;
            ExternalMotionBlocked = blocked;
            if (!blocked) return;
            DriveInput?.Invalidate();
            RequestNeutral();
        }

        private void OpenTransport()
        {
            _retryOpen = Now + 1.1;
            if (Session.Retired) { TransportStatus = "session disconnected; restart Play/app"; return; }
            if (!IPAddress.TryParse(robotAddress, out IPAddress address) || address.AddressFamily != AddressFamily.InterNetwork || port < 1 || port > 65535)
            { TransportStatus = "set a valid measured IPv4 address / port"; return; }
#if UNITY_EDITOR
            if (DriveInput != null && DriveInput.IsSimulation && !IPAddress.IsLoopback(address))
            { TransportStatus = "Editor simulation permits loopback only"; return; }
#endif
            try
            {
                _remote = new IPEndPoint(address, port);
                _socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
                _socket.Client.Blocking = false;
                _socket.Connect(_remote); // ACKs use this very same endpoint/socket.
                Session.Block();
                DriveInput?.Invalidate();
                TransportStatus = "waiting for ACK";
                SendCommand();
            }
            catch (SocketException e) { FailTransport(e.Message); }
        }

        private void ReceiveAcks()
        {
            try
            {
                // Bound the work so malformed/flooded traffic cannot freeze control updates.
                for (int i = 0; i < 64 && _socket != null && _socket.Available > 0; i++)
                {
                    IPEndPoint source = new IPEndPoint(IPAddress.Any, 0);
                    byte[] bytes = _socket.Receive(ref source);
                    if (!source.Equals(_remote) || bytes.Length > 2048) continue;
                    if (!V2AckParser.TryParse(Encoding.UTF8.GetString(bytes), out V2Ack ack)) continue;
                    if (Session.TryGetAckKind(ack, Now, PathOperationAckMaxAgeSeconds, out string kind) &&
                        (kind == "path" || kind == "path_cancel")) PathAckObserved?.Invoke(ack, kind);
                    // A healthy duplicate is not new control-state evidence,
                    // even before the next heartbeat. Preserve genuine stop or
                    // fault reports carried by the same operation receipt.
                    bool duplicatePathReceipt = kind == "path" && !ack.accepted && ack.reason == "out_of_order" &&
                        !ack.emergency_stop && (ack.state == "idle" || ack.state == "active");
                    bool applied = !duplicatePathReceipt && Session.AcceptAck(ack, Now, maxAckAgeSeconds);
                    AckObserved?.Invoke(ack, applied);
                    if (!applied) continue;
                    if (Debug.isDebugBuild)
                    {
                        string diagnostic = $"{ack.accepted}/{ack.state}/{ack.reason}/{ack.emergency_stop}";
                        if (diagnostic != _lastAckDiagnostic)
                        {
                            _lastAckDiagnostic = diagnostic;
                            Debug.Log($"PUPPYPI_V2_ACK accepted={ack.accepted} state={ack.state} reason={ack.reason} " +
                                $"emergency={ack.emergency_stop} neutral={Session.NeutralConfirmed}", this);
                        }
                    }
                    TransportStatus = ack.accepted ? "ACK accepted (not measured motion)" : "ACK rejected";
                    if (_pendingEvent == Session.LastAckKind && ack.accepted) _pendingEvent = null;
                    if (!Session.NeutralConfirmed && DriveInput != null && DriveInput.ControlActive) DriveInput.Invalidate();
                }
            }
            catch (SocketException e)
            {
                if (e.SocketErrorCode != SocketError.WouldBlock) FailTransport(e.Message);
            }
        }

        private void SendCommand()
        {
            if (_socket == null || (PathCommandHeld && PathHeartbeatSuspended)) return;
#if UNITY_EDITOR
            if (DriveInput != null && DriveInput.IsSimulation && !IPAddress.IsLoopback(_remote.Address))
            {
                Session.Block();
                TransportStatus = "Editor simulation blocked for a real robot";
            }
#endif
            Vector2 axes = DriveInput != null ? DriveInput.Axes : Vector2.zero;
            float scale = float.IsNaN(speedScale) || float.IsInfinity(speedScale) ? 0 : Mathf.Clamp01(speedScale);
            V2Command message = Session.Command(Now, InputValid && !StudyCommandHeld, axes.x * scale, axes.y * scale);
            if (Send(JsonUtility.ToJson(message), message.sequence))
            {
                LastCommand = message;
                CommandSent?.Invoke(message);
            }
        }

        private void SendEvent(string kind)
        {
            if (_socket == null) return;
            V2Event message = Session.Event(Now, kind);
            if (Send(JsonUtility.ToJson(message), message.sequence)) ControlEventSent?.Invoke(kind, message.sequence);
        }

        public bool TrySendPath(IReadOnlyList<Vector2> points, out long sequence, out string error)
        {
            sequence = 0;
            error = null;
            if (!CanSendPath(out error)) return false;
            if (points == null || points.Count < 1 || points.Count > 20)
            { error = "A path requires 1 to 20 map points."; return false; }
            var values = new double[points.Count][];
            for (int i = 0; i < points.Count; i++) values[i] = new[] { (double)points[i].x, points[i].y };
            V2Path message;
            try { message = Session.Path(Now, values); }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
            { error = e.Message; return false; }
            sequence = message.Sequence;
            if (Send(message.ToJson(), sequence)) { ControlEventSent?.Invoke("path", sequence); return true; }
            error = "Path datagram was not sent.";
            return false;
        }

        public bool TryCancelPath(out long sequence, out string error)
            => TrySendPathCancellation(true, out sequence, out error);

        private bool TrySendPathCancellation(bool requireNetworkEnabled, out long sequence, out string error)
        {
            sequence = 0;
            error = null;
            if (_socket == null || (requireNetworkEnabled && !networkEnabled) || Session.Retired || _pendingEvent != null)
            { error = "Control transport unavailable or a stop event has priority."; return false; }
            var message = Session.PathCancel(Now);
            sequence = message.sequence;
            if (Send(JsonUtility.ToJson(message), sequence)) { ControlEventSent?.Invoke("path_cancel", sequence); return true; }
            error = "Path cancellation was not sent.";
            return false;
        }

        private bool CanSendPath(out string error)
        {
            error = null;
            if (_socket == null || !networkEnabled || !isActiveAndEnabled || _paused || !_focused ||
                StudyMotionBlocked || StudyCommandHeld || !PathCommandHeld || PathHeartbeatSuspended || _pendingEvent != null ||
                (_allowAutomaticPathEmergency && (ExternalMotionBlocked || (DriveInput != null && DriveInput.Engaged))))
            { error = "Release the grip and wait for an available control channel."; return false; }
#if UNITY_EDITOR
            if (DriveInput != null && DriveInput.IsSimulation && !IPAddress.IsLoopback(_remote.Address))
            { error = "Editor simulation permits loopback only."; return false; }
#endif
            return true;
        }

        private bool Send(string json, long sequence)
        {
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                _socket.Send(bytes, bytes.Length);
                LastSentSequence = sequence;
                return true;
            }
            catch (SocketException e) { Session.ForgetUnsent(sequence); FailTransport(e.Message); }
            catch (ObjectDisposedException) { Session.ForgetUnsent(sequence); CloseTransport(); }
            return false;
        }

        public void RequestNeutral()
        {
            Session.Block();
            if (PathCommandHeld && _allowAutomaticPathEmergency && !_suppressPathCancellation)
                PathCancellationRequested?.Invoke("Control input invalidated");
            SendCommand();
        }

        public void EmergencyStop()
        {
            Session.LatchEmergency(); // Keep the stop intent even if no socket exists yet.
            _pendingEvent = "emergency_stop";
            DriveInput?.Invalidate();
            SendEvent(_pendingEvent);
        }

        public void ClearEmergency()
        {
            if (PathCommandHeld || (DriveInput != null && DriveInput.Engaged)) return;
            _pendingEvent = "clear_emergency";
            Session.Block();
            SendEvent(_pendingEvent);
        }

        public void DisconnectSession()
        {
            StopHeldPathForTransportClose();
            RequestNeutral();
            if (_socket != null && !Session.Retired) SendEvent("disconnect");
            networkEnabled = false;
            CloseTransport();
            TransportStatus = "disconnected";
        }

        private void StopHeldPathForTransportClose()
        {
            if (!PathCommandHeld) return;
            if (_allowAutomaticPathEmergency)
            {
                EmergencyStop(); // Strict paths keep the original latched-stop policy.
                return;
            }
            // A neutral heartbeat cannot cancel a robot path. Try the ordinary
            // cancellation while the socket exists, including the frame in
            // which networkEnabled was just turned off, then let its watchdog
            // expire rather than renewing the path or latching a global stop.
            TrySendPathCancellation(false, out _, out _);
            SetPathHeartbeatSuspended(true);
        }

        private void FailTransport(string reason)
        {
            CloseTransport();
            TransportStatus = reason;
            _retryOpen = Now + 1.1; // Let the old address's 1s lease expire before reopening.
        }

        private void CloseTransport()
        {
            _socket?.Close();
            _socket = null;
            _remote = null;
            Session.CheckTimeout(double.PositiveInfinity, 0);
            Session.Block();
        }

        private void OnApplicationPause(bool paused) { _paused = paused; if (paused) RequestNeutral(); }
        private void OnApplicationFocus(bool focused) { _focused = focused; if (!focused) RequestNeutral(); }
        private void OnApplicationQuit() { DisconnectSession(); }
        private void OnDisable()
        {
            if (_boundInput != null) _boundInput.NeutralRequested -= RequestNeutral;
            _boundInput = null;
            StopHeldPathForTransportClose();
            RequestNeutral();
            CloseTransport(); // Temporary disable keeps UUID; only final exit sends disconnect.
        }
    }
}
