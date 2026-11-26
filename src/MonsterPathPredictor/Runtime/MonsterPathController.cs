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
        private readonly PlanSignatureCache _planCache = new PlanSignatureCache();
        private readonly List<VirtualPlayerGhost> _ghostBuf = new List<VirtualPlayerGhost>(4);

        private PredictorSettings _settings = new PredictorSettings();
        private BoardGraph _board;
        private List<LiveMonsterInfo> _monsters = new List<LiveMonsterInfo>();
        private List<TrackedPlayer> _players = new List<TrackedPlayer>();

        private long _selectedMonsterId;

        private double _nextBoardScanSec;
        private double _nextUnitSyncSec;
        private double _nextWarnSec;
        private double _selectedMissingSinceSec = -1.0;
        private bool _sessionStarted;
        private bool _boardScanIncomplete;
        private bool _needsUnitSync;
        private bool _awaitingPostLayoutUnitSync;
        private bool _lastSnapshotInvalid;
        private bool _savingSelfConfig;
        private int _activeBattleSceneHandle = -1;
        private int _popupDataRevision = 1;
        private IReadOnlyList<TrackedPlayer> _cachedEffectivePlayers;

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
            var reloaded = PredictorConfig.Load(Config, isInitial: false, out bool saveOk, fallbackOnError: prev);
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
            _popupDataRevision++;
            _popup.Invalidate();
            if (!_settings.Enabled)
            {
                if (_sessionStarted || _selectedMonsterId != 0 || _board != null || _popup.IsOpen)
                {
                    ResetBattleState();
                }
                else
                {
                    _tracker.SetEventCollectionEnabled(false);
                }
            }
            Log.Info("配置已热重载：enabled=" + _settings.Enabled +
                     ", previewSteps=" + _settings.PreviewSteps +
                     ", lineWidth=" + _settings.LineWidth +
                     ", dashSpeed=" + _settings.DashSpeed +
                     ", pickerHotkey=" + _settings.PickerHotkey +
                     ", postCombatMode=" + PredictorSettings.FormatPostCombatMode(_settings.PostCombatMode));
        }

        private void SyncActiveBattleSceneHandle()
        {
            try
            {
                var sc = BattleSceneController.inst;
                if (sc != null && sc.gameObject != null)
                    _activeBattleSceneHandle = sc.gameObject.scene.handle;
            }
            catch { }
        }

        public override void OnSceneUnloaded(UnityEngine.SceneManagement.Scene scene)
        {
            // Compare against the CURRENT live BattleSceneController owner BEFORE tearing down: if scene B
            // already replaced scene A while an overlay was open, unloading old scene A must NOT reset B!
            int liveOwnerHandle = -1;
            try
            {
                var sc = BattleSceneController.inst;
                if (sc != null && sc.gameObject != null)
                    liveOwnerHandle = sc.gameObject.scene.handle;
            }
            catch { }
            if (liveOwnerHandle >= 0 && liveOwnerHandle != scene.handle)
            {
                _activeBattleSceneHandle = liveOwnerHandle;
                return;
            }

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

        private void MarkPlanInputsChanged(bool forceReplan = false)
        {
            if (forceReplan) _planCache.InvalidateForceReplan();
            else _planCache.MarkInputsMaybeChanged();
            _cachedEffectivePlayers = null;
            _popupDataRevision++;
        }

        private bool RefreshMatchAndVerifyGeneration(int expectedMatchGen)
        {
            var ms = _tracker.EnsureSameMatch();
            if (ms == MatchSyncResult.Switched || ms == MatchSyncResult.InitialAcquired)
            {
                InvalidateMatchCache();
                return false;
            }
            if (ms == MatchSyncResult.Unavailable)
            {
                // Defer scoped UI mutations whenever match identity cannot currently be verified.
                return false;
            }
            return expectedMatchGen == _tracker.MatchGeneration;
        }

        private void OnPopupMonsterSelected(long monsterId, int expectedMatchGen)
        {
            if (_awaitingPostLayoutUnitSync || _lastSnapshotInvalid || _boardScanIncomplete) return;
            if (!RefreshMatchAndVerifyGeneration(expectedMatchGen)) return;
            if (_selectedMonsterId == monsterId)
            {
                _selectedMonsterId = 0;
                _selectedMissingSinceSec = -1.0;
                MarkPlanInputsChanged();
                if (_settings.ClosePickerOnSelect) _popup.Hide();
                return;
            }

            // Revalidate live monster existence and current eligibility (never trusting a stale 120ms cache
            // on a press-deferred popup row!).
            if (!MonsterStateTracker.TryIsMonsterLiveAndEligible(monsterId, _settings.AllowNonAggressiveMonsters))
            {
                _needsUnitSync = true;
                _popupDataRevision++;
                _popup.Invalidate();
                return;
            }
            var picked = FindMonster(monsterId);
            if (picked == null || picked.State == null)
            {
                _needsUnitSync = true;
                _popupDataRevision++;
                _popup.Invalidate();
                return;
            }

            _selectedMonsterId = monsterId;
            _selectedMissingSinceSec = -1.0;
            MarkPlanInputsChanged();
            if (_settings.ClosePickerOnSelect) _popup.Hide();
            Log.Info("弹窗已锁定怪物：" + picked.State.DisplayName + "（地块 #" + picked.State.CurrentLandId + "）");
        }

        private void OnPopupClearRequested(long expectedSelectedMonsterId, bool expectedHadVirtualState, int expectedMatchGen, int expectedLayoutGen)
        {
            if (_awaitingPostLayoutUnitSync || _lastSnapshotInvalid || _boardScanIncomplete) return;
            if (!RefreshMatchAndVerifyGeneration(expectedMatchGen)) return;
            if (expectedLayoutGen != _virtualSandbox.LayoutGeneration) return;

            // Execute ONLY the exact intent captured at press start so a concurrent selection change
            // between press and release can never clear a different monster or silently switch to ResetAll!
            if (expectedSelectedMonsterId != 0)
            {
                if (_selectedMonsterId == expectedSelectedMonsterId)
                {
                    ClearSelectionAndOverlay();
                }
            }
            else if (expectedHadVirtualState && _selectedMonsterId == 0 && _virtualSandbox.HasAnyState)
            {
                OnVirtualResetAllRequested(expectedMatchGen, expectedLayoutGen);
            }
        }

        private void OnPopupStepsDelta(int delta)
        {
            ApplyStyleMutation(s => s.PreviewSteps = PredictorSettings.ClampInt(s.PreviewSteps + delta, 1, 30));
        }

        private void OnVirtualPlayerSelected(long playerId, int expectedMatchGen, int expectedLayoutGen)
        {
            if (_awaitingPostLayoutUnitSync || _lastSnapshotInvalid || _boardScanIncomplete) return;
            if (!RefreshMatchAndVerifyGeneration(expectedMatchGen)) return;
            if (expectedLayoutGen != _virtualSandbox.LayoutGeneration) return;
            if (!_tracker.TryReadLiveHeroState(playerId, _board, out bool liveIsAlive, out _) || !liveIsAlive)
            {
                _needsUnitSync = true;
                _popupDataRevision++;
                _popup.Invalidate();
                return;
            }
            _picker.CancelRightClickOnly();
            _virtualSandbox.SelectPlayer(playerId);
            _popupDataRevision++;
            _popup.Invalidate();
        }

        private void OnVirtualStepNeighborRequested(long playerId, int expectedSourceLandId, int targetLandId, int expectedMatchGen, int expectedLayoutGen)
        {
            if (_awaitingPostLayoutUnitSync || _lastSnapshotInvalid || _boardScanIncomplete || _board == null) return;
            if (!RefreshMatchAndVerifyGeneration(expectedMatchGen)) return;
            if (expectedLayoutGen != _virtualSandbox.LayoutGeneration) return;
            if (playerId == 0 || playerId != _virtualSandbox.SelectedPlayerId) return;

            // Validate against the LIVE board topology AND LIVE hero liveness/position at commit time
            // (never falling back to stale cached player position or liveness when live lookup fails!).
            if (!TryGetLandManager(out var landManager))
            {
                SuspendForIncompleteBoard();
                return;
            }
            long scanSeq = _tracker.LatestPlaceSequence;
            var liveBoard = _tracker.CaptureBoard(landManager, out bool liveBoardComplete);
            if (!liveBoardComplete)
            {
                SuspendForIncompleteBoard();
                return;
            }
            if (!_board.SameTopology(liveBoard))
            {
                HandleBoardTopologyReplaced(liveBoard, scanSeq);
                return;
            }

            if (!_tracker.TryReadLiveHeroState(playerId, liveBoard, out bool liveIsAlive, out int liveLand) || !liveIsAlive)
            {
                _needsUnitSync = true;
                _popupDataRevision++;
                _popup.Invalidate();
                return;
            }

            bool hasOverride = _virtualSandbox.TryGetVirtualLand(playerId, out int virtLand) && liveBoard.Contains(virtLand);
            if (!hasOverride && !liveBoard.Contains(liveLand))
            {
                // Following live position but live position cannot be verified on board -> defer/resync!
                _needsUnitSync = true;
                _popupDataRevision++;
                _popup.Invalidate();
                return;
            }

            int currentEffective = hasOverride ? virtLand : liveLand;
            if (currentEffective != expectedSourceLandId)
            {
                _needsUnitSync = true;
                _popupDataRevision++;
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
            if (_awaitingPostLayoutUnitSync || _lastSnapshotInvalid || _boardScanIncomplete || board == null) return;
            if (!_tracker.TryReadLiveHeroState(playerId, board, out bool liveIsAlive, out int liveLand) || !liveIsAlive)
            {
                _needsUnitSync = true;
                _popupDataRevision++;
                _popup.Invalidate();
                return;
            }
            if (_virtualSandbox.SetVirtualLand(playerId, targetLandId, liveLand, board))
            {
                MarkPlanInputsChanged();
                _popup.Invalidate();
            }
        }

        private void OnVirtualUndoRequested(long playerId, int expectedMatchGen, int expectedLayoutGen)
        {
            if (_awaitingPostLayoutUnitSync || _lastSnapshotInvalid || _boardScanIncomplete || _board == null) return;
            if (!RefreshMatchAndVerifyGeneration(expectedMatchGen)) return;
            if (expectedLayoutGen != _virtualSandbox.LayoutGeneration) return;
            if (playerId == 0 || playerId != _virtualSandbox.SelectedPlayerId) return;
            if (!TryGetLandManager(out var landManager))
            {
                SuspendForIncompleteBoard();
                return;
            }
            long scanSeq = _tracker.LatestPlaceSequence;
            var liveBoard = _tracker.CaptureBoard(landManager, out bool liveBoardComplete);
            if (!liveBoardComplete)
            {
                SuspendForIncompleteBoard();
                return;
            }
            if (!_board.SameTopology(liveBoard))
            {
                HandleBoardTopologyReplaced(liveBoard, scanSeq);
                return;
            }

            if (!_tracker.TryReadLiveHeroState(playerId, liveBoard, out bool liveIsAlive, out int liveLand) || !liveIsAlive)
            {
                _needsUnitSync = true;
                _popupDataRevision++;
                _popup.Invalidate();
                return;
            }
            if (_virtualSandbox.Undo(playerId, liveLand, liveBoard))
            {
                MarkPlanInputsChanged();
                _popup.Invalidate();
            }
        }

        private void OnVirtualResetPlayerRequested(long playerId, int expectedMatchGen, int expectedLayoutGen)
        {
            if (_awaitingPostLayoutUnitSync || _lastSnapshotInvalid || _boardScanIncomplete) return;
            if (!RefreshMatchAndVerifyGeneration(expectedMatchGen)) return;
            if (expectedLayoutGen != _virtualSandbox.LayoutGeneration) return;
            if (playerId == 0 || playerId != _virtualSandbox.SelectedPlayerId) return;
            if (_virtualSandbox.ResetPlayer(playerId))
            {
                MarkPlanInputsChanged();
                _popup.Invalidate();
            }
        }

        private void OnVirtualResetAllRequested(int expectedMatchGen, int expectedLayoutGen)
        {
            if (_awaitingPostLayoutUnitSync || _lastSnapshotInvalid || _boardScanIncomplete) return;
            if (!RefreshMatchAndVerifyGeneration(expectedMatchGen)) return;
            if (expectedLayoutGen != _virtualSandbox.LayoutGeneration) return;
            if (_virtualSandbox.ResetAll(disarmBoardPick: false))
            {
                MarkPlanInputsChanged();
                _popup.Invalidate();
            }
        }

        private void OnVirtualBoardPickToggled(long expectedOwnerId, bool armed, int expectedMatchGen, int expectedLayoutGen)
        {
            if (_awaitingPostLayoutUnitSync || _lastSnapshotInvalid || _boardScanIncomplete) return;
            if (!RefreshMatchAndVerifyGeneration(expectedMatchGen)) return;
            if (expectedLayoutGen != _virtualSandbox.LayoutGeneration) return;
            if (expectedOwnerId == 0 || expectedOwnerId != _virtualSandbox.SelectedPlayerId) return;
            if (!_tracker.TryReadLiveHeroState(expectedOwnerId, _board, out bool liveIsAlive, out _) || !liveIsAlive)
            {
                _needsUnitSync = true;
                _popupDataRevision++;
                _popup.Invalidate();
                return;
            }

            _picker.CancelRightClickOnly();
            _virtualSandbox.SetBoardPickArmed(armed, _players);
            _popupDataRevision++;
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

            ConfigPersistenceCoordinator.StylePersistOutcome outcome;
            try
            {
                _savingSelfConfig = true;
                outcome = PredictorConfig.ApplyStyleMutationAndPersist(
                    Config,
                    _settings,
                    _pendingStylePatch,
                    mutate,
                    isFullReset);
            }
            finally
            {
                _savingSelfConfig = false;
            }

            _settings = outcome.Settings;
            if (outcome.PlanInputsChanged)
            {
                MarkPlanInputsChanged();
            }
            if (outcome.StyleChanged || outcome.ExternalChanged || isFullReset)
            {
                _popupDataRevision++;
                _popup.Invalidate();
            }

            if (!outcome.SaveAttempted) return;

            _popup.SetStyleSaveFailed(!outcome.SaveOk);
            if (!outcome.SaveOk)
            {
                if (outcome.ReloadFailed)
                    Log.Warn("读取外部 config.json 失败，已中止写入以免覆盖磁盘配置（本局样式已生效）。");
                else if (Config == null)
                    Log.Warn("配置存储不可用，本局样式已生效但尚未写入 config.json。");
                else
                    Log.Warn("样式配置写入 config.json 失败，本局内仍已即时生效；下次点击将自动重试保存。");
            }
        }

        private void SuspendForIncompleteBoard()
        {
            _boardScanIncomplete = true;
            _needsUnitSync = true;
            _picker.CancelPendingPointer();
            _popup.CancelPendingPress();
            _popup.SetTemporarilyHidden(true);
            _renderer.HideAll();
        }

        private void HandleMatchSyncResult(MatchSyncResult ms)
        {
            if (ms == MatchSyncResult.Switched ||
                (ms == MatchSyncResult.InitialAcquired &&
                 (_board != null || _monsters.Count > 0 || _players.Count > 0 || _selectedMonsterId != 0 || _virtualSandbox.HasAnyState)))
            {
                InvalidateMatchCache();
            }
        }

        private void HandleBoardTopologyReplaced(BoardGraph nextBoard, long scanMaxPlaceSeq)
        {
            _board = nextBoard;
            _boardScanIncomplete = false;
            _tracker.NotifyBoardTopologyChanged(nextBoard, scanMaxPlaceSeq);
            _virtualSandbox.NotifyBoardLayoutChanged();
            _picker.CancelPendingPointer();
            _monsters.Clear();
            _players.Clear();
            _awaitingPostLayoutUnitSync = true;
            _needsUnitSync = true;
            MarkPlanInputsChanged(forceReplan: true);
            _popup.CancelPendingPress();
            _popup.SetTemporarilyHidden(true);
            _popup.Invalidate();
        }

        private void MergePartialPlayerUpdates(List<TrackedPlayer> partialPlayers)
        {
            if (partialPlayers == null || partialPlayers.Count == 0) return;
            bool changed = false;
            for (int i = 0; i < partialPlayers.Count; i++)
            {
                var upd = partialPlayers[i];
                if (upd == null || upd.PlayerId <= 0) continue;
                bool found = false;
                for (int j = 0; j < _players.Count; j++)
                {
                    var existing = _players[j];
                    if (existing != null && existing.PlayerId == upd.PlayerId)
                    {
                        found = true;
                        bool updLandValid = _board != null && upd.LandId >= 0 && _board.Contains(upd.LandId);
                        int mergedLand = updLandValid
                            ? upd.LandId
                            : ((_board != null && existing.LandId >= 0 && _board.Contains(existing.LandId)) ? existing.LandId : upd.LandId);

                        existing.Slot = upd.Slot;
                        if (!string.IsNullOrEmpty(upd.Name)) existing.Name = upd.Name;
                        existing.LandId = mergedLand;
                        existing.IsAlive = upd.IsAlive;
                        existing.IsTargetable = upd.IsTargetable;
                        existing.CanBeAttacked = upd.CanBeAttacked;
                        existing.Gold = upd.Gold;
                        existing.CrimeNum = upd.CrimeNum;
                        existing.HasProblemStudentMark = upd.HasProblemStudentMark;
                        existing.HasMandarinDuckMark = upd.HasMandarinDuckMark;
                        changed = true;
                        break;
                    }
                }
                if (!found)
                {
                    // Finding #18: include independently confirmed new players (e.g. newly readable P4)
                    // even on a partial roster frame so they are never omitted from forecasts or sandbox!
                    _players.Add(upd);
                    changed = true;
                }
            }
            if (changed)
            {
                _players.Sort((a, b) =>
                {
                    int cmp = a.Slot.CompareTo(b.Slot);
                    return cmp != 0 ? cmp : a.PlayerId.CompareTo(b.PlayerId);
                });
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
                    if (_sessionStarted || _selectedMonsterId != 0 || _board != null || _popup.IsOpen)
                        ResetBattleState();
                    else
                        _tracker.SetEventCollectionEnabled(false);
                    return;
                }

                var sessionState = GetPveSessionState(out var battle);
                if (sessionState == PveSessionState.ConfirmedExited)
                {
                    if (_sessionStarted || _selectedMonsterId != 0 || _board != null || _popup.IsOpen)
                        ResetBattleState();
                    else
                        _tracker.SetEventCollectionEnabled(false);
                    return;
                }
                if (sessionState == PveSessionState.TransientUnavailable)
                {
                    // Even during a transient room/scene-metadata gap, keep observing available battle turns so
                    // A -> B -> A transitions are never missed while rendering & input stay suspended.
                    SyncActiveBattleSceneHandle();
                    if (battle != null)
                    {
                        _sessionStarted = true;
                        _tracker.SetEventCollectionEnabled(true);
                        HandleMatchSyncResult(_tracker.ObserveTurnBoundary(battle, out _, out _));
                    }
                    _picker.CancelPendingPointer();
                    _popup.CancelPendingPress();
                    _renderer.HideAll();
                    _popup.SetTemporarilyHidden(true);
                    _needsUnitSync = true;
                    return;
                }

                // Enable game event ingestion only while actively inside an enabled PVE battle
                // (stays active across FightWindow so combat events resolve accurately upon return).
                _sessionStarted = true;
                _tracker.SetEventCollectionEnabled(true);
                // Bind the live BattleSceneController owner BEFORE visual gates so scene B taking over
                // during an overlay prevents unloading old scene A from resetting B's session.
                SyncActiveBattleSceneHandle();

                // Keep authoritative turn-boundary tracking advancing even while FightWindow / settings popups
                // or transient camera hiccups temporarily suspend visual overlay rendering (prevents A->B->A miss).
                HandleMatchSyncResult(_tracker.ObserveTurnBoundary(battle, out _, out _));

                // Combat window or in-battle pause/settings popup: temporarily hide overlay AND popup
                // without dropping the locked monster so tracking resumes automatically afterwards.
                if (IsTemporaryOverlayOpen())
                {
                    _picker.CancelPendingPointer();
                    _popup.CancelPendingPress();
                    _renderer.HideAll();
                    _popup.SetTemporarilyHidden(true);
                    _needsUnitSync = true;
                    MarkPlanInputsChanged();
                    return;
                }

                // Distinguish missing/empty/rebuilding LandManager from a camera-only hiccup:
                // latch _boardScanIncomplete whenever the board is missing/empty so recovery ALWAYS
                // performs a full board rescan before any unit sync, input, or render.
                if (!TryGetLandManager(out var landManager))
                {
                    SuspendForIncompleteBoard();
                    return;
                }

                if (!TryGetCamera(out var camera))
                {
                    _picker.CancelPendingPointer();
                    _popup.CancelPendingPress();
                    _renderer.HideAll();
                    _popup.SetTemporarilyHidden(true);
                    _needsUnitSync = true;
                    return;
                }

                if (_tracker.IsAwaitingMapLayoutReplacement(landManager))
                {
                    // 1086 MapIndexChangeS2C arrived and MapSceneChange() has not replaced LandManager yet:
                    // suspend tile-dependent planning and input until the replacement board loads.
                    SuspendForIncompleteBoard();
                    return;
                }

                double nowSec = MonotonicSeconds();
                long placeSeqSnapshot = _tracker.LatestPlaceSequence;
                bool managerForceReplace = _tracker.ShouldForceTopologyReplaced(landManager);
                bool forceBoardScan = _board == null || _boardScanIncomplete || managerForceReplace || nowSec >= _nextBoardScanSec || _tracker.HasUnvalidatedPlace;

                if (forceBoardScan)
                {
                    var nextBoard = _tracker.CaptureBoard(landManager, out bool boardComplete);
                    if (boardComplete)
                    {
                        _boardScanIncomplete = false;
                        _nextBoardScanSec = nowSec + 0.50;
                        if (_board == null)
                        {
                            _board = nextBoard;
                            _tracker.MarkMapLayoutCaptured(landManager);
                            _awaitingPostLayoutUnitSync = true;
                            _needsUnitSync = true;
                            MarkPlanInputsChanged(forceReplan: true);
                            _tracker.AcknowledgeBoardValidatedUpTo(_board, placeSeqSnapshot);
                        }
                        else if (!_board.SameTopology(nextBoard) || managerForceReplace)
                        {
                            _tracker.MarkMapLayoutCaptured(landManager);
                            HandleBoardTopologyReplaced(nextBoard, placeSeqSnapshot);
                        }
                        else
                        {
                            _tracker.MarkMapLayoutCaptured(landManager);
                            _tracker.AcknowledgeBoardValidatedUpTo(_board, placeSeqSnapshot);
                        }
                    }
                    else
                    {
                        SuspendForIncompleteBoard();
                        return;
                    }
                }

                if (_board == null || _boardScanIncomplete)
                {
                    SuspendForIncompleteBoard();
                    return;
                }

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
                        out var matchSync, out bool snapshotValid,
                        out bool playersAuthoritative, out bool playerPositionsReady);
                    if (matchSync == MatchSyncResult.Switched ||
                        (matchSync == MatchSyncResult.InitialAcquired &&
                         (_board != null || _monsters.Count > 0 || _players.Count > 0 || _selectedMonsterId != 0 || _virtualSandbox.HasAnyState)))
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
                            // On an established layout, preserve any previously confirmed same-layout player tile
                            // if a living hero's tile was transiently unreadable on this frame (never dropping the hero).
                            if (!_awaitingPostLayoutUnitSync && !playerPositionsReady && _players.Count > 0 && _board != null)
                            {
                                for (int ni = 0; ni < nextPlayers.Count; ni++)
                                {
                                    var np = nextPlayers[ni];
                                    if (np == null || !np.IsAlive || (np.LandId >= 0 && _board.Contains(np.LandId))) continue;
                                    for (int oi = 0; oi < _players.Count; oi++)
                                    {
                                        var op = _players[oi];
                                        if (op != null && op.PlayerId == np.PlayerId && op.LandId >= 0 && _board.Contains(op.LandId))
                                        {
                                            np.LandId = op.LandId;
                                            break;
                                        }
                                    }
                                }
                            }
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
                    _picker.CancelPendingPointer();
                    _popup.CancelPendingPress();
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
                    long rmbScanSeq = _tracker.LatestPlaceSequence;
                    var commitBoard = _tracker.CaptureBoard(landManager, out bool commitComplete);
                    if (commitComplete && _board.SameTopology(commitBoard))
                    {
                        OnVirtualMoveRequested(pickedVirtualPlayerId, pickedVirtualLandId, commitBoard);
                    }
                    else if (commitComplete)
                    {
                        HandleBoardTopologyReplaced(commitBoard, rmbScanSeq);
                        return;
                    }
                    else
                    {
                        SuspendForIncompleteBoard();
                        return;
                    }
                }
                if (clearVirtualRequested && _virtualSandbox.HasAnyState)
                {
                    OnVirtualResetAllRequested(_tracker.MatchGeneration, _virtualSandbox.LayoutGeneration);
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
                    _popup.Refresh(_monsters, _players, _board, _virtualSandbox, _tracker.LocalPlayerId, _selectedMonsterId, _settings, _tracker.MatchGeneration, _popupDataRevision);
                }

                _virtualSandbox.BuildGhostsInto(_players, _board, _tracker.LocalPlayerId, _ghostBuf);

                if (_selectedMonsterId == 0)
                {
                    _planCache.Clear();
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

                // Revision-gated signature cache: skips player cloning & signature building between snapshots,
                // AND skips MonsterPathEngine.Plan completely on unchanged 120ms snapshots!
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
                var activePlan = _planCache.GetOrCompute(
                    _board,
                    selected.State,
                    effectivePlayers,
                    planOpts,
                    _virtualSandbox.LayoutGeneration);

                // Refresh live sprite bounds every frame so the header badge smoothly tracks moving monsters
                if (selected.CharacterRef != null && UnityObject.IsAlive(selected.CharacterRef))
                {
                    selected.VisualBounds = SpriteBoundsReader.Capture(selected.CharacterRef, selected.FeetWorldPos, camera);
                }

                _renderer.Render(camera, _board, selected, activePlan, _settings, _ghostBuf);
            }
            catch (Exception ex)
            {
                _cachedEffectivePlayers = null;
                _needsUnitSync = true;
                MarkPlanInputsChanged(forceReplan: true);
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

            // Obtain any available battle handle independently of SceneManager availability so transient
            // scene-metadata gaps still observe turn boundaries cleanly.
            GameLogicManager gm = null;
            if (SimpleSingletonProvider<GameLogicManager>.hasInstance)
            {
                gm = SimpleSingletonProvider<GameLogicManager>.inst;
                battle = gm != null ? gm.battle : null;
            }

            if (IsBattleSettled()) return PveSessionState.ConfirmedExited;

            if (!SimpleSingletonProvider<global::Core.Scene.SceneManager>.hasInstance)
                return (_sessionStarted || _board != null || battle != null)
                    ? PveSessionState.TransientUnavailable
                    : PveSessionState.ConfirmedExited;

            if (SimpleSingletonProvider<global::Core.Scene.SceneManager>.inst.currentType.Value != SceneType.Battle)
                return PveSessionState.ConfirmedExited;

            if (gm == null)
                return PveSessionState.TransientUnavailable;

            var room = gm.room;
            if (room == null || room.curRoomInfo == null)
                return PveSessionState.TransientUnavailable;

            if (!room.curRoomInfo.IsPVE())
                return PveSessionState.ConfirmedExited;

            if (battle == null)
                return PveSessionState.TransientUnavailable;

            return PveSessionState.Active;
        }

        private static bool TryGetLandManager(out LandManager landManager)
        {
            landManager = null;
            if (!SimpleSingletonProvider<LandManager>.hasInstance) return false;
            landManager = SimpleSingletonProvider<LandManager>.inst;
            return landManager != null && landManager.NodeDict != null && landManager.NodeDict.Count > 0;
        }

        private static bool TryGetCamera(out Camera camera)
        {
            camera = null;
            var sceneCtrl = BattleSceneController.inst;
            if (sceneCtrl == null) return false;
            camera = sceneCtrl.mainCamera;
            return UnityObject.IsAlive(camera);
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
            _planCache.Clear();
            _popupDataRevision++;
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
            _boardScanIncomplete = false;
            _activeBattleSceneHandle = -1;
            _monsters.Clear();
            _players.Clear();
            _cachedEffectivePlayers = null;
            _lastSnapshotInvalid = false;
            _nextBoardScanSec = 0.0;
            _nextUnitSyncSec = 0.0;
            _needsUnitSync = true;
            _awaitingPostLayoutUnitSync = true;
            MarkPlanInputsChanged(forceReplan: true);
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
            _boardScanIncomplete = false;
            _sessionStarted = false;
            _monsters.Clear();
            _players.Clear();
            _cachedEffectivePlayers = null;
            _lastSnapshotInvalid = false;
            _nextBoardScanSec = 0.0;
            _nextUnitSyncSec = 0.0;
            _needsUnitSync = false;
            _awaitingPostLayoutUnitSync = false;
            _planCache.Clear();
            _activeBattleSceneHandle = -1;
            _tracker.SetEventCollectionEnabled(false);
            _tracker.Reset();
        }
    }
}
