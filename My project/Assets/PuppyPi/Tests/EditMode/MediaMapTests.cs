using NUnit.Framework;
using UnityEngine;

namespace PuppyPi.Tests
{
    public class MediaMapTests
    {
        [Test]
        public void MapMetersProjectIntoCroppedMediaImageWithUpwardPositiveY()
        {
            var map = new RobotMapInfo {
                width_px = 321, height_px = 232, left_m = -7.4f, bottom_m = -7.4f,
                px_per_m = 20, resolution_m = 0.05f
            };
            Assert.That(PuppyPiMapProjection.TryProject(map, new Vector2(-7.4f, -7.4f), out Vector2 origin), Is.True);
            Assert.That(origin.x, Is.EqualTo(0).Within(0.001));
            Assert.That(origin.y, Is.EqualTo(232).Within(0.001));
            Assert.That(PuppyPiMapProjection.TryProject(map, new Vector2(-6.4f, -6.4f), out Vector2 offset), Is.True);
            Assert.That(offset.x, Is.EqualTo(20).Within(0.001));
            Assert.That(offset.y, Is.EqualTo(212).Within(0.001));
            Assert.That(PuppyPiMapProjection.TryProject(map, new Vector2(0.10625f, 0.03125f), out Vector2 robot), Is.True);
            Assert.That(robot.x, Is.EqualTo(150.125).Within(0.001));
            Assert.That(robot.y, Is.EqualTo(83.375).Within(0.001));
        }

        [Test]
        public void ProjectionRejectsInvalidScaleAndNonfiniteCoordinates()
        {
            var map = new RobotMapInfo { width_px = 321, height_px = 232, px_per_m = 20 };
            Assert.That(PuppyPiMapProjection.TryProject(map, new Vector2(float.NaN, 0), out _), Is.False);
            map.px_per_m = 0;
            Assert.That(PuppyPiMapProjection.TryProject(map, Vector2.zero, out _), Is.False);
            map.px_per_m = 20;
            map.left_m = float.PositiveInfinity;
            Assert.That(PuppyPiMapProjection.TryProject(map, Vector2.zero, out _), Is.False);
        }
    }
}
