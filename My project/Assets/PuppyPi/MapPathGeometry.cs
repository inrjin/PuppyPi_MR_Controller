using System;
using System.Collections.Generic;
using UnityEngine;

namespace PuppyPi
{
    [Serializable]
    public sealed class MapGridGeometry
    {
        public int width;
        public int height;
        public float resolution;
        public Vector2 origin;
        public string frame = "map";

        public bool IsValid => width > 0 && height > 0 && (long)width * height <= int.MaxValue &&
            Finite(resolution) && resolution > 0 && Finite(origin.x) && Finite(origin.y) &&
            Finite(width * resolution) && Finite(height * resolution) &&
            Finite(origin.x + width * resolution) && Finite(origin.y + height * resolution) &&
            !string.IsNullOrWhiteSpace(frame);

        // Unity texture UV starts at the bottom left. UV 1 is the outer grid
        // boundary, not the center of its final cell. Collision checks use the
        // half-open cell domain [origin, origin + size) and never clamp points.
        public bool TryUvToMeters(Vector2 uv, out Vector2 meters)
        {
            meters = default;
            if (!IsValid || !Finite(uv.x) || !Finite(uv.y) || uv.x < 0 || uv.x > 1 || uv.y < 0 || uv.y > 1)
                return false;
            meters = origin + new Vector2(uv.x * width * resolution, uv.y * height * resolution);
            return Finite(meters.x) && Finite(meters.y);
        }

        public Vector2 MetersToUv(Vector2 meters)
        {
            if (!IsValid || !Finite(meters.x) || !Finite(meters.y)) return new Vector2(float.NaN, float.NaN);
            return new Vector2((meters.x - origin.x) / (width * resolution),
                (meters.y - origin.y) / (height * resolution));
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    [Serializable]
    public sealed class MapRobotFootprint
    {
        // Nominal Hiwonder PuppyPi length/width: 226 x 149 mm
        // (https://www.hiwonder.com/products/puppypi). The added allowance is
        // our configurable walking clearance, not a vendor value.
        public float lengthMeters = .226f;
        public float widthMeters = .149f;
        public float marginMeters = .05f;
        public float LengthMeters => lengthMeters;
        public float WidthMeters => widthMeters;
        public float MarginMeters => marginMeters;
        public float HalfLength => lengthMeters * .5f + marginMeters;
        public float HalfWidth => widthMeters * .5f + marginMeters;
        public float RotationRadius => (float)Math.Sqrt((double)HalfLength * HalfLength + (double)HalfWidth * HalfWidth);
        public bool IsValid => Finite(lengthMeters) && lengthMeters > 0 && Finite(widthMeters) && widthMeters > 0 &&
            Finite(marginMeters) && marginMeters >= 0 && Finite(HalfLength) && Finite(HalfWidth) && Finite(RotationRadius);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    public enum MapPathBlockKind { Invalid, Boundary, Unknown, Occupied }

    [Serializable]
    public sealed class MapPathBlock
    {
        public MapPathBlockKind Kind;
        // path[i] -> path[i + 1]; a single-point disk uses index zero.
        public int SegmentIndex;
        public Vector2 Position;
        // Meaningful for Unknown/Occupied. Other kinds have no source cell.
        public int CellValue;
    }

    public static class MapPathGeometry
    {
        // Match PathFollower.arrive_m. Apply only at an execution boundary,
        // never to individual collision/search edges or later route corners.
        public const float ExecutionArrivalMeters = .10f;
        private const int MaximumRawPoints = 8192;
        private const int MaximumSmoothingNeighbors = 128;
        private const int OccupiedThreshold = 50;

        public static int ReachedExecutionPrefix(IReadOnlyList<Vector2> points, Vector2 robot, int startIndex = 0)
        {
            if (startIndex < 0 || startIndex > (points?.Count ?? 0))
                throw new ArgumentOutOfRangeException(nameof(startIndex));
            int end = startIndex;
            while (points != null && end < points.Count &&
                Vector2.Distance(robot, points[end]) <= ExecutionArrivalMeters) end++;
            return end - startIndex;
        }

        // The follower consumes reached initial waypoints before choosing its
        // heading. Reanchor that same remaining route at the current pose so
        // localization jitter cannot create an unexecuted backwards connector.
        // Stop at the FIRST unreached point; later returns/corners stay intact.
        public static Vector2[] NormalizeExecutionPrefix(IReadOnlyList<Vector2> points, Vector2 robot,
            out int consumedPoints, int startIndex = 0)
        {
            consumedPoints = ReachedExecutionPrefix(points, robot, startIndex);
            int remaining = (points?.Count ?? 0) - startIndex - consumedPoints;
            var route = new Vector2[remaining + 1];
            route[0] = robot;
            for (int i = 0; i < remaining; i++) route[i + 1] = points[startIndex + consumedPoints + i];
            return route;
        }

        // Product policy: an open stroke is straight unless it contains a
        // persistent, localized direction change. Each resulting run is fitted
        // as one line; adjacent lines meet at a sharp corner. toleranceMeters is
        // the noise/context scale, NOT a maximum distance from the original ink.
        public static bool TryPrepare(IReadOnlyList<Vector2> raw, out Vector2[] waypoints,
            out string error, int maxPoints = 20, float toleranceMeters = 0.04f)
        {
            waypoints = Array.Empty<Vector2>();
            error = null;
            if (raw == null || raw.Count < 2) return Fail("Draw at least two different points.", out error);
            if (raw.Count > MaximumRawPoints) return Fail("The stroke exceeds the sample budget; draw a shorter stroke.", out error);
            if (maxPoints < 2 || !Finite(toleranceMeters) || toleranceMeters < 0)
                return Fail("Invalid path straightening settings.", out error);
            var points = new List<Vector2>(raw.Count);
            for (int i = 0; i < raw.Count; i++)
            {
                Vector2 point = raw[i];
                if (!Finite(point.x) || !Finite(point.y)) return Fail("The path contains a non-finite coordinate.", out error);
                if (points.Count == 0 || !ExactlyEqual(point, points[points.Count - 1])) points.Add(point);
            }
            if (points.Count < 2) return Fail("The stroke has no movement.", out error);
            var forced = new bool[points.Count];
            forced[0] = forced[forced.Length - 1] = true;
            ProtectReturns(points, forced, toleranceMeters);
            List<Vector2> pilot = BuildPilot(points, toleranceMeters);
            double[] arc = ArcDistances(pilot);
            if (IsStraightCorridor(points, pilot, arc, forced, toleranceMeters))
            { waypoints = new[] { points[0], points[points.Count - 1] }; return true; }
            var corners = (bool[])forced.Clone();
            double context = Math.Max(.24, Math.Min(.32, 3.0 * toleranceMeters));
            FindCorners(points, pilot, arc, corners, context);
            double rawLength = 0;
            for (int i = 1; i < points.Count; i++) rawLength += Math.Sqrt(DistanceSquared(points[i - 1], points[i]));
            if (rawLength <= arc[arc.Length - 1] * 1.10) FindExactRunCorners(points, corners, context);
            var indices = new List<int>();
            for (int i = 0; i < corners.Length; i++) if (corners[i]) indices.Add(i);
            // Gentle waviness does not create runs. An otherwise unsplit broad
            // arc can get one fitted bend; loops and reversals keep supports.
            MergeWeakCorners(pilot, arc, indices, forced, context);
            if (indices.Count == 2)
            {
                Array.Clear(corners, 0, corners.Length);
                corners[0] = corners[corners.Length - 1] = true;
                AddBroadBends(pilot, arc, corners, context);
                indices.Clear();
                for (int i = 0; i < corners.Length; i++) if (corners[i]) indices.Add(i);
            }
            if (indices.Count > maxPoints)
                return Fail("The route has " + indices.Count + " deliberate corner/end points; the limit is " + maxPoints + ". Use fewer turns.", out error);
            var lines = new FittedLine[indices.Count - 1];
            for (int i = 0; i < lines.Length; i++)
                lines[i] = FitLine(pilot, arc, indices[i], indices[i + 1], context);
            if (lines.Length == 1)
            {
                waypoints = new[] { points[0], points[points.Count - 1] };
                return true;
            }
            // Preserve the actual requested endpoints, including noisy ones.
            lines[0].x = points[0].x; lines[0].y = points[0].y;
            lines[lines.Length - 1].x = points[points.Count - 1].x;
            lines[lines.Length - 1].y = points[points.Count - 1].y;
            var fitted = new Vector2[indices.Count];
            fitted[0] = points[0]; fitted[fitted.Length - 1] = points[points.Count - 1];
            for (int i = 1; i < fitted.Length - 1; i++)
            {
                FittedLine a = lines[i - 1], b = lines[i];
                double cross = a.dx * b.dy - a.dy * b.dx;
                Vector2 vertex;
                if (forced[indices[i]] || Math.Abs(cross) < .05)
                {
                    // Parallel opposing runs are a U-turn, not an intersection
                    // at infinity. Keep the original extremum/return support.
                    vertex = points[indices[i]];
                }
                else
                {
                    double t = ((b.x - a.x) * b.dy - (b.y - a.y) * b.dx) / cross;
                    vertex = new Vector2((float)(a.x + t * a.dx), (float)(a.y + t * a.dy));
                    double neighborhood = Math.Max(context * 3, .3);
                    if (!Finite(vertex.x) || !Finite(vertex.y) ||
                        DistanceSquared(vertex, pilot[indices[i]]) > neighborhood * neighborhood)
                        return Fail("A corner could not be fitted reliably. Draw longer, clearer straight runs.", out error);
                }
                fitted[i] = vertex;
            }
            waypoints = fitted;
            return true;
        }

        private struct FittedLine
        {
            public double x, y, dx, dy;
        }

        private static bool IsStraightCorridor(List<Vector2> raw, List<Vector2> pilot, double[] arc, bool[] forced, float noiseScale)
        {
            for (int i = 1; i < forced.Length - 1; i++) if (forced[i]) return false;
            double chordSquared = DistanceSquared(pilot[0], pilot[pilot.Count - 1]);
            if (chordSquared <= 1e-12 || arc[arc.Length - 1] <= 0 ||
                Math.Sqrt(chordSquared) < arc[arc.Length - 1] * .60) return false;
            double rawLength = 0;
            for (int i = 1; i < raw.Count; i++) rawLength += Math.Sqrt(DistanceSquared(raw[i - 1], raw[i]));
            // This shortcut is only for demonstrable high-frequency roughness,
            // not a narrow zigzag composed of genuine, stable straight legs.
            if (rawLength < arc[arc.Length - 1] * 1.20) return false;
            double width = Math.Max(.32, noiseScale * 3.0);
            double corridor = Math.Min(chordSquared * .0144, width * width);
            foreach (Vector2 point in pilot)
                if (PointSegmentDistanceSquared(point, pilot[0], pilot[pilot.Count - 1]) > corridor) return false;
            return true;
        }

        private static List<Vector2> BuildPilot(List<Vector2> points, float noiseScale)
        {
            double minX = points[0].x, maxX = minX, minY = points[0].y, maxY = minY;
            foreach (Vector2 point in points)
            {
                minX = Math.Min(minX, point.x); maxX = Math.Max(maxX, point.x);
                minY = Math.Min(minY, point.y); maxY = Math.Max(maxY, point.y);
            }
            double extent = Math.Max(.001, Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY)));
            double sigma = Math.Max(.15, Math.Min(64, Math.Max(.025, Math.Min(.06, noiseScale * .6)) * points.Count / extent));
            int radius = Math.Min(MaximumSmoothingNeighbors, (int)Math.Ceiling(3 * sigma));
            var pilot = new List<Vector2>(points.Count);
            for (int i = 0; i < points.Count; i++)
            {
                if (i == 0 || i == points.Count - 1) { pilot.Add(points[i]); continue; }
                double x = 0, y = 0, total = 0;
                for (int j = Math.Max(0, i - radius); j <= Math.Min(points.Count - 1, i + radius); j++)
                {
                    double delta = (j - i) / sigma, weight = Math.Exp(-.5 * delta * delta);
                    x += points[j].x * weight; y += points[j].y * weight; total += weight;
                }
                pilot.Add(new Vector2((float)(x / total), (float)(y / total)));
            }
            return pilot;
        }

        private static void FindCorners(List<Vector2> points, List<Vector2> pilot, double[] arc, bool[] corners, double context)
        {
            int candidate = -1;
            double strongest = 0;
            var candidates = new List<(int index, double strength)>();
            for (int i = 1; i < pilot.Count - 1; i++)
            {
                int left = ArcIndex(arc, arc[i] - context, false), right = ArcIndex(arc, arc[i] + context, true);
                int nearLeft = ArcIndex(arc, arc[i] - context * .35, false);
                int nearRight = ArcIndex(arc, arc[i] + context * .35, true);
                double broad = TurnScore(pilot[left], pilot[i], pilot[right]);
                double local = TurnScore(pilot[nearLeft], pilot[i], pilot[nearRight]);
                bool enoughRun = arc[i] >= context * .75 && arc[arc.Length - 1] - arc[i] >= context * .75 &&
                    DistanceSquared(pilot[left], pilot[i]) >= context * context * .16 &&
                    DistanceSquared(pilot[right], pilot[i]) >= context * context * .16;
                bool sharp = enoughRun && broad >= .18 && local >= .095 && local >= broad * .55;
                if (sharp)
                {
                    double strength = local + broad;
                    if (candidate >= 0 && DistanceSquared(pilot[candidate], pilot[i]) >= context * context * .64)
                    {
                        candidates.Add((ReversalExtremum(points, pilot, arc, candidate, context), strongest));
                        candidate = -1; strongest = 0;
                    }
                    if (candidate < 0 || strength > strongest) { candidate = i; strongest = strength; }
                }
                else if (candidate >= 0)
                {
                    candidates.Add((ReversalExtremum(points, pilot, arc, candidate, context), strongest));
                    candidate = -1; strongest = 0;
                }
            }
            if (candidate >= 0) candidates.Add((ReversalExtremum(points, pilot, arc, candidate, context), strongest));
            candidates.Sort((a, b) => b.strength.CompareTo(a.strength));
            foreach (var item in candidates)
            {
                if (DistanceSquared(pilot[item.index], pilot[0]) < context * context * .36 ||
                    DistanceSquared(pilot[item.index], pilot[pilot.Count - 1]) < context * context * .36) continue;
                bool nearby = false;
                for (int i = 1; i < corners.Length - 1; i++)
                    if (corners[i] && DistanceSquared(pilot[i], pilot[item.index]) < context * context)
                    { nearby = true; break; }
                if (!nearby) corners[item.index] = true;
            }
        }

        private static void AddBroadBends(List<Vector2> pilot, double[] arc, bool[] corners, double context)
        {
            // Only an otherwise unsplit stroke gets one broad bend. Splitting
            // every existing run again would follow its residual hand waviness.
            int count = 0;
            foreach (bool corner in corners) if (corner) count++;
            if (count > 2) return;
            int first = 0;
            var additions = new List<int>();
            for (int last = 1; last < pilot.Count; last++)
            {
                if (!corners[last]) continue;
                double chord = Math.Sqrt(DistanceSquared(pilot[first], pilot[last]));
                if (chord > context * 2)
                {
                    int split = -1;
                    double furthest = Math.Max(context * context * .16, chord * chord * .0144);
                    for (int i = first + 1; i < last; i++)
                    {
                        if (Math.Sqrt(DistanceSquared(pilot[first], pilot[i])) < context ||
                            Math.Sqrt(DistanceSquared(pilot[last], pilot[i])) < context) continue;
                        double value = PointSegmentDistanceSquared(pilot[i], pilot[first], pilot[last]);
                        if (value > furthest) { furthest = value; split = i; }
                    }
                    if (split >= 0)
                    {
                        FittedLine a = FitLine(pilot, arc, first, split, context), b = FitLine(pilot, arc, split, last, context);
                        if (a.dx * b.dx + a.dy * b.dy < .82) additions.Add(split);
                    }
                }
                first = last;
            }
            foreach (int index in additions) corners[index] = true;
        }

        private static int ReversalExtremum(List<Vector2> points, List<Vector2> pilot, double[] arc, int index, double context)
        {
            int left = ArcIndex(arc, arc[index] - context, false), right = ArcIndex(arc, arc[index] + context, true);
            if (TurnScore(pilot[left], pilot[index], pilot[right]) < 1.8) return index;
            double dx = (double)pilot[index].x - pilot[left].x, dy = (double)pilot[index].y - pilot[left].y;
            double furthest = double.NegativeInfinity;
            int selected = index;
            for (int i = left; i <= right; i++)
            {
                double distance = ((double)points[i].x - pilot[left].x) * dx + ((double)points[i].y - pilot[left].y) * dy;
                if (distance > furthest) { furthest = distance; selected = i; }
            }
            return selected;
        }

        private static void FindExactRunCorners(List<Vector2> points, bool[] corners, double context)
        {
            // Preserve densely sampled clean polygon vertices even when the
            // noise pilot rounds them. Multiple persistent edges on both sides
            // distinguish an actual corner from a one-sample hand spike.
            for (int i = 2; i < points.Count - 2; i++)
            {
                if (TurnScore(points[i - 1], points[i], points[i + 1]) < .18) continue;
                double ax = (double)points[i].x - points[i - 1].x, ay = (double)points[i].y - points[i - 1].y;
                double bx = (double)points[i + 1].x - points[i].x, by = (double)points[i + 1].y - points[i].y;
                double aLength = Math.Sqrt(ax * ax + ay * ay), bLength = Math.Sqrt(bx * bx + by * by);
                int left = i - 1, right = i + 1;
                while (left > 0 && AlignedEdge(points[left - 1], points[left], ax, ay, aLength)) left--;
                while (right < points.Count - 1 && AlignedEdge(points[right], points[right + 1], bx, by, bLength)) right++;
                if (i - left >= 2 && right - i >= 2 &&
                    DistanceSquared(points[left], points[i]) >= context * context * .25 &&
                    DistanceSquared(points[right], points[i]) >= context * context * .25) corners[i] = true;
            }
        }

        private static bool AlignedEdge(Vector2 a, Vector2 b, double x, double y, double length)
        {
            double dx = (double)b.x - a.x, dy = (double)b.y - a.y;
            double edge = Math.Sqrt(dx * dx + dy * dy);
            return length > 0 && edge > 0 && (dx * x + dy * y) / (edge * length) > .996;
        }

        private static double TurnScore(Vector2 a, Vector2 p, Vector2 b)
        {
            double ax = (double)p.x - a.x, ay = (double)p.y - a.y, bx = (double)b.x - p.x, by = (double)b.y - p.y;
            double length = Math.Sqrt((ax * ax + ay * ay) * (bx * bx + by * by));
            return length > 1e-12 ? 1 - (ax * bx + ay * by) / length : 0;
        }

        private static void MergeWeakCorners(List<Vector2> pilot, double[] arc, List<int> indices, bool[] forced, double context)
        {
            bool changed;
            do
            {
                changed = false;
                for (int i = 1; i < indices.Count - 1; i++)
                {
                    if (forced[indices[i]]) continue;
                    FittedLine a = FitLine(pilot, arc, indices[i - 1], indices[i], context);
                    FittedLine b = FitLine(pilot, arc, indices[i], indices[i + 1], context);
                    if (a.dx * b.dx + a.dy * b.dy > .86 ||
                        Math.Min(DistanceSquared(pilot[indices[i]], pilot[indices[i - 1]]),
                            DistanceSquared(pilot[indices[i + 1]], pilot[indices[i]])) < context * context * .64)
                    { indices.RemoveAt(i); changed = true; break; }
                }
            } while (changed);
        }

        private static FittedLine FitLine(List<Vector2> points, double[] arc, int first, int last, double context)
        {
            double trim = Math.Min(context * .4, (arc[last] - arc[first]) * .2);
            int from = Math.Max(first, ArcIndex(arc, arc[first] + trim, true));
            int to = Math.Min(last, ArcIndex(arc, arc[last] - trim, false));
            if (to <= from) { from = first; to = last; }
            var weights = new double[to - from + 1];
            for (int i = 0; i < weights.Length; i++) weights[i] = 1;
            FittedLine line = default;
            for (int pass = 0; pass < 3; pass++)
            {
                double x = 0, y = 0, total = 0;
                for (int i = from; i <= to; i++) { double w = weights[i - from]; x += points[i].x * w; y += points[i].y * w; total += w; }
                x /= total; y /= total;
                double xx = 0, xy = 0, yy = 0;
                for (int i = from; i <= to; i++)
                {
                    double dx = points[i].x - x, dy = points[i].y - y, w = weights[i - from];
                    xx += w * dx * dx; xy += w * dx * dy; yy += w * dy * dy;
                }
                double angle = .5 * Math.Atan2(2 * xy, xx - yy), ux = Math.Cos(angle), uy = Math.Sin(angle);
                if (ux * (points[last].x - points[first].x) + uy * (points[last].y - points[first].y) < 0) { ux = -ux; uy = -uy; }
                line = new FittedLine { x = x, y = y, dx = ux, dy = uy };
                var residuals = new double[weights.Length];
                for (int i = from; i <= to; i++) residuals[i - from] = Math.Abs((points[i].x - x) * uy - (points[i].y - y) * ux);
                var sorted = (double[])residuals.Clone(); Array.Sort(sorted);
                double scale = Math.Max(.001, sorted[sorted.Length / 2] * 1.4826 * 2.5);
                for (int i = 0; i < weights.Length; i++) weights[i] = residuals[i] <= scale ? 1 : scale / residuals[i];
            }
            return line;
        }

        private static double[] ArcDistances(List<Vector2> points)
        {
            var arc = new double[points.Count];
            for (int i = 1; i < points.Count; i++) arc[i] = arc[i - 1] + Math.Sqrt(DistanceSquared(points[i - 1], points[i]));
            return arc;
        }

        private static int ArcIndex(double[] arc, double value, bool ceiling)
        {
            int left = 0, right = arc.Length - 1;
            while (left < right)
            {
                int middle = left + (right - left) / 2;
                if (arc[middle] < value) left = middle + 1;
                else right = middle;
            }
            return !ceiling && arc[left] > value && left > 0 ? left - 1 : left;
        }

        private static void ProtectReturns(List<Vector2> points, bool[] keep, float tolerance)
        {
            double closure = Math.Max(tolerance, 0.001);
            if (DistanceSquared(points[0], points[points.Count - 1]) <= closure * closure)
                ProtectLoop(points, keep, 0, points.Count - 1);
            var visits = new Dictionary<Vector2, int>();
            for (int i = 0; i < points.Count; i++)
            {
                if (visits.TryGetValue(points[i], out int first) && i - first > 1)
                {
                    double excursion = 0;
                    for (int j = first + 1; j < i; j++) excursion = Math.Max(excursion, DistanceSquared(points[first], points[j]));
                    if (excursion >= closure * closure) ProtectLoop(points, keep, first, i);
                }
                else visits[points[i]] = i;
            }
        }

        private static void ProtectLoop(List<Vector2> points, bool[] keep, int first, int last)
        {
            if (last - first < 2) return;
            int farthest = first;
            double distance = 0;
            for (int i = first + 1; i < last; i++)
            {
                double value = DistanceSquared(points[first], points[i]);
                if (value > distance) { distance = value; farthest = i; }
            }
            if (farthest == first) return;
            int side = first;
            double sideDistance = 0;
            int opposite = first;
            double lowCross = 0, highCross = 0;
            for (int i = first + 1; i < last; i++)
            {
                double dx = (double)points[farthest].x - points[first].x;
                double dy = (double)points[farthest].y - points[first].y;
                double cross = ((double)points[i].x - points[first].x) * dy - ((double)points[i].y - points[first].y) * dx;
                if (cross < lowCross) { lowCross = cross; side = i; }
                if (cross > highCross) { highCross = cross; opposite = i; }
                double value = cross * cross / distance;
                if (value > sideDistance) sideDistance = value;
            }
            keep[farthest] = true;
            if (sideDistance > Math.Max(1e-16, distance * 1e-12))
            { keep[first] = keep[last] = true; if (lowCross < 0) keep[side] = true; if (highCross > 0) keep[opposite] = true; }
        }

        // The rectangle follows each straight leg. A turn is checked with its
        // circumscribed disk because the robot rotates in place before driving.
        // The final endpoint does not imply another turn.
        public static bool TryFindFootprintBlock(MapGridGeometry geometry, int[] occupancy,
            IReadOnlyList<Vector2> path, float initialYawRadians, MapRobotFootprint footprint, out MapPathBlock block)
        {
            block = null;
            if (geometry == null || !geometry.IsValid || occupancy == null ||
                occupancy.Length != (long)geometry.width * geometry.height || path == null || path.Count == 0 ||
                !Finite(initialYawRadians) || footprint == null || !footprint.IsValid)
                return Block(MapPathBlockKind.Invalid, -1, Vector2.zero, 0, out block);
            foreach (Vector2 point in path)
                if (!Finite(point.x) || !Finite(point.y))
                    return Block(MapPathBlockKind.Invalid, -1, Vector2.zero, 0, out block);

            double headingX = Math.Cos(initialYawRadians), headingY = Math.Sin(initialYawRadians);
            if (FindSweptRectangleBlock(geometry, occupancy, path[0], path[0], headingX, headingY,
                footprint, 0, out block)) return true;
            for (int i = 0; i < path.Count - 1; i++)
            {
                Vector2 start = path[i], end = path[i + 1];
                double dx = (double)end.x - start.x, dy = (double)end.y - start.y;
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (length == 0) continue;
                double directionX = dx / length, directionY = dy / length;
                // A reversal also turns even though its cross product is zero.
                if (headingX * directionX + headingY * directionY < 1 - 1e-10 &&
                    FindSegmentBlock(geometry, occupancy, start, start, footprint.RotationRadius, i, out block)) return true;
                if (FindSweptRectangleBlock(geometry, occupancy, start, end, directionX, directionY,
                    footprint, i, out block)) return true;
                headingX = directionX;
                headingY = directionY;
            }
            return false;
        }

        private static bool FindSweptRectangleBlock(MapGridGeometry geometry, int[] occupancy,
            Vector2 start, Vector2 end, double directionX, double directionY,
            MapRobotFootprint footprint, int segmentIndex, out MapPathBlock block)
        {
            block = null;
            double resolution = geometry.resolution;
            double ax = ((double)start.x - geometry.origin.x) / resolution;
            double ay = ((double)start.y - geometry.origin.y) / resolution;
            double bx = ((double)end.x - geometry.origin.x) / resolution;
            double by = ((double)end.y - geometry.origin.y) / resolution;
            double centerX = (ax + bx) * .5, centerY = (ay + by) * .5;
            double dx = bx - ax, dy = by - ay;
            double halfLength = Math.Sqrt(dx * dx + dy * dy) * .5 + footprint.HalfLength / resolution;
            double halfWidth = footprint.HalfWidth / resolution;
            double extentX = Math.Abs(directionX) * halfLength + Math.Abs(directionY) * halfWidth;
            double extentY = Math.Abs(directionY) * halfLength + Math.Abs(directionX) * halfWidth;
            double minX = centerX - extentX, maxX = centerX + extentX;
            double minY = centerY - extentY, maxY = centerY + extentY;
            if (minX < 0 || minY < 0 || maxX >= geometry.width || maxY >= geometry.height)
            {
                double initialExtentX = Math.Abs(directionX) * footprint.HalfLength / resolution +
                    Math.Abs(directionY) * halfWidth;
                double initialExtentY = Math.Abs(directionY) * footprint.HalfLength / resolution +
                    Math.Abs(directionX) * halfWidth;
                bool startOutside = ax - initialExtentX < 0 || ay - initialExtentY < 0 ||
                    ax + initialExtentX >= geometry.width || ay + initialExtentY >= geometry.height;
                return Block(MapPathBlockKind.Boundary, segmentIndex, startOutside ? start : end, 0, out block);
            }
            int firstX = Math.Max(0, (int)Math.Floor(minX) - 1), lastX = Math.Min(geometry.width - 1, (int)Math.Floor(maxX));
            int firstY = Math.Max(0, (int)Math.Floor(minY) - 1), lastY = Math.Min(geometry.height - 1, (int)Math.Floor(maxY));
            double cellProjectedRadius = .5 * (Math.Abs(directionX) + Math.Abs(directionY));
            // Separating axis theorem: world X/Y and both rectangle axes.
            // Full cell squares are tested, including touching edges/corners.
            const double epsilon = 1e-10;
            for (int y = firstY; y <= lastY; y++)
            for (int x = firstX; x <= lastX; x++)
            {
                int value = occupancy[y * geometry.width + x];
                if (value >= 0 && value < OccupiedThreshold) continue;
                double relativeX = x + .5 - centerX, relativeY = y + .5 - centerY;
                if (Math.Abs(relativeX) > extentX + .5 + epsilon || Math.Abs(relativeY) > extentY + .5 + epsilon ||
                    Math.Abs(relativeX * directionX + relativeY * directionY) > halfLength + cellProjectedRadius + epsilon ||
                    Math.Abs(-relativeX * directionY + relativeY * directionX) > halfWidth + cellProjectedRadius + epsilon) continue;
                return Block(value < 0 ? MapPathBlockKind.Unknown : MapPathBlockKind.Occupied, segmentIndex,
                    geometry.origin + new Vector2((x + .5f) * geometry.resolution,
                        (y + .5f) * geometry.resolution), value, out block);
            }
            return false;
        }

        public static bool SegmentClear(MapGridGeometry geometry, int[] occupancy,
            Vector2 start, Vector2 end, float clearanceRadiusMeters = 0) =>
            !FindSegmentBlock(geometry, occupancy, start, end, clearanceRadiusMeters, 0, out _);

        public static bool PathClear(MapGridGeometry geometry, int[] occupancy,
            IReadOnlyList<Vector2> path, float clearanceRadiusMeters = 0) =>
            !TryFindBlock(geometry, occupancy, path, clearanceRadiusMeters, out _);

        // Returns true for every case that fails PathClear, including invalid
        // input. The first blocked segment and source cell are deterministic.
        public static bool TryFindBlock(MapGridGeometry geometry, int[] occupancy,
            IReadOnlyList<Vector2> path, float clearanceRadiusMeters, out MapPathBlock block)
        {
            block = null;
            if (path == null || path.Count == 0)
                return Block(MapPathBlockKind.Invalid, -1, Vector2.zero, 0, out block);
            if (path.Count == 1)
                return FindSegmentBlock(geometry, occupancy, path[0], path[0], clearanceRadiusMeters, 0, out block);
            for (int i = 0; i < path.Count - 1; i++)
                if (FindSegmentBlock(geometry, occupancy, path[i], path[i + 1], clearanceRadiusMeters, i, out block)) return true;
            return false;
        }

        private static bool FindSegmentBlock(MapGridGeometry geometry, int[] occupancy,
            Vector2 start, Vector2 end, float clearanceRadiusMeters, int segmentIndex, out MapPathBlock block)
        {
            block = null;
            if (geometry == null || !geometry.IsValid || occupancy == null ||
                occupancy.Length != (long)geometry.width * geometry.height ||
                !Finite(start.x) || !Finite(start.y) || !Finite(end.x) || !Finite(end.y) ||
                !Finite(clearanceRadiusMeters) || clearanceRadiusMeters < 0)
                return Block(MapPathBlockKind.Invalid, segmentIndex,
                    Finite(start.x) && Finite(start.y) ? start : Vector2.zero, 0, out block);
            double ax = ((double)start.x - geometry.origin.x) / geometry.resolution;
            double ay = ((double)start.y - geometry.origin.y) / geometry.resolution;
            double bx = ((double)end.x - geometry.origin.x) / geometry.resolution;
            double by = ((double)end.y - geometry.origin.y) / geometry.resolution;
            double radius = clearanceRadiusMeters / (double)geometry.resolution;
            double minX = Math.Min(ax, bx) - radius, maxX = Math.Max(ax, bx) + radius;
            double minY = Math.Min(ay, by) - radius, maxY = Math.Max(ay, by) + radius;
            if (minX < 0 || minY < 0 || maxX >= geometry.width || maxY >= geometry.height)
            {
                bool startOutside = ax - radius < 0 || ay - radius < 0 ||
                    ax + radius >= geometry.width || ay + radius >= geometry.height;
                return Block(MapPathBlockKind.Boundary, segmentIndex, startOutside ? start : end, 0, out block);
            }
            int firstX = Math.Max(0, (int)Math.Floor(minX) - 1), lastX = Math.Min(geometry.width - 1, (int)Math.Floor(maxX));
            int firstY = Math.Max(0, (int)Math.Floor(minY) - 1), lastY = Math.Min(geometry.height - 1, (int)Math.Floor(maxY));
            double squaredRadius = radius * radius;
            for (int y = firstY; y <= lastY; y++)
            for (int x = firstX; x <= lastX; x++)
            {
                int value = occupancy[y * geometry.width + x];
                if (value >= 0 && value < OccupiedThreshold) continue;
                if (SegmentRectangleDistanceSquared(ax, ay, bx, by, x, y, x + 1, y + 1) <= squaredRadius)
                    return Block(value < 0 ? MapPathBlockKind.Unknown : MapPathBlockKind.Occupied, segmentIndex,
                        geometry.origin + new Vector2((x + 0.5f) * geometry.resolution,
                            (y + 0.5f) * geometry.resolution), value, out block);
            }
            return false;
        }

        private static bool Block(MapPathBlockKind kind, int segmentIndex, Vector2 position,
            int value, out MapPathBlock block)
        {
            block = new MapPathBlock { Kind = kind, SegmentIndex = segmentIndex, Position = position, CellValue = value };
            return true;
        }

        private static double SegmentRectangleDistanceSquared(double ax, double ay, double bx, double by,
            double left, double bottom, double right, double top)
        {
            double t0 = 0, t1 = 1, dx = bx - ax, dy = by - ay;
            if (Clip(-dx, ax - left, ref t0, ref t1) && Clip(dx, right - ax, ref t0, ref t1) &&
                Clip(-dy, ay - bottom, ref t0, ref t1) && Clip(dy, top - ay, ref t0, ref t1)) return 0;
            double distance = Math.Min(PointRectangleDistanceSquared(ax, ay, left, bottom, right, top),
                PointRectangleDistanceSquared(bx, by, left, bottom, right, top));
            distance = Math.Min(distance, PointSegmentDistanceSquared(left, bottom, ax, ay, bx, by));
            distance = Math.Min(distance, PointSegmentDistanceSquared(left, top, ax, ay, bx, by));
            distance = Math.Min(distance, PointSegmentDistanceSquared(right, bottom, ax, ay, bx, by));
            return Math.Min(distance, PointSegmentDistanceSquared(right, top, ax, ay, bx, by));
        }

        private static bool Clip(double p, double q, ref double first, ref double last)
        {
            if (p == 0) return q >= 0;
            double ratio = q / p;
            if (p < 0) { if (ratio > last) return false; first = Math.Max(first, ratio); }
            else { if (ratio < first) return false; last = Math.Min(last, ratio); }
            return true;
        }

        private static double PointRectangleDistanceSquared(double x, double y, double left, double bottom, double right, double top)
        {
            double dx = Math.Max(left - x, Math.Max(0, x - right));
            double dy = Math.Max(bottom - y, Math.Max(0, y - top));
            return dx * dx + dy * dy;
        }

        private static double PointSegmentDistanceSquared(Vector2 p, Vector2 a, Vector2 b) =>
            PointSegmentDistanceSquared(p.x, p.y, a.x, a.y, b.x, b.y);

        private static double PointSegmentDistanceSquared(double px, double py, double ax, double ay, double bx, double by)
        {
            double dx = bx - ax, dy = by - ay, length = dx * dx + dy * dy;
            double t = length > 0 ? Math.Max(0, Math.Min(1, ((px - ax) * dx + (py - ay) * dy) / length)) : 0;
            double ex = px - ax - t * dx, ey = py - ay - t * dy;
            return ex * ex + ey * ey;
        }

        private static double DistanceSquared(Vector2 a, Vector2 b)
        {
            double x = (double)a.x - b.x, y = (double)a.y - b.y;
            return x * x + y * y;
        }

        private static bool ExactlyEqual(Vector2 a, Vector2 b) => a.x == b.x && a.y == b.y;
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Fail(string message, out string error) { error = message; return false; }
    }
}
