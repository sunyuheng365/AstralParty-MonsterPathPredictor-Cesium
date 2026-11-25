using System;
using System.Collections.Generic;
using CesiumLoader.SDK.Engine;
using CesiumLoader.SDK.Inputs;
using FairyGUI;
using UnityEngine;
using MonsterPathPredictor.Core;

namespace MonsterPathPredictor.Runtime
{
    /// <summary>
    /// Handles clicking anywhere on a monster's rendered sprite (with stacked-monster disambiguation)
    /// and toggling the monster picker popup or clearing selection via hotkeys.
    /// </summary>
    internal sealed class MonsterPicker
    {
        private const float DragThresholdPx = 8f;

        private readonly PointerClickTracker _clickTracker = new PointerClickTracker(DragThresholdPx);
        private int _lastProcessedFrame = -1;

        public void CancelPendingPointer()
        {
            _clickTracker.Reset();
        }

        public bool ProcessInput(
            GComponent layer,
            Camera camera,
            IReadOnlyList<LiveMonsterInfo> monsters,
            PredictorSettings settings,
            MonsterPickerPopup popup,
            ref long selectedMonsterId)
        {
            int frame = UnityTime.FrameCount;
            if (frame == _lastProcessedFrame) return false;
            _lastProcessedFrame = frame;

            // 1. Toggle monster picker popup hotkey (default F8)
            if (IsHotkeyDown(settings.PickerHotkey))
            {
                popup?.Toggle();
                return false;
            }

            // 2. Clear hotkey (default Escape): cancel any in-flight pointer gesture so releasing the mouse
            //    cannot immediately re-select a monster, then close popup first if open, otherwise clear selection.
            if (IsHotkeyDown(settings.ClearHotkey))
            {
                CancelPendingPointer();
                if (popup != null && popup.IsOpen)
                {
                    popup.Hide();
                    return false;
                }
                if (selectedMonsterId != 0)
                {
                    selectedMonsterId = 0;
                    return true;
                }
                return false;
            }

            bool isDown = InputService.IsMouseButtonPressed(0);
            bool isHeld = InputService.IsMouseButtonHeld(0);
            bool isUp = InputService.IsMouseButtonReleased(0);

            if (!isDown && !isHeld && !isUp && !_clickTracker.IsTracking)
                return false;

            bool hasPos = TryGetStageAndLayerMouse(layer, out Vector2 stagePos, out Vector2 localPos);

            bool blockedOnDown = false;
            long downHitId = 0;
            if (isDown && hasPos)
            {
                // Ignore clicks starting inside our own picker popup or blocking game UI (buttons/cards/windows)
                if ((popup != null && popup.IsOpen && popup.ContainsStagePoint(stagePos)) ||
                    IsPointerOverBlockingUi())
                {
                    blockedOnDown = true;
                }
                else
                {
                    // Snapshot hit right on MouseDown so clicking a fast-moving monster still resolves accurately on MouseUp
                    downHitId = HitTestMonsters(layer, camera, monsters, localPos, settings);
                }
            }

            // Track pointer displacement in layer-local coordinates so DragThresholdPx matches
            // ClickRadius and sprite hitboxes consistently across all FairyGUI GRoot scale factors.
            var outcome = _clickTracker.Update(
                isDownThisFrame: isDown,
                isHeld: isHeld,
                isUpThisFrame: isUp,
                hasPosition: hasPos,
                position: hasPos ? new Point2(localPos.x, localPos.y) : default,
                blockedByUiOnDown: blockedOnDown,
                downHitMonsterId: downHitId);

            if (!outcome.IsBoardClick) return false;

            // Prefer the monster cleanly hit on MouseDown if still alive so a passing monster
            // cannot hijack the release click; fall back to release-position hit test if needed.
            long hitMonsterId = IsMonsterPresent(monsters, outcome.DownHitMonsterId)
                ? outcome.DownHitMonsterId
                : 0;
            if (hitMonsterId == 0)
            {
                hitMonsterId = HitTestMonsters(layer, camera, monsters, localPos, settings);
            }

            long nextSelected = PointerClickTracker.ApplyBoardClickSelection(
                selectedMonsterId,
                hitMonsterId,
                settings.ClearOnEmptyClick);

            if (nextSelected != selectedMonsterId)
            {
                selectedMonsterId = nextSelected;
                return true;
            }

            return false;
        }

        private static bool IsMonsterPresent(IReadOnlyList<LiveMonsterInfo> monsters, long monsterId)
        {
            if (monsterId == 0 || monsters == null) return false;
            for (int i = 0; i < monsters.Count; i++)
            {
                var m = monsters[i];
                if (m != null && m.State != null && m.State.MonsterId == monsterId) return true;
            }
            return false;
        }

        private static bool IsHotkeyDown(string keyName)
        {
            if (string.IsNullOrWhiteSpace(keyName)) return false;
            if (Enum.TryParse(keyName.Trim(), ignoreCase: true, out KeyCode code) && code != KeyCode.None)
            {
                return InputService.IsKeyDown(code);
            }
            return false;
        }

        private static bool IsPointerOverBlockingUi()
        {
            try
            {
                var root = GRoot.inst;
                if (root == null) return false;
                var target = root.touchTarget;
                if (target == null || ReferenceEquals(target, root)) return false;

                string name = target.name ?? string.Empty;
                if (name == "GRoot" || name == "Stage") return false;
                return target.touchable;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetStageAndLayerMouse(GComponent layer, out Vector2 stagePos, out Vector2 localPos)
        {
            stagePos = Vector2.zero;
            localPos = Vector2.zero;
            if (layer == null) return false;

            try
            {
                if (Stage.inst != null)
                {
                    Vector2 sp = Stage.inst.touchPosition;
                    if (!float.IsNaN(sp.x) && !float.IsNaN(sp.y))
                    {
                        stagePos = sp;
                        localPos = layer.GlobalToLocal(sp);
                        return !float.IsNaN(localPos.x) && !float.IsNaN(localPos.y);
                    }
                }
            }
            catch { }

            try
            {
                if (InputService.TryGetMousePosition(out Vector2 unityPos))
                {
                    float stageHeight = Stage.inst != null ? Stage.inst.height : layer.height;
                    if (stageHeight > 1f)
                    {
                        stagePos = new Vector2(unityPos.x, stageHeight - unityPos.y);
                        localPos = layer.GlobalToLocal(stagePos);
                        return !float.IsNaN(localPos.x) && !float.IsNaN(localPos.y);
                    }
                }
            }
            catch { }

            return false;
        }

        private static long HitTestMonsters(
            GComponent layer,
            Camera camera,
            IReadOnlyList<LiveMonsterInfo> monsters,
            Vector2 clickLocal,
            PredictorSettings settings)
        {
            if (monsters == null || monsters.Count == 0 || camera == null) return 0;

            var camTr = UnityObject.GetTransform(camera);
            var camPos = TransformService.GetPosition(camTr);
            var camForward = TransformService.GetForward(camTr);

            var boxes = new List<MonsterHitBox>(monsters.Count);
            for (int i = 0; i < monsters.Count; i++)
            {
                var m = monsters[i];
                if (m == null || m.State == null) continue;
                if (!settings.AllowNonAggressiveMonsters && !m.State.IsNormalAggressive) continue;

                // Refresh live visual bounds right on click so moving/stacked sprites are pixel-accurate
                var bounds = m.CharacterRef != null
                    ? SpriteBoundsReader.Capture(m.CharacterRef, m.FeetWorldPos, camera)
                    : m.VisualBounds;

                if (!TryProjectHitBox(layer, camera, camPos, camForward, m.State.MonsterId, bounds, out var hitBox))
                    continue;

                boxes.Add(hitBox);
            }

            return MonsterHitTester.PickMonster(new Point2(clickLocal.x, clickLocal.y), boxes, settings.ClickRadius);
        }

        private static bool TryProjectHitBox(
            GComponent layer,
            Camera camera,
            Vector3 camPos,
            Vector3 camForward,
            long monsterId,
            MonsterVisualBounds bounds,
            out MonsterHitBox hitBox)
        {
            hitBox = null;
            if (!bounds.HasCorners) return false;

            // Use the camera's actual near clip plane consistently with DashedOverlayRenderer,
            // clipping the 4 sprite perimeter edges so sprites completely behind nearClipPlane
            // can never be clicked while sprites straddling nearClipPlane project their visible portion.
            float nearClip = 0.01f;
            try { if (camera.nearClipPlane > 0f) nearClip = Math.Max(0.01f, camera.nearClipPlane); } catch { }
            float clippedMinDepth = Math.Max(0.001f, nearClip * 0.5f);

            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            int valid = 0;
            float depthSum = 0f;

            void AcceptPoint(Vector3 w)
            {
                float d = Vector3.Dot(w - camPos, camForward);
                if (d < clippedMinDepth) return;
                Vector2 p = layer.WorldToLocal(w, camera);
                if (float.IsNaN(p.x) || float.IsInfinity(p.x) || float.IsNaN(p.y) || float.IsInfinity(p.y))
                    return;
                if (p.x < minX) minX = p.x;
                if (p.y < minY) minY = p.y;
                if (p.x > maxX) maxX = p.x;
                if (p.y > maxY) maxY = p.y;
                depthSum += d;
                valid++;
            }

            Vector3 e0a = bounds.BL, e0b = bounds.BR;
            Vector3 e1a = bounds.BR, e1b = bounds.TR;
            Vector3 e2a = bounds.TR, e2b = bounds.TL;
            Vector3 e3a = bounds.TL, e3b = bounds.BL;

            for (int e = 0; e < 4; e++)
            {
                Vector3 a = e == 0 ? e0a : e == 1 ? e1a : e == 2 ? e2a : e3a;
                Vector3 b = e == 0 ? e0b : e == 1 ? e1b : e == 2 ? e2b : e3b;
                if (!DashGeometry.TryClipSegmentToNearPlane(
                        a.x, a.y, a.z, b.x, b.y, b.z,
                        camPos.x, camPos.y, camPos.z,
                        camForward.x, camForward.y, camForward.z,
                        nearClip,
                        out float ax, out float ay, out float az,
                        out float bx, out float by, out float bz,
                        out _, out _))
                {
                    continue;
                }
                AcceptPoint(new Vector3(ax, ay, az));
                AcceptPoint(new Vector3(bx, by, bz));
            }

            if (valid < 2) return false;

            float centerDepth = Vector3.Dot(bounds.CenterWorld - camPos, camForward);
            float effectiveDepth = centerDepth >= nearClip ? centerDepth : (depthSum / valid);

            // Keep the raw projected sprite bounds uninflated so MonsterHitTester directHits only matches
            // clicks truly inside a monster's sprite; zoomed-out small sprites remain easy to pick via nearMissRadius.
            hitBox = new MonsterHitBox
            {
                MonsterId = monsterId,
                Rect = new HitRect2D(minX, minY, maxX, maxY),
                CameraDepth = effectiveDepth
            };
            return true;
        }
    }
}
