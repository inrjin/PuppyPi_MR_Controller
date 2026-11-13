using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace PuppyPi
{
    // Meter-space points remain unchanged when the window moves, scales or the
    // map viewport changes. Only this projection is rebuilt.
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class PuppyPiMapPathGraphic : MaskableGraphic
    {
        private MapGridGeometry _geometry;
        private IReadOnlyList<Vector2> _raw, _prepared;
        private Vector3 _robot;
        private bool _hasRobot;
        private float _clearance;
        private MapRobotFootprint _footprint;
        public bool NavigationVerified { get; private set; }
        public MapPathBlock Block { get; private set; }
        public bool BlockAtRobot { get; private set; }
        public bool IsDrawing { get; private set; }
        public float RawOpacity => IsDrawing ? 1 : _prepared != null && _prepared.Count > 0 ? 0.08f : 0.35f;

        public void SetPaths(MapGridGeometry geometry, IReadOnlyList<Vector2> raw,
            IReadOnlyList<Vector2> prepared, bool hasRobot, Vector3 robot,
            bool navigationVerified = false, MapPathBlock block = null, bool blockAtRobot = false,
            float clearanceRadiusMeters = 0, bool isDrawing = false, MapRobotFootprint footprint = null)
        {
            _geometry = geometry;
            _raw = raw;
            _prepared = prepared;
            _hasRobot = hasRobot && Finite(robot.x) && Finite(robot.y) && Finite(robot.z);
            _robot = robot;
            NavigationVerified = navigationVerified;
            Block = block;
            BlockAtRobot = blockAtRobot;
            IsDrawing = isDrawing;
            _clearance = Finite(clearanceRadiusMeters) ? Mathf.Max(0, clearanceRadiusMeters) : 0;
            _footprint = footprint;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            if (_geometry == null || !_geometry.IsValid) return;
            Color routeColor = NavigationVerified ? new Color(0.15f, 0.95f, 0.75f, 1) : new Color(1, 0.7f, 0.15f, 1);
            bool confirmedObstacle = Block != null && Block.Kind == MapPathBlockKind.Occupied;
            Color blockedColor = confirmedObstacle ? new Color(1, 0.18f, 0.15f, 1) : new Color(1, 0.7f, 0.15f, 1);
            if (_prepared != null && _prepared.Count > 0 && _hasRobot)
            {
                Vector2 robot = new Vector2(_robot.x, _robot.y);
                DrawClearanceEdges(mesh, robot, _prepared[0], routeColor);
                DrawSegment(mesh, robot, _prepared[0], 2, routeColor);
            }
            if (_prepared != null)
                for (int i = 1; i < _prepared.Count; i++)
                    DrawClearanceEdges(mesh, _prepared[i - 1], _prepared[i], routeColor);
            DrawPath(mesh, _raw, IsDrawing ? 2 : 1, new Color(0.12f, 0.35f, 0.75f, RawOpacity));
            DrawPath(mesh, _prepared, 2, routeColor);
            if (_prepared != null && _prepared.Count > 0)
                Disc(mesh, Project(_prepared[0]), 3, routeColor);
            if (Block != null && Block.Kind != MapPathBlockKind.Invalid)
                DrawBlockedCell(mesh, Block.Position, blockedColor, confirmedObstacle);
            if (_hasRobot && Finite(_robot.x) && Finite(_robot.y) && Finite(_robot.z))
            {
                Vector2 uv = _geometry.MetersToUv(new Vector2(_robot.x, _robot.y));
                if (uv.x < 0 || uv.x > 1 || uv.y < 0 || uv.y > 1) return;
                Vector2 center = Project(new Vector2(_robot.x, _robot.y));
                Vector2 direction = new Vector2(Mathf.Cos(_robot.z), Mathf.Sin(_robot.z));
                if (_footprint != null && _footprint.IsValid)
                {
                    Vector2 position = new Vector2(_robot.x, _robot.y);
                    Vector2 forward = direction * _footprint.HalfLength;
                    Vector2 lateral = new Vector2(-direction.y, direction.x) * _footprint.HalfWidth;
                    Color outline = new Color(routeColor.r, routeColor.g, routeColor.b, .65f);
                    DrawSegment(mesh, position + forward + lateral, position + forward - lateral, 1, outline);
                    DrawSegment(mesh, position + forward - lateral, position - forward - lateral, 1, outline);
                    DrawSegment(mesh, position - forward - lateral, position - forward + lateral, 1, outline);
                    DrawSegment(mesh, position - forward + lateral, position + forward + lateral, 1, outline);
                }
                // A non-square grid has different UV units per meter. Project
                // the direction through the same geometry as the path.
                Vector2 tip = Project(new Vector2(_robot.x, _robot.y) + direction * _geometry.resolution) - center;
                if (tip.sqrMagnitude < 0.0001f) return;
                tip.Normalize();
                Vector2 side = new Vector2(-tip.y, tip.x);
                Triangle(mesh, center + tip * 12, center - tip * 8 + side * 8,
                    center - tip * 8 - side * 8, new Color(1, 0.2f, 0.18f));
            }
        }

        private Vector2 Project(Vector2 meters)
        {
            Vector2 uv = _geometry.MetersToUv(meters);
            Rect rect = rectTransform.rect;
            return new Vector2(rect.xMin + uv.x * rect.width, rect.yMin + uv.y * rect.height);
        }

        private void DrawPath(VertexHelper mesh, IReadOnlyList<Vector2> points, float width, Color color)
        {
            if (points == null) return;
            for (int i = 1; i < points.Count; i++)
                DrawSegment(mesh, points[i - 1], points[i], width, color);
        }

        private void DrawSegment(VertexHelper mesh, Vector2 a, Vector2 b, float width, Color color) =>
            DrawPixelSegment(mesh, Project(a), Project(b), width, color);

        // The route stays a thin centreline. Dashed edges describe the checked
        // robot clearance without painting over obstacle cells inside it.
        private void DrawClearanceEdges(VertexHelper mesh, Vector2 a, Vector2 b, Color color)
        {
            Vector2 direction = b - a;
            float halfWidth = _footprint != null && _footprint.IsValid ? _footprint.HalfWidth : _clearance;
            if (halfWidth <= 0 || direction.sqrMagnitude < 0.000001f) return;
            Vector2 side = new Vector2(-direction.y, direction.x).normalized * halfWidth;
            Color edge = new Color(color.r, color.g, color.b, .35f);
            DrawDashedPixelSegment(mesh, Project(a + side), Project(b + side), edge);
            DrawDashedPixelSegment(mesh, Project(a - side), Project(b - side), edge);
        }

        private void DrawDashedPixelSegment(VertexHelper mesh, Vector2 a, Vector2 b, Color color)
        {
            if (!Clip(rectTransform.rect, ref a, ref b)) return;
            Vector2 delta = b - a;
            float length = delta.magnitude;
            if (length < .001f) return;
            Vector2 direction = delta / length;
            for (float at = 0; at < length; at += 12)
                DrawPixelSegment(mesh, a + direction * at, a + direction * Mathf.Min(at + 5, length), 1, color);
        }

        private void DrawBlockedCell(VertexHelper mesh, Vector2 position, Color color, bool confirmedObstacle)
        {
            Vector2 center = Project(position);
            Vector2 cell = new Vector2(rectTransform.rect.width / _geometry.width,
                rectTransform.rect.height / _geometry.height);
            Vector2 half = new Vector2(Mathf.Max(3, cell.x * .5f), Mathf.Max(3, cell.y * .5f));
            Vector2 lower = center - half, upper = center + half;
            DrawPixelSegment(mesh, lower, new Vector2(upper.x, lower.y), 2, color);
            DrawPixelSegment(mesh, new Vector2(upper.x, lower.y), upper, 2, color);
            DrawPixelSegment(mesh, upper, new Vector2(lower.x, upper.y), 2, color);
            DrawPixelSegment(mesh, new Vector2(lower.x, upper.y), lower, 2, color);
            // A cross denotes confirmed occupancy. Missing observations and
            // map boundaries retain an amber outline without an obstacle cross.
            if (confirmedObstacle)
            {
                DrawPixelSegment(mesh, center - Vector2.right * 6, center + Vector2.right * 6, 1, color);
                DrawPixelSegment(mesh, center - Vector2.up * 6, center + Vector2.up * 6, 1, color);
            }
        }

        private void DrawPixelSegment(VertexHelper mesh, Vector2 a, Vector2 b, float width, Color color)
        {
            if (!Clip(rectTransform.rect, ref a, ref b)) return;
            Vector2 delta = b - a;
            if (delta.sqrMagnitude < 0.0001f) return;
            Vector2 side = new Vector2(-delta.y, delta.x).normalized * width * 0.5f;
            int start = mesh.currentVertCount;
            mesh.AddVert(a - side, color, Vector2.zero);
            mesh.AddVert(a + side, color, Vector2.zero);
            mesh.AddVert(b + side, color, Vector2.zero);
            mesh.AddVert(b - side, color, Vector2.zero);
            mesh.AddTriangle(start, start + 1, start + 2);
            mesh.AddTriangle(start, start + 2, start + 3);
        }

        private static bool Clip(Rect rect, ref Vector2 a, ref Vector2 b)
        {
            Vector2 delta = b - a;
            float lo = 0, hi = 1;
            if (!ClipEdge(-delta.x, a.x - rect.xMin, ref lo, ref hi) ||
                !ClipEdge(delta.x, rect.xMax - a.x, ref lo, ref hi) ||
                !ClipEdge(-delta.y, a.y - rect.yMin, ref lo, ref hi) ||
                !ClipEdge(delta.y, rect.yMax - a.y, ref lo, ref hi)) return false;
            Vector2 original = a;
            a = original + delta * lo;
            b = original + delta * hi;
            return true;
        }

        private static bool ClipEdge(float p, float q, ref float lo, ref float hi)
        {
            if (Mathf.Abs(p) < 0.000001f) return q >= 0;
            float ratio = q / p;
            if (p < 0) { if (ratio > hi) return false; lo = Mathf.Max(lo, ratio); }
            else { if (ratio < lo) return false; hi = Mathf.Min(hi, ratio); }
            return true;
        }

        private static void Disc(VertexHelper mesh, Vector2 center, float radius, Color color)
        {
            for (int i = 0; i < 12; i++)
            {
                float a = i * Mathf.PI / 6, b = (i + 1) * Mathf.PI / 6;
                Triangle(mesh, center, center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius,
                    center + new Vector2(Mathf.Cos(b), Mathf.Sin(b)) * radius, color);
            }
        }

        private static void Triangle(VertexHelper mesh, Vector2 a, Vector2 b, Vector2 c, Color color)
        {
            int start = mesh.currentVertCount;
            mesh.AddVert(a, color, Vector2.zero);
            mesh.AddVert(b, color, Vector2.zero);
            mesh.AddVert(c, color, Vector2.zero);
            mesh.AddTriangle(start, start + 1, start + 2);
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
