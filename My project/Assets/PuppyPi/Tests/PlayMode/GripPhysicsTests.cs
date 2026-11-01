#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using Oculus.Interaction;
using Oculus.Interaction.GrabAPI;
using Oculus.Interaction.HandGrab;
using Oculus.Interaction.Input;
using UnityEngine;
using UnityEngine.TestTools;

namespace PuppyPi.Tests
{
    // Supplies tracking without requiring headset cameras or gesture recognition.
    public sealed class PhysicsTestHandSource : MonoBehaviour, IHand
    {
        public bool valid = true;
        public Pose rootPose = Pose.identity;
        public Handedness Handedness => Handedness.Right;
        public bool IsConnected => valid;
        public bool IsHighConfidence => valid;
        public bool IsDominantHand => true;
        public float Scale => 1;
        public bool IsPointerPoseValid => valid;
        public bool IsTrackedDataValid => valid;
        public int CurrentDataVersion => 0;
        public bool GetFingerIsPinching(HandFinger finger) => false;
        public bool GetIndexFingerIsPinching() => false;
        public bool GetFingerIsHighConfidence(HandFinger finger) => valid;
        public float GetFingerPinchStrength(HandFinger finger) => 0;
        public bool GetRootPose(out Pose pose) { pose = rootPose; return valid; }
        public bool GetPointerPose(out Pose pose) => GetRootPose(out pose);
        public bool GetJointPose(HandJointId joint, out Pose pose) => GetRootPose(out pose);
        public bool GetJointPoseLocal(HandJointId joint, out Pose pose) { pose = Pose.identity; return false; }
        public bool GetJointPoseFromWrist(HandJointId joint, out Pose pose) { pose = Pose.identity; return false; }
        public bool GetPalmPoseLocal(out Pose pose) { pose = Pose.identity; return false; }
        public bool GetJointPosesLocal(out ReadOnlyHandJointPoses poses) { poses = default; return false; }
        public bool GetJointPosesFromWrist(out ReadOnlyHandJointPoses poses) { poses = default; return false; }
        public event System.Action WhenHandUpdated { add { } remove { } }
    }

    public class GripPhysicsTests
    {
        private GameObject _root;

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            if (_root != null) Object.Destroy(_root);
            yield return null;
        }

        [UnityTest]
        public IEnumerator TwoAxisJointReleaseTrackingAndRotatedCockpit()
        {
            _root = new GameObject("Physics test BallGrip");
            _root.SetActive(false);
            _root.transform.rotation = Quaternion.Euler(-90, 0, 0);
            Rigidbody baseBody = _root.AddComponent<Rigidbody>();
            baseBody.isKinematic = true;
            GameObject handle = new GameObject("BoxLid");
            handle.transform.SetParent(_root.transform, false);
            var body = handle.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.solverIterations = 12;
            body.solverVelocityIterations = 12;
            var collider = handle.AddComponent<SphereCollider>();
            collider.center = new Vector3(0, 0, 0.1f);
            collider.radius = 0.04f;
            var joint = handle.AddComponent<ConfigurableJoint>();
            joint.connectedBody = baseBody;
            joint.autoConfigureConnectedAnchor = false;
            joint.anchor = joint.connectedAnchor = Vector3.zero;
            joint.axis = Vector3.right;
            joint.secondaryAxis = Vector3.up;
            joint.xMotion = joint.yMotion = joint.zMotion = ConfigurableJointMotion.Locked;
            joint.angularXMotion = joint.angularYMotion = ConfigurableJointMotion.Limited;
            joint.angularZMotion = ConfigurableJointMotion.Locked;
            joint.lowAngularXLimit = new SoftJointLimit { limit = -40 };
            joint.highAngularXLimit = joint.angularYLimit = new SoftJointLimit { limit = 40 };
            joint.enablePreprocessing = false;
            var input = _root.AddComponent<BallGripInput>();
            input.baseFrame = _root.transform;
            input.grip = handle.transform;
            input.editorPreview = true;
            var grab = handle.AddComponent<Grabbable>();
            input.grabbable = grab;
            var driver = handle.AddComponent<BallGripJointDriver>();
            driver.input = input;
            grab.InjectOptionalOneGrabTransformer(driver);
            grab.InjectOptionalRigidbody(body);
            grab.InjectOptionalKinematicWhileSelected(false);
            grab.InjectOptionalThrowWhenUnselected(false);
            _root.SetActive(true);
            yield return null;
            Assert.That(input.Axes, Is.EqualTo(Vector2.zero));
            input.previewGrabbed = true;
            foreach (Vector2 target in new[] { new Vector2(25, 0), new Vector2(-25, 0), new Vector2(0, 25),
                new Vector2(0, -25), new Vector2(25, -25), new Vector2(-25, 25) })
            {
                input.previewTilt = target;
                yield return new WaitForSeconds(1f);
                Assert.That(input.Tilt.x, Is.EqualTo(target.x).Within(4), "forward target " + target);
                Assert.That(input.Tilt.y, Is.EqualTo(target.y).Within(4), "turn target " + target);
                Assert.That(handle.transform.localPosition.magnitude, Is.LessThan(0.01));
                Assert.That(Mathf.Abs(input.Axes.x), Is.LessThanOrEqualTo(1));
                Assert.That(Mathf.Abs(input.Axes.y), Is.LessThanOrEqualTo(1));
            }
            input.previewTracking = false;
            yield return null;
            Assert.That(input.Axes, Is.EqualTo(Vector2.zero), "tracking loss must immediately neutralize");
            input.previewTracking = true;
            yield return null;
            Assert.That(input.ControlActive, Is.False, "tracking recovery while held must stay blocked");
            input.previewGrabbed = false;
            yield return new WaitForSeconds(1.5f);
            Assert.That(input.Axes, Is.EqualTo(Vector2.zero));
            Assert.That(input.Tilt.magnitude, Is.LessThan(2), "spring should return to center");
            Quaternion delta = Quaternion.Euler(0, 120, 0);
            Quaternion oldHandle = body.rotation;
            _root.transform.rotation = delta * _root.transform.rotation;
            baseBody.rotation = _root.transform.rotation;
            body.rotation = delta * oldHandle;
            Physics.SyncTransforms();
            input.previewGrabbed = true;
            input.previewTilt = new Vector2(25, 20);
            yield return new WaitForSeconds(1f);
            Assert.That(input.Axes.x, Is.GreaterThan(0.3f));
            Assert.That(input.Axes.y, Is.GreaterThan(0.2f));
            input.previewGrabbed = false;
            yield return null;
            Assert.That(input.Axes, Is.EqualTo(Vector2.zero), "release must be neutral during physical return");
        }

        [UnityTest]
        public IEnumerator HandPointerMovementDrivesSleepingBodyAndRequiresRegrabAfterTrackingLoss()
        {
            _root = new GameObject("Hand pointer physics fixture");
            _root.SetActive(false);
            _root.transform.rotation = Quaternion.Euler(-90, 0, 0);
            Rigidbody baseBody = _root.AddComponent<Rigidbody>();
            baseBody.isKinematic = true;

            var handle = new GameObject("BoxLid");
            handle.transform.SetParent(_root.transform, false);
            var body = handle.AddComponent<Rigidbody>();
            body.useGravity = false;
            var collider = handle.AddComponent<SphereCollider>();
            collider.center = new Vector3(0, 0, 0.1f);
            collider.radius = 0.04f;
            var joint = handle.AddComponent<ConfigurableJoint>();
            joint.connectedBody = baseBody;
            joint.autoConfigureConnectedAnchor = false;
            joint.anchor = joint.connectedAnchor = Vector3.zero;
            joint.axis = Vector3.right;
            joint.secondaryAxis = Vector3.up;
            joint.xMotion = joint.yMotion = joint.zMotion = ConfigurableJointMotion.Locked;
            joint.angularXMotion = joint.angularYMotion = ConfigurableJointMotion.Limited;
            joint.angularZMotion = ConfigurableJointMotion.Locked;
            joint.lowAngularXLimit = new SoftJointLimit { limit = -40 };
            joint.highAngularXLimit = joint.angularYLimit = new SoftJointLimit { limit = 40 };
            joint.enablePreprocessing = false;

            var input = _root.AddComponent<BallGripInput>();
            input.baseFrame = _root.transform;
            input.grip = handle.transform;
            input.editorPreview = false;
            var grab = handle.AddComponent<Grabbable>();
            input.grabbable = grab;
            var driver = handle.AddComponent<BallGripJointDriver>();
            driver.input = input;
            grab.InjectOptionalOneGrabTransformer(driver);
            grab.InjectOptionalRigidbody(body);
            grab.InjectOptionalKinematicWhileSelected(false);
            grab.InjectOptionalThrowWhenUnselected(false);

            var handObject = new GameObject("Tracked hand");
            handObject.transform.SetParent(_root.transform, false);
            handObject.transform.localPosition = Vector3.one * 10;
            var hand = handObject.AddComponent<PhysicsTestHandSource>();
            var handBody = handObject.AddComponent<Rigidbody>();
            handBody.isKinematic = true;
            var handCollider = handObject.AddComponent<SphereCollider>();
            handCollider.isTrigger = true;
            handCollider.radius = 0.01f;
            var handApi = handObject.AddComponent<HandGrabAPI>();
            handApi.InjectHand(hand);
            var interactor = handObject.AddComponent<HandGrabInteractor>();
            interactor.InjectHand(hand);
            interactor.InjectHandGrabApi(handApi);
            interactor.InjectRigidbody(handBody);
            interactor.InjectGrabOrigin(handObject.transform);

            _root.SetActive(true);
            yield return null;
            yield return new WaitForFixedUpdate();
            Assert.That(interactor.Hand, Is.SameAs(hand));
            Assert.That(input.editorPreview, Is.False);
            body.Sleep();
            Assert.That(body.IsSleeping(), Is.True, "test must start from a settled handle");

            int pointer = interactor.Identifier;
            Vector3 localStart = new Vector3(0, 0, 0.15f);
            Pose start = new Pose(_root.transform.TransformPoint(localStart), _root.transform.rotation);
            Pose moved = new Pose(_root.transform.TransformPoint(localStart + new Vector3(-0.04f, -0.06f, 0)), start.rotation);
            hand.rootPose = start;
            // Feed SDK pointer events, rather than the editor-preview shortcut.
            grab.ProcessPointerEvent(new PointerEvent(pointer, PointerEventType.Hover, start, interactor.Data));
            grab.ProcessPointerEvent(new PointerEvent(pointer, PointerEventType.Select, start, interactor.Data));
            Assert.That(input.Grabbed && input.TrackingValid && input.ControlActive, Is.True);
            hand.rootPose = moved;
            grab.ProcessPointerEvent(new PointerEvent(pointer, PointerEventType.Move, moved, interactor.Data));
            Assert.That(Quaternion.Angle(joint.targetRotation, Quaternion.identity), Is.GreaterThan(15));
            yield return new WaitForSeconds(1f);
            Assert.That(input.Tilt.x, Is.GreaterThan(10), "hand movement must rotate the actual Rigidbody forward");
            Assert.That(input.Tilt.y, Is.GreaterThan(7), "hand movement must rotate the actual Rigidbody sideways");
            Assert.That(input.Axes.x, Is.GreaterThan(0.1f));
            Assert.That(input.Axes.y, Is.GreaterThan(0.05f));
            Assert.That(handle.transform.localPosition.magnitude, Is.LessThan(0.01f));

            grab.ProcessPointerEvent(new PointerEvent(pointer, PointerEventType.Unselect, moved, interactor.Data));
            Assert.That(input.Axes, Is.EqualTo(Vector2.zero), "release must neutralize before physical return");
            yield return new WaitForSeconds(1.5f);
            Assert.That(input.Tilt.magnitude, Is.LessThan(2), "released handle must return to center");

            hand.rootPose = start;
            grab.ProcessPointerEvent(new PointerEvent(pointer, PointerEventType.Select, start, interactor.Data));
            grab.ProcessPointerEvent(new PointerEvent(pointer, PointerEventType.Move, moved, interactor.Data));
            hand.valid = false;
            yield return null;
            Assert.That(input.ControlActive, Is.False);
            Assert.That(input.Axes, Is.EqualTo(Vector2.zero));
            hand.valid = true;
            yield return null;
            Assert.That(input.TrackingValid, Is.True);
            Assert.That(input.ControlActive, Is.False, "tracking recovery while still held must not resume control");
            grab.ProcessPointerEvent(new PointerEvent(pointer, PointerEventType.Move, moved, interactor.Data));
            yield return new WaitForSeconds(1f);
            Assert.That(input.Tilt.magnitude, Is.LessThan(2));
            Assert.That(input.Axes, Is.EqualTo(Vector2.zero));

            grab.ProcessPointerEvent(new PointerEvent(pointer, PointerEventType.Unselect, moved, interactor.Data));
            grab.ProcessPointerEvent(new PointerEvent(pointer, PointerEventType.Select, start, interactor.Data));
            grab.ProcessPointerEvent(new PointerEvent(pointer, PointerEventType.Move, moved, interactor.Data));
            Assert.That(input.ControlActive, Is.True, "a fresh tracked grab must re-enable physical control");
            yield return new WaitForSeconds(1f);
            Assert.That(input.Tilt.x, Is.GreaterThan(10));
            grab.ProcessPointerEvent(new PointerEvent(pointer, PointerEventType.Cancel, moved, interactor.Data));
            Assert.That(input.Axes, Is.EqualTo(Vector2.zero));
        }
    }
}
#endif
