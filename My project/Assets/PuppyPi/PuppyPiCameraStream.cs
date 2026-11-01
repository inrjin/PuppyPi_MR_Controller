using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace PuppyPi
{
    // Read-only ROS web_video_server stream; no camera/robot commands are sent.
    public sealed class PuppyPiCameraStream : MonoBehaviour
    {
        public bool streamEnabled = true;
        [Range(1, 15)] public int maximumFramesPerSecond = 12;
        public const double SignalTimeoutSeconds = 2;
        private const float RetryDelaySeconds = 2;
        private const string DefaultCameraTopic = "/usb_cam/image_raw";
        private const int MaximumFrameBytes = 2 * 1024 * 1024;
        private const int MaximumDimension = 2048;
        private const long MaximumPixels = 2 * 1024 * 1024;
        private string _host;
        private string _topic;
        private bool _wanted;
        private bool _suspended;
        private Coroutine _routine;
        private UnityWebRequest _request;
        private MjpegFrameParser _parser;
        private Texture2D _texture;
        private double _lastFrameAt = double.NegativeInfinity;
        private double _attemptStarted;
        private double _nextDecodeAt;

        public Texture Texture => _texture;
        public double FrameAgeSeconds => _texture == null ? double.PositiveInfinity :
            Math.Max(0, Time.realtimeSinceStartupAsDouble - _lastFrameAt);
        public bool IsStreaming => _wanted && !_suspended && _request != null &&
            _lastFrameAt >= _attemptStarted && FrameAgeSeconds < SignalTimeoutSeconds;
        public string Status { get; private set; } = "Camera disconnected";
        public string LastError { get; private set; } = "";

        public static string BuildStreamUrl(string host) => BuildStreamUrl(host, DefaultCameraTopic);

        public static string BuildStreamUrl(string host, string topic)
        {
            if (string.IsNullOrWhiteSpace(host) || host != host.Trim() || Uri.CheckHostName(host) == UriHostNameType.Unknown)
                throw new ArgumentException("A camera host name or IP address is required.", nameof(host));
            if (string.IsNullOrEmpty(topic) || topic.Length > 256 || topic[0] != '/')
                throw new ArgumentException("An absolute ROS image topic is required.", nameof(topic));
            foreach (char character in topic)
                if (character != '/' && character != '_' && !(character >= 'a' && character <= 'z') &&
                    !(character >= 'A' && character <= 'Z') && !(character >= '0' && character <= '9'))
                    throw new ArgumentException("The image topic contains unsupported characters.", nameof(topic));
            return new UriBuilder(Uri.UriSchemeHttp, host, 8080)
            {
                Path = "/stream",
                // web_video_server does not decode %2F in topic values. The character
                // whitelist above permits literal ROS topic paths without query injection.
                Query = "topic=" + topic + "&type=ros_compressed"
            }.Uri.AbsoluteUri;
        }

        public void Connect(string host) => Connect(host, DefaultCameraTopic);

        public void Connect(string host, string topic)
        {
            try { BuildStreamUrl(host, topic); }
            catch (ArgumentException)
            {
                Disconnect();
                Status = "Invalid media address";
                return;
            }
            if (_wanted && string.Equals(_host, host, StringComparison.OrdinalIgnoreCase) && _topic == topic)
            {
                EnsureRunning();
                return;
            }
            Disconnect();
            _host = host;
            _topic = topic;
            _wanted = true;
            EnsureRunning();
        }

        public void Disconnect()
        {
            _wanted = false;
            _host = null;
            _topic = null;
            StopSession();
            ClearTexture();
            Status = "Camera disconnected";
            LastError = "";
        }

        private void OnEnable()
        {
            _suspended = false;
            EnsureRunning();
        }

        private void OnDisable()
        {
            _suspended = true;
            StopSession();
            ClearTexture();
            Status = _wanted ? "Camera paused" : "Camera disconnected";
        }

        private void OnDestroy() => Disconnect();

        private void OnApplicationPause(bool paused)
        {
            _suspended = paused;
            if (paused)
            {
                StopSession();
                ClearTexture();
                Status = _wanted ? "Camera paused" : "Camera disconnected";
            }
            else EnsureRunning();
        }

        private void EnsureRunning()
        {
            if (!_wanted || _suspended || !isActiveAndEnabled || _routine != null) return;
            if (!streamEnabled) { Status = "Camera disabled"; return; }
            Status = "Connecting camera";
            _routine = StartCoroutine(Receive());
        }

        private void StopSession()
        {
            if (_routine != null) { StopCoroutine(_routine); _routine = null; }
            ReleaseRequest();
        }

        private void ReleaseRequest()
        {
            if (_parser != null) { _parser.Close(); _parser = null; }
            if (_request != null)
            {
                _request.Abort();
                _request.Dispose();
                _request = null;
            }
        }

        private void ClearTexture()
        {
            if (_texture != null) Destroy(_texture);
            _texture = null;
            _lastFrameAt = double.NegativeInfinity;
            _nextDecodeAt = 0;
        }

        private IEnumerator Receive()
        {
            while (_wanted && !_suspended && streamEnabled)
            {
                UnityWebRequestAsyncOperation operation = null;
                string setupFailure = null;
                try
                {
                    _parser = new MjpegFrameParser(MaximumFrameBytes);
                    _request = new UnityWebRequest(BuildStreamUrl(_host, _topic), UnityWebRequest.kHttpVerbGET)
                    {
                        downloadHandler = new FrameDownloadHandler(_parser),
                        disposeDownloadHandlerOnDispose = true,
                        timeout = 0,
                        redirectLimit = 0
                    };
                    _request.SetRequestHeader("Accept", "multipart/x-mixed-replace,image/jpeg");
                    _attemptStarted = Time.realtimeSinceStartupAsDouble;
                    if (Status != "NO SIGNAL") Status = "Waiting for camera";
                    operation = _request.SendWebRequest();
                }
                catch (Exception exception) when (exception is UnityException || exception is InvalidOperationException ||
                    exception is ArgumentException)
                {
                    setupFailure = "Camera request could not start: " + exception.Message;
                }
                if (setupFailure != null)
                {
                    ReleaseRequest();
                    LastError = setupFailure;
                    Status = "NO SIGNAL";
                    yield return new WaitForSecondsRealtime(RetryDelaySeconds);
                    continue;
                }
                while (!operation.isDone && _wanted && !_suspended && streamEnabled)
                {
                    double now = Time.realtimeSinceStartupAsDouble;
                    double lastProgress = Math.Max(_attemptStarted, _lastFrameAt);
                    if (_request.responseCode >= 400 || now - lastProgress >= SignalTimeoutSeconds)
                    {
                        _request.Abort();
                        break;
                    }
                    yield return null;
                }
                string failure = _request.responseCode >= 400 ? "Camera HTTP " + _request.responseCode :
                    string.IsNullOrEmpty(_request.error) ? "Camera stream ended" : "Camera unavailable: " + _request.error;
                ReleaseRequest();
                if (!_wanted || _suspended || !streamEnabled) break;
                LastError = failure;
                Status = "NO SIGNAL";
                yield return new WaitForSecondsRealtime(RetryDelaySeconds);
            }
            _routine = null;
        }

        private void Update()
        {
            if (!streamEnabled)
            {
                if (_routine != null || _request != null) { StopSession(); ClearTexture(); }
                if (_wanted) Status = "Camera disabled";
                return;
            }
            EnsureRunning();
            double now = Time.realtimeSinceStartupAsDouble;
            if (!_wanted || _suspended || _parser == null || now < _nextDecodeAt) return;
            _nextDecodeAt = now + 1.0 / Mathf.Clamp(maximumFramesPerSecond, 1, 15);
            if (!_parser.TryTakeLatest(out byte[] jpeg)) return;
            if (!JpegFrameInfo.TryGetDimensions(jpeg, MaximumDimension, MaximumPixels, out _, out _))
            {
                Status = "Invalid camera frame";
                return;
            }
            if (_texture == null)
            {
                _texture = new Texture2D(2, 2, TextureFormat.RGB24, false)
                {
                    name = "PuppyPi camera",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
            }
            try
            {
                if (!ImageConversion.LoadImage(_texture, jpeg, false)) { Status = "Invalid camera image"; return; }
                _lastFrameAt = now;
                Status = "Camera live";
                LastError = "";
            }
            catch (Exception exception) when (exception is UnityException || exception is ArgumentException)
            {
                Status = "Camera image could not be decoded";
            }
        }

        private sealed class FrameDownloadHandler : DownloadHandlerScript
        {
            private readonly MjpegFrameParser _parser;

            public FrameDownloadHandler(MjpegFrameParser parser) : base(new byte[64 * 1024]) { _parser = parser; }

            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                if (data == null || dataLength <= 0) return true;
                _parser.Append(data, 0, dataLength);
                return true;
            }
        }
    }
}
