using System;
using System.Collections.Generic;

namespace MonsterPathPredictor.Core
{
    public struct Point2
    {
        public float X;
        public float Y;

        public Point2(float x, float y)
        {
            X = x;
            Y = y;
        }

        public float DistanceTo(Point2 other)
        {
            float dx = other.X - X;
            float dy = other.Y - Y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }
    }

    /// <summary>
    /// Pure 2D screen-space dashed line & directional arrowhead polygon generator.
    /// Guarantees positive signed area on all polygons for FairyGUI PolygonMesh ear-clipping.
    /// </summary>
    public static class DashGeometry
    {
        /// <summary>
        /// Generates a sequence of 4-vertex dash quads along the segment from <paramref name="start"/>
        /// to <paramref name="end"/>.
        /// </summary>
        public static List<Point2[]> BuildDashedSegments(
            Point2 start,
            Point2 end,
            float dashLength,
            float gapLength,
            float thickness,
            float trimStart = 0f,
            float trimEnd = 0f)
        {
            var quads = new List<Point2[]>();
            if (dashLength <= 0.5f || thickness <= 0.25f) return quads;
            gapLength = Math.Max(0f, gapLength);

            float dx = end.X - start.X;
            float dy = end.Y - start.Y;
            float totalLength = (float)Math.Sqrt(dx * dx + dy * dy);
            if (float.IsNaN(totalLength) || float.IsInfinity(totalLength) || totalLength <= 1f)
            {
                return quads;
            }

            float usableStart = Math.Max(0f, trimStart);
            float usableEnd = Math.Max(usableStart, totalLength - Math.Max(0f, trimEnd));
            float usableLength = usableEnd - usableStart;
            if (usableLength < 2f) return quads;

            float ux = dx / totalLength;
            float uy = dy / totalLength;
            float nx = -uy * (thickness * 0.5f);
            float ny = ux * (thickness * 0.5f);

            float period = dashLength + gapLength;
            if (period <= 0.5f) return quads;

            float cursor = usableStart;
            int safety = 0;
            while (cursor < usableEnd - 0.5f && safety++ < 256)
            {
                float segEnd = Math.Min(usableEnd, cursor + dashLength);
                if (segEnd - cursor < 1.5f) break;

                float x0 = start.X + ux * cursor;
                float y0 = start.Y + uy * cursor;
                float x1 = start.X + ux * segEnd;
                float y1 = start.Y + uy * segEnd;

                var quad = EnsurePositiveWinding(new[]
                {
                    new Point2(x0 + nx, y0 + ny),
                    new Point2(x1 + nx, y1 + ny),
                    new Point2(x1 - nx, y1 - ny),
                    new Point2(x0 - nx, y0 - ny)
                });
                quads.Add(quad);

                cursor += period;
            }

            return quads;
        }

        /// <summary>
        /// Generates a crisp 3-vertex directional arrowhead positioned along <paramref name="start"/> -> <paramref name="end"/>.
        /// </summary>
        public static Point2[] BuildArrowHead(
            Point2 start,
            Point2 end,
            float arrowLength,
            float arrowHalfWidth,
            float tipRatio = 0.62f)
        {
            if (arrowLength <= 1f || arrowHalfWidth <= 0.5f) return Array.Empty<Point2>();

            float dx = end.X - start.X;
            float dy = end.Y - start.Y;
            float length = (float)Math.Sqrt(dx * dx + dy * dy);
            if (float.IsNaN(length) || float.IsInfinity(length) || length < 6f) return Array.Empty<Point2>();

            float ux = dx / length;
            float uy = dy / length;
            float clampedRatio = Math.Max(0.25f, Math.Min(0.92f, tipRatio));
            float effectiveLen = Math.Min(arrowLength, length * 0.45f);
            float effectiveHalfW = Math.Min(arrowHalfWidth, effectiveLen * 0.85f);

            float tipDist = length * clampedRatio;
            var tip = new Point2(start.X + ux * tipDist, start.Y + uy * tipDist);
            float baseX = tip.X - ux * effectiveLen;
            float baseY = tip.Y - uy * effectiveLen;
            float nx = -uy * effectiveHalfW;
            float ny = ux * effectiveHalfW;

            return EnsurePositiveWinding(new[]
            {
                tip,
                new Point2(baseX + nx, baseY + ny),
                new Point2(baseX - nx, baseY - ny)
            });
        }

        /// <summary>
        /// Computes a screen-space label anchor offset slightly perpendicular to the edge so fork exit labels never overlap.
        /// </summary>
        public static Point2 ComputeEdgeLabelAnchor(Point2 from, Point2 to, float alongRatio = 0.52f, float lateralOffset = 0f)
        {
            float dx = to.X - from.X;
            float dy = to.Y - from.Y;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            float mx = from.X + dx * alongRatio;
            float my = from.Y + dy * alongRatio;
            if (len < 1f || Math.Abs(lateralOffset) < 0.01f) return new Point2(mx, my);

            float nx = -dy / len;
            float ny = dx / len;
            return new Point2(mx + nx * lateralOffset, my + ny * lateralOffset);
        }

        public static float SignedArea(Point2[] points)
        {
            if (points == null || points.Length < 3) return 0f;
            float sum = 0f;
            for (int i = 0; i < points.Length; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Length];
                sum += a.X * b.Y - b.X * a.Y;
            }
            return sum * 0.5f;
        }

        private static Point2[] EnsurePositiveWinding(Point2[] points)
        {
            if (SignedArea(points) < 0f) Array.Reverse(points);
            return points;
        }
    }
}
