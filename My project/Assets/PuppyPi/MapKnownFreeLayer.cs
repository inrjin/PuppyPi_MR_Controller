using System;
using System.Collections.Generic;

namespace PuppyPi
{
    // Previously mapped free space is a prior, not a new sensor observation.
    // Confirmed current hits veto that prior until a direct free ray clears it.
    public sealed class MapKnownFreeLayer
    {
        private const int MaximumCells = 1024 * 1024;
        private const int MaximumRevokedCells = 65536;
        // Match the mapper's free_thresh=0.25 classification of its 0..100
        // OccupancyGrid probabilities. Unknown (-1) and values above 25 never
        // grant map-prior permission; this is not a new laser observation.
        private const int MapperFreeThresholdPercent = 25;
        private readonly HashSet<long> _revoked = new HashSet<long>();
        private readonly HashSet<int> _reused = new HashSet<int>();
        private readonly HashSet<int> _directReplacements = new HashSet<int>();
        private MapGridGeometry _geometry;
        private double _anchorX, _anchorY;
        private int _offsetX, _offsetY;
        private int[] _lastMergedCells;
        private bool _faulted;

        public int ReusedCellCount => _reused.Count;
        public bool IsReusedCell(int index) => _reused.Contains(index);

        public void ClearPresentation()
        {
            _reused.Clear();
            _directReplacements.Clear();
            _lastMergedCells = null;
        }

        public void Reset()
        {
            ClearPresentation();
            _revoked.Clear();
            _geometry = null;
            _anchorX = _anchorY = 0;
            _offsetX = _offsetY = 0;
            _faulted = false;
        }

        public bool Observe(MapGridGeometry geometry, int[] currentFilteredScanCells, out string error)
        {
            error = null;
            if (_faulted) return Fail("Known-free revocation budget exceeded; a new map session is required.", out error);
            if (geometry == null || !geometry.IsValid ||
                (long)geometry.width * geometry.height > MaximumCells ||
                !ValidCells(currentFilteredScanCells, (long)geometry.width * geometry.height))
                return Fail("Invalid known-free observation grid.", out error);
            if (!PrepareGeometry(geometry, out error)) return false;

            var additions = new List<long>();
            var removals = new List<long>();
            _directReplacements.Clear();
            bool replacesSameOutput = ReferenceEquals(currentFilteredScanCells, _lastMergedCells);
            for (int i = 0; i < currentFilteredScanCells.Length; i++)
            {
                int value = currentFilteredScanCells[i];
                if (value < 50 && value != 0) continue;
                long key = WorldKey(i);
                if (value >= 50)
                {
                    if (!_revoked.Contains(key)) additions.Add(key);
                }
                else
                {
                    if (_revoked.Contains(key)) removals.Add(key);
                    // Also support a caller overwriting its previous output
                    // with genuine direct scan evidence in the same array.
                    if (replacesSameOutput && _reused.Contains(i)) _directReplacements.Add(i);
                }
            }
            if ((long)_revoked.Count + additions.Count - removals.Count > MaximumRevokedCells)
            {
                _faulted = true;
                ClearPresentation();
                return Fail("Known-free revocation budget exceeded; a new map session is required.", out error);
            }
            foreach (long key in removals) _revoked.Remove(key);
            foreach (long key in additions) _revoked.Add(key);
            return true;
        }

        // Called AFTER measured-free reuse. This method never updates a clock
        // or clears a veto; only Observe may admit direct scan evidence.
        public bool TryMerge(int[] mapCells, int[] cells, out string error)
        {
            error = null;
            if (_faulted) return Fail("Known-free revocation budget exceeded; a new map session is required.", out error);
            if (_geometry == null || ReferenceEquals(mapCells, cells) ||
                !ValidCells(mapCells, (long)_geometry.width * _geometry.height) ||
                !ValidCells(cells, (long)_geometry.width * _geometry.height))
                return Fail("Invalid known-free merge grid.", out error);

            if (ReferenceEquals(cells, _lastMergedCells))
            {
                // Recompute our own contribution on expiry/map refresh. Do not
                // mistake a previously applied prior for independent free proof.
                foreach (int index in _reused)
                    if (cells[index] == 0 && !_directReplacements.Contains(index)) cells[index] = -1;
            }
            _reused.Clear();
            _directReplacements.Clear();
            _lastMergedCells = cells;
            for (int i = 0; i < cells.Length; i++)
            {
                if (cells[i] == 0 && _revoked.Count > 0 && _revoked.Contains(WorldKey(i)))
                {
                    // A cached free value cannot override a newer confirmed
                    // hit. Direct free has already removed the veto in Observe.
                    cells[i] = -1;
                    continue;
                }
                if (cells[i] != -1 || mapCells[i] < 0 ||
                    mapCells[i] > MapperFreeThresholdPercent || _revoked.Contains(WorldKey(i))) continue;
                cells[i] = 0;
                _reused.Add(i);
            }
            return true;
        }

        private bool PrepareGeometry(MapGridGeometry next, out string error)
        {
            error = null;
            if (_geometry == null)
            {
                _anchorX = next.origin.x;
                _anchorY = next.origin.y;
                _geometry = Copy(next);
                return true;
            }
            if (_geometry.width == next.width && _geometry.height == next.height &&
                _geometry.origin.x == next.origin.x && _geometry.origin.y == next.origin.y &&
                _geometry.resolution == next.resolution && _geometry.frame == next.frame) return true;

            double dx = ((double)next.origin.x - _anchorX) / next.resolution;
            double dy = ((double)next.origin.y - _anchorY) / next.resolution;
            double x = Math.Round(dx), y = Math.Round(dy);
            bool aligned = next.frame == _geometry.frame && next.resolution == _geometry.resolution &&
                Math.Abs(dx - x) <= .001 && Math.Abs(dy - y) <= .001;
            if (!aligned)
            {
                // Different coordinate lattice: this is a new map generation.
                Reset();
                _anchorX = next.origin.x;
                _anchorY = next.origin.y;
                _geometry = Copy(next);
                return true;
            }
            if (x < int.MinValue || x > (double)int.MaxValue - next.width ||
                y < int.MinValue || y > (double)int.MaxValue - next.height)
                return Fail("Known-free map offset exceeds the supported lattice.", out error);

            // Store vetoes in the original lattice, including cells outside the
            // current viewport. A crop out and back cannot resurrect old free.
            ClearPresentation();
            _offsetX = (int)x;
            _offsetY = (int)y;
            _geometry = Copy(next);
            return true;
        }

        private long WorldKey(int index)
        {
            int x = _offsetX + index % _geometry.width;
            int y = _offsetY + index / _geometry.width;
            return ((long)x << 32) | (uint)y;
        }

        private static bool ValidCells(int[] cells, long count)
        {
            if (cells == null || cells.LongLength != count) return false;
            foreach (int value in cells) if (value < -1 || value > 100) return false;
            return true;
        }

        private static MapGridGeometry Copy(MapGridGeometry geometry) => new MapGridGeometry {
            width = geometry.width, height = geometry.height, resolution = geometry.resolution,
            origin = geometry.origin, frame = geometry.frame
        };

        private static bool Fail(string message, out string error)
        { error = message; return false; }
    }
}
