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
    /// Handles clicking a monster on the PVE board to select/toggle its path forecast,
    /// using FairyGUI stage coordinates and camera projection without calling raw Unity ECalls.
    /// </summary>
    internal sealed class MonsterPicker
    {
        private int _lastProcessedFrame = -1;

        public bool ProcessInput(
            GComponent layer,
            Camera camera,
            IReadOnlyList<LiveMonsterInfo> monsters,
            PredictorSettings settings,
            ref long selectedMonsterId)
        {
            int frame = UnityTime.FrameCount;
            if (frame == _lastProcessedFrame) return false;
            _lastProcessedFrame = frame;

            // Check clear hotkey (default Escape)
            if (selectedMonsterId != 0 && IsClearHotkeyDown(settings.ClearHotkey))
            {
                selectedMonsterId = 0;
                return true;
            }

            if (!InputService.IsMouseButtonPressed(0)) return false;

            // Ignore clicks consumed by interactive UI buttons/panels/cards
            if (IsPointerOverBlockingUi()) return false;

            if (!TryGetLayerMousePosition(layer, out Vector2 clickLocal)) return false;

            long hitMonsterId = HitTestMonsters(layer, camera, monsters, clickLocal, settings);
            if (hitMonsterId != 0)
            {
                // Click same monster to toggle off; click another monster to switch immediately
                selectedMonsterId = (selectedMonsterId == hitMonsterId) ? 0 : hitMonsterId;
                return true;
            }

            if (selectedMonsterId != 0 && settings.ClearOnEmptyClick)
            {
                selectedMonsterId = 0;
                return true;
            }

            return false;
        }

        private static bool IsClearHotkeyDown(string keyName)
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

                // Allow clicking through full-screen non-blocking battle HUD container if needed,
                // but block clicks that land on actual interactive buttons/lists/cards/windows.
                string name = target.name ?? string.Empty;
                if (name == "GRoot" || name == "Stage") return false;
                return target.touchable;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetLayerMousePosition(GComponent layer, out Vector2 localPos)
        {
            localPos = Vector2.zero;
            if (layer == null) return false;

            try
            {
                // Stage.inst.touchPosition is already in FairyGUI Stage top-left global coordinates
                if (Stage.inst != null)
                {
                    Vector2 stagePos = Stage.inst.touchPosition;
                    if (!float.IsNaN(stagePos.x) && !float.IsNaN(stagePos.y))
                    {
                        localPos = layer.GlobalToLocal(stagePos);
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
                        localPos = layer.GlobalToLocal(new Vector2(unityPos.x, stageHeight - unityPos.y));
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

            float maxDist = settings.ClickRadius;
            float bestDist = maxDist;
            long bestId = 0;

            for (int i = 0; i < monsters.Count; i++)
            {
                var m = monsters[i];
                if (m == null || m.State == null) continue;
                if (!settings.AllowNonAggressiveMonsters && !m.State.IsNormalAggressive) continue;

                // Test distance to both body center and tile feet (monster models stand above their tile)
                float dBody = ProjectedDistance(layer, camera, camPos, camForward, m.BodyWorldPos, clickLocal);
                float dFeet = ProjectedDistance(layer, camera, camPos, camForward, m.FeetWorldPos, clickLocal);
                float d = Math.Min(dBody, dFeet);

                if (d < bestDist)
                {
                    bestDist = d;
                    bestId = m.State.MonsterId;
                }
            }

            return bestId;
        }

        private static float ProjectedDistance(
            GComponent layer,
            Camera camera,
            Vector3 camPos,
            Vector3 camForward,
            Vector3 world,
            Vector2 clickLocal)
        {
            if (Vector3.Dot(world - camPos, camForward) <= 0.1f) return float.MaxValue;
            Vector2 pt = layer.WorldToLocal(world, camera);
            if (float.IsNaN(pt.x) || float.IsInfinity(pt.x) || float.IsNaN(pt.y) || float.IsInfinity(pt.y))
                return float.MaxValue;
            return Vector2.Distance(pt, clickLocal);
        }
    }
}
