using System;
using System.Collections.Generic;
using Oculus.Interaction;
using Oculus.Interaction.Grab;
using Oculus.Interaction.HandGrab;
using Oculus.Interaction.Surfaces;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PuppyPi
{
    // Independent map instrument. Drawing only prepares a path; releasing a
    // finger never sends a robot command. RUN is a separate explicit action.
    public sealed class PuppyPiMapWindow : MonoBehaviour
    {
        public PuppyPiRobotController controller;
        public Transform head;
        [Range(0.1f, 1f)] public float minimumScale = 0.35f;
        [Range(1f, 4f)] public float maximumScale = 2;
        [Tooltip("Scale used to distinguish hand jitter from deliberate changes of direction.")]
        [Range(0.02f, 0.15f)] public float correctionToleranceMeters = 0.08f;
        public Transform WindowRoot => _root != null ? _root.transform : null;
        public RawImage MapView { get; private set; }
        public PuppyPiMapDrawingSurface DrawingSurface { get; private set; }
        public Text StatusLabel { get; private set; }
        public Text NoMapLabel { get; private set; }
        public bool IsVisible => _root != null && _root.activeInHierarchy;
        public bool IsGrabbed => IsVisible && _grab != null && _grab.SelectingPointsCount > 0;
        public bool IsDrawing => _pointer.HasValue;
        public bool DrawMode => _drawMode;
        public bool PlacementLocked => _externalLock || IsDrawing;
        public IReadOnlyList<Vector2> RawPoints => _raw.AsReadOnly();
        public IReadOnlyList<Vector2> PreparedWaypoints => Array.AsReadOnly(_prepared);
        public bool NavigationVerified => _navigationVerified;
        private bool OperatorSupervised => controller != null && controller.PathClient != null &&
            controller.PathClient.operatorSupervisedNavigation;
        public bool BypassPreviewReady => _navigationVerified && _runRoute != null && !ReferenceEquals(_runRoute, _prepared) &&
            controller != null && controller.MapData != null &&
            (OperatorSupervised ? controller.MapData.HasNavigationPose : controller.MapData.NavigationLive) &&
            _mapRevision == controller.MapData.Revision && ReferenceEquals(_navigationOccupancy, controller.MapData.Occupancy);
        public MapPathBlock PreviewBlock => _previewBlock;
        public string ValidationStatus => _validationStatus;
        public string RunUnavailableReason => GetRunBlockReason();

        private const float Width = 960, Height = 760, MetersPerPixel = 0.001f;
        private const int MaximumRawPoints = 4096;
        private static readonly Color Accent = new Color(0.15f, 0.85f, 0.72f);
        private readonly List<Vector2> _raw = new List<Vector2>();
        private readonly Stack<Vector2[]> _undo = new Stack<Vector2[]>();
        private Vector2[] _prepared = Array.Empty<Vector2>(), _runRoute = Array.Empty<Vector2>(), _beforeStroke;
        private GameObject _root;
        private Grabbable _grab;
        private HandGrabInteractable _handGrab;
        private GrabInteractable _controllerGrab;
        private PointableCanvas _pointable;
        private PuppyPiMapPathGraphic _pathGraphic;
        private AspectRatioFitter _aspect;
        private Button _drawButton, _runButton, _undoButton, _clearButton;
        private Font _font;
        private string _lastRunDiagnostic;
        private double _nextRunDiagnostic;
        private MapGridGeometry _geometry, _strokeGeometry;
        private int? _pointer;
        private string _session, _endpoint, _mapStatus = "Waiting for map", _notice = "Draw a path starting at the red robot marker.";
        private string _identitySession, _identityEndpoint, _identityFrame;
        private bool _drawMode, _externalLock, _editingAvailable, _hasPose, _hadIdentity, _wasGrabbed;
        private Vector3 _robotPose;
        private readonly List<Vector2> _validationPath = new List<Vector2>(21);
        private readonly Vector2[] _robotDisk = new Vector2[2];
        private readonly MapObstacleReplanGate _previewObstacleGate = new MapObstacleReplanGate();
        private int[] _navigationOccupancy;
        private bool _navigationLive, _navigationVerified, _blockAtRobot, _pathWasActive;
        private float _clearanceRadius;
        private MapRobotFootprint _footprint;
        private float _footprintLength, _footprintWidth, _footprintMargin;
        private MapPathBlock _previewBlock;
        private string _navigationStatus = "Waiting for current obstacle observations.", _validationStatus = "";
        private string _filterStatus = "";
        private bool _validationDirty = true;
        private long _mapRevision = -1;
        private long _detourRevision = long.MinValue;
        private MapGridGeometry _detourGeometry;
        private int[] _detourOccupancy;
        private Vector2[] _detourRequest, _detourRoute;
        private Vector3 _detourPose;
        private float _detourLength, _detourWidth, _detourMargin;
        private bool _detourSucceeded;
        private bool _detourDeferred;
        private string _detourFailureReason;
        private double _nextDetourPlanAt;
        private int _activeRouteHash;

        public void ShowAndRecenter()
        {
            if (PlacementLocked) return;
            EnsureWindow();
            if (!OperatorSupervised) CancelPath("map_window_recenter");
            ResetNavigationPreview();
            _root.SetActive(false);
            Transform viewer = head != null ? head : Camera.main != null ? Camera.main.transform : transform;
            Vector3 forward = Vector3.ProjectOnPlane(viewer.forward, Vector3.up);
            if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
            forward.Normalize();
            Vector3 position = viewer.position + forward * 0.75f - Vector3.up * 0.06f;
            _root.transform.SetPositionAndRotation(position, Quaternion.LookRotation(position - viewer.position, Vector3.up));
            _root.SetActive(isActiveAndEnabled);
            ApplyPlacementLock();
            Physics.SyncTransforms();
            RefreshFromController();
            RefreshUi();
        }

        public void SetPlacementLocked(bool locked)
        {
            _externalLock = locked;
            if (locked)
            {
                CancelStroke("Drawing cancelled by the current operating mode.");
                _drawMode = false;
            }
            ApplyPlacementLock();
            RefreshUi();
        }

        public void SetPresentationVisible(bool visible)
        {
            if (!visible)
            {
                CancelStroke("Map window closed.");
                _drawMode = false;
                if (!OperatorSupervised) CancelPath("map_window_hidden");
                ResetNavigationPreview();
            }
            if (_root != null) _root.SetActive(visible && isActiveAndEnabled);
            if (visible) { RefreshFromController(); RefreshUi(); }
        }

        // Filter confirmation is evidence about individual cells. It is not a
        // global readiness handshake and must not disable an already verified
        // free route while unrelated candidate points are being checked.
        public void SetObstacleFilterStatus(string status)
        {
            _filterStatus = string.IsNullOrWhiteSpace(status) ? "" : status;
            RefreshUi();
        }

        // Display-only ingress for stored map previews and isolated UI tests.
        // The cached coordinate map stays editable across sensor gaps; age and
        // pose freshness only gate navigation through SetNavigationFrame.
        // The data client owns texture/occupancy; this view never mutates them.
        public void SetMapFrame(Texture2D texture, MapGridGeometry geometry, Vector3 robotPose,
            bool hasPose, bool editingAvailable, string sessionId, long revision, float ageSeconds,
            string status, string endpointIdentity = "")
        {
            if (_root == null) return;
            bool wasReady = EditingReady();
            string frame = geometry != null ? geometry.frame : null;
            bool geometryChanged = !SameGeometry(_geometry, geometry);
            bool validIdentity = geometry != null && geometry.IsValid && !string.IsNullOrEmpty(sessionId);
            bool identityChanged = OperatorSupervised ? _hadIdentity && validIdentity &&
                (_identitySession != sessionId || _identityEndpoint != endpointIdentity || _identityFrame != frame) :
                _hadIdentity && (_session != sessionId || _endpoint != endpointIdentity ||
                    (_geometry != null ? _geometry.frame : null) != frame);
            if (identityChanged)
            {
                CancelPath("map_identity_changed");
                ClearDraft("Map session or endpoint changed. Draw a new path.");
            }
            else if (IsDrawing && !SameGeometry(_strokeGeometry, geometry))
                CancelStroke("Map geometry changed during drawing. Draw again.");
            if (IsDrawing && (texture == null || geometry == null || !geometry.IsValid || string.IsNullOrEmpty(sessionId)))
                CancelStroke("Map unavailable. Draw again after it loads.");
            if (identityChanged || geometryChanged)
            {
                ResetNavigationPreview();
            }
            // An absent frame during suspend/reconnect is not a new origin.
            // Retain only the drawing's identity, never stale obstacle data.
            if (!OperatorSupervised || validIdentity) _hadIdentity = validIdentity;
            if (validIdentity)
            {
                _identitySession = sessionId;
                _identityEndpoint = endpointIdentity;
                _identityFrame = frame;
            }
            _session = sessionId;
            _endpoint = endpointIdentity;
            _geometry = CopyGeometry(geometry);
            bool validPose = hasPose && Finite(robotPose.x) && Finite(robotPose.y) && Finite(robotPose.z);
            _validationDirty |= geometryChanged || identityChanged || _robotPose != robotPose ||
                _hasPose != validPose || _mapRevision != revision;
            _mapRevision = revision;
            _robotPose = robotPose;
            _hasPose = validPose;
            _editingAvailable = editingAvailable && texture != null && _geometry != null && _geometry.IsValid;
            _mapStatus = string.IsNullOrEmpty(status) ? "Map unavailable" : status;
            MapView.texture = texture;
            _validationDirty |= wasReady != EditingReady();
            if (_geometry != null && _geometry.IsValid)
                _aspect.aspectRatio = (float)_geometry.width / _geometry.height;
            RefreshUi();
        }

        public void ToggleDrawMode()
        {
            if (PlacementLocked || IsGrabbed) return;
            if (_drawMode) { _drawMode = false; RefreshUi(); return; }
            if (!EditingReady()) { _notice = "Wait for a map to load before drawing."; RefreshUi(); return; }
            if (controller != null && controller.PathClient != null && controller.PathClient.IsActive)
            {
                CancelPath("map_editing");
                _notice = "Stopping the current route. Select DRAW after the stop is confirmed.";
                RefreshUi();
                return;
            }
            CancelPath("map_editing");
            _drawMode = true;
            _notice = "Touch and drag, or pinch the pointer, from the red robot marker. Release to preview.";
            RefreshUi();
        }

        // Uses only the separately validated current obstacle layer. An image
        // or the old accumulated grid alone never turns the preview green.
        public void SetNavigationFrame(int[] occupancy, bool live, string status, float clearanceRadiusMeters,
            MapRobotFootprint footprint = null)
        {
            string detail = string.IsNullOrWhiteSpace(status) ? "Waiting for current obstacle observations." : status;
            _validationDirty |= !ReferenceEquals(_navigationOccupancy, occupancy) || _navigationLive != live ||
                _navigationStatus != detail || _clearanceRadius != clearanceRadiusMeters ||
                (_footprint == null) != (footprint == null) ||
                (footprint != null && (_footprintLength != footprint.LengthMeters ||
                    _footprintWidth != footprint.WidthMeters || _footprintMargin != footprint.MarginMeters));
            _navigationOccupancy = occupancy;
            _navigationLive = live;
            _navigationStatus = detail;
            _clearanceRadius = clearanceRadiusMeters;
            _footprint = footprint;
            _footprintLength = footprint != null ? footprint.LengthMeters : 0;
            _footprintWidth = footprint != null ? footprint.WidthMeters : 0;
            _footprintMargin = footprint != null ? footprint.MarginMeters : 0;
            RefreshUi();
        }

        public bool BeginStroke(int pointerId, Vector2 uv)
        {
            if (!IsVisible || !_drawMode || IsDrawing || IsGrabbed || _externalLock || !EditingReady() ||
                (controller != null && controller.PathClient != null && controller.PathClient.IsActive) ||
                !_geometry.TryUvToMeters(uv, out Vector2 meters)) return false;
            _strokeGeometry = CopyGeometry(_geometry);
            _beforeStroke = _raw.ToArray();
            _raw.Clear();
            _raw.Add(meters);
            _prepared = Array.Empty<Vector2>();
            _pointer = pointerId;
            _validationDirty = true;
            _notice = "Drawing. Release to prepare the route; RUN sends it separately.";
            ApplyPlacementLock();
            RefreshUi();
            return true;
        }

        public bool AppendStroke(int pointerId, Vector2 uv)
        {
            if (_pointer != pointerId || _strokeGeometry == null ||
                !_strokeGeometry.TryUvToMeters(uv, out Vector2 meters)) return false;
            if (_raw.Count >= MaximumRawPoints)
            { CancelStroke("Drawing limit reached. Use a shorter stroke."); return false; }
            // Bound jitter without tying sampling density to the window scale.
            if ((_raw[_raw.Count - 1] - meters).sqrMagnitude < 0.000025f) return true;
            _raw.Add(meters);
            RefreshPreview();
            return true;
        }

        public bool EndStroke(int pointerId)
        {
            if (_pointer != pointerId) return false;
            _pointer = null;
            DrawingSurface.ReleaseCapture();
            _drawMode = false;
            if (_beforeStroke != null)
            {
                if (_undo.Count >= 16) _undo.Clear();
                _undo.Push(_beforeStroke);
            }
            _beforeStroke = null;
            _strokeGeometry = null;
            ApplyPlacementLock();
            bool prepared = PrepareDraft();
            RefreshUi();
            return prepared;
        }

        public void CancelStroke(string reason)
        {
            if (!IsDrawing) return;
            _pointer = null;
            DrawingSurface?.ReleaseCapture();
            _raw.Clear();
            if (_beforeStroke != null) _raw.AddRange(_beforeStroke);
            _beforeStroke = null;
            _strokeGeometry = null;
            _drawMode = false;
            PrepareDraft();
            _notice = reason;
            ApplyPlacementLock();
            RefreshUi();
        }

        public void ClearDraft(string reason = "Route cleared.")
        {
            CancelPath("map_clear");
            CancelStroke(reason);
            _raw.Clear();
            _prepared = Array.Empty<Vector2>();
            _validationDirty = true;
            _previewObstacleGate.Reset();
            ResetDetourPreview();
            _undo.Clear();
            _drawMode = false;
            _notice = reason;
            RefreshUi();
        }

        public void Undo()
        {
            if (IsDrawing || IsGrabbed || _externalLock || _undo.Count == 0) return;
            CancelPath("map_undo");
            _raw.Clear();
            _raw.AddRange(_undo.Pop());
            _drawMode = false;
            PrepareDraft();
            RefreshUi();
        }

        public void Run()
        {
            // A button event can precede this window's LateUpdate. Validate
            // against the current data revision before using a green preview.
            RefreshFromController();
            RevalidatePreview();
            string blocked = GetRunBlockReason();
            if (blocked != null)
            {
                _notice = "RUN NOT SENT | " + blocked;
                RefreshUi();
                return;
            }
            if (controller == null || controller.PathClient == null)
                _notice = "Path command channel is not configured.";
            else if (controller.PathClient.Execute(_prepared, _geometry.frame, _session, out string error))
                _notice = "Route requested. Waiting for the path controller.";
            else _notice = string.IsNullOrEmpty(error) ? "Route was not sent." : error;
            RefreshUi();
        }

        public void Stop()
        {
            CancelStroke("Route stopped.");
            _drawMode = false;
            CancelPath("map_stop");
            _notice = "Route stop requested.";
            RefreshUi();
        }

        private bool PrepareDraft()
        {
            _validationDirty = true;
            _previewObstacleGate.Reset();
            ResetDetourPreview();
            if (!MapPathGeometry.TryPrepare(_raw, out _prepared, out string error, toleranceMeters: correctionToleranceMeters))
            {
                _prepared = Array.Empty<Vector2>();
                _notice = error ?? "Draw a route with at least two points.";
                if (_raw.Count >= 2) Debug.Log($"PUPPYPI_PATH_PREPARE accepted=false raw={_raw.Count} tolerance={correctionToleranceMeters:F3} reason={_notice}", this);
                return false;
            }
            Debug.Log($"PUPPYPI_PATH_PREPARE accepted=true raw={_raw.Count} prepared={_prepared.Length} tolerance={correctionToleranceMeters:F3}", this);
            _notice = "STRAIGHTENED ROUTE | Straight sections cleaned up; clear corners retained. Review, then RUN.";
            return true;
        }

        private bool EditingReady() => _editingAvailable && _hasPose && _geometry != null && _geometry.IsValid &&
            MapView != null && MapView.texture != null && !string.IsNullOrEmpty(_session);
        private string GetRunBlockReason()
        {
            if (!EditingReady()) return "Wait for a map and robot marker before running a route.";
            if (IsDrawing || _drawMode) return "Finish drawing and release the pointer before RUN.";
            if (!OperatorSupervised && (IsGrabbed || _externalLock)) return "Release the map and movable screens before RUN.";
            if (controller != null && controller.PathClient != null && controller.PathClient.IsActive)
                return "Stop the current route before running another.";
            if (_prepared.Length < 2) return "Draw a route from the red robot marker.";
            if (controller != null && controller.PathClient != null)
            {
                string control = controller.PathClient.ControlBlockReason;
                if (control != null) return control;
            }
            if (!OperatorSupervised && !_navigationVerified) return string.IsNullOrEmpty(_validationStatus) ?
                "Wait for the latest scan to verify the route." : _validationStatus;
            if (controller == null || controller.PathClient == null) return "Path command channel is not configured.";
            return controller.PathClient.ControlBlockReason;
        }

        private void ResetNavigationPreview()
        {
            // Reopening retains the user's metre-space drawing, but never
            // revives a green preview, blocked cell or detour from a closed view.
            _navigationLive = _navigationVerified = false;
            _navigationOccupancy = null;
            _previewBlock = null;
            _blockAtRobot = false;
            _validationDirty = true;
            _navigationStatus = "Waiting for the latest scan.";
            _filterStatus = "";
            _previewObstacleGate.Reset();
            ResetDetourPreview();
        }

        private void ResetDetourPreview()
        {
            _detourRevision = long.MinValue;
            _detourGeometry = null;
            _detourOccupancy = null;
            _detourRequest = _detourRoute = null;
            _detourSucceeded = _detourDeferred = false;
            _detourFailureReason = null;
            _nextDetourPlanAt = 0;
        }
        private void CancelPath(string reason) { if (controller != null && controller.PathClient != null) controller.PathClient.Cancel(reason); }

        private void RefreshFromController()
        {
            if (controller == null || controller.MapData == null) return;
            var data = controller.MapData;
            string endpoint = controller.DriveClient != null ? controller.DriveClient.robotAddress + ":" + controller.DriveClient.port : "";
            SetMapFrame(data.Texture, data.Geometry, data.RobotPose, data.PoseAvailable, data.EditingAvailable,
                data.SessionId, data.Revision, data.AgeSeconds, data.Status, endpoint);
            _filterStatus = data.FilterStatus ?? "";
            bool sessionFresh = controller.MediaClient != null && controller.MediaClient.MapInfoLive;
            SetNavigationFrame(data.Occupancy, OperatorSupervised ? data.EditingAvailable && data.HasNavigationPose :
                data.NavigationLive && sessionFresh,
                OperatorSupervised ? data.NavigationLive ? "SUPERVISED | Current obstacles available." :
                    "SUPERVISED | Obstacle observations pending; operator monitors the route." :
                    sessionFresh ? data.NavigationStatus : "Waiting for map session update.",
                controller.PathClient != null ? controller.PathClient.clearanceRadiusMeters : 0,
                controller.PathClient != null ? controller.PathClient.Footprint : null);
        }

        private void LateUpdate()
        {
            if (!IsVisible) return;
            RefreshFromController();
            bool grabbed = IsGrabbed;
            if (grabbed && !_wasGrabbed)
            {
                CancelStroke("Drawing cancelled while moving the window.");
                _drawMode = false;
                if (!OperatorSupervised) CancelPath("map_window_placement");
            }
            _wasGrabbed = grabbed;
            RefreshUi();
        }

        private void RefreshUi()
        {
            if (_root == null) return;
            RevalidatePreview();
            bool ready = EditingReady();
            MapView.color = ready ? Color.white : new Color(0.4f, 0.4f, 0.4f);
            MapView.raycastTarget = _drawMode && ready && !_externalLock && !IsGrabbed;
            NoMapLabel.gameObject.SetActive(!ready);
            NoMapLabel.text = "NO MAP";
            _drawButton.interactable = ready && !_externalLock && !IsGrabbed && !IsDrawing;
            _drawButton.GetComponent<Image>().color = _drawMode ? Accent : new Color(0.08f, 0.25f, 0.32f);
            string runBlocked = GetRunBlockReason();
            _runButton.interactable = runBlocked == null;
            LogRunGate(runBlocked);
            _undoButton.interactable = !IsDrawing && !IsGrabbed && !_externalLock && _undo.Count > 0;
            _clearButton.interactable = !IsDrawing && !IsGrabbed && !_externalLock;
            string path = controller != null && controller.PathClient != null ? Trim(controller.PathClient.Status, 110) : "";
            bool pathActive = controller != null && controller.PathClient != null && controller.PathClient.IsActive;
            if (_prepared.Length >= 2 && !pathActive && runBlocked != null)
                path = "RUN BLOCKED | " + Trim(runBlocked, 115);
            string liveStatus = string.IsNullOrEmpty(_filterStatus) ? _mapStatus : _filterStatus;
            string mapStatus = !ready ? "NO MAP | " + Trim(_mapStatus, 55) : _navigationLive ?
                "MAP READY | " + Trim(liveStatus, 80) : OperatorSupervised ?
                "MAP READY | Obstacle observations are updating." : _navigationStatus.StartsWith("Waiting for map session", StringComparison.Ordinal) ?
                "Waiting for map session | Editing remains available." :
                "Updating scan | Editing remains available; RUN waits for fresh observations.";
            StatusLabel.text = mapStatus + "\n" +
                Trim(_validationStatus, 150) + "\n" + path + "\n" + Trim(_notice, 110);
            RefreshPreview();
        }

        private void LogRunGate(string blocked)
        {
            if (!Debug.isDebugBuild || _prepared.Length < 2 || Time.realtimeSinceStartupAsDouble < _nextRunDiagnostic) return;
            string signature = (blocked ?? "READY") + "/" + _navigationLive + "/" + _navigationVerified;
            if (signature == _lastRunDiagnostic) return;
            _lastRunDiagnostic = signature;
            _nextRunDiagnostic = Time.realtimeSinceStartupAsDouble + .5;
            var map = controller != null ? controller.MapData : null;
            int baseValue = -999;
            if (map?.Geometry != null && map.BaseOccupancy != null && _previewBlock != null)
            {
                int x = Mathf.FloorToInt((_previewBlock.Position.x - map.Geometry.origin.x) / map.Geometry.resolution);
                int y = Mathf.FloorToInt((_previewBlock.Position.y - map.Geometry.origin.y) / map.Geometry.resolution);
                if (x >= 0 && y >= 0 && x < map.Geometry.width && y < map.Geometry.height)
                    baseValue = map.BaseOccupancy[y * map.Geometry.width + x];
            }
            Debug.Log($"PUPPYPI_RUN_GATE enabled={blocked == null} reason={blocked} navigation={_navigationLive}/{_navigationVerified} " +
                $"block={_previewBlock?.Kind} position={_previewBlock?.Position} base={baseValue} robot={map?.RobotPose} " +
                $"{controller?.PathClient?.DiagnosticSummary}", this);
        }

        private void RevalidatePreview()
        {
            bool pathActive = controller != null && controller.PathClient != null && controller.PathClient.IsActive;
            IReadOnlyList<Vector2> activePoints = pathActive ? controller.PathClient.RemainingPoints : null;
            int activeHash = RouteHash(activePoints);
            _validationDirty |= pathActive != _pathWasActive || activeHash != _activeRouteHash ||
                (_detourDeferred && Time.realtimeSinceStartupAsDouble >= _nextDetourPlanAt);
            _pathWasActive = pathActive;
            _activeRouteHash = activeHash;
            if (!_validationDirty) return;
            _validationDirty = false;
            _navigationVerified = false;
            _previewBlock = null;
            _blockAtRobot = false;
            _detourDeferred = false;
            _runRoute = _prepared;
            if (IsDrawing) { _validationStatus = "DRAWING | Navigation has not been checked."; return; }
            if (_prepared.Length < 2) { _validationStatus = "Draw a route from the red robot marker."; return; }
            if (!EditingReady() || !_navigationLive)
            { _validationStatus = "NOT CHECKED | " + _navigationStatus; return; }
            Vector2 robot = new Vector2(_robotPose.x, _robotPose.y);
            float startDistance = Vector2.Distance(robot, _prepared[0]);
            if (!OperatorSupervised && !pathActive && startDistance > .3f)
            {
                _validationStatus = $"START TOO FAR | First point is {startDistance:F2} m from the robot (maximum 0.30 m). Draw from its current marker.";
                return;
            }
            _robotDisk[0] = _robotDisk[1] = robot;
            if (TryFindPreviewBlock(_robotDisk, out _previewBlock))
            {
                _previewObstacleGate.Reset();
                _blockAtRobot = true;
                _validationStatus = IsUnconfirmedCandidate(_previewBlock) ? DescribeCandidate(_previewBlock) :
                    DescribeBlock("Robot surroundings", _previewBlock);
                return;
            }
            _validationPath.Clear();
            _validationPath.AddRange(MapPathGeometry.NormalizeExecutionPrefix(
                pathActive ? activePoints : _prepared, robot, out _));
            if (TryFindPreviewBlock(_validationPath, out _previewBlock))
            {
                if (IsUnconfirmedCandidate(_previewBlock))
                {
                    _previewObstacleGate.Reset();
                    _validationStatus = DescribeCandidate(_previewBlock);
                    return;
                }
                if (_previewBlock.Kind != MapPathBlockKind.Occupied) _previewObstacleGate.Reset();
                // A scan shadow is not evidence of an obstacle to route around.
                // Keep the draft until this space has actually been observed.
                if (_previewBlock.Kind == MapPathBlockKind.Occupied && !pathActive &&
                    _footprint != null && controller != null && controller.PathClient != null &&
                    controller.PathClient.automaticObstacleDetour)
                {
                    // The cell filter and this route-specific gate have separate
                    // jobs: a short confirmed burst must not redraw the route.
                    // Free corridors never enter this waiting branch.
                    var data = controller.MapData;
                    if (!OperatorSupervised && data != null && _previewObstacleGate.Observe(data.NavigationObservationStamp,
                        data.NavigationObservationAt, Time.realtimeSinceStartupAsDouble, _previewBlock, false) != MapObstacleDecision.Replan)
                    {
                        _validationStatus = $"VERIFYING OBSTACLE | Checking the blocked route at ({_previewBlock.Position.x:F2}, {_previewBlock.Position.y:F2}) m before planning a bypass.";
                        return;
                    }
                    if (TryPreviewDetour(out Vector2[] detour))
                    {
                        _runRoute = detour;
                        _previewBlock = null;
                        _navigationVerified = true;
                        _validationStatus = "DETOUR | Forward straight sections avoid the blocked route; turns checked. Review before RUN.";
                        return;
                    }
                }
                if (_previewBlock.Kind == MapPathBlockKind.Occupied && _detourDeferred)
                    _validationStatus = "CHECKING ROUTE | Waiting to recheck a forward bypass against the latest scan.";
                else if (_previewBlock.Kind == MapPathBlockKind.Occupied && !pathActive && _detourFailureReason != null)
                    _validationStatus = "NO ROUTE | " + _detourFailureReason;
                else _validationStatus = DescribeBlock(_previewBlock.SegmentIndex == 0 ? "Robot to first point" :
                    "Route segment " + _previewBlock.SegmentIndex, _previewBlock);
                return;
            }
            _previewObstacleGate.Reset();
            _navigationVerified = true;
            if (OperatorSupervised)
            {
                _validationStatus = "READY | Operator-supervised route; confirmed obstacles are checked for a bypass.";
                return;
            }
            float width = _footprint != null ? _footprint.HalfWidth * 2 : _clearanceRadius * 2;
            _validationStatus = UsesRobotForwardGuard ?
                $"CLEAR | Body width {width:F2} m; turns and robot front guard checked. Dashed edges: body clearance." :
                $"CLEAR | Checked width {width:F2} m; turns also checked. Dashed edges: clearance. Thin line: route.";
        }

        private bool TryPreviewDetour(out Vector2[] route)
        {
            if (!TryGetPlanningGrid(out var routeGeometry, out var routeCells))
            {
                route = Array.Empty<Vector2>();
                _detourFailureReason = controller?.MapData?.RouteGridStatus ?? "Route coordinates are unavailable.";
                return false;
            }
            // Status text/freshness changes can refresh the UI without changing
            // any planning input. Reuse success or failure for that exact frame.
            bool unchanged = _detourRevision == _mapRevision && SameGeometry(_detourGeometry, _geometry) &&
                ReferenceEquals(_detourOccupancy, _navigationOccupancy) && ReferenceEquals(_detourRequest, _prepared) &&
                _detourPose == _robotPose && _detourLength == _footprint.LengthMeters &&
                _detourWidth == _footprint.WidthMeters && _detourMargin == _footprint.MarginMeters;
            if (unchanged)
            {
                route = _detourRoute;
                return _detourSucceeded;
            }
            bool sameRequest = SameGeometry(_detourGeometry, _geometry) && ReferenceEquals(_detourRequest, _prepared) &&
                _detourLength == _footprint.LengthMeters && _detourWidth == _footprint.WidthMeters &&
                _detourMargin == _footprint.MarginMeters;
            if (sameRequest && _detourSucceeded && _detourRoute != null && _detourRoute.Length >= 2)
            {
                Vector2 robot = new Vector2(_robotPose.x, _robotPose.y);
                Vector2[] candidate = MapPathGeometry.NormalizeExecutionPrefix(_detourRoute, robot, out _);
                // Recheck against every newly received occupancy frame. A
                // cached green route is never permission to cross a new cell.
                if (!TryFindPreviewBlock(candidate, out _))
                {
                    _detourRoute = candidate;
                    RememberDetourInputs();
                    route = candidate;
                    return true;
                }
            }
            if (Time.realtimeSinceStartupAsDouble < _nextDetourPlanAt)
            {
                _detourDeferred = true;
                route = Array.Empty<Vector2>();
                return false;
            }
            _nextDetourPlanAt = Time.realtimeSinceStartupAsDouble + .75;
            _detourSucceeded = MapDetourPlanner.TryPlan(routeGeometry, routeCells, _robotPose, _prepared,
                _footprint, out _detourRoute, out _detourFailureReason);
            RememberDetourInputs();
            route = _detourRoute;
            return _detourSucceeded;
        }

        private void RememberDetourInputs()
        {
            _detourRevision = _mapRevision;
            _detourGeometry = CopyGeometry(_geometry);
            _detourOccupancy = _navigationOccupancy;
            _detourRequest = _prepared;
            _detourPose = _robotPose;
            _detourLength = _footprint.LengthMeters;
            _detourWidth = _footprint.WidthMeters;
            _detourMargin = _footprint.MarginMeters;
        }

        private static int RouteHash(IReadOnlyList<Vector2> points)
        {
            if (points == null) return 0;
            unchecked
            {
                int hash = points.Count;
                foreach (Vector2 point in points)
                    hash = (hash * 397 ^ point.x.GetHashCode()) * 397 ^ point.y.GetHashCode();
                return hash;
            }
        }

        private bool UsesRobotForwardGuard => _footprint != null && controller != null && controller.PathClient != null &&
            controller.PathClient.automaticObstacleDetour;

        private bool TryFindPreviewBlock(IReadOnlyList<Vector2> points, out MapPathBlock block)
        {
            if (!TryGetPlanningGrid(out var geometry, out var cells))
            { block = new MapPathBlock { Kind = MapPathBlockKind.Invalid }; return true; }
            if (_footprint == null)
                return MapPathGeometry.TryFindBlock(geometry, cells, points, _clearanceRadius, out block);
            if (UsesRobotForwardGuard)
                return MapDetourPlanner.TryFindRouteBlock(geometry, cells, points, _robotPose.z, _footprint, out block);
            return MapPathGeometry.TryFindFootprintBlock(geometry, cells, points, _robotPose.z, _footprint, out block);
        }

        private bool TryGetPlanningGrid(out MapGridGeometry geometry, out int[] cells)
        {
            if (OperatorSupervised && controller.MapData != null)
                return controller.MapData.TryGetRouteGrid(true, _footprint, out geometry, out cells);
            geometry = _geometry;
            cells = _navigationOccupancy;
            return geometry != null && geometry.IsValid && cells != null &&
                cells.LongLength == (long)geometry.width * geometry.height;
        }

        private string DescribeBlock(string location, MapPathBlock block)
        {
            string reason = block.Kind == MapPathBlockKind.Unknown ? "unobserved sensor area" :
                block.Kind == MapPathBlockKind.Occupied ? "obstacle cells" :
                block.Kind == MapPathBlockKind.Boundary ? "outside map boundary" : "invalid navigation data";
            if ((block.Kind == MapPathBlockKind.Unknown || block.Kind == MapPathBlockKind.Occupied) &&
                controller != null && controller.MapData != null)
                reason = controller.MapData.DescribeNavigationCell(block.Position);
            string state = block.Kind == MapPathBlockKind.Unknown ? "WAITING FOR OBSERVED SPACE" : "BLOCKED";
            string marker = block.Kind == MapPathBlockKind.Occupied ? "Red cross: confirmed obstacle." : "Amber outline: waiting for observed space.";
            return $"{state} | {location}: {reason} at ({block.Position.x:F2}, {block.Position.y:F2}) m. {marker}";
        }

        private bool IsUnconfirmedCandidate(MapPathBlock block) => block != null &&
            controller != null && controller.MapData != null && controller.MapData.IsObstacleCandidate(block.Position);

        private static string DescribeCandidate(MapPathBlock block) =>
            $"CHECKING SENSOR | Unconfirmed return at ({block.Position.x:F2}, {block.Position.y:F2}) m. Amber outline; checking new scans.";

        private void RefreshPreview()
        {
            IReadOnlyList<Vector2> route = _runRoute;
            if (controller != null && controller.PathClient != null && controller.PathClient.IsActive)
                route = controller.PathClient.RemainingPoints;
            if (_hasPose && !IsDrawing && route != null && route.Count > 0)
                route = MapPathGeometry.NormalizeExecutionPrefix(route, new Vector2(_robotPose.x, _robotPose.y), out _);
            _pathGraphic?.SetPaths(_geometry, _raw, route, _hasPose, _robotPose,
                _navigationVerified && !IsDrawing, _previewBlock, _blockAtRobot, _clearanceRadius, IsDrawing, _footprint);
        }

        private void ApplyPlacementLock()
        {
            bool enabled = !PlacementLocked;
            if (_grab != null) _grab.enabled = enabled;
            if (_handGrab != null) _handGrab.enabled = enabled;
            if (_controllerGrab != null) _controllerGrab.enabled = enabled;
        }

        private void OnCanvasPointer(PointerEvent evt)
        {
            if (_pointer != evt.Identifier) return;
            // The UI module may project/clamp its PointerEventData position at
            // the canvas boundary. Validate the native world pose before that
            // conversion so an off-map release cannot accept a partial stroke.
            if (evt.Type == PointerEventType.Move || evt.Type == PointerEventType.Unselect)
            {
                if (DrawingSurface == null || !DrawingSurface.TryWorldToUv(evt.Pose.position, out Vector2 uv))
                {
                    CancelStroke("Drawing left the map. Draw again.");
                    return;
                }
                if (evt.Type == PointerEventType.Unselect)
                {
                    // Meta buffers its Unity PointerUp until Process(). A
                    // normal Unhover may arrive in this same frame first.
                    // Finish on the native release so that later Unhover and
                    // deferred UI PointerUp cannot cancel or end this twice.
                    AppendStroke(evt.Identifier, uv);
                    EndStroke(evt.Identifier);
                    return;
                }
            }
            if (evt.Type == PointerEventType.Cancel || evt.Type == PointerEventType.Unhover)
                CancelStroke("Pointer tracking ended. Draw again.");
        }

        private void EnsureWindow()
        {
            if (_root != null) return;
            EnsureInputModule();
            _root = new GameObject("PuppyPi floating map window");
            _root.SetActive(false);
            var body = _root.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            AddHandle("Top map grip", new Vector3(0, 0.3975f, 0), new Vector3(1.03f, 0.035f, 0.035f));
            AddHandle("Bottom map grip", new Vector3(0, -0.3975f, 0), new Vector3(1.03f, 0.035f, 0.035f));
            AddHandle("Left map grip", new Vector3(-0.4975f, 0, 0), new Vector3(0.035f, 0.76f, 0.035f));
            AddHandle("Right map grip", new Vector3(0.4975f, 0, 0), new Vector3(0.035f, 0.76f, 0.035f));
            _grab = _root.AddComponent<Grabbable>();
            _grab.InjectOptionalRigidbody(body);
            _grab.InjectOptionalTargetTransform(_root.transform);
            _grab.InjectOptionalKinematicWhileSelected(true);
            _grab.InjectOptionalThrowWhenUnselected(false);
            _grab.TransferOnSecondSelection = false;
            _grab.MaxGrabPoints = 2;
#pragma warning disable CS0618
            var one = _root.AddComponent<OneGrabFreeTransformer>();
            var two = _root.AddComponent<TwoGrabFreeTransformer>();
            two.InjectOptionalConstraints(new TwoGrabFreeTransformer.TwoGrabFreeConstraints {
                ConstraintsAreRelative = false,
                MinScale = new FloatConstraint { Constrain = true, Value = Mathf.Clamp(minimumScale, 0.1f, 1) },
                MaxScale = new FloatConstraint { Constrain = true, Value = Mathf.Clamp(maximumScale, 1, 4) },
                ConstrainXScale = true, ConstrainYScale = true, ConstrainZScale = true
            });
            _grab.InjectOptionalOneGrabTransformer(one);
            _grab.InjectOptionalTwoGrabTransformer(two);
#pragma warning restore CS0618
            _handGrab = _root.AddComponent<HandGrabInteractable>();
            _handGrab.InjectRigidbody(body);
            _handGrab.InjectSupportedGrabTypes(GrabTypeFlags.All);
            _handGrab.InjectOptionalPointableElement(_grab);
            _handGrab.HandAlignment = HandAlignType.None;
            _handGrab.ResetGrabOnGrabsUpdated = false;
            _controllerGrab = _root.AddComponent<GrabInteractable>();
            _controllerGrab.InjectRigidbody(body);
            _controllerGrab.InjectOptionalPointableElement(_grab);
            _controllerGrab.ResetGrabOnGrabsUpdated = false;
            _controllerGrab.UseClosestPointAsGrabSource = true;
            BuildCanvas();
            ApplyPlacementLock();
            RefreshUi();
        }

        private void AddHandle(string name, Vector3 position, Vector3 size)
        {
            var obj = new GameObject(name);
            obj.transform.SetParent(_root.transform, false);
            obj.transform.localPosition = position;
            var collider = obj.AddComponent<BoxCollider>();
            collider.size = size;
            collider.isTrigger = true;
        }

        private void BuildCanvas()
        {
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var rect = Rect("Map controls", _root.transform, new Vector2(Width, Height), Vector2.zero);
            rect.localScale = Vector3.one * MetersPerPixel;
            var canvas = rect.gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = Camera.main;
            canvas.sortingOrder = 45;
            rect.gameObject.AddComponent<GraphicRaycaster>();
            var background = rect.gameObject.AddComponent<Image>();
            background.color = new Color(0.02f, 0.04f, 0.06f, 0.99f);
            background.raycastTarget = false;
            _pointable = rect.gameObject.AddComponent<PointableCanvas>();
            _pointable.InjectCanvas(canvas);
            _pointable.WhenPointerEventRaised += OnCanvasPointer;
            ConfigureSurface(rect);
            AddRim(rect, new Vector2(1030, 35), new Vector2(0, 397.5f));
            AddRim(rect, new Vector2(1030, 35), new Vector2(0, -397.5f));
            AddRim(rect, new Vector2(35, 760), new Vector2(-497.5f, 0));
            AddRim(rect, new Vector2(35, 760), new Vector2(497.5f, 0));
            Label(rect, "MAP ROUTE | Grab rim to move | Two hands to resize", new Vector2(900, 34), new Vector2(0, 344), 21);
            StatusLabel = Label(rect, "Map status", new Vector2(900, 86), new Vector2(0, 282), 16);
            var area = Rect("Map viewport", rect, new Vector2(880, 480), new Vector2(0, -10));
            var imageRect = Rect("Map image", area, Vector2.zero, Vector2.zero);
            MapView = imageRect.gameObject.AddComponent<RawImage>();
            MapView.raycastTarget = false;
            imageRect.gameObject.AddComponent<RectMask2D>();
            _aspect = imageRect.gameObject.AddComponent<AspectRatioFitter>();
            _aspect.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            _aspect.aspectRatio = 1;
            DrawingSurface = imageRect.gameObject.AddComponent<PuppyPiMapDrawingSurface>();
            DrawingSurface.window = this;
            var overlay = Rect("Route preview", imageRect, Vector2.zero, Vector2.zero);
            overlay.anchorMin = Vector2.zero;
            overlay.anchorMax = Vector2.one;
            _pathGraphic = overlay.gameObject.AddComponent<PuppyPiMapPathGraphic>();
            _pathGraphic.raycastTarget = false;
            NoMapLabel = Label(area, "NO MAP", new Vector2(800, 90), Vector2.zero, 32);
            _drawButton = MakeButton(rect, "DRAW", 0, ToggleDrawMode);
            _clearButton = MakeButton(rect, "CLEAR", 1, () => { CancelPath("map_clear"); ClearDraft(); });
            _undoButton = MakeButton(rect, "UNDO", 2, Undo);
            _runButton = MakeButton(rect, "RUN", 3, Run);
            MakeButton(rect, "STOP", 4, Stop, true);
            MakeButton(rect, "RECENTER", 5, ShowAndRecenter);
            MakeButton(rect, "CLOSE", 6, () => SetPresentationVisible(false), true);
            Label(rect, "Dark: confirmed obstacle | Pale: unobserved | Red cross: obstacle | Amber outline: waiting",
                new Vector2(900, 30), new Vector2(0, -352), 16);
        }

        private void ConfigureSurface(RectTransform rect)
        {
            var surface = new GameObject("Map UI interaction surface");
            surface.transform.SetParent(rect, false);
            var plane = surface.AddComponent<PlaneSurface>();
            plane.Facing = PlaneSurface.NormalFacing.Backward;
            plane.DoubleSided = false;
            var bounds = surface.AddComponent<BoundsClipper>();
            bounds.Size = new Vector3(Width, Height, 0.01f);
            var patch = surface.AddComponent<ClippedPlaneSurface>();
            patch.InjectAllClippedPlaneSurface(plane, new IBoundsClipper[] { bounds });
            var poke = surface.AddComponent<PokeInteractable>();
            poke.InjectSurfacePatch(patch);
            poke.InjectOptionalPointableElement(_pointable);
            var ray = surface.AddComponent<RayInteractable>();
            ray.InjectSurface(patch);
            ray.InjectOptionalSelectSurface(plane);
            ray.InjectOptionalPointableElement(_pointable);
        }

        private Button MakeButton(Transform parent, string title, int index, Action action, bool always = false)
        {
            var rect = Rect(title, parent, new Vector2(116, 48), new Vector2(-372 + index * 124, -307));
            var image = rect.gameObject.AddComponent<Image>();
            image.color = title == "STOP" ? new Color(0.7f, 0.15f, 0.18f) : new Color(0.08f, 0.25f, 0.32f);
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            Label(rect, title, new Vector2(112, 42), Vector2.zero, 18);
            button.onClick.AddListener(() => {
                bool supervisedRun = OperatorSupervised && title == "RUN";
                if (!always && (IsDrawing || (!supervisedRun && (IsGrabbed || _externalLock)))) return;
                action();
            });
            return button;
        }

        private void AddRim(Transform parent, Vector2 size, Vector2 position)
        {
            var image = Rect("Map rim", parent, size, position).gameObject.AddComponent<Image>();
            image.color = new Color(0.08f, 0.3f, 0.36f);
            image.raycastTarget = false;
        }

        private Text Label(Transform parent, string text, Vector2 size, Vector2 position, int fontSize)
        {
            var label = Rect(text, parent, size, position).gameObject.AddComponent<Text>();
            label.font = _font;
            label.fontSize = fontSize;
            label.text = text;
            label.color = Color.white;
            label.alignment = TextAnchor.MiddleCenter;
            label.raycastTarget = false;
            label.supportRichText = false;
            return label;
        }

        private static RectTransform Rect(string name, Transform parent, Vector2 size, Vector2 position)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
            return rect;
        }

        private static void EnsureInputModule()
        {
            var module = FindFirstObjectByType<PointableCanvasModule>();
            if (module == null)
            {
                var events = EventSystem.current != null ? EventSystem.current : FindFirstObjectByType<EventSystem>();
                if (events == null) events = new GameObject("Map UI EventSystem", typeof(EventSystem)).GetComponent<EventSystem>();
                module = events.gameObject.AddComponent<PointableCanvasModule>();
            }
            module.ExclusiveMode = true;
        }

        private static MapGridGeometry CopyGeometry(MapGridGeometry geometry) => geometry == null ? null : new MapGridGeometry {
            width = geometry.width, height = geometry.height, resolution = geometry.resolution,
            origin = geometry.origin, frame = geometry.frame
        };
        private static bool SameGeometry(MapGridGeometry a, MapGridGeometry b) => a != null && b != null &&
            a.width == b.width && a.height == b.height && a.resolution == b.resolution && a.origin.Equals(b.origin) && a.frame == b.frame;
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static string Trim(string value, int count) => string.IsNullOrEmpty(value) ? "" : value.Length <= count ? value : value.Substring(0, count);

        private void OnApplicationPause(bool paused) { if (paused) Stop(); }
        private void OnApplicationFocus(bool focused) { if (!focused) Stop(); }
        private void OnDisable() { SetPresentationVisible(false); }
        private void OnDestroy()
        {
            if (_pointable != null) _pointable.WhenPointerEventRaised -= OnCanvasPointer;
            if (_root == null) return;
            if (Application.isPlaying) Destroy(_root); else DestroyImmediate(_root);
        }
    }
}
