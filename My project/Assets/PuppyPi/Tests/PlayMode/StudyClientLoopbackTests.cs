#if UNITY_EDITOR
using System;
using System.Collections;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.TestTools;

namespace PuppyPi.Tests
{
    // Run against tools/study_loopback_bench.py. This tests real HTTP/UDP and
    // serialized C# messages, while all input, camera observations and motors are mock.
    public class StudyClientLoopbackTests
    {
        private GameObject _root;
        private string _endpoint;
        private PuppyPiStudyClient _study;

        [Serializable] private sealed class SessionRequest
        {
            public int schema_version = 1;
            public string request_id = Guid.NewGuid().ToString("N");
            public string participant_id = "UNITY_MOCK_ONLY";
            public string[] interface_order = { "smartphone_touch", "mr_ballgrip" };
        }
        [Serializable] private sealed class TrialRequest
        {
            public int schema_version = 1;
            public string request_id = Guid.NewGuid().ToString("N");
            public string experiment_session_id;
            public string trial_id = Guid.NewGuid().ToString("N");
            public string control_client_id;
            public string interface_id = "mr_ballgrip";
            public int period_index = 2;
            public string difficulty_id = "demo-wide";
            public bool practice = true;
        }

        private IEnumerator Post(string path, object value, Action<StudyStatus> result)
        {
            using (var request = new UnityWebRequest(_endpoint + path, "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonUtility.ToJson(value)));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.timeout = 3;
                yield return request.SendWebRequest();
                Assert.That(request.result, Is.EqualTo(UnityWebRequest.Result.Success), request.downloadHandler.text);
                result?.Invoke(JsonUtility.FromJson<StudyStatus>(request.downloadHandler.text));
            }
        }

        private static IEnumerator WaitUntil(Func<bool> condition, string message, double timeout = 8)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + timeout;
            while (!condition() && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.That(condition(), Is.True, message);
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            if (_study != null && _study.Active) _study.AbortTrial("test_cleanup");
            if (_root != null) UnityEngine.Object.Destroy(_root);
            yield return null;
        }

        [UnityTest]
        public IEnumerator RealPythonServicePreservesGoGrabAndTerminalNeutral()
        {
            string http = Environment.GetEnvironmentVariable("PUPPYPI_STUDY_HTTP_PORT");
            string udp = Environment.GetEnvironmentVariable("PUPPYPI_STUDY_UDP_PORT");
            bool hasHttp = int.TryParse(http, out int httpPort);
            bool hasUdp = int.TryParse(udp, out int udpPort);
            if (!hasHttp || !hasUdp)
                Assert.Ignore("Start the motor-free study bench and supply PUPPYPI_STUDY_HTTP_PORT / PUPPYPI_STUDY_UDP_PORT.");
            _endpoint = "http://127.0.0.1:" + httpPort;
            StudyStatus session = null;
            yield return Post("/v1/session", new SessionRequest(), value => session = value);
            _root = new GameObject("MR study HTTP UDP integration (mock only)");
            _root.SetActive(false);
            var grip = new GameObject("Mock grip");
            grip.transform.SetParent(_root.transform);
            var input = _root.AddComponent<BallGripInput>();
            input.grip = grip.transform;
            input.baseFrame = _root.transform;
            input.editorPreview = true;
            var drive = _root.AddComponent<PuppyPiV2Client>();
            drive.input = input;
            drive.robotAddress = "127.0.0.1";
            drive.port = udpPort;
            drive.networkEnabled = true;
            var controller = _root.AddComponent<PuppyPiRobotController>();
            controller.enabled = false; // The bench supplies no ROS/media endpoints.
            controller.ResolveComponents();
            _study = controller.studyClient;
            _study.presentationPolicy = StudyPresentationPolicy.NoMedia;
            _study.servicePort = httpPort;
            _root.SetActive(true);
            yield return WaitUntil(() => drive.Session.NeutralConfirmed, "loopback v2 neutral ACK");
            Assert.That(_study.EnterStudy("127.0.0.1", out string error), Is.True, error);
            yield return WaitUntil(() => _study.Status != null && _study.Status.ready, "registered client readiness: " + _study.Notice);
            yield return Post("/v1/trials/start", new TrialRequest {
                experiment_session_id = session.experiment_session_id, control_client_id = drive.Session.ClientId
            }, null);
            yield return WaitUntil(() => _study.HasPendingSignal && _study.BlockReason == "study_countdown", "GO signal from Python");
            _study.NotifySignalPresented("mock_signal_submission");
            grip.transform.localRotation = BallGripMath.Rotation(new Vector2(26, 10));
            input.previewGrabbed = true; // React immediately, before the HTTP signal acknowledgement.
            yield return WaitUntil(() => _study.Status != null && _study.Status.state == "running", "signal_shown accepted: " + _study.Notice);
            yield return WaitUntil(() => drive.LastCommand != null && drive.LastCommand.control_active, "the post-GO grab survives server confirmation");
            Assert.That(_study.Status.period_index, Is.EqualTo(2));
            Assert.That(_study.Status.interface_id, Is.EqualTo("mr_ballgrip"));
            grip.transform.localRotation = input.Neutral;
            yield return WaitUntil(() => _study.Status != null && _study.Status.state == "success", "new-frame framing hold succeeds: " + _study.Notice);
            yield return null;
            Assert.That(drive.LastCommand.control_active, Is.False, "terminal outcome neutralizes the client");
            Assert.That(drive.Session.Emergency, Is.False, "normal success never changes the emergency latch");
            _study.ExitStudy();
            yield return WaitUntil(() => !_study.Active, "bounded study exit", 3);
            yield return new WaitForSecondsRealtime(0.2f);
            Assert.That(drive.LastCommand.control_active, Is.False, "normal mode cannot restart a held grip");
            input.previewGrabbed = false;
            yield return new WaitForSecondsRealtime(0.2f);
            input.previewGrabbed = true;
            yield return WaitUntil(() => drive.LastCommand.control_active, "fresh grab restores normal loopback control");
        }
    }
}
#endif
