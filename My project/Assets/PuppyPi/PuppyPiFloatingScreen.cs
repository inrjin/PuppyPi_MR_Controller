using System;
using System.Globalization;
using Oculus.Interaction;
using Oculus.Interaction.Grab;
using Oculus.Interaction.HandGrab;
using UnityEngine;
using UnityEngine.UI;

namespace PuppyPi
{
    // This view owns an independent world-space window, never the cockpit or
    // controller transform. Grabbing the rim cannot move the robot joystick.
    public sealed class PuppyPiFloatingScreen : MonoBehaviour
    {
        public PuppyPiRobotController controller;
        public Transform head;
        [Range(0.1f, 1f)] public float minimumScale = 0.35f;
        [Range(1f, 4f)] public float maximumScale = 2f;
        public Transform ScreenRoot => _screenRoot != null ? _screenRoot.transform : null;
        public RawImage CameraView { get; private set; }
        public Text NoSignalLabel { get; private set; }
        public Text StatusLabel { get; private set; }
        public bool IsVisible => _screenRoot != null && _screenRoot.activeInHierarchy;
        public bool IsGrabbed => IsVisible && _grabbable != null && _grabbable.SelectingPoints != null &&
            _grabbable.SelectingPointsCount > 0;

        private const double SignalTimeoutSeconds = 2;
        private const float WorldUnitsPerPixel = 0.001f;
        private GameObject _screenRoot;
        private Grabbable _grabbable;
        private AspectRatioFitter _aspect;
        private Image[] _rim;
        private Font _font;
        private float _nextStatusUpdate;
        private bool _lastSignal;
        private bool _lastGrabbed;
        private int _lastWidth, _lastHeight;

        public void ShowAndRecenter()
        {
            EnsureWindow();
            // Disabling the Grabbable cancels existing selections before its
            // grab offsets become invalid after recentering.
            _screenRoot.SetActive(false);
            Transform viewer = ResolveHead();
            Vector3 origin = viewer != null ? viewer.position : transform.position;
            Vector3 forward = viewer != null ? Vector3.ProjectOnPlane(viewer.forward, Vector3.up) : Vector3.forward;
            if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
            forward.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
            Vector3 position = origin + forward * 0.68f - right * 0.42f - Vector3.up * 0.08f;
            _screenRoot.transform.SetPositionAndRotation(position, Quaternion.LookRotation(position - origin, Vector3.up));
            _screenRoot.SetActive(true);
            Physics.SyncTransforms();
            RefreshFromController();
        }

        // A display-only entry point, also useful when previewing a stored frame.
        // The stream owns the texture; this window never destroys it.
        public void SetFrame(Texture texture, bool live, double ageSeconds, string status)
        {
            if (_screenRoot == null) return;
            if (CameraView.texture != texture) CameraView.texture = texture;
            if (texture != null && texture.width > 0 && texture.height > 0 &&
                (_lastWidth != texture.width || _lastHeight != texture.height))
            {
                _lastWidth = texture.width;
                _lastHeight = texture.height;
                _aspect.aspectRatio = (float)texture.width / texture.height;
            }
            bool signal = texture != null && live && !double.IsNaN(ageSeconds) &&
                !double.IsInfinity(ageSeconds) && ageSeconds >= 0 && ageSeconds < SignalTimeoutSeconds;
            CameraView.color = signal ? Color.white : new Color(0.25f, 0.25f, 0.25f, 1);
            NoSignalLabel.gameObject.SetActive(!signal);
            if (signal != _lastSignal || Time.unscaledTime >= _nextStatusUpdate)
            {
                _lastSignal = signal;
                _nextStatusUpdate = Time.unscaledTime + 0.2f;
                string age = double.IsNaN(ageSeconds) || double.IsInfinity(ageSeconds) || ageSeconds < 0 ? "--" :
                    ageSeconds.ToString("F1", CultureInfo.InvariantCulture) + " s";
                string detail = string.IsNullOrEmpty(status) ? "" : " | " + (status.Length > 70 ? status.Substring(0, 70) : status);
                StatusLabel.text = (signal ? "LIVE" : "NO SIGNAL") + " | Last frame " + age + detail;
            }
        }

        private Transform ResolveHead()
        {
            if (head != null) return head;
            if (Camera.main != null) return Camera.main.transform;
            foreach (Camera camera in FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                if (camera.isActiveAndEnabled && camera.stereoEnabled) return camera.transform;
            return null;
        }

        private void LateUpdate()
        {
            if (!IsVisible) return;
            RefreshFromController();
            bool grabbed = IsGrabbed;
            if (grabbed == _lastGrabbed) return;
            _lastGrabbed = grabbed;
            foreach (Image rim in _rim) rim.color = grabbed ? new Color(0.12f, 0.65f, 0.8f, 1) : new Color(0.08f, 0.25f, 0.32f, 1);
        }

        private void RefreshFromController()
        {
            if (controller != null) SetFrame(controller.CameraTexture, controller.CameraLive, controller.CameraFrameAgeSeconds, controller.CameraStatus);
        }

        private void EnsureWindow()
        {
            if (_screenRoot != null) return;
            _screenRoot = new GameObject("PuppyPi floating camera screen");
            _screenRoot.SetActive(false);
            var body = _screenRoot.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.isKinematic = true;
            AddHandle("Top grip", new Vector3(0, 0.26f, 0), new Vector3(0.84f, 0.04f, 0.035f));
            AddHandle("Bottom grip", new Vector3(0, -0.26f, 0), new Vector3(0.84f, 0.04f, 0.035f));
            AddHandle("Left grip", new Vector3(-0.4f, 0, 0), new Vector3(0.04f, 0.48f, 0.035f));
            AddHandle("Right grip", new Vector3(0.4f, 0, 0), new Vector3(0.04f, 0.48f, 0.035f));
            ConfigureGrab(body);
            BuildDisplay();
            SetFrame(null, false, double.PositiveInfinity, "Waiting for camera");
        }

        private void AddHandle(string name, Vector3 position, Vector3 size)
        {
            var handle = new GameObject(name);
            handle.transform.SetParent(_screenRoot.transform, false);
            handle.transform.localPosition = position;
            var collider = handle.AddComponent<BoxCollider>();
            collider.size = size;
            // Meta's collision registry uses trigger overlaps. These handles do
            // not push against the cockpit's physical joystick.
            collider.isTrigger = true;
        }

        private void ConfigureGrab(Rigidbody body)
        {
            _grabbable = _screenRoot.AddComponent<Grabbable>();
            _grabbable.InjectOptionalRigidbody(body);
            _grabbable.InjectOptionalTargetTransform(_screenRoot.transform);
            _grabbable.InjectOptionalKinematicWhileSelected(true);
            _grabbable.InjectOptionalThrowWhenUnselected(false);
            _grabbable.TransferOnSecondSelection = false;
            _grabbable.MaxGrabPoints = 2;
#pragma warning disable CS0618 // Use the installed SDK's canonical one/two-grab transformers.
            var oneHand = _screenRoot.AddComponent<OneGrabFreeTransformer>();
            var twoHands = _screenRoot.AddComponent<TwoGrabFreeTransformer>();
            twoHands.InjectOptionalConstraints(new TwoGrabFreeTransformer.TwoGrabFreeConstraints {
                ConstraintsAreRelative = false,
                MinScale = new FloatConstraint { Constrain = true, Value = Mathf.Clamp(minimumScale, 0.1f, 1f) },
                MaxScale = new FloatConstraint { Constrain = true, Value = Mathf.Clamp(maximumScale, 1f, 4f) },
                ConstrainXScale = true, ConstrainYScale = true, ConstrainZScale = true
            });
            _grabbable.InjectOptionalOneGrabTransformer(oneHand);
            _grabbable.InjectOptionalTwoGrabTransformer(twoHands);
#pragma warning restore CS0618
            var handGrab = _screenRoot.AddComponent<HandGrabInteractable>();
            handGrab.InjectRigidbody(body);
            handGrab.InjectSupportedGrabTypes(GrabTypeFlags.All);
            handGrab.InjectOptionalPointableElement(_grabbable);
            handGrab.HandAlignment = HandAlignType.None;
            handGrab.ResetGrabOnGrabsUpdated = false;
            var controllerGrab = _screenRoot.AddComponent<GrabInteractable>();
            controllerGrab.InjectRigidbody(body);
            controllerGrab.InjectOptionalPointableElement(_grabbable);
            controllerGrab.ResetGrabOnGrabsUpdated = false;
            controllerGrab.UseClosestPointAsGrabSource = true;
        }

        private void BuildDisplay()
        {
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            RectTransform canvasRect = Rect("Camera display", _screenRoot.transform, new Vector2(840, 560), Vector2.zero);
            canvasRect.localScale = Vector3.one * WorldUnitsPerPixel;
            var canvas = canvasRect.gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 40;
            canvas.worldCamera = Camera.main;
            Image background = canvasRect.gameObject.AddComponent<Image>();
            background.color = new Color(0.015f, 0.025f, 0.035f, 1);
            background.raycastTarget = false;
            _rim = new[] {
                Rim(canvasRect, "Top rim", new Vector2(840, 40), new Vector2(0, 260)),
                Rim(canvasRect, "Bottom rim", new Vector2(840, 40), new Vector2(0, -260)),
                Rim(canvasRect, "Left rim", new Vector2(40, 480), new Vector2(-400, 0)),
                Rim(canvasRect, "Right rim", new Vector2(40, 480), new Vector2(400, 0))
            };
            Text title = Label(canvasRect, "Camera title", new Vector2(780, 36), new Vector2(0, 260), 18);
            title.text = "CAMERA | Grab rim to move | Two hands to resize";
            RectTransform imageArea = Rect("Camera viewport", canvasRect, new Vector2(760, 440), Vector2.zero);
            RectTransform imageRect = Rect("Camera image", imageArea, Vector2.zero, Vector2.zero);
            CameraView = imageRect.gameObject.AddComponent<RawImage>();
            CameraView.raycastTarget = false;
            _aspect = imageRect.gameObject.AddComponent<AspectRatioFitter>();
            _aspect.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            _aspect.aspectRatio = 16f / 9f;
            NoSignalLabel = Label(imageArea, "Camera no signal", new Vector2(700, 100), Vector2.zero, 36);
            NoSignalLabel.text = "NO SIGNAL";
            StatusLabel = Label(canvasRect, "Camera status", new Vector2(780, 38), new Vector2(0, -260), 15);
        }

        private static RectTransform Rect(string name, Transform parent, Vector2 size, Vector2 position)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
            return rect;
        }

        private static Image Rim(Transform parent, string name, Vector2 size, Vector2 position)
        {
            var rim = Rect(name, parent, size, position).gameObject.AddComponent<Image>();
            rim.color = new Color(0.08f, 0.25f, 0.32f, 1);
            rim.raycastTarget = false;
            return rim;
        }

        private Text Label(Transform parent, string name, Vector2 size, Vector2 position, int fontSize)
        {
            var label = Rect(name, parent, size, position).gameObject.AddComponent<Text>();
            label.font = _font;
            label.fontSize = fontSize;
            label.color = Color.white;
            label.alignment = TextAnchor.MiddleCenter;
            label.raycastTarget = false;
            label.supportRichText = false;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Truncate;
            return label;
        }

        private void OnDisable()
        {
            if (_screenRoot != null) _screenRoot.SetActive(false);
        }

        private void OnDestroy()
        {
            if (_screenRoot == null) return;
            if (Application.isPlaying) Destroy(_screenRoot);
            else DestroyImmediate(_screenRoot);
        }
    }
}
