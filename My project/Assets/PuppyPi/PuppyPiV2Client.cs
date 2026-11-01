using System;
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
        public bool networkEnabled;
        [Tooltip("Current resolved IPv4 address. Use the Game view connection panel to find/connect to a Pi.")]
        public string robotAddress = "";
        public int port = 5001;
        [Tooltip("Provisional test value; not an agreed robot/Quest policy.")]
        [Range(0.1f, 2f)] public float ackTimeoutSeconds = 0.3f;
        [Range(0.05f, 1f)] public float maxAckAgeSeconds = 0.2f;
        [Range(0.1f, 1f)] public float speedScale = 1f;
        public bool ExternalMotionBlocked { get; private set; }
        public V2Session Session { get; private set; } = new V2Session();
        public V2Command LastCommand { get; private set; }
        public long LastSentSequence { get; private set; }
        public string TransportStatus { get; private set; } = "local preview";
        public double AckAge => Now - Session.LastAckTime;
        public string RemoteEndpoint => _remote == null ? "" : _remote.ToString();
        private static double Now => Time.realtimeSinceStartupAsDouble;
        private UdpClient _socket;
        private IPEndPoint _remote;
        private double _nextSend;
        private double _retryOpen;
        private bool _paused;
        private bool _focused = true;
        private string _pendingEvent;
        private string _lastAckDiagnostic;

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
            if (input != null && input.editorPreview && !IPAddress.IsLoopback(address))
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
            if (input != null) input.Invalidate();
            networkEnabled = true;
            TransportStatus = "waiting for ACK";
            return true;
        }

        private void OnEnable()
        {
            if (input != null) input.NeutralRequested += RequestNeutral;
        }

        private void Update()
        {
            if (!networkEnabled)
            {
                if (_socket != null) { RequestNeutral(); CloseTransport(); }
                TransportStatus = "local preview (network disabled)";
                return;
            }
            if (_socket == null && Now >= _retryOpen) OpenTransport();
            if (_socket == null) return;
            // Expire connectivity BEFORE processing queued ACKs or a new input edge.
            if (Session.CheckTimeout(Now, ackTimeoutSeconds)) RequestNeutral();
            ReceiveAcks();
            Session.ObserveInput(input != null && input.Grabbed, InputValid);
            if (Now >= _nextSend)
            {
                _nextSend += 0.05; // Preserve average 20 Hz across render-frame rounding.
                if (_nextSend <= Now) _nextSend = Now + 0.05; // Drop missed frames; never burst them.
                if (_pendingEvent != null) SendEvent(_pendingEvent);
                else SendCommand();
            }
        }

        private bool InputValid => !_paused && _focused && !ExternalMotionBlocked && input != null && input.ControlActive;

        public void SetExternalMotionBlocked(bool blocked)
        {
            if (ExternalMotionBlocked == blocked) return;
            ExternalMotionBlocked = blocked;
            if (!blocked) return;
            if (input != null) input.Invalidate();
            RequestNeutral();
        }

        private void OpenTransport()
        {
            _retryOpen = Now + 1.1;
            if (Session.Retired) { TransportStatus = "session disconnected; restart Play/app"; return; }
            if (!IPAddress.TryParse(robotAddress, out IPAddress address) || address.AddressFamily != AddressFamily.InterNetwork || port < 1 || port > 65535)
            { TransportStatus = "set a valid measured IPv4 address / port"; return; }
#if UNITY_EDITOR
            if (input != null && input.editorPreview && !IPAddress.IsLoopback(address))
            { TransportStatus = "Editor simulation permits loopback only"; return; }
#endif
            try
            {
                _remote = new IPEndPoint(address, port);
                _socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
                _socket.Client.Blocking = false;
                _socket.Connect(_remote); // ACKs use this very same endpoint/socket.
                Session.Block();
                if (input != null) input.Invalidate();
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
                    if (!Session.AcceptAck(ack, Now, maxAckAgeSeconds)) continue;
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
                    if (!Session.NeutralConfirmed && input != null && input.ControlActive) input.Invalidate();
                }
            }
            catch (SocketException e)
            {
                if (e.SocketErrorCode != SocketError.WouldBlock) FailTransport(e.Message);
            }
        }

        private void SendCommand()
        {
            if (_socket == null) return;
#if UNITY_EDITOR
            if (input != null && input.editorPreview && !IPAddress.IsLoopback(_remote.Address))
            {
                Session.Block();
                TransportStatus = "Editor simulation blocked for a real robot";
            }
#endif
            Vector2 axes = input != null ? input.Axes : Vector2.zero;
            float scale = float.IsNaN(speedScale) || float.IsInfinity(speedScale) ? 0 : Mathf.Clamp01(speedScale);
            V2Command message = Session.Command(Now, InputValid, axes.x * scale, axes.y * scale);
            if (Send(JsonUtility.ToJson(message), message.sequence)) LastCommand = message;
        }

        private void SendEvent(string kind)
        {
            if (_socket == null) return;
            V2Event message = Session.Event(Now, kind);
            Send(JsonUtility.ToJson(message), message.sequence);
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
            SendCommand();
        }

        public void EmergencyStop()
        {
            Session.LatchEmergency(); // Keep the stop intent even if no socket exists yet.
            _pendingEvent = "emergency_stop";
            if (input != null) input.Invalidate();
            SendEvent(_pendingEvent);
        }

        public void ClearEmergency()
        {
            if (input != null && input.Grabbed) return;
            _pendingEvent = "clear_emergency";
            Session.Block();
            SendEvent(_pendingEvent);
        }

        public void DisconnectSession()
        {
            RequestNeutral();
            if (_socket != null && !Session.Retired) SendEvent("disconnect");
            networkEnabled = false;
            CloseTransport();
            TransportStatus = "disconnected";
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
            if (input != null) input.NeutralRequested -= RequestNeutral;
            RequestNeutral();
            CloseTransport(); // Temporary disable keeps UUID; only final exit sends disconnect.
        }
    }
}
