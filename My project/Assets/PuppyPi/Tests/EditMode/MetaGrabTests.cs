using System;
using System.Reflection;
using NUnit.Framework;
using Oculus.Interaction;
using Oculus.Interaction.Input;
using UnityEngine;

namespace PuppyPi.Tests
{
    public sealed class TestControllerSource : MonoBehaviour, IController
    {
        public bool valid = true;
        public Handedness Handedness => Handedness.Right;
        public float Scale => 1;
        public bool IsConnected => valid;
        public bool IsPoseValid => valid;
        public ControllerInput ControllerInput => default;
        public bool TryGetPose(out Pose pose) { pose = Pose.identity; return valid; }
        public bool TryGetPointerPose(out Pose pose) { pose = Pose.identity; return valid; }
        public bool IsButtonUsageAnyActive(ControllerButtonUsage usage) => false;
        public bool IsButtonUsageAllActive(ControllerButtonUsage usage) => false;
        public event Action WhenUpdated { add { } remove { } }
    }

    public class MetaGrabTests
    {
        private static void Lifecycle(MonoBehaviour component, string method) =>
            component.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(component, null);

        [Test]
        public void SdkSelectMoveUnselectCancelAndTrackingProviderAreConnected()
        {
            var root = new GameObject("Meta input fixture");
            var controllerObject = new GameObject("Controller grab fixture");
            try
            {
                var grip = new GameObject("BoxLid");
                grip.transform.SetParent(root.transform, false);
                var joint = grip.AddComponent<ConfigurableJoint>();
                var grab = grip.AddComponent<Grabbable>();
                // EditMode does not run MonoBehaviour lifecycle automatically.
                Lifecycle(grab, "Awake");
                grab.InjectOptionalKinematicWhileSelected(false);
                grab.InjectOptionalThrowWhenUnselected(false);
                var input = root.AddComponent<BallGripInput>();
                input.enabled = false;
                input.grip = grip.transform;
                input.baseFrame = root.transform;
                input.grabbable = grab;
                Lifecycle(input, "Awake");
                input.enabled = true;
                Lifecycle(input, "OnEnable");
                var driver = grip.AddComponent<BallGripJointDriver>();
                driver.input = input;
                driver.Initialize(grab);
                grab.InjectOptionalOneGrabTransformer(driver);
                var provider = controllerObject.AddComponent<TestControllerSource>();
                var interactor = controllerObject.AddComponent<GrabInteractor>();
                var selectorObject = new GameObject("GripButtonSelector");
                selectorObject.transform.SetParent(controllerObject.transform, false);
                var selector = selectorObject.AddComponent<ControllerSelector>();
                selector.InjectController(provider);
                int neutralRequests = 0;
                input.NeutralRequested += () => neutralRequests++;
                Pose pose = new Pose(new Vector3(0, 0, 0.15f), Quaternion.identity);
                grab.ProcessPointerEvent(new PointerEvent(12, PointerEventType.Hover, pose, interactor));
                grab.ProcessPointerEvent(new PointerEvent(12, PointerEventType.Select, pose, interactor));
                Assert.That(input.Grabbed && input.ControlActive && input.TrackingValid, Is.True);
                pose.position += new Vector3(-0.04f, -0.05f, 0);
                grab.ProcessPointerEvent(new PointerEvent(12, PointerEventType.Move, pose, interactor));
                Vector2 target = BallGripMath.TiltDegrees(Quaternion.Inverse(joint.targetRotation));
                Assert.That(target.x, Is.GreaterThan(10));
                Assert.That(target.y, Is.GreaterThan(10));
                Quaternion beforeTwist = joint.targetRotation;
                pose.rotation = Quaternion.Euler(0, 0, 80);
                grab.ProcessPointerEvent(new PointerEvent(12, PointerEventType.Move, pose, interactor));
                Assert.That(Quaternion.Angle(beforeTwist, joint.targetRotation), Is.LessThan(0.01));
                provider.valid = false;
                Lifecycle(input, "Update");
                Assert.That(input.ControlActive, Is.False);
                Assert.That(neutralRequests, Is.GreaterThan(0));
                provider.valid = true;
                Lifecycle(input, "Update");
                Assert.That(input.ControlActive, Is.False);
                grab.ProcessPointerEvent(new PointerEvent(12, PointerEventType.Unselect, pose, interactor));
                Assert.That(input.Grabbed, Is.False);
                Assert.That(Quaternion.Angle(joint.targetRotation, Quaternion.identity), Is.LessThan(0.01));
                grab.ProcessPointerEvent(new PointerEvent(12, PointerEventType.Select, pose, interactor));
                Assert.That(input.ControlActive, Is.True);
                grab.ProcessPointerEvent(new PointerEvent(12, PointerEventType.Cancel, pose, interactor));
                Assert.That(input.Grabbed, Is.False);
                Assert.That(input.Axes, Is.EqualTo(Vector2.zero));
                Lifecycle(input, "OnDisable");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(controllerObject);
            }
        }
    }
}
