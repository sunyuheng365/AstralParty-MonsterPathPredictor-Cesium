using System;
using System.Collections.Generic;
using CesiumLoader.SDK.Engine;
using CesiumLoader.SDK.Events;
using CesiumLoader.SDK.Mods;
using Core.Scene;
using Core.Unit;
using GameLogic;
using Tools;
using UI;
using UnityEngine;
using UnityEngine.SceneManagement;
using MonsterPathPredictor.Config;
using MonsterPathPredictor.Core;

namespace MonsterPathPredictor.Runtime
{
    public sealed class MonsterPathController : ModBase
    {
        private readonly MonsterStateTracker _tracker = new MonsterStateTracker();
        private readonly MonsterPicker _picker = new MonsterPicker();
        private readonly DashedOverlayRenderer _renderer = new DashedOverlayRenderer();

        private PredictorSettings _settings = new PredictorSettings();
        private BoardGraph _board;
        private List<LiveMonsterInfo> _monsters = new List<LiveMonsterInfo>();
        private List<TrackedPlayer> _players = new List<TrackedPlayer>();

        private long _selectedMonsterId;
        private PathPlan _cachedPlan;

        private DateTime _nextBoardScan;
        private DateTime _nextUnitSync;
        private DateTime _nextWarnAt;
        private bool _forceReplan;

        public override string Name => "怪物寻路预测";
        public override string Version => "1.0.0";
        public override string Author => "sunyuheng365";
        public override string Description => "点击 PVE 怪物以虚线预览接下来 X 格的精确寻路规划，在岔路口展示走向与多分支概率百分比。";

        public override void OnInitialize()
        {
            _settings = PredictorConfig.Load(Config, isInitial: true);
            _tracker.Start();
            UpdateEvents.LateUpdate += DrawAndTrack;
            Context?.RegisterCleanup(Stop);
            Log.Info("怪物寻路预测已启用：点击 PVE 怪物显示虚线规划（默认延伸 " + _settings.PreviewSteps + " 格，按 " + _settings.ClearHotkey + " 取消）。");
        }

        public override void OnConfigChanged()
        {
            _settings = PredictorConfig.Load(Config, isInitial: false);
            _forceReplan = true;
            if (!_settings.Enabled)
            {
                ClearSelectionAndOverlay();
            }
            Log.Info("配置已热重载：enabled=" + _settings.Enabled + ", previewSteps=" + _settings.PreviewSteps + ", fontSize=" + _settings.FontSize);
        }

        public override void OnSceneUnloaded(UnityEngine.SceneManagement.Scene scene)
        {
            ResetBattleState();
        }

        public override void OnUnload()
        {
            Stop();
        }

        private void Stop()
        {
            UpdateEvents.LateUpdate -= DrawAndTrack;
            _tracker.Stop();
            ResetBattleState();
            _renderer.Dispose();
        }

        private void DrawAndTrack()
        {
            try
            {
                if (!_settings.Enabled || !TryGetPveBattleContext(out var battle, out var landManager, out var camera))
                {
                    if (_selectedMonsterId != 0 || _board != null) ResetBattleState();
                    return;
                }

                var now = DateTime.UtcNow;

                // Refresh board topology periodically (~0.5s)
                if (_board == null || now >= _nextBoardScan)
                {
                    _nextBoardScan = now.AddMilliseconds(500);
                    var nextBoard = _tracker.CaptureBoard(landManager);
                    if (_board == null || !_board.SameTopology(nextBoard))
                    {
                        _board = nextBoard;
                        _forceReplan = true;
                    }
                }

                // Sync monsters & players frequently (~120ms) so clicks and live board changes stay responsive
                if (now >= _nextUnitSync || _forceReplan)
                {
                    _nextUnitSync = now.AddMilliseconds(120);
                    _tracker.SyncAndSnapshot(_board, battle, _settings, out _monsters, out _players);
                    _forceReplan = true;
                }

                var layer = _renderer.EnsureLayer();
                if (layer == null) return;

                // Process click-to-select / clear hotkey every frame
                long beforeSelection = _selectedMonsterId;
                bool selectionChanged = _picker.ProcessInput(layer, camera, _monsters, _settings, ref _selectedMonsterId);
                if (selectionChanged)
                {
                    _forceReplan = true;
                    if (_selectedMonsterId != 0 && _selectedMonsterId != beforeSelection)
                    {
                        var picked = FindMonster(_selectedMonsterId);
                        if (picked != null)
                        {
                            Log.Info("已选中怪物：" + picked.State.Name + "（地块 " + picked.State.CurrentLandId + "），规划 " + _settings.PreviewSteps + " 格");
                        }
                    }
                }

                if (_selectedMonsterId == 0)
                {
                    _cachedPlan = null;
                    _renderer.HideAll();
                    return;
                }

                var selected = FindMonster(_selectedMonsterId);
                if (selected == null)
                {
                    // Monster died or disappeared from board
                    ClearSelectionAndOverlay();
                    return;
                }

                if (_forceReplan || _cachedPlan == null || _cachedPlan.MonsterId != _selectedMonsterId)
                {
                    _forceReplan = false;
                    _cachedPlan = MonsterPathEngine.Plan(
                        _board,
                        selected.State,
                        _players,
                        new PlanOptions
                        {
                            MaxSteps = _settings.PreviewSteps,
                            SimulateCombatOnFork = _settings.SimulateCombatOnFork
                        });
                }

                _renderer.Render(camera, _board, selected, _cachedPlan, _settings);
            }
            catch (Exception ex)
            {
                ClearSelectionAndOverlay();
                if (DateTime.UtcNow >= _nextWarnAt)
                {
                    _nextWarnAt = DateTime.UtcNow.AddSeconds(15);
                    Log.Warn("怪物寻路预测暂不可用：" + ex.Message);
                }
            }
        }

        private LiveMonsterInfo FindMonster(long monsterId)
        {
            for (int i = 0; i < _monsters.Count; i++)
            {
                if (_monsters[i].State.MonsterId == monsterId) return _monsters[i];
            }
            return null;
        }

        private static bool TryGetPveBattleContext(out BattleLogic battle, out LandManager landManager, out Camera camera)
        {
            battle = null;
            landManager = null;
            camera = null;

            if (!SimpleSingletonProvider<global::Core.Scene.SceneManager>.hasInstance ||
                SimpleSingletonProvider<global::Core.Scene.SceneManager>.inst.currentType.Value != SceneType.Battle)
                return false;

            if (!SimpleSingletonProvider<GameLogicManager>.hasInstance) return false;
            var gm = SimpleSingletonProvider<GameLogicManager>.inst;
            var room = gm.room;
            if (room == null || room.curRoomInfo == null || !room.curRoomInfo.IsPVE()) return false;

            battle = gm.battle;
            if (battle == null) return false;

            if (IsOverlayBlocked()) return false;

            var sceneCtrl = BattleSceneController.inst;
            if (sceneCtrl == null) return false;
            camera = sceneCtrl.mainCamera;
            if (!UnityObject.IsAlive(camera)) return false;

            if (!SimpleSingletonProvider<LandManager>.hasInstance) return false;
            landManager = SimpleSingletonProvider<LandManager>.inst;
            return landManager != null && landManager.NodeDict != null && landManager.NodeDict.Count > 0;
        }

        private static bool IsOverlayBlocked()
        {
            try
            {
                if (!UIManager.hasInstance) return false;
                var ui = UIManager.inst;
                if (ui.currentPanel is BattleSettlementPanel) return true;
                if (ui.propUpWindows != null)
                {
                    foreach (var popup in ui.propUpWindows)
                    {
                        if (popup == null || !popup.isShowing) continue;
                        if (popup is FightWindow || popup is SettingInBattleWindow || popup is SinglePlayerSettingInBattleWindow)
                            return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private void ClearSelectionAndOverlay()
        {
            _selectedMonsterId = 0;
            _cachedPlan = null;
            _renderer.HideAll();
        }

        private void ResetBattleState()
        {
            ClearSelectionAndOverlay();
            _board = null;
            _monsters.Clear();
            _players.Clear();
            _nextBoardScan = DateTime.MinValue;
            _nextUnitSync = DateTime.MinValue;
            _forceReplan = false;
            _tracker.Reset();
        }
    }
}
