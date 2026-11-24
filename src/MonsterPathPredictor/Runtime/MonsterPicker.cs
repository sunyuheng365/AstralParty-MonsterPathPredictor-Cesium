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

            // 2. Clear hotkey (default Escape): close popup first if open, otherwise clear selection
            if (IsHotkeyDown(settings.ClearHotkey))
            {
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

            float depth = Vector3.Dot(bounds.CenterWorld - camPos, camForward);
            if (depth <= 0.1f) return false;

            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            int valid = 0;

            for (int i = 0; i < 4; i++)
            {
                Vector3 c = i == 0 ? bounds.BL : i == 1 ? bounds.BR : i == 2 ? bounds.TL : bounds.TR;
                if (Vector3.Dot(c - camPos, camForward) <= 0.05f) continue;
                Vector2 p = layer.WorldToLocal(c, camera);
                if (float.IsNaN(p.x) || float.IsInfinity(p.x) || float.IsNaN(p.y) || float.IsInfinity(p.y))
                    continue;
                if (p.x < minX) minX = p.x;
                if (p.y < minY) minY = p.y;
                if (p.x > maxX) maxX = p.x;
                if (p.y > maxY) maxY = p.y;
                valid++;
            }

            if (valid < 2) return false;

            // Ensure a sensible minimum clickable box if monster is zoomed far away
            if (maxX - minX < 28f)
            {
                float cx = (minX + maxX) * 0.5f;
                minX = cx - 14f;
                maxX = cx + 14f;
            }
            if (maxY - minY < 36f)
            {
                float cy = (minY + maxY) * 0.5f;
                minY = cy - 18f;
                maxY = cy + 18f;
            }

            hitBox = new MonsterHitBox
            {
                MonsterId = monsterId,
                Rect = new HitRect2D(minX, minY, maxX, maxY),
                CameraDepth = depth
            };
            return true;
        }
    }
}
