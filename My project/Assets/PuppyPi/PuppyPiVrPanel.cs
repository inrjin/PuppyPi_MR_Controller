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
    // A separate cockpit instrument: no transform or input changes to the ball grip.
    public sealed class PuppyPiVrPanel : MonoBehaviour
    {
        public PuppyPiRobotController controller;
        [Min(0.0001f)] public float metersPerPixel = 0.0009f;
        [Range(0.1f, 1f)] public float minimumScale = 0.35f;
        [Range(1f, 4f)] public float maximumScale = 2f;
        public RawImage CameraView { get; private set; }
        public RawImage MapView { get; private set; }
        public Transform PanelRoot => _panelRoot != null ? _panelRoot.transform : null;
        public bool IsGrabbed => isActiveAndEnabled && _panelRoot != null && _panelRoot.activeInHierarchy &&
            _grabbable != null && _grabbable.SelectingPoints != null && _grabbable.SelectingPointsCount > 0;

        private const float Width = 840;
        private const float Height = 800;
        private const int ActionsPerPage = 8;
        private const int DevicesPerPage = 3;
        private const int DashboardPage = 0;
        private const int MapPage = 1;
        private const int PosesPage = 2;
        private const int ActionsPage = 3;
        private const int ConnectionPage = 4;
        private const int StudyPage = 5;
        public bool PlacementLocked { get; private set; }
        private static readonly Color Background = new Color(0.035f, 0.06f, 0.085f, 0.98f);
        private static readonly Color ButtonColor = new Color(0.12f, 0.21f, 0.26f, 1);
        private static readonly Color Accent = new Color(0.21f, 0.76f, 0.66f, 1);
        private static readonly Color Danger = new Color(0.7f, 0.18f, 0.22f, 1);
        private static readonly Color Muted = new Color(0.66f, 0.76f, 0.81f, 1);
        private readonly List<KeyValuePair<Button, Func<PuppyPiRobotController, bool>>> _gated =
            new List<KeyValuePair<Button, Func<PuppyPiRobotController, bool>>>();
        private readonly List<Button> _poseButtons = new List<Button>();
        private readonly List<Button> _actionButtons = new List<Button>();
        private readonly List<Button> _deviceButtons = new List<Button>();
        private readonly DiscoveredPi[] _displayedDevices = new DiscoveredPi[DevicesPerPage];
        private readonly List<Button> _tabs = new List<Button>();
        private readonly GameObject[] _pages = new GameObject[6];
        private Font _font;
        private GameObject _panelRoot;
        private Grabbable _grabbable;
        private Image[] _rim;
        private bool _lastGrabbed;
        private Canvas _canvas;
        private Text _summary;
        private Text _result;
        private Text _speed;
        private Text _cameraNotice;
        private Text _mapNotice;
        private Text _mediaDetails;
        private Text _selectedPoseLabel;
        private Text _selectedActionLabel;
        private Text _actionPageLabel;
        private Text _networkInfo;
        private Text _studyInfo;
        private Text _studyAddress;
        private string _studyHost = "";
        private Text _devicePageLabel;
        private Button _hostButton;
        private Button _portButton;
        private Button _searchButton;
        private string _host = "";
        private string _port = "5001";
        private string _localNotice = "";
        private string _selectedPose;
        private string _selectedAction;
        private bool _editingPort;
        private int _actionPage;
        private int _devicePage;
        private int _visiblePage;
        private double _nextRefresh;

        private void Start()
        {
            if (controller == null) controller = FindFirstObjectByType<PuppyPiRobotController>();
            if (controller != null)
            {
                controller.dashboard = this;
                if (controller.Connection != null)
                {
                    _host = controller.Connection.AddressText;
                    _port = controller.Connection.PortText;
                }
                if (string.IsNullOrWhiteSpace(_host) && controller.DriveClient != null)
                    _host = controller.DriveClient.robotAddress;
            }
            Build();
        }

        private void Update()
        {
            if (_canvas == null || Time.realtimeSinceStartupAsDouble < _nextRefresh) return;
            _nextRefresh = Time.realtimeSinceStartupAsDouble + 0.1;
            Refresh();
        }

        private void LateUpdate()
        {
            if (_rim == null || IsGrabbed == _lastGrabbed) return;
            _lastGrabbed = IsGrabbed;
            foreach (var rim in _rim) rim.color = _lastGrabbed ? Accent : ButtonColor;
        }

        public void Build()
        {
            if (_canvas != null) return;
            EnsureInputModule();
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            // Grab transforms use meter units and scale 1; the pixel canvas keeps its own fixed scale.
            _panelRoot = new GameObject("Movable VR dashboard");
            _panelRoot.SetActive(false);
            _panelRoot.transform.SetParent(transform, false);
            var root = new GameObject("Native VR controls", typeof(RectTransform));
            root.SetActive(false);
            root.transform.SetParent(_panelRoot.transform, false);
            root.transform.localScale = Vector3.one * metersPerPixel;
            var rect = (RectTransform)root.transform;
            rect.sizeDelta = new Vector2(Width, Height);
            _canvas = root.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            _canvas.worldCamera = Camera.main;
            _canvas.sortingOrder = 30;
            root.AddComponent<GraphicRaycaster>();
            var background = root.AddComponent<Image>();
            background.color = Background;
            background.raycastTarget = false;
            ConfigureInteraction(root, _canvas);
            ConfigureGrabFrame(root.transform);

            Label(root.transform, "PUPPYPI  /  ROBOT CONSOLE", 24, 16, 792, 32, 26, Color.white);
            Button(root.transform, "RECENTER", 666, 16, 150, 32, Recenter, allowWhileGrabbed: true);
            _summary = Label(root.transform, "Waiting for robot status", 24, 58, 792, 92, 17, Muted);
            Command(root.transform, "UDP STOP", 24, 158, 186, 52, c => c.UdpStop(), null, Danger, true);
            Command(root.transform, "CLEAR STOP", 226, 158, 186, 52, c => c.ClearUdpStop(), c => c.CanClearStop);
            Command(root.transform, "ROBOT STOP", 428, 158, 186, 52, c => c.RobotStop(), null, Danger, true);
            Command(root.transform, "RESET ROBOT", 630, 158, 186, 52, c => c.ResetRobot(),
                c => c.RosBridge != null && c.RosBridge.IsConnected && c.CanClearStop);
            Command(root.transform, "-10%", 24, 222, 145, 46,
                c => c.SetSpeedPercent(Mathf.Clamp(c.SpeedPercent - 10, 10, 100)), c => !c.StudyActive);
            _speed = Label(root.transform, "SPEED 50%", 183, 222, 474, 46, 21, Accent, TextAnchor.MiddleCenter);
            Command(root.transform, "+10%", 671, 222, 145, 46,
                c => c.SetSpeedPercent(Mathf.Clamp(c.SpeedPercent + 10, 10, 100)), c => !c.StudyActive);

            string[] tabNames = { "DASHBOARD", "MAP", "POSES", "ACTIONS", "CONNECT", "STUDY" };
            const float tabGap = 12;
            float tabWidth = (792 - tabGap * (tabNames.Length - 1)) / tabNames.Length;
            for (int i = 0; i < tabNames.Length; i++)
            {
                int index = i;
                _tabs.Add(Button(root.transform, tabNames[i], 24 + i * (tabWidth + tabGap), 280, tabWidth, 48, () => ShowPage(index)));
                _pages[i] = Rect(root.transform, tabNames[i] + " page", 24, 342, 792, 350).gameObject;
            }
            BuildDashboard(_pages[DashboardPage].transform);
            BuildMap(_pages[MapPage].transform);
            BuildPoses(_pages[PosesPage].transform);
            BuildActions(_pages[ActionsPage].transform);
            BuildConnection(_pages[ConnectionPage].transform);
            BuildStudy(_pages[StudyPage].transform);
            _result = Label(root.transform, "Select a control to begin.", 24, 706, 792, 66, 17, Muted);
            Label(root.transform, "Touch buttons | Grab the outer rim to move | Two hands to resize",
                24, 777, 792, 18, 14, Muted, TextAnchor.MiddleCenter);
            ShowPage(DashboardPage);
            root.SetActive(true);
            _panelRoot.SetActive(true);
            Refresh();
        }

        private void BuildStudy(Transform parent)
        {
            if (controller != null && controller.studyClient != null) _studyHost = controller.studyClient.serviceAddress;
            _studyInfo = Label(parent, "Study service is not connected.", 0, 0, 414, 146, 17, Muted);
            _studyAddress = Label(parent, "SERVICE IP: _", 0, 148, 414, 40, 21, Color.white);
            Button(parent, "ROBOT HOST", 0, 194, 196, 46, () => {
                if (controller != null && controller.DriveClient != null) _studyHost = controller.DriveClient.robotAddress;
            });
            Button(parent, "CLEAR IP", 208, 194, 196, 46, () => _studyHost = "");
            Button(parent, "ENTER STUDY", 0, 250, 196, 46, () => {
                if (controller == null || controller.studyClient == null) return;
                if (!controller.studyClient.EnterStudy(_studyHost, out string error)) _localNotice = error;
                else _localNotice = "";
            }, Accent);
            Button(parent, "EXIT STUDY", 208, 250, 196, 46, () => controller?.studyClient?.ExitStudy());
            Button(parent, "ABORT TRIAL", 0, 304, 404, 46, () => controller?.studyClient?.AbortTrial(), Danger, true);
            string[] keys = { "1", "2", "3", "4", "5", "6", "7", "8", "9", ".", "0", "DEL" };
            for (int i = 0; i < keys.Length; i++)
            {
                string key = keys[i];
                Button(parent, key, 438 + i % 3 * 122, 50 + i / 3 * 54, 110, 46, () => {
                    if (key == "DEL") _studyHost = _studyHost.Length > 0 ? _studyHost.Substring(0, _studyHost.Length - 1) : "";
                    else if (_studyHost.Length < 15) _studyHost += key;
                });
            }
            Label(parent, "Operator starts trials from the study CLI.", 438, 278, 354, 70, 18, Muted);
            Button(parent, "FEEDBACK POLICY", 438, 0, 354, 42, () => {
                var study = controller != null ? controller.studyClient : null;
                if (study == null || study.Active) return;
                study.presentationPolicy = (StudyPresentationPolicy)(((int)study.presentationPolicy + 1) % 4);
            });
        }

        public void SetPlacementLocked(bool locked)
        {
            PlacementLocked = locked;
            if (_grabbable != null) _grabbable.enabled = !locked;
        }

        private void ConfigureGrabFrame(Transform canvas)
        {
            const float thickness = 0.03f;
            float width = Width * metersPerPixel;
            float height = Height * metersPerPixel;
            var body = _panelRoot.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.isKinematic = true;
            AddGrip("Top dashboard grip", new Vector3(0, (height + thickness) / 2, 0),
                new Vector3(width + thickness * 2, thickness, 0.035f));
            AddGrip("Bottom dashboard grip", new Vector3(0, -(height + thickness) / 2, 0),
                new Vector3(width + thickness * 2, thickness, 0.035f));
            AddGrip("Left dashboard grip", new Vector3(-(width + thickness) / 2, 0, 0),
                new Vector3(thickness, height, 0.035f));
            AddGrip("Right dashboard grip", new Vector3((width + thickness) / 2, 0, 0),
                new Vector3(thickness, height, 0.035f));

            float pixels = thickness / metersPerPixel;
            _rim = new[] {
                Rim(canvas, "Top dashboard rim", -pixels, -pixels, Width + pixels * 2, pixels),
                Rim(canvas, "Bottom dashboard rim", -pixels, Height, Width + pixels * 2, pixels),
                Rim(canvas, "Left dashboard rim", -pixels, 0, pixels, Height),
                Rim(canvas, "Right dashboard rim", Width, 0, pixels, Height)
            };
            _grabbable = _panelRoot.AddComponent<Grabbable>();
            _grabbable.InjectOptionalRigidbody(body);
            _grabbable.InjectOptionalTargetTransform(_panelRoot.transform);
            _grabbable.InjectOptionalKinematicWhileSelected(true);
            _grabbable.InjectOptionalThrowWhenUnselected(false);
            _grabbable.TransferOnSecondSelection = false;
            _grabbable.MaxGrabPoints = 2;
#pragma warning disable CS0618 // Match the installed SDK's tested one/two-hand window transformers.
            var oneHand = _panelRoot.AddComponent<OneGrabFreeTransformer>();
            var twoHands = _panelRoot.AddComponent<TwoGrabFreeTransformer>();
            twoHands.InjectOptionalConstraints(new TwoGrabFreeTransformer.TwoGrabFreeConstraints {
                ConstraintsAreRelative = false,
                MinScale = new FloatConstraint { Constrain = true, Value = Mathf.Clamp(minimumScale, 0.1f, 1f) },
                MaxScale = new FloatConstraint { Constrain = true, Value = Mathf.Clamp(maximumScale, 1f, 4f) },
                ConstrainXScale = true, ConstrainYScale = true, ConstrainZScale = true
            });
            _grabbable.InjectOptionalOneGrabTransformer(oneHand);
            _grabbable.InjectOptionalTwoGrabTransformer(twoHands);
#pragma warning restore CS0618
            var handGrab = _panelRoot.AddComponent<HandGrabInteractable>();
            handGrab.InjectRigidbody(body);
            handGrab.InjectSupportedGrabTypes(GrabTypeFlags.All);
            handGrab.InjectOptionalPointableElement(_grabbable);
            handGrab.HandAlignment = HandAlignType.None;
            handGrab.ResetGrabOnGrabsUpdated = false;
            var controllerGrab = _panelRoot.AddComponent<GrabInteractable>();
            controllerGrab.InjectRigidbody(body);
            controllerGrab.InjectOptionalPointableElement(_grabbable);
            controllerGrab.ResetGrabOnGrabsUpdated = false;
            controllerGrab.UseClosestPointAsGrabSource = true;
        }

        private void AddGrip(string name, Vector3 position, Vector3 size)
        {
            var grip = new GameObject(name);
            grip.transform.SetParent(_panelRoot.transform, false);
            grip.transform.localPosition = position;
            var collider = grip.AddComponent<BoxCollider>();
            collider.isTrigger = true;
            collider.size = size;
        }

        private static Image Rim(Transform parent, string name, float x, float y, float width, float height)
        {
            var image = Rect(parent, name, x, y, width, height).gameObject.AddComponent<Image>();
            image.color = ButtonColor;
            image.raycastTarget = false;
            return image;
        }

        private static void EnsureInputModule()
        {
            var module = FindFirstObjectByType<PointableCanvasModule>();
            if (module == null)
            {
                var system = EventSystem.current;
                if (system == null) system = FindFirstObjectByType<EventSystem>();
                if (system == null) system = new GameObject("VR UI EventSystem", typeof(EventSystem)).GetComponent<EventSystem>();
                module = system.gameObject.AddComponent<PointableCanvasModule>();
            }
            // The SDK disables competing input modules, preventing duplicate pointer clicks.
            module.ExclusiveMode = true;
        }

        private static void ConfigureInteraction(GameObject root, Canvas canvas)
        {
            var pointable = root.AddComponent<PointableCanvas>();
            pointable.InjectCanvas(canvas);
            var surface = new GameObject("UI interaction surface");
            surface.transform.SetParent(root.transform, false);
            var plane = surface.AddComponent<PlaneSurface>();
            plane.Facing = PlaneSurface.NormalFacing.Backward;
            plane.DoubleSided = false;
            var bounds = surface.AddComponent<BoundsClipper>();
            bounds.Position = Vector3.zero;
            bounds.Size = new Vector3(Width, Height, 0.01f);
            var patch = surface.AddComponent<ClippedPlaneSurface>();
            patch.InjectAllClippedPlaneSurface(plane, new IBoundsClipper[] { bounds });
            var poke = surface.AddComponent<PokeInteractable>();
            poke.InjectSurfacePatch(patch);
            poke.InjectOptionalPointableElement(pointable);
            var ray = surface.AddComponent<RayInteractable>();
            ray.InjectSurface(patch);
            ray.InjectOptionalSelectSurface(plane);
            ray.InjectOptionalPointableElement(pointable);
        }

        private void BuildDashboard(Transform parent)
        {
            CameraView = MediaView(parent, "Robot camera", "Camera frame", 16f / 9f);
            _cameraNotice = Label(parent, "NO SIGNAL | WAITING FOR CAMERA", 0, 284, 496, 36, 15, Muted, TextAnchor.MiddleCenter);
            Label(parent, "BALL GRIP DRIVE", 522, 0, 270, 32, 22, Accent);
            Label(parent, "Hold the ball grip to drive.\n\nRelease to stop.\n\nAfter a stop, release and re-grab.",
                522, 46, 270, 130, 18, Color.white);
            Command(parent, "OPEN / RECENTER CAMERA", 522, 186, 270, 44, c => c.ShowCameraScreen());
            Command(parent, "STAND / GO HOME", 522, 238, 270, 62, c => c.GoHome(), c => c.CanExecuteMotion,
                allowSupervisedGrab: true);
            Label(parent, "Camera view is separate from motion control.", 0, 324, 792, 24, 15, Muted);
        }

        private void BuildMap(Transform parent)
        {
            MapView = MediaView(parent, "Lidar map", "Map frame", 1);
            _mapNotice = Label(parent, "NO SIGNAL | WAITING FOR MAP", 0, 284, 496, 36, 15, Muted, TextAnchor.MiddleCenter);
            Label(parent, "LIDAR MAP", 522, 0, 270, 32, 22, Accent);
            _mediaDetails = Label(parent, "Waiting for media status", 522, 44, 270, 210, 15, Muted);
            Command(parent, "OPEN MAP / DRAW PATH", 522, 264, 270, 50,
                c => c.ShowMapWindow(), c => !c.StudyActive);
            Label(parent, "Red: robot position  |  Blue: traveled path", 0, 324, 792, 24, 15, Muted);
        }

        private static RawImage MediaView(Transform parent, string name, string frameName, float ratio)
        {
            // Each image fits its own frame so stream dimensions cannot move the controls.
            var frame = Rect(parent, frameName, 0, 0, 496, 280);
            var imageRect = Rect(frame, name, 0, 0, 496, 280);
            imageRect.anchorMin = Vector2.zero;
            imageRect.anchorMax = Vector2.one;
            imageRect.pivot = new Vector2(0.5f, 0.5f);
            imageRect.anchoredPosition = Vector2.zero;
            imageRect.sizeDelta = Vector2.zero;
            var image = imageRect.gameObject.AddComponent<RawImage>();
            image.color = Color.black;
            image.raycastTarget = false;
            var aspect = imageRect.gameObject.AddComponent<AspectRatioFitter>();
            aspect.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            aspect.aspectRatio = ratio;
            return image;
        }

        private void BuildPoses(Transform parent)
        {
            _selectedPoseLabel = Label(parent, "Select a pose, then press APPLY POSE.", 0, 0, 792, 34, 19, Muted);
            string[] poses = RobotCommandCatalog.PoseNames;
            for (int i = 0; i < poses.Length; i++)
            {
                string pose = poses[i];
                var button = Button(parent, Pretty(pose), (i % 2) * 406, 45 + (i / 2) * 58, 386, 48,
                    () => { _selectedPose = pose; RefreshSelections(); });
                _poseButtons.Add(button);
            }
            Command(parent, "APPLY SELECTED POSE", 0, 294, 792, 56,
                c => c.ExecutePose(_selectedPose), c => c.CanExecuteMotion && !string.IsNullOrEmpty(_selectedPose), Accent,
                allowSupervisedGrab: true);
        }

        private void BuildActions(Transform parent)
        {
            _selectedActionLabel = Label(parent, "Select an action, then press RUN ACTION.", 0, 0, 792, 31, 18, Muted);
            for (int i = 0; i < ActionsPerPage; i++)
            {
                int slot = i;
                _actionButtons.Add(Button(parent, "", (i % 2) * 406, 40 + (i / 2) * 50, 386, 44, () =>
                {
                    int index = _actionPage * ActionsPerPage + slot;
                    if (index >= RobotCommandCatalog.ActionNames.Length) return;
                    _selectedAction = RobotCommandCatalog.ActionNames[index];
                    RefreshSelections();
                }));
            }
            Button(parent, "PREVIOUS", 0, 246, 180, 42, () => ChangeActionPage(-1));
            _actionPageLabel = Label(parent, "1 / 4", 190, 246, 412, 42, 19, Muted, TextAnchor.MiddleCenter);
            Button(parent, "NEXT", 612, 246, 180, 42, () => ChangeActionPage(1));
            Command(parent, "RUN SELECTED ACTION", 0, 300, 792, 50,
                c => c.ExecuteAction(_selectedAction), c => c.CanExecuteMotion && !string.IsNullOrEmpty(_selectedAction), Accent,
                allowSupervisedGrab: true);
            RefreshActionPage();
        }

        private void BuildConnection(Transform parent)
        {
            _networkInfo = Label(parent, "Current: -\nSaved: -", 0, 0, 414, 42, 16, Muted);
            _hostButton = Button(parent, "IP: " + _host, 0, 50, 300, 46, () => { _editingPort = false; RefreshAddress(); });
            _portButton = Button(parent, _port, 310, 50, 104, 46, () => { _editingPort = true; RefreshAddress(); });
            _searchButton = Command(parent, "SEARCH", 0, 108, 130, 42, c => c.Discover(), c => !c.IsDiscovering);
            Command(parent, "RECONNECT", 142, 108, 130, 42, c => c.Reconnect());
            Command(parent, "DISCONNECT", 284, 108, 130, 42, c => c.Disconnect());
            for (int i = 0; i < DevicesPerPage; i++)
            {
                int slot = i;
                _deviceButtons.Add(Command(parent, "No device", 0, 162 + i * 44, 414, 38, c =>
                {
                    DiscoveredPi device = _displayedDevices[slot];
                    if (device != null) c.ConnectDevice(device);
                }, c => _displayedDevices[slot] != null));
            }
            Button(parent, "<", 0, 304, 68, 44, () => { _devicePage = Mathf.Max(0, _devicePage - 1); RefreshDevices(); });
            _devicePageLabel = Label(parent, "No devices", 80, 304, 254, 44, 16, Muted, TextAnchor.MiddleCenter);
            Button(parent, ">", 346, 304, 68, 44, () =>
            {
                if (controller != null && (_devicePage + 1) * DevicesPerPage < controller.Devices.Count) _devicePage++;
                RefreshDevices();
            });
            Label(parent, "TAP IP OR PORT, THEN TYPE", 438, 0, 354, 40, 16, Accent, TextAnchor.MiddleCenter);
            string[] keys = { "1", "2", "3", "4", "5", "6", "7", "8", "9", ".", "0", "DEL" };
            for (int i = 0; i < keys.Length; i++)
            {
                string key = keys[i];
                Button(parent, key, 438 + (i % 3) * 122, 50 + (i / 3) * 54, 110, 46, () => TypeAddress(key));
            }
            Button(parent, "CLEAR", 438, 278, 110, 70, () =>
            {
                if (_editingPort) _port = ""; else _host = "";
                RefreshAddress();
            });
            Command(parent, "CONNECT IP", 560, 278, 232, 70, c =>
            {
                if (!int.TryParse(_port, out int port) || port < 1 || port > 65535)
                { _localNotice = "Enter a port from 1 to 65535."; return; }
                c.ConnectManual(_host.Trim(), port);
            }, null, Accent);
            RefreshAddress();
        }

        private void ShowPage(int index)
        {
            if (index == MapPage && controller != null && !controller.StudyMapVisible) return;
            _visiblePage = index;
            for (int i = 0; i < _pages.Length; i++)
            {
                _pages[i].SetActive(i == index);
                _tabs[i].GetComponent<Image>().color = i == index ? Accent * 0.55f : ButtonColor;
            }
            RefreshSelections();
        }

        private void Refresh()
        {
            if (_summary == null) return;
            if (controller == null)
            {
                _summary.text = "Robot controller is not configured.";
                foreach (var item in _gated) item.Key.interactable = false;
                return;
            }
            // Keep connection, engine, ownership and VR permission visible without clipping.
            string[] statusLines = (controller.StatusText ?? "").Split('\n');
            _summary.text = string.Join("\n", statusLines, 0, Mathf.Min(3, statusLines.Length));
            _result.text = controller.StudyActive ? controller.studyClient.DisplayText :
                string.IsNullOrEmpty(_localNotice) ? controller.LastResult : _localNotice;
            _speed.text = $"DRIVE SPEED  {controller.SpeedPercent}%";
            RefreshMedia(CameraView, _cameraNotice, controller.CameraTexture, controller.CameraLive,
                controller.CameraStatus, controller.CameraFrameAgeSeconds, "ROBOT CAMERA");
            RefreshMedia(MapView, _mapNotice, controller.MapTexture, controller.MapLive,
                controller.MapStatus, controller.MapFrameAgeSeconds, "LIDAR MAP");
            _mediaDetails.text = controller.MediaStatusText;
            CameraView.gameObject.SetActive(controller.StudyCameraVisible);
            MapView.gameObject.SetActive(controller.StudyMapVisible);
            _mediaDetails.gameObject.SetActive(controller.StudyMapVisible);
            _cameraNotice.text = controller.StudyCameraVisible ? _cameraNotice.text : "Camera hidden by study configuration.";
            _tabs[MapPage].interactable = controller.StudyMapVisible;
            if (_visiblePage == MapPage && !controller.StudyMapVisible) ShowPage(StudyPage);
            if (_studyInfo != null && controller.studyClient != null)
            {
                var study = controller.studyClient;
                _studyInfo.text = study.Notice + "\nClient:\n" + study.ControlClientId + "\n" + (study.Status != null ?
                    study.Status.difficulty_id : "Release the grip before readiness.") +
                    "\nPort: " + study.servicePort + " | " + study.presentationPolicy;
                _studyAddress.text = "SERVICE IP: " + _studyHost + "_";
            }
            foreach (var item in _gated) item.Key.interactable = item.Value == null || item.Value(controller);
            if (_visiblePage == ConnectionPage) RefreshDevices();
        }

        private static void RefreshMedia(RawImage image, Text notice, Texture texture, bool live,
            string status, double age, string name)
        {
            image.texture = texture;
            image.color = texture == null ? Color.black : Color.white;
            if (texture != null)
                image.GetComponent<AspectRatioFitter>().aspectRatio = (float)texture.width / Mathf.Max(1, texture.height);
            string frameAge = double.IsNaN(age) || double.IsInfinity(age) ? "NO RECENT FRAME" : $"{Math.Max(0, age):F1}s OLD";
            bool fresh = texture != null && live && age >= 0 && age < 2;
            notice.text = fresh ? $"LIVE {name} | {frameAge}" :
                texture == null ? "NO SIGNAL | " + status : $"NO SIGNAL / STALE {frameAge} | {status}";
            notice.color = fresh ? Accent : new Color(0.95f, 0.67f, 0.3f, 1);
        }

        private void RefreshSelections()
        {
            if (_selectedPoseLabel != null)
                _selectedPoseLabel.text = string.IsNullOrEmpty(_selectedPose) ? "Select a pose, then press APPLY POSE." : "SELECTED: " + Pretty(_selectedPose);
            if (_selectedActionLabel != null)
                _selectedActionLabel.text = string.IsNullOrEmpty(_selectedAction) ? "Select an action, then press RUN ACTION." : "SELECTED: " + Pretty(_selectedAction);
            for (int i = 0; i < _poseButtons.Count; i++)
                _poseButtons[i].GetComponent<Image>().color = RobotCommandCatalog.PoseNames[i] == _selectedPose ? Accent * 0.55f : ButtonColor;
            RefreshActionPage();
            if (_canvas != null) Refresh();
        }

        private void ChangeActionPage(int direction)
        {
            int pages = Mathf.Max(1, (RobotCommandCatalog.ActionNames.Length + ActionsPerPage - 1) / ActionsPerPage);
            _actionPage = Mathf.Clamp(_actionPage + direction, 0, pages - 1);
            RefreshActionPage();
        }

        private void RefreshActionPage()
        {
            string[] names = RobotCommandCatalog.ActionNames;
            int pages = Mathf.Max(1, (names.Length + ActionsPerPage - 1) / ActionsPerPage);
            for (int i = 0; i < _actionButtons.Count; i++)
            {
                int index = _actionPage * ActionsPerPage + i;
                var button = _actionButtons[i];
                button.gameObject.SetActive(index < names.Length);
                if (index >= names.Length) continue;
                button.GetComponentInChildren<Text>().text = Pretty(names[index]);
                button.GetComponent<Image>().color = names[index] == _selectedAction ? Accent * 0.55f : ButtonColor;
            }
            if (_actionPageLabel != null) _actionPageLabel.text = $"{_actionPage + 1} / {pages}";
        }

        private void RefreshDevices()
        {
            if (controller == null || _networkInfo == null) return;
            string current = controller.DriveClient == null ? "-" : $"{controller.DriveClient.robotAddress}:{controller.DriveClient.port}";
            string saved = controller.Connection == null ? "-" : $"{controller.Connection.AddressText}:{controller.Connection.PortText}";
            _networkInfo.text = $"Current: {current}\nRecent: {saved}";
            _searchButton.GetComponentInChildren<Text>().text = controller.IsDiscovering ? "SEARCHING" : "SEARCH";
            int count = controller.Devices.Count;
            int pages = Mathf.Max(1, (count + DevicesPerPage - 1) / DevicesPerPage);
            _devicePage = Mathf.Clamp(_devicePage, 0, pages - 1);
            for (int i = 0; i < _deviceButtons.Count; i++)
            {
                int index = _devicePage * DevicesPerPage + i;
                var button = _deviceButtons[i];
                button.interactable = index < count;
                _displayedDevices[i] = index < count ? controller.Devices[index] : null;
                string text = "No device found";
                if (index < count)
                {
                    DiscoveredPi device = _displayedDevices[i];
                    string name = device.Name;
                    if (string.IsNullOrWhiteSpace(name)) name = "PuppyPi";
                    foreach (char character in name)
                        if (character < 32 || character > 126) { name = "PuppyPi"; break; }
                    text = $"{name}  {device.Address}:{device.Port}";
                    if (controller.Connection != null && device.DeviceId == controller.Connection.SavedDeviceId)
                        text = "RECENT  " + text;
                }
                button.GetComponentInChildren<Text>().text = text;
            }
            _devicePageLabel.text = count == 0 ? "Search for a nearby Pi" : $"{count} devices  /  {_devicePage + 1} of {pages}";
        }

        private void TypeAddress(string key)
        {
            string value = _editingPort ? _port : _host;
            if (key == "DEL") value = value.Length == 0 ? "" : value.Substring(0, value.Length - 1);
            else if ((!_editingPort || key != ".") && value.Length < (_editingPort ? 5 : 15)) value += key;
            if (_editingPort) _port = value; else _host = value;
            RefreshAddress();
        }

        private void RefreshAddress()
        {
            if (_hostButton == null) return;
            _hostButton.GetComponentInChildren<Text>().text = "IP: " + _host + (!_editingPort ? "_" : "");
            _portButton.GetComponentInChildren<Text>().text = _port + (_editingPort ? "_" : "");
            _hostButton.GetComponent<Image>().color = !_editingPort ? Accent * 0.55f : ButtonColor;
            _portButton.GetComponent<Image>().color = _editingPort ? Accent * 0.55f : ButtonColor;
        }

        public void Recenter()
        {
            if (PlacementLocked) return;
            var camera = Camera.main;
            if (camera == null) return;
            // Cancel selections before resetting grab offsets, preserving the user's chosen scale.
            if (_panelRoot != null) _panelRoot.SetActive(false);
            Vector3 forward = Vector3.ProjectOnPlane(camera.transform.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
            transform.position = camera.transform.position + right * 0.48f + forward * 0.7f + Vector3.down * 0.08f;
            transform.rotation = Quaternion.LookRotation(transform.position - camera.transform.position, Vector3.up);
            if (_panelRoot != null)
            {
                _panelRoot.transform.localPosition = Vector3.zero;
                _panelRoot.transform.localRotation = Quaternion.identity;
                _panelRoot.SetActive(isActiveAndEnabled);
                Physics.SyncTransforms();
            }
        }

        private void OnEnable()
        {
            if (_panelRoot != null) _panelRoot.SetActive(true);
        }

        private void OnDisable()
        {
            if (_panelRoot != null) _panelRoot.SetActive(false);
        }

        private Button Command(Transform parent, string title, float x, float y, float width, float height,
            Action<PuppyPiRobotController> callback, Func<PuppyPiRobotController, bool> enabled = null, Color? color = null,
            bool allowWhileGrabbed = false, bool allowSupervisedGrab = false)
        {
            var button = Button(parent, title, x, y, width, height, () =>
            {
                if (controller == null || (enabled != null && !enabled(controller))) return;
                bool supervised = allowSupervisedGrab && !controller.StudyActive &&
                    controller.PathClient != null && controller.PathClient.operatorSupervisedNavigation;
                if (IsGrabbed && !allowWhileGrabbed && !supervised) return;
                _localNotice = "";
                callback(controller);
                Refresh();
            }, color, allowWhileGrabbed: true);
            _gated.Add(new KeyValuePair<Button, Func<PuppyPiRobotController, bool>>(button, enabled));
            return button;
        }

        private Button Button(Transform parent, string title, float x, float y, float width, float height,
            Action callback, Color? color = null, bool allowWhileGrabbed = false)
        {
            var rect = Rect(parent, title, x, y, width, height);
            var image = rect.gameObject.AddComponent<Image>();
            image.color = color ?? ButtonColor;
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.2f, 1.2f, 1.2f, 1);
            colors.pressedColor = new Color(0.55f, 0.85f, 0.85f, 1);
            colors.disabledColor = new Color(0.45f, 0.45f, 0.45f, 0.65f);
            button.colors = colors;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            Label(rect, title, 7, 3, width - 14, height - 6, 17, Color.white, TextAnchor.MiddleCenter);
            // Hover only changes the highlight. Commands require one completed button click.
            button.onClick.AddListener(() =>
            {
                if (!allowWhileGrabbed && IsGrabbed) return;
                callback();
            });
            return button;
        }

        private Text Label(Transform parent, string text, float x, float y, float width, float height,
            int size, Color color, TextAnchor alignment = TextAnchor.UpperLeft)
        {
            var rect = Rect(parent, "Label", x, y, width, height);
            var label = rect.gameObject.AddComponent<Text>();
            label.font = _font;
            label.fontSize = size;
            label.text = text;
            label.color = color;
            label.alignment = alignment;
            label.supportRichText = false;
            label.raycastTarget = false;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Truncate;
            return label;
        }

        private static RectTransform Rect(Transform parent, string name, float x, float y, float width, float height)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0, 1);
            rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
            return rect;
        }

        private static string Pretty(string name) => name.Replace('_', ' ').ToUpperInvariant();
    }
}
