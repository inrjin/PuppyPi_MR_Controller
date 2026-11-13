#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace PuppyPi.Tests
{
    public class MapDataClientLoopbackTests
    {
        private GameObject _root;
        private LocalMapServer _server;
        [Serializable] private sealed class Args { public string[] names; }
        [Serializable] private sealed class Request { public string op, topic, service, id; public int queue_length, throttle_rate; public Args args; }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            if (_root != null) Object.Destroy(_root);
            _server?.Dispose();
            yield return null;
        }

        private PuppyPiMapDataClient Connect()
        {
            _server = new LocalMapServer();
            _root = new GameObject("map data loopback fixture");
            var client = _root.AddComponent<PuppyPiMapDataClient>();
            client.enabled = false;
            client.Connect("127.0.0.1", _server.Port);
            client.SetSession("loopback-map-session");
            client.enabled = true;
            return client;
        }

        private static IEnumerator Until(Func<bool> predicate, string message, float timeout = 8f)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + timeout;
            while (!predicate() && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.That(predicate(), Is.True, message);
        }

        private static IEnumerator Sent(Task task)
        {
            yield return Until(() => task.IsCompleted, "loopback map send timed out");
            task.GetAwaiter().GetResult();
        }

        [UnityTest]
        public IEnumerator LargeFragmentedGridKeepsCropCoordinatesTextureAndReadOnlySubscriptions()
        {
            PuppyPiMapDataClient client = Connect();
            yield return Until(() => _server.Messages.Length >= 4, "map subscriptions/calibration query did not arrive: " + _server.Error);
            string grid = GridJson(320, 256, true);
            Assert.That(Encoding.UTF8.GetByteCount(grid), Is.GreaterThan(65536));
            yield return Sent(_server.Send(grid, fragmentBytes: 4093));
            yield return Until(() => client.Live, "large fragmented map did not become live: " + _server.Error);

            Assert.That(client.SessionId, Is.EqualTo("loopback-map-session"));
            Assert.That(client.Geometry.width, Is.LessThan(320), "the known area should produce a cropped map");
            Assert.That(client.Geometry.height, Is.LessThan(256));
            Assert.That(client.Texture.width, Is.EqualTo(client.Geometry.width));
            Assert.That(client.Texture.height, Is.EqualTo(client.Geometry.height));
            Assert.That(client.Occupancy.Length, Is.EqualTo(client.Texture.width * client.Texture.height));
            int left = Mathf.RoundToInt((client.Geometry.origin.x + 8) / .05f);
            int bottom = Mathf.RoundToInt((client.Geometry.origin.y + 6.4f) / .05f);
            int occupiedX = 182 - left, occupiedY = 132 - bottom;
            Assert.That(client.BaseOccupancy[occupiedY * client.Geometry.width + occupiedX], Is.EqualTo(100));
            Assert.That(client.BaseOccupancy[(occupiedY - 1) * client.Geometry.width + occupiedX], Is.Zero,
                "diagnostic source cells must not be vertically flipped or shifted");
            Assert.That(client.Occupancy, Is.All.EqualTo(-1), "a raw grid alone provides no current scan obstacle evidence");
            Assert.That(client.Texture.GetPixel(occupiedX, occupiedY).r, Is.GreaterThan(.8f),
                "stored occupied cells must not appear in the clean current-scan display");
            Vector2 cellCenterUv = new Vector2((occupiedX + .5f) / client.Geometry.width,
                (occupiedY + .5f) / client.Geometry.height);
            Assert.That(client.Geometry.TryUvToMeters(cellCenterUv, out var meters), Is.True);
            Assert.That(meters.x, Is.EqualTo(-8 + 182.5f * .05f).Within(.00001));
            Assert.That(meters.y, Is.EqualTo(-6.4f + 132.5f * .05f).Within(.00001));
            AssertReadOnlyRequests();
        }

        [UnityTest]
        public IEnumerator RepeatedPoseStampCannotRefreshPoseAndReconnectRequiresNewGridAndPose()
        {
            PuppyPiMapDataClient client = Connect();
            yield return Until(() => _server.Messages.Length >= 4, "map subscriptions/calibration query did not arrive");
            yield return Sent(_server.Send(GridJson(8, 8, false)));
            yield return Until(() => client.Live, "initial map did not become live");
            yield return Sent(_server.Send(PoseJson(10)));
            yield return Until(() => client.HasPose, "initial pose did not become fresh");
            yield return new WaitForSecondsRealtime(.35f);
            yield return Sent(_server.Send(PoseJson(10)));
            yield return new WaitForSecondsRealtime(.35f);
            Assert.That(client.HasPose, Is.False, "repeated source stamp must not extend pose freshness");
            yield return Sent(_server.Send(PoseJson(11)));
            yield return Until(() => client.HasPose, "increasing pose stamp did not restore freshness");

            int oldConnections = _server.ConnectionCount;
            _server.DropConnection();
            yield return Until(() => _server.ConnectionCount > oldConnections && _server.Messages.Length >= 8,
                "map connection did not resubscribe after disconnect: " + _server.Error);
            Assert.That(client.Live, Is.False, "previous connection's recent grid is not a current map");
            Assert.That(client.HasPose, Is.False, "previous connection's recent pose is not a current pose");
            yield return Sent(_server.Send(GridJson(8, 8, false)));
            yield return Until(() => client.Live, "new connection map did not arrive");
            Assert.That(client.HasPose, Is.False, "a new grid must not revive the previous connection's pose");
            yield return Sent(_server.Send(PoseJson(12)));
            yield return Until(() => client.Live && client.HasPose, "new connection pose did not arrive");
            AssertReadOnlyRequests();
        }

        [UnityTest]
        public IEnumerator SessionChangeDiscardsQueuedMessagesAndFrameChangeRequiresMatchingNewPose()
        {
            PuppyPiMapDataClient client = Connect();
            yield return Until(() => _server.Messages.Length >= 4, "initial map subscriptions/calibration query did not arrive");
            yield return Sent(_server.Send(GridJson(8, 8, false)));
            yield return Until(() => client.Live, "initial grid did not arrive");
            yield return Sent(_server.Send(PoseJson(10)));
            yield return Until(() => client.HasPose, "initial pose did not arrive");

            object previousTransport = CurrentTransport(client);
            int connections = _server.ConnectionCount;
            int subscriptions = _server.Messages.Length;
            QueueWithoutUnityUpdate(previousTransport, GridJson(8, 8, false), PoseJson(11));
            client.SetSession("next-session");
            Assert.That(CurrentTransport(client), Is.Not.SameAs(previousTransport), "session change must replace the old receive queue");
            Assert.That(client.Geometry, Is.Null);
            Assert.That(client.Occupancy, Is.Null);
            Assert.That(client.Live, Is.False);
            Assert.That(client.HasPose, Is.False);
            yield return Until(() => _server.ConnectionCount > connections && _server.Messages.Length >= subscriptions + 4,
                "new session did not create fresh subscriptions");
            Assert.That(client.SessionId, Is.EqualTo("next-session"));
            Assert.That(client.Live, Is.False, "queued old-session grid must not revive after reconnect");
            Assert.That(client.HasPose, Is.False, "queued old-session pose must not revive after reconnect");
            yield return Sent(_server.Send(GridJson(8, 8, false)));
            yield return Until(() => client.Live, "new-session grid did not arrive");
            Assert.That(client.HasPose, Is.False, "the new session still needs its own pose");
            yield return Sent(_server.Send(PoseJson(12)));
            yield return Until(() => client.HasPose, "new-session pose did not arrive");

            yield return Sent(_server.Send(GridJson(8, 8, false).Replace("\"frame_id\":\"map\"", "\"frame_id\":\"other\"")));
            yield return Until(() => client.Live && client.Geometry.frame == "other", "replacement coordinate frame did not arrive");
            Assert.That(client.HasPose, Is.False, "the previous frame's recent pose must be invalidated immediately");
            object transport = CurrentTransport(client);
            QueueWithoutUnityUpdate(transport, null, PoseJson(13));
            yield return Until(() => !HasPending(transport, "Pose"), "old-frame pose was not consumed");
            Assert.That(client.HasPose, Is.False, "old-frame pose received on the current connection must still be rejected");
            yield return Sent(_server.Send(PoseJson(14).Replace("\"frame_id\":\"map\"", "\"frame_id\":\"other\"")));
            yield return Until(() => client.HasPose, "a matching pose in the replacement frame did not become fresh");
            Assert.That(client.Live, Is.True);
            AssertReadOnlyRequests();
        }

        private static object CurrentTransport(PuppyPiMapDataClient client) =>
            typeof(PuppyPiMapDataClient).GetField("_transport", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(client);

        private static bool HasPending(object transport, string field)
        {
            object gate = transport.GetType().GetField("Gate").GetValue(transport);
            lock (gate) return transport.GetType().GetField(field).GetValue(transport) != null;
        }

        private void QueueWithoutUnityUpdate(object transport, string grid, string pose, string scan = null)
        {
            // Keep this one coroutine step on the main thread until real socket
            // messages reach the worker queue. This makes the session boundary
            // deterministic without adding a production-only test hook.
            Task send = Task.Run(async () =>
            {
                if (grid != null) await _server.Send(grid);
                if (pose != null) await _server.Send(pose);
                if (scan != null) await _server.Send(scan);
            });
            Assert.That(send.Wait(TimeSpan.FromSeconds(2)), Is.True, "loopback queue send timed out");
            send.GetAwaiter().GetResult();
            Assert.That(SpinWait.SpinUntil(() => (grid == null || HasPending(transport, "Grid")) &&
                (pose == null || HasPending(transport, "Pose")) && (scan == null || HasPending(transport, "Scan")), TimeSpan.FromSeconds(2)), Is.True,
                "worker did not queue the actual WebSocket messages before the session boundary");
        }

        private void AssertReadOnlyRequests()
        {
            var topics = new HashSet<string>();
            var calibrationIds = new HashSet<string>();
            foreach (string json in _server.Messages)
            {
                Request request = JsonUtility.FromJson<Request>(json);
                if (request.op == "subscribe")
                {
                    Assert.That(request.topic, Is.EqualTo("/map").Or.EqualTo("/lidar_map/pose").Or.EqualTo("/scan"));
                    Assert.That(request.queue_length, Is.EqualTo(1));
                    Assert.That(request.throttle_rate, Is.GreaterThan(0));
                    topics.Add(request.topic);
                }
                else
                {
                    Assert.That(request.op, Is.EqualTo("call_service"), "the map channel must never publish or advertise");
                    Assert.That(request.service, Is.EqualTo("/lidar_mapper/get_parameters"), "only the read-only calibration query is permitted");
                    Assert.That(request.args?.names, Is.EqualTo(new[] { "laser_x", "laser_y", "laser_yaw", "map_frame", "base_frame", "min_range", "max_range" }));
                    Assert.That(calibrationIds.Add(request.id), Is.True, "each connection must use one fresh request ID");
                }
            }
            Assert.That(topics, Is.EquivalentTo(new[] { "/map", "/lidar_map/pose", "/scan" }));
            Assert.That(calibrationIds.Count, Is.EqualTo(_server.ConnectionCount), "one calibration query is sent per connection");
            foreach (string target in _server.Targets) Assert.That(target, Is.EqualTo("/"));
        }

        private string CurrentCalibrationId()
        {
            string id = null;
            foreach (string json in _server.Messages)
            {
                var request = JsonUtility.FromJson<Request>(json);
                if (request.op == "call_service") id = request.id;
            }
            return id;
        }

        private static string CalibrationJson(string id) =>
            "{\"op\":\"service_response\",\"service\":\"/lidar_mapper/get_parameters\",\"id\":\"" + id +
            "\",\"result\":true,\"values\":{\"values\":[{\"type\":3,\"double_value\":0},{\"type\":3,\"double_value\":0}," +
            "{\"type\":3,\"double_value\":0},{\"type\":4,\"string_value\":\"map\"},{\"type\":4,\"string_value\":\"base_footprint\"}," +
            "{\"type\":3,\"double_value\":0.12},{\"type\":3,\"double_value\":11.5}]}}";

        private static string RawScanJson(int sec) =>
            "{\"op\":\"publish\",\"topic\":\"/scan\",\"msg\":{\"header\":{\"frame_id\":\"laser\",\"stamp\":{\"sec\":" + sec +
            ",\"nanosec\":0}},\"angle_min\":0,\"angle_max\":0,\"angle_increment\":0.1,\"range_min\":0.02,\"range_max\":12,\"ranges\":[2.3]}}";

        private static string ScanJson(int sec, int validRays = 30)
        {
            // Adjacent real measured rays supply the mapper's minimum pose
            // evidence while preserving this fixture's forward probe.
            var ranges = new string[30];
            for (int i = 0; i < ranges.Length; i++) ranges[i] = i < validRays ? "2.3" : "null";
            return RawScanJson(sec).Replace("\"ranges\":[2.3]", "\"ranges\":[" + string.Join(",", ranges) + "]")
                .Replace("\"angle_max\":0,", "\"angle_max\":0.0029,")
                .Replace("\"angle_increment\":0.1,", "\"angle_increment\":0.0001,");
        }

        private static string KnownFreeGridJson(int value)
        {
            var json = new StringBuilder("{\"op\":\"publish\",\"topic\":\"/map\",\"msg\":{\"header\":{\"frame_id\":\"map\"}," +
                "\"info\":{\"width\":60,\"height\":60,\"resolution\":0.05,\"origin\":{\"position\":{\"x\":-1.5,\"y\":-1.5},\"orientation\":{\"w\":1}}},\"data\":[");
            for (int i = 0; i < 3600; i++)
            { if (i != 0) json.Append(','); json.Append(value); }
            return json.Append("]}}").ToString();
        }

        private static string ForwardBlindScanJson(int seconds)
        {
            double step = 2 * Math.PI / 450;
            var ranges = new StringBuilder();
            for (int i = 0; i < 450; i++)
            {
                if (i != 0) ranges.Append(',');
                ranges.Append(i >= 434 && i <= 447 ? "null" : "3");
            }
            return RawScanJson(seconds).Replace("\"ranges\":[2.3]", "\"ranges\":[" + ranges + "]")
                .Replace("\"angle_max\":0,", "\"angle_max\":" + (step * 449).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",")
                .Replace("\"angle_increment\":0.1,", "\"angle_increment\":" + step.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",");
        }

        [UnityTest]
        public IEnumerator LiveMapKnownFreePriorClearsPersistentForwardBlindWedgeWithoutBypassingSensorFreshness()
        {
            var client = Connect();
            yield return Until(() => _server.Messages.Length >= 4, "read-only map requests did not arrive");
            yield return Sent(_server.Send(CalibrationJson(CurrentCalibrationId())));
            yield return Sent(_server.Send(KnownFreeGridJson(-1)));
            yield return Until(() => client.Live, "unknown coordinate map did not arrive");
            yield return Sent(_server.Send(PoseJson(10)));
            yield return Sent(_server.Send(ForwardBlindScanJson(10)));
            yield return Until(() => client.NavigationLive, "the current exact scan/pose pair did not become live");
            var footprint = new MapRobotFootprint();
            Assert.That(MapDetourPlanner.TryFindRouteBlock(client.Geometry, client.Occupancy,
                new[] { Vector2.zero, new Vector2(.3f, 0) }, 0, footprint, out var unknown), Is.True);
            Assert.That(unknown.Kind, Is.EqualTo(MapPathBlockKind.Unknown));
            Assert.That(client.MapFreeCellCount, Is.Zero);

            yield return Sent(_server.Send(KnownFreeGridJson(0)));
            yield return Until(() => client.NavigationLive && client.MapFreeCellCount > 0,
                "explicit known map free space did not bridge the persistent missing-return sector");
            Assert.That(client.IsMapKnownFree(new Vector2(.725f, -.075f)), Is.True);
            foreach (float distance in new[] { .3f, .6f })
                Assert.That(MapDetourPlanner.TryFindRouteBlock(client.Geometry, client.Occupancy,
                    new[] { Vector2.zero, new Vector2(distance, 0) }, 0, footprint, out var block), Is.False,
                    "a known-free forward route must pass body and frontal-guard checks: " + block?.Kind);

            yield return new WaitForSecondsRealtime(1.35f);
            Assert.That(client.NavigationLive, Is.False, "map free space cannot replace fresh sensor/pose observations");
            Assert.That(client.MapFreeCellCount, Is.Zero);
            Assert.That(client.Occupancy, Is.All.EqualTo(-1), "expired navigation must not resurrect a cached free layer");
            Assert.That(client.EditingAvailable, Is.True);
            AssertReadOnlyRequests();
        }

        [UnityTest]
        public IEnumerator FirstClearScanEnablesNavigationWithoutObstacleConfirmationWarmup()
        {
            var client = Connect();
            yield return Until(() => _server.Messages.Length >= 4, "map channel requests did not arrive");
            yield return Sent(_server.Send(CalibrationJson(CurrentCalibrationId())));
            yield return Sent(_server.Send(GridJson(8, 8, false)));
            yield return Until(() => client.Live, "raw grid did not arrive");
            var ranges = new StringBuilder();
            for (int i = 0; i < 360; i++)
            { if (i != 0) ranges.Append(','); ranges.Append('6'); }
            double step = 2 * Math.PI / 360;
            string scan = RawScanJson(10).Replace("\"ranges\":[2.3]", "\"ranges\":[" + ranges + "]")
                .Replace("\"angle_max\":0,", "\"angle_max\":" + (step * 359).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",")
                .Replace("\"angle_increment\":0.1,", "\"angle_increment\":" + step.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",");
            yield return Sent(_server.Send(PoseJson(10)));
            yield return Sent(_server.Send(scan));
            yield return Until(() => client.NavigationLive, "a first measured clear scan must immediately provide usable navigation");
            Assert.That(client.FilterStatus, Does.StartWith("FILTER READY"));
            Assert.That(client.RawObstacleCellCount, Is.Zero);
            Assert.That(client.CandidateObstacleCellCount, Is.Zero);
            Assert.That(client.ConfirmedObstacleCellCount, Is.Zero);
            Assert.That(MapDetourPlanner.TryFindRouteBlock(client.Geometry, client.Occupancy,
                new[] { Vector2.zero, new Vector2(.3f, 0) }, 0, new MapRobotFootprint(), out _), Is.False);
            AssertReadOnlyRequests();
        }

        [UnityTest]
        public IEnumerator SparseLocalizationPairsRevokeLiveKnownMapImmediatelyAndFreshSupportedPairsRecover()
        {
            var client = Connect();
            yield return Until(() => _server.Messages.Length >= 4, "read-only map requests did not arrive");
            yield return Sent(_server.Send(CalibrationJson(CurrentCalibrationId())));
            yield return Sent(_server.Send(KnownFreeGridJson(0)));
            yield return Until(() => client.Live, "known-free grid did not arrive");
            int stamp = 10;
            yield return Sent(_server.Send(PoseJson(stamp)));
            yield return Sent(_server.Send(ScanJson(stamp)));
            yield return Until(() => client.NavigationLive, "30 measured rays did not establish initial live navigation");
            var texture = client.Texture;
            foreach (int count in new[] { 1, 10, 29 })
            {
                Assert.That(client.NavigationLive, Is.True);
                Assert.That(client.NavigationAgeSeconds, Is.LessThan(.6));
                stamp++;
                double sentAt = Time.realtimeSinceStartupAsDouble;
                yield return Sent(_server.Send(PoseJson(stamp)));
                yield return Sent(_server.Send(ScanJson(stamp, count)));
                yield return Until(() => !client.NavigationLive,
                    count + " fresh rays must revoke the old layer before its normal expiry", .5f);
                Assert.That(Time.realtimeSinceStartupAsDouble - sentAt, Is.LessThan(.6));
                Assert.That(client.Occupancy, Is.All.EqualTo(-1));
                Assert.That(client.MapFreeCellCount, Is.Zero);
                Assert.That(client.CurrentFreeCellCount, Is.Zero);
                Assert.That(client.EditingAvailable, Is.True);
                Assert.That(client.Texture, Is.SameAs(texture));

                stamp++;
                yield return Sent(_server.Send(PoseJson(stamp)));
                yield return Sent(_server.Send(ScanJson(stamp)));
                long recoveredStamp = stamp * 1000000000L;
                yield return Until(() => client.NavigationLive && client.NavigationObservationStamp == recoveredStamp,
                    "a new supported exact pair must recover without resetting the editing session");
            }
            AssertReadOnlyRequests();
        }

        [UnityTest]
        public IEnumerator NavigationNeedsConnectionBoundCalibrationExactPoseAndFreshScanThenClearsOnLoss()
        {
            var client = Connect();
            yield return Until(() => _server.Messages.Length >= 4, "three subscriptions and calibration query did not arrive");
            string firstId = CurrentCalibrationId();
            yield return Sent(_server.Send(GridJson(8, 8, false)));
            yield return Until(() => client.Live, "raw grid did not become live");
            yield return Sent(_server.Send(ScanJson(10)));
            yield return Sent(_server.Send(PoseJson(10)));
            yield return Until(() => client.HasPose, "exact source-stamp pose did not arrive");
            Assert.That(client.NavigationLive, Is.False, "a missing calibration must not be assumed to be zero");
            yield return Sent(_server.Send(CalibrationJson("wrong-request-id")));
            yield return null;
            Assert.That(client.NavigationLive, Is.False);
            yield return Sent(_server.Send(CalibrationJson(firstId)));
            yield return Until(() => client.NavigationLive, "verified calibration plus exact pose/scan did not produce a live layer");
            Assert.That(client.BaseOccupancy, Is.Not.SameAs(client.Occupancy));
            Texture2D cachedMap = client.Texture;
            Assert.That(client.RawObstacleCellCount, Is.EqualTo(1));
            Assert.That(client.CandidateObstacleCellCount, Is.EqualTo(1));
            Assert.That(client.ConfirmedObstacleCellCount, Is.Zero);
            Assert.That(cachedMap.GetPixel(6, 4).r, Is.GreaterThan(.8), "the first unconfirmed return must not be drawn as a wall");
            for (int stamp = 11; stamp <= 13; stamp++)
            {
                yield return new WaitForSecondsRealtime(.11f);
                yield return Sent(_server.Send(PoseJson(stamp)));
                yield return Sent(_server.Send(ScanJson(stamp)));
                long expected = stamp * 1000000000L;
                yield return Until(() => SourceStamp(client, "_layerScanStamp") == expected,
                    "a distinct obstacle confirmation scan was not paired");
            }
            Assert.That(client.ConfirmedObstacleCellCount, Is.EqualTo(1), "a persistent thin obstacle must become blocking after distinct measured scans");
            Assert.That(client.CandidateObstacleCellCount, Is.Zero);
            Assert.That(cachedMap.GetPixel(6, 4).r, Is.LessThan(.15));
            yield return new WaitForSecondsRealtime(.7f);
            Assert.That(client.NavigationLive, Is.True, "a bounded brief sensor gap should not flicker the execution gate");
            Assert.That(client.BriefSensorGap, Is.True);
            Assert.That(client.HasPose, Is.False, "the legacy 0.6-second raw pose property retains its meaning");
            Assert.That(client.PoseAvailable, Is.True, "a cached marker remains available for editing");
            yield return new WaitForSecondsRealtime(.65f);
            Assert.That(client.NavigationLive, Is.False, "old scan data cannot keep navigation alive");
            Assert.That(client.Occupancy, Is.All.EqualTo(-1), "expired current observations never restore stored map debris");
            Assert.That(client.EditingAvailable, Is.True);
            Assert.That(client.Texture, Is.SameAs(cachedMap), "sensor expiry retains the coordinate map for editing");
            Assert.That(client.Texture.GetPixel(6, 4).r, Is.GreaterThan(.8), "expired endpoints disappear with the expired navigation evidence");
            Assert.That(client.NavigationStatus, Does.Contain("NO SIGNAL"));

            int connections = _server.ConnectionCount;
            _server.DropConnection();
            yield return Until(() => _server.ConnectionCount > connections && _server.Messages.Length >= (connections + 1) * 4,
                "new connection did not obtain its own calibration request");
            string nextId = CurrentCalibrationId();
            Assert.That(nextId, Is.Not.EqualTo(firstId));
            yield return Sent(_server.Send(GridJson(8, 8, false)));
            yield return Sent(_server.Send(PoseJson(11)));
            yield return Sent(_server.Send(ScanJson(11)));
            yield return Until(() => client.HasPose && client.Live, "new raw map and pose did not arrive");
            yield return Sent(_server.Send(CalibrationJson(firstId)));
            yield return null;
            Assert.That(client.NavigationLive, Is.False, "old-connection calibration responses must not restore navigation");
            yield return Sent(_server.Send(CalibrationJson(nextId)));
            yield return Until(() => client.NavigationLive, "new calibration did not restore current navigation");
            AssertReadOnlyRequests();
        }

        private static long SourceStamp(PuppyPiMapDataClient client, string field) =>
            (long)typeof(PuppyPiMapDataClient).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(client);

        [UnityTest]
        public IEnumerator OneScanLateMapperPoseAndNextScanInSameSocketBatchDoNotStarveNavigation()
        {
            var client = Connect();
            yield return Until(() => _server.Messages.Length >= 4, "map channel requests did not arrive");
            yield return Sent(_server.Send(CalibrationJson(CurrentCalibrationId())));
            yield return Sent(_server.Send(GridJson(8, 8, false)));
            yield return Until(() => client.Live, "raw grid did not arrive");
            yield return Sent(_server.Send(ScanJson(10)));
            yield return Until(() => SourceStamp(client, "_scanStamp") == 10000000000L, "the first pending scan was not consumed");
            Assert.That(client.NavigationLive, Is.False, "there is no matching pose yet");
            for (int stamp = 10; stamp < 14; stamp++)
            {
                // Block this coroutine step until both actual WS frames occupy
                // the latest-value worker slots, then let one Unity Update drain them.
                QueueWithoutUnityUpdate(CurrentTransport(client), null, PoseJson(stamp), ScanJson(stamp + 1));
                long expected = stamp * 1000000000L, next = (stamp + 1) * 1000000000L;
                yield return Until(() => SourceStamp(client, "_poseStamp") == expected && SourceStamp(client, "_scanStamp") == next,
                    "the pose N + scan N+1 batch was not consumed");
                Assert.That(client.NavigationLive, Is.True, "one-frame mapper latency must not starve exact timestamp matching");
                Assert.That(SourceStamp(client, "_layerScanStamp"), Is.EqualTo(expected), "the verified scan must advance each batch");
            }
            AssertReadOnlyRequests();
        }

        private static int PendingSensorCount(object transport, string field)
        {
            object gate = transport.GetType().GetField("Gate").GetValue(transport);
            lock (gate)
            {
                object queue = transport.GetType().GetField(field).GetValue(transport);
                return (int)queue.GetType().GetProperty("Count").GetValue(queue);
            }
        }

        private void QueueSensorBatchWithoutUnityUpdate(object transport, string[] messages, int poses, int scans)
        {
            Task send = Task.Run(async () => { foreach (string message in messages) await _server.Send(message); });
            Assert.That(send.Wait(TimeSpan.FromSeconds(2)), Is.True, "sensor batch send timed out");
            send.GetAwaiter().GetResult();
            Assert.That(SpinWait.SpinUntil(() => PendingSensorCount(transport, "Poses") >= poses &&
                PendingSensorCount(transport, "Scans") >= scans, TimeSpan.FromSeconds(2)), Is.True,
                "the actual WebSocket worker did not retain the complete bounded batch");
        }

        [UnityTest]
        public IEnumerator MultipleRawScansBeforeDelayedMapperPosesSurviveWorkerAndMainThreadBatching()
        {
            var client = Connect();
            yield return Until(() => _server.Messages.Length >= 4, "map channel requests did not arrive");
            yield return Sent(_server.Send(CalibrationJson(CurrentCalibrationId())));
            yield return Sent(_server.Send(GridJson(8, 8, false)));
            yield return Until(() => client.Live, "raw grid did not arrive");
            object transport = CurrentTransport(client);
            QueueSensorBatchWithoutUnityUpdate(transport, new[] { ScanJson(10), ScanJson(11), ScanJson(12), ScanJson(13),
                PoseJson(10), PoseJson(11) }, poses: 2, scans: 4);
            yield return Until(() => SourceStamp(client, "_scanStamp") == 13000000000L && SourceStamp(client, "_poseStamp") == 11000000000L,
                "first delayed batch was not consumed");
            Assert.That(client.NavigationLive, Is.True, "the exact pair for scan 11 must survive newer raw scan 12/13");
            Assert.That(SourceStamp(client, "_layerScanStamp"), Is.EqualTo(11000000000L));
            QueueSensorBatchWithoutUnityUpdate(transport, new[] { ScanJson(14), ScanJson(15), PoseJson(12), PoseJson(13) }, poses: 2, scans: 2);
            yield return Until(() => SourceStamp(client, "_scanStamp") == 15000000000L && SourceStamp(client, "_poseStamp") == 13000000000L,
                "second delayed batch was not consumed");
            Assert.That(client.NavigationLive, Is.True);
            Assert.That(SourceStamp(client, "_layerScanStamp"), Is.EqualTo(13000000000L));

            var burst = new List<string>();
            for (int stamp = 30; stamp < 50; stamp++) burst.Add(ScanJson(stamp));
            for (int stamp = 30; stamp < 50; stamp++) burst.Add(PoseJson(stamp));
            QueueSensorBatchWithoutUnityUpdate(transport, burst.ToArray(), poses: 16, scans: 16);
            Assert.That(PendingSensorCount(transport, "Poses"), Is.EqualTo(16));
            Assert.That(PendingSensorCount(transport, "Scans"), Is.EqualTo(16));
            yield return Until(() => SourceStamp(client, "_layerScanStamp") == 49000000000L, "the newest retained exact pair did not become usable");
            Assert.That(client.NavigationLive, Is.True);
            Assert.That(client.DiagnosticSummary, Does.Contain("queueDrops=8"));
            AssertReadOnlyRequests();
        }

        private static string GridJson(int width, int height, bool crop)
        {
            var json = new StringBuilder("{\"op\":\"publish\",\"topic\":\"/map\",\"msg\":{\"header\":{\"frame_id\":\"map\"},\"info\":{\"width\":");
            json.Append(width).Append(",\"height\":").Append(height);
            json.Append(crop ? ",\"resolution\":0.05,\"origin\":{\"position\":{\"x\":-8,\"y\":-6.4},\"orientation\":{\"w\":1}}},\"data\":[" :
                ",\"resolution\":1,\"origin\":{\"position\":{\"x\":-4,\"y\":-4},\"orientation\":{\"w\":1}}},\"data\":[");
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                if (x != 0 || y != 0) json.Append(',');
                int value = !crop || (x >= 179 && x <= 185 && y >= 129 && y <= 135) ? 0 : -1;
                if (crop && x == 182 && y == 132) value = 100;
                json.Append(value);
            }
            return json.Append("]}}").ToString();
        }

        private static string PoseJson(int seconds) => "{\"op\":\"publish\",\"topic\":\"/lidar_map/pose\",\"msg\":{\"header\":{\"frame_id\":\"map\",\"stamp\":{\"sec\":" +
            seconds + ",\"nanosec\":0}},\"pose\":{\"position\":{\"x\":0,\"y\":0},\"orientation\":{\"w\":1}}}}";

        // RFC6455 server bound exclusively to localhost; this fixture never contacts ROS or a robot.
        private sealed class LocalMapServer : IDisposable
        {
            private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
            private readonly SemaphoreSlim _send = new SemaphoreSlim(1, 1);
            private readonly List<string> _messages = new List<string>(), _targets = new List<string>();
            private WebSocket _socket;
            public int Port { get; }
            public int ConnectionCount;
            public string Error = "";
            public string[] Messages { get { lock (_messages) return _messages.ToArray(); } }
            public string[] Targets { get { lock (_targets) return _targets.ToArray(); } }

            public LocalMapServer()
            {
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _ = Task.Run(Run);
            }

            public async Task Send(string json, int fragmentBytes = 8192)
            {
                await _send.WaitAsync(_cancel.Token);
                try
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(json);
                    WebSocket socket = _socket;
                    for (int offset = 0; offset < bytes.Length; offset += fragmentBytes)
                    {
                        int count = Math.Min(fragmentBytes, bytes.Length - offset);
                        await socket.SendAsync(new ArraySegment<byte>(bytes, offset, count), WebSocketMessageType.Text,
                            offset + count == bytes.Length, _cancel.Token);
                    }
                }
                finally { _send.Release(); }
            }

            public void DropConnection() => _socket?.Abort();

            private async Task Run()
            {
                while (!_cancel.IsCancellationRequested)
                {
                    try
                    {
                        using (TcpClient client = await _listener.AcceptTcpClientAsync())
                        using (NetworkStream stream = client.GetStream())
                        {
                            var header = new StringBuilder();
                            var one = new byte[1];
                            while (header.Length < 8192 && !header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                            {
                                if (await stream.ReadAsync(one, 0, 1, _cancel.Token) == 0) throw new IOException("short handshake");
                                header.Append((char)one[0]);
                            }
                            string[] lines = header.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None);
                            lock (_targets) _targets.Add(lines[0].Split(' ')[1]);
                            string key = null;
                            foreach (string line in lines)
                                if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                                    key = line.Substring(line.IndexOf(':') + 1).Trim();
                            if (key == null) throw new IOException("missing WebSocket key");
                            string accept;
                            using (SHA1 sha = SHA1.Create()) accept = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                            byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
                            await stream.WriteAsync(response, 0, response.Length, _cancel.Token);
                            using (WebSocket socket = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(10)))
                            {
                                _socket = socket;
                                Interlocked.Increment(ref ConnectionCount);
                                var buffer = new byte[1024];
                                while (!_cancel.IsCancellationRequested)
                                {
                                    using (var message = new MemoryStream())
                                    {
                                        WebSocketReceiveResult part;
                                        do
                                        {
                                            part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancel.Token);
                                            if (part.MessageType != WebSocketMessageType.Text) throw new IOException("client closed");
                                            message.Write(buffer, 0, part.Count);
                                            if (message.Length > 8192) throw new IOException("oversize test request");
                                        } while (!part.EndOfMessage);
                                        lock (_messages) _messages.Add(Encoding.UTF8.GetString(message.ToArray()));
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception error) when (error is IOException || error is WebSocketException || error is SocketException ||
                        error is OperationCanceledException || error is ObjectDisposedException)
                    { if (!_cancel.IsCancellationRequested) Error = error.Message; }
                }
            }

            public void Dispose()
            {
                _cancel.Cancel();
                _socket?.Abort();
                _listener.Stop();
            }
        }
    }
}
#endif
