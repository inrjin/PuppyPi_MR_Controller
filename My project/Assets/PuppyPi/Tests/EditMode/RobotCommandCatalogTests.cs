using System;
using NUnit.Framework;
using UnityEngine;

namespace PuppyPi.Tests
{
    public class RobotCommandCatalogTests
    {
        [Serializable] private sealed class PoseEnvelope { public string op, topic; public PoseValues msg; }
        [Serializable] private sealed class PoseValues { public float pitch, height, x_shift, stance_x; public int run_time; }
        [Serializable] private sealed class ServiceEnvelope { public string op, service, type, id; public ActionArguments args; }
        [Serializable] private sealed class ActionArguments { public string name; public bool wait; }

        [TestCase("Stand", 0, -10, -0.5f, 0)]
        [TestCase("LieDown", 0, -5, 2, 0)]
        [TestCase("LookDown", -15, -10, -0.5f, 0)]
        [TestCase("LookDown_10deg", -10, -9, -0.1f, 0)]
        [TestCase("LookDown_20deg", -20, -9, -0.1f, 0)]
        [TestCase("LookDown_30deg", -30, -9.6f, -1.4f, 1)]
        [TestCase("StandLow", 0, -7, -0.5f, 0)]
        public void PoseValuesMatchMacControllerRadiansAndCentimeters(string name, float degrees, float height, float shift, float stance)
        {
            var message = JsonUtility.FromJson<PoseEnvelope>(RobotRosProtocol.Pose(name));
            Assert.That(message.op, Is.EqualTo("publish"));
            Assert.That(message.topic, Is.EqualTo("/puppy_control/pose"));
            Assert.That(message.msg.pitch, Is.EqualTo(degrees * Mathf.Deg2Rad).Within(0.00001));
            Assert.That(message.msg.height, Is.EqualTo(height));
            Assert.That(message.msg.x_shift, Is.EqualTo(shift));
            Assert.That(message.msg.stance_x, Is.EqualTo(stance));
            Assert.That(message.msg.run_time, Is.EqualTo(500));
        }

        [Test]
        public void EveryMacActionUsesItsExactFileNameAndDoesNotWaitForCompletion()
        {
            Assert.That(RobotCommandCatalog.PoseNames.Length, Is.EqualTo(7));
            Assert.That(RobotCommandCatalog.ActionNames.Length, Is.EqualTo(25));
            foreach (string name in RobotCommandCatalog.ActionNames)
            {
                var message = JsonUtility.FromJson<ServiceEnvelope>(RobotRosProtocol.Service(RobotRosProtocol.ActionService, "test-request", name));
                Assert.That(message.service, Is.EqualTo("/puppy_control/runActionGroup"));
                Assert.That(message.type, Is.EqualTo("puppy_control/SetRunActionName"));
                Assert.That(message.id, Is.EqualTo("test-request"));
                Assert.That(message.args.name, Is.EqualTo(name + ".d6ac"));
                Assert.That(message.args.wait, Is.False);
            }
        }

        [TestCase("../stand")]
        [TestCase("stand.d6ac")]
        [TestCase("stand\",\"wait\":true")]
        [TestCase("")]
        [TestCase(null)]
        public void ArbitraryPoseAndActionArgumentsAreRejected(string value)
        {
            Assert.That(RobotCommandCatalog.IsAction(value), Is.False);
            Assert.That(RobotCommandCatalog.TryGetPose(value, out _), Is.False);
            Assert.Throws<ArgumentException>(() => RobotRosProtocol.Pose(value));
            Assert.Throws<ArgumentException>(() => RobotRosProtocol.Service(RobotRosProtocol.ActionService, "test", value));
        }

        [TestCase(RobotRosProtocol.StandService, "std_srvs/Empty")]
        [TestCase(RobotRosProtocol.StopService, "std_srvs/Trigger")]
        [TestCase(RobotRosProtocol.ResetService, "std_srvs/Trigger")]
        public void ServiceTypesAndEscapedRequestIdsMatchRosbridge(string service, string type)
        {
            string id = "request-\"\\\n";
            var message = JsonUtility.FromJson<ServiceEnvelope>(RobotRosProtocol.Service(service, id));
            Assert.That(message.op, Is.EqualTo("call_service"));
            Assert.That(message.service, Is.EqualTo(service));
            Assert.That(message.type, Is.EqualTo(type));
            Assert.That(message.id, Is.EqualTo(id));
        }

        [Test]
        public void UnsupportedRosTopicsAndServicesCannotBeConstructed()
        {
            Assert.Throws<ArgumentException>(() => RobotRosProtocol.Subscribe("/cmd_vel"));
            Assert.Throws<ArgumentException>(() => RobotRosProtocol.Service("/unknown", "request"));
        }
    }
}
