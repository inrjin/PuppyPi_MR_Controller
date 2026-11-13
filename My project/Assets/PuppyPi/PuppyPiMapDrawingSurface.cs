using UnityEngine;
using UnityEngine.EventSystems;

namespace PuppyPi
{
    // Uses the existing Meta PointableCanvasModule, so poke and ray share one
    // pointer capture instead of competing global hand gesture detectors.
    public sealed class PuppyPiMapDrawingSurface : MonoBehaviour, IPointerDownHandler,
        IPointerUpHandler, IInitializePotentialDragHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public PuppyPiMapWindow window;
        private int? _pointer;

        public bool TryWorldToUv(Vector3 world, out Vector2 uv)
        {
            var rect = (RectTransform)transform;
            Vector3 local = rect.InverseTransformPoint(world);
            return TryLocalToUv(rect.rect, local, out uv);
        }

        public static bool TryLocalToUv(Rect rect, Vector2 local, out Vector2 uv)
        {
            uv = default;
            if (rect.width <= 0 || rect.height <= 0 || !Finite(local.x) || !Finite(local.y)) return false;
            uv = new Vector2((local.x - rect.xMin) / rect.width, (local.y - rect.yMin) / rect.height);
            return uv.x >= 0 && uv.x <= 1 && uv.y >= 0 && uv.y <= 1;
        }

        private bool TryPointerUv(PointerEventData data, out Vector2 uv)
        {
            uv = default;
            var rect = (RectTransform)transform;
            // Meta replaces the canvas event camera while processing pointers.
            // Its event camera, rather than Camera.main, matches these positions.
            return RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, data.position,
                data.pressEventCamera, out Vector2 local) && TryLocalToUv(rect.rect, local, out uv);
        }

        public void OnPointerDown(PointerEventData data)
        {
            if (_pointer.HasValue || window == null || !TryPointerUv(data, out Vector2 uv)) return;
            if (!window.BeginStroke(data.pointerId, uv)) return;
            _pointer = data.pointerId;
            data.useDragThreshold = false;
            data.eligibleForClick = false;
        }

        public void OnInitializePotentialDrag(PointerEventData data) => data.useDragThreshold = false;
        public void OnBeginDrag(PointerEventData data) => data.eligibleForClick = false;

        public void OnDrag(PointerEventData data)
        {
            if (_pointer != data.pointerId || window == null) return;
            if (!TryPointerUv(data, out Vector2 uv))
            {
                window.CancelStroke("Drawing left the map. Draw again.");
                _pointer = null;
                return;
            }
            window.AppendStroke(data.pointerId, uv);
        }

        public void OnPointerUp(PointerEventData data)
        {
            if (_pointer != data.pointerId || window == null) return;
            if (TryPointerUv(data, out Vector2 uv))
            {
                window.AppendStroke(data.pointerId, uv);
                window.EndStroke(data.pointerId);
            }
            else window.CancelStroke("Drawing left the map. Draw again.");
            _pointer = null;
        }

        public void OnEndDrag(PointerEventData data) => OnPointerUp(data);
        public void ReleaseCapture() => _pointer = null;

        private void OnDisable()
        {
            _pointer = null;
            if (window != null) window.CancelStroke("Drawing cancelled.");
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
