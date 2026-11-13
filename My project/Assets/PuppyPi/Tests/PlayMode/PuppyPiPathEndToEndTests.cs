#if UNITY_EDITOR
using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using Oculus.Interaction;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace PuppyPi.Tests
{
    // Real component lifecycle, native canvas pointer events and real local
    // HTTP/WS/UDP. No readiness, phase, ACK or freshness field is overwritten.
    public class PuppyPiPathEndToEndTests
    {
        private GameObject _root;
        private LocalPathRobot _robot;
        private PuppyPiRobotController _controller;
        private PuppyPiV2Client _drive;
        private PuppyPiMapWindow _window;
        private PointableCanvasModule _module;
        private PathRunBeforeLateUpdate _frameTiming;
        private int _pathReceipts;
        private Vector2 _twoObstacleGoal;

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            if (_robot != null)
                Debug.Log($"PUPPYPI_HARD_METRICS case={TestContext.CurrentContext.Test.Name} " +
                    $"ghostScans={_robot.ReferenceGhostScans} droppedHeartbeats={_robot.ReferenceDroppedHeartbeats} " +
                    $"droppedCancels={_robot.ReferenceDroppedCancels} emergencyCommands={_robot.ReferenceEmergencyCommands} " +
                    $"maxUnityFrameGap={_frameTiming?.MaximumFrameGap:F3}");
            if (_robot != null)
                Debug.Log($"PUPPYPI_FULL_FLOW case={TestContext.CurrentContext.Test.Name} paths={_robot.PathCount} " +
                    $"cancels={_robot.CancelCount} heartbeats={_robot.HeartbeatCount} activeHeartbeats={_robot.NonNeutralPathHeartbeats} " +
                    $"simulatedPosition=({_robot.X:F3},{_robot.Y:F3},{_robot.Yaw:F3}) state={_robot.State} " +
                    $"collisionSamples={_robot.CollisionSamples} error={_robot.Error} pathStatus={_controller?.PathClient?.Status} " +
                    $"events={string.Join(",", _robot.Events)} motorCommands=false");
            if (_root != null) Object.Destroy(_root);
            yield return null;
            _robot?.Dispose();
            _robot = null;
            yield return null;
        }

        private IEnumerator Connect(bool navigationExpected = true, bool supervised = false)
        {
            if (_robot == null) _robot = new LocalPathRobot();
            _root = new GameObject("Complete MR path flow; localhost only");
            _root.SetActive(false);
            _frameTiming = _root.AddComponent<PathRunBeforeLateUpdate>();
            _root.AddComponent<EventSystem>();
            _module = _root.AddComponent<PointableCanvasModule>();
            var head = new GameObject("Simulated viewer", typeof(Camera));
            head.transform.SetParent(_root.transform, false);
            head.transform.position = new Vector3(0, 1.6f, -1);
            var grip = new GameObject("Simulated released grip");
            grip.transform.SetParent(_root.transform, false);
            var input = _root.AddComponent<BallGripInput>();
            input.grip = grip.transform;
            input.baseFrame = _root.transform;
            input.editorPreview = true;
            input.previewGrabbed = false;
            _drive = _root.AddComponent<PuppyPiV2Client>();
            _drive.input = input;
            _drive.PathCancellationRequested += LogCancellationFrameTiming;
            _controller = _root.AddComponent<PuppyPiRobotController>();
            _controller.driveClient = _drive;
            _controller.gripInput = input;
            _controller.showCameraScreenOnStart = false;
            _controller.ResolveComponents();
            Assert.That(_controller.PathClient.operatorSupervisedNavigation, Is.True,
                "a newly created path controller must use the operator-supervised default");
            if (!supervised) _controller.PathClient.operatorSupervisedNavigation = false;
            // The camera is not part of route permission, and the user disabled
            // it. Keep the real media metadata channel active for session gates.
            _controller.cameraStream.streamEnabled = false;
            _controller.mapStream.streamEnabled = false;
            var windowObject = new GameObject("Path drawing window");
            windowObject.transform.SetParent(_root.transform, false);
            _window = windowObject.AddComponent<PuppyPiMapWindow>();
            _window.controller = _controller;
            _window.head = head.transform;
            _controller.mapWindow = _window;
            _pathReceipts = 0;
            _drive.PathAckObserved += (ack, kind) => { if (kind == "path" && ack.accepted) _pathReceipts++; };
            _root.SetActive(true);
            Assert.That(_drive.ConnectTo("127.0.0.1", _robot.UdpPort, out string error), Is.True, error);
            _controller.ShowMapWindow();
            yield return Until(() => _drive.Session.Connected && _drive.Session.NeutralConfirmed &&
                _controller.RosBridge.IsConnected && _controller.RosBridge.RobotStatusFresh &&
                _controller.RosBridge.ArbiterStateFresh && _controller.MediaClient.MapInfoLive &&
                _controller.MapData.EditingAvailable && _controller.MapData.PoseAvailable,
                "real connection, ownership, neutral, ROS status or map session did not become ready");
            if (navigationExpected)
                yield return Until(() => _controller.MapData.NavigationLive, "exact paired scan/pose navigation did not become live");
            // Real input is delivered only after LateUpdate has presented the
            // received map. Network readiness alone does not make the UI ready.
            yield return Until(() => _window.IsVisible && _window.MapView != null &&
                _window.MapView.texture != null && !_window.NoMapLabel.gameObject.activeSelf,
                "the received map never reached the visible drawing surface");
            Assert.That(_drive.Session.LastAccepted, Is.True);
            Assert.That(_drive.Session.Emergency, Is.False);
            Assert.That(_controller.StudyActive, Is.False);
            Assert.That(_robot.HeartbeatCount, Is.GreaterThan(0));
            Canvas.ForceUpdateCanvases();
        }

        private void LogCancellationFrameTiming(string reason)
        {
            if (_controller?.PathClient == null || !_controller.PathClient.IsActive || _frameTiming == null) return;
            double sinceFrameSample = double.IsNaN(_frameTiming.LastFrameAt) ? double.NaN :
                Time.realtimeSinceStartupAsDouble - _frameTiming.LastFrameAt;
            Debug.Log($"PUPPYPI_FRAME_TIMING case={TestContext.CurrentContext.Test.Name} reason={reason} " +
                $"lastUnityFrameGap={_frameTiming.LastFrameGap:F3} maxUnityFrameGap={_frameTiming.MaximumFrameGap:F3} " +
                $"sinceFrameSample={sinceFrameSample:F3} ackAge={_drive.AckAge:F3} " +
                $"path={_controller.PathClient.DiagnosticSummary} map=[{_controller.MapData?.DiagnosticSummary}]");
        }

        private string Diagnostic => _robot == null ? "no peer" :
            $"peer={_robot.Error}; paths={_robot.PathCount}; cancel={_robot.CancelCount}; peerState={_robot.State}; " +
            $"drive={_drive?.TransportStatus}; path={_controller?.PathClient?.Status}; gate={_window?.RunUnavailableReason}; " +
            $"map={_controller?.MapData?.DiagnosticSummary}; UI={_window?.StatusLabel?.text}";

        private IEnumerator Until(Func<bool> predicate, string message, double seconds = 8)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + seconds;
            while (!predicate() && Time.realtimeSinceStartupAsDouble < deadline)
            {
                Assert.That(_robot.Error, Is.Empty, Diagnostic);
                yield return null;
            }
            Assert.That(predicate(), Is.True, message + " | " + Diagnostic);
            Assert.That(_robot.Error, Is.Empty, Diagnostic);
        }

        private void Pointer(int id, PointerEventType kind, Vector2 meters)
        {
            RectTransform rect = _window.MapView.rectTransform;
            Vector2 uv = _controller.MapData.Geometry.MetersToUv(meters);
            Vector3 point = rect.TransformPoint(new Vector3(Mathf.LerpUnclamped(rect.rect.xMin, rect.rect.xMax, uv.x),
                Mathf.LerpUnclamped(rect.rect.yMin, rect.rect.yMax, uv.y), 0));
            var canvas = _window.WindowRoot.GetComponentInChildren<PointableCanvas>();
            canvas.ProcessPointerEvent(new PointerEvent(id, kind, new Pose(point, _window.WindowRoot.rotation)));
            _module.Process();
        }

        private IEnumerator Draw(float distance, int pointer = 7100, Vector2? explicitStart = null)
        {
            Vector2 start = explicitStart ?? new Vector2((float)_robot.X, (float)_robot.Y);
            Vector2 direction = new Vector2((float)Math.Cos(_robot.Yaw), (float)Math.Sin(_robot.Yaw));
            Vector2 side = new Vector2(-direction.y, direction.x);
            int pathsBeforeDrawing = _robot.PathCount;
            float jitter = .006f;
            _window.ToggleDrawMode();
            Assert.That(_window.DrawMode, Is.True, Diagnostic);
            Pointer(pointer, PointerEventType.Hover, start);
            Pointer(pointer, PointerEventType.Select, start);
            Assert.That(_window.IsDrawing, Is.True, "native canvas Select must begin the real drawing handler");
            yield return new WaitForSecondsRealtime(.025f);
            Assert.That(_drive.ExternalMotionBlocked, Is.True, "a real controller frame must block manual motion during drawing");
            Assert.That(_drive.Session.Armed, Is.False);
            Assert.That(_drive.LastCommand, Is.Not.Null);
            Assert.That(_drive.LastCommand.control_active, Is.False);
            for (int i = 1; i <= 24; i++)
            {
                float t = i / 24f;
                Pointer(pointer, PointerEventType.Move, start + direction * distance * t +
                    side * (i == 24 ? 0 : jitter * Mathf.Sin(i * 1.7f)));
                yield return new WaitForSecondsRealtime(.015f);
                Assert.That(_window.IsDrawing, Is.True, "the selected native pointer must survive multiple controller/map frames");
                Assert.That(_drive.ExternalMotionBlocked, Is.True);
                Assert.That(_drive.LastCommand.control_active, Is.False);
                Assert.That(_robot.PathCount, Is.EqualTo(pathsBeforeDrawing), "drawing alone must never submit a path");
            }
            long beforeReleaseSequence = _drive.LastSentSequence;
            Pointer(pointer, PointerEventType.Unselect, start + direction * distance);
            Pointer(pointer, PointerEventType.Unhover, start + direction * distance);
            Assert.That(_window.IsDrawing, Is.False);
            Assert.That(_window.DrawMode, Is.False);
            if (distance > .1f)
                Assert.That(_window.PreparedWaypoints.Count, Is.EqualTo(2), "noisy straight input must remain one straight leg");
            else
            {
                Assert.That(_window.PreparedWaypoints.Count, Is.GreaterThanOrEqualTo(2));
                Assert.That(_window.PreparedWaypoints.Count, Is.LessThanOrEqualTo(20));
                foreach (Vector2 point in _window.PreparedWaypoints)
                    Assert.That(Vector2.Distance(point, start), Is.LessThanOrEqualTo(.101f),
                        "the arrival-boundary fixture must stay entirely inside its tested distance");
                Debug.Log("PUPPYPI_ARRIVAL_DRAFT distance=" + distance + " points=" +
                    string.Join(";", _window.PreparedWaypoints.Select(point => point.ToString("F5")).ToArray()));
            }
            yield return Until(() => !_drive.ExternalMotionBlocked && _drive.Session.NeutralConfirmed &&
                _drive.Session.LastAckKind == "command" && _drive.Session.LastAckSequence > beforeReleaseSequence,
                "release did not obtain a genuinely newer neutral heartbeat acknowledgement");
        }

        private Button RunButton => _window.WindowRoot.GetComponentsInChildren<Button>().Single(button => button.name == "RUN");

        private IEnumerator RunReviewed(float distance, int expectedPaths, int pointer = 7100)
        {
            yield return Draw(distance, pointer);
            yield return Until(() => RunButton.interactable, "the reviewed green route never enabled RUN");
            Assert.That(_window.NavigationVerified, Is.True, Diagnostic);
            Assert.That(_robot.PathCount, Is.EqualTo(expectedPaths - 1), "drawing/releasing alone must not submit motion");
            RunButton.onClick.Invoke();
            yield return Until(() => _robot.PathCount == expectedPaths, "RUN never produced an actual UDP path packet");
            yield return Until(() => _controller.PathClient.RemoteState == "drive" ||
                _controller.PathClient.RemoteState == "rotate", "HTTP did not confirm actual follower execution");
            Assert.That(_controller.PathClient.IsActive, Is.True);
        }

        [UnityTest]
        public IEnumerator ActualRobotSupervisedUnknownMapKeepsStationaryRunReadyAndCompletesPastOneSecond()
        {
            _robot = new LocalPathRobot(true, baseFreeProbability: -1);
            yield return Connect(supervised: true);
            yield return Draw(.7f, 7410);
            yield return Until(() => RunButton.interactable, "unknown source cells must not block a supervised draft");
            Assert.That(_controller.MapData.BaseOccupancy, Is.All.EqualTo(-1));
            Assert.That(_controller.MapData.MapFreeCellCount, Is.Zero,
                "the regression must not silently depend on a previously observed free source map");
            var map = _controller.MapData;
            Assert.That(MapDetourPlanner.TryFindRouteBlock(map.Geometry, map.Occupancy,
                new[] { Vector2.zero, new Vector2(.7f, 0) }, map.RobotPose.z,
                _controller.PathClient.Footprint, out var strictBlock), Is.True,
                "the physical scan's permanent missing sector must actually fail the old full-route preflight");
            Assert.That(strictBlock.Kind, Is.EqualTo(MapPathBlockKind.Unknown));
            double stableUntil = Time.realtimeSinceStartupAsDouble + 2.2;
            while (Time.realtimeSinceStartupAsDouble < stableUntil)
            {
                Assert.That(_robot.X, Is.EqualTo(0).Within(.001));
                Assert.That(_robot.Y, Is.EqualTo(0).Within(.001));
                Assert.That(RunButton.interactable, Is.True,
                    "supervised RUN must remain available while the stationary robot receives incomplete scans: " + Diagnostic);
                Assert.That(_robot.PathCount, Is.Zero);
                yield return null;
            }
            RunButton.onClick.Invoke();
            yield return Until(() => _robot.PathCount == 1 && _robot.X > .15,
                "the unknown-map request did not sustain actual reference-follower translation", 5);
            Assert.That(_controller.PathClient.IsActive, Is.True,
                "movement must remain active beyond the former one-second free-cache gate");
            Assert.That(_robot.CancelCount, Is.Zero);
            yield return Until(() => !_controller.PathClient.IsActive,
                "the supervised route stopped instead of completing across unknown source cells", 8);
            Assert.That(_controller.PathClient.Status, Does.StartWith("Path completed"), Diagnostic);
            Assert.That(_robot.X, Is.GreaterThan(.55));
            Assert.That(_robot.CancelCount, Is.Zero);
            Assert.That(_robot.PathCount, Is.EqualTo(1));
            Assert.That(_robot.ReferenceEmergencyCommands, Is.Zero);
            Assert.That(_robot.CollisionSamples, Is.Zero);
            Assert.That(_robot.NonNeutralPathHeartbeats, Is.Zero);
        }

        [UnityTest]
        public IEnumerator ActualRobotSupervisedMissingScanStartsAndRecoversWithoutAnotherRun()
        {
            _robot = new LocalPathRobot(true, baseFreeProbability: -1) { AllBeamsMissing = true };
            yield return Connect(navigationExpected: false, supervised: true);
            yield return Draw(.55f, 7411);
            Assert.That(_controller.MapData.NavigationLive, Is.False);
            yield return Until(() => RunButton.interactable,
                "fresh position and a released native draft must permit supervised RUN without a laser layer");
            RunButton.onClick.Invoke();
            yield return Until(() => _robot.PathCount == 1,
                "supervised RUN never reached the actual UDP follower while all scan returns were missing");
            yield return new WaitForSecondsRealtime(1.4f);
            Assert.That(_controller.PathClient.IsActive, Is.True,
                "missing obstacle observations must not cause an MR route cancellation: " + Diagnostic);
            Assert.That(_robot.CancelCount, Is.Zero);
            Assert.That(_robot.ReferenceEmergencyCommands, Is.Zero);
            // The real robot can retain its own sensor waiting policy. This
            // verifies MR authorization and continuation, not permission to
            // override a robot-side stale-sensor or emergency stop.
            _robot.AllBeamsMissing = false;
            yield return Until(() => _controller.MapData.NavigationLive,
                "restored scan/pose pairs did not enable current obstacle checks");
            yield return Until(() => !_controller.PathClient.IsActive,
                "the same supervised request failed after scan recovery", 8);
            Assert.That(_controller.PathClient.Status, Does.StartWith("Path completed"), Diagnostic);
            Assert.That(_robot.PathCount, Is.EqualTo(1), "scan recovery must not require or replay a second RUN");
            Assert.That(_robot.CancelCount, Is.Zero);
            Assert.That(_robot.ReferenceEmergencyCommands, Is.Zero);
        }

        [UnityTest]
        public IEnumerator ActualRobotSupervisedUnknownMapStillDetoursAndExplicitStopRevokesRestart()
        {
            _robot = new LocalPathRobot(true, baseFreeProbability: -1);
            yield return Connect(supervised: true);
            yield return RunReviewed(1.2f, 1, 7412);
            _robot.HoldCancelAck = true;
            _robot.AddObstacleAhead(.5, .07);
            yield return Until(() => _robot.CancelCount > 0,
                "a persistent confirmed obstacle must still stop the original supervised path", 5);
            int stoppedSnapshots = _robot.InactiveSnapshots;
            yield return Until(() => _robot.InactiveSnapshots > stoppedSnapshots,
                "the peer did not serve an inactive pre-ACK snapshot");
            Assert.That(_robot.PathCount, Is.EqualTo(1),
                "supervision must not bypass the accepted-stop barrier for automatic replacement");
            Assert.That(_robot.PostCancelAckSnapshots, Is.Zero);
            _robot.ReleaseCancelReceipts();
            yield return Until(() => _robot.PathCount == 2,
                "confirmed obstacles must produce a bypass through unobserved source space", 8);
            Assert.That(_robot.PostCancelAckSnapshots, Is.GreaterThan(0));
            Assert.That(_robot.LastPathPointCount, Is.GreaterThanOrEqualTo(3));
            Assert.That(_controller.PathClient.IsDetouring, Is.True);
            _window.Stop();
            yield return Until(() => !_controller.PathClient.IsActive,
                "explicit operator STOP failed during the supervised bypass", 4);
            yield return new WaitForSecondsRealtime(1.2f);
            Assert.That(_robot.PathCount, Is.EqualTo(2), "an explicit STOP must revoke automatic restart");
            Assert.That(_robot.ReferenceEmergencyCommands, Is.Zero);
            Assert.That(_robot.CollisionSamples, Is.Zero);
            Assert.That(_robot.NonNeutralPathHeartbeats, Is.Zero);
        }

        [UnityTest]
        public IEnumerator ActualRobotSupervisedSketchBeyondThirtyCentimetresUsesCurrentRobotAnchor()
        {
            _robot = new LocalPathRobot(true, baseFreeProbability: -1);
            yield return Connect(supervised: true);
            yield return Draw(.4f, 7413, new Vector2(.45f, 0));
            Assert.That(_window.PreparedWaypoints[0].x, Is.EqualTo(.45f).Within(.002));
            yield return Until(() => RunButton.interactable,
                "a valid supervised sketch must not require a first point within thirty centimetres");
            RunButton.onClick.Invoke();
            yield return Until(() => _robot.PathCount == 1, "the distant-start sketch never reached the real follower");
            double[][] sent = _robot.LastPathPoints;
            Assert.That(sent.Length, Is.EqualTo(3));
            Assert.That(sent[0][0], Is.EqualTo(0).Within(.003));
            Assert.That(sent[1][0], Is.EqualTo(.45).Within(.003));
            Assert.That(sent[2][0], Is.EqualTo(.85).Within(.003));
            yield return Until(() => !_controller.PathClient.IsActive,
                "the current-anchor connector and native sketch did not complete", 10);
            Assert.That(_controller.PathClient.Status, Does.StartWith("Path completed"), Diagnostic);
            Assert.That(_robot.X, Is.GreaterThan(.65));
            Assert.That(_robot.CancelCount, Is.Zero);
            Assert.That(_robot.ReferenceEmergencyCommands, Is.Zero);
            Assert.That(_robot.CollisionSamples, Is.Zero);
        }

        [UnityTest]
        public IEnumerator ActualRobotSupervisedStatusGapDoesNotLockRunOrCancelItsExecution()
        {
            _robot = new LocalPathRobot(true);
            yield return Connect(supervised: true);
            yield return Draw(1.1f, 7420);
            _robot.PauseCommandStatus = true;
            yield return new WaitForSecondsRealtime(2.8f);
            Assert.That(_controller.RosBridge.RobotStatusFresh, Is.False);
            Assert.That(_controller.RosBridge.ArbiterStateFresh, Is.False);
            Assert.That(_controller.RosBridge.IsConnected, Is.True);
            Assert.That(RunButton.interactable, Is.True,
                "missing advisory status must not disable a valid supervised route: " + Diagnostic);
            Assert.That(_controller.CanExecuteMotion, Is.True,
                "the same advisory status gap must not disable all dashboard actions");
            RunButton.onClick.Invoke();
            yield return Until(() => _robot.PathCount == 1 && _robot.X > .15,
                "RUN did not move the actual reference follower during a status-only gap", 5);
            Assert.That(_controller.PathClient.IsActive, Is.True, Diagnostic);
            Assert.That(_robot.CancelCount, Is.Zero);
            yield return Until(() => !_controller.PathClient.IsActive,
                "the route did not complete while auxiliary status stayed absent", 9);
            Assert.That(_controller.PathClient.Status, Does.StartWith("Path completed"), Diagnostic);
            Assert.That(_robot.X, Is.GreaterThan(.9));
            Assert.That(_robot.ReferenceEmergencyDatagrams, Is.Zero);
            Assert.That(_drive.Session.Emergency, Is.False);
            _robot.PauseCommandStatus = false;
        }

        [UnityTest]
        public IEnumerator ActualRobotSupervisedViewingMapDoesNotCancelTheRunningRoute()
        {
            _robot = new LocalPathRobot(true);
            yield return Connect(supervised: true);
            yield return RunReviewed(1.1f, 1, 7421);
            yield return Until(() => _robot.X > .1, "the reference follower never began translation");
            _window.SetPresentationVisible(false);
            yield return new WaitForSecondsRealtime(.3f);
            Assert.That(_controller.PathClient.IsActive, Is.True, Diagnostic);
            _window.ShowAndRecenter();
            _window.SetPlacementLocked(true);
            yield return new WaitForSecondsRealtime(.3f);
            _window.SetPlacementLocked(false);
            Assert.That(_robot.CancelCount, Is.Zero,
                "viewing and repositioning the map must not cancel its requested path");
            yield return Until(() => !_controller.PathClient.IsActive,
                "the path did not finish after the map view changed", 9);
            Assert.That(_controller.PathClient.Status, Does.StartWith("Path completed"), Diagnostic);
            Assert.That(_robot.ReferenceEmergencyDatagrams, Is.Zero);
        }

        [UnityTest]
        public IEnumerator ActualRobotSupervisedUnsentPauseDoesNotLatchStopAndTheDraftCanRunAgain()
        {
            _robot = new LocalPathRobot(true);
            yield return Connect(supervised: true);
            yield return Draw(.6f, 7422);
            RunButton.onClick.Invoke();
            Assert.That(_controller.PathClient.IsActive, Is.True);
            Assert.That(_controller.PathClient.DiagnosticSummary, Does.Contain("sent=False sequence=0"));
            _controller.PathClient.SendMessage("OnApplicationPause", true);
            Assert.That(_controller.PathClient.IsActive, Is.False,
                "a genuinely unsent request should be abandoned locally");
            Assert.That(_drive.Session.Emergency, Is.False);
            _controller.PathClient.SendMessage("OnApplicationPause", false);
            yield return new WaitForSecondsRealtime(1.7f);
            Assert.That(_robot.ReferencePathDatagrams, Is.Zero);
            Assert.That(_robot.ReferenceEmergencyDatagrams, Is.Zero);
            yield return Until(() => RunButton.interactable, "the retained draft stayed locked after resume");
            RunButton.onClick.Invoke();
            yield return Until(() => _robot.PathCount == 1, "the same retained draft could not start again");
            yield return Until(() => !_controller.PathClient.IsActive, "the retried draft did not complete", 8);
            Assert.That(_controller.PathClient.Status, Does.StartWith("Path completed"), Diagnostic);
            Assert.That(_robot.ReferenceEmergencyDatagrams, Is.Zero);
        }

        [UnityTest]
        public IEnumerator ActualRobotSupervisedLostCancelReceiptDoesNotCreateGlobalStopOrReplay()
        {
            _robot = new LocalPathRobot(true);
            yield return Connect(supervised: true);
            yield return RunReviewed(1.1f, 1, 7423);
            _robot.HoldCancelAck = true;
            _robot.ApplyReferenceSettings();
            _window.Stop();
            yield return Until(() => _robot.CancelCount > 0, "the explicit path STOP never reached the follower");
            yield return new WaitForSecondsRealtime(1.8f);
            Assert.That(_drive.Session.Emergency, Is.False,
                "a late cancellation receipt must not latch the whole robot emergency stop");
            Assert.That(_robot.ReferenceEmergencyDatagrams, Is.Zero);
            Assert.That(_controller.PathClient.IsActive, Is.True,
                "an uncertain remote operation must retain its cancellation barrier");
            Assert.That(_drive.PathHeartbeatSuspended, Is.True);
            Assert.That(_robot.PathCount, Is.EqualTo(1));
            _robot.ReleaseCancelReceipts();
            yield return Until(() => !_controller.PathClient.IsActive,
                "fresh stop confirmation failed to release the cancellation barrier", 5);
            Assert.That(_drive.PathHeartbeatSuspended, Is.False);
            Assert.That(_robot.PathCount, Is.EqualTo(1), "the canceled path must never restart itself");
            yield return RunReviewed(.5f, 2, 7424);
            yield return Until(() => !_controller.PathClient.IsActive,
                "a new route could not complete after the delayed stop receipt", 8);
            Assert.That(_controller.PathClient.Status, Does.StartWith("Path completed"), Diagnostic);
            Assert.That(_robot.ReferenceEmergencyDatagrams, Is.Zero);
        }

        [UnityTest]
        public IEnumerator ActualRobotSupervisedSlowPathHttpDoesNotStopAnExecutingRoute()
        {
            _robot = new LocalPathRobot(true);
            yield return Connect(supervised: true);
            yield return RunReviewed(1.2f, 1, 7425);
            _robot.PathStatusResponseDelayMilliseconds = 1800;
            double before = _robot.X;
            yield return new WaitForSecondsRealtime(2.1f);
            Assert.That(_controller.PathClient.IsActive, Is.True, Diagnostic);
            Assert.That(_robot.X, Is.GreaterThan(before + .15),
                "the actual follower must continue while only HTTP status replies time out");
            Assert.That(_robot.CancelCount, Is.Zero);
            Assert.That(_drive.Session.Emergency, Is.False);
            _robot.PathStatusResponseDelayMilliseconds = 0;
            yield return Until(() => !_controller.PathClient.IsActive,
                "execution did not reconcile after HTTP replies recovered", 9);
            Assert.That(_controller.PathClient.Status, Does.StartWith("Path completed"), Diagnostic);
            Assert.That(_robot.ReferenceEmergencyDatagrams, Is.Zero);
        }

        [UnityTest]
        public IEnumerator ActualRobotSupervisedExplicitStopStaysVisibleAcrossAckLoss()
        {
            _robot = new LocalPathRobot(true);
            yield return Connect(supervised: true);
            _controller.UdpStop();
            yield return Until(() => _drive.Session.Emergency && _robot.ReferenceEmergencyCommands > 0,
                "the explicit operator STOP did not latch the real reference session");
            Assert.That(_controller.StatusText, Does.Contain("VR STOP"));
            _robot.HeartbeatBlackoutSeconds = .65f;
            yield return Until(() => !_drive.Session.Connected,
                "the test did not actually interrupt heartbeat acknowledgements", 2);
            Assert.That(_controller.StatusText, Does.Contain("VR STOP"),
                "loss of connectivity must not hide an already latched explicit stop");
            Assert.That(_controller.CanExecuteMotion, Is.False);
            yield return Until(() => _drive.Session.Connected, "the reference link did not recover", 3);
            Assert.That(_drive.Session.Emergency, Is.True,
                "normal link recovery must never clear an explicit operator stop");
            Assert.That(_robot.PathCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator ActualRobotSupervisedExplicitEmergencyCanBeClearedAfterThePathStops()
        {
            _robot = new LocalPathRobot(true);
            yield return Connect(supervised: true);
            yield return RunReviewed(1.1f, 1, 7426);
            // This is the same explicit STOP entry used by the controller B
            // shortcut: latch first, so later path_cancel packets are rejected
            // by the actual robot emergency state rather than accepted early.
            _drive.EmergencyStop();
            yield return Until(() => _robot.ReferenceEmergencyCommands > 0,
                "the explicit emergency packet did not reach the reference robot");
            yield return Until(() => !_controller.PathClient.IsActive,
                "a confirmed emergency stop left the path hold permanently blocking CLEAR", 5);
            Assert.That(_robot.CancelCount, Is.Zero,
                "the regression must confirm stop through emergency-rejected cancellation receipts");
            Assert.That(_drive.Session.Emergency, Is.True);
            Assert.That(_drive.PathCommandHeld, Is.False);
            yield return Until(() => _controller.CanClearStop, "explicit CLEAR remained locked after a confirmed stop");
            _controller.ClearUdpStop();
            yield return Until(() => !_drive.Session.Emergency && _drive.Session.NeutralConfirmed,
                "the explicit CLEAR did not recover the session");
            yield return new WaitForSecondsRealtime(.3f);
            Assert.That(_robot.PathCount, Is.EqualTo(1), "CLEAR must never replay the canceled path");
            yield return RunReviewed(.5f, 2, 7427);
            yield return Until(() => !_controller.PathClient.IsActive,
                "a new user route could not complete after an explicit emergency and clear", 8);
            Assert.That(_controller.PathClient.Status, Does.StartWith("Path completed"), Diagnostic);
        }

        [UnityTest]
        public IEnumerator ActualRobotSupervisedDroppedCancelPacketsRecoverAfterTheCommandWatchdog()
        {
            _robot = new LocalPathRobot(true);
            yield return Connect(supervised: true);
            yield return RunReviewed(1.2f, 1, 7428);
            bool neutralRequiredReceipt = false;
            _drive.PathAckObserved += (ack, kind) =>
            {
                if (kind == "path_cancel" && !ack.accepted && ack.reason == "neutral_required")
                    neutralRequiredReceipt = true;
            };
            _robot.DropPathCancelCount = 10;
            _robot.ApplyReferenceSettings();
            _window.Stop();
            yield return Until(() => _robot.ReferenceDroppedCancels == 10,
                "the cancellation packet-loss injector did not consume all ten packets", 6);
            yield return Until(() => !_controller.PathClient.IsActive,
                "watchdog stop plus a fresh neutral-required receipt left a permanent path hold", 6);
            Assert.That(neutralRequiredReceipt, Is.True,
                "recovery must exercise the actual post-watchdog rejection rather than a cached accepted receipt");
            Assert.That(_drive.PathCommandHeld, Is.False);
            Assert.That(_drive.PathHeartbeatSuspended, Is.False);
            Assert.That(_drive.Session.Emergency, Is.False);
            Assert.That(_robot.ReferenceEmergencyDatagrams, Is.Zero);
            Assert.That(_robot.PathCount, Is.EqualTo(1));
            yield return RunReviewed(.5f, 2, 7429);
            yield return Until(() => !_controller.PathClient.IsActive,
                "a new path could not complete after a real watchdog cancellation", 8);
            Assert.That(_controller.PathClient.Status, Does.StartWith("Path completed"), Diagnostic);
        }

        [UnityTest]
        public IEnumerator ActualRobotUnsentRunInvalidatedBeforeFirstSendAbortsLocallyWithoutRemoteStop()
        {
            _robot = new LocalPathRobot(true);
            yield return Connect();
            yield return Draw(.55f, 7340);
            yield return Until(() => RunButton.interactable, "native draft never became ready for the unsent race");
            string replacement = LocalPathRobot.GridJson(-1);
            var before = _controller.MapData.Geometry;
            bool acceptedBeforeInvalidation = false, replacementApplied = false;
            long neutralSequence = -1;
            int ingressAtInvalidation = -1;
            var earlyClick = _root.AddComponent<PathRunBeforeLateUpdate>();
            earlyClick.Map = _controller.MapData;
            earlyClick.Window = _window;
            earlyClick.AfterRevision = _controller.MapData.Revision;
            earlyClick.Click = () =>
            {
                _window.Run();
                acceptedBeforeInvalidation = _controller.PathClient.IsActive &&
                    _controller.PathClient.DiagnosticSummary.Contains("phase=Sending sent=False sequence=0") &&
                    !_drive.Session.NeutralConfirmed;
                neutralSequence = _drive.LastSentSequence;
                ingressAtInvalidation = _robot.ReferencePathDatagrams;
                // Real data ingestion withdraws the source-map permission in
                // the persistent absent-ray sector before the next client tick.
                replacementApplied = _controller.MapData.ApplyGrid(replacement);
            };
            earlyClick.Armed = true;
            yield return Until(() => earlyClick.Invoked, "no actual map update reached the scheduled native RUN callback");
            Assert.That(acceptedBeforeInvalidation, Is.True,
                "the regression must accept RUN into unsent Sending and require a new neutral ACK");
            Assert.That(ingressAtInvalidation, Is.Zero, "invalidation must precede the first UDP path datagram");
            Assert.That(replacementApplied, Is.True);
            Assert.That(_controller.MapData.Geometry.width, Is.EqualTo(before.width));
            Assert.That(_controller.MapData.Geometry.height, Is.EqualTo(before.height));
            Assert.That(_controller.MapData.Geometry.origin, Is.EqualTo(before.origin));
            Assert.That(_controller.MapData.Geometry.resolution, Is.EqualTo(before.resolution));
            Assert.That(_controller.MapData.BaseOccupancy, Is.All.EqualTo(-1));
            yield return Until(() => !_controller.PathClient.IsActive,
                "the invalidated unsent operation did not abort locally", 3);
            // Exceed the previous 1.5-second stop-confirmation deadline while
            // keeping real socket heartbeats and sensor frames running.
            yield return new WaitForSecondsRealtime(1.8f);
            yield return Until(() => _drive.Session.NeutralConfirmed &&
                _drive.Session.LastAckKind == "command" && _drive.Session.LastAckSequence > neutralSequence,
                "local abort did not obtain a genuinely newer neutral heartbeat acknowledgement");
            Assert.That(_controller.PathClient.Status, Does.StartWith("Route not sent:"), Diagnostic);
            Assert.That(_controller.PathClient.IsActive, Is.False);
            Assert.That(_drive.PathCommandHeld, Is.False);
            Assert.That(_drive.Session.Emergency, Is.False);
            Assert.That(_drive.Session.Armed, Is.False);
            Assert.That(_drive.LastCommand.control_active, Is.False);
            Assert.That(_robot.PathCount, Is.Zero);
            Assert.That(_robot.CancelCount, Is.Zero);
            Assert.That(_robot.ReferenceEmergencyCommands, Is.Zero);
            Assert.That(_robot.ReferencePathDatagrams, Is.Zero, "no path packet may leave a locally aborted unsent operation");
            Assert.That(_robot.ReferenceCancelDatagrams, Is.Zero, "an unsent local operation has no remote path to cancel");
            Assert.That(_robot.ReferenceEmergencyDatagrams, Is.Zero, "no stop timeout may escalate an unsent operation");
            Assert.That(_robot.X, Is.EqualTo(0).Within(.001));
            Assert.That(_robot.Y, Is.EqualTo(0).Within(.001));
        }

        [UnityTest]
        public IEnumerator ActualRobotGradedFreeMapAndReachedStartJitterRunDirectlyPastARearObstacle()
        {
            _robot = new LocalPathRobot(true, baseFreeProbability: 11);
            yield return Connect();
            _robot.AddObstacleAhead(-.4, .03);
            yield return Draw(.55f, 7341);
            yield return Until(() => _controller.MapData.ConfirmedObstacleCellCount > 0 && RunButton.interactable,
                "the real rear-obstacle scans did not leave the forward draft runnable");
            Assert.That(_controller.MapData.BaseOccupancy, Is.All.EqualTo(11));
            double stationaryUntil = Time.realtimeSinceStartupAsDouble + 2.2;
            while (Time.realtimeSinceStartupAsDouble < stationaryUntil)
            {
                Assert.That(_robot.X, Is.EqualTo(0).Within(.001));
                Assert.That(_robot.Y, Is.EqualTo(0).Within(.001));
                Assert.That(RunButton.interactable && _window.NavigationVerified, Is.True,
                    "a physically fixed robot must keep RUN ready beyond the one-second free-cache window: " + Diagnostic);
                Assert.That(_robot.PathCount, Is.Zero);
                yield return null;
            }
            Assert.That(_controller.MapData.IsMapKnownFree(new Vector2(.725f, -.075f)), Is.True,
                "the actual absent-ray sector must use graded mapper-free permission");
            Vector2 requestedStart = _window.PreparedWaypoints[0];
            Vector2 requestedGoal = _window.PreparedWaypoints.Last();
            Assert.That(Vector2.Distance(requestedStart, Vector2.zero), Is.LessThan(.001f));
            Assert.That(Vector2.Distance(requestedGoal, new Vector2(.55f, 0)), Is.LessThan(.002f));
            // The test backend accepts pose resets only while its real follower
            // is inactive. Both ROS pose and scan then report the changed plant.
            _robot.ResetReferencePoseX = .03;
            _robot.ResetReferencePose = true;
            yield return Until(() => Math.Abs(_robot.X - .03) < .001 &&
                Math.Abs(_controller.MapData.RobotPose.x - .03f) < .001 && _controller.MapData.NavigationLive,
                "the inactive reference plant and timestamp-paired map pose did not reflect the 3 cm drift");
            yield return null;
            yield return Until(() => RunButton.interactable && _window.NavigationVerified,
                "already reached sketch start incorrectly blocked the forward route after pose drift");
            Assert.That(_window.PreparedWaypoints[0], Is.EqualTo(requestedStart), "native sketch intent remains unchanged");
            Assert.That(_window.PreparedWaypoints.Last(), Is.EqualTo(requestedGoal));
            Assert.That(_window.BypassPreviewReady, Is.False, "an obstacle behind a reached start must not require a bypass");
            Assert.That(_robot.PathCount, Is.Zero);
            RunButton.onClick.Invoke();
            yield return Until(() => _robot.PathCount == 1, "the direct current-anchor route never reached the real UDP follower");
            double[][] sent = _robot.LastPathPoints;
            Assert.That(sent.Length, Is.EqualTo(2), "the wire request must contain only current anchor and original goal");
            Assert.That(sent[0][0], Is.EqualTo(.03).Within(.003));
            Assert.That(sent[0][1], Is.EqualTo(0).Within(.003));
            Assert.That(sent[1][0], Is.EqualTo(requestedGoal.x).Within(.003));
            Assert.That(sent[1][1], Is.EqualTo(requestedGoal.y).Within(.003));
            Assert.That(_controller.PathClient.IsDetouring, Is.False);
            yield return Until(() => !_controller.PathClient.IsActive,
                "the graded-free forward route failed to complete after the reached start was normalized", 8);
            Assert.That(_controller.PathClient.Status, Does.StartWith("Path completed"), Diagnostic);
            Assert.That(_robot.State, Is.EqualTo("done"));
            Assert.That(Vector2.Distance(new Vector2((float)_robot.X, (float)_robot.Y), requestedGoal),
                Is.LessThanOrEqualTo(.11f));
            Assert.That(Math.Abs(_robot.Y), Is.LessThan(.01));
            Assert.That(Math.Abs(_robot.Yaw), Is.LessThan(.03), "the route must never turn back toward its consumed start");
            Assert.That(_robot.PathCount, Is.EqualTo(1));
            Assert.That(_robot.ReferencePathDatagrams, Is.EqualTo(1));
            Assert.That(_robot.CancelCount, Is.Zero);
            Assert.That(_robot.ReferenceCancelDatagrams, Is.Zero);
            Assert.That(_robot.ReferenceEmergencyCommands, Is.Zero);
            Assert.That(_robot.ReferenceEmergencyDatagrams, Is.Zero);
            Assert.That(_drive.Session.Emergency, Is.False);
            Assert.That(_robot.CollisionSamples, Is.Zero);
            Assert.That(_robot.NonNeutralPathHeartbeats, Is.Zero);
        }

        [UnityTest]
        public IEnumerator ActualRobotHardFrequentNearGhostsWithSlowStatusAndHeartbeatLossStillFinish()
        {
            _robot = new LocalPathRobot(true)
            {
                PeriodicGhosts = true, GhostPeriodSeconds = .3f, GhostDurationSeconds = .1f,
                GhostRangeMeters = .2f, HeartbeatDropEvery = 3,
                PathStatusResponseDelayMilliseconds = 500, PathAckDelayMilliseconds = 1000
            };
            yield return Connect();
            yield return RunReviewed(.7f, 1, 7300);
            yield return Until(() => !_controller.PathClient.IsActive, "combined near ghosts and transport degradation stalled", 10);
            Assert.That(_controller.PathClient.Status, Does.StartWith("Path completed"), Diagnostic);
            yield return RunReviewed(.4f, 2, 7301);
            yield return Until(() => !_controller.PathClient.IsActive, "second degraded run stalled", 8);
            Assert.That(_controller.PathClient.Status, Does.StartWith("Path completed"), Diagnostic);
            Assert.That(_robot.ReferenceGhostScans, Is.GreaterThanOrEqualTo(8));
            Assert.That(_robot.ReferenceDroppedHeartbeats, Is.GreaterThan(10));
            Assert.That(_robot.CancelCount, Is.Zero);
            Assert.That(_robot.ReferenceEmergencyCommands, Is.Zero);
        }

        [UnityTest]
        public IEnumerator ActualRobotHardLostCancelAndLateReceiptDoNotCauseEmergency()
        {
            _robot = new LocalPathRobot(true);
            yield return Connect();
            yield return RunReviewed(1.1f, 1, 7310);
            _robot.DropPathCancelCount = 1;
            _robot.PathCancelAckDelayMilliseconds = 350;
            _robot.AddObstacleAhead(.50);
            yield return Until(() => _robot.PathCount == 2 || _robot.ReferenceEmergencyCommands > 0 ||
                !_controller.PathClient.IsActive, "cancel loss/delay did not resolve", 7);
            Assert.That(_robot.ReferenceDroppedCancels, Is.EqualTo(1));
            Assert.That(_robot.ReferenceEmergencyCommands, Is.Zero,
                "a 350 ms operation receipt with fresh heartbeats must not require emergency stop");
            Assert.That(_drive.Session.Emergency, Is.False, "the local emergency latch must remain clear");
            Assert.That(_controller.PathClient.Status, Does.Not.Contain("emergency stop requested"));
            Assert.That(_robot.PathCount, Is.EqualTo(2), Diagnostic);
            Assert.That(_robot.PostCancelAckSnapshots, Is.GreaterThan(0));
            _window.Stop();
            yield return Until(() => !_controller.PathClient.IsActive, "explicit STOP did not finish", 4);
            Assert.That(_robot.ReferenceEmergencyCommands, Is.Zero);
            Assert.That(_robot.CollisionSamples, Is.Zero);
        }

        [UnityTest]
        public IEnumerator ActualRobotHardMissingRaysDuringMotionCancelWithoutEmergencyAndNeedNewRun()
        {
            _robot = new LocalPathRobot(true);
            yield return Connect();
            yield return RunReviewed(1.1f, 1, 7320);
            double before = _robot.X;
            _robot.AllBeamsMissing = true;
            yield return Until(() => !_controller.PathClient.IsActive, "all-NaN input failed to cancel", 4);
            Assert.That(_robot.CancelCount, Is.GreaterThan(0));
            Assert.That(_robot.X - before, Is.LessThan(.07));
            Assert.That(_robot.ReferenceEmergencyCommands, Is.Zero);
            _robot.AllBeamsMissing = false;
            yield return Until(() => _controller.MapData.NavigationLive, "sensor recovery did not restore editing readiness");
            yield return new WaitForSecondsRealtime(.6f);
            Assert.That(_robot.PathCount, Is.EqualTo(1), "recovery must not silently resume a cancelled path");
            yield return RunReviewed(.4f, 2, 7321);
            yield return Until(() => !_controller.PathClient.IsActive, "explicit run after NaN recovery failed", 7);
            Assert.That(_controller.PathClient.Status, Does.StartWith("Path completed"), Diagnostic);
            Assert.That(_robot.ReferenceEmergencyCommands, Is.Zero);
        }

        [UnityTest]
        public IEnumerator ActualRobotHardActualSensorPauseStopsThenRecoversWithoutEmergency()
        {
            _robot = new LocalPathRobot(true);
            yield return Connect();
            yield return RunReviewed(1.1f, 1, 7330);
            double before = _robot.X;
            _robot.PauseSensors = true;
            yield return Until(() => !_controller.PathClient.IsActive, "actual sensor pause failed to cancel", 5);
            Assert.That(_robot.X - before, Is.LessThan(.11), "existing 0.6 s robot telemetry timeout must stop output");
            Assert.That(_robot.ReferenceEmergencyCommands, Is.Zero);
            _robot.PauseSensors = false;
            yield return Until(() => _controller.MapData.NavigationLive, "actual sensor input did not recover");
            yield return new WaitForSecondsRealtime(.6f);
            Assert.That(_robot.PathCount, Is.EqualTo(1));
            yield return RunReviewed(.4f, 2, 7331);
            yield return Until(() => !_controller.PathClient.IsActive, "new run after actual sensor pause failed", 7);
            Assert.That(_controller.PathClient.Status, Does.StartWith("Path completed"), Diagnostic);
            Assert.That(_robot.ReferenceEmergencyCommands, Is.Zero);
        }

        [UnityTest]
        public IEnumerator ActualRobotHardCloseObstacleStopsBeforeBodyContact()
        {
            yield return CloseObstacleStopsBeforeBodyContact(0);
        }

        [UnityTest]
        public IEnumerator ActualRobotHardCloseObstacleStopsBeforeBodyContactAtLaterSensorPhase()
        {
            yield return CloseObstacleStopsBeforeBodyContact(.035f);
        }

        [UnityTest]
        public IEnumerator ActualRobotHardCloseObstacleStopsBeforeBodyContactNearSensorBoundary()
        {
            yield return CloseObstacleStopsBeforeBodyContact(.075f);
        }

        private IEnumerator CloseObstacleStopsBeforeBodyContact(float insertionDelay)
        {
            _robot = new LocalPathRobot(true);
            yield return Connect();
            yield return RunReviewed(1.1f, 1, 7340);
            yield return Until(() => _robot.X > .06, "robot did not begin actual translation");
            if (insertionDelay > 0) yield return new WaitForSecondsRealtime(insertionDelay);
            _robot.AddObstacleAhead(.30, .07);
            yield return Until(() => _robot.CancelCount > 0 || _robot.CollisionSamples > 0,
                "close obstruction did not produce a measurable reaction", 5);
            yield return new WaitForSecondsRealtime(.2f);
            Assert.That(_robot.CollisionSamples, Is.Zero,
                "a 30 cm sudden obstacle contacted the passive walking footprint before MR stopped");
            Assert.That(_robot.ReferenceEmergencyCommands, Is.Zero);
            _window.Stop();
            yield return Until(() => !_controller.PathClient.IsActive, "close-obstacle test did not end", 4);
        }

        [UnityTest]
        public IEnumerator ActualRobotHardSecondObstacleDuringBypassStillReachesOriginalGoal()
        {
            yield return StartTwoObstacleDetours(7350);
            yield return Until(() => !_controller.PathClient.IsActive, "two-obstacle bypass did not finish", 130);
            Assert.That(_controller.PathClient.Status, Does.StartWith("Path completed"), Diagnostic);
            Assert.That(Vector2.Distance(new Vector2((float)_robot.X, (float)_robot.Y), _twoObstacleGoal), Is.LessThanOrEqualTo(.15f));
            Assert.That(_robot.CollisionSamples, Is.Zero);
            Assert.That(_robot.ReferenceEmergencyCommands, Is.Zero);
        }

        [UnityTest]
        public IEnumerator ActualRobotHardSecondObstacleReplacementRepeatB()
        {
            yield return StartTwoObstacleDetours(7360);
            _window.Stop();
            yield return Until(() => !_controller.PathClient.IsActive, "STOP after repeated second bypass failed", 4);
            Assert.That(_robot.CollisionSamples, Is.Zero);
            Assert.That(_robot.ReferenceEmergencyCommands, Is.Zero);
        }

        [UnityTest]
        public IEnumerator ActualRobotHardSecondObstacleReplacementRepeatC()
        {
            yield return StartTwoObstacleDetours(7370);
            _window.Stop();
            yield return Until(() => !_controller.PathClient.IsActive, "STOP after third second-bypass attempt failed", 4);
            Assert.That(_robot.CollisionSamples, Is.Zero);
            Assert.That(_robot.ReferenceEmergencyCommands, Is.Zero);
        }

        private IEnumerator StartTwoObstacleDetours(int pointer)
        {
            _robot = new LocalPathRobot(true);
            yield return Connect();
            _twoObstacleGoal = new Vector2((float)_robot.X + 1.2f, (float)_robot.Y);
            yield return RunReviewed(1.2f, 1, pointer);
            _robot.AddObstacleAhead(.50, .07);
            yield return Until(() => _robot.PathCount == 2, "first bypass was not started", 8);
            yield return Until(() => _robot.State == "drive", "first bypass did not finish its initial turn", 25);
            _robot.AddSecondObstacleAhead(.50, .07);
            bool captured = false;
            yield return Until(() =>
            {
                if (!captured && _controller.PathClient.Status.Contains("Route observation is incomplete"))
                {
                    captured = true;
                    LogMixedRouteEvidence();
                }
                return _robot.PathCount == 3 || !_controller.PathClient.IsActive;
            },
                "second obstacle did not trigger another bounded bypass", 8);
            Assert.That(_robot.PathCount, Is.EqualTo(3), Diagnostic);
        }

        private void LogMixedRouteEvidence()
        {
            var map = _controller.MapData;
            if (map?.Geometry == null) return;
            var remaining = _controller.PathClient.RemainingPoints.ToArray();
            if (remaining.Length == 0)
            {
                int index = PuppyPiPathStatus.TryParse(_robot.PathReports.LastOrDefault(), out var report) ? report.Index : 0;
                remaining = _controller.PathClient.Points.Skip(index).ToArray();
            }
            var ahead = new[] { new Vector2(map.RobotPose.x, map.RobotPose.y) }.Concat(remaining).ToArray();
            bool blocked = MapDetourPlanner.TryFindRouteBlock(map.Geometry, map.Occupancy, ahead,
                map.RobotPose.z, _controller.PathClient.Footprint, out var first);
            bool confirmed = map.TryFindConfirmedRouteObstacle(ahead, map.RobotPose.z,
                _controller.PathClient.Footprint, out var obstacle);
            Debug.Log($"PUPPYPI_MIXED_ROUTE case={TestContext.CurrentContext.Test.Name} blocked={blocked} " +
                $"first={first?.Kind}/{first?.Position} confirmed={confirmed}/{obstacle?.Position} " +
                $"remaining={remaining.Length} {map.DiagnosticSummary}");
        }

        [UnityTest]
        public IEnumerator ActualRobotFollowerIgnoresPeriodicGhostsAcrossTwoNativeRuns()
        {
            _robot = new LocalPathRobot(true) { PeriodicGhosts = true };
            yield return Connect();
            yield return RunReviewed(.55f, 1);
            yield return Until(() => !_controller.PathClient.IsActive, "real follower stalled on periodic ghost pairs", 8);
            Assert.That(_controller.PathClient.Status, Does.StartWith("Path completed"), Diagnostic);
            double firstEnd = _robot.X;
            int firstGhosts = _robot.ReferenceGhostScans;
            Assert.That(firstGhosts, Is.GreaterThanOrEqualTo(3));
            yield return RunReviewed(.4f, 2, 7201);
            yield return Until(() => !_controller.PathClient.IsActive, "second actual follower run stalled on periodic ghosts", 7);
            Assert.That(_controller.PathClient.Status, Does.StartWith("Path completed"), Diagnostic);
            Assert.That(_robot.X - firstEnd, Is.GreaterThan(.2));
            Assert.That(_robot.ReferenceGhostScans, Is.GreaterThan(firstGhosts));
            Assert.That(_robot.ReferenceBlockedCount, Is.Zero, "unconfirmed ghost pairs must not trigger robot braking");
            Assert.That(_robot.CancelCount, Is.Zero);
            Assert.That(_robot.PathCount, Is.EqualTo(2));
            Assert.That(_robot.NonNeutralPathHeartbeats, Is.Zero);
        }

        [UnityTest]
        public IEnumerator ActualRobotFastDoneBeforeFirstPollCompletesWithoutPathAck()
        {
            _robot = new LocalPathRobot(true) { PathStatusResponseDelayMilliseconds = 600, DropNextPathAck = true };
            yield return Connect();
            yield return Draw(.12f, 7210);
            yield return Until(() => RunButton.interactable, "short observed route did not enable RUN");
            RunButton.onClick.Invoke();
            yield return Until(() => _robot.PathCount == 1, "short path was not received");
            yield return Until(() => !_controller.PathClient.IsActive, "identified fast done was not accepted", 4);
            Assert.That(_controller.PathClient.Status, Does.StartWith("Path completed"), Diagnostic);
            Assert.That(_robot.ActivePathSnapshots, Is.Zero, "the regression must miss every active HTTP snapshot");
            Assert.That(_pathReceipts, Is.Zero, "completion must be proven by actual execution identity despite a lost ACK");
            Assert.That(_robot.CancelCount, Is.Zero);
            Assert.That(_robot.X, Is.InRange(.015, .04), "actual follower uses its 10 cm arrival radius");
        }

        [UnityTest]
        public IEnumerator ActualRobotOldIdenticalDoneCannotCompleteTheNextRequestBeforeCallback()
        {
            _robot = new LocalPathRobot(true) { PathStatusResponseDelayMilliseconds = 300 };
            yield return Connect();
            yield return Draw(.12f, 7220);
            yield return Until(() => RunButton.interactable, "first short route did not enable RUN");
            RunButton.onClick.Invoke();
            yield return Until(() => _robot.PathCount == 1 && !_controller.PathClient.IsActive,
                "first identified route did not complete", 4);
            Assert.That(_controller.PathClient.Status, Does.StartWith("Path completed"), Diagnostic);
            string oldReport = _robot.PathReports.Last();
            Assert.That(PuppyPiPathStatus.TryParse(oldReport, out var old), Is.True);
            Assert.That(old.HasRequestIdentity, Is.True);
            long oldSequence = old.RequestSequence.Value;
            _robot.ResetReferencePose = true;
            yield return Until(() => Math.Abs(_robot.X) < .001 && Math.Abs(_controller.MapData.RobotPose.x) < .001,
                "inactive ideal plant did not reset for an identical-route replay");
            _robot.PathStatusResponseDelayMilliseconds = 0;
            _robot.CallbackDelayMilliseconds = 200;
            yield return Draw(.12f, 7221);
            yield return Until(() => RunButton.interactable, "identical second route did not enable RUN");
            int reportsBefore = _robot.PathReports.Length;
            RunButton.onClick.Invoke();
            yield return Until(() => _pathReceipts >= 2, "second request ACK was not received");
            Assert.That(_controller.PathClient.IsActive, Is.True,
                "an accepted ACK and old identical done must not complete the new execution");
            yield return Until(() => _robot.PathCount == 2 && !_controller.PathClient.IsActive,
                "second actual execution did not complete", 4);
            Assert.That(_controller.PathClient.Status, Does.StartWith("Path completed"), Diagnostic);
            var reports = _robot.PathReports.Skip(reportsBefore).Select(json =>
                PuppyPiPathStatus.TryParse(json, out var status) ? status : null).Where(status => status != null).ToArray();
            Assert.That(reports.Any(status => status.State == "done" && status.RequestSequence == oldSequence), Is.True,
                "the integration must actually serve the stale identical done before callback commitment");
            Assert.That(reports.Any(status => status.State == "done" && status.RequestSequence > oldSequence), Is.True);
            Assert.That(_robot.CancelCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator NativeDrawRunCompletionAndSecondRunSurviveLateAndMissingPathReceipts()
        {
            _robot = new LocalPathRobot { PathAckDelayMilliseconds = 80 };
            yield return Connect();
            Assert.That(_controller.MapData.MapFreeCellCount, Is.GreaterThan(0), "the persistent front missing-beam sector must use the selected known-free policy");
            string uuid = _drive.Session.ClientId;
            yield return RunReviewed(.55f, 1);
            yield return Until(() => !_controller.PathClient.IsActive, "the first route never completed", 8);
            Assert.That(_controller.PathClient.Status, Does.StartWith("Path completed"), Diagnostic);
            Assert.That(_robot.X, Is.GreaterThan(.35));
            Assert.That(_pathReceipts, Is.EqualTo(1), "the delayed path ACK must survive newer heartbeat ACKs");
            int heartbeatCount = _robot.HeartbeatCount;
            double firstEnd = _robot.X;
            _robot.PathAckDelayMilliseconds = 0;
            _robot.DropNextPathAck = true;
            yield return RunReviewed(.4f, 2, 7101);
            yield return Until(() => !_controller.PathClient.IsActive, "the second run stalled after missing only its ingress ACK", 7);
            Assert.That(_controller.PathClient.Status, Does.StartWith("Path completed"), Diagnostic);
            Assert.That(_robot.X - firstEnd, Is.GreaterThan(.2));
            Assert.That(_robot.PathCount, Is.EqualTo(2), "the 20 Hz heartbeat loop must never replay either path");
            Assert.That(_pathReceipts, Is.EqualTo(1));
            Assert.That(_robot.HeartbeatCount - heartbeatCount, Is.GreaterThan(10));
            Assert.That(_robot.NonNeutralPathHeartbeats, Is.Zero);
            Assert.That(_drive.Session.ClientId, Is.EqualTo(uuid), "normal repeated RUN must reuse the existing ownership session");
        }

        [UnityTest]
        public IEnumerator ConfirmedObstacleCancelsThenReplacesOnlyAfterAcceptedStopAndFreshInactiveSnapshot()
        {
            yield return Connect();
            yield return RunReviewed(1f, 1);
            _robot.HoldCancelAck = true;
            int inactiveBefore = _robot.InactiveSnapshots;
            _robot.AddObstacleAhead();
            yield return Until(() => _robot.CancelCount > 0, "persistent actual scan obstacle never cancelled the first route", 5);
            yield return Until(() => _robot.InactiveSnapshots > inactiveBefore, "the peer never served a pre-ACK stopped snapshot");
            Assert.That(_robot.PathCount, Is.EqualTo(1), "stopped HTTP alone must not submit a replacement path");
            Assert.That(_robot.PostCancelAckSnapshots, Is.Zero);
            Assert.That(_controller.PathClient.IsActive, Is.True, "stop confirmation is still pending");
            _robot.ReleaseCancelReceipts();
            yield return Until(() => _robot.PathCount == 2, "no safe replacement reached the actual UDP peer", 8);
            Assert.That(_robot.PostCancelAckSnapshots, Is.GreaterThan(0));
            Assert.That(_robot.LastPathPointCount, Is.GreaterThanOrEqualTo(3), "an obstacle on the old straight leg requires an actual forward bypass");
            Assert.That(_controller.PathClient.IsDetouring, Is.True);
            double stoppedX = _robot.X, stoppedY = _robot.Y;
            yield return Until(() => Math.Abs(_robot.X - stoppedX) + Math.Abs(_robot.Y - stoppedY) > .03,
                "the replacement was sent but the simulated follower made no progress", 25);
            _window.Stop();
            yield return Until(() => !_controller.PathClient.IsActive, "operator STOP did not finish cancellation");
            yield return new WaitForSecondsRealtime(.4f);
            Assert.That(_robot.PathCount, Is.EqualTo(2), "STOP must not create a third automatic run");
            Assert.That(_robot.NonNeutralPathHeartbeats, Is.Zero);
        }

        [UnityTest]
        public IEnumerator AcceptedIngressWithoutAnActiveFollowerTimesOutWithoutPretendingToRunOrReplaying()
        {
            _robot = new LocalPathRobot { IgnoreFollowerStart = true };
            yield return Connect();
            yield return Draw(.5f);
            yield return Until(() => RunButton.interactable, "RUN did not become available");
            RunButton.onClick.Invoke();
            yield return Until(() => _robot.PathCount == 1, "the requested route never reached the peer");
            Assert.That(_controller.PathClient.Status, Does.Not.StartWith("Following path"));
            yield return Until(() => !_controller.PathClient.IsActive, "an ACK-only start never reached its bounded failure", 6);
            Assert.That(_controller.PathClient.Status, Does.Contain("could not be verified"), Diagnostic);
            Assert.That(_robot.PathCount, Is.EqualTo(1));
            Assert.That(_robot.CancelCount, Is.GreaterThan(0));
        }

        [UnityTest]
        public IEnumerator AnEntirelyMissingScanBlocksRunDespiteKnownMapAndRecoversWithoutLosingTheDraft()
        {
            _robot = new LocalPathRobot { AllBeamsMissing = true };
            yield return Connect(navigationExpected: false);
            yield return Draw(.4f);
            yield return new WaitForSecondsRealtime(.2f);
            Assert.That(_controller.MapData.NavigationLive, Is.False);
            Assert.That(RunButton.interactable, Is.False);
            _window.Run(); // A stale UI callback must still obey the real gate.
            yield return new WaitForSecondsRealtime(.15f);
            Assert.That(_robot.PathCount, Is.Zero, "historical map free is not a substitute for a live scan");
            _robot.AllBeamsMissing = false;
            yield return Until(() => RunButton.interactable, "a fresh scan did not revalidate the retained draft");
            Assert.That(_window.PreparedWaypoints.Count, Is.EqualTo(2));
            RunButton.onClick.Invoke();
            yield return Until(() => _robot.PathCount == 1, "the recovered draft did not send");
            yield return Until(() => !_controller.PathClient.IsActive, "the recovered route did not complete", 7);
            Assert.That(_controller.PathClient.Status, Does.StartWith("Path completed"));
        }

        [UnityTest]
        public IEnumerator ScanLossDuringExecutionCancelsAndRecoveryDoesNotRestartTheOldRoute()
        {
            yield return Connect();
            yield return RunReviewed(1.2f, 1);
            _robot.PauseSensors = true;
            yield return Until(() => !_controller.PathClient.IsActive, "sensor loss did not stop the active route", 5);
            Assert.That(_robot.CancelCount, Is.GreaterThan(0));
            Assert.That(_controller.PathClient.Status, Does.Contain("stale"), Diagnostic);
            _robot.PauseSensors = false;
            yield return Until(() => _controller.MapData.NavigationLive, "fresh observations did not restore map readiness");
            yield return new WaitForSecondsRealtime(.5f);
            Assert.That(_robot.PathCount, Is.EqualTo(1), "data recovery is not new RUN authorization");
            Assert.That(_controller.PathClient.IsActive, Is.False);
        }

        [UnityTest]
        public IEnumerator AStrokeEntirelyWithinRobotArrivalToleranceCannotCreateAFakeStart()
        {
            yield return Connect();
            foreach (float distance in new[] { .05f, .10f })
            {
                _window.ClearDraft();
                yield return Draw(distance);
                yield return Until(() => RunButton.interactable, "the small draft never reached the final Execute gate");
                RunButton.onClick.Invoke();
                yield return new WaitForSecondsRealtime(.2f);
                Assert.That(_robot.PathCount, Is.Zero, "a route at or inside the actual follower arrival tolerance must not be sent");
                Assert.That(_controller.PathClient.IsActive, Is.False);
                Assert.That(_window.StatusLabel.text, Does.Contain("arrival"), Diagnostic);
            }
        }

        [UnityTest]
        public IEnumerator LosingArbiterPermissionCancelsActivePathAndOnlyAnExplicitNewRunCanResume()
        {
            yield return Connect();
            yield return RunReviewed(1f, 1);
            _robot.MotionPermitted = false;
            yield return Until(() => !_controller.PathClient.IsActive,
                "fresh arbiter denial did not cancel the active route", 3);
            Assert.That(_robot.CancelCount, Is.GreaterThan(0));
            Assert.That(_controller.RosBridge.ArbiterState.vr_permitted, Is.False);
            Assert.That(_drive.ExternalMotionBlocked, Is.True);
            Assert.That(_controller.PathClient.Status, Does.StartWith("Cancelled:"), Diagnostic);
            _robot.MotionPermitted = true;
            yield return Until(() => _controller.RosBridge.ArbiterState.vr_permitted &&
                _drive.Session.NeutralConfirmed && !_drive.ExternalMotionBlocked,
                "restored permission did not return to a neutral, released state");
            yield return new WaitForSecondsRealtime(.4f);
            Assert.That(_robot.PathCount, Is.EqualTo(1), "permission recovery is not permission to restart an old path");
            Assert.That(_controller.PathClient.IsActive, Is.False);
            yield return RunReviewed(.4f, 2, 7102);
            yield return Until(() => !_controller.PathClient.IsActive, "the explicitly reviewed recovery route did not complete", 7);
            Assert.That(_controller.PathClient.Status, Does.StartWith("Path completed"), Diagnostic);
        }

        [UnityTest]
        public IEnumerator RunBetweenNewMapUpdateAndWindowLateUpdateRefreshesTheReviewedBypass()
        {
            yield return Connect();
            _robot.AddObstacleAhead();
            yield return Draw(1f);
            yield return Until(() => _window.BypassPreviewReady && RunButton.interactable,
                "the stationary confirmed obstacle never produced a reviewed green bypass");
            Assert.That(_robot.PathCount, Is.Zero);
            var earlyClick = _root.AddComponent<PathRunBeforeLateUpdate>();
            earlyClick.Map = _controller.MapData;
            earlyClick.Window = _window;
            earlyClick.AfterRevision = _controller.MapData.Revision;
            earlyClick.Click = () => _window.Run();
            earlyClick.Armed = true;
            yield return Until(() => earlyClick.Invoked, "the map did not advance while the old preview was visible");
            Assert.That(earlyClick.ObservedStaleBypass, Is.True,
                "the click must occur after source data advanced and before the window refreshed");
            yield return Until(() => _robot.PathCount == 1,
                "RUN used a stale preview instead of refreshing it before checking bypass authorization");
            Assert.That(_robot.LastPathPointCount, Is.GreaterThanOrEqualTo(3));
            Assert.That(_controller.PathClient.IsActive, Is.True);
            _window.Stop();
            yield return Until(() => !_controller.PathClient.IsActive, "the race-test route did not cancel cleanly");
        }

        [UnityTest]
        public IEnumerator ActualRobotForwardBypassRejoinsOriginalGoalAndCanRunAgain()
        {
            _robot = new LocalPathRobot(true);
            yield return ForwardBypassRejoinsOriginalGoalAndCanRunAgain();
        }

        [UnityTest]
        public IEnumerator ForwardBypassRejoinsOriginalGoalAndCanRunAgain()
        {
            yield return Connect();
            string uuid = _drive.Session.ClientId;
            Vector2 originalGoal = new Vector2((float)_robot.X, (float)_robot.Y) +
                new Vector2((float)Math.Cos(_robot.Yaw), (float)Math.Sin(_robot.Yaw)) * 1.1f;
            yield return RunReviewed(1.1f, 1);
            _robot.AddObstacleAhead();
            yield return Until(() => _robot.PathCount == 2,
                "the original route was not replaced with an actual obstacle bypass", 8);
            Assert.That(_robot.LastPathPointCount, Is.GreaterThanOrEqualTo(3));
            Assert.That(_robot.PostCancelAckSnapshots, Is.GreaterThan(0));
            yield return Until(() => !_controller.PathClient.IsActive,
                "the complete forward bypass never reached its original goal", 75);
            Assert.That(_controller.PathClient.RemoteState, Is.EqualTo("done"), Diagnostic);
            Assert.That(_controller.PathClient.Status, Does.StartWith("Path completed"), Diagnostic);
            Assert.That(_robot.State, Is.EqualTo("done"));
            Assert.That(Vector2.Distance(new Vector2((float)_robot.X, (float)_robot.Y), originalGoal),
                Is.LessThanOrEqualTo(.15f), "the physical-model pose must rejoin the original requested goal");
            Assert.That(Vector2.Distance(new Vector2(_controller.MapData.RobotPose.x, _controller.MapData.RobotPose.y), originalGoal),
                Is.LessThanOrEqualTo(.15f), "the timestamp-paired map pose must also show arrival at the original goal");
            Assert.That(_robot.PathCount, Is.EqualTo(2), "completion must use one original and one bypass, not repeated escape attempts");
            Assert.That(_robot.CollisionSamples, Is.Zero,
                "the oriented walking footprint contacted the obstacle during the executed bypass");
            Assert.That(_robot.NonNeutralPathHeartbeats, Is.Zero);
            yield return RunReviewed(.4f, 3, 7103);
            yield return Until(() => !_controller.PathClient.IsActive,
                "an explicit route after completed avoidance did not finish", 7);
            Assert.That(_controller.PathClient.Status, Does.StartWith("Path completed"), Diagnostic);
            Assert.That(_robot.PathCount, Is.EqualTo(3));
            Assert.That(_drive.Session.ClientId, Is.EqualTo(uuid));
            Assert.That(_robot.CollisionSamples, Is.Zero);
            Assert.That(_robot.NonNeutralPathHeartbeats, Is.Zero);
        }
    }
}
#endif
