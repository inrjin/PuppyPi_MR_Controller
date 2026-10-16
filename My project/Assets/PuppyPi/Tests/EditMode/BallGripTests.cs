using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PuppyPi.Tests
{
    public class BallGripTests
    {
        [TestCase(30, 0)] [TestCase(-30, 0)] [TestCase(0, 30)] [TestCase(0, -30)]
        [TestCase(25, -25)] [TestCase(-25, 25)] [TestCase(40, 40)]
        public void DirectionAndDiagonalRemainInBaseFrame(float forward, float turn)
        {
            Quaternion basis = Quaternion.Euler(0, 137, 0) * Quaternion.Euler(-90, 0, 0);
            Quaternion local = BallGripMath.Rotation(new Vector2(forward, turn));
            Vector2 result = BallGripMath.TiltDegrees(Quaternion.Inverse(basis) * (basis * local));
            Assert.That(result.x, Is.EqualTo(forward).Within(0.001));
            Assert.That(result.y, Is.EqualTo(turn).Within(0.001));
        }

        [Test]
        public void PhysicalWorldSignsAndAxialTwist()
        {
            Quaternion basis = Quaternion.Euler(-90, 0, 0);
            Assert.That((basis * BallGripMath.Rotation(new Vector2(30, 0)) * Vector3.forward).z, Is.GreaterThan(0));
            Assert.That((basis * BallGripMath.Rotation(new Vector2(0, 30)) * Vector3.forward).x, Is.LessThan(0));
            Vector2 twist = BallGripMath.TiltDegrees(Quaternion.AngleAxis(80, Vector3.forward));
            Assert.That(twist.magnitude, Is.LessThan(0.001));
            Quaternion diagonal = BallGripMath.Rotation(new Vector2(20, -30));
            Vector2 twisted = BallGripMath.TiltDegrees(diagonal * Quaternion.AngleAxis(70, Vector3.forward));
            Assert.That(twisted.x, Is.EqualTo(20).Within(0.001));
            Assert.That(twisted.y, Is.EqualTo(-30).Within(0.001));
        }

        [Test]
        public void DeadzoneIsContinuousAndSensitivityAppliedOnce()
        {
            Assert.That(BallGripMath.Normalize(5, 5, 40, 1), Is.Zero);
            Assert.That(BallGripMath.Normalize(5.001f, 5, 40, 1), Is.LessThan(0.001));
            Assert.That(BallGripMath.Normalize(22.5f, 5, 40, 1), Is.EqualTo(0.5f).Within(0.001));
            Assert.That(BallGripMath.Normalize(-22.5f, 5, 40, 2), Is.EqualTo(-1));
            Assert.That(BallGripMath.Normalize(100, 5, 40, 2), Is.EqualTo(1));
        }

        [Test]
        public void SceneHasOneV2ClientAndNoEnabledLegacySender()
        {
            var scene = EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity");
            var roots = scene.GetRootGameObjects();
            var clients = roots.SelectMany(r => r.GetComponentsInChildren<PuppyPiV2Client>(true)).ToArray();
            Assert.That(clients.Length, Is.EqualTo(1));
            Assert.That(clients[0].port, Is.EqualTo(5001));
            Assert.That(clients[0].networkEnabled, Is.False);
            Assert.That(clients[0].input.grabbable, Is.Not.Null);
            Assert.That(roots.Single(r => r.name.Trim() == "Console_StickGrip").activeSelf, Is.False);
            foreach (var component in roots.SelectMany(r => r.GetComponentsInChildren<MonoBehaviour>(true)))
                if (component != null && component.GetType().Name == "UDP_Joystick_Sender") Assert.That(component.enabled, Is.False);
            var joint = clients[0].input.grip.GetComponent<ConfigurableJoint>();
            Assert.That(joint, Is.Not.Null);
            Assert.That(joint.connectedBody, Is.EqualTo(clients[0].GetComponent<Rigidbody>()));
            Assert.That(joint.xMotion, Is.EqualTo(ConfigurableJointMotion.Locked));
            Assert.That(joint.yMotion, Is.EqualTo(ConfigurableJointMotion.Locked));
            Assert.That(joint.zMotion, Is.EqualTo(ConfigurableJointMotion.Locked));
            Assert.That(joint.angularXMotion, Is.EqualTo(ConfigurableJointMotion.Limited));
            Assert.That(joint.angularYMotion, Is.EqualTo(ConfigurableJointMotion.Limited));
            Assert.That(joint.angularZMotion, Is.EqualTo(ConfigurableJointMotion.Locked));
            Assert.That(joint.lowAngularXLimit.limit, Is.EqualTo(-40));
            Assert.That(joint.highAngularXLimit.limit, Is.EqualTo(40));
            Assert.That(joint.angularYLimit.limit, Is.EqualTo(40));
            Assert.That(clients[0].input.grip.GetComponent<HingeJoint>(), Is.Null);
            Assert.That(clients[0].input.grip.GetComponent<BallGripJointDriver>().input, Is.EqualTo(clients[0].input));
        }
    }
}
