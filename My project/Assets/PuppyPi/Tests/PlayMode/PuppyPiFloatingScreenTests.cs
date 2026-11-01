#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Oculus.Interaction;
using Oculus.Interaction.HandGrab;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace PuppyPi.Tests
{
    public class PuppyPiFloatingScreenTests
    {
        private GameObject _owner;
        private PuppyPiFloatingScreen _screen;
        private readonly List<Texture2D> _textures = new List<Texture2D>();

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            if (_screen != null && _screen.ScreenRoot != null) Object.Destroy(_screen.ScreenRoot.gameObject);
            if (_owner != null) Object.Destroy(_owner);
            foreach (Texture2D texture in _textures) if (texture != null) Object.Destroy(texture);
            _textures.Clear();
            yield return null;
        }

        private PuppyPiFloatingScreen CreateWindow()
        {
            _owner = new GameObject("Independent floating screen owner");
            var head = new GameObject("Head");
            head.transform.SetParent(_owner.transform, false);
            head.transform.position = new Vector3(1, 1.6f, -2);
            head.transform.rotation = Quaternion.Euler(0, 30, 0);
            _screen = _owner.AddComponent<PuppyPiFloatingScreen>();
            _screen.head = head.transform;
            _screen.ShowAndRecenter();
            return _screen;
        }

        private static void Pointer(Grabbable grab, int id, PointerEventType kind, Pose pose) =>
            grab.ProcessPointerEvent(new PointerEvent(id, kind, pose));

        private static void Select(Grabbable grab, int id, Pose pose)
        {
            Pointer(grab, id, PointerEventType.Hover, pose);
            Pointer(grab, id, PointerEventType.Select, pose);
        }

        [UnityTest]
        public IEnumerator FrameMovesWithOneGrabScalesUniformlyWithTwoAndLeavesCockpitIndependent()
        {
            PuppyPiFloatingScreen screen = CreateWindow();
            var joystick = new GameObject("Unrelated cockpit joystick");
            joystick.transform.SetParent(_owner.transform, false);
            joystick.transform.localPosition = new Vector3(0.3f, 0.4f, 0.5f);
            Vector3 ownerPosition = _owner.transform.position;
            Quaternion ownerRotation = _owner.transform.rotation;
            Vector3 joystickPosition = joystick.transform.position;
            yield return null;

            Transform window = screen.ScreenRoot;
            Assert.That(window.parent, Is.Null, "the window must not inherit cockpit movement or scale");
            var body = window.GetComponent<Rigidbody>();
            Assert.That(body.isKinematic, Is.True);
            Assert.That(body.useGravity, Is.False);
            var grab = window.GetComponent<Grabbable>();
            Assert.That(window.GetComponent<HandGrabInteractable>().PointableElement, Is.SameAs(grab));
            Assert.That(window.GetComponent<GrabInteractable>().PointableElement, Is.SameAs(grab));
            foreach (Collider collider in window.GetComponentsInChildren<Collider>())
            {
                Assert.That(collider.isTrigger, Is.True, "the frame must not push the physical joystick");
                Assert.That(collider.bounds.Contains(window.position), Is.False, "the video center must not become a grab target");
            }
            foreach (Graphic graphic in window.GetComponentsInChildren<Graphic>(true))
                Assert.That(graphic.raycastTarget, Is.False, "camera graphics must not dispatch robot control clicks");

            Vector3 initialPosition = window.position;
            Quaternion initialRotation = window.rotation;
            Pose first = new Pose(initialPosition - window.right * 0.4f, initialRotation);
            Select(grab, 101, first);
            Assert.That(screen.IsGrabbed, Is.True);
            Pose moved = new Pose(first.position + new Vector3(0.2f, 0.15f, -0.1f), Quaternion.Euler(10, 25, 0) * first.rotation);
            Pointer(grab, 101, PointerEventType.Move, moved);
            Assert.That(Vector3.Distance(window.position, initialPosition), Is.GreaterThan(0.05));
            Assert.That(Quaternion.Angle(window.rotation, initialRotation), Is.GreaterThan(10));
            Assert.That(window.localScale, Is.EqualTo(Vector3.one));
            Pointer(grab, 101, PointerEventType.Unselect, moved);
            Vector3 releasedPosition = window.position;
            yield return new WaitForSeconds(0.1f);
            Assert.That(Vector3.Distance(window.position, releasedPosition), Is.LessThan(0.001), "releasing the screen must not throw it");

            screen.ShowAndRecenter();
            Vector3 center = window.position;
            Vector3 right = window.right;
            Pose leftGrab = new Pose(center - right * 0.2f, window.rotation);
            Pose rightGrab = new Pose(center + right * 0.2f, window.rotation);
            Select(grab, 201, leftGrab);
            Select(grab, 202, rightGrab);
            Assert.That(grab.SelectingPointsCount, Is.EqualTo(2), "second hand must join, not transfer the grab");
            rightGrab.position = center + right * 2;
            Pointer(grab, 202, PointerEventType.Move, rightGrab);
            Assert.That(window.localScale.x, Is.EqualTo(2).Within(0.001), "two-hand enlargement must respect the upper bound");
            Assert.That(window.localScale.y, Is.EqualTo(window.localScale.x).Within(0.0001));
            Assert.That(window.localScale.z, Is.EqualTo(window.localScale.x).Within(0.0001));
            rightGrab.position = leftGrab.position + right * 0.01f;
            Pointer(grab, 202, PointerEventType.Move, rightGrab);
            Assert.That(window.localScale.x, Is.EqualTo(0.35f).Within(0.001), "two-hand shrinking must respect the lower bound");
            Assert.That(window.localScale.y, Is.EqualTo(window.localScale.x).Within(0.0001));
            Assert.That(window.localScale.z, Is.EqualTo(window.localScale.x).Within(0.0001));
            Pointer(grab, 201, PointerEventType.Unselect, leftGrab);
            Pointer(grab, 202, PointerEventType.Unselect, rightGrab);
            Assert.That(screen.IsGrabbed, Is.False);
            Assert.That(_owner.transform.position, Is.EqualTo(ownerPosition));
            Assert.That(_owner.transform.rotation, Is.EqualTo(ownerRotation));
            Assert.That(joystick.transform.position, Is.EqualTo(joystickPosition));

            Vector3 windowPosition = window.position;
            _owner.transform.position += Vector3.one;
            Assert.That(window.position, Is.EqualTo(windowPosition), "moving or summoning the owner must not carry the camera window");
        }

        [UnityTest]
        public IEnumerator ImageKeepsNativeAspectAcrossResolutionAndScaleChangesAndMarksOldFrames()
        {
            PuppyPiFloatingScreen screen = CreateWindow();
            var texture = new Texture2D(160, 90);
            _textures.Add(texture);
            screen.SetFrame(texture, true, 0.1, "Camera live");
            yield return null;
            Canvas.ForceUpdateCanvases();
            var aspect = screen.CameraView.GetComponent<AspectRatioFitter>();
            Assert.That(aspect.aspectMode, Is.EqualTo(AspectRatioFitter.AspectMode.FitInParent));
            Assert.That(aspect.aspectRatio, Is.EqualTo(16f / 9f).Within(0.0001));
            Vector2 size = screen.CameraView.rectTransform.rect.size;
            Assert.That(size.x / size.y, Is.EqualTo(16f / 9f).Within(0.001));
            Assert.That(screen.NoSignalLabel.gameObject.activeSelf, Is.False);

            screen.ScreenRoot.localScale = Vector3.one * 1.7f;
            Assert.That(texture.Reinitialize(90, 160), Is.True);
            screen.SetFrame(texture, true, 0.1, "Portrait source");
            Canvas.ForceUpdateCanvases();
            Assert.That(aspect.aspectRatio, Is.EqualTo(9f / 16f).Within(0.0001), "dimensions can change without a new Texture object");
            size = screen.CameraView.rectTransform.rect.size;
            Assert.That(size.x / size.y, Is.EqualTo(9f / 16f).Within(0.001));
            Assert.That(screen.CameraView.uvRect, Is.EqualTo(new Rect(0, 0, 1, 1)), "preserving aspect must not crop the source");

            screen.SetFrame(texture, true, 2.1, "Frame stalled");
            Assert.That(screen.NoSignalLabel.gameObject.activeSelf, Is.True);
            Assert.That(screen.StatusLabel.text, Does.Contain("NO SIGNAL"));
            Assert.That(screen.StatusLabel.text, Does.Contain("2.1 s"));
            Assert.That(screen.CameraView.texture, Is.SameAs(texture), "the stale last frame may remain visible behind its warning");
            screen.SetFrame(texture, false, 0.01, "Stream paused");
            Assert.That(screen.NoSignalLabel.gameObject.activeSelf, Is.True);
            screen.SetFrame(null, false, double.PositiveInfinity, "Disconnected");
            Assert.That(screen.CameraView.texture, Is.Null);
            Assert.That(screen.NoSignalLabel.gameObject.activeSelf, Is.True);
        }
    }
}
#endif
