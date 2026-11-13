using System;
using UnityEngine;

namespace PuppyPi
{
    // A current-observation layer, never a blanket exemption for the robot's
    // starting footprint. The caller supplies timestamp-matched map pose and
    // verified sensor-to-base calibration, and owns observation freshness.
    public static class MapLaserLayer
    {
        private const int MaximumCells = 1024 * 1024;
        private const int MaximumRays = 2048;
        private const int MaximumSamples = 4 * 1024 * 1024;

        // Coordinates come from the mapper, but its accumulated occupancy is
        // deliberately absent from this API. Each scan creates a fresh local
        // observation: missing returns stay unknown rather than resurrecting
        // old obstacles or asserting that unseen space is free.
        public static bool TryBuildCurrent(MapGridGeometry geometry, Vector3 matchedPose,
            Vector3 laserExtrinsic, float[] ranges, float angleMin, float angleIncrement,
            float minRange, float maxRange, out int[] cells, out string error)
        {
            cells = null;
            if (!ValidateObservation(geometry, matchedPose, laserExtrinsic, ranges, angleMin,
                angleIncrement, minRange, maxRange, out error)) return false;
            var current = new int[geometry.width * geometry.height];
            for (int i = 0; i < current.Length; i++) current[i] = -1;
            return Build(geometry, current, matchedPose, laserExtrinsic, ranges, angleMin,
                angleIncrement, minRange, maxRange, true, out cells, out error);
        }

        public static bool TryApply(MapGridGeometry geometry, int[] baseCells, Vector3 matchedPose,
            Vector3 laserExtrinsic, float[] ranges, float angleMin, float angleIncrement,
            float minRange, float maxRange, out int[] cells, out string error)
        {
            cells = null;
            if (!ValidateObservation(geometry, matchedPose, laserExtrinsic, ranges, angleMin,
                angleIncrement, minRange, maxRange, out error)) return false;
            if (baseCells == null || baseCells.Length != (long)geometry.width * geometry.height)
                return Fail("Invalid laser-layer grid.", out error);
            foreach (int value in baseCells)
                if (value < -1 || value > 100) return Fail("Invalid base occupancy value.", out error);
            return Build(geometry, (int[])baseCells.Clone(), matchedPose, laserExtrinsic, ranges,
                angleMin, angleIncrement, minRange, maxRange, false, out cells, out error);
        }

        private static bool ValidateObservation(MapGridGeometry geometry, Vector3 matchedPose,
            Vector3 laserExtrinsic, float[] ranges, float angleMin, float angleIncrement,
            float minRange, float maxRange, out string error)
        {
            error = null;
            if (geometry == null || !geometry.IsValid || (long)geometry.width * geometry.height > MaximumCells)
                return Fail("Invalid laser-layer grid.", out error);
            if (!Finite(matchedPose) || !Finite(laserExtrinsic) || ranges == null || ranges.Length == 0 ||
                ranges.Length > MaximumRays || !Finite(angleMin) || !Finite(angleIncrement) || angleIncrement == 0 ||
                !Finite(minRange) || !Finite(maxRange) || minRange <= 0 || maxRange <= minRange)
                return Fail("Invalid laser observation or calibration.", out error);
            return true;
        }

        private static bool Build(MapGridGeometry geometry, int[] output, Vector3 matchedPose,
            Vector3 laserExtrinsic, float[] ranges, float angleMin, float angleIncrement,
            float minRange, float maxRange, bool fillAdjacentRays, out int[] cells, out string error)
        {
            cells = null;
            error = null;
            double c = Math.Cos(matchedPose.z), s = Math.Sin(matchedPose.z);
            double sensorX = matchedPose.x + c * laserExtrinsic.x - s * laserExtrinsic.y;
            double sensorY = matchedPose.y + s * laserExtrinsic.x + c * laserExtrinsic.y;
            double sensorYaw = (double)matchedPose.z + laserExtrinsic.z;
            double ax = (sensorX - geometry.origin.x) / geometry.resolution;
            double ay = (sensorY - geometry.origin.y) / geometry.resolution;
            double margin = Math.Max(2.0 * geometry.resolution, 0.10);
            var endpoints = new int[ranges.Length];
            int endpointCount = 0, validRays = 0, samples = 0;
            for (int i = 0; i < ranges.Length; i++)
            {
                float range = ranges[i];
                // Missing/non-returning beams provide no free-space evidence.
                if (!Finite(range) || range < minRange || range > maxRange) continue;
                validRays++;
                double angle = sensorYaw + angleMin + (double)i * angleIncrement;
                double dx = Math.Cos(angle), dy = Math.Sin(angle);
                double endX = (sensorX + range * dx - geometry.origin.x) / geometry.resolution;
                double endY = (sensorY + range * dy - geometry.origin.y) / geometry.resolution;
                if (TryIndex(geometry, endX, endY, out int endpoint)) endpoints[endpointCount++] = endpoint;
                double freeRange = range - margin;
                if (freeRange <= 0) continue;
                double bx = (sensorX + freeRange * dx - geometry.origin.x) / geometry.resolution;
                double by = (sensorY + freeRange * dy - geometry.origin.y) / geometry.resolution;
                if (!ClipToGrid(geometry, ax, ay, bx, by, out double fromX, out double fromY,
                    out double toX, out double toY)) continue;
                double length = Math.Sqrt((toX - fromX) * (toX - fromX) + (toY - fromY) * (toY - fromY));
                if (double.IsNaN(length) || double.IsInfinity(length) || length * 2 + 2 > MaximumSamples - samples)
                    return Fail("Laser observation exceeds the bounded ray-work budget.", out error);
                int steps = Math.Max(1, (int)Math.Ceiling(length * 2));
                samples += steps + 1;
                // At most half a cell per sample. Cells not actually sampled
                // retain their base value; there is no dilation or gap filling.
                for (int step = 0; step <= steps; step++)
                {
                    double t = step / (double)steps;
                    if (TryIndex(geometry, fromX + t * (toX - fromX), fromY + t * (toY - fromY), out int index))
                        output[index] = 0;
                }
            }
            if (validRays == 0) return Fail("The laser observation has no valid measured rays.", out error);
            if (fillAdjacentRays && Math.Abs(angleIncrement) <= Math.PI / 90)
            {
                for (int i = 1; i < ranges.Length; i++)
                    if (!FillFan(geometry, output, ax, ay, sensorYaw + angleMin + (double)(i - 1) * angleIncrement,
                        angleIncrement, ranges[i - 1], ranges[i], minRange, maxRange, margin, ref samples))
                        return Fail("Laser observation exceeds the bounded fan-work budget.", out error);
                // A full 360-degree scan also observes the final/first seam.
                // Partial scans must never close their blind sector this way.
                double turn = 2 * Math.PI;
                if (ranges.Length > 2 && Math.Abs(Math.Abs((double)angleIncrement) * ranges.Length - turn) <=
                    Math.Max(.0001, Math.Abs(angleIncrement) * .05))
                    if (!FillFan(geometry, output, ax, ay,
                        sensorYaw + angleMin + (double)(ranges.Length - 1) * angleIncrement,
                        Math.Sign(angleIncrement) * turn - (double)(ranges.Length - 1) * angleIncrement,
                        ranges[ranges.Length - 1], ranges[0], minRange, maxRange, margin, ref samples))
                        return Fail("Laser observation exceeds the bounded fan-work budget.", out error);
            }
            // Resolve crossing rays conservatively: a current endpoint is never
            // erased by another beam's free traversal in the same observation.
            for (int i = 0; i < endpointCount; i++) output[endpoints[i]] = 100;
            cells = output;
            return true;
        }

        private static bool FillFan(MapGridGeometry grid, int[] cells, double ax, double ay,
            double angle, double increment, float firstRange, float secondRange,
            float minRange, float maxRange, double margin, ref int work)
        {
            if (!Measured(firstRange, minRange, maxRange) || !Measured(secondRange, minRange, maxRange)) return true;
            double minimum = Math.Min(firstRange, secondRange);
            // Do not bridge the edge between a near object and a distant wall.
            if (Math.Abs(firstRange - secondRange) > Math.Max(.15, minimum * .10)) return true;
            double radius = (minimum - margin) / grid.resolution;
            if (radius <= 0) return true;
            double bx = ax + radius * Math.Cos(angle), by = ay + radius * Math.Sin(angle);
            double cx = ax + radius * Math.Cos(angle + increment), cy = ay + radius * Math.Sin(angle + increment);
            double area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
            if (Math.Abs(area) < 1e-9) return true;
            double sign = area > 0 ? 1 : -1;
            double minimumX = Math.Max(0, Math.Min(ax, Math.Min(bx, cx)));
            double maximumX = Math.Min(grid.width - 1.0, Math.Max(ax, Math.Max(bx, cx)) - 1);
            double minimumY = Math.Max(0, Math.Min(ay, Math.Min(by, cy)));
            double maximumY = Math.Min(grid.height - 1.0, Math.Max(ay, Math.Max(by, cy)) - 1);
            if (minimumX > maximumX || minimumY > maximumY) return true;
            int firstY = (int)Math.Ceiling(minimumY - 1e-9), lastY = (int)Math.Floor(maximumY + 1e-9);
            for (int y = firstY; y <= lastY; y++)
            {
                if (++work > MaximumSamples) return false;
                double low = minimumX, high = maximumX;
                // Each half-plane is shrunk by the minimum over all four cell
                // corners. The result frees a cell only when the WHOLE cell is
                // inside this observed triangle, never just its centre.
                if (!FanEdge(ax, ay, bx, by, sign, y, ref low, ref high) ||
                    !FanEdge(bx, by, cx, cy, sign, y, ref low, ref high) ||
                    !FanEdge(cx, cy, ax, ay, sign, y, ref low, ref high)) continue;
                int from = (int)Math.Ceiling(low - 1e-9), to = (int)Math.Floor(high + 1e-9);
                if (from > to) continue;
                int count = to - from + 1;
                if (count > MaximumSamples - work) return false;
                work += count;
                for (int x = from; x <= to; x++) cells[y * grid.width + x] = 0;
            }
            return true;
        }

        private static bool FanEdge(double ax, double ay, double bx, double by, double sign,
            int row, ref double low, ref double high)
        {
            double nx = -(by - ay) * sign, ny = (bx - ax) * sign;
            double offset = -nx * ax - ny * ay;
            double minimum = ny * row + offset + Math.Min(0, nx) + Math.Min(0, ny);
            if (Math.Abs(nx) < 1e-12) return minimum >= -1e-9;
            double edge = -minimum / nx;
            if (nx > 0) low = Math.Max(low, edge);
            else high = Math.Min(high, edge);
            return low <= high + 1e-9;
        }

        private static bool Measured(float value, float min, float max) => Finite(value) && value >= min && value <= max;

        private static bool ClipToGrid(MapGridGeometry grid, double ax, double ay, double bx, double by,
            out double fromX, out double fromY, out double toX, out double toY)
        {
            double first = 0, last = 1, dx = bx - ax, dy = by - ay;
            fromX = fromY = toX = toY = 0;
            if (!Clip(-dx, ax, ref first, ref last) || !Clip(dx, grid.width - ax, ref first, ref last) ||
                !Clip(-dy, ay, ref first, ref last) || !Clip(dy, grid.height - ay, ref first, ref last)) return false;
            fromX = ax + first * dx;
            fromY = ay + first * dy;
            toX = ax + last * dx;
            toY = ay + last * dy;
            return true;
        }

        private static bool Clip(double direction, double distance, ref double first, ref double last)
        {
            if (direction == 0) return distance >= 0;
            double ratio = distance / direction;
            if (direction < 0) { if (ratio > last) return false; first = Math.Max(first, ratio); }
            else { if (ratio < first) return false; last = Math.Min(last, ratio); }
            return true;
        }

        private static bool TryIndex(MapGridGeometry grid, double x, double y, out int index)
        {
            index = -1;
            if (!(x >= 0 && y >= 0 && x < grid.width && y < grid.height)) return false;
            index = (int)Math.Floor(y) * grid.width + (int)Math.Floor(x);
            return true;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static bool Fail(string message, out string error) { error = message; return false; }
    }
}
