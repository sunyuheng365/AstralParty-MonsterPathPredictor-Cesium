using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private readonly List<VirtualPlayerGhost> _ghostBuf = new List<VirtualPlayerGhost>(4);

        private PredictorSettings _settings = new PredictorSettings();
        private BoardGraph _board;
        private List<LiveMonsterInfo> _monsters = new List<LiveMonsterInfo>();
        private List<TrackedPlayer> _players = new List<TrackedPlayer>();

        private long _selectedMonsterId;
        private PathPlan _cachedPlan;

        private double _nextBoardScanSec;
        private double _nextUnitSyncSec;
        private double _nextWarnSec;
        private double _selectedMissingSinceSec = -1.0;
        private bool _needsUnitSync;
        private bool _awaitingPostLayoutUnitSync;
        private bool _planDirty;
        private bool _savingSelfConfig;
        private bool _hasPendingStyleSave;
        private int _activeBattleSceneHandle = -1;
        private string _lastPlanInputSig = string.Empty;

        private static double MonotonicSeconds()
        {
            return Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        }

        public override string Name => "怪物寻路预测";
        public override string Version => "1.0.0";
        public override string Author => "sunyuheng365";
        public override string Description => "点击 PVE 怪物或按热键弹窗选择怪物，以动态虚线预览接下来 X 格的精确寻路规划，并在岔路口展示走向与多分支概率百分比。";

        public override void OnInitialize()
        {
            _settings = PredictorConfig.Load(Config, isInitial: true, out bool saveOk);
            _popup.SetStyleSaveFailed(!saveOk);
            _tracker.Start();

            _popup.MonsterSelected += OnPopupMonsterSelected;
            _popup.ClearRequested += OnPopupClearRequested;
            _popup.StepsDeltaRequested += OnPopupStepsDelta;
            _popup.VirtualPlayerSelected += OnVirtualPlayerSelected;
            _popup.VirtualStepNeighborRequested += OnVirtualStepNeighborRequested;
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
            var prev = _settings;
            _settings = PredictorConfig.Load(Config, isInitial: false, out bool saveOk);
            _popup.SetStyleSaveFailed(!saveOk);
            _needsUnitSync = true;
            if (!_settings.PlanInputsEqual(prev))
            {
                _planDirty = true;
            }
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
            // Only tear down battle state when the owning battle scene itself unloaded, battle settled,
            // or SceneManager explicitly left SceneType.Battle (never on an additive scene unload during a room lookup hiccup).
            bool leftBattleSceneType = false;
            try
            {
                if (SimpleSingletonProvider<global::Core.Scene.SceneManager>.hasInstance)
                {
                    leftBattleSceneType = SimpleSingletonProvider<global::Core.Scene.SceneManager>.inst.currentType.Value != SceneType.Battle;
                }
            }
            catch { }

            if ((_activeBattleSceneHandle >= 0 && scene.handle == _activeBattleSceneHandle) ||
                leftBattleSceneType ||
                IsBattleSettled())
            {
                ResetBattleState();
            }
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
            _popup.VirtualStepNeighborRequested -= OnVirtualStepNeighborRequested;
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
            _selectedMissingSinceSec = -1.0;
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
            else if (_virtualSandbox.HasAnyState)
            {
                OnVirtualResetAllRequested();
            }
        }

        private void OnPopupStepsDelta(int delta)
        {
            ApplyStyleMutation(s => s.PreviewSteps = PredictorSettings.ClampInt(s.PreviewSteps + delta, 1, 30));
        }

        private void OnVirtualPlayerSelected(long playerId)
        {
            _picker.CancelRightClickOnly();
            _virtualSandbox.SelectPlayer(playerId);
            _popup.Invalidate();
        }

        private void OnVirtualStepNeighborRequested(long playerId, int expectedSourceLandId, int targetLandId, int expectedLayoutGen)
        {
            if (_awaitingPostLayoutUnitSync || _board == null) return;
            if (expectedLayoutGen != _virtualSandbox.LayoutGeneration) return;
            var player = FindPlayer(playerId);
            if (player == null || !player.IsAlive) return;
            int currentEffective = _virtualSandbox.GetEffectiveLandId(player);
            if (currentEffective != expectedSourceLandId) return;
            if (!_board.IsValidNeighbor(expectedSourceLandId, targetLandId)) return;

            if (_virtualSandbox.SetVirtualLand(playerId, targetLandId, player.LandId, _board))
            {
                _planDirty = true;
                _popup.Invalidate();
            }
        }

        private void OnVirtualMoveRequested(long playerId, int targetLandId)
        {
            if (_awaitingPostLayoutUnitSync || _board == null) return;
            var player = FindPlayer(playerId);
            if (player == null || !player.IsAlive) return;
            if (_virtualSandbox.SetVirtualLand(playerId, targetLandId, player.LandId, _board))
            {
                _planDirty = true;
                _popup.Invalidate();
            }
        }

        private void OnVirtualUndoRequested(long playerId)
        {
            if (_awaitingPostLayoutUnitSync || _board == null) return;
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
            _picker.CancelRightClickOnly();
            _virtualSandbox.SetBoardPickArmed(armed, _players);
            _popup.Invalidate();
        }

        private void OnStyleMutationRequested(Action<PredictorSettings> mutate)
        {
            if (mutate == null) return;
            ApplyStyleMutation(mutate, isFullReset: false);
        }

        private void OnStyleResetRequested()
        {
            ApplyStyleMutation(s => s.ResetStyleToDefaults(), isFullReset: true);
        }

        private void ApplyStyleMutation(Action<PredictorSettings> mutate, bool isFullReset = false)
        {
            if (mutate == null) return;

            // Reload any pending external disk edits first. If reload fails, abort persistence so we never
            // overwrite a user's external disk file with stale in-memory state.
            bool externalChanged = false;
            bool reloadFailed = false;
            if (Config != null)
            {
                try
                {
                    _savingSelfConfig = true;
                    if (Config.ReloadIfChanged())
                    {
                        var reloaded = PredictorConfig.Load(Config, isInitial: false);
                        externalChanged = !reloaded.PlanInputsEqual(_settings);
                        _settings = reloaded;
                    }
                }
                catch
                {
                    reloadFailed = true;
                }
                finally
                {
                    _savingSelfConfig = false;
                }
            }

            var before = _settings.Clone();
            mutate(_settings);
            _settings.ClampInPlace();

            bool styleChanged = !_settings.StyleSettingsEqual(before);
            bool shouldPersist = styleChanged || _hasPendingStyleSave;

            if (externalChanged || !_settings.PlanInputsEqual(before))
            {
                _planDirty = true;
            }
            if (styleChanged || externalChanged)
            {
                _popup.Invalidate();
            }

            if (!shouldPersist || Config == null) return;

            if (reloadFailed)
            {
                _hasPendingStyleSave = true;
                _popup.SetStyleSaveFailed(true);
                Log.Warn("读取外部 config.json 失败，已中止写入以免覆盖磁盘配置（本局样式已生效）。");
                return;
            }

            bool saved = false;
            try
            {
                _savingSelfConfig = true;
                saved = PredictorConfig.SaveChangedStyleKeys(Config, before, _settings, writeAllStyleKeys: isFullReset || _hasPendingStyleSave);
            }
            catch
            {
                saved = false;
            }
            finally
            {
                _savingSelfConfig = false;
            }

            _hasPendingStyleSave = !saved;
            _popup.SetStyleSaveFailed(!saved);
            if (!saved)
            {
                Log.Warn("样式配置写入 config.json 失败，本局内仍已即时生效；下次点击将自动重试保存。");
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

                // Record owning battle scene handle so additive UI/effect scene unloads cannot wipe active battle state.
                if (_activeBattleSceneHandle < 0)
                {
                    try { _activeBattleSceneHandle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle; } catch { }
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

                double nowSec = MonotonicSeconds();
                bool forceBoardScan = _board == null || nowSec >= _nextBoardScanSec || _tracker.HasPendingPlaceEvents;

                if (forceBoardScan)
                {
                    _nextBoardScanSec = nowSec + 0.50;
                    var nextBoard = _tracker.CaptureBoard(landManager, out bool boardComplete);
                    if (boardComplete && (_board == null || !_board.SameTopology(nextBoard)))
                    {
                        bool layoutChanged = _board != null;
                        _board = nextBoard;
                        if (layoutChanged)
                        {
                            _tracker.NotifyBoardTopologyChanged();
                            _virtualSandbox.NotifyBoardLayoutChanged();
                            _picker.CancelPendingPointer();
                            _monsters.Clear();
                            _players.Clear();
                            _cachedPlan = null;
                            _lastPlanInputSig = string.Empty;
                            _awaitingPostLayoutUnitSync = true;
                            _popup.Invalidate();
                        }
                        _needsUnitSync = true;
                        _planDirty = true;
                    }
                }

                if (_board == null) return;

                // Detect tile-to-tile movement of the tracked monster immediately (0-frame lag)
                if (_selectedMonsterId != 0 && !_needsUnitSync && !_awaitingPostLayoutUnitSync)
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
                if (nowSec >= _nextUnitSyncSec || _needsUnitSync || _awaitingPostLayoutUnitSync)
                {
                    _nextUnitSyncSec = nowSec + 0.12;
                    _needsUnitSync = false;
                    _tracker.SyncAndSnapshot(_board, battle, camera, _settings, out var nextMonsters, out var nextPlayers, out bool matchChanged, out bool snapshotValid, out bool playersAuthoritative);
                    if (matchChanged)
                    {
                        InvalidateMatchCache();
                        return;
                    }
                    // Never wipe virtual overrides/history or active player cache on a transient unavailable/incomplete sample.
                    if (snapshotValid)
                    {
                        _monsters = nextMonsters;
                        if (playersAuthoritative)
                        {
                            _players = nextPlayers;
                            long prevSelPlayer = _virtualSandbox.SelectedPlayerId;
                            bool prevArmed = _virtualSandbox.BoardPickArmed;
                            _virtualSandbox.Reconcile(_players, _board, _tracker.LocalPlayerId);
                            if (_virtualSandbox.SelectedPlayerId != prevSelPlayer || _virtualSandbox.BoardPickArmed != prevArmed)
                            {
                                _picker.CancelRightClickOnly();
                            }
                        }
                        if (!_awaitingPostLayoutUnitSync || playersAuthoritative || _monsters.Count > 0)
                        {
                            _awaitingPostLayoutUnitSync = false;
                        }
                    }
                }

                // Suspend board input and planning while waiting for the first unit sample on a newly switched layout.
                if (_awaitingPostLayoutUnitSync)
                {
                    _renderer.HideAll();
                    return;
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
                    _virtualSandbox.SelectedPlayerId,
                    _virtualSandbox.LayoutGeneration,
                    ref _selectedMonsterId,
                    out int pickedVirtualLandId,
                    out long pickedVirtualPlayerId,
                    out bool clearVirtualRequested);

                if (pickedVirtualLandId >= 0 && pickedVirtualPlayerId != 0)
                {
                    // Re-verify board topology right at RMB commit so a layout switch inside the 500ms scan window
                    // cannot place a player onto an obsolete board graph.
                    var commitBoard = _tracker.CaptureBoard(landManager, out bool commitComplete);
                    if (commitComplete && _board.SameTopology(commitBoard))
                    {
                        OnVirtualMoveRequested(pickedVirtualPlayerId, pickedVirtualLandId);
                    }
                    else if (commitComplete)
                    {
                        _board = commitBoard;
                        _tracker.NotifyBoardTopologyChanged();
                        _virtualSandbox.NotifyBoardLayoutChanged();
                        _picker.CancelPendingPointer();
                        _monsters.Clear();
                        _players.Clear();
                        _cachedPlan = null;
                        _lastPlanInputSig = string.Empty;
                        _awaitingPostLayoutUnitSync = true;
                        _needsUnitSync = true;
                        _popup.Invalidate();
                        return;
                    }
                }
                if (clearVirtualRequested && _virtualSandbox.HasAnyState)
                {
                    OnVirtualResetAllRequested();
                }

                if (selectionChanged)
                {
                    _selectedMissingSinceSec = -1.0;
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

                _virtualSandbox.BuildGhostsInto(_players, _board, _tracker.LocalPlayerId, _ghostBuf);

                if (_selectedMonsterId == 0)
                {
                    _cachedPlan = null;
                    if (_ghostBuf.Count > 0)
                        _renderer.Render(camera, _board, null, null, _settings, _ghostBuf);
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
                        if (_ghostBuf.Count > 0)
                            _renderer.Render(camera, _board, null, null, _settings, _ghostBuf);
                        return;
                    }

                    // Monster is still alive in the battle roster (only its CharacterInst/model is
                    // momentarily unrendered/rebuilding): keep lock & memory intact, hide path overlay only.
                    if (_tracker.IsMonsterInAliveRoster(_selectedMonsterId))
                    {
                        _selectedMissingSinceSec = -1.0;
                        if (_ghostBuf.Count > 0)
                            _renderer.Render(camera, _board, null, null, _settings, _ghostBuf);
                        else
                            _renderer.HideAll();
                        return;
                    }

                    // Roster temporarily empty/unavailable: brief grace window before clearing
                    if (_selectedMissingSinceSec < 0.0)
                    {
                        _selectedMissingSinceSec = nowSec;
                    }
                    else if ((nowSec - _selectedMissingSinceSec) >= 0.35)
                    {
                        ClearSelectionAndOverlay();
                        if (_ghostBuf.Count > 0)
                            _renderer.Render(camera, _board, null, null, _settings, _ghostBuf);
                        return;
                    }

                    if (_ghostBuf.Count > 0)
                        _renderer.Render(camera, _board, null, null, _settings, _ghostBuf);
                    else
                        _renderer.HideAll();
                    return;
                }
                _selectedMissingSinceSec = -1.0;

                var effectivePlayers = _virtualSandbox.HasOverrides
                    ? _virtualSandbox.ApplyOverrides(_players, _board)
                    : _players;
                string planInputSig = BuildPlanInputSignature(selected.State, effectivePlayers, _settings, _virtualSandbox.LayoutGeneration);
                if (_planDirty || _cachedPlan == null || _cachedPlan.MonsterId != _selectedMonsterId || !string.Equals(planInputSig, _lastPlanInputSig, StringComparison.Ordinal))
                {
                    _planDirty = false;
                    _lastPlanInputSig = planInputSig;
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

                _renderer.Render(camera, _board, selected, _cachedPlan, _settings, _ghostBuf);
            }
            catch (Exception ex)
            {
                _cachedPlan = null;
                try { _renderer.HideAll(); } catch { }
                double warnNow = MonotonicSeconds();
                if (warnNow >= _nextWarnSec)
                {
                    _nextWarnSec = warnNow + 15.0;
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

        private static string BuildPlanInputSignature(MonsterState m, IReadOnlyList<TrackedPlayer> players, PredictorSettings s, int layoutGen)
        {
            var sb = new System.Text.StringBuilder(160);
            sb.Append(layoutGen).Append('|')
              .Append(s.PreviewSteps).Append(':')
              .Append(s.SimulateCombatOnFork ? '1' : '0')
              .Append((int)s.PostCombatMode).Append('|')
              .Append(m.MonsterId).Append(':')
              .Append(m.CurrentLandId).Append(':')
              .Append(m.FromLandId).Append(':')
              .Append(m.TurnStartLandId).Append(':')
              .Append(m.IsCurrentlyMovingMidTurn ? '1' : '0')
              .Append(m.JustFoughtOnCurrentFork ? '1' : '0')
              .Append(m.HasLaunchedFightThisTurn ? '1' : '0')
              .Append(m.JustSpawnedOnFork ? '1' : '0')
              .Append(m.IsTaunted ? '1' : '0').Append(':').Append(m.TauntTargetPlayerId).Append(':')
              .Append(m.RequiresCrimeTarget ? '1' : '0').Append(':')
              .Append(m.BossTargetPlayerId).Append(':')
              .Append(m.BossTargetClearedThisTurn ? '1' : '0').Append(':')
              .Append((int)m.SkillMoveKind).Append(':').Append(m.SkillMovePushDirectionLandId).Append(':').Append(m.SkillMovePushFromLandId).Append(':');
            if (m.FrontNodeIds != null)
            {
                for (int i = 0; i < m.FrontNodeIds.Length; i++) sb.Append(m.FrontNodeIds[i]).Append(',');
            }
            sb.Append('|');
            if (players != null)
            {
                for (int i = 0; i < players.Count; i++)
                {
                    var p = players[i];
                    if (p == null) continue;
                    sb.Append(p.PlayerId).Append(':')
                      .Append(p.Slot).Append(':')
                      .Append(p.LandId).Append(':')
                      .Append(p.IsAlive ? '1' : '0')
                      .Append(p.IsTargetable ? '1' : '0')
                      .Append(p.CanBeAttacked ? '1' : '0')
                      .Append(p.HasProblemStudentMark ? '1' : '0')
                      .Append(p.HasMandarinDuckMark ? '1' : '0').Append(':')
                      .Append(p.Gold).Append(':').Append(p.CrimeNum).Append(';');
                }
            }
            return sb.ToString();
        }

        private void ClearSelectionAndOverlay()
        {
            _selectedMonsterId = 0;
            _selectedMissingSinceSec = -1.0;
            _cachedPlan = null;
            _lastPlanInputSig = string.Empty;
            try { _renderer.HideAll(); } catch { }
            try { _renderer.ClearStableHeaderCache(); } catch { }
        }

        private void InvalidateMatchCache()
        {
            ClearSelectionAndOverlay();
            _virtualSandbox.ResetAll(disarmBoardPick: true);
            _picker.CancelPendingPointer();
            _board = null;
            _monsters.Clear();
            _players.Clear();
            _nextBoardScanSec = 0.0;
            _nextUnitSyncSec = 0.0;
            _needsUnitSync = true;
            _awaitingPostLayoutUnitSync = false;
            _planDirty = true;
            _popup.Invalidate();
        }

        private void ResetBattleState()
        {
            ClearSelectionAndOverlay();
            _virtualSandbox.ResetAll(disarmBoardPick: true);
            _picker.CancelPendingPointer();
            try { _popup.Hide(); } catch { }
            _board = null;
            _monsters.Clear();
            _players.Clear();
            _nextBoardScanSec = 0.0;
            _nextUnitSyncSec = 0.0;
            _needsUnitSync = false;
            _awaitingPostLayoutUnitSync = false;
            _planDirty = false;
            _activeBattleSceneHandle = -1;
            _tracker.SetEventCollectionEnabled(false);
            _tracker.Reset();
        }
    }
}
