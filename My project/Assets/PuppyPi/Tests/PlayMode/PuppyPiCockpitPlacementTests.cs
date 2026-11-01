#if UNITY_EDITOR
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using Oculus.Interaction;
using Oculus.Interaction.HandGrab;
using UnityEngine;
using UnityEngine.TestTools;

namespace PuppyPi.Tests
{
    public class PuppyPiCockpitPlacementTests
    {
        private GameObject _fixture;
        private Transform _cockpit;
        private Rigidbody _gripBody;
        private SphereCollider _gripCollider;
        private ConfigurableJoint _joint;
        private BallGripInput _input;
        private PuppyPiV2Client _drive;
        private PuppyPiRobotController _controller;
        private PuppyPiCockpitPlacement _placement;

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            if (_fixture != null) Object.Destroy(_fixture);
            yield return null;
        }

        private void BuildFixture()
        {
            _fixture = new GameObject("Offline cockpit placement fixture");
            _fixture.SetActive(false);
            _cockpit = new GameObject("Console with authored minus ninety degree tilt").transform;
            _cockpit.SetParent(_fixture.transform, false);
            _cockpit.SetPositionAndRotation(new Vector3(1, 0.7f, 2),
                Quaternion.Euler(0, 37, 0) * Quaternion.Euler(-90, 0, 0));
            var baseBody = _cockpit.gameObject.AddComponent<Rigidbody>();
            baseBody.isKinematic = true;
            baseBody.useGravity = false;
            var grip = new GameObject("Separate dynamic driving grip");
            grip.transform.SetParent(_cockpit, false);
            _gripBody = grip.AddComponent<Rigidbody>();
            _gripBody.useGravity = false;
            _gripCollider = grip.AddComponent<SphereCollider>();
            _gripCollider.center = new Vector3(0, 0, 0.105f);
            _gripCollider.radius = 0.045f;
            _joint = grip.AddComponent<ConfigurableJoint>();
            _joint.connectedBody = baseBody;
            _joint.autoConfigureConnectedAnchor = false;
            _joint.anchor = new Vector3(0.01f, 0.02f, 0.03f);
            _joint.connectedAnchor = _joint.anchor;
            _joint.xMotion = _joint.yMotion = _joint.zMotion = ConfigurableJointMotion.Locked;
            _joint.angularXMotion = _joint.angularYMotion = _joint.angularZMotion = ConfigurableJointMotion.Locked;

            _input = _cockpit.gameObject.AddComponent<BallGripInput>();
            _input.baseFrame = _cockpit;
            _input.grip = grip.transform;
            _input.editorPreview = false;
            _drive = _cockpit.gameObject.AddComponent<PuppyPiV2Client>();
            _drive.input = _input;
            _drive.networkEnabled = false;
            _placement = _cockpit.gameObject.AddComponent<PuppyPiCockpitPlacement>();
            _placement.cockpitRoot = _cockpit;
            _placement.input = _input;
            _controller = _cockpit.gameObject.AddComponent<PuppyPiRobotController>();
            _controller.driveClient = _drive;
            _controller.gripInput = _input;
            _controller.cockpitPlacement = _placement;
            _controller.showCameraScreenOnStart = false;
            _fixture.SetActive(true);
            _placement.EnsureHandle();
        }

        private void TickCoordinator()
        {
            // Editor batch focus must not affect this local placement contract.
            typeof(PuppyPiRobotController).GetMethod("OnApplicationFocus", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(_controller, new object[] { true });
            typeof(PuppyPiRobotController).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(_controller, null);
        }

        private static void Pointer(Grabbable grab, PointerEventType kind, Pose pose) =>
            grab.ProcessPointerEvent(new PointerEvent(601, kind, pose));

        private Pose SelectHandle()
        {
            var pose = new Pose(_placement.HandleRoot.GetComponentInChildren<Collider>().transform.position,
                _placement.HandleRoot.rotation);
            Pointer(_placement.HandleGrabbable, PointerEventType.Hover, pose);
            Pointer(_placement.HandleGrabbable, PointerEventType.Select, pose);
            return pose;
        }

        private static void AssertPosition(Vector3 actual, Vector3 expected, string message) =>
            Assert.That(Vector3.Distance(actual, expected), Is.LessThan(0.002f), message);

        [UnityTest]
        public IEnumerator SdkGrabMovesAndRotatesWholeTiltedConsoleWithoutChangingGripNeutralOrJoint()
        {
            BuildFixture();
            yield return null;
            yield return new WaitForFixedUpdate();
            Vector3 rootStart = _cockpit.position;
            Quaternion rotationStart = _cockpit.rotation;
            Vector3 gripStart = _gripBody.position;
            Quaternion gripRotationStart = _gripBody.rotation;
            Quaternion neutral = _input.Neutral;
            Quaternion relative = Quaternion.Inverse(rotationStart) * gripRotationStart;
            Vector3 anchor = _joint.anchor;
            Vector3 connectedAnchor = _joint.connectedAnchor;
            Vector3 scale = _cockpit.localScale;
            Pose hand = SelectHandle();
            Assert.That(_placement.IsGrabbed, Is.True);

            Vector3 translation = new Vector3(0.2f, 0.15f, -0.1f);
            hand.position += translation;
            Pointer(_placement.HandleGrabbable, PointerEventType.Move, hand);
            AssertPosition(_cockpit.position, rootStart + translation, "body movement must follow the selected bar");
            AssertPosition(_gripBody.position, gripStart + translation, "the dynamic joystick must receive the same world translation");
            Assert.That(Quaternion.Angle(_cockpit.rotation, rotationStart), Is.LessThan(0.1f));

            Vector3 beforeRotation = _cockpit.position;
            Quaternion beforeRotationQuaternion = _cockpit.rotation;
            Vector3 gripBeforeRotation = _gripBody.position;
            Quaternion gripRotationBeforeRotation = _gripBody.rotation;
            hand.rotation = Quaternion.Euler(12, 30, 8) * hand.rotation;
            Pointer(_placement.HandleGrabbable, PointerEventType.Move, hand);
            Quaternion delta = _cockpit.rotation * Quaternion.Inverse(beforeRotationQuaternion);
            Assert.That(Quaternion.Angle(_cockpit.rotation, rotationStart), Is.GreaterThan(15));
            AssertPosition(_gripBody.position, _cockpit.position + delta * (gripBeforeRotation - beforeRotation),
                "all child physics bodies must share the console's rigid rotation");
            Assert.That(Quaternion.Angle(_gripBody.rotation, delta * gripRotationBeforeRotation), Is.LessThan(0.1f));
            Assert.That(Quaternion.Angle(Quaternion.Inverse(_cockpit.rotation) * _gripBody.rotation, relative), Is.LessThan(0.1f));
            Assert.That(Quaternion.Angle(_input.Neutral, neutral), Is.LessThan(0.1f));
            Assert.That(_joint.anchor, Is.EqualTo(anchor));
            Assert.That(_joint.connectedAnchor, Is.EqualTo(connectedAnchor));
            Assert.That(_joint.connectedBody, Is.SameAs(_cockpit.GetComponent<Rigidbody>()));
            Assert.That(_cockpit.localScale, Is.EqualTo(scale));

            Pointer(_placement.HandleGrabbable, PointerEventType.Unselect, hand);
            Assert.That(_placement.IsGrabbed, Is.False);
            Vector3 releasedRoot = _cockpit.position;
            Quaternion releasedRotation = _cockpit.rotation;
            Vector3 releasedGrip = _gripBody.position;
            yield return new WaitForSeconds(0.15f);
            AssertPosition(_cockpit.position, releasedRoot, "releasing the move bars must leave the console fixed");
            AssertPosition(_gripBody.position, releasedGrip, "release must not throw the dynamic grip");
            Assert.That(Quaternion.Angle(_cockpit.rotation, releasedRotation), Is.LessThan(0.1f));
            Assert.That(_cockpit.localScale, Is.EqualTo(scale));
            Assert.That(_drive.networkEnabled, Is.False);
        }

        [UnityTest]
        public IEnumerator DedicatedBarCollidersExcludeDrivingGripAndSelectionBlocksMotion()
        {
            BuildFixture();
            yield return null;
            Transform handle = _placement.HandleRoot;
            Rigidbody proxy = handle.GetComponent<Rigidbody>();
            var handGrab = handle.GetComponent<HandGrabInteractable>();
            var controllerGrab = handle.GetComponent<GrabInteractable>();
            Collider[] colliders = handle.GetComponentsInChildren<Collider>();
            Assert.That(colliders.Length, Is.EqualTo(2));
            Assert.That(proxy, Is.Not.SameAs(_gripBody));
            Assert.That(proxy, Is.Not.SameAs(_cockpit.GetComponent<Rigidbody>()));
            Assert.That(proxy.isKinematic, Is.True);
            Assert.That(proxy.useGravity, Is.False);
            Assert.That(handle.lossyScale.x, Is.EqualTo(1).Within(0.0001));
            Assert.That(handle.lossyScale.y, Is.EqualTo(1).Within(0.0001));
            Assert.That(handle.lossyScale.z, Is.EqualTo(1).Within(0.0001));
            foreach (Collider collider in colliders)
            {
                Assert.That(collider.isTrigger, Is.True);
                Assert.That(collider.attachedRigidbody, Is.SameAs(proxy));
                Assert.That(Mathf.Abs(collider.transform.localPosition.x), Is.EqualTo(_placement.handleOffsetMeters.x).Within(0.0001));
            }
            Assert.That(_gripCollider.attachedRigidbody, Is.SameAs(_gripBody));
            Assert.That(handGrab.Rigidbody, Is.SameAs(proxy));
            Assert.That(controllerGrab.Rigidbody, Is.SameAs(proxy));
            Assert.That(handGrab.PointableElement, Is.SameAs(_placement.HandleGrabbable));
            Assert.That(controllerGrab.PointableElement, Is.SameAs(_placement.HandleGrabbable));
            CollectionAssert.AreEquivalent(colliders, handGrab.Colliders);
            CollectionAssert.AreEquivalent(colliders, controllerGrab.Colliders);
            Assert.That(handGrab.Colliders, Has.No.Member(_gripCollider));
            Assert.That(_placement.HandleGrabbable.MaxGrabPoints, Is.EqualTo(1));

            Pose hand = SelectHandle();
            TickCoordinator();
            Assert.That(_placement.IsGrabbed, Is.True);
            Assert.That(_controller.IsPositioningPanel, Is.True);
            Assert.That(_drive.ExternalMotionBlocked, Is.True);
            Assert.That(_drive.Session.Armed, Is.False);
            Assert.That(_input.ControlActive, Is.False);
            Pointer(_placement.HandleGrabbable, PointerEventType.Unselect, hand);
            TickCoordinator();
            Assert.That(_controller.IsPositioningPanel, Is.False);
            Assert.That(_drive.ExternalMotionBlocked, Is.False);
            Assert.That(_drive.Session.Armed, Is.False, "releasing a positioning handle must never arm driving");
            Assert.That(_drive.networkEnabled, Is.False);
        }

        [UnityTest]
        public IEnumerator InvalidTeleportLeavesPhysicsUntouchedAndCancelDropsSelection()
        {
            BuildFixture();
            yield return null;
            SelectHandle();
            Vector3 rootPosition = _cockpit.position;
            Quaternion rootRotation = _cockpit.rotation;
            Vector3 gripPosition = _gripBody.position;
            Quaternion gripRotation = _gripBody.rotation;
            Vector3 scale = _cockpit.localScale;
            Vector3 connectedAnchor = _joint.connectedAnchor;
            Assert.That(_placement.MoveTo(new Pose(new Vector3(float.NaN, 0, 0), Quaternion.identity)), Is.False);
            Assert.That(_placement.MoveTo(new Pose(rootPosition, new Quaternion(0, 0, 0, 0))), Is.False);
            Assert.That(_placement.MoveTo(new Pose(rootPosition, new Quaternion(0, float.PositiveInfinity, 0, 1))), Is.False);
            _placement.CancelGrab();
            Assert.That(_placement.IsGrabbed, Is.False);
            Assert.That(_placement.HandleGrabbable.SelectingPointsCount, Is.Zero);
            AssertPosition(_cockpit.position, rootPosition, "invalid poses and cancellation must not teleport the console");
            AssertPosition(_gripBody.position, gripPosition, "invalid poses must not alter physics bodies");
            Assert.That(Quaternion.Angle(_cockpit.rotation, rootRotation), Is.LessThan(0.1f));
            Assert.That(Quaternion.Angle(_gripBody.rotation, gripRotation), Is.LessThan(0.1f));
            Assert.That(_cockpit.localScale, Is.EqualTo(scale));
            Assert.That(_joint.connectedAnchor, Is.EqualTo(connectedAnchor));
            TickCoordinator();
            Assert.That(_drive.ExternalMotionBlocked, Is.False);
        }
    }
}
#endif
