using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using NUnit.Framework;
using Oculus.Interaction;
using Oculus.Interaction.HandGrab;
using UnityEditor;
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
        public void SceneBallGripInteractionsTargetDrivenHandle()
        {
            var scene = EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity");
            var client = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<PuppyPiV2Client>(true))
                .Single(component => component.isActiveAndEnabled);
            var input = client.input;
            Assert.That(input, Is.Not.Null);
            Assert.That(input.grip, Is.Not.Null);
            var body = input.grip.GetComponent<Rigidbody>();
            var driver = input.grip.GetComponent<BallGripJointDriver>();
            Assert.That(body, Is.Not.Null);
            Assert.That(driver, Is.Not.Null);
            var placement = input.GetComponent<PuppyPiCockpitPlacement>();
            Assert.That(placement, Is.Not.Null, "The console body needs its own placement handles.");
            Assert.That(placement.input, Is.SameAs(input));
            Assert.That(placement.cockpitRoot, Is.SameAs(input.baseFrame));
            Assert.That(input.grabbable, Is.EqualTo(input.grip.GetComponent<Grabbable>()));
            Assert.That(driver.input, Is.EqualTo(input));
            Assert.That(new SerializedObject(input.grabbable)
                .FindProperty("_oneGrabTransformer").objectReferenceValue, Is.EqualTo(driver));

            var handGrabs = input.grip.GetComponentsInChildren<HandGrabInteractable>(true)
                .Where(component => component.isActiveAndEnabled).ToArray();
            var controllerGrabs = input.grip.GetComponentsInChildren<GrabInteractable>(true)
                .Where(component => component.isActiveAndEnabled).ToArray();
            Assert.That(handGrabs, Is.Not.Empty, "The active console must support hand grabbing.");
            Assert.That(controllerGrabs, Is.Not.Empty, "The active console must support controller grabbing.");

            foreach (var interactable in handGrabs.Cast<Component>().Concat(controllerGrabs))
            {
                var path = AnimationUtility.CalculateTransformPath(interactable.transform, input.transform);
                var serialized = new SerializedObject(interactable);
                Assert.That(serialized.FindProperty("_pointableElement").objectReferenceValue,
                    Is.EqualTo(input.grabbable), path + " must forward grab events to the driven handle.");
                Assert.That(serialized.FindProperty("_rigidbody").objectReferenceValue,
                    Is.EqualTo(body), path + " must use the driven handle's Rigidbody.");
            }
        }

        [Test]
        public void RejectedOwnerAckIsNotDisplayedAsConnected()
        {
            var root = new GameObject("Rejected owner HUD fixture");
            try
            {
                var input = root.AddComponent<BallGripInput>();
                var client = root.AddComponent<PuppyPiV2Client>();
                client.input = input;
                client.networkEnabled = true;
                var hud = root.AddComponent<BallGripHud>();
                hud.input = input;
                hud.client = client;
                var command = client.Session.Command(0, false, 0, 0);
                var ack = new V2Ack {
                    protocol = 2, type = "ack", client_id = command.client_id,
                    sequence = command.sequence, accepted = false, reason = "not_owner",
                    state = "emergency_stop", emergency_stop = true
                };
                Assert.That(client.Session.AcceptAck(ack, 0.01, 0.2), Is.True);
                Assert.That(client.Session.Connected, Is.True, "the server is responding but rejects control");
                string status = (string)typeof(BallGripHud)
                    .GetMethod("Status", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(hud, null);
                Assert.That(status.StartsWith("PUPPYPI CONNECTED"), Is.False,
                    "a rejected owner must not look ready to control");
                Assert.That(status, Does.Contain("not_owner"));
                Assert.That(status, Does.Contain("accepted=False"));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SceneHasOneV2ClientAndNoEnabledLegacySender()
        {
            var scene = EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity");
            var roots = scene.GetRootGameObjects();
            var clients = roots.SelectMany(r => r.GetComponentsInChildren<PuppyPiV2Client>(true)).ToArray();
            Assert.That(clients.Length, Is.EqualTo(1));
            Assert.That(clients[0].port, Is.EqualTo(5001));
            if (clients[0].networkEnabled)
            {
                Assert.That(IPAddress.TryParse(clients[0].robotAddress, out IPAddress address), Is.True,
                    "a deployed scene must specify its robot endpoint");
                Assert.That(address.AddressFamily, Is.EqualTo(AddressFamily.InterNetwork));
                Assert.That(clients[0].input.editorPreview, Is.False,
                    "real robot deployment must not use simulated grip input");
            }
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
