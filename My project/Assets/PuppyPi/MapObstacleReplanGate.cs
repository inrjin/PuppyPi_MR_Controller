using System;
using System.Collections.Generic;
using UnityEngine;

namespace PuppyPi
{
    public enum MapObstacleDecision { Clear, Verifying, Replan, UnconfirmedStop }

    // A robot-side blocked snapshot can outlive a short raw hit. Replacing the
    // route therefore needs independent, persistent confirmed map evidence.
    // This gate never sends commands and never turns missing data into free.
    public sealed class MapObstacleReplanGate
    {
        public const int RequiredObservations = 3;
        public const double ConfirmationSeconds = .40;
        public const double MaximumObservationAgeSeconds = .60;
        public const double ConfirmationHistorySeconds = 1.0;
        public const double MaximumUnconfirmedBlockedSeconds = 3.0;
        public const float MatchDistanceMeters = .15f;
        private const double MinimumVoteSpacingSeconds = .05;
        private const int MaximumObservations = 16;

        private readonly List<double> _observedAt = new List<double>();
        private long _lastSourceStamp = -1;
        private double _lastNow = double.NegativeInfinity;
        private double _lastObservedAt = double.NegativeInfinity;
        private double _lastVoteAt = double.NegativeInfinity;
        private double _blockedSince = double.PositiveInfinity;
        private bool _hasConflict;
        private Vector2 _anchor;
        private int _segment;

        public void Reset()
        {
            ResetConflict();
            _lastSourceStamp = -1;
            _lastNow = double.NegativeInfinity;
            _lastObservedAt = double.NegativeInfinity;
            _blockedSince = double.PositiveInfinity;
        }

        public MapObstacleDecision Observe(long sourceStamp, double observedAt, double now,
            MapPathBlock confirmedObstacleOrNull, bool robotBlocked)
        {
            if (sourceStamp < 0 || !Finite(observedAt) || !Finite(now) || now < observedAt - 1e-6 ||
                now < _lastNow - 1e-6 || now - observedAt >= MaximumObservationAgeSeconds ||
                sourceStamp < _lastSourceStamp || observedAt < _lastObservedAt - 1e-6 ||
                sourceStamp == _lastSourceStamp && Math.Abs(observedAt - _lastObservedAt) > 1e-6)
            {
                ResetConflict();
                return MapObstacleDecision.UnconfirmedStop;
            }
            bool newSource = sourceStamp > _lastSourceStamp;
            _lastSourceStamp = sourceStamp;
            _lastNow = now;
            _lastObservedAt = observedAt;
            if (robotBlocked)
            {
                if (double.IsPositiveInfinity(_blockedSince)) _blockedSince = now;
            }
            else _blockedSince = double.PositiveInfinity;

            var obstacle = confirmedObstacleOrNull;
            if (obstacle == null)
            {
                ResetConflict();
                return BlockedDecision(now, robotBlocked);
            }
            if (obstacle.Kind != MapPathBlockKind.Occupied || obstacle.SegmentIndex < 0 ||
                !Finite(obstacle.Position.x) || !Finite(obstacle.Position.y))
            {
                ResetConflict();
                return MapObstacleDecision.UnconfirmedStop;
            }
            if (!_hasConflict || obstacle.SegmentIndex != _segment ||
                (obstacle.Position - _anchor).sqrMagnitude > MatchDistanceMeters * MatchDistanceMeters + 1e-7f)
            {
                ResetConflict();
                _hasConflict = true;
                _anchor = obstacle.Position;
                _segment = obstacle.SegmentIndex;
            }
            // Admission needs a fresh current frame. Already accepted original
            // times live longer so ordinary HTTP/scan delay cannot prevent
            // three observations from ever spanning the confirmation interval.
            _observedAt.RemoveAll(at => now - at >= ConfirmationHistorySeconds);
            if (newSource && observedAt - _lastVoteAt >= MinimumVoteSpacingSeconds - 1e-6)
            {
                _observedAt.Add(observedAt);
                _lastVoteAt = observedAt;
                if (_observedAt.Count > MaximumObservations) _observedAt.RemoveAt(0);
            }
            if (_observedAt.Count >= RequiredObservations &&
                _observedAt[_observedAt.Count - 1] - _observedAt[0] >= ConfirmationSeconds - 1e-6)
                return MapObstacleDecision.Replan;
            return robotBlocked ? BlockedDecision(now, true) : MapObstacleDecision.Verifying;
        }

        private MapObstacleDecision BlockedDecision(double now, bool blocked) => !blocked ? MapObstacleDecision.Clear :
            now - _blockedSince >= MaximumUnconfirmedBlockedSeconds ? MapObstacleDecision.UnconfirmedStop :
            MapObstacleDecision.Verifying;

        private void ResetConflict()
        {
            _hasConflict = false;
            _observedAt.Clear();
            _lastVoteAt = double.NegativeInfinity;
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
