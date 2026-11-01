using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using UnityEngine;

namespace PuppyPi
{
    // One UDP control session, with independent ROS commands and camera streams.
    [DefaultExecutionOrder(50)]
    [DisallowMultipleComponent]
    public sealed class PuppyPiRobotController : MonoBehaviour
    {
        private const string SpeedPreference = "PuppyPi.Vr.SpeedPercent";
        public PuppyPiV2Client driveClient;
        public BallGripInput gripInput;
        public RobotRosBridge rosBridge;
        public PuppyPiCameraStream cameraStream;
        public PuppyPiCameraStream mapStream;
        public PuppyPiMediaClient mediaClient;
        public PuppyPiFloatingScreen floatingScreen;
        public PuppyPiVrPanel dashboard;
        public PuppyPiCockpitPlacement cockpitPlacement;
        public PuppyPiConnection connection;
        public bool showCameraScreenOnStart = true;
        [Range(10, 100)] public int speedPercent = 50;

        public PuppyPiV2Client DriveClient => driveClient;
        public BallGripInput GripInput => gripInput;
        public RobotRosBridge RosBridge => rosBridge;
        public PuppyPiConnection Connection => connection;
        public Texture CameraTexture => cameraStream != null ? cameraStream.Texture : null;
        public string CameraStatus => cameraStream != null ? cameraStream.Status : "Camera not configured";
        public bool CameraLive => cameraStream != null && cameraStream.IsStreaming;
        public double CameraFrameAgeSeconds => cameraStream != null ? cameraStream.FrameAgeSeconds : double.PositiveInfinity;
        public Texture MapTexture => mapStream != null ? mapStream.Texture : null;
        public bool MapLive => mapStream != null && mapStream.IsStreaming;
        public string MapStatus => mapStream != null ? mapStream.Status : "Map not configured";
        public double MapFrameAgeSeconds => mapStream != null ? mapStream.FrameAgeSeconds : double.PositiveInfinity;
        public string MediaStatusText => mediaClient != null ? mediaClient.Summary : "Media API not configured";
        public int SpeedPercent => speedPercent;
        public string LastResult { get; private set; } = "Release the grip before selecting a robot command.";
        public bool IsDiscovering => connection != null && connection.Discovery.Searching;
        public IReadOnlyList<DiscoveredPi> Devices => connection.Discovery.Devices;
        public bool CanExecuteMotion => MotionBlockReason() == null;
        public bool IsPositioningPanel => (floatingScreen != null && floatingScreen.IsGrabbed) ||
            (dashboard != null && dashboard.IsGrabbed) ||
            (cockpitPlacement != null && cockpitPlacement.IsGrabbed);
        public bool CanClearStop => !_paused && _focused && gripInput != null && !gripInput.Grabbed &&
            !IsPositioningPanel &&
            driveClient != null && driveClient.networkEnabled && driveClient.Session.Connected &&
            driveClient.Session.ServerReason != "not_owner";

        private string _streamsHost = "";
        private bool _remoteMotionBlocked;
        private bool _paused;
        private bool _focused = true;
        private bool _eventsBound;
        private double _motionCooldownUntil;
        private double _nextDiagnosticAt;
        private string _lastDiagnostic;
        private bool _ownsCameraScreen;
        private static double Now => Time.realtimeSinceStartupAsDouble;

        private void Awake()
        {
            ResolveComponents();
            speedPercent = PlayerPrefs.GetInt(SpeedPreference, speedPercent);
            ApplySpeed(speedPercent, false);
        }

        public void ResolveComponents()
        {
            if (driveClient == null) driveClient = GetComponent<PuppyPiV2Client>();
            if (gripInput == null && driveClient != null) gripInput = driveClient.input;
            if (connection == null) connection = GetComponent<PuppyPiConnection>();
            if (connection == null) connection = gameObject.AddComponent<PuppyPiConnection>();
            connection.client = driveClient;
            if (rosBridge == null) rosBridge = GetComponent<RobotRosBridge>();
            if (rosBridge == null) rosBridge = gameObject.AddComponent<RobotRosBridge>();
            if (cameraStream == null) cameraStream = GetComponent<PuppyPiCameraStream>();
            if (cameraStream == null) cameraStream = gameObject.AddComponent<PuppyPiCameraStream>();
            if (mapStream == null)
                foreach (var stream in GetComponents<PuppyPiCameraStream>())
                    if (stream != cameraStream) { mapStream = stream; break; }
            if (mapStream == null) mapStream = gameObject.AddComponent<PuppyPiCameraStream>();
            if (mediaClient == null) mediaClient = GetComponent<PuppyPiMediaClient>();
            if (mediaClient == null) mediaClient = gameObject.AddComponent<PuppyPiMediaClient>();
            if (dashboard == null)
                foreach (var panel in FindObjectsByType<PuppyPiVrPanel>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (panel.controller == this) { dashboard = panel; break; }
            if (cockpitPlacement == null) cockpitPlacement = GetComponent<PuppyPiCockpitPlacement>();
            if (cockpitPlacement == null) cockpitPlacement = gameObject.AddComponent<PuppyPiCockpitPlacement>();
            cockpitPlacement.input = gripInput;
            cockpitPlacement.cockpitRoot = gripInput != null && gripInput.baseFrame != null ? gripInput.baseFrame : transform;
        }

        private void Start()
        {
            if (showCameraScreenOnStart) ShowCameraScreen();
        }

        public void ShowCameraScreen()
        {
            if (floatingScreen == null)
            {
                floatingScreen = new GameObject("PuppyPi movable camera screen").AddComponent<PuppyPiFloatingScreen>();
                _ownsCameraScreen = true;
            }
            floatingScreen.controller = this;
            floatingScreen.ShowAndRecenter();
        }

        private void OnEnable()
        {
            ResolveComponents();
            if (!_eventsBound)
            {
                rosBridge.CommandCompleted += OnCommandCompleted;
                _eventsBound = true;
            }
        }

        private void Update()
        {
            if (driveClient == null) return;
            if (_paused || !driveClient.networkEnabled)
            {
                StopStreams();
            }
            else if (!string.IsNullOrWhiteSpace(driveClient.robotAddress) && _streamsHost != driveClient.robotAddress)
            {
                StopStreams();
                _streamsHost = driveClient.robotAddress;
                _remoteMotionBlocked = false;
                rosBridge.Connect(_streamsHost);
                cameraStream.Connect(_streamsHost);
                mapStream.Connect(_streamsHost, "/lidar_map/image");
                mediaClient.Connect(_streamsHost);
            }

            if (rosBridge.IsConnected && rosBridge.RobotStatusFresh && rosBridge.ArbiterStateFresh)
            {
                var state = rosBridge.ArbiterState;
                _remoteMotionBlocked = state.estop || !state.vr_permitted;
            }
            // Unblocking never arms a held grip; the existing V2 session requires a new grab.
            driveClient.SetExternalMotionBlocked(_paused || !_focused || IsPositioningPanel || _remoteMotionBlocked ||
                rosBridge.HasPendingCommand || Now < _motionCooldownUntil);
            LogChannels();
        }

        public void SetSpeedPercent(int percent) => ApplySpeed(percent, true);

        private void ApplySpeed(int percent, bool save)
        {
            speedPercent = Mathf.Clamp(Mathf.RoundToInt(percent / 10f) * 10, 10, 100);
            if (driveClient != null) driveClient.speedScale = speedPercent / 100f;
            if (!save) return;
            PlayerPrefs.SetInt(SpeedPreference, speedPercent);
            PlayerPrefs.Save();
            LastResult = $"Maximum input: {speedPercent}% of the robot's configured limits.";
        }

        private string MotionBlockReason()
        {
            if (_paused || !_focused) return "Return to the VR app before sending a command.";
            if (gripInput == null || driveClient == null) return "Grip references are missing.";
            if (gripInput.Grabbed) return "Release the grip before choosing a pose or action.";
            if (IsPositioningPanel) return "Release the console, dashboard and camera screen before choosing a pose or action.";
            if (rosBridge == null || !rosBridge.IsConnected) return "Robot command channel is disconnected.";
            if (!rosBridge.RobotStatusFresh || !rosBridge.ArbiterStateFresh) return "Waiting for fresh robot status.";
            if (!driveClient.networkEnabled || !driveClient.Session.Connected) return "Connect to the robot first.";
            if (!driveClient.Session.LastAccepted) return "The robot has not granted this control session.";
            if (driveClient.Session.Emergency || driveClient.Session.Faulted || rosBridge.ArbiterState.estop)
                return "Clear the stop or robot fault before requesting motion.";
            if (!driveClient.Session.NeutralConfirmed) return "Release the grip and wait for neutral acknowledgement.";
            if (rosBridge.HasPendingCommand || Now < _motionCooldownUntil) return "A robot command is already pending.";
            string owner = rosBridge.ArbiterState.owner;
            if (!string.IsNullOrEmpty(owner) && owner != "vr") return "Wait for the current robot operation to finish.";
            if (rosBridge.RobotStatus.engine != "ready") return "The robot engine is not ready.";
            return null;
        }

        private void RequestMotion(Func<string> send)
        {
            string reason = MotionBlockReason();
            if (reason != null) { LastResult = reason; return; }
            HoldNeutral();
            _motionCooldownUntil = Now + 1;
            driveClient.SetExternalMotionBlocked(true);
            string id = send();
            LastResult = id == null ? "Command was not sent. Check the robot channel." : "Request sent; waiting for the robot.";
        }

        public void GoHome() => RequestMotion(() => rosBridge.Stand());

        public void ExecutePose(string name)
        {
            if (Array.IndexOf(RobotCommandCatalog.PoseNames, name) < 0)
            { LastResult = "Select a known pose."; return; }
            RequestMotion(() => rosBridge.SetPose(name));
        }

        public void ExecuteAction(string name)
        {
            if (Array.IndexOf(RobotCommandCatalog.ActionNames, name) < 0)
            { LastResult = "Select a known action group."; return; }
            RequestMotion(() => rosBridge.RunAction(name));
        }

        public void UdpStop()
        {
            if (driveClient == null) return;
            driveClient.EmergencyStop();
            LastResult = "VR emergency stop requested.";
        }

        public void ClearUdpStop()
        {
            if (!CanClearStop) { LastResult = "Release the grip and obtain control before clearing the VR stop."; return; }
            driveClient.ClearEmergency();
            LastResult = "VR stop release requested. Wait for neutral ACK, then grab again.";
        }

        public void RobotStop()
        {
            UdpStop();
            if (rosBridge == null || !rosBridge.IsConnected)
            { LastResult = "VR stop requested; whole-robot channel is disconnected."; return; }
            rosBridge.StopRobot();
            LastResult = "Whole-robot emergency stop requested.";
        }

        public void ResetRobot()
        {
            if (!CanClearStop)
            { LastResult = "Release the grip and obtain control before resetting the robot stop."; return; }
            if (rosBridge == null || !rosBridge.IsConnected)
            { LastResult = "Robot command channel is disconnected."; return; }
            HoldNeutral();
            rosBridge.ResetRobotStop();
            LastResult = "Whole-robot reset requested. Clear the VR stop separately if needed.";
        }

        public void Discover()
        {
            connection.Search();
            LastResult = "Searching this LAN for PuppyPi devices...";
        }

        public void ConnectDevice(DiscoveredPi device)
        {
            HoldNeutral();
            StopStreams();
            connection.ConnectDevice(device);
            LastResult = "Connecting to the selected robot. Keep the grip released.";
        }

        public void ConnectManual(string host, int port)
        {
            host = host?.Trim();
            UriHostNameType kind = Uri.CheckHostName(host ?? "");
            if (string.IsNullOrEmpty(host) || host.Length > 253 ||
                (kind != UriHostNameType.IPv4 && kind != UriHostNameType.Dns) || port < 1 || port > 65535)
            { LastResult = "Enter a valid robot IPv4 address or host and a port from 1 to 65535."; return; }
            if (IPAddress.TryParse(host, out IPAddress address))
            {
                if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast) || address.GetAddressBytes()[0] >= 224)
                { LastResult = "Enter the robot's unicast IPv4 address."; return; }
#if UNITY_EDITOR
                if (gripInput != null && gripInput.editorPreview && !IPAddress.IsLoopback(address))
                { LastResult = "Disable simulated grip input before connecting to a real robot."; return; }
#endif
            }
            HoldNeutral();
            StopStreams();
            connection.AddressText = host ?? "";
            connection.PortText = port.ToString(CultureInfo.InvariantCulture);
            connection.ConnectManual();
            LastResult = "Connecting. Keep the grip released until neutral is confirmed.";
        }

        public void Reconnect()
        {
            string address = driveClient != null ? driveClient.robotAddress : connection.AddressText;
            int port = driveClient != null ? driveClient.port : 5001;
            ConnectManual(address, port);
        }

        public void Disconnect()
        {
            HoldNeutral();
            connection.Disconnect();
            StopStreams();
            LastResult = "Disconnected.";
        }

        private void HoldNeutral()
        {
            if (gripInput != null) gripInput.Invalidate();
            if (driveClient != null) driveClient.RequestNeutral();
        }

        private void StopStreams()
        {
            if (_streamsHost.Length == 0) return;
            _streamsHost = "";
            rosBridge.Disconnect();
            cameraStream.Disconnect();
            mapStream.Disconnect();
            mediaClient.Disconnect();
        }

        private void OnCommandCompleted(RobotRosCommandResult result)
        {
            LastResult = result.Message;
            if (Debug.isDebugBuild)
                Debug.Log($"PUPPYPI_ROBOT_COMMAND operation={result.Operation} success={result.Success} " +
                    $"acknowledged={result.Acknowledged} timedOut={result.TimedOut}: {result.Message}", this);
        }

        private void LogChannels()
        {
            if (!Debug.isDebugBuild || Now < _nextDiagnosticAt) return;
            _nextDiagnosticAt = Now + 0.5;
            string diagnostic = $"ros={rosBridge.ConnectionState} fresh={rosBridge.RobotStatusFresh && rosBridge.ArbiterStateFresh} " +
                $"engine={rosBridge.RobotStatus?.engine ?? "unknown"} owner={rosBridge.ArbiterState?.owner ?? "none"} " +
                $"robotStop={rosBridge.ArbiterState?.estop} vrPermitted={rosBridge.ArbiterState?.vr_permitted} " +
                $"cameraLive={CameraLive} camera={CameraStatus} cameraError={cameraStream?.LastError} " +
                $"mapLive={MapLive} map={MapStatus} mapError={mapStream?.LastError}";
            if (diagnostic == _lastDiagnostic) return;
            _lastDiagnostic = diagnostic;
            Debug.Log("PUPPYPI_CHANNELS " + diagnostic, this);
        }

        public string StatusText
        {
            get
            {
                if (driveClient == null || rosBridge == null) return "Robot controls are initializing...";
                V2Session session = driveClient.Session;
                string control = !driveClient.networkEnabled ? "DISCONNECTED" : !session.Connected ? "WAITING FOR ACK" :
                    session.ServerReason == "not_owner" ? "SESSION REJECTED: NOT OWNER" : session.Emergency ? "VR STOP" :
                    session.Faulted ? "ROBOT FAULT" : !session.LastAccepted ? "CONTROL REJECTED" :
                    session.Armed ? "CONTROLLING" : "READY - RELEASE AND GRAB AGAIN";
                var robot = rosBridge.RobotStatus;
                var arbiter = rosBridge.ArbiterState;
                string camera = cameraStream != null ? cameraStream.Status : "not configured";
                return $"{driveClient.robotAddress}:{driveClient.port}  |  {control}\n" +
                    $"ROS: {rosBridge.ConnectionState} ({(rosBridge.RobotStatusFresh && rosBridge.ArbiterStateFresh ? "LIVE" : "STALE")})  |  Engine: {robot?.engine ?? "unknown"}\n" +
                    $"Owner: {arbiter?.owner ?? "none"}  |  Robot stop: {(arbiter == null ? "unknown" : arbiter.estop.ToString())}  |  VR permitted: {(arbiter == null ? "unknown" : arbiter.vr_permitted.ToString())}\n" +
                    $"Status: {(rosBridge.RobotStatusFresh && rosBridge.ArbiterStateFresh ? "LIVE" : "STALE / WAITING")}  |  Camera: {camera}\n" +
                    $"Grab: {(gripInput != null && gripInput.Grabbed)}  |  Tracking: {(gripInput != null && gripInput.TrackingValid)}  |  Speed: {speedPercent}%\n" +
                    $"Server: {session.ServerState} / {session.ServerReason}  accepted={session.LastAccepted}\n" +
                    $"{LastResult}";
            }
        }

        private void OnApplicationPause(bool paused)
        {
            _paused = paused;
            if (!paused) return;
            HoldNeutral();
            StopStreams();
        }

        private void OnApplicationFocus(bool focused)
        {
            _focused = focused;
            if (!focused) HoldNeutral();
        }

        private void OnDisable()
        {
            if (_eventsBound && rosBridge != null) rosBridge.CommandCompleted -= OnCommandCompleted;
            _eventsBound = false;
            if (driveClient != null) driveClient.SetExternalMotionBlocked(true);
            StopStreams();
        }

        private void OnDestroy()
        {
            if (!_ownsCameraScreen || floatingScreen == null) return;
            if (Application.isPlaying) Destroy(floatingScreen.gameObject);
            else DestroyImmediate(floatingScreen.gameObject);
        }
    }
}
