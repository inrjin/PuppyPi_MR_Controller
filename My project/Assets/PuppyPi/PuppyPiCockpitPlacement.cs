using Oculus.Interaction;
using Oculus.Interaction.Grab;
using Oculus.Interaction.HandGrab;
using UnityEngine;
using UnityEngine.UI;

namespace PuppyPi
{
    // A dedicated grab target for moving the entire console. Its Rigidbody owns
    // only the two cyan bars, never the central driving grip's colliders.
    public sealed class PuppyPiCockpitPlacement : MonoBehaviour, ITransformer
    {
        public Transform cockpitRoot;
        public BallGripInput input;
        public Vector3 handleOffsetMeters = new Vector3(0.065f, 0, 0.008f);
        public Vector3 handleSizeMeters = new Vector3(0.025f, 0.09f, 0.025f);
        public Transform HandleRoot => _handleRoot != null ? _handleRoot.transform : null;
        public Grabbable HandleGrabbable => _handleGrab;
        public bool IsGrabbed => _selected && isActiveAndEnabled && _handleRoot != null && _handleRoot.activeInHierarchy;

        private GameObject _handleRoot;
        private Grabbable _handleGrab;
        private IGrabbable _grabbable;
        private Material _material;
        private bool _selected;
        private Pose _grabStart;
        private Pose _rootStart;

        private void Start() => EnsureHandle();

        public void EnsureHandle()
        {
            if (_handleRoot != null) return;
            if (input == null) input = GetComponent<BallGripInput>();
            if (cockpitRoot == null) cockpitRoot = input != null && input.baseFrame != null ? input.baseFrame : transform;
            Vector3 scale = cockpitRoot.lossyScale;
            if (!Finite(scale) || Mathf.Abs(scale.x) < 0.000001f || Mathf.Abs(scale.y) < 0.000001f || Mathf.Abs(scale.z) < 0.000001f) return;
            if (!Finite(handleOffsetMeters) || !Finite(handleSizeMeters) ||
                handleSizeMeters.x <= 0 || handleSizeMeters.y <= 0 || handleSizeMeters.z <= 0) return;

            _handleRoot = new GameObject("Console body move handles");
            _handleRoot.SetActive(false);
            _handleRoot.transform.SetParent(cockpitRoot, false);
            _handleRoot.transform.localScale = new Vector3(1 / scale.x, 1 / scale.y, 1 / scale.z);
            var body = _handleRoot.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Unlit/Color");
            if (shader != null)
            {
                _material = new Material(shader);
                if (_material.HasProperty("_BaseColor")) _material.SetColor("_BaseColor", new Color(0.05f, 0.85f, 0.9f, 1));
                if (_material.HasProperty("_Color")) _material.SetColor("_Color", new Color(0.05f, 0.85f, 0.9f, 1));
            }
            Vector3 right = handleOffsetMeters;
            right.x = Mathf.Abs(right.x);
            Vector3 left = right;
            left.x = -left.x;
            AddBar("Right console move bar", right);
            AddBar("Left console move bar", left);
            AddCaption();

            _handleGrab = _handleRoot.AddComponent<Grabbable>();
            _handleGrab.InjectOptionalRigidbody(body);
            _handleGrab.InjectOptionalTargetTransform(_handleRoot.transform);
            _handleGrab.InjectOptionalKinematicWhileSelected(true);
            _handleGrab.InjectOptionalThrowWhenUnselected(false);
            _handleGrab.InjectOptionalOneGrabTransformer(this);
            _handleGrab.InjectOptionalTwoGrabTransformer(null);
            _handleGrab.MaxGrabPoints = 1;
            _handleGrab.TransferOnSecondSelection = true;

            var hand = _handleRoot.AddComponent<HandGrabInteractable>();
            hand.InjectRigidbody(body);
            hand.InjectSupportedGrabTypes(GrabTypeFlags.All);
            hand.InjectOptionalPointableElement(_handleGrab);
            hand.HandAlignment = HandAlignType.None;
            hand.ResetGrabOnGrabsUpdated = false;
            var controller = _handleRoot.AddComponent<GrabInteractable>();
            controller.InjectRigidbody(body);
            controller.InjectOptionalPointableElement(_handleGrab);
            controller.UseClosestPointAsGrabSource = true;
            controller.ResetGrabOnGrabsUpdated = false;
            _handleRoot.SetActive(isActiveAndEnabled);
            Physics.SyncTransforms();
        }

        private void AddBar(string name, Vector3 offset)
        {
            var bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bar.name = name;
            bar.transform.SetParent(_handleRoot.transform, false);
            bar.transform.localPosition = offset;
            bar.transform.localScale = handleSizeMeters;
            bar.GetComponent<BoxCollider>().isTrigger = true;
            if (_material != null) bar.GetComponent<MeshRenderer>().sharedMaterial = _material;
        }

        private void AddCaption()
        {
            var label = new GameObject("Move console label", typeof(RectTransform), typeof(Canvas));
            label.transform.SetParent(_handleRoot.transform, false);
            label.transform.localPosition = new Vector3(0, handleOffsetMeters.y, handleOffsetMeters.z + 0.035f);
            label.transform.localRotation = Quaternion.Euler(90, 0, 0);
            label.transform.localScale = Vector3.one * 0.001f;
            ((RectTransform)label.transform).sizeDelta = new Vector2(145, 28);
            label.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            var textObject = new GameObject("MOVE CONSOLE", typeof(RectTransform));
            textObject.transform.SetParent(label.transform, false);
            var rect = (RectTransform)textObject.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.sizeDelta = Vector2.zero;
            var text = textObject.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.text = "MOVE CONSOLE";
            text.fontSize = 17;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = new Color(0.15f, 1, 1, 1);
            text.raycastTarget = false;
        }

        public void Initialize(IGrabbable grabbable) => _grabbable = grabbable;

        public void BeginTransform()
        {
            if (cockpitRoot == null || _grabbable == null || _grabbable.GrabPoints.Count == 0) return;
            _grabStart = _grabbable.GrabPoints[0];
            _rootStart = new Pose(cockpitRoot.position, cockpitRoot.rotation);
            _selected = true;
            if (input != null) input.Invalidate();
        }

        public void UpdateTransform()
        {
            if (!IsGrabbed || _grabbable == null || _grabbable.GrabPoints.Count == 0) return;
            Pose current = _grabbable.GrabPoints[0];
            if (!ValidPose(current) || !ValidPose(_grabStart)) return;
            Quaternion delta = current.rotation.normalized * Quaternion.Inverse(_grabStart.rotation.normalized);
            MoveTo(new Pose(current.position + delta * (_rootStart.position - _grabStart.position), delta * _rootStart.rotation));
        }

        public void EndTransform()
        {
            _selected = false;
            if (input != null) input.Invalidate();
        }

        // A synchronous teleport: no physics simulation occurs between these
        // snapshots and assignments. Every body receives the same rigid delta,
        // so authored grip/joint poses and the root's -90-degree tilt survive.
        public bool MoveTo(Pose target)
        {
            if (cockpitRoot == null || !ValidPose(target)) return false;
            target.rotation = target.rotation.normalized;
            Vector3 oldPosition = cockpitRoot.position;
            Quaternion delta = target.rotation * Quaternion.Inverse(cockpitRoot.rotation);
            var bodies = cockpitRoot.GetComponentsInChildren<Rigidbody>(true);
            var positions = new Vector3[bodies.Length];
            var rotations = new Quaternion[bodies.Length];
            for (int i = 0; i < bodies.Length; i++)
            {
                positions[i] = bodies[i].position;
                rotations[i] = bodies[i].rotation;
            }
            if (input != null) input.Invalidate();
            cockpitRoot.SetPositionAndRotation(target.position, target.rotation);
            for (int i = 0; i < bodies.Length; i++)
            {
                bodies[i].position = target.position + delta * (positions[i] - oldPosition);
                bodies[i].rotation = delta * rotations[i];
                if (!bodies[i].isKinematic)
                {
                    bodies[i].linearVelocity = Vector3.zero;
                    bodies[i].angularVelocity = Vector3.zero;
                }
            }
            Physics.SyncTransforms();
            return true;
        }

        public void CancelGrab()
        {
            _selected = false;
            if (_handleRoot != null)
            {
                bool active = _handleRoot.activeSelf;
                _handleRoot.SetActive(false);
                if (active && isActiveAndEnabled) _handleRoot.SetActive(true);
            }
            if (input != null) input.Invalidate();
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static bool ValidPose(Pose value)
        {
            Quaternion rotation = value.rotation;
            float length = rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w;
            return Finite(value.position) && Finite(rotation.x) && Finite(rotation.y) && Finite(rotation.z) && Finite(rotation.w) &&
                Finite(length) && length > 0.00000001f;
        }

        private void OnEnable()
        {
            if (_handleRoot != null) _handleRoot.SetActive(true);
        }

        private void OnDisable()
        {
            _selected = false;
            if (_handleRoot != null) _handleRoot.SetActive(false);
            if (input != null) input.Invalidate();
        }

        private void OnDestroy()
        {
            if (_handleRoot != null)
            {
                if (Application.isPlaying) Destroy(_handleRoot);
                else DestroyImmediate(_handleRoot);
            }
            if (_material != null)
            {
                if (Application.isPlaying) Destroy(_material);
                else DestroyImmediate(_material);
            }
        }
    }
}
