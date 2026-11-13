#if UNITY_EDITOR
using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace PuppyPi.Tests
{
    // Opt-in read-only hardware probe. No controller or UDP socket is created.
    public class MapReadOnlyRobotProbeTests
    {
        private GameObject _root;
        [UnityTearDown] public IEnumerator Cleanup()
        { if (_root != null) Object.Destroy(_root); yield return null; }

        [UnityTest] public IEnumerator RobotStreamsCalibratedTimestampMatchedNavigation()
        {
            string host = Environment.GetEnvironmentVariable("PUPPYPI_MAP_PROBE_HOST");
            if (string.IsNullOrEmpty(host)) Assert.Ignore("Set PUPPYPI_MAP_PROBE_HOST for an opt-in read-only robot map probe.");
            using (var request = UnityWebRequest.Get(new UriBuilder("http", host, 8090, "/api/map").Uri.AbsoluteUri))
            {
                request.timeout = 5;
                yield return request.SendWebRequest();
                Assert.That(request.result, Is.EqualTo(UnityWebRequest.Result.Success));
                var info = JsonUtility.FromJson<RobotMapInfo>(request.downloadHandler.text);
                Assert.That(info?.session, Is.Not.Null.And.Not.Empty);
                _root = new GameObject("Read-only robot map probe");
                var client = _root.AddComponent<PuppyPiMapDataClient>();
                client.enabled = false;
                client.Connect(host);
                client.SetSession(info.session);
                client.enabled = true;
                double deadline = Time.realtimeSinceStartupAsDouble + 12;
                while (!client.NavigationLive && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
                Assert.That(client.NavigationLive, Is.True, client.NavigationStatus + " | " + client.Status);
                int samples = 0, ready = 0, forwardSamples = 0, forward30Clear = 0, forward60Clear = 0;
                int forward30Unknown = 0, forward30Occupied = 0, rotationClearSamples = 0;
                int rawForward30Occupied = 0, rawForward60Occupied = 0, filteredForward60Occupied = 0;
                int maxRawHits = 0, maxCandidates = 0, maxConfirmed = 0;
                var sampleFootprint = new MapRobotFootprint();
                double warmUntil = Time.realtimeSinceStartupAsDouble + 1;
                double stableUntil = Time.realtimeSinceStartupAsDouble + 10;
                while (Time.realtimeSinceStartupAsDouble < stableUntil)
                {
                    Assert.That(client.EditingAvailable, Is.True, "A short sensor gap must not tear down the editing map.");
                    samples++;
                    if (client.NavigationLive)
                    {
                        ready++;
                        if (Time.realtimeSinceStartupAsDouble >= warmUntil)
                        {
                            forwardSamples++;
                            maxRawHits = Math.Max(maxRawHits, client.RawObstacleCellCount);
                            maxCandidates = Math.Max(maxCandidates, client.CandidateObstacleCellCount);
                            maxConfirmed = Math.Max(maxConfirmed, client.ConfirmedObstacleCellCount);
                            int[] rawCurrent = WithUnconfirmedHits(client);
                            Vector3 pose = client.RobotPose;
                            var position = new Vector2(pose.x, pose.y);
                            var direction = new Vector2(Mathf.Cos(pose.z), Mathf.Sin(pose.z));
                            bool blocked30 = MapDetourPlanner.TryFindRouteBlock(client.Geometry, client.Occupancy,
                                new[] { position, position + direction * .3f }, pose.z, sampleFootprint, out MapPathBlock firstBlock);
                            if (!blocked30) forward30Clear++;
                            else if (firstBlock.Kind == MapPathBlockKind.Unknown) forward30Unknown++;
                            else if (firstBlock.Kind == MapPathBlockKind.Occupied) forward30Occupied++;
                            if (!MapDetourPlanner.TryFindRouteBlock(client.Geometry, client.Occupancy,
                                new[] { position, position + direction * .6f }, pose.z, sampleFootprint, out var block60)) forward60Clear++;
                            else if (block60.Kind == MapPathBlockKind.Occupied) filteredForward60Occupied++;
                            if (MapDetourPlanner.TryFindRouteBlock(client.Geometry, rawCurrent,
                                new[] { position, position + direction * .3f }, pose.z, sampleFootprint, out var rawBlock30) &&
                                rawBlock30.Kind == MapPathBlockKind.Occupied) rawForward30Occupied++;
                            if (MapDetourPlanner.TryFindRouteBlock(client.Geometry, rawCurrent,
                                new[] { position, position + direction * .6f }, pose.z, sampleFootprint, out var rawBlock60) &&
                                rawBlock60.Kind == MapPathBlockKind.Occupied) rawForward60Occupied++;
                            if (!MapPathGeometry.TryFindBlock(client.Geometry, client.Occupancy, new[] { position },
                                sampleFootprint.RotationRadius, out _)) rotationClearSamples++;
                        }
                    }
                    yield return new WaitForSecondsRealtime(.05f);
                }
                double readiness = ready / (double)Math.Max(1, samples);
                Debug.Log($"PUPPYPI_READONLY_MAP_STABILITY readyFraction={readiness:F3} samples={samples} diagnostic={client.DiagnosticSummary}");
                Debug.Log($"PUPPYPI_READONLY_MAP_FORWARD_STABILITY checked={forwardSamples} clear0.3={forward30Clear}/{forwardSamples} clear0.6={forward60Clear}/{forwardSamples} unknown0.3={forward30Unknown} occupied0.3={forward30Occupied} rotationClear={rotationClearSamples}/{forwardSamples} warmup=1s motorCommands=false");
                Debug.Log($"PUPPYPI_READONLY_OBSTACLE_FILTER checked={forwardSamples} rawOccupied0.3={rawForward30Occupied} filteredOccupied0.3={forward30Occupied} rawOccupied0.6={rawForward60Occupied} filteredOccupied0.6={filteredForward60Occupied} maxRawHits={maxRawHits} maxChecking={maxCandidates} maxConfirmed={maxConfirmed} comparison=same-recent-free-proof-plus-unconfirmed-current-hits motorCommands=false");
                Assert.That(readiness, Is.GreaterThanOrEqualTo(.9), "Navigation readiness flickers on the actual read-only robot stream.");
                Assert.That(client.BaseOccupancy, Is.Not.SameAs(client.Occupancy), "current observations must not mutate the accumulated source grid");
                var robot = new Vector2(client.RobotPose.x, client.RobotPose.y);
                bool baseClear = MapPathGeometry.SegmentClear(client.Geometry, client.BaseOccupancy, robot, robot, .22f);
                bool liveClear = MapPathGeometry.SegmentClear(client.Geometry, client.Occupancy, robot, robot, .22f);
                int clearedOccupied = 0, observedUnknown = 0, rawOccupied = 0, liveOccupied = 0;
                int hiddenDarkCells = 0, missingObstaclePixels = 0, darkCandidatePixels = 0, candidateCells = 0;
                Color32[] pixels = client.Texture.GetPixels32();
                for (int i = 0; i < client.Occupancy.Length; i++)
                {
                    if (client.BaseOccupancy[i] >= 50) rawOccupied++;
                    bool occupied = client.Occupancy[i] >= 50;
                    if (occupied) liveOccupied++;
                    if (client.IsObstacleCandidate(CellCenter(client.Geometry, i)))
                    {
                        candidateCells++;
                        if (pixels[i].r < 100) darkCandidatePixels++;
                    }
                    if (pixels[i].r < 100 && !occupied) hiddenDarkCells++;
                    if (occupied && pixels[i].r >= 100) missingObstaclePixels++;
                    if (client.Occupancy[i] != 0) continue;
                    if (client.BaseOccupancy[i] >= 50) clearedOccupied++;
                    else if (client.BaseOccupancy[i] < 0) observedUnknown++;
                }
                Assert.That(liveOccupied, Is.EqualTo(client.CurrentObstacleCellCount));
                Assert.That(liveOccupied, Is.EqualTo(client.ConfirmedObstacleCellCount));
                Assert.That(candidateCells, Is.EqualTo(client.CandidateObstacleCellCount));
                Assert.That(client.RawObstacleCellCount, Is.EqualTo(candidateCells + liveOccupied));
                Assert.That(darkCandidatePixels, Is.Zero, "unconfirmed sensor hits must not appear as confirmed dark obstacle pixels");
                Assert.That(client.CurrentObstacleCellCount + client.CurrentFreeCellCount + client.CurrentUnknownCellCount,
                    Is.EqualTo(client.Occupancy.Length));
                Assert.That(liveOccupied, Is.LessThanOrEqualTo(2048), "a latest scan cannot create more occupied cells than supported measured rays");
                Assert.That(hiddenDarkCells, Is.Zero, "the display must not redraw accumulated obstacle debris");
                Assert.That(missingObstaclePixels, Is.Zero, "actual current obstacles must remain visible");

                var footprint = new MapRobotFootprint();
                bool bodyClear = !MapPathGeometry.TryFindFootprintBlock(client.Geometry, client.Occupancy,
                    new[] { robot }, client.RobotPose.z, footprint, out MapPathBlock bodyBlock);
                bool rotationClear = !MapPathGeometry.TryFindBlock(client.Geometry, client.Occupancy,
                    new[] { robot }, footprint.RotationRadius, out MapPathBlock rotationBlock);
                int clearCandidates = 0, unknownCandidates = 0, obstacleCandidates = 0, boundaryCandidates = 0;
                var candidateDetails = new System.Text.StringBuilder();
                for (int heading = 0; heading < 8; heading++)
                foreach (float distance in new[] { .3f, .6f })
                {
                    float yaw = client.RobotPose.z + heading * Mathf.PI / 4;
                    Vector2 target = robot + new Vector2(Mathf.Cos(yaw), Mathf.Sin(yaw)) * distance;
                    bool blocked = MapDetourPlanner.TryFindRouteBlock(client.Geometry, client.Occupancy,
                        new[] { robot, target }, client.RobotPose.z, footprint, out MapPathBlock block);
                    if (!blocked) clearCandidates++;
                    else if (block.Kind == MapPathBlockKind.Unknown) unknownCandidates++;
                    else if (block.Kind == MapPathBlockKind.Occupied) obstacleCandidates++;
                    else if (block.Kind == MapPathBlockKind.Boundary) boundaryCandidates++;
                    candidateDetails.Append($" h{heading}/{distance:F1} target=({target.x:F3},{target.y:F3}) {(blocked ? Describe(block, robot) : "clear")}");
                }
                // Environment obstacles are legitimate failures. This opt-in
                // probe reports candidates without sending Execute/path/drive.
                Debug.Log($"PUPPYPI_READONLY_MAP_ROUTE_CANDIDATES robot=({client.RobotPose.x:F4},{client.RobotPose.y:F4},{client.RobotPose.z:F4}rad) bodyClear={bodyClear} bodyBlock={Describe(bodyBlock, robot)} rotationRadius={footprint.RotationRadius:F4} rotationClear={rotationClear} rotationBlock={Describe(rotationBlock, robot)} clear={clearCandidates}/16 unknown={unknownCandidates} occupied={obstacleCandidates} boundary={boundaryCandidates} detail={candidateDetails}");
                Debug.Log($"PUPPYPI_READONLY_MAP_PROBE navigationLive={client.NavigationLive} sourceDiskClear={baseClear} currentDiskClear={liveClear} storedOccupied={rawOccupied} rawScanOccupied={client.RawObstacleCellCount} checking={client.CandidateObstacleCellCount} confirmed={client.ConfirmedObstacleCellCount} darkChecking={darkCandidatePixels} currentOccupied={liveOccupied} currentFree={client.CurrentFreeCellCount} recentFree={client.RecentFreeCellCount} freeWindow={client.RecentFreeWindowSeconds:F2}s currentUnknown={client.CurrentUnknownCellCount} clearedOccupied={clearedOccupied} observedUnknown={observedUnknown} hiddenDark={hiddenDarkCells} missingObstaclePixels={missingObstaclePixels} width={client.Geometry.width} height={client.Geometry.height} status={client.NavigationStatus} filter={client.FilterStatus}");
            }
        }

        // This comparison changes only current candidate hits to occupied. It
        // keeps exactly the same measured free-space evidence and footprint.
        private static int[] WithUnconfirmedHits(PuppyPiMapDataClient client)
        {
            var cells = (int[])client.Occupancy.Clone();
            for (int i = 0; i < cells.Length; i++)
                if (client.IsObstacleCandidate(CellCenter(client.Geometry, i))) cells[i] = 100;
            return cells;
        }

        private static Vector2 CellCenter(MapGridGeometry geometry, int index) => geometry.origin +
            new Vector2(index % geometry.width + .5f, index / geometry.width + .5f) * geometry.resolution;

        private static string Describe(MapPathBlock block, Vector2 robot) => block == null ? "clear" :
            $"{block.Kind}@({block.Position.x:F3},{block.Position.y:F3}) cellValue={block.CellValue} centerDistance={Vector2.Distance(robot, block.Position):F3} segment={block.SegmentIndex}";
    }
}
#endif
