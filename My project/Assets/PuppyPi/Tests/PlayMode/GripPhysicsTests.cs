#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using Oculus.Interaction;
using UnityEngine;
using UnityEngine.TestTools;

namespace PuppyPi.Tests
{
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
    }
}
#endif
