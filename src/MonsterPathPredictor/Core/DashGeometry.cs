using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

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
            bool ok = TryClipSegmentToRect(a, b, minX, minY, maxX, maxY, out clippedA, out clippedB, out double trimHi, out double trimLo);
            startTrimPx = trimHi + trimLo;
            return ok;
        }

        /// <summary>
        /// Exact rational Liang-Barsky clipping over the true binary IEEE-754 float values of all 8 inputs.
        /// Eliminates both decimal 15-digit conversion drift (e.g. H=2^57) and double anchor cancellation
        /// (e.g. H=2^84, 2^100), and outputs the clipped start distance as a 106-bit double-double
        /// (<paramref name="startTrimHi"/> + <paramref name="startTrimLo"/>) so huge clip distances
        /// (e.g. 2^60 - 40 px) retain exact dash phase modulo period.
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
            out double startTrimHi,
            out double startTrimLo)
        {
            return TryClipSegmentToRectCore(
                a, b, minX, minY, maxX, maxY, 0.0,
                out clippedA, out clippedB,
                out startTrimHi, out startTrimLo,
                out _, out _);
        }

        /// <summary>
        /// Exact rational Liang-Barsky clipping that also returns:
        /// - <paramref name="startTrimModPeriod"/>: exact clipped start length modulo <paramref name="dashPeriod"/>
        ///   computed in arbitrary precision so huge coordinates (e.g. 2^113 horizontal, sqrt(2)*2^120 diagonal)
        ///   never lose dash phase;
        /// - <paramref name="endTrimPx"/>: direct clipped distance from <paramref name="b"/> to <paramref name="clippedB"/>
        ///   (exact 0 when <paramref name="b"/> is not clipped).
        /// </summary>
        public static bool TryClipSegmentToRect(
            Point2 a,
            Point2 b,
            float minX,
            float minY,
            float maxX,
            float maxY,
            double dashPeriod,
            out Point2 clippedA,
            out Point2 clippedB,
            out double startTrimPx,
            out double startTrimModPeriod,
            out double endTrimPx)
        {
            bool ok = TryClipSegmentToRectCore(
                a, b, minX, minY, maxX, maxY, dashPeriod,
                out clippedA, out clippedB,
                out double trimHi, out double trimLo,
                out startTrimModPeriod, out endTrimPx);
            startTrimPx = trimHi + trimLo;
            return ok;
        }

        private static bool TryClipSegmentToRectCore(
            Point2 a,
            Point2 b,
            float minX,
            float minY,
            float maxX,
            float maxY,
            double dashPeriod,
            out Point2 clippedA,
            out Point2 clippedB,
            out double startTrimHi,
            out double startTrimLo,
            out double startTrimModPeriod,
            out double endTrimPx)
        {
            clippedA = a;
            clippedB = b;
            startTrimHi = 0.0;
            startTrimLo = 0.0;
            startTrimModPeriod = 0.0;
            endTrimPx = 0.0;

            if (float.IsNaN(a.X) || float.IsInfinity(a.X) || float.IsNaN(a.Y) || float.IsInfinity(a.Y) ||
                float.IsNaN(b.X) || float.IsInfinity(b.X) || float.IsNaN(b.Y) || float.IsInfinity(b.Y) ||
                float.IsNaN(minX) || float.IsInfinity(minX) || float.IsNaN(minY) || float.IsInfinity(minY) ||
                float.IsNaN(maxX) || float.IsInfinity(maxX) || float.IsNaN(maxY) || float.IsInfinity(maxY) ||
                minX > maxX || minY > maxY)
            {
                return false;
            }

            // Decode exact binary IEEE-754 single representation: v = (-1)^neg * mant * 2^exp
            DecodeFloat(a.X, out bool axNeg, out uint axM, out int axE);
            DecodeFloat(a.Y, out bool ayNeg, out uint ayM, out int ayE);
            DecodeFloat(b.X, out bool bxNeg, out uint bxM, out int bxE);
            DecodeFloat(b.Y, out bool byNeg, out uint byM, out int byE);
            DecodeFloat(minX, out bool mnXNeg, out uint mnXM, out int mnXE);
            DecodeFloat(minY, out bool mnYNeg, out uint mnYM, out int mnYE);
            DecodeFloat(maxX, out bool mxXNeg, out uint mxXM, out int mxXE);
            DecodeFloat(maxY, out bool mxYNeg, out uint mxYM, out int mxYE);

            int baseExp = int.MaxValue;
            void MinExp(uint m, int e) { if (m != 0 && e < baseExp) baseExp = e; }
            MinExp(axM, axE); MinExp(ayM, ayE); MinExp(bxM, bxE); MinExp(byM, byE);
            MinExp(mnXM, mnXE); MinExp(mnYM, mnYE); MinExp(mxXM, mxXE); MinExp(mxYM, mxYE);
            if (baseExp == int.MaxValue)
            {
                clippedA = new Point2(0f, 0f);
                clippedB = new Point2(0f, 0f);
                return true;
            }

            ExactInt eAx = ExactInt.FromShiftedUint(axNeg, axM, axM == 0 ? 0 : axE - baseExp);
            ExactInt eAy = ExactInt.FromShiftedUint(ayNeg, ayM, ayM == 0 ? 0 : ayE - baseExp);
            ExactInt eBx = ExactInt.FromShiftedUint(bxNeg, bxM, bxM == 0 ? 0 : bxE - baseExp);
            ExactInt eBy = ExactInt.FromShiftedUint(byNeg, byM, byM == 0 ? 0 : byE - baseExp);
            ExactInt eMinX = ExactInt.FromShiftedUint(mnXNeg, mnXM, mnXM == 0 ? 0 : mnXE - baseExp);
            ExactInt eMinY = ExactInt.FromShiftedUint(mnYNeg, mnYM, mnYM == 0 ? 0 : mnYE - baseExp);
            ExactInt eMaxX = ExactInt.FromShiftedUint(mxXNeg, mxXM, mxXM == 0 ? 0 : mxXE - baseExp);
            ExactInt eMaxY = ExactInt.FromShiftedUint(mxYNeg, mxYM, mxYM == 0 ? 0 : mxYE - baseExp);

            ExactInt eDx = ExactInt.Sub(eBx, eAx);
            ExactInt eDy = ExactInt.Sub(eBy, eAy);

            // Exact rational interval [t0N/t0D, t1N/t1D] with t0D > 0, t1D > 0 (initially [0/1, 1/1]).
            ExactInt t0N = ExactInt.Zero, t0D = ExactInt.One;
            ExactInt t1N = ExactInt.One, t1D = ExactInt.One;
            int hitStartEdge = 0; // 1=minX, 2=maxX, 3=minY, 4=maxY
            int hitEndEdge = 0;

            bool ClipExact(ExactInt p, ExactInt q, int edgeId)
            {
                if (p.IsZero) return !q.IsNegative;
                // Candidate root r = q / p; normalize denominator > 0 as rN / rD.
                ExactInt rN = p.IsNegative ? q.Negate() : q;
                ExactInt rD = p.Abs();
                if (p.IsNegative)
                {
                    // Constraint: t >= rN / rD
                    if (ExactInt.CompareRat(rN, rD, t1N, t1D) > 0) return false;
                    if (ExactInt.CompareRat(rN, rD, t0N, t0D) > 0)
                    {
                        t0N = rN; t0D = rD; hitStartEdge = edgeId;
                    }
                }
                else
                {
                    // Constraint: t <= rN / rD
                    if (ExactInt.CompareRat(rN, rD, t0N, t0D) < 0) return false;
                    if (ExactInt.CompareRat(rN, rD, t1N, t1D) < 0)
                    {
                        t1N = rN; t1D = rD; hitEndEdge = edgeId;
                    }
                }
                return true;
            }

            if (!ClipExact(eDx.Negate(), ExactInt.Sub(eAx, eMinX), 1)) return false;
            if (!ClipExact(eDx, ExactInt.Sub(eMaxX, eAx), 2)) return false;
            if (!ClipExact(eDy.Negate(), ExactInt.Sub(eAy, eMinY), 3)) return false;
            if (!ClipExact(eDy, ExactInt.Sub(eMaxY, eAy), 4)) return false;

            float x0 = hitStartEdge == 1 ? minX : (hitStartEdge == 2 ? maxX : ExactInt.ToFloatRatio(ExactInt.Add(ExactInt.Mul(eAx, t0D), ExactInt.Mul(eDx, t0N)), t0D, baseExp));
            float y0 = hitStartEdge == 3 ? minY : (hitStartEdge == 4 ? maxY : ExactInt.ToFloatRatio(ExactInt.Add(ExactInt.Mul(eAy, t0D), ExactInt.Mul(eDy, t0N)), t0D, baseExp));
            float x1 = hitEndEdge == 1 ? minX : (hitEndEdge == 2 ? maxX : ExactInt.ToFloatRatio(ExactInt.Add(ExactInt.Mul(eAx, t1D), ExactInt.Mul(eDx, t1N)), t1D, baseExp));
            float y1 = hitEndEdge == 3 ? minY : (hitEndEdge == 4 ? maxY : ExactInt.ToFloatRatio(ExactInt.Add(ExactInt.Mul(eAy, t1D), ExactInt.Mul(eDy, t1N)), t1D, baseExp));

            clippedA = new Point2(x0, y0);
            clippedB = new Point2(x1, y1);

            if (!t0N.IsZero)
            {
                ExactInt startNumX = ExactInt.Mul(eDx, t0N);
                ExactInt startNumY = ExactInt.Mul(eDy, t0N);
                ExactInt.ToDoubleDoubleRatio(startNumX, t0D, baseExp, out double dcxHi, out double dcxLo);
                ExactInt.ToDoubleDoubleRatio(startNumY, t0D, baseExp, out double dcyHi, out double dcyLo);
                HypotDD(dcxHi, dcxLo, dcyHi, dcyLo, out startTrimHi, out startTrimLo);

                if (dashPeriod > 0.0 && !double.IsNaN(dashPeriod) && !double.IsInfinity(dashPeriod))
                {
                    startTrimModPeriod = ComputeExactLengthModPeriod(startNumX, startNumY, t0D, baseExp, dashPeriod);
                }
            }

            if (hitEndEdge != 0)
            {
                ExactInt oneMinusT1N = ExactInt.Sub(t1D, t1N);
                if (!oneMinusT1N.IsZero)
                {
                    ExactInt.ToDoubleDoubleRatio(ExactInt.Mul(eDx, oneMinusT1N), t1D, baseExp, out double dexHi, out double dexLo);
                    ExactInt.ToDoubleDoubleRatio(ExactInt.Mul(eDy, oneMinusT1N), t1D, baseExp, out double deyHi, out double deyLo);
                    HypotDD(dexHi, dexLo, deyHi, deyLo, out double endHi, out double endLo);
                    endTrimPx = endHi + endLo;
                }
            }

            return true;
        }

        /// <summary>
        /// Computes exact signed depth along the camera forward axis over the true binary IEEE-754 float values,
        /// eliminating catastrophic cancellation across large opposing dot-product terms (e.g. H=2^60).
        /// </summary>
        public static double ComputeCameraDepth(
            float x, float y, float z,
            float camX, float camY, float camZ,
            float fwdX, float fwdY, float fwdZ)
        {
            if (IsNonFinite(x) || IsNonFinite(y) || IsNonFinite(z) ||
                IsNonFinite(camX) || IsNonFinite(camY) || IsNonFinite(camZ) ||
                IsNonFinite(fwdX) || IsNonFinite(fwdY) || IsNonFinite(fwdZ))
            {
                return double.NaN;
            }
            ExactSignedDepth(x, y, z, camX, camY, camZ, fwdX, fwdY, fwdZ, 0f, out ExactInt num, out int scaleExp);
            return ExactInt.ToDoubleRatio(num, ExactInt.One, scaleExp);
        }

        /// <summary>
        /// Clips a 3D world-space segment against the camera near plane defined by
        /// <c>Dot(P - planeOrigin, planeNormal) >= nearDist</c> using exact rational arithmetic over
        /// binary IEEE-754 float inputs. Eliminates cancellation in depth evaluation, handles symmetric
        /// huge endpoints (e.g. +/-2^60), and never rejects genuinely crossing segments due to small denominators.
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
            clippedStart = false;
            clippedEnd = false;
            outAx = ax; outAy = ay; outAz = az;
            outBx = bx; outBy = by; outBz = bz;

            if (IsNonFinite(ax) || IsNonFinite(ay) || IsNonFinite(az) ||
                IsNonFinite(bx) || IsNonFinite(by) || IsNonFinite(bz) ||
                IsNonFinite(camX) || IsNonFinite(camY) || IsNonFinite(camZ) ||
                IsNonFinite(fwdX) || IsNonFinite(fwdY) || IsNonFinite(fwdZ) ||
                IsNonFinite(nearDist))
            {
                return false;
            }

            ExactSignedDepth(ax, ay, az, camX, camY, camZ, fwdX, fwdY, fwdZ, nearDist, out ExactInt dANum, out int dAExp);
            ExactSignedDepth(bx, by, bz, camX, camY, camZ, fwdX, fwdY, fwdZ, nearDist, out ExactInt dBNum, out int dBExp);

            bool aBehind = dANum.IsNegative;
            bool bBehind = dBNum.IsNegative;
            if (aBehind && bBehind) return false;
            if (!aBehind && !bBehind) return true;

            // Align dA and dB to a common scale exponent.
            int commonDExp = Math.Min(dAExp, dBExp);
            ExactInt eDA = dANum.IsZero ? ExactInt.Zero : dANum.ShiftLeft(dAExp - commonDExp);
            ExactInt eDB = dBNum.IsZero ? ExactInt.Zero : dBNum.ShiftLeft(dBExp - commonDExp);

            if (aBehind)
            {
                // eDA < 0 <= eDB => denom = eDB - eDA > 0 strictly.
                // Exact intersection: P = (A * eDB - B * eDA) / (eDB - eDA)
                ExactInt denom = ExactInt.Sub(eDB, eDA);
                if (denom.IsZero) return false;
                ExactInt wA = eDB;
                ExactInt wB = eDA.Negate(); // positive
                outAx = ExactWeightedFloatAverage(ax, wA, bx, wB, denom);
                outAy = ExactWeightedFloatAverage(ay, wA, by, wB, denom);
                outAz = ExactWeightedFloatAverage(az, wA, bz, wB, denom);
                clippedStart = true;
            }
            else
            {
                // eDB < 0 <= eDA => denom = eDA - eDB > 0 strictly.
                // Exact intersection: P = (B * eDA - A * eDB) / (eDA - eDB)
                ExactInt denom = ExactInt.Sub(eDA, eDB);
                if (denom.IsZero) return false;
                ExactInt wA = eDB.Negate(); // positive
                ExactInt wB = eDA;
                outBx = ExactWeightedFloatAverage(ax, wA, bx, wB, denom);
                outBy = ExactWeightedFloatAverage(ay, wA, by, wB, denom);
                outBz = ExactWeightedFloatAverage(az, wA, bz, wB, denom);
                clippedEnd = true;
            }

            return true;
        }

        /// <summary>
        /// Computes the dash phase offset for a viewport-clipped sub-segment in double precision,
        /// reducing each signed term individually modulo <paramref name="period"/> before combining.
        /// </summary>
        public static float ComputeClippedDashPhase(
            double phaseOffset,
            double origTrimStart,
            double clippedFromStartPx,
            double newTrimStart,
            double period = 0.0)
        {
            return ComputeClippedDashPhase(phaseOffset, origTrimStart, clippedFromStartPx, 0.0, newTrimStart, period);
        }

        /// <summary>
        /// High-precision overload accepting a 106-bit double-double clipped length
        /// (<paramref name="clippedFromStartHi"/> + <paramref name="clippedFromStartLo"/>) so huge
        /// clip distances (e.g. 2^60 - 40 px) retain exact phase remainder modulo <paramref name="period"/>.
        /// </summary>
        public static float ComputeClippedDashPhase(
            double phaseOffset,
            double origTrimStart,
            double clippedFromStartHi,
            double clippedFromStartLo,
            double newTrimStart,
            double period = 0.0)
        {
            if (double.IsNaN(phaseOffset) || double.IsInfinity(phaseOffset) ||
                double.IsNaN(origTrimStart) || double.IsInfinity(origTrimStart) ||
                double.IsNaN(clippedFromStartHi) || double.IsInfinity(clippedFromStartHi) ||
                double.IsNaN(clippedFromStartLo) || double.IsInfinity(clippedFromStartLo) ||
                double.IsNaN(newTrimStart) || double.IsInfinity(newTrimStart))
            {
                return 0f;
            }

            if (period > 0.0 && !double.IsNaN(period) && !double.IsInfinity(period))
            {
                double a = phaseOffset % period;
                double b = origTrimStart % period;
                double cHi = clippedFromStartHi % period;
                double cLo = clippedFromStartLo % period;
                double d = newTrimStart % period;
                double rem = ((a + b) - (cHi + cLo + d)) % period;
                if (rem < 0.0) rem += period;
                return (float)rem;
            }

            double raw = phaseOffset + origTrimStart - clippedFromStartHi - clippedFromStartLo - newTrimStart;
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

        [StructLayout(LayoutKind.Explicit)]
        private struct FloatBitsUnion
        {
            [FieldOffset(0)] public float F;
            [FieldOffset(0)] public int I;
        }

        private static void DecodeFloat(float value, out bool neg, out uint mant, out int exp)
        {
            if (value == 0f)
            {
                neg = false; mant = 0; exp = 0;
                return;
            }
            var u = new FloatBitsUnion { F = value };
            int bits = u.I;
            neg = (bits & unchecked((int)0x80000000)) != 0;
            int rawExp = (bits >> 23) & 0xFF;
            uint frac = (uint)(bits & 0x7FFFFF);
            if (rawExp == 0)
            {
                mant = frac;
                exp = -149;
            }
            else
            {
                mant = frac | 0x800000u;
                exp = rawExp - 150;
            }
            while ((mant & 1u) == 0)
            {
                mant >>= 1;
                exp++;
            }
        }

        private static bool IsNonFinite(float v) => float.IsNaN(v) || float.IsInfinity(v);

        private static void DecodeDouble(double value, out bool neg, out ulong mant, out int exp)
        {
            if (value == 0.0)
            {
                neg = false; mant = 0; exp = 0;
                return;
            }
            long bits = BitConverter.DoubleToInt64Bits(value);
            neg = bits < 0;
            int rawExp = (int)((bits >> 52) & 0x7FFL);
            ulong frac = (ulong)(bits & 0xFFFFFFFFFFFFFL);
            if (rawExp == 0)
            {
                mant = frac;
                exp = -1074;
            }
            else
            {
                mant = frac | (1UL << 52);
                exp = rawExp - 1075;
            }
            while ((mant & 1UL) == 0)
            {
                mant >>= 1;
                exp++;
            }
        }

        private static void ExactFloatDiff(float a, float b, out ExactInt val, out int exp)
        {
            DecodeFloat(a, out bool aNeg, out uint aM, out int aE);
            DecodeFloat(b, out bool bNeg, out uint bM, out int bE);
            if (aM == 0 && bM == 0) { val = ExactInt.Zero; exp = 0; return; }
            if (aM == 0) { val = ExactInt.FromShiftedUint(!bNeg, bM, 0); exp = bE; return; }
            if (bM == 0) { val = ExactInt.FromShiftedUint(aNeg, aM, 0); exp = aE; return; }
            exp = Math.Min(aE, bE);
            ExactInt eA = ExactInt.FromShiftedUint(aNeg, aM, aE - exp);
            ExactInt eB = ExactInt.FromShiftedUint(bNeg, bM, bE - exp);
            val = ExactInt.Sub(eA, eB);
        }

        private static void ExactAxisTerm(float p, float c, float f, out ExactInt val, out int exp)
        {
            DecodeFloat(f, out bool fNeg, out uint fM, out int fE);
            if (fM == 0) { val = ExactInt.Zero; exp = 0; return; }
            ExactFloatDiff(p, c, out ExactInt diff, out int dExp);
            if (diff.IsZero) { val = ExactInt.Zero; exp = 0; return; }
            val = ExactInt.Mul(diff, ExactInt.FromShiftedUint(fNeg, fM, 0));
            exp = dExp + fE;
        }

        private static void ExactSignedDepth(
            float px, float py, float pz,
            float camX, float camY, float camZ,
            float fwdX, float fwdY, float fwdZ,
            float nearDist,
            out ExactInt num, out int scaleExp)
        {
            ExactAxisTerm(px, camX, fwdX, out ExactInt tX, out int eX);
            ExactAxisTerm(py, camY, fwdY, out ExactInt tY, out int eY);
            ExactAxisTerm(pz, camZ, fwdZ, out ExactInt tZ, out int eZ);
            DecodeFloat(nearDist, out bool nNeg, out uint nM, out int eN);
            ExactInt tN = nM == 0 ? ExactInt.Zero : ExactInt.FromShiftedUint(!nNeg, nM, 0);

            int minExp = int.MaxValue;
            if (!tX.IsZero && eX < minExp) minExp = eX;
            if (!tY.IsZero && eY < minExp) minExp = eY;
            if (!tZ.IsZero && eZ < minExp) minExp = eZ;
            if (!tN.IsZero && eN < minExp) minExp = eN;

            if (minExp == int.MaxValue)
            {
                num = ExactInt.Zero;
                scaleExp = 0;
                return;
            }

            ExactInt sum = ExactInt.Zero;
            if (!tX.IsZero) sum = ExactInt.Add(sum, tX.ShiftLeft(eX - minExp));
            if (!tY.IsZero) sum = ExactInt.Add(sum, tY.ShiftLeft(eY - minExp));
            if (!tZ.IsZero) sum = ExactInt.Add(sum, tZ.ShiftLeft(eZ - minExp));
            if (!tN.IsZero) sum = ExactInt.Add(sum, tN.ShiftLeft(eN - minExp));

            num = sum;
            scaleExp = sum.IsZero ? 0 : minExp;
        }

        private static float ExactWeightedFloatAverage(float a, ExactInt wA, float b, ExactInt wB, ExactInt denom)
        {
            if (a == b) return a;
            DecodeFloat(a, out bool aNeg, out uint aM, out int aE);
            DecodeFloat(b, out bool bNeg, out uint bM, out int bE);
            if (aM == 0 && bM == 0) return 0f;
            int baseExp = aM == 0 ? bE : (bM == 0 ? aE : Math.Min(aE, bE));
            ExactInt eA = aM == 0 ? ExactInt.Zero : ExactInt.FromShiftedUint(aNeg, aM, aE - baseExp);
            ExactInt eB = bM == 0 ? ExactInt.Zero : ExactInt.FromShiftedUint(bNeg, bM, bE - baseExp);
            ExactInt num = ExactInt.Add(ExactInt.Mul(eA, wA), ExactInt.Mul(eB, wB));
            return ExactInt.ToFloatRatio(num, denom, baseExp);
        }

        private static double ComputeExactLengthModPeriod(
            ExactInt numX, ExactInt numY, ExactInt den, int baseExp, double period)
        {
            if ((numX.IsZero && numY.IsZero) || den.IsZero ||
                period <= 0.0 || double.IsNaN(period) || double.IsInfinity(period))
            {
                return 0.0;
            }

            DecodeDouble(period, out _, out ulong pMant, out int pExp);
            if (pMant == 0UL) return 0.0;

            ExactInt bDen = ExactInt.Mul(den.Abs(), ExactInt.FromShiftedUlong(false, pMant, 0));
            int s = baseExp - pExp;
            const int FracBits = 64;

            if (numX.IsZero || numY.IsZero)
            {
                ExactInt absAxis = numX.IsZero ? numY.Abs() : numX.Abs();
                int approxExp = absAxis.BitLength - bDen.BitLength + s;
                if (approxExp <= -2)
                {
                    double direct = ExactInt.ToDoubleRatio(absAxis, den.Abs(), baseExp);
                    return direct < period ? direct : direct % period;
                }
                if (approxExp > 4096) return 0.0;

                int shift = s + FracBits;
                ExactInt n = shift >= 0 ? absAxis.ShiftLeft(shift) : absAxis;
                ExactInt d = shift < 0 ? bDen.ShiftLeft(-shift) : bDen;
                ExactInt q = ExactInt.DivRemPos(n, d, out _);
                double rem = (double)q.Low64() * Math.Pow(2.0, -FracBits) * period;
                return rem >= period ? 0.0 : rem;
            }

            ExactInt aSq = ExactInt.Add(ExactInt.Mul(numX, numX), ExactInt.Mul(numY, numY));
            int approxDiagExp = ((aSq.BitLength + 1) >> 1) - bDen.BitLength + s;
            if (approxDiagExp <= -2)
            {
                ExactInt.ToDoubleDoubleRatio(numX, den, baseExp, out double xh, out double xl);
                ExactInt.ToDoubleDoubleRatio(numY, den, baseExp, out double yh, out double yl);
                HypotDD(xh, xl, yh, yl, out double rh, out double rl);
                double direct = rh + rl;
                return direct < period ? direct : direct % period;
            }
            if (approxDiagExp > 4096) return 0.0;

            int shift2 = 2 * (s + FracBits);
            ExactInt nSq = shift2 >= 0 ? aSq.ShiftLeft(shift2) : aSq;
            ExactInt bSq = ExactInt.Mul(bDen, bDen);
            ExactInt dSq = shift2 < 0 ? bSq.ShiftLeft(-shift2) : bSq;
            ExactInt qDiag = ExactInt.ISqrt(ExactInt.DivRemPos(nSq, dSq, out _));
            double remDiag = (double)qDiag.Low64() * Math.Pow(2.0, -FracBits) * period;
            return remDiag >= period ? 0.0 : remDiag;
        }

        private static void TwoSum(double a, double b, out double s, out double e)
        {
            s = a + b;
            double v = s - a;
            e = (a - (s - v)) + (b - v);
        }

        private static void TwoProd(double a, double b, out double p, out double e)
        {
            // Veltkamp split (27-bit) — works on netstandard2.0 without Math.FusedMultiplyAdd.
            const double Split = 134217729.0; // 2^27 + 1
            p = a * b;
            double ca = Split * a;
            double ah = ca - (ca - a);
            double al = a - ah;
            double cb = Split * b;
            double bh = cb - (cb - b);
            double bl = b - bh;
            e = ((ah * bh - p) + ah * bl + al * bh) + al * bl;
        }

        private static void HypotDD(double axHi, double axLo, double ayHi, double ayLo, out double rHi, out double rLo)
        {
            if (axHi == 0.0 && axLo == 0.0)
            {
                rHi = Math.Abs(ayHi);
                rLo = ayHi < 0.0 ? -ayLo : ayLo;
                return;
            }
            if (ayHi == 0.0 && ayLo == 0.0)
            {
                rHi = Math.Abs(axHi);
                rLo = axHi < 0.0 ? -axLo : axLo;
                return;
            }

            double maxH = Math.Max(Math.Abs(axHi), Math.Abs(ayHi));
            int scaleExp = (int)Math.Floor(Math.Log(maxH, 2.0));
            double invScale = Math.Pow(2.0, -scaleExp);
            double xh = axHi * invScale, xl = axLo * invScale;
            double yh = ayHi * invScale, yl = ayLo * invScale;

            // Compute s = x^2 + y^2 in double-double
            TwoProd(xh, xh, out double x2h, out double x2e);
            double x2l = x2e + 2.0 * xh * xl;
            TwoSum(x2h, x2l, out x2h, out x2l);

            TwoProd(yh, yh, out double y2h, out double y2e);
            double y2l = y2e + 2.0 * yh * yl;
            TwoSum(y2h, y2l, out y2h, out y2l);

            TwoSum(x2h, y2h, out double sh, out double se);
            double sl = se + x2l + y2l;
            TwoSum(sh, sl, out sh, out sl);

            // One Newton step in double-double: r = sqrt(sh); r += (s - r^2) / (2*r)
            double r0 = Math.Sqrt(sh);
            TwoProd(r0, r0, out double r2h, out double r2l);
            double diff = (sh - r2h) + (sl - r2l);
            double corr = r0 > 0.0 ? diff / (2.0 * r0) : 0.0;
            TwoSum(r0, corr, out double rh, out double rl);

            double scale = Math.Pow(2.0, scaleExp);
            rHi = rh * scale;
            rLo = rl * scale;
        }

        private readonly struct ExactInt
        {
            public static readonly ExactInt Zero = new ExactInt(false, Array.Empty<uint>());
            public static readonly ExactInt One = new ExactInt(false, new uint[] { 1u });

            private readonly bool _neg;
            private readonly uint[] _limbs; // little-endian, trimmed (no leading zero limbs)

            private ExactInt(bool neg, uint[] limbs)
            {
                int len = limbs != null ? limbs.Length : 0;
                while (len > 0 && limbs[len - 1] == 0u) len--;
                if (len == 0)
                {
                    _neg = false;
                    _limbs = Array.Empty<uint>();
                }
                else
                {
                    _neg = neg;
                    if (len == limbs.Length)
                    {
                        _limbs = limbs;
                    }
                    else
                    {
                        _limbs = new uint[len];
                        Array.Copy(limbs, _limbs, len);
                    }
                }
            }

            public bool IsZero => _limbs == null || _limbs.Length == 0;
            public bool IsNegative => _neg && !IsZero;

            public ExactInt Abs() => IsNegative ? new ExactInt(false, _limbs) : this;
            public ExactInt Negate() => IsZero ? Zero : new ExactInt(!_neg, _limbs);

            public int BitLength
            {
                get
                {
                    if (IsZero) return 0;
                    uint top = _limbs[_limbs.Length - 1];
                    int bits = (_limbs.Length - 1) * 32;
                    while (top != 0u) { bits++; top >>= 1; }
                    return bits;
                }
            }

            public static ExactInt FromShiftedUint(bool neg, uint mant, int shift)
            {
                if (mant == 0u) return Zero;
                int w = shift >> 5;
                int b = shift & 31;
                if (b == 0)
                {
                    var arr = new uint[w + 1];
                    arr[w] = mant;
                    return new ExactInt(neg, arr);
                }
                uint lo = mant << b;
                uint hi = mant >> (32 - b);
                var limbs = new uint[hi != 0u ? w + 2 : w + 1];
                limbs[w] = lo;
                if (hi != 0u) limbs[w + 1] = hi;
                return new ExactInt(neg, limbs);
            }

            public static ExactInt FromShiftedUlong(bool neg, ulong mant, int shift)
            {
                if (mant == 0UL) return Zero;
                uint lo = (uint)mant;
                uint hi = (uint)(mant >> 32);
                ExactInt baseVal = hi != 0u
                    ? new ExactInt(neg, new uint[] { lo, hi })
                    : new ExactInt(neg, new uint[] { lo });
                return shift > 0 ? baseVal.ShiftLeft(shift) : baseVal;
            }

            public ulong Low64()
            {
                if (IsZero) return 0UL;
                ulong lo = _limbs[0];
                if (_limbs.Length > 1) lo |= ((ulong)_limbs[1]) << 32;
                return lo;
            }

            public ExactInt ShiftLeft(int shift)
            {
                if (IsZero || shift <= 0) return this;
                int w = shift >> 5;
                int b = shift & 31;
                int n = _limbs.Length;
                if (b == 0)
                {
                    var res = new uint[n + w];
                    Array.Copy(_limbs, 0, res, w, n);
                    return new ExactInt(_neg, res);
                }
                var outArr = new uint[n + w + 1];
                ulong carry = 0;
                for (int i = 0; i < n; i++)
                {
                    ulong v = ((ulong)_limbs[i] << b) | carry;
                    outArr[i + w] = (uint)v;
                    carry = v >> 32;
                }
                if (carry != 0) outArr[n + w] = (uint)carry;
                return new ExactInt(_neg, outArr);
            }

            public static int CompareAbs(ExactInt a, ExactInt b)
            {
                int al = a._limbs != null ? a._limbs.Length : 0;
                int bl = b._limbs != null ? b._limbs.Length : 0;
                if (al != bl) return al.CompareTo(bl);
                for (int i = al - 1; i >= 0; i--)
                {
                    if (a._limbs[i] != b._limbs[i]) return a._limbs[i].CompareTo(b._limbs[i]);
                }
                return 0;
            }

            public static int CompareTo(ExactInt a, ExactInt b)
            {
                if (a.IsZero && b.IsZero) return 0;
                if (a.IsNegative != b.IsNegative) return a.IsNegative ? -1 : 1;
                int c = CompareAbs(a, b);
                return a.IsNegative ? -c : c;
            }

            public static int CompareRat(ExactInt n1, ExactInt d1, ExactInt n2, ExactInt d2)
            {
                return CompareTo(Mul(n1, d2), Mul(n2, d1));
            }

            public static ExactInt Add(ExactInt a, ExactInt b)
            {
                if (a.IsZero) return b;
                if (b.IsZero) return a;
                if (a._neg == b._neg) return new ExactInt(a._neg, AddAbs(a._limbs, b._limbs));
                int cmp = CompareAbs(a, b);
                if (cmp == 0) return Zero;
                if (cmp > 0) return new ExactInt(a._neg, SubAbs(a._limbs, b._limbs));
                return new ExactInt(b._neg, SubAbs(b._limbs, a._limbs));
            }

            public static ExactInt Sub(ExactInt a, ExactInt b) => Add(a, b.Negate());

            public static ExactInt Mul(ExactInt a, ExactInt b)
            {
                if (a.IsZero || b.IsZero) return Zero;
                int al = a._limbs.Length, bl = b._limbs.Length;
                var res = new uint[al + bl];
                for (int i = 0; i < al; i++)
                {
                    ulong ai = a._limbs[i];
                    ulong carry = 0;
                    for (int j = 0; j < bl; j++)
                    {
                        ulong prod = ai * b._limbs[j] + res[i + j] + carry;
                        res[i + j] = (uint)prod;
                        carry = prod >> 32;
                    }
                    if (carry != 0) res[i + bl] = (uint)carry;
                }
                return new ExactInt(a._neg != b._neg, res);
            }

            private static uint[] AddAbs(uint[] a, uint[] b)
            {
                int n = Math.Max(a.Length, b.Length);
                var res = new uint[n + 1];
                ulong carry = 0;
                for (int i = 0; i < n; i++)
                {
                    ulong sum = carry + (i < a.Length ? a[i] : 0UL) + (i < b.Length ? b[i] : 0UL);
                    res[i] = (uint)sum;
                    carry = sum >> 32;
                }
                if (carry != 0) res[n] = (uint)carry;
                return res;
            }

            private static uint[] SubAbs(uint[] a, uint[] b)
            {
                // Assumes |a| >= |b|
                var res = new uint[a.Length];
                long borrow = 0;
                for (int i = 0; i < a.Length; i++)
                {
                    long diff = (long)a[i] - (i < b.Length ? b[i] : 0L) - borrow;
                    if (diff < 0)
                    {
                        diff += 1L << 32;
                        borrow = 1;
                    }
                    else
                    {
                        borrow = 0;
                    }
                    res[i] = (uint)diff;
                }
                return res;
            }

            public static double ToDoubleRatio(ExactInt num, ExactInt den, int scaleExp)
            {
                ToDoubleDoubleRatio(num, den, scaleExp, out double hi, out double lo);
                return hi + lo;
            }

            public static void ToDoubleDoubleRatio(ExactInt num, ExactInt den, int scaleExp, out double hi, out double lo)
            {
                if (num.IsZero || den.IsZero)
                {
                    hi = 0.0;
                    lo = 0.0;
                    return;
                }

                bool neg = num._neg != den._neg;
                ExactInt a = num.Abs();
                ExactInt b = den.Abs();

                // Locate exact expTop such that 2^expTop <= a / b < 2^(expTop + 1).
                int expTop = a.BitLength - b.BitLength;
                ExactInt rem = expTop < 0 ? a.ShiftLeft(-expTop) : a;
                ExactInt div = expTop > 0 ? b.ShiftLeft(expTop) : b;
                if (CompareAbs(rem, div) < 0)
                {
                    expTop--;
                    rem = rem.ShiftLeft(1);
                }

                // Restoring binary long division extracting 53 bits for qHi + 53 bits for qLo (= 106 bits exact).
                ulong qHi = 0;
                ulong qLo = 0;
                for (int i = 0; i < 106; i++)
                {
                    if (CompareAbs(rem, div) >= 0)
                    {
                        rem = new ExactInt(false, SubAbs(rem._limbs, div._limbs));
                        if (i < 53) qHi |= 1UL << (52 - i);
                        else qLo |= 1UL << (105 - i);
                    }
                    rem = rem.ShiftLeft(1);
                }

                // Round to nearest on bit 106
                if (CompareAbs(rem, div) >= 0)
                {
                    qLo++;
                    if (qLo == (1UL << 53))
                    {
                        qLo = 0;
                        qHi++;
                    }
                }

                hi = (double)qHi * Math.Pow(2.0, expTop - 52 + scaleExp);
                lo = (double)qLo * Math.Pow(2.0, expTop - 105 + scaleExp);
                if (neg)
                {
                    hi = -hi;
                    lo = -lo;
                }
            }

            /// <summary>
            /// Directly rounds exact rational <c>(num / den) * 2^scaleExp</c> to IEEE-754 binary32
            /// using round-to-nearest-even with exact remainder comparison (eliminating double-rounding
            /// through binary64 at binary32 midpoints such as 1 + 2^-24 + 2^-64).
            /// </summary>
            public static float ToFloatRatio(ExactInt num, ExactInt den, int scaleExp)
            {
                if (num.IsZero || den.IsZero) return 0f;

                bool neg = num._neg != den._neg;
                ExactInt a = num.Abs();
                ExactInt b = den.Abs();

                int expTop = a.BitLength - b.BitLength;
                ExactInt rem = expTop < 0 ? a.ShiftLeft(-expTop) : a;
                ExactInt div = expTop > 0 ? b.ShiftLeft(expTop) : b;
                if (CompareAbs(rem, div) < 0)
                {
                    expTop--;
                    rem = rem.ShiftLeft(1);
                }

                int trueExp = expTop + scaleExp;
                if (trueExp > 127) return neg ? float.NegativeInfinity : float.PositiveInfinity;
                if (trueExp < -150) return neg ? -0f : 0f;

                int sigBits = trueExp >= -126 ? 24 : (trueExp + 150);
                uint q = 0;
                for (int i = 0; i < sigBits; i++)
                {
                    q <<= 1;
                    if (CompareAbs(rem, div) >= 0)
                    {
                        rem = new ExactInt(false, SubAbs(rem._limbs, div._limbs));
                        q |= 1u;
                    }
                    rem = rem.ShiftLeft(1);
                }

                // After loop, rem has already been shifted left by 1 relative to the last extracted bit,
                // so CompareAbs(rem, div) directly compares fractional remainder against 0.5 ULP.
                int halfCmp = CompareAbs(rem, div);
                if (halfCmp > 0 || (halfCmp == 0 && (q & 1u) != 0u))
                {
                    q++;
                    if (trueExp >= -126)
                    {
                        if (q == (1u << 24))
                        {
                            q >>= 1;
                            trueExp++;
                            if (trueExp > 127) return neg ? float.NegativeInfinity : float.PositiveInfinity;
                        }
                    }
                    else if (q == (1u << 23))
                    {
                        trueExp = -126;
                    }
                }

                int signBit = neg ? unchecked((int)0x80000000) : 0;
                if (q == 0u) return new FloatBitsUnion { I = signBit }.F;
                if (trueExp >= -126)
                {
                    int biased = trueExp + 127;
                    return new FloatBitsUnion { I = signBit | (biased << 23) | (int)(q & 0x7FFFFFu) }.F;
                }
                return new FloatBitsUnion { I = signBit | (int)(q & 0x7FFFFFu) }.F;
            }

            public ExactInt ShiftRight(int shift)
            {
                if (IsZero || shift <= 0) return this;
                int w = shift >> 5;
                int b = shift & 31;
                if (w >= _limbs.Length) return Zero;
                int newLen = _limbs.Length - w;
                var res = new uint[newLen];
                if (b == 0)
                {
                    Array.Copy(_limbs, w, res, 0, newLen);
                }
                else
                {
                    for (int i = 0; i < newLen; i++)
                    {
                        uint lo = _limbs[i + w] >> b;
                        uint hi = (i + w + 1 < _limbs.Length) ? (_limbs[i + w + 1] << (32 - b)) : 0u;
                        res[i] = lo | hi;
                    }
                }
                return new ExactInt(_neg, res);
            }

            public static ExactInt DivRemPos(ExactInt a, ExactInt b, out ExactInt rem)
            {
                if (a.IsZero || CompareAbs(a, b) < 0)
                {
                    rem = a.Abs();
                    return Zero;
                }
                int shift = a.BitLength - b.BitLength;
                ExactInt r = a.Abs();
                ExactInt d = b.Abs().ShiftLeft(shift);
                var qLimbs = new uint[(shift >> 5) + 1];
                for (int k = shift; k >= 0; k--)
                {
                    if (CompareAbs(r, d) >= 0)
                    {
                        r = new ExactInt(false, SubAbs(r._limbs, d._limbs));
                        qLimbs[k >> 5] |= 1u << (k & 31);
                    }
                    if (k > 0) d = d.ShiftRight(1);
                }
                rem = r;
                return new ExactInt(false, qLimbs);
            }

            public static ExactInt ISqrt(ExactInt n)
            {
                if (n.IsZero || n.IsNegative) return Zero;
                int bits = n.BitLength;
                ExactInt x = One.ShiftLeft((bits + 1) >> 1);
                while (true)
                {
                    ExactInt q = DivRemPos(n, x, out _);
                    ExactInt next = Add(x, q).ShiftRight(1);
                    if (CompareAbs(next, x) >= 0) return x;
                    x = next;
                }
            }
        }
    }
}
