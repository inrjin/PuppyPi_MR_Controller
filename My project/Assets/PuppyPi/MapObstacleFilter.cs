using System;
using System.Collections.Generic;
using UnityEngine;

namespace PuppyPi
{
    // Confidence is computed in map coordinates from distinct, recent sensor
    // observations. A pending return does not certify free space; the caller
    // may separately reuse its still-valid measured-free evidence underneath.
    public sealed class MapObstacleFilter
    {
        public const double ObservationWindowSeconds = .75;
        public const double MaximumNewObservationAgeSeconds = 1.5;
        public const double MinimumVoteSpacingSeconds = .06;
        public const double ClusterConfirmationSpanSeconds = .16;
        public const double IsolatedConfirmationSpanSeconds = .24;
        private const double ClearSpanSeconds = .10;
        private const int MaximumCells = 1024 * 1024, MaximumHits = 4096, MaximumObservations = 14;
        private const int OccupiedThreshold = 50;

        public int RawOccupiedCount { get; private set; }
        public int ConfirmedCellCount => _confirmedCurrent.Count;
        public int CandidateCellCount => _candidates.Count;
        public IReadOnlyList<int> CandidateIndices => _candidates;
        public IReadOnlyList<int> ConfirmedIndices => _confirmedCurrent;
        public IReadOnlyList<int> RawOccupiedIndices => _rawCurrent;
        public bool LastObservationAddedVote { get; private set; }
        public long LastSourceStamp => _lastStamp;

        private sealed class Observation
        {
            public double At;
            public HashSet<int> Hits;
        }

        private sealed class Track
        {
            public double LastHitAt;
            public int FreeSamples;
            public double FirstFreeAt;
        }

        private readonly List<Observation> _history = new List<Observation>();
        private readonly Dictionary<int, Track> _tracks = new Dictionary<int, Track>();
        private readonly HashSet<int> _cachedConfirmed = new HashSet<int>();
        private readonly List<int> _rawCurrent = new List<int>(), _candidates = new List<int>(), _confirmedCurrent = new List<int>();
        private MapGridGeometry _geometry;
        private long _lastStamp = -1;
        private double _lastObservedAt = double.NegativeInfinity, _lastNow = double.NegativeInfinity;
        private double _lastVoteAt = double.NegativeInfinity;

        public void Reset()
        {
            _history.Clear();
            _tracks.Clear();
            _cachedConfirmed.Clear();
            _rawCurrent.Clear();
            _candidates.Clear();
            _confirmedCurrent.Clear();
            _geometry = null;
            _lastStamp = -1;
            _lastObservedAt = _lastNow = _lastVoteAt = double.NegativeInfinity;
            RawOccupiedCount = 0;
            LastObservationAddedVote = false;
        }

        public bool TryApply(MapGridGeometry geometry, int[] rawCells, long sourceStamp,
            double observedAt, double now, out int[] filtered, out string error)
        {
            filtered = null;
            error = null;
            if (geometry == null || !geometry.IsValid || (long)geometry.width * geometry.height > MaximumCells ||
                rawCells == null || rawCells.Length != (long)geometry.width * geometry.height)
                return Fail("Invalid obstacle-filter grid.", out error);
            if (sourceStamp < 0 || !Finite(observedAt) || !Finite(now) || observedAt < 0 || now < 0 || now < observedAt - 1e-6 ||
                now < _lastNow - 1e-6)
                return Fail("Invalid obstacle-filter source stamp or observation clock.", out error);
            int hitCount = 0;
            foreach (int value in rawCells)
            {
                if (value < -1 || value > 100) return Fail("Invalid obstacle-filter occupancy value.", out error);
                if (value >= OccupiedThreshold && ++hitCount > MaximumHits)
                    return Fail("Obstacle-filter observation exceeds its endpoint budget.", out error);
            }

            PrepareGeometry(geometry);
            if (sourceStamp < _lastStamp) return Fail("Obstacle-filter source stamp moved backwards.", out error);
            bool duplicate = sourceStamp == _lastStamp;
            // A re-projection of an accepted scan is not a newly received scan.
            // It may reuse its cached decision, but never refresh its clock.
            double effectiveAt = duplicate ? _lastObservedAt : observedAt;
            if (now - effectiveAt >= (duplicate ? 1.5 : MaximumNewObservationAgeSeconds))
                return Fail("Obstacle-filter observation is stale.", out error);
            _lastNow = now;
            _rawCurrent.Clear();
            _candidates.Clear();
            _confirmedCurrent.Clear();
            RawOccupiedCount = hitCount;
            LastObservationAddedVote = false;
            var hits = new HashSet<int>();
            for (int i = 0; i < rawCells.Length; i++)
                if (rawCells[i] >= OccupiedThreshold) { hits.Add(i); _rawCurrent.Add(i); }

            filtered = (int[])rawCells.Clone();
            if (duplicate)
            {
                foreach (int cell in _rawCurrent)
                    SetDecision(filtered, cell, _cachedConfirmed.Contains(cell));
                return true;
            }

            Prune(now, rawCells, effectiveAt);
            if (effectiveAt - _lastVoteAt >= MinimumVoteSpacingSeconds - 1e-6)
            {
                _history.Add(new Observation { At = effectiveAt, Hits = hits });
                _lastVoteAt = effectiveAt;
                LastObservationAddedVote = true;
                while (_history.Count > MaximumObservations) _history.RemoveAt(0);
            }
            _cachedConfirmed.Clear();
            foreach (int cell in _rawCurrent)
            {
                bool coherent = NeighborCount(hits, cell, 2) >= 2;
                bool confirmed = HasConfirmedNeighbor(cell, now) ||
                    HasVotes(cell, coherent ? 4 : 5, coherent ? 3 : 4,
                        coherent ? ClusterConfirmationSpanSeconds : IsolatedConfirmationSpanSeconds);
                SetDecision(filtered, cell, confirmed);
                if (!confirmed) continue;
                _cachedConfirmed.Add(cell);
            }
            // Do not let a newly confirmed cell turn its entire neighbouring
            // chain into confirmed obstacles within this same observation.
            foreach (int cell in _confirmedCurrent)
            {
                if (!_tracks.TryGetValue(cell, out Track track))
                { track = new Track(); _tracks.Add(cell, track); }
                track.LastHitAt = Math.Max(track.LastHitAt, effectiveAt);
                track.FreeSamples = 0;
                track.FirstFreeAt = double.PositiveInfinity;
            }
            _lastStamp = sourceStamp;
            _lastObservedAt = effectiveAt;
            return true;
        }

        private void SetDecision(int[] filtered, int cell, bool confirmed)
        {
            if (confirmed) { filtered[cell] = 100; _confirmedCurrent.Add(cell); }
            else { filtered[cell] = -1; _candidates.Add(cell); }
        }

        private void Prune(double now, int[] current, double observedAt)
        {
            _history.RemoveAll(sample => now - sample.At >= ObservationWindowSeconds);
            var remove = new List<int>();
            foreach (var pair in _tracks)
            {
                Track track = pair.Value;
                if (now - track.LastHitAt >= ObservationWindowSeconds) { remove.Add(pair.Key); continue; }
                if (current[pair.Key] != 0) continue;
                if (track.FreeSamples == 0) track.FirstFreeAt = observedAt;
                track.FreeSamples++;
                if (track.FreeSamples >= 2 && observedAt - track.FirstFreeAt >= ClearSpanSeconds - 1e-6)
                    remove.Add(pair.Key);
            }
            foreach (int cell in remove) _tracks.Remove(cell);
        }

        private bool HasConfirmedNeighbor(int cell, double now)
        {
            int x = cell % _geometry.width, y = cell / _geometry.width;
            for (int py = Math.Max(0, y - 1); py <= Math.Min(_geometry.height - 1, y + 1); py++)
            for (int px = Math.Max(0, x - 1); px <= Math.Min(_geometry.width - 1, x + 1); px++)
                if (_tracks.TryGetValue(py * _geometry.width + px, out Track track) &&
                    now - track.LastHitAt < ObservationWindowSeconds) return true;
            return false;
        }

        private bool HasVotes(int cell, int lastObservations, int requiredHits, double requiredSpan)
        {
            int count = 0;
            double first = double.PositiveInfinity, last = double.NegativeInfinity;
            for (int i = Math.Max(0, _history.Count - lastObservations); i < _history.Count; i++)
            {
                Observation sample = _history[i];
                if (NeighborCount(sample.Hits, cell, 1) == 0) continue;
                count++;
                first = Math.Min(first, sample.At);
                last = Math.Max(last, sample.At);
            }
            return count >= requiredHits && last - first >= requiredSpan - 1e-6;
        }

        private int NeighborCount(HashSet<int> cells, int cell, int stopAt)
        {
            int count = 0, x = cell % _geometry.width, y = cell / _geometry.width;
            for (int py = Math.Max(0, y - 1); py <= Math.Min(_geometry.height - 1, y + 1); py++)
            for (int px = Math.Max(0, x - 1); px <= Math.Min(_geometry.width - 1, x + 1); px++)
                if (cells.Contains(py * _geometry.width + px) && ++count >= stopAt) return count;
            return count;
        }

        private void PrepareGeometry(MapGridGeometry next)
        {
            if (_geometry == null) { _geometry = Copy(next); return; }
            if (_geometry.width == next.width && _geometry.height == next.height &&
                _geometry.origin.x == next.origin.x && _geometry.origin.y == next.origin.y &&
                _geometry.resolution == next.resolution && _geometry.frame == next.frame) return;
            double dx = ((double)next.origin.x - _geometry.origin.x) / next.resolution;
            double dy = ((double)next.origin.y - _geometry.origin.y) / next.resolution;
            double rx = Math.Round(dx), ry = Math.Round(dy);
            bool aligned = _geometry.frame == next.frame && _geometry.resolution == next.resolution &&
                Math.Abs(dx - rx) <= .001 && Math.Abs(dy - ry) <= .001 &&
                rx > -next.width && rx < _geometry.width && ry > -next.height && ry < _geometry.height;
            if (!aligned) { Reset(); _geometry = Copy(next); return; }
            int offsetX = (int)rx, offsetY = (int)ry;
            foreach (Observation sample in _history) sample.Hits = Rebase(sample.Hits, next, offsetX, offsetY);
            var shifted = new Dictionary<int, Track>();
            foreach (var pair in _tracks)
                if (TryRebase(pair.Key, next, offsetX, offsetY, out int cell)) shifted.Add(cell, pair.Value);
            _tracks.Clear();
            foreach (var pair in shifted) _tracks.Add(pair.Key, pair.Value);
            HashSet<int> confirmed = Rebase(_cachedConfirmed, next, offsetX, offsetY);
            _cachedConfirmed.Clear();
            foreach (int cell in confirmed) _cachedConfirmed.Add(cell);
            _geometry = Copy(next);
        }

        private HashSet<int> Rebase(HashSet<int> cells, MapGridGeometry next, int dx, int dy)
        {
            var result = new HashSet<int>();
            foreach (int index in cells)
                if (TryRebase(index, next, dx, dy, out int cell)) result.Add(cell);
            return result;
        }

        private bool TryRebase(int before, MapGridGeometry next, int dx, int dy, out int cell)
        {
            int x = before % _geometry.width - dx, y = before / _geometry.width - dy;
            cell = y * next.width + x;
            return x >= 0 && x < next.width && y >= 0 && y < next.height;
        }

        private static MapGridGeometry Copy(MapGridGeometry geometry) => new MapGridGeometry {
            width = geometry.width, height = geometry.height, resolution = geometry.resolution,
            origin = geometry.origin, frame = geometry.frame
        };
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static bool Fail(string message, out string error) { error = message; return false; }
    }
}
