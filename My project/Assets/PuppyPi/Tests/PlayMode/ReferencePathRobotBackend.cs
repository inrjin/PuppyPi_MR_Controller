#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace PuppyPi.Tests
{
    [Serializable]
    internal sealed class ReferencePathRobotConfig
    {
        public bool pauseSensors, allBeamsMissing, ignoreFollowerStart, dropNextPathAck, holdCancelAck;
        public int pathAckDelayMilliseconds, callbackDelayMilliseconds;
        public bool motionPermitted = true, obstacle, periodicGhosts, resetPose;
        public double resetPoseX, resetPoseY, resetPoseYaw;
        public double obstacleX, obstacleY, obstacleRadius = .07;
        public double ghostPeriodSeconds = .8, ghostDurationSeconds = .1, ghostRangeMeters = .3;
        public double startHoldSeconds = .2;
        public bool secondObstacle;
        public double secondObstacleX, secondObstacleY, secondObstacleRadius = .07;
        public int heartbeatDropEvery, pathCancelAckDelayMilliseconds, dropPathCancelCount;
        public double heartbeatBlackoutSeconds;
    }

    [Serializable]
    internal sealed class ReferencePathRobotSnapshot
    {
        public double x, y, yaw, started, elapsed, cancelAckAt;
        public string state, pointsJson, pathJson, poseJson, scanJson, error;
        public bool active, motionPermitted;
        public long scanNumber, lastPathSequence;
        public int index, paths, cancels, heartbeats, nonNeutral, collisionSamples, ghostScans, blockedCount, validRays;
        public int droppedHeartbeats, droppedCancels, emergencyCommands;
        public int pathDatagrams, cancelDatagrams, emergencyDatagrams;
        public string[] events;
    }

    // Test-only stdio bridge. The Python process binds UDP exclusively to
    // localhost and imports the actual pure robot modules without ROS/motors.
    internal sealed class ReferencePathRobotBackend : IDisposable
    {
        [Serializable] private sealed class Hello { public int udp_port; }
        [Serializable] private sealed class ConfigurationRequest
        { public string op = "configure"; public ReferencePathRobotConfig values; }
        private readonly object _gate = new object();
        private readonly object _errorGate = new object();
        private readonly Process _process;
        private string _stderr = "";
        private bool _disposed;
        public int UdpPort { get; }
        public string HelloJson { get; }

        public ReferencePathRobotBackend(string robotSource = null)
        {
            string repository = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            string helper = Path.Combine(repository, "tools", "path_robot_reference_peer.py");
            robotSource = robotSource ?? Path.Combine(repository, ".local", "robot-fix-review", "puppy_vr_bridge");
            if (!File.Exists(helper) || !File.Exists(Path.Combine(robotSource, "path_follower.py")))
                throw new FileNotFoundException("The actual patched robot reference is required for this test: " + robotSource);
            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "python3",
                    Arguments = Quote(helper) + " --robot-src " + Quote(Path.GetFullPath(robotSource)),
                    WorkingDirectory = repository,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                },
                EnableRaisingEvents = true
            };
            _process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_errorGate) _stderr += e.Data + "\n"; };
            try
            {
                if (!_process.Start()) throw new InvalidOperationException("Could not start the reference Python process.");
                _process.BeginErrorReadLine();
                HelloJson = ReadLine();
                var hello = JsonUtility.FromJson<Hello>(HelloJson);
                if (hello == null || hello.udp_port < 1 || hello.udp_port > 65535)
                    throw new InvalidDataException("Invalid reference peer hello: " + HelloJson);
                UdpPort = hello.udp_port;
            }
            catch { Dispose(); throw; }
        }

        private static string Quote(string text)
        {
            if (text.IndexOf('"') >= 0 || text.IndexOf('\n') >= 0 || text.IndexOf('\r') >= 0)
                throw new ArgumentException("Invalid reference helper path.");
            return "\"" + text + "\"";
        }

        private string ReadLine()
        {
            Task<string> pending = _process.StandardOutput.ReadLineAsync();
            if (!pending.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Reference peer did not respond within five seconds. " + _stderr);
            string result = pending.Result;
            if (result == null)
                throw new EndOfStreamException("Reference peer exited unexpectedly. " + _stderr);
            return result;
        }

        private string Request(string json)
        {
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ReferencePathRobotBackend));
                _process.StandardInput.WriteLine(json);
                _process.StandardInput.Flush();
                return ReadLine();
            }
        }

        public void Configure(ReferencePathRobotConfig values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            string response = Request(JsonUtility.ToJson(new ConfigurationRequest { values = values }));
            if (!response.Contains("\"ok\":true"))
                throw new InvalidDataException("Reference peer configuration failed: " + response);
        }

        public ReferencePathRobotSnapshot Snapshot()
        {
            string json = Request("{\"op\":\"snapshot\"}");
            var snapshot = JsonUtility.FromJson<ReferencePathRobotSnapshot>(json);
            if (snapshot == null || snapshot.state == null || snapshot.pathJson == null)
                throw new InvalidDataException("Invalid reference peer snapshot: " + json);
            return snapshot;
        }

        public void ReleaseCancelReceipts() => Request("{\"op\":\"release_cancel_receipts\"}");

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.StandardInput.WriteLine("{\"op\":\"shutdown\"}");
                        _process.StandardInput.Flush();
                        if (!_process.WaitForExit(2500)) _process.Kill();
                    }
                }
                catch (Exception e) when (e is InvalidOperationException || e is IOException || e is System.ComponentModel.Win32Exception)
                { /* Partial startup still needs disposal. */ }
                finally { _disposed = true; _process.Dispose(); }
            }
        }
    }
}
#endif
