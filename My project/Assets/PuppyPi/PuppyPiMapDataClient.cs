using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace PuppyPi
{
    // Read-only connection: large grids never queue behind motion/service traffic.
    // The raw grid supplies coordinates and previously observed free cells.
    // Current confirmed obstacles override that prior; accumulated obstacle
    // points are not imported. Exact scan/pose pairing remains mandatory.
    public sealed class PuppyPiMapDataClient : MonoBehaviour
    {
        public Texture2D Texture { get; private set; }
        public MapGridGeometry Geometry { get; private set; }
        public int[] Occupancy { get; private set; }
        public int[] BaseOccupancy { get; private set; }
        public string RouteGridStatus { get; private set; } = "";
        public int CurrentObstacleCellCount { get; private set; }
        public int CurrentFreeCellCount { get; private set; }
        public int CurrentUnknownCellCount { get; private set; }
        public int RawObstacleCellCount => _obstacleFilter.RawOccupiedCount;
        public int CandidateObstacleCellCount => _obstacleFilter.CandidateCellCount;
        public int ConfirmedObstacleCellCount => CurrentObstacleCellCount;
        public long NavigationObservationStamp => _layerScanStamp;
        public double NavigationObservationAt => Math.Min(_layerScanAt, _layerPoseAt);
        public string FilterStatus => !_hasLayer ? "FILTER | Waiting for a fresh scan" :
            $"FILTER READY | {ConfirmedObstacleCellCount} confirmed, {CandidateObstacleCellCount} checking | map free {MapFreeCellCount}";
        public int MapFreeCellCount => _knownFreeLayer.ReusedCellCount;
        public int RecentFreeCellCount => _recentFreeIndices.Count;
        public double RecentFreeWindowSeconds => Math.Min(1.0, FreshSeconds);
        public Vector3 RobotPose { get; private set; }
        public string SessionId { get; private set; } = "";
        public long Revision { get; private set; }
        [Range(0.6f, 1.5f)] public float navigationFreshSeconds = 1.25f;
        public bool EditingAvailable => Geometry != null && Geometry.IsValid && Texture != null &&
            BaseOccupancy != null && !string.IsNullOrEmpty(SessionId);
        // Cached pose is for presentation/editing, never a permission to move.
        public bool PoseAvailable => EditingAvailable && _poseStamp >= 0 && _poseEpoch >= 0 &&
            _transport != null && _poseEpoch == _transport.Epoch;
        public double PoseAgeSeconds => Math.Max(0, Now - _poseAt);
        public double ScanAgeSeconds => Math.Max(0, Now - _lastScanAt);
        public double NavigationAgeSeconds => Math.Max(0, Math.Max(Now - _layerScanAt, Now - _layerPoseAt));
        public bool HasNavigationPose => PoseAvailable && _transport.Connected && PoseAgeSeconds < FreshSeconds;
        public bool HasPose => _transport != null && _transport.Connected && _poseEpoch == _transport.Epoch &&
            Time.realtimeSinceStartupAsDouble - _poseAt < 0.6;
        public float AgeSeconds => (float)(Time.realtimeSinceStartupAsDouble - _gridAt);
        public bool Live => _transport != null && _transport.Connected && _gridEpoch == _transport.Epoch &&
            Geometry != null && Geometry.IsValid && AgeSeconds < 6;
        public bool NavigationLive => Live && HasNavigationPose && !string.IsNullOrEmpty(SessionId) && _hasLayer &&
            _calibration != null && _calibrationEpoch == _transport.Epoch && _layerEpoch == _transport.Epoch &&
            _layerBaseRevision == _baseRevision && Geometry.frame == _calibration.MapFrame &&
            NavigationAgeSeconds < FreshSeconds;
        public bool BriefSensorGap => NavigationLive && (NavigationAgeSeconds >= .6 || PoseAgeSeconds >= .6);
        public string NavigationStatus => NavigationLive ? BriefSensorGap ?
            $"UPDATING SCAN | Verified frame age {NavigationAgeSeconds:F1}s / {FreshSeconds:F2}s" : "LIVE LIDAR | Current scan and matched pose" :
            "NO SIGNAL | " + _navigationStatus;
        public string Status => NavigationStatus;

        private const int MaxCells = 1024 * 1024;
        private const int MaxMessageBytes = 6 * 1024 * 1024;
        private const int MaxPendingSensors = 16, MaxSensorQueueBytes = 1024 * 1024;
        // puppy_lidar.mapper_core ignores scans below this count and may
        // republish its previous pose with the new scan's source stamp.
        private const int MinimumLocalizationRays = 30;
        private Transport _transport;
        private string _host = "", _error = "";
        private int _port = 9090, _gridEpoch = -1, _poseEpoch = -1;
        private double _gridAt = double.NegativeInfinity, _poseAt = double.NegativeInfinity;
        private long _poseStamp = -1;
        private bool _paused;
        private int _activeEpoch = -1, _calibrationEpoch = -1, _layerEpoch = -1;
        private long _scanStamp = -1;
        private long _baseRevision, _layerBaseRevision = -1, _layerScanStamp = -1;
        private double _layerScanAt = double.NegativeInfinity, _layerPoseAt = double.NegativeInfinity;
        private bool _hasLayer;
        private bool _processingBatch;
        private string _navigationStatus = "Read robot laser calibration and wait for a fresh scan.", _scanFrame;
        private Calibration _calibration;
        private ScanSample _latestScan;
        private ScanSample _confirmedScan;
        private PoseSample _confirmedPose;
        private readonly List<PoseSample> _poseHistory = new List<PoseSample>();
        private readonly List<ScanSample> _pendingScans = new List<ScanSample>();
        private Color32[] _textureColors;
        private double[] _recentFreeObservedAt;
        private MapGridGeometry _recentFreeGeometry;
        private readonly List<int> _recentFreeIndices = new List<int>();
        private readonly MapObstacleFilter _obstacleFilter = new MapObstacleFilter();
        private readonly MapKnownFreeLayer _knownFreeLayer = new MapKnownFreeLayer();
        private readonly HashSet<int> _candidateObstacleCells = new HashSet<int>();
        private int[] _confirmedObstacleEvidence;
        private long _routeGridRevision = long.MinValue;
        private MapGridGeometry _routeGridSource, _routeGridSourceValues, _routeGridGeometry;
        private int[] _routeGridEvidence, _routeGridCells;
        private float _routeGridLength, _routeGridWidth, _routeGridMargin;
        private string _routeGridError;
        private static double Now => Time.realtimeSinceStartupAsDouble;
        private double FreshSeconds => Finite(navigationFreshSeconds) ? Math.Max(.6, Math.Min(1.5, navigationFreshSeconds)) : 1.25;
        private double _lastScanAt = double.NegativeInfinity, _lastPoseReceiveAt = double.NegativeInfinity;
        private double _scanPeriod, _posePeriod, _nextDiagnosticAt;
        private long _receivedScans, _receivedPoses, _matchedScans, _expiredScans, _rejectedSensors;
        private const string CalibrationService = "/lidar_mapper/get_parameters";
        private static readonly string[] CalibrationNames = { "laser_x", "laser_y", "laser_yaw", "map_frame", "base_frame", "min_range", "max_range" };

        private sealed class Calibration
        {
            public Vector3 Laser;
            public string MapFrame, BaseFrame;
            public double MinRange, MaxRange;
        }
        private sealed class PoseSample { public long Stamp; public Vector3 Pose; public double At; public int Epoch; }
        private sealed class ScanSample
        {
            public long Stamp;
            public float[] Ranges;
            public float AngleMin, AngleIncrement, MinRange, MaxRange;
            public double At;
            public int Epoch;
        }
        private sealed class SensorMessage { public string Json; public double At; public int Epoch, Bytes; }

        private sealed class Transport
        {
            public readonly object Gate = new object();
            public readonly CancellationTokenSource Cancel = new CancellationTokenSource();
            public ClientWebSocket Socket;
            public volatile bool Connected;
            public volatile string Error = "";
            public int Epoch;
            public string Grid, Pose, Scan, Calibration, CalibrationId;
            public double GridAt, PoseAt, ScanAt;
            public int GridEpoch, PoseEpoch, ScanEpoch, CalibrationEpoch;
            public readonly Queue<SensorMessage> Poses = new Queue<SensorMessage>(), Scans = new Queue<SensorMessage>();
            public int SensorBytes;
            public long DroppedSensors;
        }

        [Serializable] private sealed class Stamp { public long sec, secs; public int nanosec, nsecs; }
        [Serializable] private sealed class Header { public string frame_id; public Stamp stamp; }
        [Serializable] private sealed class Point { public double x, y, z; }
        [Serializable] private sealed class Rotation { public double x, y, z, w; }
        [Serializable] private sealed class Pose { public Point position; public Rotation orientation; }
        [Serializable] private sealed class Info { public int width, height; public float resolution; public Pose origin; }
        [Serializable] private sealed class GridMessage { public Header header; public Info info; public int[] data; }
        [Serializable] private sealed class PoseMessage { public Header header; public Pose pose; }
        [Serializable] private sealed class GridPublication { public string op, topic; public GridMessage msg; }
        [Serializable] private sealed class PosePublication { public string op, topic; public PoseMessage msg; }
        [Serializable] private sealed class Envelope { public string op, topic, service, id; }

        public void Connect(string host, int port = 9090)
        {
            if (host == _host && port == _port && _transport != null) return;
            Disconnect();
            if (string.IsNullOrWhiteSpace(host) || Uri.CheckHostName(host) == UriHostNameType.Unknown || port < 1 || port > 65535)
                return;
            _host = host;
            _port = port;
            if (isActiveAndEnabled && !_paused) StartTransport();
        }

        public void SetSession(string session)
        {
            session = session ?? "";
            if (SessionId == session) return;
            _knownFreeLayer.Reset();
            SessionId = session;
            _poseStamp = -1;
            _poseAt = double.NegativeInfinity;
            _gridAt = double.NegativeInfinity;
            Geometry = null;
            Occupancy = null;
            BaseOccupancy = null;
            Revision++;
            // Discard messages queued before the new map generation. A fresh
            // subscription is required before old coordinates can become live.
            StopTransport();
            DestroyTexture();
            if (isActiveAndEnabled && !_paused) StartTransport();
        }

        public void Disconnect()
        {
            StopTransport();
            _knownFreeLayer.Reset();
            _host = "";
            SessionId = "";
            Geometry = null;
            Occupancy = null;
            BaseOccupancy = null;
            _poseStamp = -1;
            _gridAt = _poseAt = double.NegativeInfinity;
            Revision++;
            DestroyTexture();
        }

        private void StartTransport()
        {
            if (_transport != null || string.IsNullOrEmpty(_host)) return;
            var transport = new Transport();
            _transport = transport;
            Uri endpoint = new UriBuilder("ws", _host, _port, "/").Uri;
            _ = Task.Run(() => Receive(transport, endpoint));
        }

        private void StopTransport()
        {
            Transport old = _transport;
            _transport = null;
            _gridEpoch = _poseEpoch = -1;
            _activeEpoch = -1;
            ResetNavigation("Connection/session changed; fresh calibration, pose and scan required.", true, true);
            if (old == null) return;
            old.Connected = false;
            old.Cancel.Cancel();
            lock (old.Gate) old.Socket?.Abort();
        }

        private void Update()
        {
            Transport transport = _transport;
            if (transport == null) return;
            _error = transport.Error;
            if (!transport.Connected)
            {
                if (_hasLayer || _calibration != null) ResetNavigation("ROS bridge disconnected; editing cached map.", true, true, true);
                LogDiagnosticPulse();
                return;
            }
            if (_activeEpoch != transport.Epoch)
            {
                ResetNavigation("New connection; waiting for laser calibration and synchronized scan.", true, true, true);
                _activeEpoch = transport.Epoch;
                _poseStamp = -1;
                _gridAt = _poseAt = double.NegativeInfinity;
                _gridEpoch = _poseEpoch = -1;
            }
            string grid, calibration, calibrationId;
            SensorMessage[] poses, scans;
            double gridAt;
            int gridEpoch, calibrationEpoch;
            lock (transport.Gate)
            {
                grid = transport.Grid;
                calibration = transport.Calibration; calibrationId = transport.CalibrationId;
                gridAt = transport.GridAt;
                gridEpoch = transport.GridEpoch; calibrationEpoch = transport.CalibrationEpoch;
                poses = transport.Poses.ToArray(); scans = transport.Scans.ToArray();
                transport.Poses.Clear(); transport.Scans.Clear(); transport.SensorBytes = 0;
                transport.Grid = transport.Pose = transport.Scan = transport.Calibration = null;
            }
            double receiveClock = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
            double unityClock = Now;
            if (calibration != null && calibrationEpoch == transport.Epoch && ApplyCalibration(calibration, calibrationId))
                _calibrationEpoch = calibrationEpoch;
            if (grid != null && gridEpoch == transport.Epoch && ApplyGrid(grid))
            { _gridAt = unityClock - Math.Max(0, receiveClock - gridAt); _gridEpoch = gridEpoch; }
            _processingBatch = true;
            try
            {
                foreach (var pose in poses)
                {
                    double at = unityClock - Math.Max(0, receiveClock - pose.At);
                    if (pose.Epoch == transport.Epoch && ApplyPoseAt(pose.Json, at, pose.Epoch))
                    { _poseAt = at; _poseEpoch = pose.Epoch; }
                }
                foreach (var scan in scans)
                    if (scan.Epoch == transport.Epoch)
                        ApplyScanAt(scan.Json, unityClock - Math.Max(0, receiveClock - scan.At), scan.Epoch);
            }
            finally { _processingBatch = false; }
            TryApplyLatestScan();
            RefreshNavigationFreshness();
            ExpireRecentFreeEvidence();
            LogDiagnosticPulse();
        }

        // Public parsers keep map validation independently testable without a robot.
        public bool ApplyGrid(string json)
        {
            try
            {
                GridPublication publication = JsonUtility.FromJson<GridPublication>(json);
                GridMessage msg = publication?.msg;
                Info info = msg?.info;
                if (publication?.op != "publish" || publication.topic != "/map" || info == null ||
                    msg.header == null || string.IsNullOrEmpty(msg.header.frame_id) || msg.header.frame_id.Length > 128 ||
                    info.width < 1 || info.height < 1 || info.width > 1024 || info.height > 1024 ||
                    (long)info.width * info.height > MaxCells || msg.data == null || msg.data.Length != info.width * info.height ||
                    !Finite(info.resolution) || info.resolution < 0.005f || info.resolution > 1 ||
                    !ValidPose(info.origin) || Math.Abs(info.origin.orientation.x) > 1e-5 ||
                    Math.Abs(info.origin.orientation.y) > 1e-5 || Math.Abs(info.origin.orientation.z) > 1e-5 ||
                    Math.Abs(Math.Abs(info.origin.orientation.w) - 1) > 1e-5) return false;
                foreach (int value in msg.data) if (value < -1 || value > 100) return false;
                int left = info.width, bottom = info.height, right = -1, top = -1;
                for (int y = 0; y < info.height; y++)
                    for (int x = 0; x < info.width; x++)
                        if (msg.data[y * info.width + x] >= 0)
                        { left = Math.Min(left, x); right = Math.Max(right, x); bottom = Math.Min(bottom, y); top = Math.Max(top, y); }
                if (right < left) { left = bottom = 0; right = info.width - 1; top = info.height - 1; }
                else
                {
                    int margin = Mathf.CeilToInt(1f / info.resolution);
                    int minimum = Mathf.CeilToInt(6f / info.resolution);
                    int cx = (left + right) / 2, cy = (bottom + top) / 2;
                    left = Math.Max(0, Math.Min(left - margin, cx - minimum / 2));
                    right = Math.Min(info.width - 1, Math.Max(right + margin, cx + minimum / 2));
                    bottom = Math.Max(0, Math.Min(bottom - margin, cy - minimum / 2));
                    top = Math.Min(info.height - 1, Math.Max(top + margin, cy + minimum / 2));
                }
                int width = right - left + 1, height = top - bottom + 1;
                var cells = new int[width * height];
                for (int y = 0; y < height; y++) Array.Copy(msg.data, (bottom + y) * info.width + left, cells, y * width, width);
                var geometry = new MapGridGeometry { width = width, height = height,
                    resolution = info.resolution, origin = new Vector2((float)info.origin.position.x + left * info.resolution,
                        (float)info.origin.position.y + bottom * info.resolution),
                    frame = msg.header.frame_id };
                if (!geometry.IsValid) return false;
                if (Geometry != null && Geometry.frame != geometry.frame)
                {
                    _poseAt = double.NegativeInfinity;
                    _poseEpoch = -1;
                    _poseStamp = -1;
                    ResetNavigation("Map coordinate frame changed.", false, true);
                }
                if (Texture == null || Texture.width != width || Texture.height != height)
                {
                    DestroyTexture();
                    Texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
                    { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "PuppyPi coordinate map" };
                }
                Geometry = geometry;
                BaseOccupancy = cells;
                Occupancy = UnknownCells(cells.Length);
                _knownFreeLayer.ClearPresentation();
                _baseRevision++;
                RenderCurrentCells();
                TryApplyLatestScan();
                // Rebase a still-fresh verified scan onto a replacement raw grid
                // while a newer scan waits for its exact pose callback.
                if (_hasLayer && _layerBaseRevision != _baseRevision && _confirmedScan != null && _confirmedPose != null &&
                    Now - _confirmedScan.At < FreshSeconds && Now - _confirmedPose.At < FreshSeconds)
                    ApplyVerifiedScan(_confirmedScan, _confirmedPose);
                Revision++;
                return true;
            }
            catch (ArgumentException) { return false; }
        }

        public bool ApplyPose(string json) => ApplyPoseAt(json, Now, _transport?.Epoch ?? -1);

        private bool ApplyPoseAt(string json, double receivedAt, int epoch)
        {
            try
            {
                PosePublication publication = JsonUtility.FromJson<PosePublication>(json);
                PoseMessage msg = publication?.msg;
                if (publication?.op != "publish" || publication.topic != "/lidar_map/pose" ||
                    msg?.header?.stamp == null || !ValidPose(msg.pose) || Geometry == null || msg.header.frame_id != Geometry.frame)
                { ResetNavigation("Robot pose has an invalid coordinate frame.", false, false); return false; }
                Stamp stamp = msg.header.stamp;
                long seconds = stamp.sec != 0 ? stamp.sec : stamp.secs;
                int nanos = stamp.nanosec != 0 ? stamp.nanosec : stamp.nsecs;
                if (seconds < 0 || nanos < 0 || nanos >= 1000000000 || seconds > (long.MaxValue - nanos) / 1000000000L)
                    return RejectSensor("Invalid robot pose timestamp.");
                long sourceTime = seconds * 1000000000L + nanos;
                if (sourceTime <= _poseStamp)
                    return RejectSensor("Old/repeated robot pose ignored without refreshing its age.");
                Rotation q = msg.pose.orientation;
                double norm = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
                if (Math.Abs(norm - 1) > 0.02)
                    return RejectSensor("Invalid robot pose orientation.");
                double yaw = Math.Atan2(2 * (q.w * q.z + q.x * q.y), 1 - 2 * (q.y * q.y + q.z * q.z));
                RobotPose = new Vector3((float)msg.pose.position.x, (float)msg.pose.position.y, (float)yaw);
                _poseStamp = sourceTime;
                _receivedPoses++;
                UpdatePeriod(ref _posePeriod, _lastPoseReceiveAt, receivedAt);
                _lastPoseReceiveAt = receivedAt;
                _poseHistory.Add(new PoseSample { Stamp = sourceTime, Pose = RobotPose, At = receivedAt, Epoch = epoch });
                while (_poseHistory.Count > 64 || (_poseHistory.Count > 0 && receivedAt - _poseHistory[0].At > 2))
                    _poseHistory.RemoveAt(0);
                // Consume the previous pending scan now. Update may also have
                // queued scan N+1, which must not replace scan N before pose N pairs.
                if (!_processingBatch) TryApplyLatestScan();
                return true;
            }
            catch (ArgumentException)
            { return RejectSensor("Invalid robot pose message."); }
        }

        // Only this read-only RPC is permitted on the map connection. IDs bind a
        // response to one physical WebSocket connection, never another session.
        public static string CalibrationRequest(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 128 || !Regex.IsMatch(id, "^[a-zA-Z0-9_-]+$"))
                throw new ArgumentException("Invalid calibration request ID", nameof(id));
            return "{\"op\":\"call_service\",\"service\":\"" + CalibrationService + "\",\"id\":\"" + id +
                "\",\"args\":{\"names\":[\"" + string.Join("\",\"", CalibrationNames) + "\"]}}";
        }

        public bool ApplyCalibration(string json, string expectedId)
        {
            if (string.IsNullOrEmpty(expectedId) || !MapJson.TryRead(json, out var value) ||
                !(value is Dictionary<string, object> root) || !Text(root, "op", out string op) || op != "service_response" ||
                !Text(root, "service", out string service) || service != CalibrationService ||
                !Text(root, "id", out string id) || id != expectedId) return false;
            if (!root.TryGetValue("result", out object result) || !(result is bool ok) || !ok ||
                !Object(root, "values", out var response) || !response.TryGetValue("values", out object items) ||
                !(items is List<object> list) || list.Count != CalibrationNames.Length)
            { ResetNavigation("Laser calibration RPC failed.", true, true); return false; }
            var numbers = new double[7];
            var frames = new string[2];
            for (int i = 0; i < list.Count; i++)
            {
                if (!(list[i] is Dictionary<string, object> parameter) || !Number(parameter, "type", out double type))
                { ResetNavigation("Invalid laser calibration response.", true, true); return false; }
                if (i == 3 || i == 4)
                {
                    if (type != 4 || !Text(parameter, "string_value", out string frame) || string.IsNullOrWhiteSpace(frame) || frame.Length > 128)
                    { ResetNavigation("Missing laser calibration coordinate frames.", true, true); return false; }
                    frames[i - 3] = frame;
                }
                else if (type != 3 || !Number(parameter, "double_value", out numbers[i]))
                { ResetNavigation("Missing laser calibration numeric values.", true, true); return false; }
            }
            if (Math.Abs(numbers[0]) > 10 || Math.Abs(numbers[1]) > 10 || Math.Abs(numbers[2]) > 100 ||
                numbers[5] <= 0 || numbers[6] <= numbers[5] || numbers[6] > 100)
            { ResetNavigation("Laser calibration is outside supported bounds.", true, true); return false; }
            var calibration = new Calibration { Laser = new Vector3((float)numbers[0], (float)numbers[1], (float)numbers[2]),
                MapFrame = frames[0], BaseFrame = frames[1], MinRange = numbers[5], MaxRange = numbers[6] };
            bool changed = _calibration != null && (_calibration.Laser != calibration.Laser || _calibration.MapFrame != calibration.MapFrame ||
                _calibration.BaseFrame != calibration.BaseFrame || _calibration.MinRange != calibration.MinRange || _calibration.MaxRange != calibration.MaxRange);
            if (changed) ResetNavigation("Laser calibration changed; waiting for new pose and scan.", false, true);
            _calibration = calibration;
            _calibrationEpoch = _transport?.Epoch ?? -1;
            _navigationStatus = "Waiting for a fresh LiDAR scan and time-matched pose.";
            return true;
        }

        public bool ApplyScan(string json) => ApplyScanAt(json, Now, _transport?.Epoch ?? -1);

        private bool ApplyScanAt(string json, double receivedAt, int epoch)
        {
            if (!MapJson.TryRead(json, out var value) || !(value is Dictionary<string, object> root) ||
                !Text(root, "op", out string op) || op != "publish" || !Text(root, "topic", out string topic) || topic != "/scan" ||
                !Object(root, "msg", out var msg) || !Object(msg, "header", out var header) ||
                !Text(header, "frame_id", out string frame) || string.IsNullOrWhiteSpace(frame) || frame.Length > 128 ||
                !Object(header, "stamp", out var stamp) || !SourceStamp(stamp, out long sourceTime) ||
                !Number(msg, "angle_min", out double angleMin) || !Number(msg, "angle_increment", out double increment) ||
                !Number(msg, "angle_max", out double angleMax) || Math.Abs(angleMin) > 100 || Math.Abs(increment) < 1e-7 || Math.Abs(increment) > 6.3 ||
                !Number(msg, "range_min", out double min) || !Number(msg, "range_max", out double max) || min <= 0 || max <= min || max > 100 ||
                !msg.TryGetValue("ranges", out object ranges) || !(ranges is List<object> array) || array.Count == 0 || array.Count > 2048 ||
                Math.Abs(increment * (array.Count - 1)) > 6.4 ||
                Math.Abs(angleMax - (angleMin + increment * (array.Count - 1))) > Math.Max(.0001, Math.Abs(increment) * .5))
                return RejectSensor("Invalid LiDAR scan.");
            if (sourceTime <= _scanStamp || Now - receivedAt >= FreshSeconds)
                return RejectSensor("Old/repeated LiDAR scan ignored without refreshing its age.");
            if (_scanFrame != null && _scanFrame != frame)
            { ResetNavigation("Laser coordinate frame changed. Reconnect to read calibration.", true, true); return false; }
            var values = new float[array.Count];
            for (int i = 0; i < values.Length; i++)
            {
                if (array[i] == null) values[i] = float.NaN;
                else if (array[i] is double number && number >= -float.MaxValue && number <= float.MaxValue) values[i] = (float)number;
                else return RejectSensor("Invalid LiDAR range value.");
            }
            _scanFrame = frame;
            _scanStamp = sourceTime;
            _receivedScans++;
            UpdatePeriod(ref _scanPeriod, _lastScanAt, receivedAt);
            _lastScanAt = receivedAt;
            _latestScan = new ScanSample { Stamp = sourceTime, Ranges = values, AngleMin = (float)angleMin,
                AngleIncrement = (float)increment, MinRange = (float)min, MaxRange = (float)max, At = receivedAt, Epoch = epoch };
            _pendingScans.Add(_latestScan);
            while (_pendingScans.Count > MaxPendingSensors) { _pendingScans.RemoveAt(0); _expiredScans++; }
            if (!_processingBatch) TryApplyLatestScan();
            return true;
        }

        private void TryApplyLatestScan()
        {
            if (Geometry == null || BaseOccupancy == null) return;
            if (_calibration == null || Geometry.frame != _calibration.MapFrame)
            { _navigationStatus = "Laser calibration is unavailable or belongs to another map frame."; return; }
            for (int i = _pendingScans.Count - 1; i >= 0; i--)
            {
                var pending = _pendingScans[i];
                if (pending.Epoch != _calibrationEpoch || Now - pending.At >= FreshSeconds)
                { _pendingScans.RemoveAt(i); _expiredScans++; }
            }
            ScanSample scan = null;
            PoseSample pose = null;
            // Pose callbacks can follow several newer raw scans. Keep bounded
            // pending observations and choose the newest exact pair, not merely
            // the single newest scan. Source timestamps are never approximated.
            foreach (var pending in _pendingScans)
            {
                if (pending.Stamp <= _layerScanStamp) continue;
                foreach (var sample in _poseHistory)
                    if (sample.Epoch == pending.Epoch && sample.Stamp == pending.Stamp && Now - sample.At < FreshSeconds &&
                        (scan == null || pending.Stamp > scan.Stamp))
                    { scan = pending; pose = sample; }
            }
            if (scan != null)
            {
                if (ApplyVerifiedScan(scan, pose)) _pendingScans.RemoveAll(p => p.Stamp <= scan.Stamp);
                else _pendingScans.Remove(scan);
            }
            else if (_hasLayer && _layerBaseRevision != _baseRevision && _confirmedScan != null && _confirmedPose != null &&
                Now - _confirmedScan.At < FreshSeconds && Now - _confirmedPose.At < FreshSeconds)
                ApplyVerifiedScan(_confirmedScan, _confirmedPose);
            else if (!_hasLayer)
                _navigationStatus = "Waiting for an exact source-stamp LiDAR/pose pair; editing remains available.";
        }

        private bool ApplyVerifiedScan(ScanSample scan, PoseSample closest)
        {
            // Use the same verified sensor validity window as the mapper. In
            // particular, body/leg echoes below its configured minimum must not
            // become MR-only obstacles that the robot map never displays.
            // ROS ranges are float32, but mapper parameters retain double
            // precision. Round inward so a cast cannot admit a value the
            // mapper rejected just outside its configured validity window.
            float minimum = InwardRangeBound(Math.Max(scan.MinRange, _calibration.MinRange), true);
            float maximum = InwardRangeBound(Math.Min(scan.MaxRange, _calibration.MaxRange), false);
            int localizationRays = 0;
            foreach (float range in scan.Ranges)
                if (Finite(range) && range >= minimum && range <= maximum) localizationRays++;
            if (localizationRays < MinimumLocalizationRays)
            {
                // An explicitly unusable localization frame revokes the old
                // layer immediately. A fresh header on an unchanged pose must
                // never extend cached permission to continue driving.
                _rejectedSensors++;
                ResetNavigation($"LiDAR position estimation unavailable: {localizationRays}/{MinimumLocalizationRays} valid measured rays.", false, false);
                return false;
            }
            if (!MapLaserLayer.TryBuildCurrent(Geometry, closest.Pose, _calibration.Laser, scan.Ranges,
                scan.AngleMin, scan.AngleIncrement, minimum, maximum, out int[] rawCells, out string error))
                return RejectSensor(error ?? "LiDAR navigation layer could not be verified.");
            double observedAt = Math.Min(scan.At, closest.At);
            if (!_obstacleFilter.TryApply(Geometry, rawCells, scan.Stamp, observedAt, Now,
                out int[] cells, out error)) return RejectSensor(error ?? "Obstacle evidence could not be verified.");
            // Only direct scan evidence may revoke or restore a map-free prior.
            // Neither recent-free reuse nor repeated map publications can clear
            // a confirmed obstacle's veto after its beam goes missing.
            if (!_knownFreeLayer.Observe(Geometry, cells, out error))
            {
                ResetNavigation(error ?? "Known map free space could not be verified.", false, false, true, true);
                return false;
            }
            _candidateObstacleCells.Clear();
            foreach (int cell in _obstacleFilter.CandidateIndices) _candidateObstacleCells.Add(cell);
            MergeRecentFreeEvidence(cells, observedAt);
            // Keep map history separate from measured-free timestamps. A prior
            // fills missing evidence, but never creates a new scan observation.
            if (!_knownFreeLayer.TryMerge(BaseOccupancy, cells, out error))
            {
                ResetNavigation(error ?? "Known map free space could not be merged.", false, false, true, true);
                return false;
            }
            Occupancy = cells;
            if (_layerScanStamp != scan.Stamp) _matchedScans++;
            _hasLayer = true;
            _layerEpoch = scan.Epoch;
            _layerScanAt = scan.At;
            _layerPoseAt = closest.At;
            _layerScanStamp = scan.Stamp;
            _layerBaseRevision = _baseRevision;
            _confirmedScan = scan;
            _confirmedPose = closest;
            _navigationStatus = "Fresh scan was paired; waiting for current connection, grid and pose.";
            RenderCurrentCells();
            Revision++;
            return true;
        }

        private static float InwardRangeBound(double value, bool minimum)
        {
            float bound = (float)value;
            if ((minimum && bound < value) || (!minimum && bound > value))
                bound = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(bound) + (minimum ? 1 : -1));
            return bound;
        }

        private void ResetNavigation(string reason, bool clearCalibration, bool clearHistory,
            bool preservePresentation = false, bool preservePending = false)
        {
            bool redraw = Geometry != null && Texture != null;
            _obstacleFilter.Reset();
            _candidateObstacleCells.Clear();
            _confirmedObstacleEvidence = null;
            _knownFreeLayer.ClearPresentation();
            ClearRecentFreeEvidence();
            _hasLayer = false;
            if (!preservePending) { _latestScan = null; _pendingScans.Clear(); }
            _confirmedScan = null;
            _confirmedPose = null;
            if (!preservePresentation) _layerScanAt = _layerPoseAt = double.NegativeInfinity;
            _layerEpoch = -1;
            if (!preservePending) _layerScanStamp = -1;
            _layerBaseRevision = -1;
            _navigationStatus = reason;
            if (clearCalibration)
            { _calibration = null; _calibrationEpoch = -1; _scanFrame = null; _scanStamp = -1; _lastScanAt = double.NegativeInfinity; }
            if (clearHistory) _poseHistory.Clear();
            if (redraw)
            {
                Occupancy = UnknownCells(Geometry.width * Geometry.height);
                RenderCurrentCells();
                Revision++;
            }
        }

        private void RefreshNavigationFreshness()
        {
            if (_hasLayer && NavigationAgeSeconds >= FreshSeconds)
                ResetNavigation("LiDAR/pose pair expired; editing cached map while waiting for updates.", false, false, true, true);
        }

        private void ClearRecentFreeEvidence()
        {
            _recentFreeObservedAt = null;
            _recentFreeGeometry = null;
            _recentFreeIndices.Clear();
        }

        private void MergeRecentFreeEvidence(int[] cells, double observedAt)
        {
            if (_recentFreeObservedAt == null || _recentFreeObservedAt.Length != cells.Length ||
                _recentFreeGeometry == null || _recentFreeGeometry.width != Geometry.width ||
                _recentFreeGeometry.height != Geometry.height || _recentFreeGeometry.resolution != Geometry.resolution ||
                _recentFreeGeometry.origin.x != Geometry.origin.x || _recentFreeGeometry.origin.y != Geometry.origin.y ||
                _recentFreeGeometry.frame != Geometry.frame)
            {
                var rebased = new double[cells.Length];
                for (int i = 0; i < cells.Length; i++) rebased[i] = double.NegativeInfinity;
                RebaseRecentFreeEvidence(rebased);
                _recentFreeObservedAt = rebased;
                _recentFreeGeometry = new MapGridGeometry { width = Geometry.width, height = Geometry.height,
                    resolution = Geometry.resolution, origin = Geometry.origin, frame = Geometry.frame };
            }
            _recentFreeIndices.Clear();
            double now = Now, window = RecentFreeWindowSeconds;
            for (int i = 0; i < cells.Length; i++)
            {
                if (cells[i] >= 50)
                {
                    // A confirmed obstacle revokes any older free proof. If
                    // its next beam is missing, that cell becomes unknown,
                    // rather than silently reusing pre-obstacle free space.
                    _recentFreeObservedAt[i] = double.NegativeInfinity;
                }
                else if (cells[i] == 0)
                    _recentFreeObservedAt[i] = Math.Max(_recentFreeObservedAt[i], observedAt);
                else if (now - _recentFreeObservedAt[i] < window)
                { cells[i] = 0; _recentFreeIndices.Add(i); }
            }
        }

        private void RebaseRecentFreeEvidence(double[] destination)
        {
            var before = _recentFreeGeometry;
            if (before == null || _recentFreeObservedAt == null || before.frame != Geometry.frame ||
                before.resolution != Geometry.resolution || _recentFreeObservedAt.Length != before.width * before.height)
                return;
            double dx = ((double)Geometry.origin.x - before.origin.x) / Geometry.resolution;
            double dy = ((double)Geometry.origin.y - before.origin.y) / Geometry.resolution;
            double roundedX = Math.Round(dx), roundedY = Math.Round(dy);
            if (Math.Abs(dx - roundedX) > .001 || Math.Abs(dy - roundedY) > .001 ||
                roundedX <= -Geometry.width || roundedX >= before.width ||
                roundedY <= -Geometry.height || roundedY >= before.height) return;
            int offsetX = (int)roundedX, offsetY = (int)roundedY;
            int fromX = Math.Max(0, -offsetX), toX = Math.Min(Geometry.width, before.width - offsetX);
            int fromY = Math.Max(0, -offsetY), toY = Math.Min(Geometry.height, before.height - offsetY);
            // A larger coordinate crop is not a new map origin. Transfer only
            // original free-observation times for exactly aligned world cells;
            // no old obstacle or renewed timestamp enters the new layer.
            for (int y = fromY; y < toY && toX > fromX; y++)
                Array.Copy(_recentFreeObservedAt, (y + offsetY) * before.width + fromX + offsetX,
                    destination, y * Geometry.width + fromX, toX - fromX);
        }

        private void ExpireRecentFreeEvidence()
        {
            if (!_hasLayer || Occupancy == null || _recentFreeObservedAt == null) return;
            bool changed = false;
            double now = Now, window = RecentFreeWindowSeconds;
            for (int i = _recentFreeIndices.Count - 1; i >= 0; i--)
            {
                int index = _recentFreeIndices[i];
                if (now - _recentFreeObservedAt[index] < window) continue;
                Occupancy[index] = -1;
                _recentFreeIndices.RemoveAt(i);
                changed = true;
            }
            if (!changed) return;
            if (!_knownFreeLayer.TryMerge(BaseOccupancy, Occupancy, out string error))
            {
                ResetNavigation(error ?? "Known map free space became invalid.", false, false, true, true);
                return;
            }
            RenderCurrentCells();
            Revision++;
        }

        private bool RejectSensor(string reason)
        {
            _rejectedSensors++;
            if (!_hasLayer) _navigationStatus = reason;
            return false;
        }

        private static void UpdatePeriod(ref double period, double previous, double current)
        {
            if (!Finite(previous) || current <= previous) return;
            double interval = current - previous;
            period = period <= 0 ? interval : period * .8 + interval * .2;
        }

        public string DiagnosticSummary
        {
            get
            {
                string gate = NavigationLive ? BriefSensorGap ? "brief_gap" : "ready" :
                    !EditingAvailable ? "map_identity" : _transport == null || !_transport.Connected ? "disconnected" :
                    _calibration == null ? "calibration" : !Live ? "grid_stale" : !HasNavigationPose ? "pose_stale" :
                    !_hasLayer ? "no_exact_pair" : "scan_stale_or_epoch";
                long drops = 0;
                if (_transport != null) lock (_transport.Gate) drops = _transport.DroppedSensors;
                return $"gate={gate} poseHz={(_posePeriod > 0 ? 1 / _posePeriod : 0):F1} scanHz={(_scanPeriod > 0 ? 1 / _scanPeriod : 0):F1} " +
                    $"poseAge={PoseAgeSeconds:F2} scanAge={ScanAgeSeconds:F2} pairedAge={NavigationAgeSeconds:F2}/{FreshSeconds:F2} " +
                    $"poses={_receivedPoses} scans={_receivedScans} matched={_matchedScans} pending={_pendingScans.Count} expired={_expiredScans} " +
                    $"rejected={_rejectedSensors} queueDrops={drops} sourcePose={_poseStamp} sourceScan={_scanStamp} pairedStamp={_layerScanStamp} " +
                    $"currentCells={CurrentObstacleCellCount}/{CurrentFreeCellCount}/{CurrentUnknownCellCount} " +
                    $"obstacles={RawObstacleCellCount}/{CandidateObstacleCellCount}/{ConfirmedObstacleCellCount} " +
                    $"recentFree={RecentFreeCellCount}/{RecentFreeWindowSeconds:F2}s " +
                    $"mapFree={MapFreeCellCount} " +
                    $"epoch={_transport?.Epoch ?? -1}/{_gridEpoch}/{_poseEpoch}/{_calibrationEpoch}/{_layerEpoch}";
            }
        }

        private void LogDiagnosticPulse()
        {
            if (!Debug.isDebugBuild || Now < _nextDiagnosticAt) return;
            _nextDiagnosticAt = Now + 5;
            Debug.Log("PUPPYPI_MAP_SYNC " + DiagnosticSummary, this);
        }

        private static int[] UnknownCells(int count)
        {
            var cells = new int[count];
            for (int i = 0; i < cells.Length; i++) cells[i] = -1;
            return cells;
        }

        private void RenderCurrentCells()
        {
            if (Texture == null || Geometry == null || Texture.width != Geometry.width || Texture.height != Geometry.height) return;
            int count = Geometry.width * Geometry.height;
            if (_textureColors == null || _textureColors.Length != count) _textureColors = new Color32[count];
            if (_confirmedObstacleEvidence == null || _confirmedObstacleEvidence.Length != count)
                _confirmedObstacleEvidence = new int[count];
            CurrentObstacleCellCount = CurrentFreeCellCount = CurrentUnknownCellCount = 0;
            // Image and collision mask share confirmed current obstacles,
            // bounded recent free evidence and the selected map-free prior.
            // Accumulated occupied points never become current obstacles.
            // Unknown is light blue-grey, distinct from measured white free
            // space, without displaying old points as a second obstacle cloud.
            for (int i = 0; i < count; i++)
            {
                int value = Occupancy != null && i < Occupancy.Length ? Occupancy[i] : -1;
                _confirmedObstacleEvidence[i] = value >= 50 ? 100 : 0;
                if (value < 0)
                { _textureColors[i] = new Color32(226, 233, 238, 255); CurrentUnknownCellCount++; }
                else if (value >= 50)
                { _textureColors[i] = new Color32(22, 30, 36, 255); CurrentObstacleCellCount++; }
                else
                { _textureColors[i] = new Color32(244, 248, 246, 255); CurrentFreeCellCount++; }
            }
            Texture.SetPixels32(_textureColors);
            Texture.Apply(false, false);
        }

        public string DescribeNavigationCell(Vector2 position)
        {
            if (Geometry == null || !Geometry.IsValid || Occupancy == null || !Finite(position.x) || !Finite(position.y))
                return "invalid navigation data";
            int x = Mathf.FloorToInt((position.x - Geometry.origin.x) / Geometry.resolution);
            int y = Mathf.FloorToInt((position.y - Geometry.origin.y) / Geometry.resolution);
            if (x < 0 || y < 0 || x >= Geometry.width || y >= Geometry.height) return "outside map boundary";
            int index = y * Geometry.width + x;
            if (index >= Occupancy.Length) return "invalid navigation data";
            if (Occupancy[index] < 0) return _candidateObstacleCells.Contains(index) ?
                "unconfirmed sensor hit; verifying" : "not observed in current scan";
            if (Occupancy[index] >= 50) return "current LiDAR obstacle";
            if (_knownFreeLayer.IsReusedCell(index)) return "free space recorded in the map";
            if (_recentFreeIndices.Contains(index)) return "free space observed within 1 s";
            return "observed free space";
        }

        public bool IsObstacleCandidate(Vector2 position)
        {
            if (Geometry == null || !Geometry.IsValid || !Finite(position.x) || !Finite(position.y)) return false;
            int x = Mathf.FloorToInt((position.x - Geometry.origin.x) / Geometry.resolution);
            int y = Mathf.FloorToInt((position.y - Geometry.origin.y) / Geometry.resolution);
            return x >= 0 && y >= 0 && x < Geometry.width && y < Geometry.height &&
                _candidateObstacleCells.Contains(y * Geometry.width + x);
        }

        public bool IsMapKnownFree(Vector2 position)
        {
            if (Geometry == null || !Geometry.IsValid || !Finite(position.x) || !Finite(position.y)) return false;
            int x = Mathf.FloorToInt((position.x - Geometry.origin.x) / Geometry.resolution);
            int y = Mathf.FloorToInt((position.y - Geometry.origin.y) / Geometry.resolution);
            return x >= 0 && y >= 0 && x < Geometry.width && y < Geometry.height &&
                _knownFreeLayer.IsReusedCell(y * Geometry.width + x);
        }

        // Evidence for deciding whether to replan, not permission to enter
        // unknown cells. Actual motion still checks the full Occupancy layer.
        public bool TryFindConfirmedRouteObstacle(IReadOnlyList<Vector2> path, float yaw,
            MapRobotFootprint footprint, out MapPathBlock obstacle)
        {
            obstacle = null;
            if (!_hasLayer || Geometry == null || _confirmedObstacleEvidence == null) return false;
            return MapDetourPlanner.TryFindRouteBlock(Geometry, _confirmedObstacleEvidence, path, yaw,
                footprint, out obstacle) && obstacle.Kind == MapPathBlockKind.Occupied;
        }

        public bool TryGetRouteGrid(bool operatorSupervised, MapRobotFootprint footprint,
            out MapGridGeometry geometry, out int[] cells)
        {
            if (!operatorSupervised)
            {
                geometry = Geometry;
                cells = Occupancy;
                bool valid = geometry != null && geometry.IsValid && cells != null &&
                    cells.LongLength == (long)geometry.width * geometry.height;
                RouteGridStatus = valid ? "" : "The current navigation grid is unavailable.";
                return valid;
            }
            int[] evidence = _hasLayer ? _confirmedObstacleEvidence : null;
            bool same = _routeGridRevision == Revision && ReferenceEquals(_routeGridSource, Geometry) &&
                ReferenceEquals(_routeGridEvidence, evidence) && SameRouteGridGeometry(_routeGridSourceValues, Geometry) &&
                footprint != null && _routeGridLength == footprint.LengthMeters &&
                _routeGridWidth == footprint.WidthMeters && _routeGridMargin == footprint.MarginMeters;
            if (!same)
            {
                _routeGridRevision = Revision;
                _routeGridSource = Geometry;
                _routeGridSourceValues = Geometry == null ? null : new MapGridGeometry {
                    width = Geometry.width, height = Geometry.height, resolution = Geometry.resolution,
                    origin = Geometry.origin, frame = Geometry.frame
                };
                _routeGridEvidence = evidence;
                _routeGridLength = footprint != null ? footprint.LengthMeters : float.NaN;
                _routeGridWidth = footprint != null ? footprint.WidthMeters : float.NaN;
                _routeGridMargin = footprint != null ? footprint.MarginMeters : float.NaN;
                MapRoutePolicy.TryBuild(Geometry, evidence, footprint,
                    out _routeGridGeometry, out _routeGridCells, out _routeGridError);
            }
            geometry = _routeGridGeometry;
            cells = _routeGridCells;
            RouteGridStatus = _routeGridError ?? "";
            return geometry != null && cells != null;
        }

        private static bool SameRouteGridGeometry(MapGridGeometry first, MapGridGeometry second) =>
            first != null && second != null && first.width == second.width && first.height == second.height &&
            first.resolution == second.resolution && first.origin.x == second.origin.x &&
            first.origin.y == second.origin.y && first.frame == second.frame;

        private static bool SourceStamp(Dictionary<string, object> value, out long stamp)
        {
            stamp = 0;
            if (!Number(value, value.ContainsKey("sec") ? "sec" : "secs", out double sec) ||
                !Number(value, value.ContainsKey("nanosec") ? "nanosec" : "nsecs", out double ns) ||
                sec < 0 || sec > 9000000000 || sec != Math.Floor(sec) || ns < 0 || ns >= 1000000000 || ns != Math.Floor(ns)) return false;
            stamp = (long)sec * 1000000000L + (long)ns;
            return true;
        }

        private static bool Object(Dictionary<string, object> p, string key, out Dictionary<string, object> value)
        { value = null; if (!p.TryGetValue(key, out object o) || !(o is Dictionary<string, object> d)) return false; value = d; return true; }
        private static bool Number(Dictionary<string, object> p, string key, out double value)
        { value = 0; if (!p.TryGetValue(key, out object o) || !(o is double d)) return false; value = d; return true; }
        private static bool Text(Dictionary<string, object> p, string key, out string value)
        { value = null; if (!p.TryGetValue(key, out object o) || !(o is string s)) return false; value = s; return true; }

        private sealed class MapJson
        {
            private static readonly Regex Numeric = new Regex(@"\G-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?", RegexOptions.CultureInvariant);
            private readonly string _json;
            private int _at, _tokens;
            private MapJson(string json) { _json = json; }
            public static bool TryRead(string json, out object value)
            {
                value = null;
                if (string.IsNullOrWhiteSpace(json) || json.Length > 262144) return false;
                var reader = new MapJson(json);
                try { value = reader.Read(0); reader.Space(); return reader._at == json.Length; }
                catch (FormatException) { return false; }
            }
            private void Space() { while (_at < _json.Length && " \r\n\t".IndexOf(_json[_at]) >= 0) _at++; }
            private bool Take(char c) { Space(); if (_at >= _json.Length || _json[_at] != c) return false; _at++; return true; }
            private object Read(int depth)
            {
                Space();
                if (depth > 8 || ++_tokens > 12000 || _at >= _json.Length) throw new FormatException();
                char c = _json[_at];
                if (c == '"') return String();
                if (Take('{'))
                {
                    var obj = new Dictionary<string, object>();
                    if (Take('}')) return obj;
                    do
                    {
                        Space();
                        if (_at >= _json.Length || _json[_at] != '"') throw new FormatException();
                        string key = String();
                        if (obj.Count >= 64 || obj.ContainsKey(key) || !Take(':')) throw new FormatException();
                        obj.Add(key, Read(depth + 1));
                        if (Take('}')) return obj;
                    } while (Take(','));
                    throw new FormatException();
                }
                if (Take('['))
                {
                    var arr = new List<object>();
                    if (Take(']')) return arr;
                    do { if (arr.Count >= 4096) throw new FormatException(); arr.Add(Read(depth + 1)); if (Take(']')) return arr; } while (Take(','));
                    throw new FormatException();
                }
                foreach (string literal in new[] { "true", "false", "null" })
                    if (_at + literal.Length <= _json.Length && string.CompareOrdinal(_json, _at, literal, 0, literal.Length) == 0)
                    { _at += literal.Length; return literal == "null" ? null : (object)(literal == "true"); }
                var match = Numeric.Match(_json, _at);
                if (!match.Success || !double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ||
                    double.IsNaN(number) || double.IsInfinity(number)) throw new FormatException();
                _at += match.Length;
                return number;
            }
            private string String()
            {
                _at++;
                var text = new StringBuilder();
                while (_at < _json.Length)
                {
                    char c = _json[_at++];
                    if (c == '"') return text.ToString();
                    if (c < 32 || text.Length > 4096) throw new FormatException();
                    if (c != '\\') { text.Append(c); continue; }
                    if (_at >= _json.Length) throw new FormatException();
                    char escape = _json[_at++];
                    switch (escape)
                    {
                        case '"': case '\\': case '/': text.Append(escape); break;
                        case 'b': text.Append('\b'); break;
                        case 'f': text.Append('\f'); break;
                        case 'n': text.Append('\n'); break;
                        case 'r': text.Append('\r'); break;
                        case 't': text.Append('\t'); break;
                        case 'u':
                            if (_at + 4 > _json.Length || !ushort.TryParse(_json.Substring(_at, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort code)) throw new FormatException();
                            text.Append((char)code); _at += 4; break;
                        default: throw new FormatException();
                    }
                }
                throw new FormatException();
            }
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private void DestroyTexture()
        {
            _textureColors = null;
            CurrentObstacleCellCount = CurrentFreeCellCount = CurrentUnknownCellCount = 0;
            if (Texture == null) return;
            if (Application.isPlaying) Destroy(Texture);
            else DestroyImmediate(Texture);
            Texture = null;
        }
        private static bool ValidPose(Pose pose) => pose?.position != null && pose.orientation != null &&
            Finite(pose.position.x) && Finite(pose.position.y) && Math.Abs(pose.position.x) <= 10000 && Math.Abs(pose.position.y) <= 10000 &&
            Finite(pose.orientation.x) && Finite(pose.orientation.y) && Finite(pose.orientation.z) && Finite(pose.orientation.w);

        // Called under transport.Gate. Retain enough sensor callbacks to absorb
        // measured mapper delay (~0.31 s) and Unity frame batching without an
        // unbounded stream queue. Raw grid reception keeps its separate 6 MiB cap.
        private static bool QueueSensor(Transport transport, Queue<SensorMessage> target,
            string json, double at, int epoch, int bytes)
        {
            if (bytes > MaxSensorQueueBytes) { transport.DroppedSensors++; return false; }
            while (target.Count >= MaxPendingSensors)
            {
                transport.SensorBytes -= target.Dequeue().Bytes;
                transport.DroppedSensors++;
            }
            while (transport.SensorBytes + bytes > MaxSensorQueueBytes)
            {
                Queue<SensorMessage> oldest = transport.Poses.Count == 0 ? transport.Scans : transport.Scans.Count == 0 ? transport.Poses :
                    transport.Poses.Peek().At <= transport.Scans.Peek().At ? transport.Poses : transport.Scans;
                transport.SensorBytes -= oldest.Dequeue().Bytes;
                transport.DroppedSensors++;
                if (oldest.Count == 0)
                {
                    if (oldest == transport.Poses) transport.Pose = null;
                    else transport.Scan = null;
                }
            }
            target.Enqueue(new SensorMessage { Json = json, At = at, Epoch = epoch, Bytes = bytes });
            transport.SensorBytes += bytes;
            return true;
        }

        private static async Task Receive(Transport transport, Uri endpoint)
        {
            CancellationToken token = transport.Cancel.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using (var socket = new ClientWebSocket())
                        {
                            lock (transport.Gate) transport.Socket = socket;
                            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                            { timeout.CancelAfter(TimeSpan.FromSeconds(5)); await socket.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false); }
                            int epoch = Interlocked.Increment(ref transport.Epoch);
                            foreach (string topic in new[] { "/map", "/lidar_map/pose", "/scan" })
                            {
                                byte[] subscribe = Encoding.UTF8.GetBytes("{\"op\":\"subscribe\",\"topic\":\"" + topic +
                                    "\",\"queue_length\":1,\"throttle_rate\":" + (topic == "/map" ? "500" : topic == "/scan" ? "100" : "50") + "}");
                                await socket.SendAsync(new ArraySegment<byte>(subscribe), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
                            }
                            string calibrationId = "map-calibration-" + Guid.NewGuid().ToString("N");
                            lock (transport.Gate) transport.CalibrationId = calibrationId;
                            byte[] calibrationRequest = Encoding.UTF8.GetBytes(CalibrationRequest(calibrationId));
                            await socket.SendAsync(new ArraySegment<byte>(calibrationRequest), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
                            transport.Error = "";
                            transport.Connected = true;
                            var buffer = new byte[8192];
                            while (!token.IsCancellationRequested)
                            {
                                using (var stream = new MemoryStream())
                                {
                                    WebSocketReceiveResult part;
                                    do
                                    {
                                        part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                                        if (part.MessageType != WebSocketMessageType.Text) throw new IOException("Map bridge closed or sent binary data.");
                                        if (stream.Length + part.Count > MaxMessageBytes) throw new IOException("Map message is too large.");
                                        stream.Write(buffer, 0, part.Count);
                                    } while (!part.EndOfMessage);
                                    string json = Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
                                    Envelope envelope = JsonUtility.FromJson<Envelope>(json);
                                    if (envelope == null) continue;
                                    double received = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
                                    // Convert to Unity's clock on the main thread using the same monotonic origin offset.
                                    lock (transport.Gate)
                                    {
                                        if (envelope.op == "publish" && envelope.topic == "/map")
                                        { transport.Grid = json; transport.GridAt = received; transport.GridEpoch = epoch; }
                                        else if (envelope.op == "publish" && envelope.topic == "/lidar_map/pose")
                                        {
                                            if (QueueSensor(transport, transport.Poses, json, received, epoch, (int)stream.Length))
                                            { transport.Pose = json; transport.PoseAt = received; transport.PoseEpoch = epoch; }
                                        }
                                        else if (envelope.op == "publish" && envelope.topic == "/scan")
                                        {
                                            if (QueueSensor(transport, transport.Scans, json, received, epoch, (int)stream.Length))
                                            { transport.Scan = json; transport.ScanAt = received; transport.ScanEpoch = epoch; }
                                        }
                                        else if (envelope.op == "service_response" && envelope.service == CalibrationService && envelope.id == calibrationId)
                                        { transport.Calibration = json; transport.CalibrationEpoch = epoch; }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception error) when (error is WebSocketException || error is IOException || error is OperationCanceledException ||
                        error is InvalidOperationException || error is ObjectDisposedException || error is ArgumentException)
                    { if (!token.IsCancellationRequested) transport.Error = error.Message; }
                    finally { transport.Connected = false; lock (transport.Gate) transport.Socket = null; }
                    if (!token.IsCancellationRequested) await Task.Delay(2000, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            finally { transport.Cancel.Dispose(); }
        }

        private void OnEnable() { if (!_paused) StartTransport(); }
        private void OnDisable() => StopTransport();
        private void OnApplicationPause(bool paused)
        { _paused = paused; if (paused) StopTransport(); else if (isActiveAndEnabled) StartTransport(); }
        private void OnDestroy() => Disconnect();
    }
}
