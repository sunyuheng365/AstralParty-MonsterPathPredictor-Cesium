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
                if (box.Rect.Contains(click, margin: 0f))
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
                // Strictly transitive, enumeration-order-independent 3-pass selection:
                // 1) Find global minimum CameraDepth.
                // 2) Within [minDepth, minDepth + CoplanarDepthThreshold], find global minimum center distance.
                // 3) Within [minCenterDist, minCenterDist + 0.001f], break ties deterministically by (CameraDepth, MonsterId).
                float minDepth = float.MaxValue;
                for (int i = 0; i < directHits.Count; i++)
                {
                    if (directHits[i].CameraDepth < minDepth) minDepth = directHits[i].CameraDepth;
                }

                float minCenterDist = float.MaxValue;
                for (int i = 0; i < directHits.Count; i++)
                {
                    var cand = directHits[i];
                    if (cand.CameraDepth > minDepth + CoplanarDepthThreshold) continue;
                    float d = cand.Rect.NormalizedCenterDistanceSq(click);
                    if (d < minCenterDist) minCenterDist = d;
                }

                MonsterHitBox best = null;
                for (int i = 0; i < directHits.Count; i++)
                {
                    var cand = directHits[i];
                    if (cand.CameraDepth > minDepth + CoplanarDepthThreshold) continue;
                    if (cand.Rect.NormalizedCenterDistanceSq(click) > minCenterDist + 0.001f) continue;

                    if (best == null ||
                        cand.CameraDepth < best.CameraDepth ||
                        (cand.CameraDepth == best.CameraDepth && cand.MonsterId < best.MonsterId))
                    {
                        best = cand;
                    }
                }
                return best != null ? best.MonsterId : 0;
            }

            // Near-miss fallback: strictly transitive 3-pass global-minimum selection
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

            float minCenter = float.MaxValue;
            for (int i = 0; i < boxes.Count; i++)
            {
                var box = boxes[i];
                if (box == null) continue;
                float edgeDist = box.Rect.DistanceToPoint(click);
                if (edgeDist > maxDist || edgeDist > minEdgeDist + 1.5f) continue;
                float d = box.Rect.Center.DistanceTo(click);
                if (d < minCenter) minCenter = d;
            }

            MonsterHitBox nearest = null;
            for (int i = 0; i < boxes.Count; i++)
            {
                var box = boxes[i];
                if (box == null) continue;
                float edgeDist = box.Rect.DistanceToPoint(click);
                if (edgeDist > maxDist || edgeDist > minEdgeDist + 1.5f) continue;
                if (box.Rect.Center.DistanceTo(click) > minCenter + 1f) continue;

                if (nearest == null ||
                    box.CameraDepth < nearest.CameraDepth ||
                    (box.CameraDepth == nearest.CameraDepth && box.MonsterId < nearest.MonsterId))
                {
                    nearest = box;
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

            // Scale period proportionally only if an extreme edge would exceed the safety draw budget,
            // ensuring the dashed stroke ALWAYS covers 100% of the visible edge without tail truncation.
            const int maxRenderSegments = 2048;
            float effDash = dashLength;
            float effPeriod = period;
            if (usableLength / effPeriod > maxRenderSegments - 2)
            {
                float scale = usableLength / ((maxRenderSegments - 2) * effPeriod);
                effDash *= scale;
                effPeriod *= scale;
            }

            float shift = 0f;
            if (!float.IsNaN(phaseOffset) && !float.IsInfinity(phaseOffset))
            {
                shift = phaseOffset % effPeriod;
                if (shift < 0f) shift += effPeriod;
            }

            float cursor = usableStart + shift - effPeriod;
            int safety = 0;
            while (cursor < usableEnd - 0.5f && safety++ < maxRenderSegments)
            {
                float segStart = Math.Max(usableStart, cursor);
                float segEnd = Math.Min(usableEnd, cursor + effDash);
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

                cursor += effPeriod;
            }
        }

        /// <summary>
        /// Liang-Barsky 2D segment clipping against an axis-aligned viewport rectangle.
        /// Uses midpoint-centered <c>decimal</c> arithmetic (28+ digits, exact up to 1e27 including H = 2^64)
        /// with a midpoint-centered <c>double</c> fallback for even larger values, avoiding parameter-tie
        /// distortion on extreme off-screen projections like (-2^64, -2^64) -> (2^64, 2^64).
        /// </summary>
        public static bool TryClipSegmentToRect(
            Point2 a,
            Point2 b,
            float minX,
            float minY,
            float maxX,
            float maxY,
            out Point2 clippedA,
            out Point2 clippedB,
            out float startTrimPx)
        {
            bool ok = TryClipSegmentToRect(a, b, minX, minY, maxX, maxY, out clippedA, out clippedB, out double startTrimD);
            startTrimPx = (float)startTrimD;
            return ok;
        }

        public static bool TryClipSegmentToRect(
            Point2 a,
            Point2 b,
            float minX,
            float minY,
            float maxX,
            float maxY,
            out Point2 clippedA,
            out Point2 clippedB,
            out double startTrimPx)
        {
            clippedA = a;
            clippedB = b;
            startTrimPx = 0.0;

            double ax = a.X, ay = a.Y;
            double bx = b.X, by = b.Y;
            double dMinX = minX, dMinY = minY, dMaxX = maxX, dMaxY = maxY;
            if (double.IsNaN(ax) || double.IsInfinity(ax) || double.IsNaN(ay) || double.IsInfinity(ay) ||
                double.IsNaN(bx) || double.IsInfinity(bx) || double.IsNaN(by) || double.IsInfinity(by))
                return false;

            double maxAbs = Math.Max(
                Math.Max(Math.Max(Math.Abs(ax), Math.Abs(ay)), Math.Max(Math.Abs(bx), Math.Abs(by))),
                Math.Max(Math.Max(Math.Abs(dMinX), Math.Abs(dMaxX)), Math.Max(Math.Abs(dMinY), Math.Abs(dMaxY))));

            double dx = bx - ax;
            double dy = by - ay;
            double fullLen = Math.Sqrt(dx * dx + dy * dy);

            if (maxAbs <= 1e26)
            {
                decimal dax = (decimal)ax, day = (decimal)ay;
                decimal dbx = (decimal)bx, dby = (decimal)by;
                decimal dmX = (decimal)dMinX, dmY = (decimal)dMinY, dMX = (decimal)dMaxX, dMY = (decimal)dMaxY;
                decimal mx = (dax + dbx) * 0.5m;
                decimal my = (day + dby) * 0.5m;
                decimal hx = (dbx - dax) * 0.5m;
                decimal hy = (dby - day) * 0.5m;

                decimal s0 = -1m;
                decimal s1 = 1m;
                int hitStartEdge = 0; // 1=minX, 2=maxX, 3=minY, 4=maxY
                int hitEndEdge = 0;

                bool ClipDec(decimal p, decimal q, int edgeId)
                {
                    if (p == 0m) return q >= 0m;
                    // Check half-plane inequality p*s <= q at current endpoints [s0, s1] via multiplication only
                    // before dividing q/p, guaranteeing |q/p| <= 1 and eliminating OverflowException on near-axis segments.
                    decimal f0 = q - p * s0;
                    decimal f1 = q - p * s1;
                    if (p < 0m)
                    {
                        if (f1 < 0m) return false;
                        if (f0 >= 0m) return true;
                        decimal r = q / p;
                        if (r < s0) r = s0;
                        if (r > s1) r = s1;
                        s0 = r;
                        hitStartEdge = edgeId;
                    }
                    else
                    {
                        if (f0 < 0m) return false;
                        if (f1 >= 0m) return true;
                        decimal r = q / p;
                        if (r < s0) r = s0;
                        if (r > s1) r = s1;
                        s1 = r;
                        hitEndEdge = edgeId;
                    }
                    return true;
                }

                if (!ClipDec(-hx, mx - dmX, 1)) return false;
                if (!ClipDec(hx, dMX - mx, 2)) return false;
                if (!ClipDec(-hy, my - dmY, 3)) return false;
                if (!ClipDec(hy, dMY - my, 4)) return false;

                double t0 = (double)((s0 + 1m) * 0.5m);
                startTrimPx = t0 * fullLen;

                decimal x0 = hitStartEdge == 1 ? dmX : (hitStartEdge == 2 ? dMX : (mx + s0 * hx));
                decimal y0 = hitStartEdge == 3 ? dmY : (hitStartEdge == 4 ? dMY : (my + s0 * hy));
                decimal x1 = hitEndEdge == 1 ? dmX : (hitEndEdge == 2 ? dMX : (mx + s1 * hx));
                decimal y1 = hitEndEdge == 3 ? dmY : (hitEndEdge == 4 ? dMY : (my + s1 * hy));

                clippedA = new Point2((float)x0, (float)y0);
                clippedB = new Point2((float)x1, (float)y1);
                return true;
            }

            // Fallback for extreme coordinates > 1e26: anchor parameterization at a point on the segment
            // closest to the viewport center so viewport bounds never suffer catastrophic cancellation
            // against huge far-endpoint coordinates (e.g. (0,0)->(2^100,2^100) vs [0,800]x[0,100]).
            double cx = (dMinX + dMaxX) * 0.5;
            double cy = (dMinY + dMaxY) * 0.5;
            double mxD = (ax + bx) * 0.5;
            double myD = (ay + by) * 0.5;

            double ox = ax, oy = ay, tO = 0.0;
            double bestDist = Math.Max(Math.Abs(ax - cx), Math.Abs(ay - cy));
            double bDist = Math.Max(Math.Abs(bx - cx), Math.Abs(by - cy));
            if (bDist < bestDist)
            {
                ox = bx; oy = by; tO = 1.0; bestDist = bDist;
            }
            double mDist = Math.Max(Math.Abs(mxD - cx), Math.Abs(myD - cy));
            if (mDist < bestDist)
            {
                ox = mxD; oy = myD; tO = 0.5; bestDist = mDist;
            }

            double rectSpan = Math.Max(1.0, Math.Max(dMaxX - dMinX, dMaxY - dMinY));
            double dirScale = Math.Max(Math.Abs(dx), Math.Abs(dy));
            if (bestDist > rectSpan * 16.0 && dirScale > 0.0)
            {
                double sdx = dx / dirScale;
                double sdy = dy / dirScale;
                double denom = dirScale * (sdx * sdx + sdy * sdy);
                if (denom > 0.0 && !double.IsInfinity(denom))
                {
                    double du = ((cx - ox) * sdx + (cy - oy) * sdy) / denom;
                    double minDu = -tO;
                    double maxDu = 1.0 - tO;
                    if (du < minDu) du = minDu;
                    if (du > maxDu) du = maxDu;
                    double px = ox + du * dx;
                    double py = oy + du * dy;
                    double pDist = Math.Max(Math.Abs(px - cx), Math.Abs(py - cy));
                    if (pDist < bestDist)
                    {
                        ox = px; oy = py; tO += du;
                    }
                }
            }

            double u0 = -tO;
            double u1 = 1.0 - tO;
            int dHitStart = 0, dHitEnd = 0;

            bool ClipDbl(double p, double q, int edgeId)
            {
                if (p == 0.0) return q >= 0.0;
                double f0 = q - p * u0;
                double f1 = q - p * u1;
                if (p < 0.0)
                {
                    if (f1 < 0.0) return false;
                    if (f0 >= 0.0) return true;
                    double r = q / p;
                    if (r < u0) r = u0;
                    if (r > u1) r = u1;
                    u0 = r;
                    dHitStart = edgeId;
                }
                else
                {
                    if (f0 < 0.0) return false;
                    if (f1 >= 0.0) return true;
                    double r = q / p;
                    if (r < u0) r = u0;
                    if (r > u1) r = u1;
                    u1 = r;
                    dHitEnd = edgeId;
                }
                return true;
            }

            if (!ClipDbl(-dx, ox - dMinX, 1)) return false;
            if (!ClipDbl(dx, dMaxX - ox, 2)) return false;
            if (!ClipDbl(-dy, oy - dMinY, 3)) return false;
            if (!ClipDbl(dy, dMaxY - oy, 4)) return false;

            double tStart = tO + u0;
            if (tStart < 0.0) tStart = 0.0;
            if (tStart > 1.0) tStart = 1.0;
            startTrimPx = tStart * fullLen;

            double dx0 = dHitStart == 1 ? dMinX : (dHitStart == 2 ? dMaxX : (ox + u0 * dx));
            double dy0 = dHitStart == 3 ? dMinY : (dHitStart == 4 ? dMaxY : (oy + u0 * dy));
            double dx1 = dHitEnd == 1 ? dMinX : (dHitEnd == 2 ? dMaxX : (ox + u1 * dx));
            double dy1 = dHitEnd == 3 ? dMinY : (dHitEnd == 4 ? dMaxY : (oy + u1 * dy));

            clippedA = new Point2((float)dx0, (float)dy0);
            clippedB = new Point2((float)dx1, (float)dy1);
            return true;
        }

        /// <summary>
        /// Clips a 3D world-space segment against the camera near plane defined by
        /// <c>Dot(P - planeOrigin, planeNormal) >= nearDist</c>.
        /// Returns false only when both endpoints lie strictly behind the near plane.
        /// </summary>
        public static bool TryClipSegmentToNearPlane(
            float ax, float ay, float az,
            float bx, float by, float bz,
            float camX, float camY, float camZ,
            float fwdX, float fwdY, float fwdZ,
            float nearDist,
            out float outAx, out float outAy, out float outAz,
            out float outBx, out float outBy, out float outBz,
            out bool clippedStart,
            out bool clippedEnd)
        {
            double dA = ((double)ax - camX) * fwdX + ((double)ay - camY) * fwdY + ((double)az - camZ) * fwdZ - nearDist;
            double dB = ((double)bx - camX) * fwdX + ((double)by - camY) * fwdY + ((double)bz - camZ) * fwdZ - nearDist;

            clippedStart = false;
            clippedEnd = false;
            outAx = ax; outAy = ay; outAz = az;
            outBx = bx; outBy = by; outBz = bz;

            if (dA < 0.0 && dB < 0.0) return false;

            if (dA < 0.0)
            {
                double denom = dB - dA;
                if (denom <= 1e-12) return false;
                double t = -dA / denom;
                outAx = (float)(ax + t * ((double)bx - ax));
                outAy = (float)(ay + t * ((double)by - ay));
                outAz = (float)(az + t * ((double)bz - az));
                clippedStart = true;
            }
            else if (dB < 0.0)
            {
                double denom = dA - dB;
                if (denom <= 1e-12) return false;
                double t = dA / denom;
                outBx = (float)(ax + t * ((double)bx - ax));
                outBy = (float)(ay + t * ((double)by - ay));
                outBz = (float)(az + t * ((double)bz - az));
                clippedEnd = true;
            }

            return true;
        }

        /// <summary>
        /// Computes the dash phase offset for a viewport-clipped sub-segment in double precision,
        /// reducing modulo <paramref name="period"/> before converting to float so huge clip distances
        /// (e.g. 67,108,864 px) never suffer float-rounding phase drift.
        /// </summary>
        public static float ComputeClippedDashPhase(
            double phaseOffset,
            double origTrimStart,
            double clippedFromStartPx,
            double newTrimStart,
            double period = 0.0)
        {
            if (double.IsNaN(phaseOffset) || double.IsInfinity(phaseOffset) ||
                double.IsNaN(origTrimStart) || double.IsInfinity(origTrimStart) ||
                double.IsNaN(clippedFromStartPx) || double.IsInfinity(clippedFromStartPx) ||
                double.IsNaN(newTrimStart) || double.IsInfinity(newTrimStart))
            {
                return 0f;
            }

            if (period > 0.0 && !double.IsNaN(period) && !double.IsInfinity(period))
            {
                // Reduce each signed term individually modulo period before combining so huge
                // clip distances (e.g. 2^60 px) never swamp small offsets like 10 px during subtraction.
                double a = phaseOffset % period;
                double b = origTrimStart % period;
                double c = clippedFromStartPx % period;
                double d = newTrimStart % period;
                double rem = ((a + b) - (c + d)) % period;
                if (rem < 0.0) rem += period;
                return (float)rem;
            }

            double raw = phaseOffset + origTrimStart - clippedFromStartPx - newTrimStart;
            if (double.IsNaN(raw) || double.IsInfinity(raw)) return 0f;
            return (float)raw;
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
