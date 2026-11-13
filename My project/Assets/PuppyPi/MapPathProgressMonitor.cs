using System;
using UnityEngine;

namespace PuppyPi
{
    // Inspired by Nav2's baseline-pose / movement-time-allowance design:
    // https://github.com/ros-navigation/navigation2/blob/main/nav2_controller/plugins/pose_progress_checker.cpp
    // This independent implementation measures progress toward the current
    // target separately for straight driving and stopped turning. Its 5 cm,
    // .05 rad and 8 s defaults are PuppyPi policy, not Nav2 defaults.
    public sealed class MapPathProgressMonitor
    {
        public double MovementMeters { get; }
        public double TurnRadians { get; }
        public double DriveTimeoutSeconds { get; }
        public double RotateTimeoutSeconds { get; }
        public double ArrivalMeters { get; }

        private bool _hasBaseline, _hasClock;
        private double _lastAt, _baselineAt, _baselineDistance, _baselineBearing, _baselineAngleError, _baselineYaw;
        private string _state, _failure;
        private int _waypointIndex;
        private Vector2 _target;

        public MapPathProgressMonitor(double movementMeters = .05, double turnRadians = .05,
            double driveTimeoutSeconds = 8, double rotateTimeoutSeconds = 8, double arrivalMeters = .10)
        {
            if (!Positive(movementMeters) || !Positive(turnRadians) || turnRadians > Math.PI ||
                !Positive(driveTimeoutSeconds) || !Positive(rotateTimeoutSeconds) || !Finite(arrivalMeters) || arrivalMeters < 0)
                throw new ArgumentOutOfRangeException(nameof(movementMeters), "Progress thresholds and timeouts must be finite and positive.");
            MovementMeters = movementMeters;
            TurnRadians = turnRadians;
            DriveTimeoutSeconds = driveTimeoutSeconds;
            RotateTimeoutSeconds = rotateTimeoutSeconds;
            ArrivalMeters = arrivalMeters;
            Reset();
        }

        public void Reset()
        {
            _hasBaseline = _hasClock = false;
            _state = _failure = null;
            _waypointIndex = -1;
        }

        public bool Check(Vector3 pose, string state, int waypointIndex, Vector2 target, double now, out string reason)
        {
            reason = _failure;
            if (_failure != null) return false;
            if (!Finite(pose.x) || !Finite(pose.y) || !Finite(pose.z) || !Finite(target.x) || !Finite(target.y) ||
                !Finite(now) || now < 0 || waypointIndex < 0 || !KnownState(state))
                return Fail("Invalid route progress pose, target, state or clock.", out reason);
            if (_hasClock && now < _lastAt)
                return Fail("Route progress clock moved backwards.", out reason);
            _hasClock = true;
            _lastAt = now;
            if (state != "drive" && state != "rotate")
            {
                _hasBaseline = false;
                return true;
            }
            double dx = (double)target.x - pose.x, dy = (double)target.y - pose.y;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            double bearing = Math.Atan2(dy, dx);
            if (!_hasBaseline || _state != state || _waypointIndex != waypointIndex)
            {
                Baseline(pose, state, waypointIndex, target, now, distance, bearing);
                return true;
            }
            if (_target != target)
                return Fail("The route progress target changed without a new waypoint or reset.", out reason);
            // Near an arrived goal, allow the next follower-status update to
            // report completion without requiring another 5 cm of movement.
            if (distance <= ArrivalMeters)
            {
                Baseline(pose, state, waypointIndex, target, now, distance, bearing);
                return true;
            }
            const double epsilon = 1e-7;
            double timeout;
            bool progressed;
            if (state == "drive")
            {
                // A new best target distance must improve by the full amount.
                // Sideways motion, yaw-only motion and back-and-forth jitter
                // cannot repeatedly reset this baseline.
                progressed = _baselineDistance - distance + epsilon >= MovementMeters;
                timeout = DriveTimeoutSeconds;
            }
            else
            {
                // Hold the baseline target bearing fixed until a meaningful
                // turn. Translating past the target cannot impersonate yaw.
                double angleError = Math.Abs(Wrap(_baselineBearing - pose.z));
                double yawChange = Math.Abs(Wrap((double)pose.z - _baselineYaw));
                progressed = _baselineAngleError - angleError + epsilon >= TurnRadians &&
                    yawChange + epsilon >= TurnRadians;
                timeout = RotateTimeoutSeconds;
            }
            if (progressed)
            {
                Baseline(pose, state, waypointIndex, target, now, distance, bearing);
                return true;
            }
            if (now - _baselineAt >= timeout)
                return Fail(state == "drive" ?
                    $"No forward progress toward waypoint {waypointIndex + 1} for {timeout:F1}s; stop and inspect the route." :
                    $"No turning progress toward waypoint {waypointIndex + 1} for {timeout:F1}s; stop and inspect the route.", out reason);
            return true;
        }

        private void Baseline(Vector3 pose, string state, int waypointIndex, Vector2 target,
            double now, double distance, double bearing)
        {
            _hasBaseline = true;
            _state = state;
            _waypointIndex = waypointIndex;
            _target = target;
            _baselineAt = now;
            _baselineDistance = distance;
            _baselineBearing = bearing;
            _baselineYaw = pose.z;
            _baselineAngleError = Math.Abs(Wrap(bearing - pose.z));
        }

        private bool Fail(string message, out string reason)
        {
            reason = _failure = message;
            return false;
        }
        private static bool KnownState(string state) => state == "idle" || state == "drive" || state == "rotate" ||
            state == "blocked" || state == "done" || state == "cancelled";
        private static double Wrap(double angle) => Math.Atan2(Math.Sin(angle), Math.Cos(angle));
        private static bool Positive(double value) => Finite(value) && value > 0;
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
