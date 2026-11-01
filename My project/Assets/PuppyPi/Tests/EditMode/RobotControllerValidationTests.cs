using NUnit.Framework;
using UnityEngine;

namespace PuppyPi.Tests
{
    public class RobotControllerValidationTests
    {
        [TestCase("0.0.0.0", 5001)]
        [TestCase("255.255.255.255", 5001)]
        [TestCase("224.0.0.1", 5001)]
        [TestCase("192.168.100.153", 0)]
        [TestCase("", 5001)]
        public void InvalidManualTargetPreservesTheExistingControlSession(string host, int port)
        {
            var root = new GameObject("VR connection validation fixture");
            root.SetActive(false);
            try
            {
                var input = root.AddComponent<BallGripInput>();
                var client = root.AddComponent<PuppyPiV2Client>();
                client.input = input;
                client.robotAddress = "127.0.0.1";
                client.networkEnabled = true;
                var controller = root.AddComponent<PuppyPiRobotController>();
                controller.driveClient = client;
                controller.gripInput = input;
                controller.ResolveComponents();
                string id = client.Session.ClientId;
                controller.ConnectManual(host, port);
                Assert.That(client.networkEnabled, Is.True);
                Assert.That(client.robotAddress, Is.EqualTo("127.0.0.1"));
                Assert.That(client.Session.ClientId, Is.EqualTo(id));
                Assert.That(client.Session.Retired, Is.False);
                Assert.That(client.Session.LastSequence, Is.Zero, "invalid UI input must not emit control or disconnect packets");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
