using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace PuppyPi.Tests
{
    public class RobotControllerValidationTests
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static double Now => Time.realtimeSinceStartupAsDouble;

        private static PuppyPiRobotController Fixture(out GameObject root, out PuppyPiV2Client drive)
        {
            root = new GameObject("inactive robot permission fixture");
            root.SetActive(false); // No Unity lifecycle or network loop runs in these received-state tests.
            var input = root.AddComponent<BallGripInput>();
            drive = root.AddComponent<PuppyPiV2Client>();
            drive.input = input;
            drive.networkEnabled = true;
            var controller = root.AddComponent<PuppyPiRobotController>();
            controller.driveClient = drive;
            controller.gripInput = input;
            controller.ResolveComponents();
            return controller;
        }

        private static void SetField(object target, string name, object value) =>
            target.GetType().GetField(name, Hidden).SetValue(target, value);

        private static void SetProperty(object target, string name, object value) =>
            target.GetType().GetProperty(name).SetValue(target, value);

        private static void ConnectedStatus(RobotRosBridge bridge, bool fresh = false,
            bool robotStop = false, bool arbiterStop = false)
        {
            var type = typeof(RobotRosBridge).GetNestedType("Transport", BindingFlags.NonPublic);
            object transport = System.Activator.CreateInstance(type, new object[] {
                new System.Uri("ws://127.0.0.1:9090/"), 5d, 2d });
            type.GetField("Connected").SetValue(transport, true);
            type.GetField("Epoch").SetValue(transport, 1);
            SetField(bridge, "_transport", transport);
            SetField(bridge, "_robotStatusEpoch", 1);
            SetField(bridge, "_arbiterStatusEpoch", 1);
            SetProperty(bridge, "RobotStatus", new RobotRuntimeStatus { engine = "busy", estop = robotStop });
            SetProperty(bridge, "ArbiterState", new RobotArbiterState {
                owner = "action", vr_permitted = false, estop = arbiterStop });
            SetProperty(bridge, "LastRobotStatusTime", fresh ? Now : Now - 100);
            SetProperty(bridge, "LastArbiterStateTime", fresh ? Now : Now - 100);
        }

        private static void UpdateController(PuppyPiRobotController controller) =>
            typeof(PuppyPiRobotController).GetMethod("Update", Hidden).Invoke(controller, null);

        [Test]
        public void SupervisedActionsUseTheConnectedRpcChannelDespiteAdvisoryStatusAndUdpHandshake()
        {
            var controller = Fixture(out GameObject root, out PuppyPiV2Client drive);
            try
            {
                ConnectedStatus(controller.RosBridge);
                drive.networkEnabled = false;
                SetProperty(controller.GripInput, "Grabbed", true);
                Assert.That(drive.Session.Connected, Is.False);
                Assert.That(drive.Session.NeutralConfirmed, Is.False);
                Assert.That(controller.CanExecuteMotion, Is.True,
                    "stale owner/engine status, a held grip and the separate UDP handshake must not disable RPC actions");
                controller.gripInput = null;
                Assert.That(controller.CanExecuteMotion, Is.True, "an RPC action does not depend on a grip reference");
                controller.PathClient.operatorSupervisedNavigation = false;
                Assert.That(controller.CanExecuteMotion, Is.False, "the historical strict-mode gates remain available");
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void SupervisedControllerDoesNotRetainAnOldArbiterPermissionLock()
        {
            var controller = Fixture(out GameObject root, out _);
            try
            {
                ConnectedStatus(controller.RosBridge, true);
                controller.PathClient.operatorSupervisedNavigation = false;
                UpdateController(controller);
                Assert.That(controller.DriveClient.ExternalMotionBlocked, Is.True);
                controller.PathClient.operatorSupervisedNavigation = true;
                SetProperty(controller.RosBridge, "LastRobotStatusTime", Now - 100);
                SetProperty(controller.RosBridge, "LastArbiterStateTime", Now - 100);
                UpdateController(controller);
                Assert.That(controller.DriveClient.ExternalMotionBlocked, Is.False,
                    "a cached false vr_permitted snapshot must not remain a supervised global motion lock");
                Assert.That(controller.CanExecuteMotion, Is.True);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [TestCase(true, false)]
        [TestCase(false, true)]
        public void FreshExplicitRobotStopBlocksSupervisedMotionButStaleAdvisoryStateDoesNot(bool robotStop, bool arbiterStop)
        {
            var controller = Fixture(out GameObject root, out PuppyPiV2Client drive);
            try
            {
                ConnectedStatus(controller.RosBridge, true, robotStop, arbiterStop);
                UpdateController(controller);
                Assert.That(controller.DriveClient.ExternalMotionBlocked, Is.True);
                Assert.That(controller.CanExecuteMotion, Is.False);
                SetProperty(controller.RosBridge, "LastRobotStatusTime", Now - 100);
                SetProperty(controller.RosBridge, "LastArbiterStateTime", Now - 100);
                UpdateController(controller);
                Assert.That(controller.DriveClient.ExternalMotionBlocked, Is.False);
                Assert.That(controller.CanExecuteMotion, Is.True);
                drive.Session.LatchEmergency();
                Assert.That(controller.CanExecuteMotion, Is.False, "an explicit UDP stop remains latched independently of ROS freshness");
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void SupervisedActionsStillRequireAnRpcConnectionAndDoNotOverlapPendingRequests()
        {
            var controller = Fixture(out GameObject root, out _);
            try
            {
                Assert.That(controller.CanExecuteMotion, Is.False, "RPC delivery requires a connected channel");
                ConnectedStatus(controller.RosBridge);
                Assert.That(controller.CanExecuteMotion, Is.True);
                Assert.That(controller.RosBridge.Stand(), Is.Not.Null,
                    "the inactive fixture queues the request without starting a socket or sender");
                Assert.That(controller.RosBridge.HasPendingCommand, Is.True);
                Assert.That(controller.CanExecuteMotion, Is.False, "an outstanding action remains a real command conflict");
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void LatchedVrStopRemainsVisibleAcrossDisconnectTimeoutAndOwnershipRejection()
        {
            var controller = Fixture(out GameObject root, out PuppyPiV2Client drive);
            try
            {
                drive.Session.LatchEmergency();
                drive.networkEnabled = false;
                Assert.That(controller.StatusText.Split('\n')[0], Does.Contain("VR STOP"));
                Assert.That(controller.StatusText, Does.Contain("UDP: DISCONNECTED"));
                drive.networkEnabled = true;
                var command = drive.Session.Command(Now, false, 0, 0);
                Assert.That(drive.Session.AcceptAck(new V2Ack {
                    protocol = 2, type = "ack", client_id = drive.Session.ClientId, sequence = command.sequence,
                    accepted = false, state = "emergency_stop", reason = "not_owner", emergency_stop = true
                }, Now, .2), Is.True);
                Assert.That(controller.StatusText.Split('\n')[0], Does.Contain("VR STOP"));
                Assert.That(controller.StatusText, Does.Contain("UDP: SESSION REJECTED: NOT OWNER"));
                drive.Session.CheckTimeout(Now + 1, .3);
                Assert.That(controller.StatusText.Split('\n')[0], Does.Contain("VR STOP"));
                Assert.That(controller.StatusText, Does.Contain("UDP: WAITING FOR ACK"));
            }
            finally { Object.DestroyImmediate(root); }
        }

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
