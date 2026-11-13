#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Oculus.Interaction;
using Oculus.Interaction.HandGrab;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace PuppyPi.Tests
{
    public class PuppyPiDashboardGrabTests
    {
        private GameObject _fixture;
        private PuppyPiVrPanel _panel;
        private PuppyPiRobotController _controller;
        private PuppyPiV2Client _drive;
        private Transform _joystick;
        private Transform _head;

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            if (_fixture != null) Object.Destroy(_fixture);
            yield return null;
        }

        private void BuildFixture()
        {
            _fixture = new GameObject("Dashboard grab fixture");
            _fixture.SetActive(false);
            _fixture.AddComponent<EventSystem>();
            _fixture.AddComponent<PointableCanvasModule>();
            var cameraObject = new GameObject("Fixture head", typeof(Camera));
            cameraObject.transform.SetParent(_fixture.transform, false);
            cameraObject.tag = "MainCamera";
            _head = cameraObject.transform;
            _head.position = new Vector3(1, 1.6f, -2);
            _head.rotation = Quaternion.Euler(0, 25, 0);

            var controllerObject = new GameObject("Offline robot controller");
            controllerObject.transform.SetParent(_fixture.transform, false);
            var gripBase = new GameObject("Unrelated grip base");
            gripBase.transform.SetParent(controllerObject.transform, false);
            var grip = new GameObject("Unrelated physical joystick");
            grip.transform.SetParent(gripBase.transform, false);
            _joystick = grip.transform;
            _joystick.localPosition = new Vector3(0.3f, 0.4f, 0.5f);
            var input = gripBase.AddComponent<BallGripInput>();
            input.baseFrame = gripBase.transform;
            input.grip = _joystick;
            _drive = controllerObject.AddComponent<PuppyPiV2Client>();
            _drive.input = input;
            _drive.networkEnabled = false;
            _controller = controllerObject.AddComponent<PuppyPiRobotController>();
            _controller.driveClient = _drive;
            _controller.gripInput = input;
            _controller.showCameraScreenOnStart = false;
            var panelObject = new GameObject("Dashboard host");
            panelObject.transform.SetParent(_fixture.transform, false);
            _panel = panelObject.AddComponent<PuppyPiVrPanel>();
            _panel.controller = _controller;
            _controller.dashboard = _panel;
            _fixture.SetActive(true);
            _panel.Build();
            _panel.Recenter();
        }

        private void TickCoordinator()
        {
            // Batch-mode editor focus is unrelated to this panel-grab contract.
            typeof(PuppyPiRobotController).GetMethod("OnApplicationFocus", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(_controller, new object[] { true });
            typeof(PuppyPiRobotController).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_controller, null);
        }

        private static void Pointer(Grabbable grab, int id, PointerEventType kind, Pose pose) =>
            grab.ProcessPointerEvent(new PointerEvent(id, kind, pose));

        private static void Select(Grabbable grab, int id, Pose pose)
        {
            Pointer(grab, id, PointerEventType.Hover, pose);
            Pointer(grab, id, PointerEventType.Select, pose);
        }

        private Pose GripPose(string side)
        {
            Collider collider = _panel.PanelRoot.GetComponentsInChildren<Collider>()
                .Single(item => item.name.StartsWith(side + " dashboard grip"));
            return new Pose(collider.transform.position, _panel.PanelRoot.rotation);
        }

        private Button Button(string name) => _panel.PanelRoot.GetComponentsInChildren<Button>(true)
            .Single(item => item.name == name);

        private void Click(string name)
        {
            var pointer = new PointerEventData(_fixture.GetComponent<EventSystem>()) { button = PointerEventData.InputButton.Left };
            ExecuteEvents.Execute(Button(name).gameObject, pointer, ExecuteEvents.pointerClickHandler);
        }

        private bool PageVisible(string name) => _panel.PanelRoot.GetComponentsInChildren<Transform>(true)
            .Single(item => item.name == name + " page").gameObject.activeSelf;

        private void AssertUiSurfaceFollowsCanvas()
        {
            Canvas canvas = _panel.PanelRoot.GetComponentInChildren<Canvas>();
            var pointable = canvas.GetComponent<PointableCanvas>();
            var poke = canvas.GetComponentInChildren<PokeInteractable>();
            var ray = canvas.GetComponentInChildren<RayInteractable>();
            Assert.That(canvas.GetComponent<GraphicRaycaster>(), Is.Not.Null);
            Assert.That(pointable.Canvas, Is.SameAs(canvas));
            Assert.That(poke.PointableElement, Is.SameAs(pointable));
            Assert.That(ray.PointableElement, Is.SameAs(pointable));
            Assert.That(poke.SurfacePatch, Is.SameAs(ray.Surface));
            Transform surface = ray.Surface.Transform;
            Vector3 center = surface.position;
            Vector3 forward = surface.forward;
            Assert.That(ray.Surface.Raycast(new Ray(center - forward * 0.25f, forward), out var rayHit), Is.True);
            Assert.That(Vector3.Distance(rayHit.Point, center), Is.LessThan(0.001));
            Assert.That(poke.SurfacePatch.ClosestSurfacePoint(center - forward * 0.02f, out var pokeHit, 0.1f), Is.True);
            Assert.That(Vector3.Distance(pokeHit.Point, center), Is.LessThan(0.001));
            var rect = (RectTransform)canvas.transform;
            Vector3 outside = canvas.transform.TransformPoint(new Vector3(rect.rect.xMax + 10, 0, 0));
            Assert.That(ray.Surface.Raycast(new Ray(outside - forward * 0.25f, forward), out _), Is.False,
                "the button surface must stay bounded after moving or resizing the dashboard");
        }

        [UnityTest]
        public IEnumerator OneHandMovesOnlyDashboardAndBlocksDrivingUntilReleased()
        {
            BuildFixture();
            yield return null;
            Transform panelRoot = _panel.PanelRoot;
            var grab = panelRoot.GetComponent<Grabbable>();
            Vector3 hostPosition = _panel.transform.position;
            Vector3 controllerPosition = _controller.transform.position;
            Vector3 joystickPosition = _joystick.position;
            Vector3 initialPosition = panelRoot.position;
            Quaternion initialRotation = panelRoot.rotation;
            Assert.That(panelRoot.GetComponents<MonoBehaviour>().Any(item => item.GetType().Name == "OneGrabFreeTransformer"), Is.True);
            Assert.That(panelRoot.GetComponents<MonoBehaviour>().Any(item => item.GetType().Name == "TwoGrabFreeTransformer"), Is.True);
            Assert.That(panelRoot.GetComponent<HandGrabInteractable>().PointableElement, Is.SameAs(grab));
            Assert.That(panelRoot.GetComponent<GrabInteractable>().PointableElement, Is.SameAs(grab));
            Assert.That(panelRoot.GetComponent<Rigidbody>().isKinematic, Is.True);
            Assert.That(panelRoot.GetComponent<Rigidbody>().useGravity, Is.False);
            Assert.That(panelRoot.GetComponentsInChildren<DistanceHandGrabInteractable>(true), Is.Empty);
            Assert.That(panelRoot.GetComponentsInChildren<DistanceGrabInteractable>(true), Is.Empty);
            Collider[] colliders = panelRoot.GetComponentsInChildren<Collider>();
            Assert.That(colliders.Length, Is.EqualTo(4));
            Physics.SyncTransforms();
            foreach (Collider collider in colliders)
            {
                Assert.That(collider.isTrigger, Is.True);
                Assert.That(Vector3.Distance(collider.ClosestPoint(panelRoot.position), panelRoot.position), Is.GreaterThan(0.1),
                    "only the outer rim may be grabbed, not the central button area");
            }
            AssertUiSurfaceFollowsCanvas();

            Pose pose = GripPose("Left");
            Select(grab, 301, pose);
            TickCoordinator();
            Assert.That(_panel.IsGrabbed, Is.True);
            Assert.That(_controller.IsPositioningPanel, Is.True);
            Assert.That(_drive.ExternalMotionBlocked, Is.True, "positioning a panel must force driving to neutral");
            pose.position += new Vector3(0.2f, 0.15f, -0.1f);
            pose.rotation = Quaternion.Euler(12, 30, 8) * pose.rotation;
            Pointer(grab, 301, PointerEventType.Move, pose);
            Assert.That(Vector3.Distance(panelRoot.position, initialPosition), Is.GreaterThan(0.05));
            Assert.That(Quaternion.Angle(panelRoot.rotation, initialRotation), Is.GreaterThan(10));
            Assert.That(panelRoot.localScale, Is.EqualTo(Vector3.one));
            Assert.That(_panel.transform.position, Is.EqualTo(hostPosition));
            Assert.That(_controller.transform.position, Is.EqualTo(controllerPosition));
            Assert.That(_joystick.position, Is.EqualTo(joystickPosition));
            AssertUiSurfaceFollowsCanvas();

            Pointer(grab, 301, PointerEventType.Unselect, pose);
            TickCoordinator();
            Assert.That(_panel.IsGrabbed, Is.False);
            Assert.That(_controller.IsPositioningPanel, Is.False);
            Assert.That(_drive.ExternalMotionBlocked, Is.False);
            Vector3 releasedPosition = panelRoot.position;
            Quaternion releasedRotation = panelRoot.rotation;
            yield return new WaitForSeconds(0.15f);
            Assert.That(Vector3.Distance(panelRoot.position, releasedPosition), Is.LessThan(0.001), "release must not throw the dashboard");
            Assert.That(Quaternion.Angle(panelRoot.rotation, releasedRotation), Is.LessThan(0.1));
            Assert.That(_drive.networkEnabled, Is.False, "the fixture must never connect to a robot");
        }

        [UnityTest]
        public IEnumerator TwoHandsApplyUniformBoundsWithoutStretchingCanvasOrCameraImage()
        {
            BuildFixture();
            yield return null;
            Transform root = _panel.PanelRoot;
            var grab = root.GetComponent<Grabbable>();
            Transform canvas = root.GetComponentInChildren<Canvas>().transform;
            Vector3 pixels = canvas.localScale;
            var aspect = _panel.CameraView.GetComponent<AspectRatioFitter>();
            aspect.aspectRatio = 4f / 3f;
            Canvas.ForceUpdateCanvases();
            Pose left = GripPose("Left"), right = GripPose("Right");
            Select(grab, 401, left);
            Select(grab, 402, right);
            Assert.That(grab.SelectingPointsCount, Is.EqualTo(2), "the second hand must join rather than transfer selection");
            Vector3 initialSpan = right.position - left.position;
            right.position = left.position + initialSpan * 5;
            Pointer(grab, 402, PointerEventType.Move, right);
            Assert.That(root.localScale.x, Is.EqualTo(_panel.maximumScale).Within(0.001));
            Assert.That(root.localScale.y, Is.EqualTo(root.localScale.x).Within(0.0001));
            Assert.That(root.localScale.z, Is.EqualTo(root.localScale.x).Within(0.0001));
            Assert.That(canvas.localScale, Is.EqualTo(pixels), "pixel-to-meter scale belongs to the canvas, not the grab transformer");
            AssertUiSurfaceFollowsCanvas();
            Canvas.ForceUpdateCanvases();
            Vector3[] corners = new Vector3[4];
            _panel.CameraView.rectTransform.GetWorldCorners(corners);
            Assert.That(Vector3.Distance(corners[0], corners[3]) / Vector3.Distance(corners[0], corners[1]),
                Is.EqualTo(4f / 3f).Within(0.001));
            Assert.That(_panel.CameraView.uvRect, Is.EqualTo(new Rect(0, 0, 1, 1)), "resizing must not crop the source image");

            right.position = left.position + initialSpan.normalized * 0.01f;
            Pointer(grab, 402, PointerEventType.Move, right);
            Assert.That(root.localScale.x, Is.EqualTo(_panel.minimumScale).Within(0.001));
            Assert.That(root.localScale.y, Is.EqualTo(root.localScale.x).Within(0.0001));
            Assert.That(root.localScale.z, Is.EqualTo(root.localScale.x).Within(0.0001));
            AssertUiSurfaceFollowsCanvas();
            Pointer(grab, 401, PointerEventType.Unselect, left);
            Pointer(grab, 402, PointerEventType.Unselect, right);
        }

        [UnityTest]
        public IEnumerator SupervisedExplicitCommandClicksWorkWhileGrabbedWithoutUnlockingTabsOrStudyCommands()
        {
            BuildFixture();
            yield return null;
            int commands = 0;
            var createCommand = typeof(PuppyPiVrPanel).GetMethod("Command", BindingFlags.Instance | BindingFlags.NonPublic);
            // A harmless callback exercises native button routing without
            // opening a robot channel or submitting a motion request.
            var command = (Button)createCommand.Invoke(_panel, new object[] {
                _panel.PanelRoot.GetComponentInChildren<Canvas>().transform, "Fixture explicit motion command", 24f, 700f, 186f, 40f,
                new System.Action<PuppyPiRobotController>(_ => commands++),
                new System.Func<PuppyPiRobotController, bool>(_ => true), null, false, true
            });
            var pointer = new PointerEventData(_fixture.GetComponent<EventSystem>()) {
                button = PointerEventData.InputButton.Left
            };
            var grab = _panel.PanelRoot.GetComponent<Grabbable>();
            Pose pose = GripPose("Left");
            Select(grab, 601, pose);
            Assert.That(_panel.IsGrabbed, Is.True);
            ExecuteEvents.Execute(command.gameObject, pointer, ExecuteEvents.pointerEnterHandler);
            Assert.That(commands, Is.Zero, "hover must never send a command");
            ExecuteEvents.Execute(command.gameObject, pointer, ExecuteEvents.pointerClickHandler);
            Assert.That(commands, Is.EqualTo(1), "an explicit supervised command click is not a layout lock");
            Click("MAP");
            Assert.That(PageVisible("MAP"), Is.False, "ordinary page/edit buttons retain their grab protection");

            _controller.PathClient.operatorSupervisedNavigation = false;
            ExecuteEvents.Execute(command.gameObject, pointer, ExecuteEvents.pointerClickHandler);
            Assert.That(commands, Is.EqualTo(1), "strict mode retains the previous command grab guard");
            _controller.PathClient.operatorSupervisedNavigation = true;
            typeof(PuppyPiStudyClient).GetProperty("Active").SetValue(_controller.studyClient, true);
            ExecuteEvents.Execute(command.gameObject, pointer, ExecuteEvents.pointerClickHandler);
            Assert.That(commands, Is.EqualTo(1), "study command policy is not relaxed by the supervised layout exception");
            typeof(PuppyPiStudyClient).GetProperty("Active").SetValue(_controller.studyClient, false);
            Click("UDP STOP");
            Assert.That(_drive.Session.Emergency, Is.True, "explicit STOP remains usable during a grab");
            Pointer(grab, 601, PointerEventType.Unselect, pose);
        }

        [UnityTest]
        public IEnumerator RecenterKeepsChosenScaleCancelsGrabAndOrdinaryButtonsIgnoreDragging()
        {
            BuildFixture();
            yield return null;
            Transform root = _panel.PanelRoot;
            var grab = root.GetComponent<Grabbable>();
            Pose left = GripPose("Left"), right = GripPose("Right");
            Select(grab, 501, left);
            Select(grab, 502, right);
            right.position = left.position + (right.position - left.position) * 1.5f;
            Pointer(grab, 502, PointerEventType.Move, right);
            Assert.That(root.localScale.x, Is.EqualTo(1.5f).Within(0.001));
            Click("MAP");
            Assert.That(PageVisible("DASHBOARD"), Is.True, "a page click during frame manipulation must be ignored");
            Assert.That(PageVisible("MAP"), Is.False);

            Vector3 scale = root.localScale;
            Vector3 oldPosition = root.position;
            _head.position += new Vector3(0.3f, 0.1f, 0.2f);
            _panel.Recenter();
            Assert.That(root.localScale, Is.EqualTo(scale), "recenter must preserve the user's chosen size");
            Assert.That(root.localPosition, Is.EqualTo(Vector3.zero));
            Assert.That(Quaternion.Angle(root.localRotation, Quaternion.identity), Is.LessThan(0.001));
            Assert.That(Vector3.Distance(root.position, oldPosition), Is.GreaterThan(0.05));
            Assert.That(grab.SelectingPointsCount, Is.Zero, "recenter must cancel obsolete hand offsets");
            Assert.That(_panel.IsGrabbed, Is.False);
            TickCoordinator();
            Assert.That(_controller.IsPositioningPanel, Is.False);
            Assert.That(_drive.ExternalMotionBlocked, Is.False);
            AssertUiSurfaceFollowsCanvas();
            Click("MAP");
            Assert.That(PageVisible("MAP"), Is.True, "the same page click must work once frame manipulation ends");
            Assert.That(PageVisible("DASHBOARD"), Is.False);
        }
    }
}
#endif
