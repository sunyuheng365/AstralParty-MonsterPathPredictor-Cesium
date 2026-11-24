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
    /// Screen-space axis-aligned bounding rectangle of a monster's rendered sprite model.
    /// </summary>
    public struct HitRect2D
    {
        public float MinX;
        public float MinY;
        public float MaxX;
        public float MaxY;

        public HitRect2D(float minX, float minY, float maxX, float maxY)
        {
            MinX = Math.Min(minX, maxX);
            MinY = Math.Min(minY, maxY);
            MaxX = Math.Max(minX, maxX);
            MaxY = Math.Max(minY, maxY);
        }

        public float Width => Math.Max(1f, MaxX - MinX);
        public float Height => Math.Max(1f, MaxY - MinY);
        public Point2 Center => new Point2((MinX + MaxX) * 0.5f, (MinY + MaxY) * 0.5f);

        public bool Contains(Point2 p, float margin = 0f)
        {
            return p.X >= MinX - margin && p.X <= MaxX + margin &&
                   p.Y >= MinY - margin && p.Y <= MaxY + margin;
        }

        public float DistanceToPoint(Point2 p)
        {
            float dx = 0f;
            if (p.X < MinX) dx = MinX - p.X;
            else if (p.X > MaxX) dx = p.X - MaxX;

            float dy = 0f;
            if (p.Y < MinY) dy = MinY - p.Y;
            else if (p.Y > MaxY) dy = p.Y - MaxY;

            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// Normalized squared distance from the rectangle's center (0 at center, 1 at edge midpoints, 2 at corners).
        /// </summary>
        public float NormalizedCenterDistanceSq(Point2 p)
        {
            var c = Center;
            float halfW = Math.Max(1f, Width * 0.5f);
            float halfH = Math.Max(1f, Height * 0.5f);
            float nx = (p.X - c.X) / halfW;
            float ny = (p.Y - c.Y) / halfH;
            return nx * nx + ny * ny;
        }
    }

    public sealed class MonsterHitBox
    {
        public long MonsterId;
        public HitRect2D Rect;
        /// <summary>Camera-forward world depth (smaller = visually in front).</summary>
        public float CameraDepth;
    }

    /// <summary>
    /// Pure managed full-sprite hit tester with accurate disambiguation for stacked/overlapping monsters.
    /// </summary>
    public static class MonsterHitTester
    {
        private const float CoplanarDepthThreshold = 0.35f;

        /// <summary>
        /// Picks the monster clicked at <paramref name="click"/>.
        /// 1. Direct hits inside a monster's projected sprite rectangle always beat near-misses.
        /// 2. When multiple stacked/overlapping monsters contain <paramref name="click"/>:
        ///    - If their camera depths differ significantly (&gt;= 0.35 world units), the front (closer) sprite wins.
        ///    - If they stand side-by-side on the same tile (nearly co-planar), the one whose sprite center
        ///      is closer to the click point wins cleanly.
        /// 3. If no sprite rect directly contains <paramref name="click"/>, falls back to nearest sprite boundary
        ///    within <paramref name="nearMissRadius"/>.
        /// </summary>
        public static long PickMonster(Point2 click, IReadOnlyList<MonsterHitBox> boxes, float nearMissRadius)
        {
            if (boxes == null || boxes.Count == 0) return 0;

            var directHits = new List<MonsterHitBox>();
            for (int i = 0; i < boxes.Count; i++)
            {
                var box = boxes[i];
                if (box == null) continue;
                if (box.Rect.Contains(click, margin: 3f))
                {
                    directHits.Add(box);
                }
            }

            if (directHits.Count == 1)
            {
                return directHits[0].MonsterId;
            }

            if (directHits.Count > 1)
            {
                // Order-independent selection: find global closest depth first, then pick best
                // normalized center distance within the fixed depth tie band (ties by depth, then ID).
                float minDepth = float.MaxValue;
                for (int i = 0; i < directHits.Count; i++)
                {
                    if (directHits[i].CameraDepth < minDepth) minDepth = directHits[i].CameraDepth;
                }

                MonsterHitBox best = null;
                float bestCenterDist = float.MaxValue;
                for (int i = 0; i < directHits.Count; i++)
                {
                    var cand = directHits[i];
                    if (cand.CameraDepth > minDepth + CoplanarDepthThreshold) continue;

                    float candCenterDist = cand.Rect.NormalizedCenterDistanceSq(click);
                    if (best == null ||
                        candCenterDist < bestCenterDist - 0.001f ||
                        (Math.Abs(candCenterDist - bestCenterDist) <= 0.001f &&
                         (cand.CameraDepth < best.CameraDepth - 0.0001f ||
                          (Math.Abs(cand.CameraDepth - best.CameraDepth) <= 0.0001f && cand.MonsterId < best.MonsterId))))
                    {
                        best = cand;
                        bestCenterDist = candCenterDist;
                    }
                }
                return best != null ? best.MonsterId : 0;
            }

            // Near-miss fallback: global closest distance to any sprite bounding box boundary
            float maxDist = Math.Max(0f, nearMissRadius);
            if (maxDist <= 0f) return 0;

            float minEdgeDist = maxDist;
            for (int i = 0; i < boxes.Count; i++)
            {
                var box = boxes[i];
                if (box == null) continue;
                float edgeDist = box.Rect.DistanceToPoint(click);
                if (edgeDist < minEdgeDist) minEdgeDist = edgeDist;
            }

            MonsterHitBox nearest = null;
            float bestCenter = float.MaxValue;
            for (int i = 0; i < boxes.Count; i++)
            {
                var box = boxes[i];
                if (box == null) continue;
                float edgeDist = box.Rect.DistanceToPoint(click);
                if (edgeDist > maxDist || edgeDist > minEdgeDist + 1.5f) continue;

                float dCand = box.Rect.Center.DistanceTo(click);
                if (nearest == null ||
                    dCand < bestCenter - 1f ||
                    (Math.Abs(dCand - bestCenter) <= 1f &&
                     (box.CameraDepth < nearest.CameraDepth - 0.0001f ||
                      (Math.Abs(box.CameraDepth - nearest.CameraDepth) <= 0.0001f && box.MonsterId < nearest.MonsterId))))
                {
                    nearest = box;
                    bestCenter = dCand;
                }
            }

            return nearest != null ? nearest.MonsterId : 0;
        }
    }

    public readonly struct DashQuad
    {
        public readonly Point2 P0;
        public readonly Point2 P1;
        public readonly Point2 P2;
        public readonly Point2 P3;

        public DashQuad(Point2 p0, Point2 p1, Point2 p2, Point2 p3)
        {
            P0 = p0;
            P1 = p1;
            P2 = p2;
            P3 = p3;
        }
    }

    public readonly struct ArrowTriangle
    {
        public readonly Point2 P0;
        public readonly Point2 P1;
        public readonly Point2 P2;

        public ArrowTriangle(Point2 p0, Point2 p1, Point2 p2)
        {
            P0 = p0;
            P1 = p1;
            P2 = p2;
        }
    }

    /// <summary>
    /// Pure 2D screen-space animated dashed line & directional arrowhead polygon generator.
    /// Guarantees positive signed area on all polygons for FairyGUI PolygonMesh ear-clipping.
    /// </summary>
    public static class DashGeometry
    {
        /// <summary>
        /// Zero-allocation helper: appends positive-winding dash quads directly into <paramref name="output"/>.
        /// </summary>
        public static void BuildDashedSegmentsInto(
            List<DashQuad> output,
            Point2 start,
            Point2 end,
            float dashLength,
            float gapLength,
            float thickness,
            float trimStart = 0f,
            float trimEnd = 0f,
            float phaseOffset = 0f)
        {
            if (output == null) return;
            output.Clear();
            if (dashLength <= 0.5f || thickness <= 0.25f) return;
            gapLength = Math.Max(0f, gapLength);

            float dx = end.X - start.X;
            float dy = end.Y - start.Y;
            float totalLength = (float)Math.Sqrt(dx * dx + dy * dy);
            if (float.IsNaN(totalLength) || float.IsInfinity(totalLength) || totalLength <= 1f)
                return;

            float usableStart = Math.Max(0f, trimStart);
            float usableEnd = Math.Max(usableStart, totalLength - Math.Max(0f, trimEnd));
            float usableLength = usableEnd - usableStart;
            if (usableLength < 2f) return;

            float ux = dx / totalLength;
            float uy = dy / totalLength;
            float nx = -uy * (thickness * 0.5f);
            float ny = ux * (thickness * 0.5f);

            float period = dashLength + gapLength;
            if (period <= 0.5f) return;

            float shift = 0f;
            if (!float.IsNaN(phaseOffset) && !float.IsInfinity(phaseOffset))
            {
                shift = phaseOffset % period;
                if (shift < 0f) shift += period;
            }

            float cursor = usableStart + shift - period;
            int safety = 0;
            while (cursor < usableEnd - 0.5f && safety++ < 256)
            {
                float segStart = Math.Max(usableStart, cursor);
                float segEnd = Math.Min(usableEnd, cursor + dashLength);
                if (segEnd - segStart >= 1.5f)
                {
                    float x0 = start.X + ux * segStart;
                    float y0 = start.Y + uy * segStart;
                    float x1 = start.X + ux * segEnd;
                    float y1 = start.Y + uy * segEnd;

                    var a = new Point2(x0 + nx, y0 + ny);
                    var b = new Point2(x1 + nx, y1 + ny);
                    var c = new Point2(x1 - nx, y1 - ny);
                    var d = new Point2(x0 - nx, y0 - ny);
                    output.Add(MakePositiveWinding(a, b, c, d));
                }

                cursor += period;
            }
        }

        private static DashQuad MakePositiveWinding(Point2 a, Point2 b, Point2 c, Point2 d)
        {
            double area2 =
                (double)a.X * b.Y - (double)b.X * a.Y +
                (double)b.X * c.Y - (double)c.X * b.Y +
                (double)c.X * d.Y - (double)d.X * c.Y +
                (double)d.X * a.Y - (double)a.X * d.Y;
            return area2 < 0.0 ? new DashQuad(d, c, b, a) : new DashQuad(a, b, c, d);
        }

        /// <summary>
        /// Zero-allocation arrowhead helper returning a positive-winding triangle.
        /// </summary>
        public static bool TryBuildArrowHead(
            Point2 start,
            Point2 end,
            float arrowLength,
            float arrowHalfWidth,
            out ArrowTriangle triangle,
            float tipRatio = 0.62f)
        {
            triangle = default;
            if (arrowLength <= 1f || arrowHalfWidth <= 0.5f) return false;

            float dx = end.X - start.X;
            float dy = end.Y - start.Y;
            float length = (float)Math.Sqrt(dx * dx + dy * dy);
            if (float.IsNaN(length) || float.IsInfinity(length) || length < 6f) return false;

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

            var p0 = tip;
            var p1 = new Point2(baseX + nx, baseY + ny);
            var p2 = new Point2(baseX - nx, baseY - ny);
            double area2 =
                (double)p0.X * p1.Y - (double)p1.X * p0.Y +
                (double)p1.X * p2.Y - (double)p2.X * p1.Y +
                (double)p2.X * p0.Y - (double)p0.X * p2.Y;
            triangle = area2 < 0.0 ? new ArrowTriangle(p2, p1, p0) : new ArrowTriangle(p0, p1, p2);
            return true;
        }

        /// <summary>
        /// Generates a sequence of 4-vertex dash quads along <paramref name="start"/> -&gt; <paramref name="end"/>.
        /// Increasing <paramref name="phaseOffset"/> smoothly marches the dashes forward in the travel direction.
        /// </summary>
        public static List<Point2[]> BuildDashedSegments(
            Point2 start,
            Point2 end,
            float dashLength,
            float gapLength,
            float thickness,
            float trimStart = 0f,
            float trimEnd = 0f,
            float phaseOffset = 0f)
        {
            var raw = new List<DashQuad>();
            BuildDashedSegmentsInto(raw, start, end, dashLength, gapLength, thickness, trimStart, trimEnd, phaseOffset);
            var quads = new List<Point2[]>(raw.Count);
            for (int i = 0; i < raw.Count; i++)
            {
                var q = raw[i];
                quads.Add(new[] { q.P0, q.P1, q.P2, q.P3 });
            }
            return quads;
        }

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
