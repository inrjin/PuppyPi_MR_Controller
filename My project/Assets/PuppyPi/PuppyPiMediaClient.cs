using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace PuppyPi
{
    [Serializable] public sealed class RobotMapPose
    {
        public float x_m, y_m, yaw_rad, u_px, v_px;
    }

    [Serializable] public sealed class RobotMapInfo
    {
        public string media, frame, session;
        public int width_px, height_px;
        public float left_m, bottom_m, px_per_m, resolution_m, age_s;
        public long image_seq, scans, integrations;
        public double stamp_unix;
        public RobotMapPose robot;
    }

    [Serializable] public sealed class MediaStreamDescription
    {
        public string stream, snapshot, info, format, note, pixel_from_map;
        public float nominal_fps;
    }

    [Serializable] public sealed class RobotMediaCatalog
    {
        public string media, status, viewer;
        public MediaStreamDescription camera, map;
    }

    [Serializable] public sealed class MediaArbiterStatus
    {
        public string engine, owner, vr_block_reason;
        public bool estop, vr_permitted;
        public float age_s;
    }

    [Serializable] public sealed class MediaMapStatus
    {
        public RobotMapPose robot;
        public float age_s;
    }

    [Serializable] public sealed class RobotMediaStatus
    {
        public string media;
        public MediaArbiterStatus arbiter;
        public MediaMapStatus map;
    }

    public static class PuppyPiMapProjection
    {
        public static bool TryProject(RobotMapInfo map, Vector2 positionMeters, out Vector2 pixel)
        {
            pixel = default;
            if (map == null || map.width_px <= 0 || map.height_px <= 0 ||
                !Finite(map.left_m) || !Finite(map.bottom_m) || !Finite(map.px_per_m) || map.px_per_m <= 0 ||
                !Finite(positionMeters.x) || !Finite(positionMeters.y)) return false;
            pixel = new Vector2((positionMeters.x - map.left_m) * map.px_per_m,
                map.height_px - (positionMeters.y - map.bottom_m) * map.px_per_m);
            return Finite(pixel.x) && Finite(pixel.y);
        }

        public static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    // PuppyPi Media v1 is read-only. Failure here never changes control ownership or stop state.
    public sealed class PuppyPiMediaClient : MonoBehaviour
    {
        private const string Protocol = "puppypi-media/1";
        private const int MaximumJsonBytes = 64 * 1024;
        public RobotMediaCatalog Catalog { get; private set; }
        public RobotMapInfo MapInfo { get; private set; }
        public RobotMediaStatus RobotStatus { get; private set; }
        public string LastError { get; private set; } = "";
        public bool MapInfoLive => MapInfo != null && MapAgeSeconds < 2;
        public bool StatusLive => RobotStatus?.arbiter != null && StatusAgeSeconds < 2;
        public double MapAgeSeconds => MapInfo == null ? double.PositiveInfinity : MapInfo.age_s + Math.Max(0, Now - _mapReceivedAt);
        public double StatusAgeSeconds => RobotStatus?.arbiter == null ? double.PositiveInfinity :
            RobotStatus.arbiter.age_s + Math.Max(0, Now - _statusReceivedAt);
        private readonly List<UnityWebRequest> _requests = new List<UnityWebRequest>();
        private string _host;
        private int _generation;
        private double _mapReceivedAt, _statusReceivedAt;
        private static double Now => Time.realtimeSinceStartupAsDouble;

        public void Connect(string host)
        {
            if (_host == host && _host != null) return;
            Disconnect();
            if (string.IsNullOrWhiteSpace(host) || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            { LastError = "Invalid media host"; return; }
            _host = host;
            int generation = _generation;
            StartCoroutine(Poll("/api/media", generation, 30));
            StartCoroutine(Poll("/api/map", generation, 1));
            StartCoroutine(Poll("/api/status", generation, 1));
        }

        public void Disconnect()
        {
            _generation++;
            _host = null;
            StopAllCoroutines();
            foreach (var request in _requests) { request.Abort(); request.Dispose(); }
            _requests.Clear();
            Catalog = null;
            MapInfo = null;
            RobotStatus = null;
            LastError = "";
        }

        private IEnumerator Poll(string path, int generation, float interval)
        {
            while (_host != null && generation == _generation)
            {
                var handler = new BoundedJsonHandler();
                var request = new UnityWebRequest(new UriBuilder("http", _host, 8090, path).Uri.AbsoluteUri,
                    UnityWebRequest.kHttpVerbGET) { downloadHandler = handler, timeout = 3, redirectLimit = 0 };
                _requests.Add(request);
                double requestedAt = Now;
                UnityWebRequestAsyncOperation operation = null;
                try { operation = request.SendWebRequest(); }
                catch (Exception error) when (error is UnityException || error is InvalidOperationException)
                { LastError = "Media unavailable: " + error.Message; }
                if (operation != null) yield return operation;
                if (generation != _generation) yield break;
                bool received = false;
                if (operation != null && request.result == UnityWebRequest.Result.Success && !handler.TooLarge)
                {
                    try { received = Apply(path, handler.Text, requestedAt); }
                    catch (Exception error) when (error is ArgumentException || error is DecoderFallbackException)
                    { LastError = "Invalid media JSON"; }
                }
                else LastError = handler.TooLarge ? "Media JSON exceeded 64 KiB" : "Media unavailable: " + request.error;
                _requests.Remove(request);
                request.Dispose();
                yield return new WaitForSecondsRealtime(received ? interval : 2);
            }
        }

        private bool Apply(string path, string json, double requestedAt)
        {
            if (path == "/api/media")
            {
                var value = JsonUtility.FromJson<RobotMediaCatalog>(json);
                if (value?.media != Protocol || value.camera == null || value.map == null)
                { LastError = "Unsupported media catalog"; return false; }
                Catalog = value;
            }
            else if (path == "/api/map")
            {
                var value = JsonUtility.FromJson<RobotMapInfo>(json);
                if (value?.media != Protocol || value.width_px <= 0 || value.width_px > 4096 ||
                    value.height_px <= 0 || value.height_px > 4096 || value.age_s < 0 ||
                    !PuppyPiMapProjection.Finite(value.age_s) ||
                    !PuppyPiMapProjection.TryProject(value, Vector2.zero, out _))
                { LastError = "Invalid map metadata"; return false; }
                MapInfo = value;
                _mapReceivedAt = requestedAt;
            }
            else
            {
                var value = JsonUtility.FromJson<RobotMediaStatus>(json);
                if (value?.media != Protocol || value.arbiter == null || value.arbiter.age_s < 0 ||
                    !PuppyPiMapProjection.Finite(value.arbiter.age_s))
                { LastError = "Invalid media status"; return false; }
                RobotStatus = value;
                _statusReceivedAt = requestedAt;
            }
            LastError = "";
            return true;
        }

        public string Summary
        {
            get
            {
                if (MapInfo == null) return "Waiting for map coordinates\n" + LastError;
                var map = MapInfo;
                string robot = map.robot == null ? "Robot pose unavailable" :
                    $"Robot: x {map.robot.x_m:F2} m\ny {map.robot.y_m:F2} m\nheading {map.robot.yaw_rad * Mathf.Rad2Deg:F0} deg";
                return $"{(MapInfoLive ? "LIVE MAP INFO" : "STALE MAP INFO")}\n{map.width_px} x {map.height_px} px\n" +
                    $"{map.px_per_m:F1} pixels / m\n{robot}\n+x: startup forward\n+y: startup left\n" +
                    $"Age: {MapAgeSeconds:F1}s\n{LastError}";
            }
        }

        private void OnDisable() => Disconnect();

        private sealed class BoundedJsonHandler : DownloadHandlerScript
        {
            private readonly byte[] _bytes = new byte[MaximumJsonBytes];
            private int _count;
            public bool TooLarge { get; private set; }
            public string Text => new UTF8Encoding(false, true).GetString(_bytes, 0, _count);
            public BoundedJsonHandler() : base(new byte[4096]) { }
            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                if (data == null || dataLength <= 0) return true;
                if (dataLength > MaximumJsonBytes - _count) { TooLarge = true; return false; }
                Buffer.BlockCopy(data, 0, _bytes, _count, dataLength);
                _count += dataLength;
                return true;
            }
        }
    }
}
