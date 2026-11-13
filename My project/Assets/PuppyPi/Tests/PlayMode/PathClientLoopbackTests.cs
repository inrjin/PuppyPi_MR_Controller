#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace PuppyPi.Tests
{
    public class PathClientLoopbackTests
    {
        private GameObject _root;
        private UdpClient _server;
        private PuppyPiV2Client _client;
        private IPEndPoint _source;
        private string _uuid;
        private long _lastSequence;
        private int _paths, _commands, _cancels;
        private bool _emergency;
        private byte[] _delayedPathAck, _delayedCancelAck;
        private IPEndPoint _delayedCancelSource;
        private float _cancelAckDelaySeconds;
        private double _cancelAckAt;
        private bool _immediateDuplicatePathAck, _suppressCommandAcks, _duplicatePathEmergency;
        private string _duplicatePathState = "idle";

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            if (_root != null) Object.Destroy(_root);
            _server?.Close();
            yield return null;
        }

        private void Create()
        {
            _source = null; _uuid = null; _lastSequence = 0;
            _paths = _commands = _cancels = 0; _emergency = false; _delayedPathAck = _delayedCancelAck = null;
            _delayedCancelSource = null; _cancelAckDelaySeconds = 0; _cancelAckAt = 0;
            _immediateDuplicatePathAck = _suppressCommandAcks = _duplicatePathEmergency = false;
            _duplicatePathState = "idle";
            _server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            _root = new GameObject("Path protocol loopback");
            _root.SetActive(false);
            var grip = new GameObject("Grip");
            grip.transform.SetParent(_root.transform);
            var input = _root.AddComponent<BallGripInput>();
            input.grip = grip.transform;
            input.baseFrame = _root.transform;
            input.editorPreview = true;
            _client = _root.AddComponent<PuppyPiV2Client>();
            _client.input = input;
            _client.robotAddress = "127.0.0.1";
            _client.port = ((IPEndPoint)_server.Client.LocalEndPoint).Port;
            _client.networkEnabled = true;
            _root.SetActive(true);
        }

        private IEnumerator Pump(float seconds)
        {
            double until = Time.realtimeSinceStartupAsDouble + seconds;
            while (Time.realtimeSinceStartupAsDouble < until)
            {
                while (_server.Available > 0)
                {
                    var source = new IPEndPoint(IPAddress.Any, 0);
                    string json = Encoding.UTF8.GetString(_server.Receive(ref source));
                    var envelope = JsonUtility.FromJson<V2Event>(json);
                    if (_source == null) { _source = source; _uuid = envelope.client_id; }
                    Assert.That(source, Is.EqualTo(_source), "path and heartbeat must share the control socket");
                    Assert.That(envelope.client_id, Is.EqualTo(_uuid));
                    Assert.That(envelope.sequence, Is.GreaterThan(_lastSequence));
                    _lastSequence = envelope.sequence;
                    if (envelope.type == "path")
                    {
                        _paths++;
                        Assert.That(json, Does.Contain("\"points\":[[0,0],[0.5,0.25]]"));
                    }
                    else if (envelope.type == "command")
                    {
                        _commands++;
                        var command = JsonUtility.FromJson<V2Command>(json);
                        if (_client.PathCommandHeld)
                            Assert.That(command.control_active, Is.False, "path maintenance sends neutral heartbeats");
                    }
                    else if (envelope.type == "path_cancel") _cancels++;
                    else if (envelope.type == "emergency_stop") _emergency = true;
                    byte[] ack = Encoding.UTF8.GetBytes(JsonUtility.ToJson(new V2Ack { protocol = 2, type = "ack",
                        client_id = envelope.client_id, sequence = envelope.sequence, accepted = true, reason = "ok",
                        state = _emergency ? "emergency_stop" : "idle", emergency_stop = _emergency }));
                    if (envelope.type == "command" && _suppressCommandAcks) continue;
                    if (envelope.type == "path" && _immediateDuplicatePathAck)
                    {
                        byte[] duplicate = Encoding.UTF8.GetBytes(JsonUtility.ToJson(new V2Ack { protocol = 2, type = "ack",
                            client_id = envelope.client_id, sequence = envelope.sequence, accepted = false,
                            reason = "out_of_order", state = _duplicatePathState, emergency_stop = _duplicatePathEmergency }));
                        _server.Send(duplicate, duplicate.Length, source);
                        continue;
                    }
                    if (envelope.type == "path") { _delayedPathAck = ack; continue; }
                    if (envelope.type == "path_cancel" && _cancelAckDelaySeconds > 0)
                    {
                        _delayedCancelAck = ack;
                        _delayedCancelSource = source;
                        _cancelAckAt = Time.realtimeSinceStartupAsDouble + _cancelAckDelaySeconds;
                        continue;
                    }
                    _server.Send(ack, ack.Length, source);
                    if (_delayedPathAck != null)
                    {
                        _server.Send(_delayedPathAck, _delayedPathAck.Length, source);
                        _delayedPathAck = null;
                    }
                }
                if (_delayedCancelAck != null && Time.realtimeSinceStartupAsDouble >= _cancelAckAt)
                {
                    _server.Send(_delayedCancelAck, _delayedCancelAck.Length, _delayedCancelSource);
                    _delayedCancelAck = null;
                }
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator FreshDuplicatePathReceiptCannotPoisonSessionBeforeNextHeartbeatAck()
        {
            Create();
            yield return Pump(.2f);
            _client.SetPathCommandHold(true);
            yield return Pump(.15f);
            long pathSequence = 0, sequenceAtReceipt = -1;
            int received = 0;
            bool receiptApplied = true, acceptedAtReceipt = false;
            double sentAt = 0;
            _client.AckObserved += (ack, applied) =>
            {
                if (ack.sequence != pathSequence) return;
                received++;
                receiptApplied = applied;
                acceptedAtReceipt = _client.Session.LastAccepted;
                sequenceAtReceipt = _client.Session.LastAckSequence;
                Assert.That(Time.realtimeSinceStartupAsDouble - sentAt, Is.LessThan(_client.maxAckAgeSeconds),
                    "the duplicate must be fresh enough to expose global session poisoning");
            };
            _suppressCommandAcks = true;
            _immediateDuplicatePathAck = true;
            sentAt = Time.realtimeSinceStartupAsDouble;
            Assert.That(_client.TrySendPath(new[] { Vector2.zero, new Vector2(.5f, .25f) },
                out pathSequence, out string error), Is.True, error);
            yield return Pump(.12f);
            Assert.That(received, Is.EqualTo(1), "the duplicate must actually reach ReceiveAcks");
            Assert.That(receiptApplied, Is.False);
            Assert.That(acceptedAtReceipt, Is.True, "a duplicate operation receipt cannot revoke accepted control");
            Assert.That(sequenceAtReceipt, Is.LessThan(pathSequence), "a newer heartbeat must not mask the regression");
            Assert.That(_client.Session.LastAccepted, Is.True);
            _suppressCommandAcks = false;
            yield return Pump(.15f);
            Assert.That(_client.Session.LastAckSequence, Is.GreaterThan(pathSequence));
            Assert.That(_client.Session.Connected, Is.True);
            Assert.That(_client.Session.NeutralConfirmed, Is.True);
            Assert.That(_client.Session.Emergency, Is.False);
            Assert.That(_cancels, Is.Zero);
        }

        [UnityTest]
        public IEnumerator FreshDuplicatePathReceiptStillPropagatesRobotEmergencyStop()
        {
            yield return VerifyFreshDuplicateStopState("emergency_stop", true);
        }

        [UnityTest]
        public IEnumerator FreshDuplicatePathReceiptStillPropagatesRobotFault()
        {
            yield return VerifyFreshDuplicateStopState("fault", false);
        }

        private IEnumerator VerifyFreshDuplicateStopState(string state, bool emergency)
        {
            Create();
            yield return Pump(.2f);
            _client.SetPathCommandHold(true);
            yield return Pump(.15f);
            long pathSequence = 0, sequenceAtReceipt = -1;
            int received = 0;
            bool receiptApplied = false, emergencyAtReceipt = false, faultAtReceipt = false;
            double sentAt = 0;
            _client.AckObserved += (ack, applied) =>
            {
                if (ack.sequence != pathSequence) return;
                received++;
                receiptApplied = applied;
                emergencyAtReceipt = _client.Session.Emergency;
                faultAtReceipt = _client.Session.Faulted;
                sequenceAtReceipt = _client.Session.LastAckSequence;
                Assert.That(Time.realtimeSinceStartupAsDouble - sentAt, Is.LessThan(_client.maxAckAgeSeconds),
                    "the stop-carrying duplicate must be fresh");
            };
            _suppressCommandAcks = true;
            _immediateDuplicatePathAck = true;
            _duplicatePathEmergency = emergency;
            _duplicatePathState = state;
            sentAt = Time.realtimeSinceStartupAsDouble;
            Assert.That(_client.TrySendPath(new[] { Vector2.zero, new Vector2(.5f, .25f) },
                out pathSequence, out string error), Is.True, error);
            yield return Pump(.12f);
            Assert.That(received, Is.EqualTo(1), "the stop-carrying duplicate must reach ReceiveAcks");
            Assert.That(receiptApplied, Is.True, "suppress only healthy duplicate receipts");
            Assert.That(sequenceAtReceipt, Is.EqualTo(pathSequence));
            Assert.That(emergencyAtReceipt, Is.EqualTo(emergency));
            Assert.That(faultAtReceipt, Is.EqualTo(state == "fault"));
            Assert.That(_client.Session.NeutralConfirmed, Is.False);
            Assert.That(_client.Session.Armed, Is.False);
        }

        [UnityTest]
        public IEnumerator DelayedCancelReceiptRemainsCorrelatedWithoutRefreshingHeartbeatSession()
        {
            yield return VerifyDelayedCancelReceipt(.35f, .65f, 1);
        }

        [UnityTest]
        public IEnumerator CancelReceiptBeyondOperationWindowIsIgnoredDespiteFreshHeartbeats()
        {
            yield return VerifyDelayedCancelReceipt(1.65f, 1.9f, 0);
        }

        private IEnumerator VerifyDelayedCancelReceipt(float delay, float observation, int expectedReceipts)
        {
            Create();
            yield return Pump(.2f);
            _client.SetPathCommandHold(true);
            yield return Pump(.15f);
            long cancelSequence = 0;
            int operationReceipts = 0, receivedCancelAcks = 0;
            bool staleAckApplied = false;
            _client.PathAckObserved += (ack, kind) =>
            {
                if (kind == "path_cancel" && ack.sequence == cancelSequence) operationReceipts++;
            };
            _client.AckObserved += (ack, applied) =>
            {
                if (ack.sequence != cancelSequence) return;
                receivedCancelAcks++;
                staleAckApplied |= applied;
            };
            _cancelAckDelaySeconds = delay;
            Assert.That(_client.TryCancelPath(out cancelSequence, out string error), Is.True, error);
            yield return Pump(observation);
            Assert.That(_cancels, Is.EqualTo(1));
            Assert.That(receivedCancelAcks, Is.EqualTo(1), "the delayed datagram must actually reach ReceiveAcks");
            Assert.That(operationReceipts, Is.EqualTo(expectedReceipts));
            Assert.That(staleAckApplied, Is.False, "operation receipt age must not widen ordinary heartbeat freshness");
            Assert.That(_client.Session.LastAckSequence, Is.GreaterThan(cancelSequence));
            Assert.That(_client.Session.LastAckKind, Is.EqualTo("command"));
            Assert.That(_client.Session.Connected, Is.True);
            Assert.That(_client.Session.NeutralConfirmed, Is.True);
            Assert.That(_client.Session.Emergency, Is.False);
            Assert.That(_client.maxAckAgeSeconds, Is.EqualTo(.2f));
            Assert.That(_client.ackTimeoutSeconds, Is.EqualTo(.3f));
        }

        [UnityTest]
        public IEnumerator PathIsSentOnceOnExistingSocketAndLateReceiptSurvivesHeartbeatReordering()
        {
            Create();
            yield return Pump(.2f);
            _client.SetPathCommandHold(true);
            yield return Pump(.15f);
            int receipts = 0;
            _client.PathAckObserved += (ack, kind) => { if (kind == "path") receipts++; };
            Assert.That(_client.TrySendPath(new[] { Vector2.zero, new Vector2(.5f, .25f) }, out long sequence, out string error), Is.True, error);
            int commandsBefore = _commands;
            yield return Pump(.35f);
            Assert.That(_paths, Is.EqualTo(1), "the normal 20 Hz loop must not replay a path");
            Assert.That(_commands - commandsBefore, Is.GreaterThanOrEqualTo(3));
            Assert.That(receipts, Is.EqualTo(1), "a later heartbeat ACK must not hide the path receipt");
            Assert.That(_client.Session.LastAckSequence, Is.GreaterThan(sequence));
            Assert.That(_client.Session.NeutralConfirmed, Is.True);
            Assert.That(_client.TryCancelPath(out _, out error), Is.True, error);
            yield return Pump(.15f);
            Assert.That(_cancels, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator EmergencyAndStudyGatesTakePriorityOverPathPackets()
        {
            Create();
            yield return Pump(.2f);
            _client.SetPathCommandHold(true);
            yield return Pump(.15f);
            int cancellationSignals = 0;
            _client.PathCancellationRequested += _ => cancellationSignals++;
            _client.SetStudyMotionBlocked(true);
            Assert.That(cancellationSignals, Is.GreaterThan(0));
            Assert.That(_client.TrySendPath(new[] { Vector2.zero }, out _, out _), Is.False);
            _client.SetStudyMotionBlocked(false);
            yield return Pump(.15f);
            _client.EmergencyStop();
            Assert.That(_client.TrySendPath(new[] { Vector2.zero }, out _, out _), Is.False);
            Assert.That(_client.TryCancelPath(out _, out _), Is.False, "pending emergency event has priority");
            yield return Pump(.15f);
            Assert.That(_emergency, Is.True);
            Assert.That(_paths, Is.Zero);
        }
    }
}
#endif
