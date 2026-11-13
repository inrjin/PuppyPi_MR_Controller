using System;
using Oculus.Interaction;
using Oculus.Interaction.HandGrab;
using Oculus.Interaction.Input;
using UnityEngine;

namespace PuppyPi
{
    [DefaultExecutionOrder(-100)]
    public sealed class BallGripInput : MonoBehaviour, IRobotDriveInput
    {
        public Transform grip;
        public Transform baseFrame;
        public Grabbable grabbable;
        [Range(5, 60)] public float maxTiltDegrees = 40;
        [Range(0, 20)] public float deadzoneDegrees = 5;
        [Range(0.1f, 3)] public float sensitivity = 1;
        public bool invertForward;
        public bool invertTurn;
#if UNITY_EDITOR
        [Header("Editor-only local/loopback preview")]
        public bool editorPreview;
        public bool previewGrabbed;
        public bool previewTracking = true;
        public Vector2 previewTilt;
#endif

        public Vector2 Tilt { get; private set; }
        public Vector2 Axes { get; private set; }
        public bool Grabbed { get; private set; }
        public bool TrackingValid { get; private set; }
        public bool ControlActive => isActiveAndEnabled && Grabbed && TrackingValid && !_blocked;
        public Vector2 RawAxes => Tilt;
        public Vector2 RequestedAxes => new Vector2(
            BallGripMath.Normalize(Tilt.x, deadzoneDegrees, maxTiltDegrees, sensitivity) * (invertForward ? -1 : 1),
            BallGripMath.Normalize(Tilt.y, deadzoneDegrees, maxTiltDegrees, sensitivity) * (invertTurn ? -1 : 1));
        public bool Engaged => Grabbed;
        public bool InputValid => TrackingValid && _focused && !_paused;
        public bool Focused => _focused;
        public bool Paused => _paused;
        public string InterfaceId => "mr_ballgrip";
        public string RawUnits => "degrees";
        public string InputModality => IsSimulation ? "editor_preview" :
            _source is HandGrabInteractor ? "hand_tracking" : _source is GrabInteractor ? "controller_grab" : "none";
        public bool IsSimulation
        {
            get
            {
#if UNITY_EDITOR
                return editorPreview;
#else
                return false;
#endif
            }
        }
        public event Action NeutralRequested;
        public event Action<bool> GrabChanged;
        public Quaternion Neutral { get; private set; }
        private Component _source;
        private int _pointer = -1;
        private bool _blocked;
        private bool _focused = true;
        private bool _paused;

        private void Awake()
        {
            if (grip != null && baseFrame != null)
                Neutral = Quaternion.Inverse(baseFrame.rotation) * grip.rotation;
        }

        private void OnEnable()
        {
            if (grabbable != null) grabbable.WhenPointerEventRaised += OnPointer;
        }

        private void OnDisable()
        {
            if (grabbable != null) grabbable.WhenPointerEventRaised -= OnPointer;
            Release();
        }

        private void OnPointer(PointerEvent evt)
        {
            if (evt.Type == PointerEventType.Select)
            {
                // Transfers count as a release followed by a new selection.
                if (Grabbed) Release();
                _source = evt.Data as Component;
                _pointer = evt.Identifier;
                Grabbed = true;
                TrackingValid = SourceTracked();
                _blocked = !_focused || _paused || !TrackingValid;
                GrabChanged?.Invoke(true);
            }
            else if (evt.Identifier == _pointer &&
                     (evt.Type == PointerEventType.Unselect || evt.Type == PointerEventType.Cancel))
                Release();
        }

        private bool SourceTracked()
        {
            if (_source == null || !_source.gameObject.activeInHierarchy) return false;
            if (_source is HandGrabInteractor handGrab)
            {
                IHand hand = handGrab.Hand;
                return handGrab.isActiveAndEnabled && hand != null && hand.IsConnected &&
                    hand.IsTrackedDataValid && hand.IsHighConfidence && hand.GetRootPose(out _);
            }
            if (_source is GrabInteractor controllerGrab && controllerGrab.isActiveAndEnabled)
            {
                ControllerSelector selector = controllerGrab.GetComponentInChildren<ControllerSelector>();
                if (selector != null)
                {
                    IController controller = selector.Controller;
                    return controller != null && controller.IsConnected && controller.IsPoseValid && controller.TryGetPose(out _);
                }
            }
            // Unknown selectors fail closed rather than pretending tracking is valid.
            return false;
        }

        private void Update()
        {
            bool tracked = Grabbed && SourceTracked();
#if UNITY_EDITOR
            if (editorPreview)
            {
                if (Grabbed != previewGrabbed)
                {
                    if (!previewGrabbed) Release();
                    else
                    {
                        Grabbed = true;
                        _blocked = !_focused || _paused || !previewTracking;
                        GrabChanged?.Invoke(true);
                    }
                }
                tracked = previewGrabbed && previewTracking;
            }
#endif
            if (Grabbed && TrackingValid && !tracked) Invalidate();
            TrackingValid = tracked;
            if (grip == null || baseFrame == null) { Invalidate(); return; }
            Quaternion relative = Quaternion.Inverse(Neutral) * Quaternion.Inverse(baseFrame.rotation) * grip.rotation;
            Tilt = BallGripMath.TiltDegrees(relative);
            Axes = ControlActive ? RequestedAxes : Vector2.zero;
        }

        public void Invalidate()
        {
            _blocked = true;
            Axes = Vector2.zero;
            NeutralRequested?.Invoke();
        }

        private void Release()
        {
            Grabbed = false;
            TrackingValid = false;
            _source = null;
            _pointer = -1;
            Invalidate();
            GrabChanged?.Invoke(false);
        }

        private void OnApplicationPause(bool paused)
        {
            _paused = paused;
            if (paused) Invalidate();
        }

        private void OnApplicationFocus(bool focused)
        {
            _focused = focused;
            if (!focused) Invalidate();
        }
    }
}
