using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEditor;

namespace PuppyPi.Tests
{
    public class PuppyPiVrPanelTests
    {
        private GameObject _root;

        [TearDown]
        public void Cleanup()
        {
            if (_root != null) Object.DestroyImmediate(_root);
        }

        private PuppyPiVrPanel Panel()
        {
            _root = new GameObject("VR panel fixture", typeof(EventSystem));
            _root.AddComponent<PointableCanvasModule>();
            var panel = _root.AddComponent<PuppyPiVrPanel>();
            panel.Build();
            return panel;
        }

        [Test]
        public void NativeCanvasSharesOneBoundedSurfaceForHandPokeAndRay()
        {
            var panel = Panel();
            var canvas = panel.GetComponentInChildren<Canvas>(true);
            var pointable = panel.GetComponentInChildren<PointableCanvas>(true);
            var poke = panel.GetComponentInChildren<PokeInteractable>(true);
            var ray = panel.GetComponentInChildren<RayInteractable>(true);
            var clipper = panel.GetComponentInChildren<BoundsClipper>(true);
            Assert.That(canvas.renderMode, Is.EqualTo(RenderMode.WorldSpace));
            Assert.That(canvas.GetComponent<GraphicRaycaster>(), Is.Not.Null);
            Assert.That(pointable.Canvas, Is.SameAs(canvas));
            Assert.That(poke.SurfacePatch, Is.SameAs(ray.Surface));
            Assert.That(poke.PointableElement, Is.SameAs(pointable));
            Assert.That(ray.PointableElement, Is.SameAs(pointable));
            Assert.That(clipper.Size.x, Is.EqualTo(((RectTransform)canvas.transform).sizeDelta.x));
            Assert.That(clipper.Size.y, Is.EqualTo(((RectTransform)canvas.transform).sizeDelta.y));
            Assert.That(panel.CameraView.GetComponentInParent<Canvas>(), Is.SameAs(canvas));
            Assert.That(panel.CameraView.transform.parent.name, Is.EqualTo("Camera frame"));
            Assert.That(_root.GetComponent<PointableCanvasModule>().ExclusiveMode, Is.True);
        }

        [Test]
        public void DashboardGrabUsesMeterScaleWrapperAndOnlyTheOuterRim()
        {
            var panel = Panel();
            Transform target = panel.PanelRoot;
            var canvas = panel.GetComponentInChildren<Canvas>(true);
            var body = target.GetComponent<Rigidbody>();
            var grab = target.GetComponent<Grabbable>();
            var hand = target.GetComponent<Oculus.Interaction.HandGrab.HandGrabInteractable>();
            var controllerGrab = target.GetComponent<GrabInteractable>();
            Assert.That(target.localScale, Is.EqualTo(Vector3.one));
            Assert.That(canvas.transform.parent, Is.SameAs(target));
            Assert.That(canvas.transform.localScale, Is.EqualTo(Vector3.one * panel.metersPerPixel));
            Assert.That(body.isKinematic, Is.True);
            Assert.That(body.useGravity, Is.False);
            Assert.That(grab.Transform, Is.SameAs(target));
            Assert.That(grab.MaxGrabPoints, Is.EqualTo(2));
            Assert.That(new SerializedObject(grab).FindProperty("_transferOnSecondSelection").boolValue, Is.False);
            Assert.That(new SerializedObject(grab).FindProperty("_throwWhenUnselected").boolValue, Is.False);
            Assert.That(hand.PointableElement, Is.SameAs(grab));
            Assert.That(controllerGrab.PointableElement, Is.SameAs(grab));
#pragma warning disable CS0618
            var limits = target.GetComponent<TwoGrabFreeTransformer>().Constraints;
            Assert.That(limits.ConstraintsAreRelative, Is.False);
            Assert.That(limits.MinScale.Constrain && limits.MaxScale.Constrain, Is.True);
            Assert.That(limits.MinScale.Value, Is.EqualTo(0.35f));
            Assert.That(limits.MaxScale.Value, Is.EqualTo(2f));
            Assert.That(limits.ConstrainXScale && limits.ConstrainYScale && limits.ConstrainZScale, Is.True);
#pragma warning restore CS0618
            var colliders = target.GetComponentsInChildren<BoxCollider>(true);
            Assert.That(colliders.Length, Is.EqualTo(4));
            Vector2 halfSize = ((RectTransform)canvas.transform).sizeDelta * panel.metersPerPixel / 2;
            foreach (var collider in colliders)
            {
                Assert.That(collider.isTrigger, Is.True);
                var bounds = new Bounds(collider.transform.localPosition + collider.center, collider.size);
                bool outside = bounds.max.x <= -halfSize.x + 0.0001f || bounds.min.x >= halfSize.x - 0.0001f ||
                    bounds.max.y <= -halfSize.y + 0.0001f || bounds.min.y >= halfSize.y - 0.0001f;
                Assert.That(outside, Is.True, "grab colliders must not cover the dashboard's buttons");
            }
            var rims = target.GetComponentsInChildren<Image>(true).Where(image => image.name.EndsWith("dashboard rim")).ToArray();
            Assert.That(rims.Length, Is.EqualTo(4));
            Assert.That(rims.All(image => !image.raycastTarget), Is.True);
        }

        [Test]
        public void RecenterResetsDashboardPoseAndPreservesItsChosenSize()
        {
            var panel = Panel();
            var camera = new GameObject("Dashboard recenter viewer", typeof(Camera));
            camera.transform.SetParent(_root.transform, false);
            camera.tag = "MainCamera";
            panel.PanelRoot.localScale = Vector3.one * 1.4f;
            panel.PanelRoot.localPosition = new Vector3(0.2f, -0.1f, 0.3f);
            panel.PanelRoot.localRotation = Quaternion.Euler(10, 20, 30);
            panel.Recenter();
            Assert.That(panel.PanelRoot.localPosition, Is.EqualTo(Vector3.zero));
            Assert.That(panel.PanelRoot.localRotation, Is.EqualTo(Quaternion.identity));
            Assert.That(panel.PanelRoot.localScale, Is.EqualTo(Vector3.one * 1.4f));
            Assert.That(panel.PanelRoot.gameObject.activeSelf, Is.True);
            Assert.That(panel.IsGrabbed, Is.False);
        }

        [Test]
        public void HoverDoesNotSelectOrRunPoseAndClickOnlySelectsIt()
        {
            var panel = Panel();
            string pose = RobotCommandCatalog.PoseNames[0];
            var button = panel.GetComponentsInChildren<Button>(true)
                .Single(item => item.gameObject.name == pose.Replace('_', ' ').ToUpperInvariant());
            var pointer = new PointerEventData(_root.GetComponent<EventSystem>())
            {
                button = PointerEventData.InputButton.Left
            };
            var posesTab = panel.GetComponentsInChildren<Button>(true)
                .Single(item => item.gameObject.name == "POSES");
            ExecuteEvents.Execute(posesTab.gameObject, pointer, ExecuteEvents.pointerClickHandler);
            Assert.That(button.IsActive(), Is.True, "open the poses page as a user would before selecting a pose");
            var selected = typeof(PuppyPiVrPanel).GetField("_selectedPose", BindingFlags.Instance | BindingFlags.NonPublic);
            ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerEnterHandler);
            Assert.That(selected.GetValue(panel), Is.Null, "hover must never choose or execute a command");
            ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerClickHandler);
            Assert.That(selected.GetValue(panel), Is.EqualTo(pose));
            Assert.That(panel.GetComponentsInChildren<Button>(true)
                .Single(item => item.gameObject.name == "APPLY SELECTED POSE").interactable, Is.False,
                "selection cannot execute a pose without an available controller and motion permission");
        }

        [Test]
        public void MapTabDoesNotChangePoseOrConnectionPageRouting()
        {
            var panel = Panel();
            var pointer = new PointerEventData(_root.GetComponent<EventSystem>())
            {
                button = PointerEventData.InputButton.Left
            };
            var pages = (GameObject[])typeof(PuppyPiVrPanel)
                .GetField("_pages", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
            Assert.That(pages.Length, Is.EqualTo(5));
            Assert.That(panel.MapView.transform.parent.name, Is.EqualTo("Map frame"));
            Assert.That(panel.MapView.gameObject.activeInHierarchy, Is.False);
            foreach (string name in new[] { "MAP", "POSES", "ACTIONS", "CONNECT", "DASHBOARD" })
            {
                var tab = panel.GetComponentsInChildren<Button>(true)
                    .Single(item => item.gameObject.name == name);
                ExecuteEvents.Execute(tab.gameObject, pointer, ExecuteEvents.pointerClickHandler);
                Assert.That(pages.Single(page => page.activeSelf).name, Is.EqualTo(name + " page"));
                Assert.That(panel.MapView.gameObject.activeInHierarchy, Is.EqualTo(name == "MAP"));
                Assert.That(panel.CameraView.gameObject.activeInHierarchy, Is.EqualTo(name == "DASHBOARD"));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MediaPreservesImageAspectAndReportsNoSignalAtTwoSeconds(bool map)
        {
            var panel = Panel();
            var image = map ? panel.MapView : panel.CameraView;
            var notice = (Text)typeof(PuppyPiVrPanel)
                .GetField(map ? "_mapNotice" : "_cameraNotice", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
            var refresh = typeof(PuppyPiVrPanel).GetMethod("RefreshMedia", BindingFlags.Static | BindingFlags.NonPublic);
            var texture = new Texture2D(16, 9, TextureFormat.RGB24, false);
            try
            {
                refresh.Invoke(null, new object[] { image, notice, texture, true, "Streaming", 1.9, map ? "LIDAR MAP" : "ROBOT CAMERA" });
                Assert.That(notice.text, Does.StartWith("LIVE "));
                Assert.That(image.GetComponent<AspectRatioFitter>().aspectRatio, Is.EqualTo(16f / 9f).Within(0.0001));
                Assert.That(image.uvRect, Is.EqualTo(new Rect(0, 0, 1, 1)), "the server-rendered image must not be cropped or flipped");
                Assert.That(image.transform.localRotation, Is.EqualTo(Quaternion.identity));
                refresh.Invoke(null, new object[] { image, notice, texture, true, "Reconnecting", 2.0, "MEDIA" });
                Assert.That(notice.text, Does.StartWith("NO SIGNAL"), "an old frame cannot remain labeled live");
                Assert.That(notice.text, Does.Contain("STALE"));
                refresh.Invoke(null, new object[] { image, notice, null, false, "Waiting", double.PositiveInfinity, "MEDIA" });
                Assert.That(notice.text, Does.StartWith("NO SIGNAL"));
                Assert.That(image.texture, Is.Null);
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }
    }
}
