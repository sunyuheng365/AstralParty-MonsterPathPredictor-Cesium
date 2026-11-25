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
        private readonly VirtualPlayerSandbox _virtualSandbox = new VirtualPlayerSandbox();

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
        private bool _savingSelfConfig;

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
            _popup.VirtualPlayerSelected += OnVirtualPlayerSelected;
            _popup.VirtualMoveRequested += OnVirtualMoveRequested;
            _popup.VirtualUndoRequested += OnVirtualUndoRequested;
            _popup.VirtualResetPlayerRequested += OnVirtualResetPlayerRequested;
            _popup.VirtualResetAllRequested += OnVirtualResetAllRequested;
            _popup.VirtualBoardPickToggled += OnVirtualBoardPickToggled;
            _popup.StyleMutationRequested += OnStyleMutationRequested;
            _popup.StyleResetRequested += OnStyleResetRequested;

            UpdateEvents.LateUpdate += DrawAndTrack;
            Context?.RegisterCleanup(Stop);
            Log.Info("怪物寻路预测已启用：点击怪物模型任意处或按 " + _settings.PickerHotkey +
                     " 弹窗选怪/虚拟走位/自定义路线样式（延伸 " + _settings.PreviewSteps + " 格，按 " + _settings.ClearHotkey + " 取消）。");
        }

        public override void OnConfigChanged()
        {
            if (_savingSelfConfig) return;
            _settings = PredictorConfig.Load(Config, isInitial: false);
            _needsUnitSync = true;
            _planDirty = true;
            _popup.Invalidate();
            if (!_settings.Enabled)
            {
                _tracker.SetEventCollectionEnabled(false);
                ClearSelectionAndOverlay();
                _virtualSandbox.ResetAll(disarmBoardPick: true);
                _popup.Hide();
            }
            Log.Info("配置已热重载：enabled=" + _settings.Enabled +
                     ", previewSteps=" + _settings.PreviewSteps +
                     ", lineWidth=" + _settings.LineWidth +
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
            _popup.VirtualPlayerSelected -= OnVirtualPlayerSelected;
            _popup.VirtualMoveRequested -= OnVirtualMoveRequested;
            _popup.VirtualUndoRequested -= OnVirtualUndoRequested;
            _popup.VirtualResetPlayerRequested -= OnVirtualResetPlayerRequested;
            _popup.VirtualResetAllRequested -= OnVirtualResetAllRequested;
            _popup.VirtualBoardPickToggled -= OnVirtualBoardPickToggled;
            _popup.StyleMutationRequested -= OnStyleMutationRequested;
            _popup.StyleResetRequested -= OnStyleResetRequested;
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
            if (_selectedMonsterId != 0)
            {
                ClearSelectionAndOverlay();
            }
            else if (_virtualSandbox.HasOverrides)
            {
                OnVirtualResetAllRequested();
            }
        }

        private void OnPopupStepsDelta(int delta)
        {
            int nextSteps = PredictorSettings.ClampInt(_settings.PreviewSteps + delta, 1, 30);
            if (nextSteps == _settings.PreviewSteps) return;
            _settings.PreviewSteps = nextSteps;
            _planDirty = true;
            _popup.Invalidate();
            SaveCurrentStyleSettings();
        }

        private void OnVirtualPlayerSelected(long playerId)
        {
            _virtualSandbox.SelectPlayer(playerId);
            _popup.Invalidate();
        }

        private void OnVirtualMoveRequested(long playerId, int targetLandId)
        {
            var player = FindPlayer(playerId);
            if (player == null || !player.IsAlive || _board == null) return;
            if (_virtualSandbox.SetVirtualLand(playerId, targetLandId, player.LandId, _board))
            {
                _planDirty = true;
                _popup.Invalidate();
            }
        }

        private void OnVirtualUndoRequested(long playerId)
        {
            var player = FindPlayer(playerId);
            int liveLand = player != null ? player.LandId : -1;
            if (_virtualSandbox.Undo(playerId, liveLand, _board))
            {
                _planDirty = true;
                _popup.Invalidate();
            }
        }

        private void OnVirtualResetPlayerRequested(long playerId)
        {
            if (_virtualSandbox.ResetPlayer(playerId))
            {
                _planDirty = true;
                _popup.Invalidate();
            }
        }

        private void OnVirtualResetAllRequested()
        {
            if (_virtualSandbox.ResetAll(disarmBoardPick: false))
            {
                _planDirty = true;
                _popup.Invalidate();
            }
        }

        private void OnVirtualBoardPickToggled(bool armed)
        {
            _virtualSandbox.BoardPickArmed = armed;
            _popup.Invalidate();
        }

        private void OnStyleMutationRequested(Action<PredictorSettings> mutate)
        {
            if (mutate == null) return;
            mutate(_settings);
            _settings.ClampInPlace();
            _planDirty = true;
            _popup.Invalidate();
            SaveCurrentStyleSettings();
        }

        private void OnStyleResetRequested()
        {
            _settings.ResetStyleToDefaults();
            _settings.ClampInPlace();
            _planDirty = true;
            _popup.Invalidate();
            SaveCurrentStyleSettings();
        }

        private void SaveCurrentStyleSettings()
        {
            if (Config == null) return;
            try
            {
                _savingSelfConfig = true;
                PredictorConfig.SaveStyle(Config, _settings);
            }
            catch { }
            finally
            {
                _savingSelfConfig = false;
            }
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
                // On a detected match switch, immediately invalidate cached board/unit state and scan timers
                // so stale previous-match monsters can never be picked or planned in this frame.
                if (_tracker.ObserveTurnBoundary(battle))
                {
                    InvalidateMatchCache();
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
                        bool layoutChanged = _board != null;
                        _board = nextBoard;
                        if (layoutChanged)
                        {
                            _tracker.NotifyBoardTopologyChanged();
                            _virtualSandbox.Reconcile(_players, _board, _tracker.LocalPlayerId);
                        }
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
                        // Board/units just switched matches between ObserveTurnBoundary and SyncAndSnapshot:
                        // drop stale board/selection immediately and wait for next frame's fresh capture before input.
                        InvalidateMatchCache();
                        return;
                    }
                    _virtualSandbox.Reconcile(_players, _board, _tracker.LocalPlayerId);
                    _planDirty = true;
                }

                var layer = _renderer.EnsureLayer();
                if (layer == null) return;

                long beforeSelection = _selectedMonsterId;
                bool virtualPickArmed = _virtualSandbox.BoardPickArmed && _virtualSandbox.SelectedPlayerId != 0;
                bool selectionChanged = _picker.ProcessInput(
                    layer,
                    camera,
                    _board,
                    _monsters,
                    _settings,
                    _popup,
                    virtualPickArmed,
                    ref _selectedMonsterId,
                    out int pickedVirtualLandId,
                    out bool clearVirtualRequested);

                if (pickedVirtualLandId >= 0 && _virtualSandbox.SelectedPlayerId != 0)
                {
                    OnVirtualMoveRequested(_virtualSandbox.SelectedPlayerId, pickedVirtualLandId);
                }
                if (clearVirtualRequested && _virtualSandbox.HasOverrides)
                {
                    OnVirtualResetAllRequested();
                }

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
                    _popup.Refresh(_monsters, _players, _board, _virtualSandbox, _tracker.LocalPlayerId, _selectedMonsterId, _settings);
                }

                var virtualGhosts = _virtualSandbox.BuildGhosts(_players, _board, _tracker.LocalPlayerId);

                if (_selectedMonsterId == 0)
                {
                    _cachedPlan = null;
                    if (virtualGhosts.Count > 0)
                        _renderer.Render(camera, _board, null, null, _settings, virtualGhosts);
                    else
                        _renderer.HideAll();
                    return;
                }

                var selected = FindMonster(_selectedMonsterId);
                if (selected == null)
                {
                    if (_tracker.IsMonsterConfirmedDead(_selectedMonsterId))
                    {
                        ClearSelectionAndOverlay();
                        if (virtualGhosts.Count > 0)
                            _renderer.Render(camera, _board, null, null, _settings, virtualGhosts);
                        return;
                    }

                    // Monster is still alive in the battle roster (only its CharacterInst/model is
                    // momentarily unrendered/rebuilding): keep lock & memory intact, hide path overlay only.
                    if (_tracker.IsMonsterInAliveRoster(_selectedMonsterId))
                    {
                        _selectedMissingSince = DateTime.MinValue;
                        if (virtualGhosts.Count > 0)
                            _renderer.Render(camera, _board, null, null, _settings, virtualGhosts);
                        else
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
                        if (virtualGhosts.Count > 0)
                            _renderer.Render(camera, _board, null, null, _settings, virtualGhosts);
                        return;
                    }

                    if (virtualGhosts.Count > 0)
                        _renderer.Render(camera, _board, null, null, _settings, virtualGhosts);
                    else
                        _renderer.HideAll();
                    return;
                }
                _selectedMissingSince = DateTime.MinValue;

                if (_planDirty || _cachedPlan == null || _cachedPlan.MonsterId != _selectedMonsterId)
                {
                    _planDirty = false;
                    var effectivePlayers = _virtualSandbox.HasOverrides
                        ? _virtualSandbox.ApplyOverrides(_players, _board)
                        : _players;
                    _cachedPlan = MonsterPathEngine.Plan(
                        _board,
                        selected.State,
                        effectivePlayers,
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

                _renderer.Render(camera, _board, selected, _cachedPlan, _settings, virtualGhosts);
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

        private TrackedPlayer FindPlayer(long playerId)
        {
            for (int i = 0; i < _players.Count; i++)
            {
                if (_players[i] != null && _players[i].PlayerId == playerId) return _players[i];
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

        private void InvalidateMatchCache()
        {
            ClearSelectionAndOverlay();
            _virtualSandbox.ResetAll(disarmBoardPick: true);
            _picker.CancelPendingPointer();
            _board = null;
            _monsters.Clear();
            _players.Clear();
            _nextBoardScan = DateTime.MinValue;
            _nextUnitSync = DateTime.MinValue;
            _needsUnitSync = true;
            _planDirty = true;
            _popup.Invalidate();
        }

        private void ResetBattleState()
        {
            ClearSelectionAndOverlay();
            _virtualSandbox.ResetAll(disarmBoardPick: true);
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
