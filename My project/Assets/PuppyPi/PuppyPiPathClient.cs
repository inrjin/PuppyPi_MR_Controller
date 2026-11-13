using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

namespace PuppyPi
{
    // Receipts only acknowledge UDP ingress. The read-only HTTP state confirms
    // execution. Obstacle detours replace a route only after an acknowledged
    // cancellation and a subsequent fresh stopped snapshot. Supervised routes
    // tolerate auxiliary telemetry gaps; explicit cancellation never restarts one.
    [DefaultExecutionOrder(110)]
    [DisallowMultipleComponent]
    public sealed class PuppyPiPathClient : MonoBehaviour
    {
        public PuppyPiRobotController controller;
        [Tooltip("PuppyPi official dimensions, with a configurable walking allowance on each side.")]
        public MapRobotFootprint robotFootprint = new MapRobotFootprint();
        public bool automaticObstacleDetour = true;
        [Tooltip("Operator watches the robot: unknown map cells do not prevent motion; confirmed obstacles still require a bypass.")]
        public bool operatorSupervisedNavigation = true;
        public MapRobotFootprint Footprint => robotFootprint;
        public float clearanceRadiusMeters => Footprint != null ? Footprint.RotationRadius : 0.22f;
        public bool IsActive => _phase != Phase.Idle;
        public bool IsDetouring => _usingDetour || _detourPending;
        public string ControlBlockReason => StartBlockReason();
        public string Status { get; private set; } = "Draw a path, then select RUN.";
        public string RemoteState { get; private set; } = "unknown";
        public string DiagnosticSummary => $"phase={_phase} sent={_pathSent} sequence={_pathSequence} " +
            $"remote={RemoteState} cancelReceipts={_cancelSequences.Count} stopAck={!double.IsPositiveInfinity(_cancelConfirmedAt)}";
        public IReadOnlyList<Vector2> Points => _points.AsReadOnly();
        public IReadOnlyList<Vector2> RemainingPoints => _remaining.AsReadOnly();
        private enum Phase { Idle, Sending, Running, Cancelling, Replanning }
        private const double PollSeconds = 0.25, FreshSeconds = 1.0;
        private const double StartTimeoutSeconds = 3.0, StatusLossSeconds = 1.5, CancelTimeoutSeconds = 1.5;
        private readonly List<Vector2> _points = new List<Vector2>();
        private readonly List<Vector2> _intent = new List<Vector2>(), _remaining = new List<Vector2>();
        private readonly MapPathProgressMonitor _progress = new MapPathProgressMonitor();
        private readonly MapObstacleReplanGate _obstacleGate = new MapObstacleReplanGate();
        private readonly MapObstacleReplanGate _startObstacleGate = new MapObstacleReplanGate();
        private readonly HashSet<long> _cancelSequences = new HashSet<long>();
        private Phase _phase;
        private PuppyPiV2Client _drive;
        private string _host, _clientId, _frame, _session, _observedFrame, _observedSession, _cancelReason;
        private double _startedAt, _sentAt, _cancelAt, _nextPoll, _lastFreshAt, _nextCancel;
        private double _cancelConfirmedAt = double.PositiveInfinity;
        private long _pathSequence;
        private bool _pathSent, _observedRunning, _pathIdentityObserved, _unverifiedCompletion, _paused, _focused = true;
        private bool _detourPending, _usingDetour, _verifyObstacleAfterStop;
        private bool _lastManualEngaged;
        private bool _cancelRequiresEmergencySnapshot;
        private bool _cancelRequiresWatchdogSnapshot;
        private int _intentCursor, _waypointIndex, _detoursUsed, _intentRoutePrefix;
        private const int MaximumDetours = 3;
        private double _nextObstacleCheck, _stopVerifiedAt;
        private long _bodyObservationStamp = -1;
        private readonly Vector2[] _bodyProbe = new Vector2[1];
        private UnityWebRequest _request;
        private UnityWebRequest _preemptedStopRequest;
        private double _nextHttpDiagnostic;
        private int _generation;
        private static double Now => Time.realtimeSinceStartupAsDouble;

        private void OnEnable() => Bind();

        private void Bind()
        {
            if (controller == null) controller = GetComponent<PuppyPiRobotController>();
            var drive = controller != null ? controller.DriveClient : null;
            if (_drive == drive) return;
            if (_drive != null)
            {
                if (IsActive)
                {
                    if (operatorSupervisedNavigation)
                    {
                        Cancel("Control client changed; waiting for the previous path to stop.");
                        if (IsActive && drive != null &&
                            (!_drive.isActiveAndEnabled || !_drive.networkEnabled || _drive.Session.Retired))
                            Finish("Previous control channel closed; the old route will not resume.");
                        else if (IsActive)
                        {
                            _drive.SetPathHeartbeatSuspended(true);
                            return; // Keep the old channel until its cancellation is confirmed.
                        }
                    }
                    else { _drive.EmergencyStop(); Finish("Control client changed; stop requested."); }
                }
                _drive.PathCancellationRequested -= Cancel;
                _drive.PathAckObserved -= OnPathAck;
            }
            _drive = drive;
            if (_drive != null)
            {
                _drive.PathCancellationRequested += Cancel;
                _drive.PathAckObserved += OnPathAck;
            }
        }

        public bool Execute(IReadOnlyList<Vector2> points, string mapFrame, string mapSession, out string error)
        {
            Bind();
            error = StartBlockReason();
            if (error != null) return false;
            if (points == null || points.Count < 1 || points.Count > 20)
            { error = "Prepare between 1 and 20 map points."; return false; }
            foreach (var p in points)
                if (!PuppyPiMapProjection.Finite(p.x) || !PuppyPiMapProjection.Finite(p.y) || Mathf.Abs(p.x) > 100 || Mathf.Abs(p.y) > 100)
                { error = "Map coordinates must be finite and within 100 metres."; return false; }
            mapFrame = string.IsNullOrEmpty(mapFrame) ? "map" : mapFrame;
            if (mapFrame.Length > 128 || string.IsNullOrWhiteSpace(mapSession) || mapSession.Length > 128)
            { error = "A current map frame and session are required."; return false; }
            if (_observedFrame != null && (_observedFrame != mapFrame || _observedSession != mapSession))
            { error = "The map changed. Draw the path again."; return false; }
            error = MapContextReason(points, mapFrame, mapSession);
            if (error != null) return false;
            var currentPosition = new Vector2(controller.MapData.RobotPose.x, controller.MapData.RobotPose.y);
            bool needsMovement = false;
            foreach (var point in points)
                // Float coordinates and millimetre-rounded robot telemetry
                // must not turn a boundary no-op into a one-tick fake start.
                if (Vector2.Distance(currentPosition, point) > _progress.ArrivalMeters + .001)
                { needsMovement = true; break; }
            if (!needsMovement)
            {
                error = "The whole route is already within the robot's 0.10 m arrival tolerance. Draw a longer route.";
                return false;
            }
            Vector2[] intent = MapPathGeometry.NormalizeExecutionPrefix(points, currentPosition, out _);
            Vector2[] route = intent;
            if (route.Length > 20)
            { error = "The route including the current robot start exceeds the 20-point limit."; return false; }
            int[] skipped = Array.Empty<int>();
            error = FindMapBlockReason(route, mapFrame, mapSession, out MapPathBlock initialBlock);
            bool detour = error != null;
            if (detour)
            {
                if (!automaticObstacleDetour) return false;
                // Missing observations and unconfirmed returns are not a wall
                // to route around. They keep this draft pending, while an
                // already-known clear corridor starts without filter warmup.
                if (initialBlock == null || initialBlock.Kind != MapPathBlockKind.Occupied)
                { error = "Waiting for observed route space. " + error; return false; }
                var window = controller.MapWindow;
                bool reviewed = window != null && window.IsVisible && window.BypassPreviewReady;
                if (!operatorSupervisedNavigation && !reviewed && _startObstacleGate.Observe(controller.MapData.NavigationObservationStamp,
                    controller.MapData.NavigationObservationAt, Now, initialBlock, false) != MapObstacleDecision.Replan)
                { error = "Verifying a persistent obstacle before preparing a bypass. Keep the current draft."; return false; }
            }
            if (!controller.MapData.TryGetRouteGrid(operatorSupervisedNavigation, Footprint, out var routeGeometry, out var routeCells))
            { error = controller.MapData.RouteGridStatus; return false; }
            if (detour && (!automaticObstacleDetour ||
                !MapDetourPlanner.TryPlanDetailed(routeGeometry, routeCells,
                    controller.MapData.RobotPose, intent, Footprint, out route, out skipped, out error))) return false;
            error = MapBlockReason(route, mapFrame, mapSession);
            if (error != null) return false;
            _intent.Clear();
            var skip = new HashSet<int>(skipped);
            // The current-position anchor is not an unfinished user goal.
            // Keep only the unconsumed intent, with planner-skipped corners
            // removed by their indices in the same normalized request.
            for (int i = 1; i < intent.Length; i++) if (!skip.Contains(i)) _intent.Add(intent[i]);
            _intentCursor = _waypointIndex = _detoursUsed = _intentRoutePrefix = 0;
            _detourPending = false;
            _verifyObstacleAfterStop = false;
            _usingDetour = detour;
            _progress.Reset();
            _obstacleGate.Reset();
            _startObstacleGate.Reset();
            _points.Clear();
            _points.AddRange(route);
            RefreshRemaining();
            _frame = mapFrame;
            _session = mapSession;
            _host = _drive.robotAddress;
            _clientId = _drive.Session.ClientId;
            _phase = Phase.Sending;
            _bodyObservationStamp = -1;
            _pathSent = _observedRunning = _pathIdentityObserved = _unverifiedCompletion = false;
            _pathSequence = 0;
            _cancelSequences.Clear();
            _cancelConfirmedAt = double.PositiveInfinity;
            _cancelRequiresEmergencySnapshot = false;
            _cancelRequiresWatchdogSnapshot = false;
            _startedAt = _lastFreshAt = Now;
            _nextPoll = Now;
            _generation++;
            RemoteState = "unknown";
            Status = detour ? "DETOUR | Waiting for neutral ACK before the straight bypass." :
                "Waiting for neutral ACK before sending the path.";
            _drive.SetPathCommandHold(true, !operatorSupervisedNavigation);
            _lastManualEngaged = _drive.DriveInput != null && _drive.DriveInput.Engaged;
            if (Debug.isDebugBuild)
                Debug.Log($"PUPPYPI_PATH_ACCEPT {DiagnosticSummary} points={_points.Count} " +
                    $"robot={controller.MapData.RobotPose} first={_points[0]} goal={_points[_points.Count - 1]}", this);
            return true;
        }

        private string StartBlockReason(bool replacingStoppedPath = false)
        {
            if (!isActiveAndEnabled || _paused || !_focused) return "Return to the MR app before running a path.";
            if (IsActive && !replacingStoppedPath) return "Cancel the current path before running another.";
            if (_drive == null || !_drive.isActiveAndEnabled || !_drive.networkEnabled ||
                (!operatorSupervisedNavigation && !_drive.Session.Connected))
                return "Connect to the robot first.";
            if (controller.StudyActive || _drive.StudyMotionBlocked || _drive.StudyCommandHeld)
                return "Path execution is unavailable during the inspection study.";
            if (!operatorSupervisedNavigation && (_drive.DriveInput == null || _drive.DriveInput.Engaged || controller.IsPositioningPanel ||
                (controller.MapWindow != null && controller.MapWindow.IsDrawing) || _drive.ExternalMotionBlocked))
                return "Release the grip and movable screens before running a path.";
            var session = _drive.Session;
            if (session.Emergency) return "VR emergency stop is active. Select CLEAR STOP on the dashboard.";
            if (session.Faulted || session.Retired) return "The control session is stopped or faulted. Check the robot status.";
            if (operatorSupervisedNavigation) return null;
            if (!session.LastAccepted || session.ServerReason == "not_owner") return "This control session does not own the robot.";
            if (!session.NeutralConfirmed) return "Wait for the neutral ACK.";
            var ros = controller.RosBridge;
            if (ros == null || !ros.IsConnected || !ros.RobotStatusFresh || !ros.ArbiterStateFresh)
                return "Wait for fresh robot status.";
            if (ros.ArbiterState.estop) return "Robot emergency stop is active. Check CLEAR STOP / RESET ROBOT on the dashboard.";
            if (!ros.ArbiterState.vr_permitted || ros.RobotStatus.engine != "ready" || ros.HasPendingCommand)
                return "The robot is not ready for VR motion.";
            if (!string.IsNullOrEmpty(ros.ArbiterState.owner) && ros.ArbiterState.owner != "vr")
                return "Wait for the current robot operation to finish.";
            return null;
        }

        private string MapContextReason(IReadOnlyList<Vector2> points, string frame, string session)
        {
            var map = controller != null ? controller.MapData : null;
            if (map == null || !map.EditingAvailable || string.IsNullOrEmpty(map.SessionId) ||
                (operatorSupervisedNavigation ? !map.PoseAvailable :
                    controller.MediaClient == null || !controller.MediaClient.MapInfoLive || !map.Live))
                return "Wait for a live map and robot position.";
            if (map.Geometry == null || map.Geometry.frame != frame || map.SessionId != session)
                return "The map changed. Draw the path again.";
            if (!operatorSupervisedNavigation && !map.NavigationLive) return map.NavigationStatus;
            if (points == null || points.Count == 0 || Footprint == null || !Footprint.IsValid)
                return "Valid route and robot dimensions are required.";
            var robot = new Vector2(map.RobotPose.x, map.RobotPose.y);
            if (!PuppyPiMapProjection.Finite(robot.x) || !PuppyPiMapProjection.Finite(robot.y) ||
                !PuppyPiMapProjection.Finite(map.RobotPose.z) ||
                (!operatorSupervisedNavigation && Vector2.Distance(robot, points[0]) > 0.3f))
                return "Start the path within 0.30 m of the robot.";
            return null;
        }

        private string MapBlockReason(IReadOnlyList<Vector2> points, string frame, string session)
            => FindMapBlockReason(points, frame, session, out _);

        private string FindMapBlockReason(IReadOnlyList<Vector2> points, string frame, string session, out MapPathBlock block)
        {
            block = null;
            string error = MapContextReason(points, frame, session);
            if (error != null) return error;
            var map = controller.MapData;
            if (!map.TryGetRouteGrid(operatorSupervisedNavigation, Footprint, out var geometry, out var cells))
                return map.RouteGridStatus;
            var robot = new Vector2(map.RobotPose.x, map.RobotPose.y);
            Vector2[] fullPath = MapPathGeometry.NormalizeExecutionPrefix(points, robot, out _);
            if (MapPathGeometry.TryFindFootprintBlock(geometry, cells, new[] { robot }, map.RobotPose.z, Footprint, out MapPathBlock startBlock))
            { block = startBlock; return DescribeMapBlock(startBlock, "Robot area"); }
            MapPathBlock pathBlock;
            bool blocked = automaticObstacleDetour ?
                MapDetourPlanner.TryFindRouteBlock(geometry, cells, fullPath, map.RobotPose.z, Footprint, out pathBlock) :
                MapPathGeometry.TryFindFootprintBlock(geometry, cells, fullPath, map.RobotPose.z, Footprint, out pathBlock);
            if (blocked)
            { block = pathBlock; return DescribeMapBlock(pathBlock, pathBlock.SegmentIndex == 0 ? "Robot-to-start connection" : "Route segment " + pathBlock.SegmentIndex); }
            return null;
        }

        private string DescribeMapBlock(MapPathBlock block, string section)
        {
            string reason = block.Kind == MapPathBlockKind.Unknown ? "unobserved space" :
                block.Kind == MapPathBlockKind.Occupied ? "an occupied map cell" :
                block.Kind == MapPathBlockKind.Boundary ? "the map boundary" : "invalid map data";
            var map = controller != null ? controller.MapData : null;
            int baseValue = -999;
            if (map?.Geometry != null && map.BaseOccupancy != null)
            {
                int x = Mathf.FloorToInt((block.Position.x - map.Geometry.origin.x) / map.Geometry.resolution);
                int y = Mathf.FloorToInt((block.Position.y - map.Geometry.origin.y) / map.Geometry.resolution);
                if (x >= 0 && y >= 0 && x < map.Geometry.width && y < map.Geometry.height)
                    baseValue = map.BaseOccupancy[y * map.Geometry.width + x];
            }
            Debug.Log($"PUPPYPI_PATH_BLOCK section={section} kind={block.Kind} x={block.Position.x:F3} y={block.Position.y:F3} value={block.CellValue} clearance={clearanceRadiusMeters:F2} " +
                $"base={baseValue} candidate={map?.IsObstacleCandidate(block.Position)} prior={map?.IsMapKnownFree(block.Position)} " +
                $"{DiagnosticSummary} robot={map?.RobotPose}", this);
            string source = controller?.MapData != null ? controller.MapData.DescribeNavigationCell(block.Position) : reason;
            return $"{section}: {source} at ({block.Position.x:F2}, {block.Position.y:F2}) m. Review the red marker.";
        }

        public void ObserveMapIdentity(string frame, string session)
        {
            frame = string.IsNullOrEmpty(frame) ? "map" : frame;
            _observedFrame = frame;
            _observedSession = session;
            if (IsActive && (_frame != frame || _session != session)) Cancel("Map origin/session changed. Draw a new path.");
        }

        public void Cancel(string reason)
        {
            _obstacleGate.Reset();
            _startObstacleGate.Reset();
            // STOP/manual/lifecycle cancellation always revokes a pending
            // detour, even if an obstacle cancellation is already in flight.
            _detourPending = false;
            _verifyObstacleAfterStop = false;
            if (_phase == Phase.Cancelling) return;
            BeginCancel(reason);
        }

        private void BeginCancel(string reason)
        {
            if (IsActive && Debug.isDebugBuild)
                Debug.Log($"PUPPYPI_PATH_CANCEL {DiagnosticSummary} reason={reason} " +
                    $"ackAge={_drive?.AckAge:F3} mapInfoAge={controller?.MediaClient?.MapAgeSeconds:F3} " +
                    $"map=[{controller?.MapData?.DiagnosticSummary}]", this);
            if (!IsActive || _phase == Phase.Cancelling) return;
            _obstacleGate.Reset();
            _cancelReason = string.IsNullOrWhiteSpace(reason) ? "Path cancelled" : reason;
            // Before any send attempt there is no remote operation to cancel.
            // A nonzero sequence still requires the normal stop barrier even
            // when the transport did not report success: delivery is uncertain.
            if (_phase == Phase.Sending && !_pathSent && _pathSequence == 0)
            {
                bool held = _drive != null && _drive.PathCommandHeld;
                Finish("Route not sent: " + _cancelReason + ". Wait for neutral ACK, then review and RUN again.");
                if (_drive != null && !held) _drive.RequestNeutral();
                return;
            }
            _phase = Phase.Cancelling;
            _cancelAt = Now;
            _cancelSequences.Clear();
            _cancelConfirmedAt = double.PositiveInfinity;
            _cancelRequiresEmergencySnapshot = false;
            _cancelRequiresWatchdogSnapshot = false;
            _nextCancel = Now;
            _nextPoll = Now;
            Status = "Cancelling: " + _cancelReason;
            SendCancel();
        }

        private void RequestObstacleDetour(string reason) => RequestObstacleDetourCore(reason, false);

        private void RequestObstacleDetourCore(string reason, bool verifyAfterStop)
        {
            if (!automaticObstacleDetour || _phase != Phase.Running) return;
            if (!operatorSupervisedNavigation && _detoursUsed >= MaximumDetours)
            { Cancel("Repeated obstacles: stopped after three bypass attempts. Review and RUN again."); return; }
            _detourPending = true;
            _verifyObstacleAfterStop = verifyAfterStop;
            BeginCancel(reason);
            Status = "OBSTACLE | Stopping before planning a forward-only straight bypass.";
        }

        private void ObserveRemainingObstacles()
        {
            if (_phase != Phase.Running || !automaticObstacleDetour) return;
            var map = controller != null ? controller.MapData : null;
            if (map == null || !map.NavigationLive || _remaining.Count == 0)
            {
                if (operatorSupervisedNavigation)
                {
                    _obstacleGate.Reset();
                    Status = "SUPERVISED | Waiting for obstacle observations; the requested route is retained.";
                    return;
                }
                if (RemoteState == "blocked") Status = "SENSOR HOLD | Keeping the original route while the obstacle is verified.";
                return;
            }
            if (operatorSupervisedNavigation && map.NavigationAgeSeconds >= MapObstacleReplanGate.MaximumObservationAgeSeconds)
            { _obstacleGate.Reset(); return; }
            if (!map.TryGetRouteGrid(operatorSupervisedNavigation, Footprint, out var geometry, out var cells))
            { Cancel(map.RouteGridStatus); return; }
            var robot = new Vector2(map.RobotPose.x, map.RobotPose.y);
            Vector2[] ahead = MapPathGeometry.NormalizeExecutionPrefix(_remaining, robot, out _);
            if (MapPathGeometry.TryFindFootprintBlock(geometry, cells, new[] { robot },
                map.RobotPose.z, Footprint, out MapPathBlock bodyBlock))
            {
                Cancel("Robot area is not verified: " + DescribeMapBlock(bodyBlock, "Robot area"));
                return;
            }
            bool blocked = MapDetourPlanner.TryFindRouteBlock(geometry, cells, ahead,
                map.RobotPose.z, Footprint, out MapPathBlock navigationBlock);
            MapPathBlock confirmed = null;
            if (operatorSupervisedNavigation)
            { if (blocked && navigationBlock.Kind == MapPathBlockKind.Occupied) confirmed = navigationBlock; }
            else map.TryFindConfirmedRouteObstacle(ahead, map.RobotPose.z, Footprint, out confirmed);
            if (confirmed != null && confirmed.Kind != MapPathBlockKind.Occupied) confirmed = null;
            if (blocked && navigationBlock.Kind != MapPathBlockKind.Occupied)
            {
                // Always stop before entering unknown space. Independent current
                // obstacle evidence can preserve the intent to verify a bypass
                // AFTER stop is acknowledged, without permitting this route to
                // continue through the unknown cell while votes accumulate.
                if (navigationBlock.Kind == MapPathBlockKind.Unknown && confirmed != null)
                {
                    RequestObstacleDetourCore("Route observation is incomplete beside a confirmed obstacle: " +
                        DescribeMapBlock(navigationBlock, "Remaining route"), true);
                    return;
                }
                Cancel("Route observation is incomplete; no detour requested. " +
                    DescribeMapBlock(navigationBlock, "Remaining route"));
                return;
            }
            if (confirmed == null && (operatorSupervisedNavigation || RemoteState != "blocked"))
            {
                // Clear navigation keeps the existing telemetry policy. The
                // stricter obstacle-verification age must not become a new
                // global freshness lock for an already verified free route.
                _obstacleGate.Reset();
                return;
            }
            MapObstacleDecision decision = _obstacleGate.Observe(map.NavigationObservationStamp,
                map.NavigationObservationAt, Now, confirmed, !operatorSupervisedNavigation && RemoteState == "blocked");
            if (decision == MapObstacleDecision.Replan)
                RequestObstacleDetour("Confirmed persistent obstacle: " + DescribeMapBlock(confirmed, "Remaining route"));
            else if (decision == MapObstacleDecision.UnconfirmedStop)
            {
                if (operatorSupervisedNavigation) _obstacleGate.Reset();
                else Cancel("Sensor blockage could not be confirmed. Original route retained; inspect the sensor before another RUN.");
            }
            else if (decision == MapObstacleDecision.Verifying)
                Status = RemoteState == "blocked" ?
                    "VERIFYING SENSOR | Robot paused; keeping the original route. No bypass sent." :
                    "VERIFYING OBSTACLE | Keeping the original route until the obstruction persists.";
        }

        private void ObserveConfirmedRobotArea()
        {
            var map = controller != null ? controller.MapData : null;
            if (map == null || !map.NavigationLive || map.NavigationObservationStamp == _bodyObservationStamp) return;
            if (operatorSupervisedNavigation && map.NavigationAgeSeconds >= MapObstacleReplanGate.MaximumObservationAgeSeconds) return;
            _bodyObservationStamp = map.NavigationObservationStamp;
            _bodyProbe[0] = new Vector2(map.RobotPose.x, map.RobotPose.y);
            // Consume the existing confirmed-body stop criterion on the new
            // sensor frame. Candidates/unknown cells keep their normal route
            // checks; obstacle votes and detour timing remain unchanged.
            bool blocked;
            MapPathBlock block = null;
            if (operatorSupervisedNavigation)
                blocked = map.TryGetRouteGrid(true, Footprint, out var geometry, out var cells) &&
                    MapPathGeometry.TryFindFootprintBlock(geometry, cells, _bodyProbe, map.RobotPose.z, Footprint, out block) &&
                    block.Kind == MapPathBlockKind.Occupied;
            else blocked = map.TryFindConfirmedRouteObstacle(_bodyProbe, map.RobotPose.z, Footprint, out block);
            if (blocked)
                Cancel("Robot area is not verified: " + DescribeMapBlock(block, "Robot area"));
        }

        private void RefreshRemaining()
        {
            _remaining.Clear();
            for (int i = Math.Max(0, _waypointIndex); i < _points.Count; i++) _remaining.Add(_points[i]);
        }

        private bool VerifyStoppedObstacle()
        {
            if (_phase != Phase.Replanning || !_verifyObstacleAfterStop) return false;
            if (Now - _stopVerifiedAt > StartTimeoutSeconds)
            { Finish("STOPPED | Persistent obstacle could not be verified after stopping. Review and RUN again."); return false; }
            var map = controller != null ? controller.MapData : null;
            if (map == null || !map.NavigationLive || map.Geometry == null ||
                map.Geometry.frame != _frame || map.SessionId != _session)
            { Finish("STOPPED | Bypass verification lost its current map context."); return false; }
            var robot = new Vector2(map.RobotPose.x, map.RobotPose.y);
            if (!map.TryGetRouteGrid(operatorSupervisedNavigation, Footprint, out var geometry, out var cells))
            { Finish("STOPPED | " + map.RouteGridStatus); return false; }
            if (MapPathGeometry.TryFindFootprintBlock(geometry, cells, new[] { robot },
                map.RobotPose.z, Footprint, out MapPathBlock bodyBlock))
            { Finish("STOPPED | Robot area is not verified: " + DescribeMapBlock(bodyBlock, "Robot area")); return false; }
            if (map.NavigationObservationAt < _stopVerifiedAt)
            { Status = "STOP CONFIRMED | Waiting for a new obstacle observation after stopping."; return false; }
            Vector2[] ahead = MapPathGeometry.NormalizeExecutionPrefix(_remaining, robot, out _);
            map.TryFindConfirmedRouteObstacle(ahead, map.RobotPose.z, Footprint, out MapPathBlock confirmed);
            if (confirmed == null || confirmed.Kind != MapPathBlockKind.Occupied)
            {
                _obstacleGate.Reset();
                Status = "STOP CONFIRMED | Waiting for persistent current obstacle evidence before a bypass.";
                return false;
            }
            MapObstacleDecision decision = _obstacleGate.Observe(map.NavigationObservationStamp,
                map.NavigationObservationAt, Now, confirmed, false);
            if (decision == MapObstacleDecision.Replan)
            {
                _verifyObstacleAfterStop = false;
                return true;
            }
            if (decision == MapObstacleDecision.UnconfirmedStop)
                Finish("STOPPED | Current obstacle evidence could not be verified. Review and RUN again.");
            else Status = "STOP CONFIRMED | Verifying the persistent obstacle before planning a bypass.";
            return false;
        }

        private void AdvanceIntent(int completedPointCount)
        {
            // Ordered exact waypoint matches preserve deliberate L/Z corners;
            // proximity to a later leg cannot skip an unfinished user goal.
            int completed = Math.Min(completedPointCount, _points.Count);
            for (int i = _intentRoutePrefix; i < completed; i++)
                if (_intentCursor < _intent.Count && (_points[i] - _intent[_intentCursor]).sqrMagnitude < 0.000004f)
                    _intentCursor++;
            _intentRoutePrefix = Math.Max(_intentRoutePrefix, completed);
        }

        private void ReplaceAfterConfirmedStop()
        {
            _detourPending = false;
            _verifyObstacleAfterStop = false;
            string gate = StartBlockReason(true);
            if (gate != null) { Finish("Stopped; bypass cancelled: " + gate); return; }
            var map = controller.MapData;
            var robot = map != null ? new Vector2(map.RobotPose.x, map.RobotPose.y) : Vector2.zero;
            Vector2[] goals = MapPathGeometry.NormalizeExecutionPrefix(_intent, robot,
                out int consumedIntent, _intentCursor);
            int remainingIntentCursor = _intentCursor + consumedIntent;
            gate = MapContextReason(goals, _frame, _session);
            if (gate != null) { Finish("Stopped; bypass unavailable: " + gate); return; }
            if (goals.Length < 2) { Finish("Route completed while stopping."); return; }
            if (!map.TryGetRouteGrid(operatorSupervisedNavigation, Footprint, out var geometry, out var cells))
            { Finish("STOPPED | " + map.RouteGridStatus); return; }
            if (!MapDetourPlanner.TryPlanDetailed(geometry, cells, map.RobotPose, goals, Footprint,
                out Vector2[] route, out int[] skipped, out string error))
            { Finish("STOPPED | No verified forward bypass: " + error); return; }
            Vector2[] interrupted = MapPathGeometry.NormalizeExecutionPrefix(_remaining, robot, out _);
            if (!operatorSupervisedNavigation && EquivalentRoute(interrupted, route))
            { Finish("STOPPED | No different verified route around the detected obstacle. Wait for its scan or redraw."); return; }
            _intentCursor = remainingIntentCursor;
            // The drawing is a preferred route, so an intermediate corner now
            // covered by an obstacle may be bypassed. Remove only the explicit
            // skipped goals; reaching a rejoin point still requires execution.
            Array.Sort(skipped);
            for (int i = skipped.Length - 1; i >= 0; i--)
            {
                int intentIndex = _intentCursor + skipped[i] - 1; // goals[0] is current robot position
                if (skipped[i] > 0 && intentIndex >= _intentCursor && intentIndex < _intent.Count - 1)
                    _intent.RemoveAt(intentIndex);
            }
            // Sensor/map updates may have occurred during planning. The actual
            // datagram is checked again immediately before it is sent.
            _points.Clear();
            _points.AddRange(route);
            _waypointIndex = _intentRoutePrefix = 0;
            RefreshRemaining();
            _phase = Phase.Sending;
            _bodyObservationStamp = -1;
            _usingDetour = true;
            _progress.Reset();
            _obstacleGate.Reset();
            _detoursUsed++;
            _pathSent = _observedRunning = _pathIdentityObserved = _unverifiedCompletion = false;
            _pathSequence = 0;
            _cancelSequences.Clear();
            _cancelConfirmedAt = double.PositiveInfinity;
            _cancelRequiresEmergencySnapshot = false;
            _cancelRequiresWatchdogSnapshot = false;
            _startedAt = _lastFreshAt = Now;
            _nextPoll = Now;
            _generation++;
            Status = "DETOUR | Stop confirmed. Preparing a forward-only straight bypass.";
            Debug.Log($"PUPPYPI_PATH_DETOUR attempt={_detoursUsed} points={route.Length} remainingGoals={goals.Length - 1}", this);
        }

        private static bool EquivalentRoute(IReadOnlyList<Vector2> first, IReadOnlyList<Vector2> second)
        {
            var a = NormalizeRoute(first);
            var b = NormalizeRoute(second);
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if ((a[i] - b[i]).sqrMagnitude > .0025f) return false;
            return true;
        }

        private static List<Vector2> NormalizeRoute(IReadOnlyList<Vector2> points)
        {
            var result = new List<Vector2>();
            foreach (var point in points)
            {
                if (result.Count > 0 && (result[result.Count - 1] - point).sqrMagnitude < .0001f) continue;
                while (result.Count > 1)
                {
                    Vector2 before = result[result.Count - 1] - result[result.Count - 2];
                    Vector2 after = point - result[result.Count - 1];
                    float cross = before.x * after.y - before.y * after.x;
                    if (Vector2.Dot(before, after) <= 0 || Math.Abs(cross) > .01f * (before.magnitude + after.magnitude)) break;
                    result.RemoveAt(result.Count - 1);
                }
                result.Add(point);
            }
            return result;
        }

        private void SendCancel()
        {
            if (_drive == null || _drive.Session.ClientId != _clientId || _drive.robotAddress != _host) return;
            if (_drive.TryCancelPath(out long sequence, out _)) _cancelSequences.Add(sequence);
            _nextCancel = Now + PollSeconds;
        }

        private void OnPathAck(V2Ack ack, string kind)
        {
            if (!IsActive) return;
            if (Debug.isDebugBuild)
                Debug.Log($"PUPPYPI_PATH_RECEIPT kind={kind} sequence={ack.sequence} accepted={ack.accepted} " +
                    $"reason={ack.reason} state={ack.state} {DiagnosticSummary}", this);
            if (kind == "path" && ack.sequence == _pathSequence)
            {
                // A duplicate datagram can be rejected after the original was
                // applied. Only fresh HTTP execution identity confirms startup;
                // an out-of-order receipt must not override it in either direction.
                if (!ack.accepted && ack.reason != "out_of_order") Cancel("Path rejected: " + ack.reason);
                else if (ack.accepted && _phase == Phase.Sending) Status = "Path accepted; waiting for live execution status.";
            }
            // The robot advances owner/sequence checks before rejecting a
            // cancellation under an already latched emergency. That correlated
            // receipt can bar an older path, but only an emergency stopped
            // snapshot may release this hold; it never authorizes a bypass.
            bool emergencyBarrier = operatorSupervisedNavigation && !ack.accepted &&
                ack.reason == "emergency_stop" && ack.emergency_stop && ack.state == "emergency_stop";
            bool watchdogBarrier = operatorSupervisedNavigation && !ack.accepted && !ack.emergency_stop &&
                ack.reason == "neutral_required" && (ack.state == "awaiting_neutral" || ack.state == "timeout");
            if (_phase == Phase.Cancelling && kind == "path_cancel" && (ack.accepted || emergencyBarrier || watchdogBarrier) &&
                _cancelSequences.Contains(ack.sequence))
            {
                bool nextEmergency = operatorSupervisedNavigation &&
                    (emergencyBarrier || (ack.emergency_stop && ack.state == "emergency_stop"));
                bool proofChanged = _cancelRequiresEmergencySnapshot != nextEmergency ||
                    _cancelRequiresWatchdogSnapshot != watchdogBarrier;
                _cancelRequiresEmergencySnapshot = nextEmergency;
                _cancelRequiresWatchdogSnapshot = watchdogBarrier;
                if (emergencyBarrier || watchdogBarrier)
                {
                    _detourPending = _verifyObstacleAfterStop = false;
                }
                // Watchdog rejection also follows the owner's sequence check:
                // it bars an older path but needs an inactive watchdog snapshot.
                // A change of proof kind requires one new read. Repeated ACKs
                // of the same kind must never starve that stopped-state read.
                bool firstReceipt = double.IsPositiveInfinity(_cancelConfirmedAt);
                if (firstReceipt || proofChanged) _cancelConfirmedAt = Now;
                if ((firstReceipt || proofChanged) && _request != null)
                {
                    // A request started before this receipt cannot prove the
                    // follower stopped after cancellation. Do not let its full
                    // timeout delay the first useful stopped-state read.
                    _preemptedStopRequest = _request;
                    _request.Abort();
                }
                _nextPoll = Now;
            }
            // The receipt prevents a delayed older path from starting; HTTP
            // independently confirms that the follower has stopped.
        }

        private bool WaitingForInitialHandshake => _phase == Phase.Sending && !_pathSent && _pathSequence == 0;

        private bool CanTransmitPreparedPath()
        {
            if (_drive == null || _drive.PathHeartbeatSuspended) return false;
            var session = _drive.Session;
            return session.Connected && session.LastAccepted && session.NeutralConfirmed &&
                !session.Emergency && !session.Faulted && !session.Retired;
        }

        private void ObserveSupervisedControl()
        {
            bool engaged = _drive.DriveInput != null && _drive.DriveInput.Engaged;
            bool newManualGrab = engaged && !_lastManualEngaged;
            _lastManualEngaged = engaged;
            var session = _drive.Session;
            if (!_drive.networkEnabled || !_drive.isActiveAndEnabled ||
                (!session.Connected && !WaitingForInitialHandshake))
                Cancel("Control connection lost");
            else if (_paused || !_focused || controller.StudyActive || _drive.StudyMotionBlocked ||
                _drive.StudyCommandHeld || newManualGrab)
                Cancel("Manual takeover or MR app unavailable");
            else if (session.Emergency || session.Faulted || session.Retired ||
                (!session.LastAccepted && !WaitingForInitialHandshake))
                Cancel("Robot rejected control or entered a stop state");
            else
            {
                var ros = controller.RosBridge;
                if (ros != null && ros.IsConnected && ros.ArbiterStateFresh && ros.ArbiterState.estop)
                    Cancel("Robot emergency stop is active");
            }
        }

        private void Update()
        {
            Bind();
            if (!IsActive) return;
            if (_drive == null || _drive.Session.ClientId != _clientId || _drive.robotAddress != _host)
            { Finish("Connection changed. The previous path will not be resumed."); return; }
            // Keep revocation checks active while cancellation is in flight.
            // A temporary telemetry loss must not become permission to resume
            // merely because it recovers before the stopped HTTP snapshot.
            if (operatorSupervisedNavigation) ObserveSupervisedControl();
            else
            {
                if (!_drive.networkEnabled || !_drive.isActiveAndEnabled || !_drive.Session.Connected)
                    Cancel("Control connection lost");
                else if (_paused || !_focused || controller.StudyActive || _drive.StudyMotionBlocked || _drive.StudyCommandHeld ||
                    _drive.ExternalMotionBlocked || controller.IsPositioningPanel || (_drive.DriveInput != null && _drive.DriveInput.Engaged))
                    Cancel("Manual input, layout movement or motion gate");
                else if (!_drive.Session.LastAccepted || _drive.Session.Emergency || _drive.Session.Faulted)
                    Cancel("Robot rejected control or entered a stop state");
                else
                {
                    var ros = controller.RosBridge;
                    if (ros == null || !ros.IsConnected || !ros.RobotStatusFresh || !ros.ArbiterStateFresh ||
                        ros.ArbiterState.estop || !ros.ArbiterState.vr_permitted || ros.HasPendingCommand)
                        Cancel("Robot status is stale or VR motion is blocked");
                    else if (controller.MapData == null || (operatorSupervisedNavigation ?
                        !controller.MapData.HasNavigationPose : !controller.MapData.NavigationLive))
                        Cancel("Map or robot position became stale");
                    else if (!operatorSupervisedNavigation && (controller.MediaClient == null || !controller.MediaClient.MapInfoLive))
                        Cancel("Map session metadata became stale");
                }
            }
            if (_detourPending && !automaticObstacleDetour) Cancel("Automatic obstacle detours disabled");
            if (_phase == Phase.Sending && !_pathSent &&
                (operatorSupervisedNavigation ? CanTransmitPreparedPath() : _drive.Session.NeutralConfirmed))
            {
                _sentAt = Now;
                string error = MapContextReason(_points, _frame, _session);
                if (error != null) { Cancel(error); return; }
                // A fresh neutral ACK can arrive after the accepted preview.
                // Reanchor the actual datagram as well as its final validation;
                // consumed intent must not return during a later obstacle bypass.
                var robot = new Vector2(controller.MapData.RobotPose.x, controller.MapData.RobotPose.y);
                Vector2[] execution = MapPathGeometry.NormalizeExecutionPrefix(_points, robot, out _);
                if (execution.Length < 2)
                { Finish("Route completed before transmission; no movement requested."); return; }
                _points.Clear();
                _points.AddRange(execution);
                _intentCursor += MapPathGeometry.ReachedExecutionPrefix(_intent, robot, _intentCursor);
                _waypointIndex = _intentRoutePrefix = 0;
                RefreshRemaining();
                error = MapBlockReason(_points, _frame, _session);
                if (error != null) Cancel(error);
                else if (_drive.TrySendPath(_points, out _pathSequence, out error))
                {
                    _pathSent = true;
                    Status = "Path sent once at robot-configured speed; waiting for live execution status.";
                    if (Debug.isDebugBuild) Debug.Log("PUPPYPI_PATH_SENT " + DiagnosticSummary, this);
                }
                else Cancel(error);
            }
            if (_phase == Phase.Sending && Now - _startedAt > StartTimeoutSeconds)
            {
                if (operatorSupervisedNavigation && _pathSent)
                    Status = "SUPERVISED | Path sent; waiting for execution status. No automatic resend.";
                else Cancel(_unverifiedCompletion ?
                    "The robot reported a finished route, but this execution could not be identified. Update the robot path service; no automatic retry was sent" :
                    "Path execution could not be verified; no automatic retry was sent");
            }
            if (!operatorSupervisedNavigation && _phase == Phase.Running && Now - _lastFreshAt > StatusLossSeconds)
                Cancel("Path status became stale");
            if (_phase == Phase.Replanning)
            {
                // path_cancel invalidates neutral acknowledgement. A fresh
                // neutral heartbeat must be acknowledged before replacement;
                // an immediate HTTP reply must not turn that normal ordering
                // into either an unsafe restart or a failed detour.
                string gate = StartBlockReason(true);
                if (gate == "Wait for the neutral ACK." && Now - _stopVerifiedAt < StartTimeoutSeconds)
                { Status = "STOP CONFIRMED | Waiting for neutral ACK before planning the bypass."; return; }
                if (gate != null) { Finish("Stopped; bypass cancelled: " + gate); return; }
                if (_verifyObstacleAfterStop)
                {
                    if (Now - _stopVerifiedAt > StartTimeoutSeconds)
                    { Finish("STOPPED | Persistent obstacle could not be verified after stopping. Review and RUN again."); return; }
                    if (Now < _nextObstacleCheck) return;
                    _nextObstacleCheck = Now + PollSeconds;
                    if (!VerifyStoppedObstacle()) return;
                }
                ReplaceAfterConfirmedStop();
            }
            if (_phase == Phase.Running && automaticObstacleDetour)
                ObserveConfirmedRobotArea();
            if (_phase == Phase.Running && automaticObstacleDetour && Now >= _nextObstacleCheck)
            {
                _nextObstacleCheck = Now + PollSeconds;
                ObserveRemainingObstacles();
            }
            if (_phase == Phase.Cancelling)
            {
                if (Now - _cancelAt > CancelTimeoutSeconds)
                {
                    if (operatorSupervisedNavigation)
                    {
                        _drive.SetPathHeartbeatSuspended(true);
                        Status = "STOP PENDING | Path heartbeat suspended; waiting for confirmed stop. " + _cancelReason;
                    }
                    else
                    {
                        _drive.EmergencyStop();
                        Finish("Stop unconfirmed; VR emergency stop requested. " + _cancelReason);
                        return;
                    }
                }
                if (Now >= _nextCancel) SendCancel();
            }
            if (_request == null && Now >= _nextPoll && IsActive && (_pathSent || _phase == Phase.Cancelling))
            {
                _nextPoll = Now + PollSeconds;
                StartCoroutine(PollStatus(_generation));
            }
        }

        private IEnumerator PollStatus(int generation)
        {
            var handler = new PathJsonHandler();
            var request = new UnityWebRequest(new UriBuilder("http", _host, 8090, "/api/path").Uri.AbsoluteUri,
                UnityWebRequest.kHttpVerbGET) { downloadHandler = handler, timeout = 1, redirectLimit = 0 };
            _request = request;
            double requestedAt = Now;
            try
            {
                UnityWebRequestAsyncOperation operation = null;
                try { operation = request.SendWebRequest(); }
                catch (Exception e) when (e is UnityException || e is InvalidOperationException)
                { Status = "Path status unavailable: " + e.Message; }
                if (operation != null) yield return operation;
                if (generation != _generation || !IsActive) yield break;
                if (ReferenceEquals(request, _preemptedStopRequest)) yield break;
                if (operation != null && request.result == UnityWebRequest.Result.Success && !handler.TooLarge)
                {
                    try { ApplyStatus(handler.Text, requestedAt); }
                    catch (DecoderFallbackException) { Status = "Invalid path status encoding."; }
                }
                else
                {
                    Status = "Path status unavailable; waiting for stop confirmation if needed.";
                    if (Debug.isDebugBuild && Now >= _nextHttpDiagnostic)
                    {
                        _nextHttpDiagnostic = Now + 1;
                        Debug.Log($"PUPPYPI_PATH_HTTP result={request.result} code={request.responseCode} " +
                            $"elapsed={Now - requestedAt:F3} error={request.error} {DiagnosticSummary}", this);
                    }
                }
            }
            finally
            {
                if (ReferenceEquals(request, _preemptedStopRequest)) _preemptedStopRequest = null;
                if (_request == request) _request = null;
                request.Dispose();
            }
        }

        private bool ApplyStatus(string json, double requestedAt)
        {
            if (!PuppyPiPathStatus.TryParse(json, out var report))
            { Status = "Invalid path status; execution is unconfirmed."; return false; }
            double age = report.AgeSeconds + Math.Max(0, Now - requestedAt);
            if (age > FreshSeconds) { Status = "Stale path status."; return false; }
            RemoteState = report.State;
            if (_phase == Phase.Cancelling)
            {
                // Require a validated cancellation receipt, then a new HTTP poll.
                // An idle snapshot alone cannot rule out a delayed path datagram.
                if (!report.Active && requestedAt >= _cancelConfirmedAt && age <= Now - _cancelConfirmedAt + 0.02 &&
                    (!_cancelRequiresEmergencySnapshot || (report.Emergency && report.SessionState == "emergency_stop")) &&
                    (!_cancelRequiresWatchdogSnapshot || (!report.Emergency &&
                        (report.SessionState == "awaiting_neutral" || report.SessionState == "timeout"))))
                {
                    if (operatorSupervisedNavigation) _drive?.SetPathHeartbeatSuspended(false);
                    if (report.Emergency || (report.SessionState != "idle" && report.SessionState != "active"))
                        _detourPending = false;
                    if (_detourPending)
                    {
                        _phase = Phase.Replanning;
                        _stopVerifiedAt = Now;
                        _pathSent = false;
                        Status = "STOP CONFIRMED | Waiting for neutral ACK before planning the bypass.";
                    }
                    else Finish("Cancelled: " + _cancelReason + ". Wait for neutral ACK, then grab again.");
                }
                return true;
            }
            if (!_pathSent || requestedAt < _sentAt || age > Now - _sentAt + 0.02) return false;
            if (report.Emergency || (report.SessionState != "idle" && report.SessionState != "active"))
            { Cancel("Robot path session stopped: " + report.SessionState); return true; }
            if (report.HasRequestIdentity)
            {
                // The identity is assigned by the robot's path callback, not
                // its earlier UDP receipt. It also survives a route that ends
                // before the first HTTP poll observes an active state.
                if (report.RequestClientId != _clientId || report.RequestSequence != _pathSequence)
                {
                    if (_observedRunning) Cancel("Robot path execution changed unexpectedly");
                    return false;
                }
            }
            else if (_pathIdentityObserved)
                return false; // A cached legacy snapshot cannot remove this requirement.
            if (!report.Matches(_points))
            {
                if (_observedRunning) Cancel("Robot path changed unexpectedly");
                return false;
            }
            if (report.HasRequestIdentity) _pathIdentityObserved = true;
            // Legacy services still require a fresh active state. Point
            // equality or a UDP ACK alone cannot identify an old 'done'.
            if (report.Active)
            {
                if (!report.ElapsedSeconds.HasValue || report.ElapsedSeconds.Value > Now - _sentAt + FreshSeconds)
                    return false;
                _observedRunning = true;
                _phase = Phase.Running;
                _lastFreshAt = Now - age;
                _waypointIndex = Math.Max(_waypointIndex, report.Index);
                AdvanceIntent(_waypointIndex);
                RefreshRemaining();
                Status = report.Waiting != null ? "Path paused: waiting for " + report.Waiting :
                    report.State == "blocked" ? "Path blocked by an obstacle." :
                    $"{(_usingDetour ? "DETOUR" : "Following path")}: {Math.Min(report.Index + 1, report.Count)} / {report.Count} | straight sections";
                if (report.Waiting == null && automaticObstacleDetour) ObserveRemainingObstacles();
                if (operatorSupervisedNavigation && controller?.MapData != null && !controller.MapData.NavigationLive)
                    _progress.Reset();
                if (!operatorSupervisedNavigation && _phase == Phase.Running && report.Waiting == null && report.Index == _waypointIndex &&
                    _waypointIndex < _points.Count && controller != null && controller.MapData != null &&
                    (!operatorSupervisedNavigation || controller.MapData.NavigationLive) &&
                    !_progress.Check(controller.MapData.RobotPose, report.State, _waypointIndex,
                        _points[_waypointIndex], Now, out string progressError))
                    Cancel("No motion progress: " + progressError + ". Review the robot before another RUN.");
            }
            else if (report.State == "done")
            {
                if (_observedRunning || report.HasRequestIdentity)
                    Finish("Path completed. Wait for neutral ACK, then grab again.");
                else
                {
                    _unverifiedCompletion = true;
                    Status = "Finished route received without an execution identity; completion is unverified.";
                }
            }
            else if (_observedRunning) Finish("Path stopped: " + (report.Reason ?? report.State));
            return true;
        }

        private void Finish(string status)
        {
            if (IsActive && Debug.isDebugBuild)
                Debug.Log($"PUPPYPI_PATH_FINISH {DiagnosticSummary} status={status} " +
                    $"robot={controller?.MapData?.RobotPose} map=[{controller?.MapData?.DiagnosticSummary}]", this);
            _phase = Phase.Idle;
            _detourPending = false;
            _verifyObstacleAfterStop = false;
            _usingDetour = false;
            _remaining.Clear();
            _progress.Reset();
            _obstacleGate.Reset();
            _startObstacleGate.Reset();
            Status = status;
            _generation++;
            _request?.Abort();
            if (_drive != null) _drive.SetPathCommandHold(false);
        }

        private void StopForLifecycle(string reason)
        {
            if (!IsActive) return;
            Cancel(reason);
            if (operatorSupervisedNavigation)
            {
                if (IsActive)
                {
                    _drive?.SetPathHeartbeatSuspended(true);
                    Status = reason + "; waiting for remote stop confirmation. The route will not resume automatically.";
                }
                return;
            }
            // Polling cannot be relied on while the application is suspended.
            _drive?.EmergencyStop();
            Finish(reason + "; VR emergency stop requested. The path will not resume automatically.");
        }

        private void OnApplicationPause(bool paused) { _paused = paused; if (paused) StopForLifecycle("MR app paused"); }
        private void OnApplicationFocus(bool focused) { _focused = focused; if (!focused) StopForLifecycle("MR app lost focus"); }
        private void OnDisable()
        {
            StopForLifecycle("Path client disabled");
            if (_drive != null)
            {
                _drive.PathCancellationRequested -= Cancel;
                _drive.PathAckObserved -= OnPathAck;
            }
            _drive = null;
            _generation++;
            _request?.Abort();
            StopAllCoroutines();
            _request?.Dispose();
            _request = null;
            _preemptedStopRequest = null;
        }

        private sealed class PathJsonHandler : DownloadHandlerScript
        {
            private readonly byte[] _bytes = new byte[65536];
            private int _count;
            public bool TooLarge { get; private set; }
            public string Text => new UTF8Encoding(false, true).GetString(_bytes, 0, _count);
            public PathJsonHandler() : base(new byte[4096]) { }
            protected override bool ReceiveData(byte[] data, int length)
            {
                if (data == null || length <= 0) return true;
                if (length > _bytes.Length - _count) { TooLarge = true; return false; }
                Buffer.BlockCopy(data, 0, _bytes, _count, length);
                _count += length;
                return true;
            }
        }
    }

    // Bounded JSON parsing is needed because Unity JsonUtility silently defaults
    // missing fields and cannot deserialize the robot's [[x,y],...] points.
    public sealed class PuppyPiPathStatus
    {
        public string State { get; private set; }
        public string SessionState { get; private set; }
        public string Waiting { get; private set; }
        public string Reason { get; private set; }
        public bool Emergency { get; private set; }
        public double AgeSeconds { get; private set; }
        public double? ElapsedSeconds { get; private set; }
        public string RequestClientId { get; private set; }
        public long? RequestSequence { get; private set; }
        public bool HasRequestIdentity => RequestSequence.HasValue;
        public int Index { get; private set; }
        public int Count { get; private set; }
        public IReadOnlyList<Vector2> Points { get; private set; }
        public bool Active => State == "rotate" || State == "drive" || State == "blocked";
        public bool Matches(IReadOnlyList<Vector2> points)
        {
            if (points == null || points.Count != Points.Count) return false;
            for (int i = 0; i < points.Count; i++)
                if (!PuppyPiMapProjection.Finite(points[i].x) || !PuppyPiMapProjection.Finite(points[i].y) ||
                    Mathf.Abs(points[i].x - Points[i].x) > 0.0011f || Mathf.Abs(points[i].y - Points[i].y) > 0.0011f) return false;
            return true;
        }

        public static bool TryParse(string json, out PuppyPiPathStatus status)
        {
            status = null;
            if (!JsonReader.TryRead(json, out var value) || !(value is Dictionary<string, object> root) ||
                !Text(root, "media", out string media) || media != "puppypi-media/1" ||
                !Number(root, "age_s", out double age) || age < 0 ||
                !Text(root, "session_state", out string session) || !SessionStateKnown(session) ||
                !root.TryGetValue("emergency_stop", out object stop) || !(stop is bool emergency) ||
                !root.TryGetValue("path", out object path) || !(path is Dictionary<string, object> p) ||
                !Text(p, "state", out string state) || !StateKnown(state) ||
                !Number(p, "index", out double index) || index < 0 || index != Math.Floor(index) ||
                !Number(p, "count", out double count) || count < 0 || count > 20 || count != Math.Floor(count) || index > count ||
                !p.TryGetValue("points", out object rawPoints) || !(rawPoints is List<object> array) || array.Count != count ||
                !NullableText(p, "waiting", out string waiting) || (waiting != null && waiting != "pose" && waiting != "scan") ||
                !NullableText(p, "reason", out string reason) || !p.TryGetValue("elapsed_s", out object elapsed) ||
                (elapsed != null && (!(elapsed is double seconds) || seconds < 0)) ||
                !RequestIdentity(p, out string requestClientId, out long? requestSequence)) return false;
            var points = new List<Vector2>();
            foreach (var raw in array)
            {
                if (!(raw is List<object> xy) || xy.Count != 2 || !(xy[0] is double x) || !(xy[1] is double y) ||
                    Math.Abs(x) > 100 || Math.Abs(y) > 100) return false;
                points.Add(new Vector2((float)x, (float)y));
            }
            if ((state == "rotate" || state == "drive" || state == "blocked") && (count == 0 || index >= count)) return false;
            if (state == "done" && index != count) return false;
            status = new PuppyPiPathStatus { State = state, SessionState = session, Emergency = emergency, AgeSeconds = age,
                Waiting = waiting, Reason = reason, Index = (int)index, Count = (int)count, Points = points.AsReadOnly(),
                ElapsedSeconds = elapsed == null ? (double?)null : (double)elapsed,
                RequestClientId = requestClientId, RequestSequence = requestSequence };
            return true;
        }

        private static bool RequestIdentity(Dictionary<string, object> p, out string clientId, out long? sequence)
        {
            clientId = null;
            sequence = null;
            bool hasClient = p.TryGetValue("request_client_id", out object rawClient);
            bool hasSequence = p.TryGetValue("request_sequence", out object rawSequence);
            if (!hasClient && !hasSequence) return true; // Legacy service.
            if (!hasClient || !hasSequence) return false;
            if (rawClient == null && rawSequence == null) return true; // No identified execution yet.
            if (!(rawClient is string text) || text.Length != 36 ||
                !Guid.TryParseExact(text, "D", out Guid id) || id == Guid.Empty ||
                !(rawSequence is double value) || value < 0 || value > 9007199254740991L || value != Math.Floor(value))
                return false;
            clientId = id.ToString("D");
            sequence = (long)value;
            return true;
        }

        private static bool StateKnown(string s) => s == "idle" || s == "rotate" || s == "drive" || s == "blocked" || s == "done" || s == "cancelled";
        private static bool SessionStateKnown(string s) => s == "idle" || s == "active" || s == "timeout" || s == "emergency_stop" || s == "awaiting_neutral" || s == "fault";
        private static bool Number(Dictionary<string, object> p, string key, out double n)
        { n = 0; if (!p.TryGetValue(key, out object o) || !(o is double value)) return false; n = value; return true; }
        private static bool Text(Dictionary<string, object> p, string key, out string s)
        { s = null; if (!p.TryGetValue(key, out object o) || !(o is string value)) return false; s = value; return true; }
        private static bool NullableText(Dictionary<string, object> p, string key, out string s)
        { s = null; if (!p.TryGetValue(key, out object o) || (o != null && !(o is string))) return false; s = o as string; return true; }

        private sealed class JsonReader
        {
            private static readonly Regex Numeric = new Regex(@"\G-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?", RegexOptions.CultureInvariant);
            private readonly string _json;
            private int _at, _tokens;
            private JsonReader(string json) { _json = json; }
            public static bool TryRead(string json, out object value)
            {
                value = null;
                if (string.IsNullOrWhiteSpace(json) || json.Length > 65536) return false;
                var reader = new JsonReader(json);
                try { value = reader.Read(0); reader.Space(); return reader._at == json.Length; }
                catch (FormatException) { return false; }
            }
            private void Space() { while (_at < _json.Length && " \r\n\t".IndexOf(_json[_at]) >= 0) _at++; }
            private bool Take(char c) { Space(); if (_at >= _json.Length || _json[_at] != c) return false; _at++; return true; }
            private object Read(int depth)
            {
                Space();
                if (depth > 8 || ++_tokens > 1024 || _at >= _json.Length) throw new FormatException();
                char c = _json[_at];
                if (c == '"') return String();
                if (Take('{'))
                {
                    var obj = new Dictionary<string, object>();
                    if (Take('}')) return obj;
                    do
                    {
                        Space();
                        if (_at >= _json.Length || _json[_at] != '"') throw new FormatException();
                        string key = String();
                        if (obj.Count >= 64 || obj.ContainsKey(key) || !Take(':')) throw new FormatException();
                        obj.Add(key, Read(depth + 1));
                        if (Take('}')) return obj;
                    } while (Take(','));
                    throw new FormatException();
                }
                if (Take('['))
                {
                    var arr = new List<object>();
                    if (Take(']')) return arr;
                    do { if (arr.Count >= 128) throw new FormatException(); arr.Add(Read(depth + 1)); if (Take(']')) return arr; } while (Take(','));
                    throw new FormatException();
                }
                foreach (string literal in new[] { "true", "false", "null" })
                    if (_at + literal.Length <= _json.Length && string.CompareOrdinal(_json, _at, literal, 0, literal.Length) == 0)
                    { _at += literal.Length; return literal == "null" ? null : (object)(literal == "true"); }
                var match = Numeric.Match(_json, _at);
                if (!match.Success || !double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ||
                    double.IsNaN(number) || double.IsInfinity(number)) throw new FormatException();
                _at += match.Length;
                return number;
            }
            private string String()
            {
                _at++;
                var text = new StringBuilder();
                while (_at < _json.Length)
                {
                    char c = _json[_at++];
                    if (c == '"') return text.ToString();
                    if (c < 32 || text.Length > 4096) throw new FormatException();
                    if (c != '\\') { text.Append(c); continue; }
                    if (_at >= _json.Length) throw new FormatException();
                    char escape = _json[_at++];
                    switch (escape)
                    {
                        case '"': case '\\': case '/': text.Append(escape); break;
                        case 'b': text.Append('\b'); break;
                        case 'f': text.Append('\f'); break;
                        case 'n': text.Append('\n'); break;
                        case 'r': text.Append('\r'); break;
                        case 't': text.Append('\t'); break;
                        case 'u':
                            if (_at + 4 > _json.Length || !ushort.TryParse(_json.Substring(_at, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort code)) throw new FormatException();
                            text.Append((char)code); _at += 4; break;
                        default: throw new FormatException();
                    }
                }
                throw new FormatException();
            }
        }
    }
}
