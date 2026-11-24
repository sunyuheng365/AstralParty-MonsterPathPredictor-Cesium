using System;
using System.Runtime.CompilerServices;
using CesiumLoader.SDK.Engine;
using Core.Unit;
using UnityEngine;

namespace MonsterPathPredictor.Runtime
{
    internal struct MonsterVisualBounds
    {
        /// <summary>4 world-space corners of the monster sprite (inline value fields, zero heap allocation).</summary>
        public Vector3 BL;
        public Vector3 BR;
        public Vector3 TL;
        public Vector3 TR;
        public Vector3 CenterWorld;
        public Vector3 TopWorld;
        public bool HasCorners;
        public bool FromSprite;
    }

    /// <summary>
    /// Reads the exact 3D world corners of a monster's rendered SpriteRenderer (honoring DeployPlayers
    /// lateral stacking offset, animation scale, and flipX/flipY), strictly isolating Unity ECalls
    /// inside NoInlining raw helpers per SDK-UnityInterop.
    /// </summary>
    internal static class SpriteBoundsReader
    {
        public static MonsterVisualBounds Capture(Character ch, Vector3 fallbackFeet, Camera camera)
        {
            if (TryCaptureFromSprite(ch, out var result))
            {
                return result;
            }

            Vector3 anchor = fallbackFeet;
            try
            {
                Transform t = (ch != null && ch.characterObject != null)
                    ? ch.characterObject
                    : UnityObject.GetTransform(ch);
                if (t != null)
                {
                    Vector3 p = TransformService.GetPosition(t);
                    if (!float.IsNaN(p.x) && !float.IsInfinity(p.x)) anchor = p;
                }
            }
            catch { }

            Vector3 camRight = Vector3.right;
            Vector3 camUp = Vector3.up;
            if (UnityObject.IsAlive(camera))
            {
                var camTr = UnityObject.GetTransform(camera);
                camRight = TransformService.GetRight(camTr);
                camUp = TransformService.GetUp(camTr);
            }

            const float halfW = 0.65f;
            const float height = 1.95f;
            Vector3 bl = anchor - camRight * halfW;
            Vector3 br = anchor + camRight * halfW;
            Vector3 tl = bl + camUp * height;
            Vector3 tr = br + camUp * height;

            return new MonsterVisualBounds
            {
                BL = bl,
                BR = br,
                TL = tl,
                TR = tr,
                CenterWorld = anchor + camUp * (height * 0.5f),
                TopWorld = anchor + camUp * height,
                HasCorners = true,
                FromSprite = false
            };
        }

        private static bool TryCaptureFromSprite(Character ch, out MonsterVisualBounds bounds)
        {
            bounds = default;
            if (!UnityObject.IsAlive(ch)) return false;

            try
            {
                SpriteRenderer sr = FindSpriteRenderer(ch);
                if (sr == null) return false;
                if (!TryReadLocalSpriteRect(sr, out float minX, out float maxX, out float minY, out float maxY, out float z, out Transform srTransform))
                {
                    return false;
                }

                Vector3 bl = TransformService.TransformPoint(srTransform, new Vector3(minX, minY, z));
                Vector3 br = TransformService.TransformPoint(srTransform, new Vector3(maxX, minY, z));
                Vector3 tl = TransformService.TransformPoint(srTransform, new Vector3(minX, maxY, z));
                Vector3 tr = TransformService.TransformPoint(srTransform, new Vector3(maxX, maxY, z));
                Vector3 center = TransformService.TransformPoint(srTransform, new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, z));
                Vector3 top = TransformService.TransformPoint(srTransform, new Vector3((minX + maxX) * 0.5f, maxY, z));

                bounds = new MonsterVisualBounds
                {
                    BL = bl,
                    BR = br,
                    TL = tl,
                    TR = tr,
                    CenterWorld = center,
                    TopWorld = top,
                    HasCorners = true,
                    FromSprite = true
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static SpriteRenderer FindSpriteRenderer(Character ch)
        {
            try
            {
                var anim = ch.characterAnimator;
                if (anim != null)
                {
                    var direct = RawGetSpriteRenderer(anim);
                    if (direct != null) return direct;
                }
            }
            catch { }

            try
            {
                var obj = ch.characterObject;
                if (obj != null)
                {
                    var fromObj = RawGetSpriteRendererInChildren(obj);
                    if (fromObj != null) return fromObj;
                }
            }
            catch { }

            try
            {
                return RawGetSpriteRendererInChildren(ch);
            }
            catch
            {
                return null;
            }
        }

        private static bool TryReadLocalSpriteRect(
            SpriteRenderer sr,
            out float minX, out float maxX,
            out float minY, out float maxY,
            out float z,
            out Transform srTransform)
        {
            minX = maxX = minY = maxY = z = 0f;
            srTransform = null;
            try
            {
                return RawReadSpriteLocalRect(sr, out minX, out maxX, out minY, out maxY, out z, out srTransform);
            }
            catch
            {
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static SpriteRenderer RawGetSpriteRenderer(Component comp)
        {
            return comp != null ? comp.GetComponent(typeof(SpriteRenderer)) as SpriteRenderer : null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static SpriteRenderer RawGetSpriteRendererInChildren(Component comp)
        {
            return comp != null ? comp.GetComponentInChildren<SpriteRenderer>(true) : null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool RawReadSpriteLocalRect(
            SpriteRenderer sr,
            out float minX, out float maxX,
            out float minY, out float maxY,
            out float z,
            out Transform srTransform)
        {
            minX = maxX = minY = maxY = z = 0f;
            srTransform = null;
            if (sr == null || !sr.enabled) return false;
            var sprite = sr.sprite;
            if (sprite == null) return false;
            Bounds b = sprite.bounds;
            if (b.size.x <= 0.001f || b.size.y <= 0.001f) return false;

            float x0 = sr.flipX ? -b.max.x : b.min.x;
            float x1 = sr.flipX ? -b.min.x : b.max.x;
            float y0 = sr.flipY ? -b.max.y : b.min.y;
            float y1 = sr.flipY ? -b.min.y : b.max.y;

            minX = Math.Min(x0, x1);
            maxX = Math.Max(x0, x1);
            minY = Math.Min(y0, y1);
            maxY = Math.Max(y0, y1);
            z = b.center.z;
            srTransform = sr.transform;
            return srTransform != null;
        }
    }
}
