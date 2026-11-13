#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Oculus.Interaction;
using Oculus.Interaction.HandGrab;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace PuppyPi.Tests
{
    public class PuppyPiMapWindowTests
    {
        private GameObject _fixture;
        private PuppyPiMapWindow _window;
        private Texture2D _texture;
        private PointableCanvasModule _module;

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            if (_window != null && _window.WindowRoot != null) Object.Destroy(_window.WindowRoot.gameObject);
            if (_fixture != null) Object.Destroy(_fixture);
            if (_texture != null) Object.Destroy(_texture);
            yield return null;
        }

        private void Create()
        {
            _fixture = new GameObject("Offline map window fixture");
            _fixture.SetActive(false);
            _fixture.AddComponent<EventSystem>();
            _module = _fixture.AddComponent<PointableCanvasModule>();
            var head = new GameObject("Map fixture head", typeof(Camera));
            head.transform.SetParent(_fixture.transform, false);
            head.transform.position = new Vector3(1, 1.6f, -2);
            head.transform.rotation = Quaternion.Euler(0, 25, 0);
            _window = _fixture.AddComponent<PuppyPiMapWindow>();
            _window.head = head.transform;
            _fixture.SetActive(true);
            _window.ShowAndRecenter();
            _texture = new Texture2D(200, 100);
        }

        private static MapGridGeometry Geometry() => new MapGridGeometry {
            width = 200, height = 100, resolution = 0.05f,
            origin = new Vector2(-5, -2.5f), frame = "map"
        };

        private void SetMap(MapGridGeometry geometry = null, string session = "session-a", bool live = true, float age = 0.1f)
        {
            _window.SetMapFrame(_texture, geometry ?? Geometry(), Vector3.zero, true, live,
                session, 1, age, "Fixture grid", "offline:5001");
            Canvas.ForceUpdateCanvases();
        }

        private Vector3 World(Vector2 uv)
        {
            RectTransform rect = _window.MapView.rectTransform;
            return rect.TransformPoint(new Vector3(Mathf.LerpUnclamped(rect.rect.xMin, rect.rect.xMax, uv.x),
                Mathf.LerpUnclamped(rect.rect.yMin, rect.rect.yMax, uv.y), 0));
        }

        private void UiPointer(int id, PointerEventType kind, Vector2 uv)
        {
            var canvas = _window.WindowRoot.GetComponentInChildren<PointableCanvas>();
            canvas.ProcessPointerEvent(new PointerEvent(id, kind, new Pose(World(uv), _window.WindowRoot.rotation)));
            _module.Process();
        }

        private static void GrabPointer(Grabbable grab, int id, PointerEventType kind, Pose pose) =>
            grab.ProcessPointerEvent(new PointerEvent(id, kind, pose));
        private static void Select(Grabbable grab, int id, Pose pose)
        {
            GrabPointer(grab, id, PointerEventType.Hover, pose);
            GrabPointer(grab, id, PointerEventType.Select, pose);
        }

        [UnityTest]
        public IEnumerator NativeCanvasDragPreparesOnlyAndUsesActualImageRectAfterWindowTransforms()
        {
            Create();
            yield return null;
            Assert.That(_window.IsVisible, Is.True, "the window opens even before a grid arrives");
            Assert.That(_window.NoMapLabel.gameObject.activeSelf, Is.True);
            _window.ToggleDrawMode();
            Assert.That(_window.DrawMode, Is.False);
            SetMap();
            Transform root = _window.WindowRoot;
            root.localScale = Vector3.one * 1.6f;
            root.position += new Vector3(0.1f, 0.2f, -0.1f);
            root.rotation = Quaternion.Euler(5, 25, 10) * root.rotation;
            Canvas.ForceUpdateCanvases();
            RectTransform image = _window.MapView.rectTransform;
            Assert.That(image.rect.width / image.rect.height, Is.EqualTo(2).Within(0.001));
            Assert.That(image.rect.height, Is.LessThan(((RectTransform)image.parent).rect.height), "letterbox space is not a drawing target");
            Assert.That(_window.DrawingSurface.TryWorldToUv(World(new Vector2(0.25f, 0.75f)), out Vector2 uv), Is.True);
            Assert.That(Vector2.Distance(uv, new Vector2(0.25f, 0.75f)), Is.LessThan(0.0001));
            Vector3 outside = image.TransformPoint(new Vector3(0, image.rect.yMax + 5, 0));
            Assert.That(_window.DrawingSurface.TryWorldToUv(outside, out _), Is.False);
            var pointable = root.GetComponentInChildren<PointableCanvas>();
            Assert.That(root.GetComponentInChildren<PokeInteractable>().PointableElement, Is.SameAs(pointable));
            Assert.That(root.GetComponentInChildren<RayInteractable>().PointableElement, Is.SameAs(pointable));

            _window.ToggleDrawMode();
            UiPointer(1001, PointerEventType.Hover, new Vector2(0.5f, 0.5f));
            UiPointer(1001, PointerEventType.Select, new Vector2(0.5f, 0.5f));
            Assert.That(_window.IsDrawing, Is.True, "Meta canvas pointer selection must reach the map drawing handler");
            Assert.That(_window.PlacementLocked, Is.True);
            Assert.That(root.GetComponent<Grabbable>().enabled, Is.False);
            Assert.That(_window.BeginStroke(1002, new Vector2(0.6f, 0.5f)), Is.False, "the other pointer cannot replace an active stroke");
            UiPointer(1001, PointerEventType.Move, new Vector2(0.6f, 0.55f));
            UiPointer(1001, PointerEventType.Move, new Vector2(0.65f, 0.6f));
            UiPointer(1001, PointerEventType.Unselect, new Vector2(0.65f, 0.6f));
            Assert.That(_window.IsDrawing, Is.False);
            Assert.That(_window.DrawMode, Is.False);
            Assert.That(_window.PlacementLocked, Is.False);
            Assert.That(_window.PreparedWaypoints.Count, Is.GreaterThanOrEqualTo(2));
            Assert.That(_window.NavigationVerified, Is.False, "geometry preparation alone cannot produce a clear-navigation preview");
            Assert.That(_window.RawPoints[0].magnitude, Is.LessThan(0.01f), "the map origin under the robot must survive world-window transforms");
            Assert.That(_window.controller, Is.Null, "preparing a route requires no robot connection or command channel");
            Button run = root.GetComponentsInChildren<Button>().Single(button => button.name == "RUN");
            Assert.That(run.interactable, Is.False, "release cannot send a command; an unavailable path client keeps RUN disabled");
            var preview = root.GetComponentInChildren<PuppyPiMapPathGraphic>();
            Assert.That(preview.raycastTarget, Is.False);
            Assert.That(preview.GetComponent<CanvasRenderer>(), Is.Not.Null, "a clipped custom Graphic must own its CanvasRenderer");
            UiPointer(1001, PointerEventType.Unhover, new Vector2(0.65f, 0.6f));

            Vector2[] reviewed = _window.RawPoints.ToArray();
            _window.ToggleDrawMode();
            UiPointer(1003, PointerEventType.Hover, new Vector2(0.5f, 0.5f));
            UiPointer(1003, PointerEventType.Select, new Vector2(0.5f, 0.5f));
            UiPointer(1003, PointerEventType.Move, new Vector2(0.7f, 0.5f));
            // No outside Move event: release alone must cancel rather than
            // accepting the last in-bounds partial stroke as a reviewed route.
            Assert.That(_window.DrawingSurface.TryWorldToUv(World(new Vector2(1.2f, 0.5f)), out _), Is.False,
                "the fixture must deliver an actual off-map world pose, not a clamped boundary point");
            UiPointer(1003, PointerEventType.Unselect, new Vector2(1.2f, 0.5f));
            Assert.That(_window.IsDrawing, Is.False);
            CollectionAssert.AreEqual(reviewed, _window.RawPoints, "an off-map release restores the previous reviewed path");
            Assert.That(_window.StatusLabel.text, Does.Contain("left the map"));
            UiPointer(1003, PointerEventType.Unhover, new Vector2(1.2f, 0.5f));
        }

        [UnityTest]
        public IEnumerator RimTransformsStayUniformAndDraftMetersSurviveViewportChangesButNotSessionChanges()
        {
            Create();
            SetMap();
            yield return null;
            Transform root = _window.WindowRoot;
            Assert.That(root.parent, Is.Null);
            Assert.That(root.GetComponent<Rigidbody>().isKinematic, Is.True);
            Assert.That(root.GetComponent<Rigidbody>().useGravity, Is.False);
            var grab = root.GetComponent<Grabbable>();
            Assert.That(root.GetComponent<HandGrabInteractable>().PointableElement, Is.SameAs(grab));
            Assert.That(root.GetComponent<GrabInteractable>().PointableElement, Is.SameAs(grab));
            foreach (Collider collider in root.GetComponentsInChildren<Collider>())
            {
                Assert.That(collider.isTrigger, Is.True);
                Assert.That(collider.bounds.Contains(root.position), Is.False, "the map center must not become a hand-grab target");
            }
            Pose left = new Pose(root.position - root.right * 0.4f, root.rotation);
            Pose right = new Pose(root.position + root.right * 0.4f, root.rotation);
            Select(grab, 2001, left);
            Vector3 initial = root.position;
            left.position += Vector3.up * 0.12f;
            GrabPointer(grab, 2001, PointerEventType.Move, left);
            Assert.That(Vector3.Distance(root.position, initial), Is.GreaterThan(0.05));
            Select(grab, 2002, right);
            right.position = left.position + root.right * 8;
            GrabPointer(grab, 2002, PointerEventType.Move, right);
            Assert.That(root.localScale.x, Is.EqualTo(_window.maximumScale).Within(0.001));
            Assert.That(root.localScale.y, Is.EqualTo(root.localScale.x).Within(0.0001));
            Assert.That(root.localScale.z, Is.EqualTo(root.localScale.x).Within(0.0001));
            right.position = left.position + root.right * 0.01f;
            GrabPointer(grab, 2002, PointerEventType.Move, right);
            Assert.That(root.localScale.x, Is.EqualTo(_window.minimumScale).Within(0.001));
            Assert.That(root.localScale.y, Is.EqualTo(root.localScale.x).Within(0.0001));
            Assert.That(root.localScale.z, Is.EqualTo(root.localScale.x).Within(0.0001));
            right.position = left.position + root.right * 8;
            GrabPointer(grab, 2002, PointerEventType.Move, right);
            _window.ShowAndRecenter();
            Assert.That(root.localScale.x, Is.EqualTo(_window.maximumScale).Within(0.001), "recenter preserves chosen scale");
            Assert.That(grab.SelectingPointsCount, Is.Zero, "recenter cancels obsolete grab offsets");

            _window.ToggleDrawMode();
            Assert.That(_window.BeginStroke(3001, new Vector2(0.5f, 0.5f)), Is.True);
            _window.AppendStroke(3001, new Vector2(0.6f, 0.5f));
            Assert.That(_window.EndStroke(3001), Is.True);
            Vector2[] accepted = _window.RawPoints.ToArray();
            var changed = new MapGridGeometry { width = 240, height = 120, resolution = 0.05f, origin = new Vector2(-6, -3), frame = "map" };
            SetMap(changed);
            CollectionAssert.AreEqual(accepted, _window.RawPoints, "viewport changes reproject the existing meter-space route");
            _window.ToggleDrawMode();
            Assert.That(_window.BeginStroke(3002, new Vector2(0.5f, 0.5f)), Is.True);
            _window.AppendStroke(3002, new Vector2(0.7f, 0.6f));
            changed.origin = new Vector2(-7, -3);
            SetMap(changed);
            Assert.That(_window.IsDrawing, Is.False, "a geometry change must cancel the in-progress capture");
            CollectionAssert.AreEqual(accepted, _window.RawPoints, "cancelling a new stroke restores the previously reviewed draft");
            SetMap(changed, "session-b");
            Assert.That(_window.RawPoints, Is.Empty);
            Assert.That(_window.PreparedWaypoints, Is.Empty, "paths cannot cross a reset map session");
            SetMap(changed, "session-b", true, 6.1f);
            _window.ToggleDrawMode();
            Assert.That(_window.DrawMode, Is.True, "a retained map remains editable while newer observations are pending");
            Assert.That(_window.NavigationVerified, Is.False, "map editing never grants stale navigation permission");
        }

        [UnityTest]
        public IEnumerator NavigationPreviewDistinguishesRobotFootprintConnectorRouteAndMissingCurrentObservations()
        {
            Create();
            SetMap();
            yield return null;
            _window.ToggleDrawMode();
            Assert.That(_window.BeginStroke(4001, new Vector2(.52f, .5f)), Is.True);
            _window.AppendStroke(4001, new Vector2(.6f, .5f));
            Assert.That(_window.EndStroke(4001), Is.True);
            var preview = _window.WindowRoot.GetComponentInChildren<PuppyPiMapPathGraphic>();
            Assert.That(preview.NavigationVerified, Is.False);
            Assert.That(_window.ValidationStatus, Does.Contain("NOT CHECKED"));
            Vector2[] reviewed = _window.PreparedWaypoints.ToArray();

            var footprintBlocked = new int[200 * 100];
            footprintBlocked[50 * 200 + 103] = 90;
            _window.SetNavigationFrame(footprintBlocked, true, "Fixture current observations", .22f);
            Assert.That(_window.NavigationVerified, Is.False);
            Assert.That(preview.BlockAtRobot, Is.True);
            Assert.That(_window.PreviewBlock.Kind, Is.EqualTo(MapPathBlockKind.Occupied));
            Assert.That(_window.ValidationStatus, Does.Contain("Robot surroundings"));
            Assert.That(footprintBlocked[50 * 200 + 103], Is.EqualTo(90), "preview checks never clear source occupancy cells");

            var connectorBlocked = new int[200 * 100];
            connectorBlocked[50 * 200 + 108] = 100;
            _window.SetNavigationFrame(connectorBlocked, true, "Fixture current observations", .22f);
            Assert.That(preview.BlockAtRobot, Is.False);
            Assert.That(_window.PreviewBlock.SegmentIndex, Is.Zero);
            Assert.That(_window.ValidationStatus, Does.Contain("Robot to first point"));

            var routeBlocked = new int[200 * 100];
            routeBlocked[50 * 200 + 115] = -1;
            _window.SetNavigationFrame(routeBlocked, true, "Fixture current observations", .22f);
            Assert.That(_window.PreviewBlock.SegmentIndex, Is.EqualTo(1));
            Assert.That(_window.PreviewBlock.Kind, Is.EqualTo(MapPathBlockKind.Unknown));
            Assert.That(_window.ValidationStatus, Does.StartWith("WAITING FOR OBSERVED SPACE"));
            Assert.That(_window.ValidationStatus, Does.Contain("Route segment 1"));
            Assert.That(_window.ValidationStatus, Does.Contain("unobserved"));

            var free = new int[200 * 100];
            _window.SetNavigationFrame(free, false, "Scan is stale", .22f);
            Assert.That(_window.NavigationVerified, Is.False);
            Assert.That(preview.Block, Is.Null, "missing observations are not rendered as an invented obstacle");
            Assert.That(_window.ValidationStatus, Does.Contain("Scan is stale"));
            _window.SetNavigationFrame(free, true, "Fixture current observations", .22f);
            Assert.That(_window.NavigationVerified, Is.True);
            Assert.That(preview.NavigationVerified, Is.True);
            Assert.That(_window.ValidationStatus, Does.StartWith("CLEAR"));
            CollectionAssert.AreEqual(reviewed, _window.PreparedWaypoints);

            _window.ToggleDrawMode();
            Assert.That(_window.BeginStroke(4002, new Vector2(.5f, .5f)), Is.True);
            Assert.That(preview.NavigationVerified, Is.False, "a new stroke must not inherit the previous route's green check");
            Assert.That(_window.PreparedWaypoints, Is.Empty);
            _window.CancelStroke("Fixture cancelled the new stroke.");
            CollectionAssert.AreEqual(reviewed, _window.PreparedWaypoints);
        }

        [UnityTest]
        public IEnumerator NativeReleaseAndUnhoverInOneFrameKeepCorrectionWhileTrackingCancelRestoresDraft()
        {
            Create();
            SetMap();
            yield return null;
            var canvas = _window.WindowRoot.GetComponentInChildren<PointableCanvas>();
            var preview = _window.WindowRoot.GetComponentInChildren<PuppyPiMapPathGraphic>();
            _window.ToggleDrawMode();
            UiPointer(5001, PointerEventType.Hover, new Vector2(.5f, .5f));
            UiPointer(5001, PointerEventType.Select, new Vector2(.5f, .5f));
            UiPointer(5001, PointerEventType.Move, new Vector2(.55f, .502f));
            UiPointer(5001, PointerEventType.Move, new Vector2(.6f, .498f));
            Assert.That(_window.IsDrawing, Is.True);
            Assert.That(preview.RawOpacity, Is.EqualTo(1), "the active hand stroke remains clearly visible");

            // Reproduce SDK batching: native Unselect and Unhover both arrive
            // before PointableCanvasModule converts them into UI events.
            Pose release = new Pose(World(new Vector2(.65f, .5f)), _window.WindowRoot.rotation);
            canvas.ProcessPointerEvent(new PointerEvent(5001, PointerEventType.Unselect, release));
            Assert.That(_window.IsDrawing, Is.False, "native release must finish without waiting for the deferred UI PointerUp");
            Vector2[] corrected = _window.PreparedWaypoints.ToArray();
            Assert.That(corrected.Length, Is.GreaterThanOrEqualTo(2));
            canvas.ProcessPointerEvent(new PointerEvent(5001, PointerEventType.Unhover, release));
            _module.Process();
            CollectionAssert.AreEqual(corrected, _window.PreparedWaypoints, "normal same-frame Unhover must not discard the corrected route");
            Assert.That(_window.StatusLabel.text, Does.Contain("STRAIGHTENED ROUTE"));
            Assert.That(_window.StatusLabel.text, Does.Not.Contain("->"), "point counts are diagnostic data, not the main user feedback");
            Assert.That(preview.RawOpacity, Is.LessThanOrEqualTo(.1f), "the original stroke must not visually obscure the corrected route");

            _window.ToggleDrawMode();
            UiPointer(5002, PointerEventType.Hover, new Vector2(.5f, .5f));
            UiPointer(5002, PointerEventType.Select, new Vector2(.5f, .5f));
            UiPointer(5002, PointerEventType.Move, new Vector2(.5f, .65f));
            Pose cancelled = new Pose(World(new Vector2(.5f, .65f)), _window.WindowRoot.rotation);
            canvas.ProcessPointerEvent(new PointerEvent(5002, PointerEventType.Cancel, cancelled));
            canvas.ProcessPointerEvent(new PointerEvent(5002, PointerEventType.Unhover, cancelled));
            _module.Process();
            Assert.That(_window.IsDrawing, Is.False);
            CollectionAssert.AreEqual(corrected, _window.PreparedWaypoints, "tracking cancellation restores the earlier reviewed route instead of committing a partial stroke");
            Assert.That(_window.StatusLabel.text, Does.Contain("Pointer tracking ended"));
        }

        [UnityTest]
        public IEnumerator ScanGapKeepsMapAndStrokeEditableButNavigationStillRequiresFreshObservations()
        {
            Create();
            SetMap();
            yield return null;
            var free = new int[200 * 100];
            _window.SetNavigationFrame(free, true, "Fixture current observations", .22f);
            _window.ToggleDrawMode();
            Assert.That(_window.BeginStroke(6001, new Vector2(.5f, .5f)), Is.True);
            _window.AppendStroke(6001, new Vector2(.55f, .5f));

            // Geometry, frame, session and the last valid robot pose survive
            // the gap. These availability flags have no freshness deadline.
            _window.SetMapFrame(_texture, Geometry(), Vector3.zero, true, true,
                "session-a", 2, 25, "Updating scan", "offline:5001");
            _window.SetNavigationFrame(free, false, "Scan is stale", .22f);
            Assert.That(_window.IsDrawing, Is.True);
            Assert.That(_window.MapView.raycastTarget, Is.True);
            Assert.That(_window.MapView.color, Is.EqualTo(Color.white), "a temporary scan gap must not dim the drawing surface");
            Assert.That(_window.NoMapLabel.gameObject.activeSelf, Is.False);
            Assert.That(_window.StatusLabel.text, Does.Contain("Updating scan"));
            Assert.That(_window.AppendStroke(6001, new Vector2(.6f, .5f)), Is.True);
            Assert.That(_window.EndStroke(6001), Is.True);
            Assert.That(_window.NavigationVerified, Is.False);
            Assert.That(_window.PreparedWaypoints.Count, Is.GreaterThanOrEqualTo(2));
            _window.SetNavigationFrame(free, false, "Waiting for map session update.", .22f);
            Assert.That(_window.StatusLabel.text, Does.Contain("Waiting for map session"));
            Assert.That(_window.StatusLabel.text, Does.Not.Contain("Updating scan"), "metadata delay must not be described as a LiDAR failure");
            Assert.That(_window.NoMapLabel.gameObject.activeSelf, Is.False);
            _window.SetNavigationFrame(free, true, "Fixture matched scan/pose remains current", .22f);
            Assert.That(_window.NavigationVerified, Is.True, "navigation readiness comes from the matched scan/pose gate, not the cached map age");
            _window.SetNavigationFrame(free, false, "Scan is stale", .22f);
            Assert.That(_window.NavigationVerified, Is.False);
            Button draw = _window.WindowRoot.GetComponentsInChildren<Button>().Single(button => button.name == "DRAW");
            Assert.That(draw.interactable, Is.True, "fresh tracking may begin another stroke on the retained map");
            _window.ToggleDrawMode();
            Assert.That(_window.BeginStroke(6002, new Vector2(.5f, .5f)), Is.True);
            _window.CancelStroke("Fixture cancelled the second stroke.");

            // A real coordinate identity reset still discards drafts.
            _window.SetMapFrame(_texture, Geometry(), Vector3.zero, true, true,
                "session-b", 3, 25, "Updating scan", "offline:5001");
            Assert.That(_window.RawPoints, Is.Empty);
            Assert.That(_window.PreparedWaypoints, Is.Empty);
        }

        [UnityTest]
        public IEnumerator ThinRouteLeavesClearanceInteriorVisibleAndMarksOnlyTheActualBlockedCell()
        {
            Create();
            SetMap();
            yield return null;
            Canvas.ForceUpdateCanvases();
            var preview = _window.WindowRoot.GetComponentInChildren<PuppyPiMapPathGraphic>();
            Vector2[] route = { new Vector2(-1, 0), new Vector2(1, 0) };
            preview.SetPaths(Geometry(), null, route, false, Vector3.zero, clearanceRadiusMeters: .22f);
            List<UIVertex> vertices = PreviewVertices(preview);
            Assert.That(Covers(vertices, Vector2.zero), Is.True, "the thin centreline must remain visible");
            float scale = preview.rectTransform.rect.height / (Geometry().height * Geometry().resolution);
            Assert.That(Covers(vertices, Vector2.up * (.11f * scale)), Is.False,
                "the inside of the clearance band must stay transparent so map obstacles remain visible");

            Vector2 blockedPosition = new Vector2(.5f, .025f);
            preview.SetPaths(Geometry(), null, route, false, Vector3.zero,
                block: new MapPathBlock { Kind = MapPathBlockKind.Occupied, SegmentIndex = 0,
                    Position = blockedPosition, CellValue = 100 }, clearanceRadiusMeters: .22f);
            vertices = PreviewVertices(preview);
            Rect rect = preview.rectTransform.rect;
            Vector2 uv = Geometry().MetersToUv(blockedPosition);
            Vector2 expected = new Vector2(rect.xMin + uv.x * rect.width, rect.yMin + uv.y * rect.height);
            var red = (Color32)new Color(1, .18f, .15f, 1);
            UIVertex[] redVertices = vertices.Where(vertex => vertex.color.Equals(red)).ToArray();
            Assert.That(redVertices, Is.Not.Empty);
            Assert.That(redVertices.All(vertex => Vector2.Distance(vertex.position, expected) < 10), Is.True,
                "the red mark must identify the source cell rather than repainting the whole route segment");
        }

        [UnityTest]
        public IEnumerator SecondRouteUsesCurrentRobotStartAndReportsBlockCoordinatesWithoutOldState()
        {
            Create();
            SetMap();
            yield return null;
            var free = new int[200 * 100];
            _window.SetNavigationFrame(free, true, "Fixture scan", .22f);
            _window.ToggleDrawMode();
            Assert.That(_window.BeginStroke(7001, new Vector2(.5f, .5f)), Is.True);
            _window.AppendStroke(7001, new Vector2(.6f, .5f));
            Assert.That(_window.EndStroke(7001), Is.True);
            Assert.That(_window.NavigationVerified, Is.True);

            // The first drive ends at x=1. The old line must not turn green as
            // a second run from a robot that is no longer near that line's start.
            _window.SetMapFrame(_texture, Geometry(), new Vector3(1, 0, 0), true, true,
                "session-a", 2, .1f, "Fixture scan", "offline:5001");
            Assert.That(_window.NavigationVerified, Is.False);
            Assert.That(_window.ValidationStatus, Does.Contain("START TOO FAR"));
            Assert.That(_window.PreviewBlock, Is.Null, "start mismatch must never masquerade as an obstacle");

            _window.ToggleDrawMode();
            Assert.That(_window.BeginStroke(7002, new Vector2(.6f, .5f)), Is.True);
            _window.AppendStroke(7002, new Vector2(.7f, .5f));
            Assert.That(_window.EndStroke(7002), Is.True);
            Assert.That(_window.NavigationVerified, Is.True, "a new route from the current marker starts with fresh validation");
            var blocked = new int[200 * 100];
            blocked[50 * 200 + 130] = -1;
            _window.SetNavigationFrame(blocked, true, "Fixture scan", .22f);
            Assert.That(_window.NavigationVerified, Is.False);
            Assert.That(_window.ValidationStatus, Does.Contain("unobserved"));
            Assert.That(_window.ValidationStatus, Does.Contain("(1.53, 0.03) m"));
            Assert.That(_window.StatusLabel.text, Does.Contain("(1.53, 0.03) m"));
            _window.SetNavigationFrame(free, true, "Fixture next scan", .22f);
            Assert.That(_window.NavigationVerified, Is.True);
            Assert.That(_window.PreviewBlock, Is.Null, "an old failure marker must not leak into the next valid scan");
        }

        [UnityTest]
        public IEnumerator ReopeningKeepsTheDrawingButDropsOldNavigationAndBlockedCells()
        {
            Create();
            SetMap();
            yield return null;
            var free = new int[200 * 100];
            _window.SetNavigationFrame(free, true, "Fixture current scan", .22f);
            _window.ToggleDrawMode();
            Assert.That(_window.BeginStroke(7201, new Vector2(.5f, .5f)), Is.True);
            _window.AppendStroke(7201, new Vector2(.6f, .5f));
            Assert.That(_window.EndStroke(7201), Is.True);
            Vector2[] drawing = _window.PreparedWaypoints.ToArray();
            var old = new int[200 * 100];
            old[50 * 200 + 110] = 100;
            _window.SetNavigationFrame(old, true, "Fixture prior scan", .22f);
            Assert.That(_window.NavigationVerified, Is.False);
            Assert.That(_window.PreviewBlock, Is.Not.Null);

            _window.SetPresentationVisible(false);
            _window.ShowAndRecenter();
            Assert.That(_window.PreparedWaypoints, Is.EqualTo(drawing), "reopening does not change metre coordinates or redraw the stroke");
            Assert.That(_window.PreviewBlock, Is.Null, "the closed view's obstacle marker is not evidence for the reopened view");
            Assert.That(_window.NavigationVerified, Is.False, "cached drawing is not a live obstacle observation");
            Assert.That(_window.ValidationStatus, Does.Contain("latest scan"));

            _window.SetNavigationFrame(free, true, "Fixture replacement current scan", .22f);
            Assert.That(_window.NavigationVerified, Is.True, "the unchanged drawing must validate against the replacement scan, not old cells");
            Assert.That(_window.PreviewBlock, Is.Null);
        }

        [UnityTest]
        public IEnumerator AGeometricallyClearRouteExplainsItsUnavailableCommandChannel()
        {
            Create();
            SetMap();
            yield return null;
            var free = new int[200 * 100];
            _window.SetNavigationFrame(free, true, "Fixture current scan", .22f);
            _window.ToggleDrawMode();
            Assert.That(_window.BeginStroke(7202, new Vector2(.5f, .5f)), Is.True);
            _window.AppendStroke(7202, new Vector2(.6f, .5f));
            Assert.That(_window.EndStroke(7202), Is.True);
            Assert.That(_window.NavigationVerified, Is.True);
            Assert.That(_window.RunUnavailableReason, Does.Contain("command channel"));
            _window.Run();
            Assert.That(_window.StatusLabel.text, Does.Contain("RUN NOT SENT"));

            PuppyPiPathClient path = AddInactiveCommandChannel();
            _window.SetNavigationFrame(free, true, "Fixture current scan", .22f);
            Assert.That(_window.RunUnavailableReason, Is.EqualTo(path.ControlBlockReason));
            Assert.That(_window.StatusLabel.text, Does.Contain("RUN BLOCKED"));
            Assert.That(_window.StatusLabel.text, Does.Contain(path.ControlBlockReason));
            Assert.That(_window.WindowRoot.GetComponentsInChildren<Button>().Single(button => button.name == "RUN").interactable,
                Is.False, "clear map geometry alone must not hide a command-readiness gate");
        }

        [UnityTest]
        public IEnumerator AnImpossibleDetourDisplaysThePlannerFailureInsteadOfHidingIt()
        {
            Create();
            SetMap();
            yield return null;
            PuppyPiPathClient path = AddInactiveCommandChannel();
            var blocked = new int[200 * 100];
            // Cover both sides of the exact metre/grid boundary around (1,0).
            for (int y = 49; y <= 50; y++)
            for (int x = 119; x <= 120; x++) blocked[y * 200 + x] = 100;
            _window.SetNavigationFrame(blocked, true, "Fixture current scan", .22f, new MapRobotFootprint());
            _window.ToggleDrawMode();
            Assert.That(_window.BeginStroke(7203, new Vector2(.5f, .5f)), Is.True);
            _window.AppendStroke(7203, new Vector2(.6f, .5f));
            Assert.That(_window.EndStroke(7203), Is.True);
            Assert.That(_window.NavigationVerified, Is.False);
            Assert.That(_window.ValidationStatus, Does.StartWith("NO ROUTE"));
            Assert.That(_window.ValidationStatus, Does.Contain("occupied"));
            Assert.That(_window.PreviewBlock, Is.Not.Null);
            Assert.That(_window.StatusLabel.text, Does.Contain("NO ROUTE").And.Contain("occupied"),
                "the visible planner failure remains available beside command readiness");
            Assert.That(path.isActiveAndEnabled, Is.False, "this display-only fixture has no live command channel");
            Assert.That(path.ControlBlockReason, Is.Not.Null);
            Assert.That(_window.RunUnavailableReason, Is.EqualTo(path.ControlBlockReason),
                "a control gate takes priority so focus, ownership and emergency stops remain actionable");
            Assert.That(_window.StatusLabel.text, Does.Contain("RUN BLOCKED").And.Contain(path.ControlBlockReason));
            Assert.That(_window.WindowRoot.GetComponentsInChildren<Button>().Single(button => button.name == "RUN").interactable,
                Is.False, "a failed route and inactive command channel must both prevent RUN");
        }

        [UnityTest]
        public IEnumerator CandidateFilterStatusDoesNotBecomeAGlobalDrawOrNavigationGate()
        {
            Create();
            SetMap();
            yield return null;
            var free = new int[200 * 100];
            _window.SetNavigationFrame(free, true, "Fixture current scan", .22f);
            _window.ToggleDrawMode();
            Assert.That(_window.BeginStroke(7301, new Vector2(.5f, .5f)), Is.True);
            _window.AppendStroke(7301, new Vector2(.6f, .5f));
            Assert.That(_window.EndStroke(7301), Is.True);
            Assert.That(_window.NavigationVerified, Is.True);

            _window.SetObstacleFilterStatus("VERIFYING 8 candidates | CONFIRMED 0");
            Assert.That(_window.NavigationVerified, Is.True,
                "candidate confirmation elsewhere must not invalidate a separately verified free route");
            Assert.That(_window.PreviewBlock, Is.Null);
            Assert.That(_window.StatusLabel.text, Does.StartWith("MAP READY | VERIFYING 8 candidates"));
            _window.ToggleDrawMode();
            Assert.That(_window.DrawMode, Is.True, "filter candidates must not become an all-map warmup lock");
        }

        [UnityTest]
        public IEnumerator AFilterReadyCaptionCannotHideAStaleNavigationFrame()
        {
            Create();
            SetMap();
            yield return null;
            var free = new int[200 * 100];
            _window.SetNavigationFrame(free, true, "Fixture current scan", .22f);
            _window.SetObstacleFilterStatus("FILTER READY | CONFIRMED 0");
            Assert.That(_window.StatusLabel.text, Does.StartWith("MAP READY | FILTER READY"));

            _window.SetNavigationFrame(free, false, "NO SIGNAL | Fixture source expired", .22f);
            _window.SetObstacleFilterStatus("FILTER READY | CONFIRMED 0");
            Assert.That(_window.StatusLabel.text, Does.StartWith("Updating scan"));
            Assert.That(_window.StatusLabel.text, Does.Not.Contain("MAP READY | FILTER READY"));
            Assert.That(_window.NavigationVerified, Is.False);
        }

        [UnityTest]
        public IEnumerator FilterEvidenceStatusDoesNotReplaceTheControlChannelBlockReason()
        {
            Create();
            SetMap();
            yield return null;
            var free = new int[200 * 100];
            _window.SetNavigationFrame(free, true, "Fixture current scan", .22f);
            _window.ToggleDrawMode();
            Assert.That(_window.BeginStroke(7302, new Vector2(.5f, .5f)), Is.True);
            _window.AppendStroke(7302, new Vector2(.6f, .5f));
            Assert.That(_window.EndStroke(7302), Is.True);
            PuppyPiPathClient path = AddInactiveCommandChannel();
            _window.SetObstacleFilterStatus("VERIFYING 3 candidates | CONFIRMED 0");

            Assert.That(_window.NavigationVerified, Is.True);
            Assert.That(_window.StatusLabel.text, Does.Contain("VERIFYING 3 candidates"));
            Assert.That(_window.StatusLabel.text, Does.Contain("RUN BLOCKED"));
            Assert.That(_window.RunUnavailableReason, Is.EqualTo(path.ControlBlockReason));
            Assert.That(_window.WindowRoot.GetComponentsInChildren<Button>().Single(button => button.name == "RUN").interactable,
                Is.False, "filtering must not bypass ownership, focus, emergency or neutral-ACK gates");
        }

        [UnityTest]
        public IEnumerator SupervisedDraftSurvivesAnAbsentFrameButNotANewMapIdentity()
        {
            Create();
            var path = AddInactiveCommandChannel();
            path.operatorSupervisedNavigation = true;
            SetMap();
            _window.ToggleDrawMode();
            Assert.That(_window.BeginStroke(7250, new Vector2(.5f, .5f)), Is.True);
            _window.AppendStroke(7250, new Vector2(.6f, .5f));
            Assert.That(_window.EndStroke(7250), Is.True);
            var prepared = _window.PreparedWaypoints.ToArray();
            _window.SetMapFrame(null, null, Vector3.zero, false, false, "", 2, 0,
                "temporary connection gap", "offline:5001");
            CollectionAssert.AreEqual(prepared, _window.PreparedWaypoints);
            Assert.That(_window.NoMapLabel.gameObject.activeSelf, Is.True);
            SetMap();
            CollectionAssert.AreEqual(prepared, _window.PreparedWaypoints);
            _window.SetMapFrame(null, null, Vector3.zero, false, false, "", 3, 0,
                "temporary connection gap", "offline:5001");
            SetMap(session: "session-b");
            Assert.That(_window.PreparedWaypoints, Is.Empty,
                "a real new coordinate identity must still discard the old metre-space route");
            yield return null;
        }

        private PuppyPiPathClient AddInactiveCommandChannel()
        {
            // These components stay inactive: no UDP/HTTP/ROS connection or
            // robot command can be created by a display-only test.
            var controls = new GameObject("Inactive map command fixture");
            controls.SetActive(false);
            controls.transform.SetParent(_fixture.transform, false);
            var controller = controls.AddComponent<PuppyPiRobotController>();
            var path = controls.AddComponent<PuppyPiPathClient>();
            path.operatorSupervisedNavigation = false;
            controller.pathClient = path;
            path.controller = controller;
            _window.controller = controller;
            return path;
        }

        private static List<UIVertex> PreviewVertices(PuppyPiMapPathGraphic preview)
        {
            var result = new List<UIVertex>();
            using (var helper = new VertexHelper())
            {
                typeof(PuppyPiMapPathGraphic).GetMethod("OnPopulateMesh", BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new[] { typeof(VertexHelper) }, null)
                    .Invoke(preview, new object[] { helper });
                helper.GetUIVertexStream(result);
            }
            return result;
        }

        [UnityTest]
        public IEnumerator PreviewUsesRobotWidthForStraightTravelAndRechecksEditedDimensions()
        {
            Create();
            SetMap();
            yield return null;
            _window.ToggleDrawMode();
            Assert.That(_window.BeginStroke(7101, new Vector2(.5f, .5f)), Is.True);
            _window.AppendStroke(7101, new Vector2(.6f, .5f));
            Assert.That(_window.EndStroke(7101), Is.True);
            var cells = new int[200 * 100];
            cells[53 * 200 + 110] = 100; // Side obstacle 0.15 m above the centreline.
            var footprint = new MapRobotFootprint();
            _window.SetNavigationFrame(cells, true, "Fixture scan", .22f, footprint);
            Assert.That(_window.NavigationVerified, Is.True,
                "straight travel must use the supplied lateral body width, not the old 44 cm diameter disk");
            footprint.widthMeters = .4f;
            _window.SetNavigationFrame(cells, true, "Fixture scan", .22f, footprint);
            Assert.That(_window.NavigationVerified, Is.False,
                "an edited footprint must invalidate the preview even when occupancy and object references did not change");
            Assert.That(_window.PreviewBlock.Kind, Is.EqualTo(MapPathBlockKind.Occupied));
        }

        private static bool Covers(List<UIVertex> triangles, Vector2 point)
        {
            for (int i = 0; i + 2 < triangles.Count; i += 3)
            {
                Vector2 a = triangles[i].position, b = triangles[i + 1].position, c = triangles[i + 2].position;
                float ab = Cross(b - a, point - a), bc = Cross(c - b, point - b), ca = Cross(a - c, point - c);
                if ((ab >= 0 && bc >= 0 && ca >= 0) || (ab <= 0 && bc <= 0 && ca <= 0)) return true;
            }
            return false;
        }

        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
    }
}
#endif
