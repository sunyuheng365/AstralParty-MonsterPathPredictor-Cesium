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
        private enum PveSessionState
        {
            Active = 0,
            TransientUnavailable = 1,
            ConfirmedExited = 2
        }

        private readonly MonsterStateTracker _tracker = new MonsterStateTracker();
        private readonly MonsterPicker _picker = new MonsterPicker();
        private readonly MonsterPickerPopup _popup = new MonsterPickerPopup();
        private readonly DashedOverlayRenderer _renderer = new DashedOverlayRenderer();
        private readonly VirtualPlayerSandbox _virtualSandbox = new VirtualPlayerSandbox();
        private readonly StylePendingPatch _pendingStylePatch = new StylePendingPatch();
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
        private bool _lastSnapshotInvalid;
        private bool _planDirty;
        private bool _savingSelfConfig;
        private int _activeBattleSceneHandle = -1;
        private int _planInputRevision;
        private int _cachedPlanRevision = -1;
        private IReadOnlyList<TrackedPlayer> _cachedEffectivePlayers;
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
            var reloaded = PredictorConfig.Load(Config, isInitial: false, out bool saveOk);
            // Replay any unsaved local style edits on top of the freshly reloaded external config
            // so pending edits are never lost and the save-failure warning remains accurate.
            _pendingStylePatch.ApplyTo(reloaded);
            _settings = reloaded;
            _popup.SetStyleSaveFailed(!saveOk || _pendingStylePatch.HasPending);
            _needsUnitSync = true;
            if (!_settings.PlanInputsEqual(prev))
            {
                MarkPlanInputsChanged();
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

        private void MarkPlanInputsChanged()
        {
            _planDirty = true;
            _planInputRevision++;
            _cachedEffectivePlayers = null;
        }

        private void OnPopupMonsterSelected(long monsterId, int expectedMatchGen)
        {
            if (_awaitingPostLayoutUnitSync || _lastSnapshotInvalid) return;
            if (expectedMatchGen != _tracker.MatchGeneration) return;
            if (_selectedMonsterId == monsterId)
            {
                _selectedMonsterId = 0;
                _selectedMissingSinceSec = -1.0;
                MarkPlanInputsChanged();
                return;
            }

            // Revalidate live monster existence and current eligibility (never trusting a stale 120ms cache
            // on a press-deferred popup row!).
            if (!MonsterStateTracker.TryIsMonsterLiveAndEligible(monsterId, _settings.AllowNonAggressiveMonsters))
            {
                _needsUnitSync = true;
                _popup.Invalidate();
                return;
            }
            var picked = FindMonster(monsterId);
            if (picked == null || picked.State == null)
            {
                _needsUnitSync = true;
                _popup.Invalidate();
                return;
            }

            _selectedMonsterId = monsterId;
            _selectedMissingSinceSec = -1.0;
            MarkPlanInputsChanged();
            Log.Info("弹窗已锁定怪物：" + picked.State.DisplayName + "（地块 #" + picked.State.CurrentLandId + "）");
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
            if (_awaitingPostLayoutUnitSync || _lastSnapshotInvalid) return;
            _picker.CancelRightClickOnly();
            _virtualSandbox.SelectPlayer(playerId);
            _popup.Invalidate();
        }

        private void OnVirtualStepNeighborRequested(long playerId, int expectedSourceLandId, int targetLandId, int expectedLayoutGen)
        {
            if (_awaitingPostLayoutUnitSync || _lastSnapshotInvalid || _board == null) return;
            if (expectedLayoutGen != _virtualSandbox.LayoutGeneration) return;

            // Validate against the LIVE board topology AND LIVE hero liveness/position at commit time
            // (never falling back to stale cached player position or liveness when live lookup fails!).
            if (!TryGetRenderContext(out var landManager, out _)) return;
            var liveBoard = _tracker.CaptureBoard(landManager, out bool liveBoardComplete);
            if (!liveBoardComplete) return;
            if (!_board.SameTopology(liveBoard))
            {
                HandleBoardTopologyReplaced(liveBoard);
                return;
            }

            if (!_tracker.TryReadLiveHeroState(playerId, liveBoard, out bool liveIsAlive, out int liveLand) || !liveIsAlive)
            {
                _needsUnitSync = true;
                _popup.Invalidate();
                return;
            }

            bool hasOverride = _virtualSandbox.TryGetVirtualLand(playerId, out int virtLand) && liveBoard.Contains(virtLand);
            if (!hasOverride && !liveBoard.Contains(liveLand))
            {
                // Following live position but live position cannot be verified on board -> defer/resync!
                _needsUnitSync = true;
                _popup.Invalidate();
                return;
            }

            int currentEffective = hasOverride ? virtLand : liveLand;
            if (currentEffective != expectedSourceLandId)
            {
                _needsUnitSync = true;
                _popup.Invalidate();
                return;
            }
            if (!liveBoard.IsValidNeighbor(expectedSourceLandId, targetLandId)) return;

            if (_virtualSandbox.SetVirtualLand(playerId, targetLandId, liveLand, liveBoard))
            {
                MarkPlanInputsChanged();
                _popup.Invalidate();
            }
        }

        private void OnVirtualMoveRequested(long playerId, int targetLandId, BoardGraph verifiedBoard = null)
        {
            var board = verifiedBoard ?? _board;
            if (_awaitingPostLayoutUnitSync || _lastSnapshotInvalid || board == null) return;
            if (!_tracker.TryReadLiveHeroState(playerId, board, out bool liveIsAlive, out int liveLand) || !liveIsAlive)
            {
                _needsUnitSync = true;
                _popup.Invalidate();
                return;
            }
            if (_virtualSandbox.SetVirtualLand(playerId, targetLandId, liveLand, board))
            {
                MarkPlanInputsChanged();
                _popup.Invalidate();
            }
        }

        private void OnVirtualUndoRequested(long playerId, int expectedLayoutGen)
        {
            if (_awaitingPostLayoutUnitSync || _lastSnapshotInvalid || _board == null) return;
            if (expectedLayoutGen != _virtualSandbox.LayoutGeneration) return;
            if (!TryGetRenderContext(out var landManager, out _)) return;
            var liveBoard = _tracker.CaptureBoard(landManager, out bool liveBoardComplete);
            if (!liveBoardComplete) return;
            if (!_board.SameTopology(liveBoard))
            {
                HandleBoardTopologyReplaced(liveBoard);
                return;
            }

            if (!_tracker.TryReadLiveHeroState(playerId, liveBoard, out bool liveIsAlive, out int liveLand) || !liveIsAlive)
            {
                _needsUnitSync = true;
                _popup.Invalidate();
                return;
            }
            if (_virtualSandbox.Undo(playerId, liveLand, liveBoard))
            {
                MarkPlanInputsChanged();
                _popup.Invalidate();
            }
        }

        private void OnVirtualResetPlayerRequested(long playerId)
        {
            if (_virtualSandbox.ResetPlayer(playerId))
            {
                MarkPlanInputsChanged();
                _popup.Invalidate();
            }
        }

        private void OnVirtualResetAllRequested()
        {
            if (_virtualSandbox.ResetAll(disarmBoardPick: false))
            {
                MarkPlanInputsChanged();
                _popup.Invalidate();
            }
        }

        private void OnVirtualBoardPickToggled(long expectedOwnerId, bool armed, int expectedLayoutGen)
        {
            if (_awaitingPostLayoutUnitSync || _lastSnapshotInvalid) return;
            if (expectedLayoutGen != _virtualSandbox.LayoutGeneration) return;
            if (expectedOwnerId == 0 || expectedOwnerId != _virtualSandbox.SelectedPlayerId) return;
            if (!_tracker.TryReadLiveHeroState(expectedOwnerId, _board, out bool liveIsAlive, out _) || !liveIsAlive)
            {
                _needsUnitSync = true;
                _popup.Invalidate();
                return;
            }

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

            // Reload any pending external disk edits first and replay our unsaved per-key patch on top.
            // If reload fails, abort persistence so we never overwrite an unreadable external disk file.
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
                        _pendingStylePatch.ApplyTo(reloaded);
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
            if (isFullReset)
            {
                _pendingStylePatch.RecordAllStyleKeys(_settings);
            }
            else if (styleChanged)
            {
                _pendingStylePatch.RecordDiff(before, _settings);
            }

            bool shouldPersist = isFullReset || styleChanged || _pendingStylePatch.HasPending;

            if (externalChanged || !_settings.PlanInputsEqual(before))
            {
                MarkPlanInputsChanged();
            }
            if (styleChanged || externalChanged || isFullReset)
            {
                _popup.Invalidate();
            }

            if (!shouldPersist || Config == null) return;

            if (reloadFailed)
            {
                _popup.SetStyleSaveFailed(true);
                Log.Warn("读取外部 config.json 失败，已中止写入以免覆盖磁盘配置（本局样式已生效）。");
                return;
            }

            bool saved = false;
            try
            {
                _savingSelfConfig = true;
                saved = PredictorConfig.SaveStylePatch(Config, _pendingStylePatch);
            }
            catch
            {
                saved = false;
            }
            finally
            {
                _savingSelfConfig = false;
            }

            if (saved)
            {
                _pendingStylePatch.Clear();
            }
            _popup.SetStyleSaveFailed(!saved);
            if (!saved)
            {
                Log.Warn("样式配置写入 config.json 失败，本局内仍已即时生效；下次点击将自动重试保存。");
            }
        }

        private void HandleBoardTopologyReplaced(BoardGraph nextBoard)
        {
            _board = nextBoard;
            _tracker.NotifyBoardTopologyChanged(nextBoard);
            _virtualSandbox.NotifyBoardLayoutChanged();
            _picker.CancelPendingPointer();
            _monsters.Clear();
            _players.Clear();
            _cachedPlan = null;
            _lastPlanInputSig = string.Empty;
            _awaitingPostLayoutUnitSync = true;
            _needsUnitSync = true;
            MarkPlanInputsChanged();
            _popup.CancelPendingPress();
            _popup.SetTemporarilyHidden(true);
            _popup.Invalidate();
        }

        private void MergePartialPlayerUpdates(List<TrackedPlayer> partialPlayers)
        {
            if (partialPlayers == null || partialPlayers.Count == 0 || _players.Count == 0) return;
            bool changed = false;
            for (int i = 0; i < partialPlayers.Count; i++)
            {
                var upd = partialPlayers[i];
                if (upd == null || upd.PlayerId <= 0) continue;
                for (int j = 0; j < _players.Count; j++)
                {
                    if (_players[j] != null && _players[j].PlayerId == upd.PlayerId)
                    {
                        _players[j] = upd;
                        changed = true;
                        break;
                    }
                }
            }
            if (changed)
            {
                long prevSelPlayer = _virtualSandbox.SelectedPlayerId;
                bool prevArmed = _virtualSandbox.BoardPickArmed;
                _virtualSandbox.Reconcile(_players, _board, _tracker.LocalPlayerId);
                if (_virtualSandbox.SelectedPlayerId != prevSelPlayer || _virtualSandbox.BoardPickArmed != prevArmed)
                {
                    _picker.CancelRightClickOnly();
                }
                MarkPlanInputsChanged();
            }
        }

        private void DrawAndTrack()
        {
            try
            {
                if (!_settings.Enabled)
                {
                    _tracker.SetEventCollectionEnabled(false);
                    if (_selectedMonsterId != 0 || _board != null || _popup.IsOpen) ResetBattleState();
                    return;
                }

                var sessionState = GetPveSessionState(out var battle);
                if (sessionState == PveSessionState.ConfirmedExited)
                {
                    _tracker.SetEventCollectionEnabled(false);
                    if (_selectedMonsterId != 0 || _board != null || _popup.IsOpen) ResetBattleState();
                    return;
                }
                if (sessionState == PveSessionState.TransientUnavailable)
                {
                    // Even during a transient room-metadata gap, keep observing available battle turns so
                    // A -> B -> A transitions are never missed while rendering & input stay suspended.
                    if (battle != null)
                    {
                        _tracker.SetEventCollectionEnabled(true);
                        if (_tracker.ObserveTurnBoundary(battle))
                        {
                            InvalidateMatchCache();
                        }
                    }
                    _picker.CancelPendingPointer();
                    _renderer.HideAll();
                    _popup.SetTemporarilyHidden(true);
                    _needsUnitSync = true;
                    return;
                }

                // Enable game event ingestion only while actively inside an enabled PVE battle
                // (stays active across FightWindow so combat events resolve accurately upon return).
                _tracker.SetEventCollectionEnabled(true);

                // Keep authoritative turn-boundary tracking advancing even while FightWindow / settings popups
                // or transient camera hiccups temporarily suspend visual overlay rendering (prevents A->B->A miss).
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
                    MarkPlanInputsChanged();
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

                // Derive owning battle scene handle from a battle-owned object (BattleSceneController),
                // NEVER from SceneManager.GetActiveScene() which may belong to an additive UI overlay scene.
                if (_activeBattleSceneHandle < 0)
                {
                    try
                    {
                        var sc = BattleSceneController.inst;
                        if (sc != null && sc.gameObject != null)
                            _activeBattleSceneHandle = sc.gameObject.scene.handle;
                    }
                    catch { }
                }

                double nowSec = MonotonicSeconds();
                long placeSeqSnapshot = _tracker.LatestPlaceSequence;
                bool forceBoardScan = _board == null || nowSec >= _nextBoardScanSec || _tracker.HasUnvalidatedPlace;

                if (forceBoardScan)
                {
                    var nextBoard = _tracker.CaptureBoard(landManager, out bool boardComplete);
                    if (boardComplete)
                    {
                        _nextBoardScanSec = nowSec + 0.50;
                        if (_board == null)
                        {
                            _board = nextBoard;
                            _awaitingPostLayoutUnitSync = true;
                            _needsUnitSync = true;
                            MarkPlanInputsChanged();
                        }
                        else if (!_board.SameTopology(nextBoard))
                        {
                            HandleBoardTopologyReplaced(nextBoard);
                        }
                        // Acknowledge ONLY up to the Place sequence captured BEFORE CaptureBoard succeeded!
                        // Any Place arriving after placeSeqSnapshot stays unvalidated until the next complete scan.
                        _tracker.AcknowledgeBoardValidatedUpTo(placeSeqSnapshot);
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
                    _tracker.SyncAndSnapshot(
                        _board, battle, camera, _settings,
                        out var nextMonsters, out var nextPlayers,
                        out bool matchChanged, out bool snapshotValid,
                        out bool playersAuthoritative, out bool playerPositionsReady);
                    if (matchChanged)
                    {
                        InvalidateMatchCache();
                        return;
                    }
                    _lastSnapshotInvalid = !snapshotValid;
                    if (snapshotValid)
                    {
                        _monsters = nextMonsters;
                        MarkPlanInputsChanged();
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
                            if (playerPositionsReady)
                            {
                                _awaitingPostLayoutUnitSync = false;
                            }
                        }
                        else if (!_awaitingPostLayoutUnitSync)
                        {
                            // Merge confirmed per-ID updates (e.g. hero death) without wiping temporarily absent heroes.
                            MergePartialPlayerUpdates(nextPlayers);
                        }
                    }
                }

                // Suspend board input, popup interaction, and planning while waiting for authoritative player positions
                // on a newly acquired/replaced board, OR while the entire unit snapshot is unavailable.
                if (_awaitingPostLayoutUnitSync || _lastSnapshotInvalid)
                {
                    _popup.SetTemporarilyHidden(true);
                    _renderer.HideAll();
                    return;
                }

                _popup.SetTemporarilyHidden(false);

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
                        OnVirtualMoveRequested(pickedVirtualPlayerId, pickedVirtualLandId, commitBoard);
                    }
                    else if (commitComplete)
                    {
                        HandleBoardTopologyReplaced(commitBoard);
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
                    MarkPlanInputsChanged();
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
                    _popup.Refresh(_monsters, _players, _board, _virtualSandbox, _tracker.LocalPlayerId, _selectedMonsterId, _settings, _tracker.MatchGeneration);
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

                // Steady-state zero-allocation fast path: skip player cloning & signature string building
                // whenever no snapshot, sandbox, selection, or plan-option revision changed since last plan.
                if (_planDirty || _cachedPlan == null || _cachedPlan.MonsterId != _selectedMonsterId || _cachedPlanRevision != _planInputRevision)
                {
                    var effectivePlayers = _cachedEffectivePlayers;
                    if (effectivePlayers == null)
                    {
                        effectivePlayers = _virtualSandbox.HasOverrides
                            ? (IReadOnlyList<TrackedPlayer>)_virtualSandbox.ApplyOverrides(_players, _board)
                            : _players;
                        _cachedEffectivePlayers = effectivePlayers;
                    }

                    var planOpts = new PlanOptions
                    {
                        MaxSteps = _settings.PreviewSteps,
                        SimulateCombatOnFork = _settings.SimulateCombatOnFork,
                        PostCombatMode = _settings.PostCombatMode
                    };
                    string planInputSig = MonsterPathEngine.ComputeInputSignature(selected.State, effectivePlayers, planOpts, _virtualSandbox.LayoutGeneration);
                    if (_planDirty || _cachedPlan == null || _cachedPlan.MonsterId != _selectedMonsterId || !string.Equals(planInputSig, _lastPlanInputSig, StringComparison.Ordinal))
                    {
                        _lastPlanInputSig = planInputSig;
                        _cachedPlan = MonsterPathEngine.Plan(_board, selected.State, effectivePlayers, planOpts);
                    }
                    _planDirty = false;
                    _cachedPlanRevision = _planInputRevision;
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
                _cachedEffectivePlayers = null;
                _needsUnitSync = true;
                MarkPlanInputsChanged();
                try { _picker.CancelPendingPointer(); } catch { }
                try { _popup.CancelPendingPress(); } catch { }
                try { _popup.SetTemporarilyHidden(true); } catch { }
                try { _renderer.HideAll(force: true); } catch { }
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

        private PveSessionState GetPveSessionState(out BattleLogic battle)
        {
            battle = null;

            if (IsBattleSettled()) return PveSessionState.ConfirmedExited;

            if (!SimpleSingletonProvider<global::Core.Scene.SceneManager>.hasInstance)
                return _board != null ? PveSessionState.TransientUnavailable : PveSessionState.ConfirmedExited;

            if (SimpleSingletonProvider<global::Core.Scene.SceneManager>.inst.currentType.Value != SceneType.Battle)
                return PveSessionState.ConfirmedExited;

            if (!SimpleSingletonProvider<GameLogicManager>.hasInstance)
                return PveSessionState.TransientUnavailable;

            var gm = SimpleSingletonProvider<GameLogicManager>.inst;
            battle = gm.battle;
            var room = gm.room;
            if (room == null || room.curRoomInfo == null)
                return PveSessionState.TransientUnavailable;

            if (!room.curRoomInfo.IsPVE())
                return PveSessionState.ConfirmedExited;

            if (battle == null)
                return PveSessionState.TransientUnavailable;

            return PveSessionState.Active;
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
            _selectedMissingSinceSec = -1.0;
            _cachedPlan = null;
            _lastPlanInputSig = string.Empty;
            try { _renderer.HideAll(); } catch { }
            try { _renderer.ClearStableHeaderCache(); } catch { }
        }

        private void InvalidateMatchCache()
        {
            ClearSelectionAndOverlay();
            _virtualSandbox.NotifyBoardLayoutChanged();
            _virtualSandbox.ResetAll(disarmBoardPick: true);
            _picker.CancelPendingPointer();
            _popup.CancelPendingPress();
            _board = null;
            _monsters.Clear();
            _players.Clear();
            _cachedEffectivePlayers = null;
            _lastSnapshotInvalid = false;
            _nextBoardScanSec = 0.0;
            _nextUnitSyncSec = 0.0;
            _needsUnitSync = true;
            _awaitingPostLayoutUnitSync = true;
            MarkPlanInputsChanged();
            _popup.Invalidate();
        }

        private void ResetBattleState()
        {
            ClearSelectionAndOverlay();
            _virtualSandbox.NotifyBoardLayoutChanged();
            _virtualSandbox.ResetAll(disarmBoardPick: true);
            _picker.CancelPendingPointer();
            _popup.CancelPendingPress();
            try { _popup.Hide(); } catch { }
            _board = null;
            _monsters.Clear();
            _players.Clear();
            _cachedEffectivePlayers = null;
            _lastSnapshotInvalid = false;
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
