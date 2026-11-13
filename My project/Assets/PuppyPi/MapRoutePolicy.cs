using System;
using UnityEngine;

namespace PuppyPi
{
    // Operator-supervised routes avoid confirmed current obstacles. Missing
    // measurements and the edge of the displayed crop do not form walls.
    // This derived grid never changes the displayed or accumulated map.
    public static class MapRoutePolicy
    {
        public const int MaximumCells = 1024 * 1024;

        public static bool TryBuild(MapGridGeometry source, int[] confirmedCells,
            MapRobotFootprint footprint, out MapGridGeometry geometry,
            out int[] cells, out string error)
        {
            geometry = null;
            cells = null;
            error = null;
            if (source == null || !source.IsValid || footprint == null || !footprint.IsValid)
                return Fail("Valid map coordinates and robot dimensions are required.", out error);
            long sourceCount = (long)source.width * source.height;
            if (sourceCount > MaximumCells ||
                (confirmedCells != null && confirmedCells.LongLength != sourceCount))
                return Fail("The current obstacle grid has invalid dimensions or exceeds the route grid budget.", out error);
            if (confirmedCells != null)
                foreach (int value in confirmedCells)
                    if (value < -1 || value > 100)
                        return Fail("The current obstacle grid contains an invalid cell value.", out error);

            // The extra half metre covers the existing 0.46 m frontal guard.
            // Keep complete footprints inside the derived grid so a crop-edge
            // result cannot hide an occupied cell elsewhere on the same leg.
            double padding = Math.Ceiling(((double)footprint.RotationRadius + .5) / source.resolution);
            double width = source.width + 2 * padding;
            double height = source.height + 2 * padding;
            if (padding < 1 || padding > int.MaxValue || width > int.MaxValue || height > int.MaxValue ||
                width * height > MaximumCells)
                return Fail("The padded obstacle grid exceeds the route grid budget; reduce the displayed map extent.", out error);
            int pad = (int)padding, paddedWidth = (int)width, paddedHeight = (int)height;
            var result = new MapGridGeometry {
                width = paddedWidth, height = paddedHeight, resolution = source.resolution,
                origin = source.origin - new Vector2(pad * source.resolution, pad * source.resolution),
                frame = source.frame
            };
            if (!result.IsValid)
                return Fail("The padded obstacle grid has invalid coordinates.", out error);
            var padded = new int[paddedWidth * paddedHeight];
            if (confirmedCells != null)
                for (int y = 0; y < source.height; y++)
                    for (int x = 0; x < source.width; x++)
                        if (confirmedCells[y * source.width + x] >= 50)
                            padded[(y + pad) * paddedWidth + x + pad] = 100;
            geometry = result;
            cells = padded;
            return true;
        }

        private static bool Fail(string message, out string error)
        { error = message; return false; }
    }
}
