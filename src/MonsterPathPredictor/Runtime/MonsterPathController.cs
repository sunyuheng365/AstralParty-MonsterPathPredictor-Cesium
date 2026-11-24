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
using MonsterPathPredictor.Config;
using MonsterPathPredictor.Core;

namespace MonsterPathPredictor.Runtime
{
    public sealed class MonsterPathController : ModBase
    {
        private readonly MonsterStateTracker _tracker = new MonsterStateTracker();
        private readonly MonsterPicker _picker = new MonsterPicker();
        private readonly MonsterPickerPopup _popup = new MonsterPickerPopup();
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
        private DateTime _selectedMissingSince;
        private bool _needsUnitSync;
        private bool _planDirty;

        public override string Name => "怪物寻路预测";
        public override string Version => "1.0.0";
        public override string Author => "sunyuheng365";
        public override string Description => "点击 PVE 怪物或按热键弹窗选择怪物，以动态虚线预览接下来 X 格的精确寻路规划，并在岔路口展示走向与多分支概率百分比。";

        public override void OnInitialize()
        {
            _settings = PredictorConfig.Load(Config, isInitial: true);
            _tracker.Start();

            _popup.MonsterSelected += OnPopupMonsterSelected;
            _popup.ClearRequested += OnPopupClearRequested;
            _popup.StepsDeltaRequested += OnPopupStepsDelta;

            UpdateEvents.LateUpdate += DrawAndTrack;
            Context?.RegisterCleanup(Stop);
            Log.Info("怪物寻路预测已启用：点击怪物模型任意处或按 " + _settings.PickerHotkey +
                     " 弹窗选怪（延伸 " + _settings.PreviewSteps + " 格，按 " + _settings.ClearHotkey + " 取消）。");
        }

        public override void OnConfigChanged()
        {
            _settings = PredictorConfig.Load(Config, isInitial: false);
            _needsUnitSync = true;
            _planDirty = true;
            if (!_settings.Enabled)
            {
                _tracker.SetEventCollectionEnabled(false);
                ClearSelectionAndOverlay();
                _popup.Hide();
            }
            Log.Info("配置已热重载：enabled=" + _settings.Enabled +
                     ", previewSteps=" + _settings.PreviewSteps +
                     ", dashSpeed=" + _settings.DashSpeed +
                     ", pickerHotkey=" + _settings.PickerHotkey +
                     ", postCombatMode=" + PredictorSettings.FormatPostCombatMode(_settings.PostCombatMode));
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
            _popup.MonsterSelected -= OnPopupMonsterSelected;
            _popup.ClearRequested -= OnPopupClearRequested;
            _popup.StepsDeltaRequested -= OnPopupStepsDelta;
            _tracker.Stop();
            ResetBattleState();
            _popup.Dispose();
            _renderer.Dispose();
        }

        private void OnPopupMonsterSelected(long monsterId)
        {
            _selectedMonsterId = (_selectedMonsterId == monsterId) ? 0 : monsterId;
            _selectedMissingSince = DateTime.MinValue;
            _planDirty = true;
            if (_selectedMonsterId != 0)
            {
                var picked = FindMonster(_selectedMonsterId);
                if (picked != null)
                {
                    Log.Info("弹窗已锁定怪物：" + picked.State.DisplayName + "（地块 #" + picked.State.CurrentLandId + "）");
                }
            }
        }

        private void OnPopupClearRequested()
        {
            ClearSelectionAndOverlay();
        }

        private void OnPopupStepsDelta(int delta)
        {
            int nextSteps = PredictorSettings.ClampInt(_settings.PreviewSteps + delta, 1, 30);
            if (nextSteps == _settings.PreviewSteps) return;
            _settings.PreviewSteps = nextSteps;
            _planDirty = true;
            try
            {
                if (Config != null)
                {
                    Config.Set("previewSteps", nextSteps);
                    Config.Save();
                }
            }
            catch { }
        }

        private void DrawAndTrack()
        {
            try
            {
                if (!_settings.Enabled || !TryGetActivePveSession(out var battle))
                {
                    _tracker.SetEventCollectionEnabled(false);
                    if (_selectedMonsterId != 0 || _board != null || _popup.IsOpen) ResetBattleState();
                    return;
                }

                // Enable game event ingestion only while actively inside an enabled PVE battle
                // (stays active across FightWindow so combat events resolve accurately upon return).
                _tracker.SetEventCollectionEnabled(true);

                // Keep authoritative turn-boundary tracking advancing even while FightWindow / settings popups
                // or transient camera hiccups temporarily suspend visual overlay rendering (prevents A->B->A miss).
                if (_tracker.ObserveTurnBoundary(battle))
                {
                    ClearSelectionAndOverlay();
                }

                // Combat window or in-battle pause/settings popup: temporarily hide overlay AND popup
                // without dropping the locked monster so tracking resumes automatically afterwards.
                if (IsTemporaryOverlayOpen())
                {
                    _picker.CancelPendingPointer();
                    _renderer.HideAll();
                    _popup.SetTemporarilyHidden(true);
                    _needsUnitSync = true;
                    _planDirty = true;
                    return;
                }

                // Transient camera/LandManager hiccup within the same active PVE session:
                // hide overlay briefly without resetting selection or tracker state.
                if (!TryGetRenderContext(out var landManager, out var camera))
                {
                    _picker.CancelPendingPointer();
                    _renderer.HideAll();
                    _popup.SetTemporarilyHidden(true);
                    _needsUnitSync = true;
                    return;
                }

                _popup.SetTemporarilyHidden(false);

                var now = DateTime.UtcNow;

                if (_board == null || now >= _nextBoardScan)
                {
                    _nextBoardScan = now.AddMilliseconds(500);
                    var nextBoard = _tracker.CaptureBoard(landManager);
                    if (_board == null || !_board.SameTopology(nextBoard))
                    {
                        _board = nextBoard;
                        _needsUnitSync = true;
                        _planDirty = true;
                    }
                }

                // Detect tile-to-tile movement of the tracked monster immediately (0-frame lag)
                if (_selectedMonsterId != 0 && !_needsUnitSync)
                {
                    var prevSel = FindMonster(_selectedMonsterId);
                    if (prevSel != null && prevSel.CharacterRef != null && UnityObject.IsAlive(prevSel.CharacterRef))
                    {
                        try
                        {
                            int liveLandId = prevSel.CharacterRef.standLand != null ? prevSel.CharacterRef.standLand.Id : -1;
                            if (liveLandId >= 0 && liveLandId != prevSel.State.CurrentLandId)
                            {
                                _needsUnitSync = true;
                            }
                        }
                        catch { }
                    }
                }

                // Strictly throttle unit sampling (~120 ms) even when idle (no selected monster)
                if (now >= _nextUnitSync || _needsUnitSync)
                {
                    _nextUnitSync = now.AddMilliseconds(120);
                    _needsUnitSync = false;
                    _tracker.SyncAndSnapshot(_board, battle, camera, _settings, out _monsters, out _players, out bool matchChanged);
                    if (matchChanged)
                    {
                        ClearSelectionAndOverlay();
                    }
                    _planDirty = true;
                }

                var layer = _renderer.EnsureLayer();
                if (layer == null) return;

                long beforeSelection = _selectedMonsterId;
                bool selectionChanged = _picker.ProcessInput(layer, camera, _monsters, _settings, _popup, ref _selectedMonsterId);
                if (selectionChanged)
                {
                    _selectedMissingSince = DateTime.MinValue;
                    _planDirty = true;
                    if (_selectedMonsterId != 0 && _selectedMonsterId != beforeSelection)
                    {
                        var picked = FindMonster(_selectedMonsterId);
                        if (picked != null)
                        {
                            Log.Info("已选中怪物：" + picked.State.DisplayName + "（地块 #" + picked.State.CurrentLandId + "），规划 " + _settings.PreviewSteps + " 格");
                        }
                    }
                }

                if (_popup.IsOpen)
                {
                    _popup.Refresh(_monsters, _selectedMonsterId, _settings);
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
                    if (_tracker.IsMonsterConfirmedDead(_selectedMonsterId))
                    {
                        ClearSelectionAndOverlay();
                        return;
                    }

                    // Monster is still alive in the battle roster (only its CharacterInst/model is
                    // momentarily unrendered/rebuilding): keep lock & memory intact, just hide overlay.
                    if (_tracker.IsMonsterInAliveRoster(_selectedMonsterId))
                    {
                        _selectedMissingSince = DateTime.MinValue;
                        _renderer.HideAll();
                        return;
                    }

                    // Roster temporarily empty/unavailable: brief grace window before clearing
                    if (_selectedMissingSince == DateTime.MinValue)
                    {
                        _selectedMissingSince = now;
                    }
                    else if ((now - _selectedMissingSince).TotalMilliseconds >= 350.0)
                    {
                        ClearSelectionAndOverlay();
                        return;
                    }

                    _renderer.HideAll();
                    return;
                }
                _selectedMissingSince = DateTime.MinValue;

                if (_planDirty || _cachedPlan == null || _cachedPlan.MonsterId != _selectedMonsterId)
                {
                    _planDirty = false;
                    _cachedPlan = MonsterPathEngine.Plan(
                        _board,
                        selected.State,
                        _players,
                        new PlanOptions
                        {
                            MaxSteps = _settings.PreviewSteps,
                            SimulateCombatOnFork = _settings.SimulateCombatOnFork,
                            PostCombatMode = _settings.PostCombatMode
                        });
                }

                // Refresh live sprite bounds every frame so the header badge smoothly tracks moving monsters
                if (selected.CharacterRef != null && UnityObject.IsAlive(selected.CharacterRef))
                {
                    selected.VisualBounds = SpriteBoundsReader.Capture(selected.CharacterRef, selected.FeetWorldPos, camera);
                }

                _renderer.Render(camera, _board, selected, _cachedPlan, _settings);
            }
            catch (Exception ex)
            {
                _cachedPlan = null;
                _renderer.HideAll();
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

        private static bool TryGetActivePveSession(out BattleLogic battle)
        {
            battle = null;

            if (!SimpleSingletonProvider<global::Core.Scene.SceneManager>.hasInstance ||
                SimpleSingletonProvider<global::Core.Scene.SceneManager>.inst.currentType.Value != SceneType.Battle)
                return false;

            if (!SimpleSingletonProvider<GameLogicManager>.hasInstance) return false;
            var gm = SimpleSingletonProvider<GameLogicManager>.inst;
            var room = gm.room;
            if (room == null || room.curRoomInfo == null || !room.curRoomInfo.IsPVE()) return false;

            battle = gm.battle;
            if (battle == null) return false;

            return !IsBattleSettled();
        }

        private static bool TryGetRenderContext(out LandManager landManager, out Camera camera)
        {
            landManager = null;
            camera = null;

            var sceneCtrl = BattleSceneController.inst;
            if (sceneCtrl == null) return false;
            camera = sceneCtrl.mainCamera;
            if (!UnityObject.IsAlive(camera)) return false;

            if (!SimpleSingletonProvider<LandManager>.hasInstance) return false;
            landManager = SimpleSingletonProvider<LandManager>.inst;
            return landManager != null && landManager.NodeDict != null && landManager.NodeDict.Count > 0;
        }

        private static bool IsBattleSettled()
        {
            try
            {
                if (!UIManager.hasInstance) return false;
                return UIManager.inst.currentPanel is BattleSettlementPanel;
            }
            catch { }
            return false;
        }

        private static bool IsTemporaryOverlayOpen()
        {
            try
            {
                if (!UIManager.hasInstance) return false;
                var ui = UIManager.inst;
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
            _selectedMissingSince = DateTime.MinValue;
            _cachedPlan = null;
            _renderer.HideAll();
            _renderer.ClearStableHeaderCache();
        }

        private void ResetBattleState()
        {
            ClearSelectionAndOverlay();
            _picker.CancelPendingPointer();
            _popup.Hide();
            _board = null;
            _monsters.Clear();
            _players.Clear();
            _nextBoardScan = DateTime.MinValue;
            _nextUnitSync = DateTime.MinValue;
            _needsUnitSync = false;
            _planDirty = false;
            _tracker.SetEventCollectionEnabled(false);
            _tracker.Reset();
        }
    }
}
