using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using CesiumLoader.SDK.Engine;
using CesiumLoader.SDK.Gameplay;
using Core.Unit;
using GameLogic;
using party.model;
using party.protocol;
using UnityEngine;
using MonsterPathPredictor.Core;

namespace MonsterPathPredictor.Runtime
{
    internal enum MatchSyncResult
    {
        Same = 0,
        InitialAcquired = 1,
        Switched = 2,
        Unavailable = 3
    }

    internal enum LiveMonsterCheckResult
    {
        Eligible = 0,
        DeadOrIneligible = 1,
        Indeterminate = 2
    }

    internal sealed class LiveMonsterInfo
    {
        public MonsterState State;
        public Character CharacterRef;
        public Vector3 FeetWorldPos;
        public MonsterVisualBounds VisualBounds;
    }

    /// <summary>
    /// Captures live board topology, active monsters (with exact sprite world bounds and disambiguated names),
    /// trackable players, and transient combat / spawn / skill-move states.
    /// </summary>
    internal sealed class MonsterStateTracker
    {
        private struct HeuristicClaimEntry
        {
            public int MoveSeq;
            public int LandId;
            public SkillCastRecord Cast;
        }

        private sealed class MonsterMemory
        {
            public int SpawnSequence;
            public int NameOrdinal;
            public string NameBucket = string.Empty;
            public bool NameBucketIsProvisional;
            public bool HasBeenObservedOnBoard;
            public int MissingAuthoritativeFrames;
            public bool HasConfirmedActionWeight;
            public bool HasConfirmedClassification;
            public int LastActionWeight;
            public int LastHeroId;
            public bool LastIsNormalAggressive = true;
            public bool LastIsThief;
            public int LastHp;
            public int LastMaxHp;
            public bool LastHasHp = true;
            public int LastLandId = -1;
            public int LastVisualLandId = -1;
            public int LastFromLandId = -1;
            public int MoveSequence;
            public long LastAppliedPlaceSeq;
            public bool JustSpawnedOnFork;
            public bool JustFoughtOnFork;
            public int CombatForkLandId = -1;
            public int CombatTurnSequence = -1;
            public int LaunchedFightTurnSequence = -1;
            public int BossTargetLockTurnSequence = -1;
            public int BossTargetClearedTurnSequence = -1;
            public bool HasLastRawServerBossTarget;
            public long LastRawServerBossTargetId;
            public long EffectiveServerBossTargetId;
            public int BossTargetAssignedTurnSeq = -1;
            public long BossTargetAssignedOrderSeq;
            public int BossTargetAssignmentVersion;
            public long InferredBossTargetId;
            public bool HasPendingServerBossTarget;
            public long PendingServerBossTargetId;
            public long PendingServerBossTargetOrderSeq;
            public readonly List<(long OpponentId, int TurnSeq, int Version)> RecentClearedBossFights = new List<(long, int, int)>(8);
            public int StolenTurnSequence = -1;
            public SkillMoveKind SkillMoveKind = SkillMoveKind.None;
            public bool SkillMoveFromHeuristic;
            public SkillCastRecord HeuristicSkillCast;
            public readonly List<HeuristicClaimEntry> HeuristicClaims = new List<HeuristicClaimEntry>(4);
            public int UnmatchedOffTurnMoveSeq = -1;
            public int UnmatchedOffTurnFromLandId = -1;
            public int UnmatchedOffTurnToLandId = -1;
            public double UnmatchedOffTurnTimestampSec;
            public int SkillMoveLandId = -1;
            public int SkillMovePushDirId = -1;
            public int SkillMovePushFromId = -1;
            public long SkillMoveCasterPlayerId;
            public double SkillMoveTimestampSec;
            public int[] ExplicitFrontNodeIds;
            public int ExplicitFrontLandId = -1;
            public int ExplicitFrontFromLandId = -1;

            public long EffectiveBossTargetId => EffectiveServerBossTargetId != 0 ? EffectiveServerBossTargetId : InferredBossTargetId;
        }

        private sealed class ConfirmedHeroCache
        {
            public bool HasSlot;
            public int Slot;
            public string Name = string.Empty;
            public int LandId = -1;
            public bool IsAlive = true;
            public bool HasTargetable;
            public bool IsTargetable = true;
            public bool HasBuffRead;
            public bool CanBeAttacked = true;
            public bool IsTrueCulprit;
            public bool HasGold;
            public int Gold = -1;
            public bool HasCrimeNum;
            public int CrimeNum;
            public bool HasProblemStudentMark;
            public bool HasMandarinDuckMark;
        }

        private struct DiceRollRecord
        {
            public long OwnerId;
            public long MatchRoomId;
            public long MatchStartTime;
            public int MatchGeneration;
            public int DiceSeq;
        }

        private sealed class TurnStamp
        {
            public readonly long ActiveActorId;
            public readonly long RawActorId;
            public readonly int TurnSequence;
            public readonly int AcceptedRound;
            public readonly bool StalePending;
            public TurnStamp(long activeActorId, long rawActorId, int turnSequence, int acceptedRound, bool stalePending)
            {
                ActiveActorId = activeActorId;
                RawActorId = rawActorId;
                TurnSequence = turnSequence;
                AcceptedRound = acceptedRound;
                StalePending = stalePending;
            }
        }

        private struct ExactDisplacementReceipt
        {
            public long MonsterId;
            public SkillMoveKind Kind;
            public long CasterPlayerId;
            public double TimestampSec;
        }

        private sealed class SkillCastRecord
        {
            public long CasterPlayerId;
            public int SkillId;
            public long MatchRoomId;
            public long MatchStartTime;
            public int MatchGeneration;
            public int LayoutGeneration;
            public double TimestampSec;
            public HashSet<long> AppliedMonsterIds = new HashSet<long>();
        }

        private sealed class SkillDisplacementRecord
        {
            public long PlaceSeq;
            public long TargetMonsterId;
            public long CasterPlayerId;
            public int SkillId;
            public int NewLandId;
            public int BackLandId;
            public int[] FrontNodeIds;
            public long MatchRoomId;
            public long MatchStartTime;
            public int MatchGeneration;
            public int LayoutGeneration;
            public double TimestampSec;
            public bool ValidatedForCurrentLayout;
            public bool ObservedIncompatibleWithPreviousBoard;
            public int LastCheckedBoardRevision;
        }

        private struct CombatRecord
        {
            public long CombatOrderSeq;
            public long AttackerId;
            public long DefenderId;
            public int AttackerLandId;
            public int DefenderLandId;
            public bool? AttackerHadProblemStudent;
            public bool? AttackerHadMandarinDuck;
            public bool? DefenderHadProblemStudent;
            public bool? DefenderHadMandarinDuck;
            public long ActiveActorAtEvent;
            public long PublishedActorAtEvent;
            public int PublishedTurnSequenceAtEvent;
            public int PublishedRoundAtEvent;
            public int DiceSeqAtEvent;
            public long PlaceSeqAtEvent;
            public int Round;
            public long MatchRoomId;
            public long MatchStartTime;
            public int MatchGeneration;
            public int LayoutGeneration;
        }

        private struct ThiefStealRecord
        {
            public long ThiefId;
            public long VictimId;
            public int LandId;
            public long ActiveActorAtEvent;
            public long PublishedActorAtEvent;
            public int PublishedTurnSequenceAtEvent;
            public int PublishedRoundAtEvent;
            public int DiceSeqAtEvent;
            public long PlaceSeqAtEvent;
            public int Round;
            public long MatchRoomId;
            public long MatchStartTime;
            public int MatchGeneration;
            public int LayoutGeneration;
        }

        private const int MaxQueuedEvents = 256;
        private const double RetiredMonsterGraceSec = 30.0;

        private readonly object _diceQueueLock = new object();
        private readonly Queue<DiceRollRecord> _diceQueue = new Queue<DiceRollRecord>(16);
        private readonly ConcurrentQueue<SkillCastRecord> _skillQueue = new ConcurrentQueue<SkillCastRecord>();
        private readonly object _placeQueueLock = new object();
        private readonly Queue<SkillDisplacementRecord> _placeQueue = new Queue<SkillDisplacementRecord>(16);
        private readonly ConcurrentQueue<CombatRecord> _combatQueue = new ConcurrentQueue<CombatRecord>();
        private readonly ConcurrentQueue<ThiefStealRecord> _thiefStealQueue = new ConcurrentQueue<ThiefStealRecord>();

        private enum ExactPlaceLookup
        {
            NotFound = 0,
            KnownOtherCause = 1,
            TaggedDisplacement = 2
        }

        private enum EventTurnMatch
        {
            Pending = 0,
            Matched = 1,
            Superseded = 2
        }

        private readonly Dictionary<long, MonsterMemory> _memories = new Dictionary<long, MonsterMemory>();
        private readonly Dictionary<string, int> _nextNameOrdinalByName = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<string> _disambiguatedNames = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<long> _rosterAliveMonsterIds = new HashSet<long>();
        private readonly HashSet<long> _knownPlayerIds = new HashSet<long>();
        private readonly Dictionary<long, int> _missingHeroAuthoritativeFrames = new Dictionary<long, int>();
        private readonly Dictionary<long, bool> _lastKnownPlayerAlive = new Dictionary<long, bool>();
        private readonly Dictionary<long, int> _lastHeroProtoNodeId = new Dictionary<long, int>();
        private readonly Dictionary<long, int> _preLayoutHeroProtoNodeId = new Dictionary<long, int>();
        private readonly HashSet<long> _heroConfirmedOnCurrentLayout = new HashSet<long>();
        private readonly HashSet<long> _heroProtoConfirmedOnCurrentLayout = new HashSet<long>();
        private readonly Dictionary<long, ConfirmedHeroCache> _confirmedHeroFields = new Dictionary<long, ConfirmedHeroCache>();
        private readonly Dictionary<int, object> _currentLayoutLandById = new Dictionary<int, object>();
        private bool _awaitingPostLayoutHeroConfirmation;
        private readonly Dictionary<long, double> _retiredMonsterSec = new Dictionary<long, double>();
        private readonly Dictionary<long, List<DiceRollRecord>> _pendingDiceByOwner = new Dictionary<long, List<DiceRollRecord>>();
        private readonly ActionRoundTracker _roundTracker = new ActionRoundTracker();
        private readonly List<SkillCastRecord> _recentSkills = new List<SkillCastRecord>();
        private readonly List<ExactDisplacementReceipt> _recentExactDisplacements = new List<ExactDisplacementReceipt>();
        private readonly Dictionary<long, List<SkillDisplacementRecord>> _pendingPlaceByMonster = new Dictionary<long, List<SkillDisplacementRecord>>();
        private readonly Dictionary<long, List<PendingUnitCombat>> _pendingCombatByMonster = new Dictionary<long, List<PendingUnitCombat>>();
        private readonly Dictionary<long, List<PendingThiefSteal>> _pendingStealByMonster = new Dictionary<long, List<PendingThiefSteal>>();
        private readonly Dictionary<int, long> _lastAcceptedHeroIdToPlayerId = new Dictionary<int, long>();

        private bool _lastAcceptedAnyPlayerHasTrueCulprit;
        private int _knownPlayerCount;
        private int _nextSpawnSequence = 1;
        private long _localPlayerId;
        private int _activeActorStartLandId = -1;
        private bool _activeActorStartFromVisual;
        private int _activeActorLastSeenLandId = -1;
        private bool _activeActorLastSeenFromVisual;
        private bool _turnStartLayoutInvalidated;
        private bool _activeActorSteppedThisTurn;
        private TurnStamp _publishedTurn = new TurnStamp(0, 0, 0, -1, false);
        private int _totalDiceSeq;
        private int _lastConsumedDiceSeq;
        private int _currentTurnOwnerStartDiceSeq;
        private long _bossOrderSeq;
        private long _nextPlaceSeq;
        private long _validatedPlaceSeq;
        private long _lastOldLayoutValidatedSeq;
        private int _layoutGeneration;
        private int _boardScanRevision;
        private BoardGraph _lastValidatedBoard;
        private BoardGraph _previousBoardBeforeTopologyChange;

        private long _matchRoomId;
        private long _matchStartTime;
        private int _matchGeneration;
        private bool _hooked;
        private volatile bool _acceptEvents;

        private static double MonotonicSeconds()
        {
            return Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        }

        private int CommittedDiceSequence
        {
            get { lock (_diceQueueLock) return _totalDiceSeq; }
        }

        public long LatestPlaceSequence
        {
            get { lock (_placeQueueLock) return _nextPlaceSeq; }
        }

        public bool HasUnvalidatedPlace
        {
            get
            {
                int curBoardRev = Volatile.Read(ref _boardScanRevision);
                lock (_placeQueueLock)
                {
                    if (_placeQueue.Count > 0 || _nextPlaceSeq > _validatedPlaceSeq) return true;
                }
                foreach (var list in _pendingPlaceByMonster.Values)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        var r = list[i];
                        // Incompatible future-layout records already checked against curBoardRev wait for
                        // the regular board rescan/change instead of forcing full scans every frame.
                        if (!r.ValidatedForCurrentLayout &&
                            (!r.ObservedIncompatibleWithPreviousBoard || r.LastCheckedBoardRevision < curBoardRev))
                        {
                            return true;
                        }
                    }
                }
                return false;
            }
        }

        public int MatchGeneration => Volatile.Read(ref _matchGeneration);

        public void AcknowledgeBoardValidatedUpTo(BoardGraph completeBoard, long placeSeq)
        {
            if (completeBoard == null) return;
            DrainPlaceQueue();
            int curLayoutGen = Volatile.Read(ref _layoutGeneration);
            int curBoardRev = Interlocked.Increment(ref _boardScanRevision);
            lock (_placeQueueLock)
            {
                _lastValidatedBoard = completeBoard;
                if (placeSeq > _validatedPlaceSeq) _validatedPlaceSeq = placeSeq;
            }

            foreach (var list in _pendingPlaceByMonster.Values)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var rec = list[i];
                    if (rec.LayoutGeneration != curLayoutGen || rec.PlaceSeq > placeSeq) continue;
                    rec.LastCheckedBoardRevision = curBoardRev;
                    bool compatible = IsPlaceCompatibleWithBoard(rec, completeBoard);
                    if (compatible)
                    {
                        rec.ValidatedForCurrentLayout = true;
                        rec.ObservedIncompatibleWithPreviousBoard = false;
                    }
                    else
                    {
                        // Keep records incompatible with the currently scanned board quarantined/pending with
                        // positive mismatch evidence so a subsequent layout replacement migrates them cleanly.
                        rec.ValidatedForCurrentLayout = false;
                        rec.ObservedIncompatibleWithPreviousBoard = true;
                    }
                }
            }
        }

        private static bool IsPlaceCompatibleWithBoard(SkillDisplacementRecord rec, BoardGraph board)
        {
            if (rec == null || board == null) return false;
            if (!board.Contains(rec.NewLandId)) return false;
            // A displacement's BackLandId may be non-adjacent, so check node membership only.
            if (rec.BackLandId >= 0 && !board.Contains(rec.BackLandId)) return false;
            // Supplied FrontNodeIds represent outgoing exits from NewLandId: verify outgoing adjacency!
            if (rec.FrontNodeIds != null && rec.FrontNodeIds.Length > 0)
            {
                for (int i = 0; i < rec.FrontNodeIds.Length; i++)
                {
                    int fid = rec.FrontNodeIds[i];
                    if (fid >= 0 && !board.IsValidNeighbor(rec.NewLandId, fid)) return false;
                }
            }
            return true;
        }

        private bool TryMigratePlaceAcrossLayoutChange(
            SkillDisplacementRecord rec,
            BoardGraph oldBoard,
            BoardGraph newBoard,
            long oldValidatedSeq,
            int newLayoutGen,
            long newBoardScanMaxSeq)
        {
            if (rec == null || newBoard == null) return false;
            if (!IsPlaceCompatibleWithBoard(rec, newBoard)) return false;

            // Positive new-layout evidence: previously observed incompatible with oldBoard, OR
            // directly incompatible with oldBoard (including distinguishing FrontNodeIds outgoing exits!).
            bool positiveNewLayoutEvidence =
                rec.ObservedIncompatibleWithPreviousBoard ||
                (oldBoard != null && !IsPlaceCompatibleWithBoard(rec, oldBoard));

            if (positiveNewLayoutEvidence)
            {
                rec.LayoutGeneration = newLayoutGen;
                rec.ObservedIncompatibleWithPreviousBoard = false;
                rec.LastCheckedBoardRevision = Volatile.Read(ref _boardScanRevision);
                rec.ValidatedForCurrentLayout = newBoardScanMaxSeq > 0 && rec.PlaceSeq <= newBoardScanMaxSeq;
                return true;
            }

            // Otherwise the record's tile IDs & facing exits existed on oldBoard: quarantine ambiguous overlapping IDs.
            return false;
        }

        public void Start()
        {
            if (_hooked) return;
            _hooked = true;
            GameEvents.FightResolved += OnFightResolved;
            GameEvents.BattleUpdate += OnBattleUpdate;
            GameEvents.SkillUsed += OnSkillUsed;
            GameEvents.DiceResult += OnDiceResult;
            GameEvents.HeroAttrUpdated += OnHeroAttrUpdated;
            GameEvents.StartAutoHook();
        }

        public void Stop()
        {
            _acceptEvents = false;
            if (!_hooked) return;
            _hooked = false;
            GameEvents.FightResolved -= OnFightResolved;
            GameEvents.BattleUpdate -= OnBattleUpdate;
            GameEvents.SkillUsed -= OnSkillUsed;
            GameEvents.DiceResult -= OnDiceResult;
            GameEvents.HeroAttrUpdated -= OnHeroAttrUpdated;
            Reset();
        }

        public void SetEventCollectionEnabled(bool enabled)
        {
            if (_acceptEvents == enabled) return;
            _acceptEvents = enabled;
            if (!enabled)
            {
                Reset();
            }
        }

        public long LocalPlayerId => _localPlayerId;

        private void PublishTurnStampIfChanged()
        {
            long effActor = _roundTracker.EffectiveActiveActorId;
            long rawActor = _roundTracker.ActiveActorId;
            int turnSeq = _roundTracker.TurnSequence;
            int accRound = _roundTracker.LastObservedRound;
            bool stale = _roundTracker.IsCurrentActorStalePendingRound;
            var cur = Volatile.Read(ref _publishedTurn);
            if (cur.ActiveActorId == effActor &&
                cur.RawActorId == rawActor &&
                cur.TurnSequence == turnSeq &&
                cur.AcceptedRound == accRound &&
                cur.StalePending == stale)
            {
                return;
            }
            Volatile.Write(ref _publishedTurn, new TurnStamp(effActor, rawActor, turnSeq, accRound, stale));
        }

        public void Reset()
        {
            _memories.Clear();
            _nextNameOrdinalByName.Clear();
            _disambiguatedNames.Clear();
            _rosterAliveMonsterIds.Clear();
            _knownPlayerIds.Clear();
            _missingHeroAuthoritativeFrames.Clear();
            _lastKnownPlayerAlive.Clear();
            _lastHeroProtoNodeId.Clear();
            _preLayoutHeroProtoNodeId.Clear();
            _heroConfirmedOnCurrentLayout.Clear();
            _heroProtoConfirmedOnCurrentLayout.Clear();
            _confirmedHeroFields.Clear();
            _currentLayoutLandById.Clear();
            _awaitingPostLayoutHeroConfirmation = false;
            _retiredMonsterSec.Clear();
            _pendingDiceByOwner.Clear();
            _recentSkills.Clear();
            _recentExactDisplacements.Clear();
            _pendingPlaceByMonster.Clear();
            _pendingCombatByMonster.Clear();
            _pendingStealByMonster.Clear();
            _lastAcceptedHeroIdToPlayerId.Clear();
            _lastAcceptedAnyPlayerHasTrueCulprit = false;
            _knownPlayerCount = 0;
            lock (_diceQueueLock)
            {
                _diceQueue.Clear();
                _lastConsumedDiceSeq = _totalDiceSeq;
            }
            while (_skillQueue.TryDequeue(out _)) { }
            while (_combatQueue.TryDequeue(out _)) { }
            while (_thiefStealQueue.TryDequeue(out _)) { }
            _nextSpawnSequence = 1;
            _localPlayerId = 0;
            _activeActorStartLandId = -1;
            _activeActorStartFromVisual = false;
            _activeActorLastSeenLandId = -1;
            _activeActorLastSeenFromVisual = false;
            _turnStartLayoutInvalidated = false;
            _activeActorSteppedThisTurn = false;
            _currentTurnOwnerStartDiceSeq = 0;
            _roundTracker.Reset(0);
            PublishTurnStampIfChanged();
            _matchRoomId = 0;
            _matchStartTime = 0;
            lock (_placeQueueLock)
            {
                _placeQueue.Clear();
                _lastOldLayoutValidatedSeq = _nextPlaceSeq;
                _validatedPlaceSeq = _nextPlaceSeq;
                _lastValidatedBoard = null;
                _previousBoardBeforeTopologyChange = null;
                Interlocked.Increment(ref _layoutGeneration);
            }
            Interlocked.Increment(ref _matchGeneration);
        }

        /// <summary>
        /// Clears old-layout turn-start anchor and tile-scoped memory when the board topology switches mid-match,
        /// migrating ONLY records with positive new-layout evidence and quarantining old/ambiguous overlapping IDs.
        /// </summary>
        public void NotifyBoardTopologyChanged(BoardGraph newBoard = null, long newBoardScanMaxSeq = 0)
        {
            BoardGraph oldBoard;
            long oldValidatedSeq;
            int newLayoutGen;
            lock (_placeQueueLock)
            {
                oldBoard = _lastValidatedBoard;
                oldValidatedSeq = _validatedPlaceSeq;
                _lastOldLayoutValidatedSeq = oldValidatedSeq;
                _previousBoardBeforeTopologyChange = oldBoard;
                _lastValidatedBoard = newBoard;
                if (newBoard != null && newBoardScanMaxSeq > _validatedPlaceSeq)
                {
                    _validatedPlaceSeq = newBoardScanMaxSeq;
                }
                newLayoutGen = Interlocked.Increment(ref _layoutGeneration);
            }

            _activeActorStartLandId = -1;
            _activeActorStartFromVisual = false;
            _activeActorLastSeenLandId = -1;
            _activeActorLastSeenFromVisual = false;
            _turnStartLayoutInvalidated = true;
            _recentSkills.Clear();
            _recentExactDisplacements.Clear();

            // Snapshot pre-layout hero protocol tiles so post-layout readiness never accepts unchanged overlapping IDs.
            _preLayoutHeroProtoNodeId.Clear();
            foreach (var kv in _lastHeroProtoNodeId) _preLayoutHeroProtoNodeId[kv.Key] = kv.Value;
            _heroConfirmedOnCurrentLayout.Clear();
            _heroProtoConfirmedOnCurrentLayout.Clear();
            foreach (var hc in _confirmedHeroFields.Values) hc.LandId = -1;
            _awaitingPostLayoutHeroConfirmation = newBoard != null;
            Interlocked.Increment(ref _boardScanRevision);

            DrainPlaceQueue();
            if (newBoard == null)
            {
                _pendingPlaceByMonster.Clear();
            }
            else
            {
                List<long> emptyKeys = null;
                foreach (var kv in _pendingPlaceByMonster)
                {
                    var list = kv.Value;
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        var rec = list[i];
                        if (rec.LayoutGeneration == newLayoutGen)
                        {
                            // Already produced under newLayoutGen (or migrated on drain): validate compatibility.
                            if (IsPlaceCompatibleWithBoard(rec, newBoard))
                            {
                                if (newBoardScanMaxSeq > 0 && rec.PlaceSeq <= newBoardScanMaxSeq)
                                    rec.ValidatedForCurrentLayout = true;
                            }
                            else
                            {
                                rec.ObservedIncompatibleWithPreviousBoard = true;
                                rec.ValidatedForCurrentLayout = false;
                            }
                        }
                        else if (!TryMigratePlaceAcrossLayoutChange(rec, oldBoard, newBoard, oldValidatedSeq, newLayoutGen, newBoardScanMaxSeq))
                        {
                            list.RemoveAt(i);
                        }
                    }
                    if (list.Count == 0)
                    {
                        if (emptyKeys == null) emptyKeys = new List<long>();
                        emptyKeys.Add(kv.Key);
                    }
                }
                if (emptyKeys != null)
                {
                    for (int i = 0; i < emptyKeys.Count; i++) _pendingPlaceByMonster.Remove(emptyKeys[i]);
                }
            }

            foreach (var mem in _memories.Values)
            {
                mem.LastLandId = -1;
                mem.LastVisualLandId = -1;
                mem.LastFromLandId = -1;
                mem.JustSpawnedOnFork = false;
                mem.JustFoughtOnFork = false;
                mem.CombatForkLandId = -1;
                mem.SkillMoveKind = SkillMoveKind.None;
                mem.SkillMoveFromHeuristic = false;
                mem.HeuristicSkillCast = null;
                mem.HeuristicClaims.Clear();
                mem.UnmatchedOffTurnMoveSeq = -1;
                mem.UnmatchedOffTurnFromLandId = -1;
                mem.UnmatchedOffTurnToLandId = -1;
                mem.SkillMoveLandId = -1;
                mem.SkillMovePushDirId = -1;
                mem.SkillMovePushFromId = -1;
                mem.ExplicitFrontNodeIds = null;
                mem.ExplicitFrontLandId = -1;
                mem.ExplicitFrontFromLandId = -1;
            }
        }

        public bool IsMonsterInAliveRoster(long monsterId)
        {
            return monsterId != 0 && _rosterAliveMonsterIds.Contains(monsterId);
        }

        /// <summary>
        /// Authoritative live lookup for a hero's current liveness and board tile (never falling back to stale cache).
        /// Uses the exact same protocol-first authoritative land definition as <see cref="BuildTrackedPlayer"/>.
        /// </summary>
        public bool TryReadLiveHeroState(long playerId, BoardGraph board, out bool isAlive, out int authoritativeLandId)
        {
            isAlive = false;
            authoritativeLandId = -1;
            if (playerId <= 0) return false;
            try
            {
                var all = Players.All();
                if (all == null) return false;
                BattlePlayerData matched = null;
                // Scan the entire roster for cross-type/same-type duplicate ID conflicts before accepting any hero.
                for (int i = 0; i < all.Count; i++)
                {
                    var pd = all[i];
                    if (pd == null || pd.player == null || pd.player.Id != playerId) continue;
                    if (pd.characterType != CharacterType.Hero) return false;
                    if (matched != null) return false;
                    matched = pd;
                }
                if (matched == null) return false;
                var verdict = TryEvaluateUnitLiveness(matched);
                // Require a fresh authoritative liveness verdict for live-owner mutations (never fall back to cached alive).
                if (!verdict.HasValue) return false;
                isAlive = verdict.Value;
                authoritativeLandId = SafeAuthoritativeHeroLandId(matched, board, out bool posConfirmed);
                if (isAlive && !posConfirmed) authoritativeLandId = -1;
                return true;
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Checks live monster roster presence &amp; eligibility across the full unit list (rejecting cross-type
        /// or same-type duplicate IDs and distinguishing unavailable roster from dead/ineligible).
        /// </summary>
        public static LiveMonsterCheckResult CheckMonsterLiveAndEligible(long monsterId, bool allowNonAggressive)
        {
            if (monsterId <= 0) return LiveMonsterCheckResult.DeadOrIneligible;
            try
            {
                var all = Players.All();
                if (all == null || all.Count == 0) return LiveMonsterCheckResult.Indeterminate;
                BattlePlayerData matched = null;
                bool anyUnreadableEntry = false;
                for (int i = 0; i < all.Count; i++)
                {
                    var pd = all[i];
                    if (pd == null || pd.player == null)
                    {
                        anyUnreadableEntry = true;
                        continue;
                    }
                    if (pd.player.Id != monsterId) continue;
                    CharacterType ct;
                    try { ct = pd.characterType; } catch { return LiveMonsterCheckResult.Indeterminate; }
                    if (ct != CharacterType.Monster) return LiveMonsterCheckResult.DeadOrIneligible;
                    if (matched != null) return LiveMonsterCheckResult.Indeterminate; // Cross-entry conflict
                    matched = pd;
                }
                if (matched == null)
                {
                    return anyUnreadableEntry ? LiveMonsterCheckResult.Indeterminate : LiveMonsterCheckResult.DeadOrIneligible;
                }
                if (!TryCheckRosterMonsterAlive(matched, out bool alive)) return LiveMonsterCheckResult.Indeterminate;
                if (!alive) return LiveMonsterCheckResult.DeadOrIneligible;
                if (!allowNonAggressive)
                {
                    var aggOpt = TryReadIsNormalAggressive(matched);
                    if (!aggOpt.HasValue) return LiveMonsterCheckResult.Indeterminate;
                    if (!aggOpt.Value) return LiveMonsterCheckResult.DeadOrIneligible;
                }
                return LiveMonsterCheckResult.Eligible;
            }
            catch
            {
                return LiveMonsterCheckResult.Indeterminate;
            }
        }

        public static bool TryIsMonsterLiveAndEligible(long monsterId, bool allowNonAggressive)
        {
            return CheckMonsterLiveAndEligible(monsterId, allowNonAggressive) == LiveMonsterCheckResult.Eligible;
        }

        public static int TryReadLiveUnitLandId(long unitId)
        {
            if (unitId == 0) return -1;
            try
            {
                var all = Players.All();
                if (all == null) return -1;
                for (int i = 0; i < all.Count; i++)
                {
                    var pd = all[i];
                    if (pd != null && pd.player != null && pd.player.Id == unitId)
                    {
                        // Prefer authoritative protocol NodeId for event stamping so animation lag on standLand
                        // never pins combat onto the previous tile instead of the actual fork tile.
                        if (pd.player.NodeId >= 0) return pd.player.NodeId;
                        return SafeLandId(pd);
                    }
                }
            }
            catch { }
            return -1;
        }

        private static long TryReadCurrentActorId()
        {
            try
            {
                if (!Tools.SimpleSingletonProvider<GameLogicManager>.hasInstance) return 0;
                var cur = SafeGetCurrentPlayer(Tools.SimpleSingletonProvider<GameLogicManager>.inst.battle);
                return (cur != null && cur.player != null) ? cur.player.Id : 0;
            }
            catch { return 0; }
        }

        private static bool _roundMemberResolved;
        private static MemberInfo _roundMember;

        private static int TryReadCurrentRound()
        {
            try
            {
                if (!Tools.SimpleSingletonProvider<GameLogicManager>.hasInstance) return -1;
                var room = Tools.SimpleSingletonProvider<GameLogicManager>.inst.room;
                var info = room != null ? room.curRoomInfo : null;
                if (info == null) return -1;

                if (!_roundMemberResolved)
                {
                    _roundMemberResolved = true;
                    var t = info.GetType();
                    _roundMember = (MemberInfo)t.GetProperty("Round", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        ?? (MemberInfo)t.GetProperty("round", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        ?? (MemberInfo)t.GetField("Round", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        ?? t.GetField("round", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }

                if (_roundMember != null)
                {
                    object raw = _roundMember is PropertyInfo pi ? pi.GetValue(info) : ((FieldInfo)_roundMember).GetValue(info);
                    if (raw is int iv) return iv;
                    if (raw != null)
                    {
                        var valProp = raw.GetType().GetProperty("Value");
                        if (valProp != null && valProp.GetValue(raw) is int rv) return rv;
                    }
                }
            }
            catch { }
            return -1;
        }

        private static bool TryIsUnitThief(long unitId)
        {
            if (unitId == 0) return false;
            try
            {
                var pd = Players.Get(unitId);
                return pd != null && IsThiefMonster(pd);
            }
            catch { return false; }
        }

        private static bool TryIsUnitHero(long unitId)
        {
            if (unitId == 0) return false;
            try
            {
                var pd = Players.Get(unitId);
                return pd != null && pd.characterType == CharacterType.Hero;
            }
            catch { return false; }
        }

        private static void TryReadUnitBossMarks(long unitId, out bool? hasProblemStudent, out bool? hasMandarinDuck)
        {
            hasProblemStudent = null;
            hasMandarinDuck = null;
            if (unitId == 0) return;
            bool readAnySource = false;
            bool foundPs = false;
            bool foundMd = false;
            try
            {
                var buffs = Players.BuffsOf(unitId);
                if (buffs != null)
                {
                    readAnySource = true;
                    for (int i = 0; i < buffs.Count; i++)
                    {
                        int bid = buffs[i].BuffId;
                        if (bid == 2101001) foundPs = true;
                        else if (bid == 10470101) foundMd = true;
                    }
                }
            }
            catch { }
            try
            {
                var pd = Players.Get(unitId);
                var hero = pd != null && pd.player != null ? pd.player.Hero : null;
                if (hero != null && hero.Buffs != null)
                {
                    readAnySource = true;
                    foreach (var kv in hero.Buffs)
                    {
                        var b = kv.Value;
                        if (b == null) continue;
                        if (b.BuffId == 2101001) foundPs = true;
                        else if (b.BuffId == 10470101) foundMd = true;
                    }
                }
            }
            catch { }
            if (readAnySource)
            {
                hasProblemStudent = foundPs;
                hasMandarinDuck = foundMd;
            }
        }

        private void OnDiceResult(long playerId, int dice1, int dice2)
        {
            if (!_acceptEvents) return;
            TryStampCurrentMatch(out long matchRoomId, out long matchStartTime);
            int matchGen = Volatile.Read(ref _matchGeneration);
            // Publish DiceSeq and enqueue atomically so sequence order and queue order can never diverge.
            lock (_diceQueueLock)
            {
                int diceSeq = ++_totalDiceSeq;
                if (_diceQueue.Count >= MaxQueuedEvents) _diceQueue.Dequeue();
                _diceQueue.Enqueue(new DiceRollRecord
                {
                    OwnerId = playerId,
                    MatchRoomId = matchRoomId,
                    MatchStartTime = matchStartTime,
                    MatchGeneration = matchGen,
                    DiceSeq = diceSeq
                });
            }
        }

        private static void TryStampCurrentMatch(out long roomId, out long startTime)
        {
            roomId = 0;
            startTime = 0;
            try
            {
                if (Players.TryGetMatch(out var info))
                {
                    roomId = info.RoomId;
                    startTime = info.StartTime;
                }
            }
            catch { }
        }

        private void OnHeroAttrUpdated(UpdateHeroAttrS2C attr)
        {
            if (!_acceptEvents || attr == null || attr.EffectDatas == null || attr.EffectDatas.Count == 0) return;

            long ownerId = attr.PlayerId;
            var pubTurn = Volatile.Read(ref _publishedTurn);
            int curRound = TryReadCurrentRound();
            int curDiceSeq = CommittedDiceSequence;
            long curPlaceSeq = LatestPlaceSequence;
            long liveActorId = TryReadCurrentActorId();
            int causeId = attr.Cause != null ? (int)attr.Cause.Id : 0;
            TryStampCurrentMatch(out long matchRoomId, out long matchStartTime);
            int matchGen = Volatile.Read(ref _matchGeneration);
            // Capture the event's layout epoch ONCE at receipt (never replacing it with a newer layout
            // epoch at enqueue time if a topology switch raced while processing EffectDatas).
            int eventLayoutGen = Volatile.Read(ref _layoutGeneration);
            double nowSec = MonotonicSeconds();

            for (int i = 0; i < attr.EffectDatas.Count; i++)
            {
                var eff = attr.EffectDatas[i];
                if (eff == null) continue;

                // Protocol-authoritative Thief gold steal (cmd 1040 ONLY; excludes 1096 pass-by aura effects).
                // Defer thief/victim type classification to snapshot consumption if live lookup is unavailable now.
                if (ownerId > 0 && eff.Gold != null && eff.Gold.ChangeGold < 0 && eff.PlayerId > 0 && eff.PlayerId != ownerId && !TryIsUnitHero(ownerId))
                {
                    if (_thiefStealQueue.Count >= MaxQueuedEvents) _thiefStealQueue.TryDequeue(out _);
                    _thiefStealQueue.Enqueue(new ThiefStealRecord
                    {
                        ThiefId = ownerId,
                        VictimId = eff.PlayerId,
                        LandId = TryReadLiveUnitLandId(ownerId),
                        ActiveActorAtEvent = liveActorId,
                        PublishedActorAtEvent = pubTurn.RawActorId,
                        PublishedTurnSequenceAtEvent = pubTurn.TurnSequence,
                        PublishedRoundAtEvent = pubTurn.AcceptedRound,
                        DiceSeqAtEvent = curDiceSeq,
                        PlaceSeqAtEvent = curPlaceSeq,
                        Round = curRound,
                        MatchRoomId = matchRoomId,
                        MatchStartTime = matchStartTime,
                        MatchGeneration = matchGen,
                        LayoutGeneration = eventLayoutGen
                    });
                }

                // Target-bound skill displacement (cmd 1040 Place effect carries exact victim MonsterId + caster + Cause).
                // Negative NodeId (< 0) is a protocol sentinel (no destination tile): never enqueue as a wildcard tile match.
                if (eff.Place != null && eff.Place.Place != null && eff.Place.Place.NodeId >= 0 && eff.PlayerId != 0 && !TryIsUnitHero(eff.PlayerId))
                {
                    int[] placeFrontIds = Array.Empty<int>();
                    try
                    {
                        var rf = eff.Place.Place.FrontNodeIds;
                        if (rf != null && rf.Count > 0)
                        {
                            placeFrontIds = new int[rf.Count];
                            for (int fi = 0; fi < rf.Count; fi++) placeFrontIds[fi] = rf[fi];
                        }
                    }
                    catch { }
                    // Assign PlaceSeq and enqueue atomically while preserving the receipt's original eventLayoutGen
                    // so any raced topology switch routes this record through evidence-based migration!
                    lock (_placeQueueLock)
                    {
                        long placeSeq = ++_nextPlaceSeq;
                        if (_placeQueue.Count >= MaxQueuedEvents) _placeQueue.Dequeue();
                        _placeQueue.Enqueue(new SkillDisplacementRecord
                        {
                            PlaceSeq = placeSeq,
                            TargetMonsterId = eff.PlayerId,
                            CasterPlayerId = ownerId,
                            SkillId = causeId,
                            NewLandId = eff.Place.Place.NodeId,
                            BackLandId = eff.Place.Place.BackNodeId,
                            FrontNodeIds = placeFrontIds,
                            MatchRoomId = matchRoomId,
                            MatchStartTime = matchStartTime,
                            MatchGeneration = matchGen,
                            LayoutGeneration = eventLayoutGen,
                            TimestampSec = nowSec
                        });
                    }
                }
            }
        }

        private void OnFightResolved(FightSnapshot snap)
        {
            if (!_acceptEvents) return;
            if (snap.AttackerPlayerId == 0 && snap.DefenderPlayerId == 0) return;
            TryStampCurrentMatch(out long matchRoomId, out long matchStartTime);
            int matchGen = Volatile.Read(ref _matchGeneration);
            int layoutGen = Volatile.Read(ref _layoutGeneration);
            var pubTurn = Volatile.Read(ref _publishedTurn);
            int curDiceSeq = CommittedDiceSequence;
            long curPlaceSeq = LatestPlaceSequence;
            long orderSeq = Interlocked.Increment(ref _bossOrderSeq);
            TryReadUnitBossMarks(snap.AttackerPlayerId, out bool? atkPs, out bool? atkMd);
            TryReadUnitBossMarks(snap.DefenderPlayerId, out bool? defPs, out bool? defMd);
            if (_combatQueue.Count >= MaxQueuedEvents) _combatQueue.TryDequeue(out _);
            _combatQueue.Enqueue(new CombatRecord
            {
                CombatOrderSeq = orderSeq,
                AttackerId = snap.AttackerPlayerId,
                DefenderId = snap.DefenderPlayerId,
                AttackerLandId = TryReadLiveUnitLandId(snap.AttackerPlayerId),
                DefenderLandId = TryReadLiveUnitLandId(snap.DefenderPlayerId),
                AttackerHadProblemStudent = atkPs,
                AttackerHadMandarinDuck = atkMd,
                DefenderHadProblemStudent = defPs,
                DefenderHadMandarinDuck = defMd,
                ActiveActorAtEvent = TryReadCurrentActorId(),
                PublishedActorAtEvent = pubTurn.RawActorId,
                PublishedTurnSequenceAtEvent = pubTurn.TurnSequence,
                PublishedRoundAtEvent = pubTurn.AcceptedRound,
                DiceSeqAtEvent = curDiceSeq,
                PlaceSeqAtEvent = curPlaceSeq,
                Round = TryReadCurrentRound(),
                MatchRoomId = matchRoomId,
                MatchStartTime = matchStartTime,
                MatchGeneration = matchGen,
                LayoutGeneration = layoutGen
            });
        }

        private void OnBattleUpdate(Battle b)
        {
            if (!_acceptEvents || b == null) return;
            long atk = b.Attacker != null ? b.Attacker.PlayerId : 0;
            long def = b.Defender != null ? b.Defender.PlayerId : 0;
            if (atk != 0 || def != 0)
            {
                TryStampCurrentMatch(out long matchRoomId, out long matchStartTime);
                int matchGen = Volatile.Read(ref _matchGeneration);
                int layoutGen = Volatile.Read(ref _layoutGeneration);
                var pubTurn = Volatile.Read(ref _publishedTurn);
                int curDiceSeq = CommittedDiceSequence;
                long curPlaceSeq = LatestPlaceSequence;
                long orderSeq = Interlocked.Increment(ref _bossOrderSeq);
                TryReadUnitBossMarks(atk, out bool? atkPs, out bool? atkMd);
                TryReadUnitBossMarks(def, out bool? defPs, out bool? defMd);
                if (_combatQueue.Count >= MaxQueuedEvents) _combatQueue.TryDequeue(out _);
                _combatQueue.Enqueue(new CombatRecord
                {
                    CombatOrderSeq = orderSeq,
                    AttackerId = atk,
                    DefenderId = def,
                    AttackerLandId = TryReadLiveUnitLandId(atk),
                    DefenderLandId = TryReadLiveUnitLandId(def),
                    AttackerHadProblemStudent = atkPs,
                    AttackerHadMandarinDuck = atkMd,
                    DefenderHadProblemStudent = defPs,
                    DefenderHadMandarinDuck = defMd,
                    ActiveActorAtEvent = TryReadCurrentActorId(),
                    PublishedActorAtEvent = pubTurn.RawActorId,
                    PublishedTurnSequenceAtEvent = pubTurn.TurnSequence,
                    PublishedRoundAtEvent = pubTurn.AcceptedRound,
                    DiceSeqAtEvent = curDiceSeq,
                    PlaceSeqAtEvent = curPlaceSeq,
                    Round = TryReadCurrentRound(),
                    MatchRoomId = matchRoomId,
                    MatchStartTime = matchStartTime,
                    MatchGeneration = matchGen,
                    LayoutGeneration = layoutGen
                });
            }
        }

        /// <summary>
        /// Lightweight turn-boundary observer safe to invoke every frame even when overlays hide the preview,
        /// ensuring A -> B -> A transitions or round advances never reuse stale turn flags.
        /// Returns true if a new match was detected.
        /// </summary>
        private void DrainDiceQueue()
        {
            List<DiceRollRecord> batch = null;
            lock (_diceQueueLock)
            {
                if (_diceQueue.Count == 0) return;
                batch = new List<DiceRollRecord>(_diceQueue.Count);
                while (_diceQueue.Count > 0) batch.Add(_diceQueue.Dequeue());
            }

            for (int i = 0; i < batch.Count; i++)
            {
                var dr = batch[i];
                if (!IsMatchingEventStamp(dr.MatchRoomId, dr.MatchStartTime, dr.MatchGeneration)) continue;
                if (dr.OwnerId <= 0 || dr.DiceSeq <= _lastConsumedDiceSeq) continue;
                if (!_pendingDiceByOwner.TryGetValue(dr.OwnerId, out var seqs))
                {
                    seqs = new List<DiceRollRecord>(4);
                    _pendingDiceByOwner[dr.OwnerId] = seqs;
                }
                if (seqs.Count >= 16) seqs.RemoveAt(0);
                // Keep strictly ordered by DiceSeq.
                int pos = seqs.Count;
                while (pos > 0 && seqs[pos - 1].DiceSeq > dr.DiceSeq) pos--;
                seqs.Insert(pos, dr);
            }
        }

        private bool IsEarlierMonsterPriority(long incomingId, long previousId)
        {
            if (incomingId <= 0 || previousId <= 0 || incomingId == previousId) return false;
            if (!_memories.TryGetValue(incomingId, out var curMem) || !_memories.TryGetValue(previousId, out var prevMem))
                return false;
            // Infer a round rollover from priority ONLY when BOTH weights are positively confirmed
            // (never using unknown-last int.MaxValue to fabricate a provisional round!).
            if (!curMem.HasConfirmedActionWeight || !prevMem.HasConfirmedActionWeight ||
                curMem.LastActionWeight <= 0 || prevMem.LastActionWeight <= 0)
            {
                return false;
            }
            if (curMem.LastActionWeight != prevMem.LastActionWeight)
                return curMem.LastActionWeight < prevMem.LastActionWeight;
            return curMem.SpawnSequence > 0 && prevMem.SpawnSequence > 0 && curMem.SpawnSequence < prevMem.SpawnSequence;
        }

        public MatchSyncResult ObserveTurnBoundary(BattleLogic battle)
        {
            return ObserveTurnBoundary(battle, out _, out _);
        }

        public MatchSyncResult ObserveTurnBoundary(BattleLogic battle, out bool sampleRejected, out int sampledRound)
        {
            sampleRejected = false;
            sampledRound = -1;
            var matchSync = EnsureSameMatch();
            // Drain exact target-bound 1040.Place records even while rendering is paused so they
            // remain latched until board validation, tile confirmation, superseding Place, death, or reset.
            DrainPlaceQueue();
            DrainDiceQueue();
            if (battle == null) return matchSync;

            var currentActor = SafeGetCurrentPlayer(battle);
            bool hasCurrentActor = currentActor != null && currentActor.player != null && currentActor.player.Id != 0;
            long curActorId = hasCurrentActor ? currentActor.player.Id : 0;
            int curRound = TryReadCurrentRound();
            sampledRound = curRound;
            long prevRawActorId = _roundTracker.ActiveActorId;

            List<DiceRollRecord> ownedRollSeqs = null;
            int ownedNewRolls = 0;
            if (hasCurrentActor && _pendingDiceByOwner.TryGetValue(curActorId, out var bufferedSeqs) && bufferedSeqs.Count > 0)
            {
                ownedRollSeqs = bufferedSeqs;
                ownedNewRolls = bufferedSeqs.Count;
            }

            bool isEarlierPriority = hasCurrentActor && IsEarlierMonsterPriority(curActorId, prevRawActorId);
            var boundary = _roundTracker.ObserveWithOwnedRolls(
                hasCurrentActor,
                curActorId,
                curRound,
                ownedNewRolls,
                id => _rosterAliveMonsterIds.Contains(id),
                TryIsUnitHero,
                isEarlierPriority);

            if (boundary.SampleRejected)
            {
                // Regressing round sample: preserve independently stamped dice rolls for the next current observation
                // and signal caller to defer turn-scoped reconciliation!
                sampleRejected = true;
                return matchSync;
            }

            // Consume owner roll sequences only after the observation is accepted.
            int latestConsumedOwnerRollSeq = 0;
            if (ownedRollSeqs != null)
            {
                latestConsumedOwnerRollSeq = ownedRollSeqs[ownedRollSeqs.Count - 1].DiceSeq;
                if (latestConsumedOwnerRollSeq > _lastConsumedDiceSeq) _lastConsumedDiceSeq = latestConsumedOwnerRollSeq;
                _pendingDiceByOwner.Remove(curActorId);
            }

            // Expire any stale unconsumed rolls from a superseded outgoing actor occurrence so they
            // cannot fabricate an extra turn the next time that actor becomes active.
            if (prevRawActorId > 0 && prevRawActorId != _roundTracker.ActiveActorId)
            {
                _pendingDiceByOwner.Remove(prevRawActorId);
            }

            if (boundary.BoundaryAdvanced)
            {
                // Record the exact owner-specific roll sequence starting the newest active turn occurrence
                // (never polluted by another actor's later roll before the poll).
                _currentTurnOwnerStartDiceSeq = latestConsumedOwnerRollSeq;
            }
            else if (latestConsumedOwnerRollSeq > 0 && _currentTurnOwnerStartDiceSeq == 0)
            {
                // First roll coalesced into the current turn: record its exact owner sequence.
                _currentTurnOwnerStartDiceSeq = latestConsumedOwnerRollSeq;
            }

            PublishTurnStampIfChanged();

            if (boundary.ShouldResetMovementState)
            {
                _turnStartLayoutInvalidated = false;
                if (hasCurrentActor)
                {
                    int initLand = ReadUnitLandIdVerified(currentActor, out bool initVisual);
                    _activeActorStartLandId = initLand;
                    _activeActorStartFromVisual = initLand >= 0 && initVisual;
                    _activeActorLastSeenLandId = initLand;
                    _activeActorLastSeenFromVisual = initLand >= 0 && initVisual;
                }
                else
                {
                    _activeActorStartLandId = -1;
                    _activeActorStartFromVisual = false;
                    _activeActorLastSeenLandId = -1;
                    _activeActorLastSeenFromVisual = false;
                }
                _activeActorSteppedThisTurn = false;
            }
            else if (hasCurrentActor && curActorId == _roundTracker.EffectiveActiveActorId)
            {
                int nowLand = ReadUnitLandIdVerified(currentActor, out bool nowFromVisual);
                if (nowLand >= 0)
                {
                    if (!_turnStartLayoutInvalidated && _activeActorStartLandId < 0)
                    {
                        _activeActorStartLandId = nowLand;
                        _activeActorStartFromVisual = nowFromVisual;
                    }
                    if (_activeActorLastSeenLandId < 0)
                    {
                        _activeActorLastSeenLandId = nowLand;
                        _activeActorLastSeenFromVisual = nowFromVisual;
                    }
                    else if (_activeActorLastSeenFromVisual && !nowFromVisual)
                    {
                        // Visual model transiently disappeared while protocol NodeId may have jumped ahead:
                        // do NOT set _activeActorSteppedThisTurn from a protocol-only fallback!
                    }
                    else if (!_activeActorLastSeenFromVisual && nowFromVisual)
                    {
                        // First verified visual confirmation after a protocol-only observation:
                        // anchor cleanly to the confirmed visual tile without fabricating a B->A move!
                        if (!_activeActorSteppedThisTurn && !_activeActorStartFromVisual)
                        {
                            _activeActorStartLandId = nowLand;
                            _activeActorStartFromVisual = true;
                            _activeActorLastSeenLandId = nowLand;
                            _activeActorLastSeenFromVisual = true;
                        }
                        else if (nowLand == _activeActorLastSeenLandId)
                        {
                            _activeActorLastSeenFromVisual = true;
                        }
                        else
                        {
                            _activeActorSteppedThisTurn = true;
                            _activeActorLastSeenLandId = nowLand;
                            _activeActorLastSeenFromVisual = true;
                        }
                    }
                    else if (nowLand != _activeActorLastSeenLandId)
                    {
                        _activeActorSteppedThisTurn = true;
                        _activeActorLastSeenLandId = nowLand;
                    }
                }
            }

            return matchSync;
        }

        private void OnSkillUsed(long playerId, int skillId)
        {
            if (!_acceptEvents) return;
            TryStampCurrentMatch(out long matchRoomId, out long matchStartTime);
            int matchGen = Volatile.Read(ref _matchGeneration);
            int layoutGen = Volatile.Read(ref _layoutGeneration);
            if (_skillQueue.Count >= MaxQueuedEvents) _skillQueue.TryDequeue(out _);
            _skillQueue.Enqueue(new SkillCastRecord
            {
                CasterPlayerId = playerId,
                SkillId = skillId,
                MatchRoomId = matchRoomId,
                MatchStartTime = matchStartTime,
                MatchGeneration = matchGen,
                LayoutGeneration = layoutGen,
                TimestampSec = MonotonicSeconds()
            });
        }

        public BoardGraph CaptureBoard(LandManager manager, out bool isComplete)
        {
            var nodes = new List<LandNodeSnapshot>();
            isComplete = false;
            if (manager == null || manager.NodeDict == null || manager.NodeDict.Count == 0)
                return new BoardGraph(nodes);

            bool complete = true;
            var presentNodeIds = new HashSet<int>();
            var activeNodeIds = new HashSet<int>();
            var liveLandObjs = new Dictionary<int, object>(manager.NodeDict.Count);
            foreach (var pair in manager.NodeDict)
            {
                var land = pair.Value;
                if (!UnityObject.IsAlive(land))
                {
                    complete = false;
                    continue;
                }

                bool disabled;
                try
                {
                    disabled = land.Disable;
                }
                catch
                {
                    complete = false;
                    continue;
                }

                Vector3 pos = Vector3.zero;
                try { pos = TransformService.GetPosition(UnityObject.GetTransform(land)); }
                catch
                {
                    if (!disabled)
                    {
                        complete = false;
                        continue;
                    }
                }

                if (disabled)
                {
                    // Disabled placeholder tiles do not require neighbor metadata; record membership only.
                    presentNodeIds.Add(pair.Key);
                    nodes.Add(new LandNodeSnapshot(pair.Key, Array.Empty<int>(), true, pos.x, pos.y, pos.z));
                    continue;
                }

                int[] neighborIds;
                try
                {
                    var nodeInfo = land.GetNodeInfo();
                    if (nodeInfo == null || nodeInfo.neighborLandIds == null)
                    {
                        complete = false;
                        continue;
                    }
                    var raw = nodeInfo.neighborLandIds;
                    neighborIds = new int[raw.Count];
                    for (int i = 0; i < raw.Count; i++) neighborIds[i] = raw[i];
                }
                catch
                {
                    complete = false;
                    continue;
                }

                presentNodeIds.Add(pair.Key);
                activeNodeIds.Add(pair.Key);
                liveLandObjs[pair.Key] = land;
                nodes.Add(new LandNodeSnapshot(pair.Key, neighborIds, false, pos.x, pos.y, pos.z));
            }

            // Validate active-graph closure and reject degenerate graphs with no active-to-active edges:
            // require every active node's non-sentinel neighbor (>= 0) to exist in presentNodeIds,
            // and count ONLY distinct active-to-active edges toward activeEdgeCount.
            int activeEdgeCount = 0;
            if (complete && activeNodeIds.Count >= 2)
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    var node = nodes[i];
                    if (node.Disabled) continue;
                    var nbs = node.NeighborIds;
                    for (int j = 0; j < nbs.Length; j++)
                    {
                        int nid = nbs[j];
                        if (nid >= 0)
                        {
                            if (!presentNodeIds.Contains(nid))
                            {
                                complete = false;
                                break;
                            }
                            if (nid != node.Id && activeNodeIds.Contains(nid)) activeEdgeCount++;
                        }
                    }
                    if (!complete) break;
                }
            }

            isComplete = complete && activeNodeIds.Count >= 2 && activeEdgeCount > 0;
            if (isComplete)
            {
                _currentLayoutLandById.Clear();
                foreach (var kv in liveLandObjs) _currentLayoutLandById[kv.Key] = kv.Value;
            }
            return new BoardGraph(nodes);
        }

        public void SyncAndSnapshot(
            BoardGraph board,
            BattleLogic battle,
            Camera camera,
            PredictorSettings settings,
            out List<LiveMonsterInfo> monsters,
            out List<TrackedPlayer> players,
            out MatchSyncResult matchSync,
            out bool snapshotValid,
            out bool playersAuthoritative,
            out bool playerPositionsReady)
        {
            monsters = new List<LiveMonsterInfo>();
            players = new List<TrackedPlayer>();
            snapshotValid = false;
            playersAuthoritative = false;
            playerPositionsReady = false;

            matchSync = ObserveTurnBoundary(battle, out bool sampleRejected, out _);
            // Return immediately on a match switch, initial match acquisition, or rejected regressing-round sample
            // BEFORE draining queues or interpreting any unit ActionEnd/tile against an inconsistent turn state.
            if (matchSync == MatchSyncResult.Switched ||
                matchSync == MatchSyncResult.InitialAcquired ||
                sampleRejected ||
                board == null ||
                battle == null)
            {
                return;
            }

            var allUnits = Players.All();
            if (allUnits == null || allUnits.Count == 0) return;
            snapshotValid = true;

            DrainEventQueues();

            // Use ONE consistent accepted turn & round observation throughout the snapshot (no second round read!).
            long curActorId = _roundTracker.EffectiveActiveActorId;
            int turnSeq = _roundTracker.TurnSequence;
            int acceptedRound = _roundTracker.LastObservedRound;
            int curLayoutGen = Volatile.Read(ref _layoutGeneration);

            var playerPositions = new Dictionary<long, int>();
            var heroIdToPlayerId = new Dictionary<int, long>();
            var seenPlayerIds = new HashSet<long>();
            var presentUnitIds = new HashSet<long>();
            var rosterAliveIds = new HashSet<long>();
            var explicitDeadMonsterIds = new HashSet<long>();
            bool anyPlayerHasTrueCulprit = false;
            bool rosterAuthoritative = true;
            bool heroesReadable = true;
            bool allLivingHeroPositionsConfirmed = true;
            var allSeenUnitIds = new HashSet<long>();
            var conflictingUnitIds = new HashSet<long>();

            // First pass: detect same-type AND cross-type duplicate IDs across the entire roster before trusting any unit.
            for (int i = 0; i < allUnits.Count; i++)
            {
                var pd = allUnits[i];
                if (pd == null || pd.player == null)
                {
                    rosterAuthoritative = false;
                    heroesReadable = false;
                    continue;
                }
                long uid = pd.player.Id;
                if (uid > 0)
                {
                    presentUnitIds.Add(uid);
                    if (!allSeenUnitIds.Add(uid))
                    {
                        conflictingUnitIds.Add(uid);
                        rosterAuthoritative = false;
                        heroesReadable = false;
                    }
                }
                else
                {
                    rosterAuthoritative = false;
                    heroesReadable = false;
                }
            }

            for (int i = 0; i < allUnits.Count; i++)
            {
                var pd = allUnits[i];
                if (pd == null || pd.player == null) continue;

                long uid = pd.player.Id;
                if (uid <= 0 || conflictingUnitIds.Contains(uid)) continue;

                CharacterType cType = CharacterType.None;
                try { cType = pd.characterType; } catch { rosterAuthoritative = false; heroesReadable = false; continue; }

                if (cType == CharacterType.None)
                {
                    // Unknown/uninitialized characterType on an existing entry invalidates authoritative absence checks.
                    rosterAuthoritative = false;
                    heroesReadable = false;
                    continue;
                }

                if (cType == CharacterType.Monster) continue;

                if (cType == CharacterType.Hero)
                {
                    seenPlayerIds.Add(uid);

                    // Every positive hero sighting immediately clears its departure miss counter,
                    // even if another unit on this frame is temporarily unreadable.
                    _missingHeroAuthoritativeFrames.Remove(uid);

                    var tp = BuildTrackedPlayer(pd, board, settings, out bool isTrueCulprit, out bool livenessConfirmed, out bool posConfirmedOnLayout);
                    if (isTrueCulprit) anyPlayerHasTrueCulprit = true;
                    if (!livenessConfirmed)
                    {
                        heroesReadable = false;
                        rosterAuthoritative = false;
                    }
                    if (tp != null)
                    {
                        players.Add(tp);
                        if (tp.LandId >= 0) playerPositions[tp.PlayerId] = tp.LandId;
                        if (tp.IsAlive && (!posConfirmedOnLayout || !board.Contains(tp.LandId)))
                        {
                            allLivingHeroPositionsConfirmed = false;
                        }
                        try { if (Players.IsSelf(tp.PlayerId)) _localPlayerId = tp.PlayerId; } catch { }
                        int pHeroId = SafeHeroId(pd);
                        if (pHeroId > 0 && tp.IsAlive && !heroIdToPlayerId.ContainsKey(pHeroId))
                            heroIdToPlayerId[pHeroId] = tp.PlayerId;
                    }
                    else
                    {
                        heroesReadable = false;
                        rosterAuthoritative = false;
                    }
                }
            }

            // Retire genuinely departed heroes after 3 consecutive clean full-roster frames (including when
            // the last hero departs). Break any consecutive-miss streak on indeterminate/partial frames.
            if (!heroesReadable)
            {
                _missingHeroAuthoritativeFrames.Clear();
            }
            else if (_knownPlayerIds.Count > 0)
            {
                List<long> departedHeroes = null;
                foreach (long knownPid in _knownPlayerIds)
                {
                    if (seenPlayerIds.Contains(knownPid))
                    {
                        _missingHeroAuthoritativeFrames.Remove(knownPid);
                    }
                    else
                    {
                        _missingHeroAuthoritativeFrames.TryGetValue(knownPid, out int misses);
                        misses++;
                        if (misses >= 3)
                        {
                            if (departedHeroes == null) departedHeroes = new List<long>();
                            departedHeroes.Add(knownPid);
                        }
                        else
                        {
                            _missingHeroAuthoritativeFrames[knownPid] = misses;
                        }
                    }
                }
                if (departedHeroes != null)
                {
                    for (int i = 0; i < departedHeroes.Count; i++)
                    {
                        long dep = departedHeroes[i];
                        _knownPlayerIds.Remove(dep);
                        _missingHeroAuthoritativeFrames.Remove(dep);
                        _lastKnownPlayerAlive.Remove(dep);
                        _lastHeroProtoNodeId.Remove(dep);
                        _preLayoutHeroProtoNodeId.Remove(dep);
                        _heroConfirmedOnCurrentLayout.Remove(dep);
                        _heroProtoConfirmedOnCurrentLayout.Remove(dep);
                        _confirmedHeroFields.Remove(dep);
                    }
                    _knownPlayerCount = _knownPlayerIds.Count;
                }
            }

            bool allKnownPlayersPresent = true;
            foreach (long knownPid in _knownPlayerIds)
            {
                if (!seenPlayerIds.Contains(knownPid))
                {
                    allKnownPlayersPresent = false;
                    break;
                }
            }

            playersAuthoritative = heroesReadable &&
                                   (players.Count > 0 || _knownPlayerCount == 0) &&
                                   players.Count >= _knownPlayerCount &&
                                   allKnownPlayersPresent;

            if (playersAuthoritative)
            {
                foreach (long pid in seenPlayerIds)
                {
                    _knownPlayerIds.Add(pid);
                    _missingHeroAuthoritativeFrames.Remove(pid);
                }
                _knownPlayerCount = _knownPlayerIds.Count;
                _lastAcceptedAnyPlayerHasTrueCulprit = anyPlayerHasTrueCulprit;
                _lastAcceptedHeroIdToPlayerId.Clear();
                foreach (var kv in heroIdToPlayerId) _lastAcceptedHeroIdToPlayerId[kv.Key] = kv.Value;

                playerPositionsReady = allLivingHeroPositionsConfirmed;
                if (playerPositionsReady && _awaitingPostLayoutHeroConfirmation)
                {
                    _awaitingPostLayoutHeroConfirmation = false;
                }
            }
            else
            {
                if (_knownPlayerCount > 0) rosterAuthoritative = false;
                // Preserve any newly confirmed true-culprit sighting on this partial frame while also retaining
                // the previously accepted roster-wide flag and missing heroId->playerId taunt mappings.
                anyPlayerHasTrueCulprit |= _lastAcceptedAnyPlayerHasTrueCulprit;
                if (anyPlayerHasTrueCulprit) _lastAcceptedAnyPlayerHasTrueCulprit = true;
                if (_lastAcceptedHeroIdToPlayerId.Count > 0)
                {
                    foreach (var kv in _lastAcceptedHeroIdToPlayerId)
                    {
                        if (!heroIdToPlayerId.ContainsKey(kv.Key))
                            heroIdToPlayerId[kv.Key] = kv.Value;
                    }
                }
            }

            // Single name lookup per living monster: carry (Name, IsProvisional, SpawnSeq, NameOrd) into the main loop
            // and apply simultaneous provisional->authoritative rebinds in strict SpawnSequence order.
            List<long> newlyDiscoveredIds = null;
            var snapshotNameLookup = new Dictionary<long, (string Name, bool IsProvisional)>(16);
            List<(long Id, int SpawnSeq, string Name)> pendingRebinds = null;
            for (int i = 0; i < allUnits.Count; i++)
            {
                var pd = allUnits[i];
                if (pd == null || pd.player == null) continue;
                CharacterType cType = CharacterType.None;
                try { cType = pd.characterType; } catch { continue; }
                if (cType != CharacterType.Monster) continue;
                long mid = pd.player.Id;
                if (mid <= 0 || conflictingUnitIds.Contains(mid) || snapshotNameLookup.ContainsKey(mid)) continue;
                if (!TryCheckRosterMonsterAlive(pd, out bool alive) || !alive) continue;

                string resolvedName = ResolveMonsterName(pd, SafeHeroId(pd), out bool isProv);
                snapshotNameLookup[mid] = (resolvedName, isProv);

                if (!_memories.TryGetValue(mid, out var existingMem))
                {
                    if (newlyDiscoveredIds == null) newlyDiscoveredIds = new List<long>(4);
                    newlyDiscoveredIds.Add(mid);
                }
                else if ((existingMem.NameBucketIsProvisional && !isProv && !string.Equals(existingMem.NameBucket, resolvedName, StringComparison.Ordinal)) ||
                         (string.Equals(existingMem.NameBucket, "怪物", StringComparison.Ordinal) && !string.Equals(resolvedName, "怪物", StringComparison.Ordinal)))
                {
                    if (pendingRebinds == null) pendingRebinds = new List<(long, int, string)>(4);
                    pendingRebinds.Add((mid, existingMem.SpawnSequence, resolvedName));
                }
            }

            if (pendingRebinds != null && pendingRebinds.Count > 0)
            {
                pendingRebinds.Sort((a, b) =>
                {
                    int cmp = a.SpawnSeq.CompareTo(b.SpawnSeq);
                    return cmp != 0 ? cmp : a.Id.CompareTo(b.Id);
                });
                for (int i = 0; i < pendingRebinds.Count; i++)
                {
                    var rb = pendingRebinds[i];
                    var em = _memories[rb.Id];
                    em.NameBucket = rb.Name;
                    em.NameOrdinal = AllocateNameOrdinal(rb.Name);
                    em.NameBucketIsProvisional = snapshotNameLookup[rb.Id].IsProvisional;
                }
            }

            Dictionary<long, int> batchSpawnSeq = null;
            Dictionary<long, int> batchNameOrd = null;
            if (newlyDiscoveredIds != null && newlyDiscoveredIds.Count > 0)
            {
                MonsterActionOrder.OrderBatchedNewSpawnIds(newlyDiscoveredIds);
                batchSpawnSeq = new Dictionary<long, int>(newlyDiscoveredIds.Count);
                batchNameOrd = new Dictionary<long, int>(newlyDiscoveredIds.Count);
                for (int i = 0; i < newlyDiscoveredIds.Count; i++)
                {
                    long nid = newlyDiscoveredIds[i];
                    batchSpawnSeq[nid] = _nextSpawnSequence++;
                    batchNameOrd[nid] = AllocateNameOrdinal(snapshotNameLookup[nid].Name);
                }
            }

            var logicalMonsters = new List<LiveMonsterInfo>();
            for (int i = 0; i < allUnits.Count; i++)
            {
                var pd = allUnits[i];
                if (pd == null || pd.player == null) continue;

                CharacterType cType = CharacterType.None;
                try { cType = pd.characterType; } catch { continue; }
                if (cType != CharacterType.Monster) continue;

                long mid = pd.player.Id;
                if (mid <= 0 || conflictingUnitIds.Contains(mid))
                {
                    rosterAuthoritative = false;
                    continue;
                }
                if (!TryCheckRosterMonsterAlive(pd, out bool isAlive))
                {
                    rosterAuthoritative = false;
                    continue;
                }
                if (!isAlive)
                {
                    explicitDeadMonsterIds.Add(mid);
                    continue;
                }

                rosterAliveIds.Add(mid);
                _retiredMonsterSec.Remove(mid);

                int rawHeroId = SafeHeroId(pd);
                string resolvedName;
                bool nameIsProvisional;
                if (snapshotNameLookup.TryGetValue(mid, out var nt))
                {
                    resolvedName = nt.Name;
                    nameIsProvisional = nt.IsProvisional;
                }
                else
                {
                    resolvedName = ResolveMonsterName(pd, rawHeroId, out nameIsProvisional);
                }
                bool? serverActionEnd = SafeActionEnd(pd);

                bool isNew = !_memories.TryGetValue(mid, out var mem);
                if (isNew)
                {
                    int nextOrd = (batchNameOrd != null && batchNameOrd.TryGetValue(mid, out int ord))
                        ? ord
                        : AllocateNameOrdinal(resolvedName);
                    int assignedSeq = (batchSpawnSeq != null && batchSpawnSeq.TryGetValue(mid, out int seq))
                        ? seq
                        : _nextSpawnSequence++;
                    mem = new MonsterMemory
                    {
                        SpawnSequence = assignedSeq,
                        NameOrdinal = nextOrd,
                        NameBucket = resolvedName,
                        NameBucketIsProvisional = nameIsProvisional
                    };
                    _memories[mid] = mem;
                    if (serverActionEnd.HasValue)
                    {
                        _roundTracker.SeedInitialServerActionEnd(mid, serverActionEnd.Value, acceptedRound);
                    }
                }
                else
                {
                    mem.MissingAuthoritativeFrames = 0;

                    if (serverActionEnd.HasValue)
                    {
                        _roundTracker.ReconcileServerActionEnd(mid, serverActionEnd.Value, acceptedRound);
                    }
                }

                int heroId = rawHeroId > 0 ? rawHeroId : mem.LastHeroId;
                // Publish the accepted authoritative NameBucket so a resolved name never reverts to a nickname.
                string monsterName = !string.IsNullOrEmpty(mem.NameBucket) ? mem.NameBucket : resolvedName;

                int landId = ReadUnitLandIdVerified(pd, out bool landFromVisual);
                int serverNodeId = SafeRoomPlayerNodeId(pd);
                bool onBoard = board.Contains(landId);
                var chForFacing = pd.CharacterInst;
                bool hasLiveChar = UnityObject.IsAlive(chForFacing);
                bool visualConfirmed = landFromVisual && hasLiveChar;
                int fromLandId = onBoard ? SafeFromLandId(pd) : -1;
                bool? knownThiefOpt = TryReadMonsterIsThief(pd);
                bool? knownAggOpt = TryReadIsNormalAggressive(pd);
                int? knownWeightOpt = TryReadActionWeight(pd);
                bool isThief = knownThiefOpt.HasValue ? knownThiefOpt.Value : (mem.HasConfirmedClassification ? mem.LastIsThief : false);
                bool isNormalAggressive = knownAggOpt.HasValue ? knownAggOpt.Value : (mem.HasConfirmedClassification ? mem.LastIsNormalAggressive : true);
                int actionWeight = (knownWeightOpt.HasValue && knownWeightOpt.Value > 0)
                    ? knownWeightOpt.Value
                    : (mem.HasConfirmedActionWeight ? mem.LastActionWeight : 0);
                bool movedSinceLastSnapshot = false;
                int[] frontIds = Array.Empty<int>();
                double snapNowSec = MonotonicSeconds();
                PruneExpiredHeuristicClaims(mem, snapNowSec, curLayoutGen);

                if (onBoard)
                {
                    if (!mem.HasBeenObservedOnBoard || mem.LastLandId < 0)
                    {
                        bool firstOnBoard = !mem.HasBeenObservedOnBoard;
                        mem.HasBeenObservedOnBoard = true;
                        int[] initialExplicitFront = LiveFacingResolver.TryAcceptInitialProtocolFrontIds(
                            board, landId, serverNodeId, fromLandId, SafeRawProtocolFrontNodeIds(pd));
                        mem.LastLandId = landId;
                        if (visualConfirmed) mem.LastVisualLandId = landId;
                        mem.LastFromLandId = fromLandId;
                        mem.ExplicitFrontNodeIds = initialExplicitFront;
                        mem.ExplicitFrontLandId = initialExplicitFront != null ? landId : -1;
                        mem.ExplicitFrontFromLandId = initialExplicitFront != null ? fromLandId : -1;

                        ExactPlaceLookup initPlace = ExactPlaceLookup.NotFound;
                        if (visualConfirmed)
                        {
                            initPlace = TryConsumeExactPlaceDisplacement(
                                board, mid, mem, fromLandId, landId, fromLandId, serverNodeId,
                                leftPreviousTile: false, isInitialBoardObservation: true, playerPositions, allUnits, out _);
                        }
                        frontIds = ResolveMonsterFrontNodeIds(board, chForFacing, hasLiveChar, landId, fromLandId, mem);
                        if (firstOnBoard && initPlace == ExactPlaceLookup.NotFound)
                        {
                            bool noIncomingEdge = !board.IsValidNeighbor(fromLandId, landId) && !board.IsValidNeighbor(landId, fromLandId);
                            mem.JustSpawnedOnFork = board.IsFork(landId) && serverNodeId == landId && noIncomingEdge;
                        }
                    }
                    else
                    {
                        if (mem.JustFoughtOnFork && mem.CombatTurnSequence != turnSeq)
                        {
                            mem.JustFoughtOnFork = false;
                            mem.CombatForkLandId = -1;
                        }
                        if (mem.StolenTurnSequence != -1 && mem.StolenTurnSequence != turnSeq)
                        {
                            mem.StolenTurnSequence = -1;
                        }
                        if (mem.LaunchedFightTurnSequence != -1 && mem.LaunchedFightTurnSequence != turnSeq)
                        {
                            mem.LaunchedFightTurnSequence = -1;
                        }

                        // Keep protocol and verified visual baselines strictly separated:
                        // - If no prior verified visual tile exists yet, establishing the first visual sample
                        //   anchors LastVisualLandId cleanly without emitting a fabricated B->A move.
                        // - Only verified visual samples advance MoveSequence, consume Places, or run heuristics.
                        if (visualConfirmed && mem.LastVisualLandId < 0)
                        {
                            mem.LastVisualLandId = landId;
                            mem.LastLandId = landId;
                            mem.LastFromLandId = fromLandId;
                            if (_pendingPlaceByMonster.ContainsKey(mid))
                            {
                                TryConsumeExactPlaceDisplacement(
                                    board, mid, mem, fromLandId, landId, fromLandId, serverNodeId,
                                    leftPreviousTile: false, isInitialBoardObservation: true, playerPositions, allUnits, out _);
                            }
                        }
                        else if (visualConfirmed && mem.LastVisualLandId != landId)
                        {
                            movedSinceLastSnapshot = true;
                            int prevLandId = mem.LastVisualLandId;
                            bool confirmedOffTurn = curActorId > 0 && curActorId != mid;
                            bool movedNonAdjacently = prevLandId >= 0 && !board.IsValidNeighbor(prevLandId, landId);

                            mem.JustSpawnedOnFork = false;
                            mem.JustFoughtOnFork = false;
                            mem.CombatForkLandId = -1;
                            mem.SkillMoveKind = SkillMoveKind.None;
                            mem.SkillMoveFromHeuristic = false;
                            mem.HeuristicSkillCast = null;
                            mem.UnmatchedOffTurnMoveSeq = -1;
                            mem.SkillMoveLandId = -1;
                            mem.SkillMovePushDirId = -1;
                            mem.SkillMovePushFromId = -1;
                            mem.SkillMoveCasterPlayerId = 0;
                            mem.SkillMoveTimestampSec = 0;
                            mem.ExplicitFrontNodeIds = null;
                            mem.ExplicitFrontLandId = -1;
                            mem.ExplicitFrontFromLandId = -1;

                            var exactLookup = TryConsumeExactPlaceDisplacement(
                                board, mid, mem, prevLandId, landId, fromLandId, serverNodeId,
                                leftPreviousTile: true, isInitialBoardObservation: false, playerPositions, allUnits, out int consumedTileHops);
                            // Count each actual tile-changing hop once: if no exact Place hop incremented MoveSequence,
                            // advance once for this observed visual tile change.
                            if (consumedTileHops == 0)
                            {
                                mem.MoveSequence++;
                            }
                            if (exactLookup == ExactPlaceLookup.NotFound && (confirmedOffTurn || movedNonAdjacently))
                            {
                                if (!TryTagSkillDisplacementFromWindow(board, mid, mem, prevLandId, landId, playerPositions, allUnits))
                                {
                                    // Retain short-lived unmatched off-turn move so a late SkillUsed arrival can still correlate.
                                    mem.UnmatchedOffTurnMoveSeq = mem.MoveSequence;
                                    mem.UnmatchedOffTurnFromLandId = prevLandId;
                                    mem.UnmatchedOffTurnToLandId = landId;
                                    mem.UnmatchedOffTurnTimestampSec = snapNowSec;
                                }
                            }

                            mem.LastVisualLandId = landId;
                            mem.LastLandId = landId;
                            mem.LastFromLandId = fromLandId;
                        }
                        else if (visualConfirmed)
                        {
                            if (_pendingPlaceByMonster.ContainsKey(mid))
                            {
                                var sameTileLookup = TryConsumeExactPlaceDisplacement(
                                    board, mid, mem, mem.LastVisualLandId, landId, fromLandId, serverNodeId,
                                    leftPreviousTile: false, isInitialBoardObservation: false, playerPositions, allUnits, out _);
                                if (sameTileLookup != ExactPlaceLookup.NotFound)
                                {
                                    mem.UnmatchedOffTurnMoveSeq = -1;
                                }
                                if (sameTileLookup == ExactPlaceLookup.NotFound && mem.LastFromLandId != fromLandId)
                                {
                                    mem.ExplicitFrontNodeIds = null;
                                    mem.ExplicitFrontLandId = -1;
                                    mem.ExplicitFrontFromLandId = -1;
                                }
                                mem.LastFromLandId = fromLandId;
                            }
                            else if (mem.LastFromLandId != fromLandId)
                            {
                                mem.ExplicitFrontNodeIds = null;
                                mem.ExplicitFrontLandId = -1;
                                mem.ExplicitFrontFromLandId = -1;
                                mem.LastFromLandId = fromLandId;
                            }

                            // Re-evaluate a short-lived unmatched off-turn move when a late SkillUsed cast arrives.
                            if (mem.UnmatchedOffTurnMoveSeq == mem.MoveSequence &&
                                mem.UnmatchedOffTurnToLandId == landId &&
                                mem.SkillMoveKind == SkillMoveKind.None)
                            {
                                if ((snapNowSec - mem.UnmatchedOffTurnTimestampSec) > 2.5)
                                {
                                    mem.UnmatchedOffTurnMoveSeq = -1;
                                }
                                else if (_recentSkills.Count > 0 &&
                                         TryTagSkillDisplacementFromWindow(board, mid, mem, mem.UnmatchedOffTurnFromLandId, landId, playerPositions, allUnits))
                                {
                                    mem.UnmatchedOffTurnMoveSeq = -1;
                                }
                            }
                        }

                        frontIds = ResolveMonsterFrontNodeIds(board, chForFacing, hasLiveChar, landId, fromLandId, mem);
                    }
                }

                bool usesBossTarget = (heroId == 1018 || heroId == 1047);
                long preUpdateEffectiveBossTargetId = mem.EffectiveBossTargetId;
                long? serverBossTargetOpt = usesBossTarget ? TryReadBossTargetId(pd) : null;

                if (usesBossTarget)
                {
                    // Promote the latest authoritative server BossTargetId transition (including explicit 0!)
                    // captured while the previous turn was locally suppressed.
                    if (mem.HasPendingServerBossTarget && mem.BossTargetClearedTurnSequence != turnSeq)
                    {
                        mem.EffectiveServerBossTargetId = mem.PendingServerBossTargetId;
                        mem.BossTargetAssignedTurnSeq = turnSeq;
                        mem.BossTargetAssignedOrderSeq = mem.PendingServerBossTargetOrderSeq > 0
                            ? mem.PendingServerBossTargetOrderSeq
                            : Interlocked.Increment(ref _bossOrderSeq);
                        mem.BossTargetAssignmentVersion++;
                        if (mem.EffectiveServerBossTargetId != 0) mem.InferredBossTargetId = 0;
                        mem.PendingServerBossTargetId = 0;
                        mem.PendingServerBossTargetOrderSeq = 0;
                        mem.HasPendingServerBossTarget = false;
                    }

                    if (serverBossTargetOpt.HasValue)
                    {
                        long sv = serverBossTargetOpt.Value;
                        // Compare against the raw server observation (NEVER the locally zeroed effective target)
                        // so an unchanged server value cannot restore a cleared target, while any real transition
                        // (including P2 -> 0) replaces a pending replacement cleanly!
                        bool hadLastRaw = mem.HasLastRawServerBossTarget;
                        bool serverTransitioned = !hadLastRaw || sv != mem.LastRawServerBossTargetId;
                        mem.HasLastRawServerBossTarget = true;
                        mem.LastRawServerBossTargetId = sv;

                        if (serverTransitioned)
                        {
                            if (mem.BossTargetClearedTurnSequence != turnSeq)
                            {
                                mem.EffectiveServerBossTargetId = sv;
                                mem.BossTargetAssignedTurnSeq = turnSeq;
                                mem.BossTargetAssignedOrderSeq = hadLastRaw ? Interlocked.Increment(ref _bossOrderSeq) : 0L;
                                mem.BossTargetAssignmentVersion++;
                                if (sv != 0) mem.InferredBossTargetId = 0;
                                mem.PendingServerBossTargetId = 0;
                                mem.PendingServerBossTargetOrderSeq = 0;
                                mem.HasPendingServerBossTarget = false;
                            }
                            else
                            {
                                mem.PendingServerBossTargetId = sv;
                                mem.PendingServerBossTargetOrderSeq = Interlocked.Increment(ref _bossOrderSeq);
                                mem.HasPendingServerBossTarget = true;
                            }
                        }
                    }
                }

                // Evaluate retained per-monster combat records using tri-state turn matching (Pending/Matched/Superseded),
                // idempotent assignment-versioned boss-lock clearance, layout checks, and movement-occurrence validation.
                if (_pendingCombatByMonster.TryGetValue(mid, out var combatList))
                {
                    for (int ci = 0; ci < combatList.Count; )
                    {
                        var combatEvt = combatList[ci];
                        var turnMatch = MatchEventTurn(
                            curActorId,
                            mid,
                            turnSeq,
                            combatEvt.ActiveActor,
                            combatEvt.PublishedActorAtEvent,
                            combatEvt.PublishedTurnSequenceAtEvent,
                            combatEvt.PublishedRoundAtEvent,
                            combatEvt.DiceSeqAtEvent,
                            combatEvt.Round,
                            ref combatEvt.BoundTurnSequence);
                        combatList[ci] = combatEvt;

                        if (turnMatch == EventTurnMatch.Pending)
                        {
                            ci++;
                            continue;
                        }

                        // Record confirmed monster-initiated ordinary attacks at the turn level even on non-fork tiles!
                        if (turnMatch == EventTurnMatch.Matched && combatEvt.IsAttacker)
                        {
                            mem.LaunchedFightTurnSequence = turnSeq;
                        }

                        int fightTurnSeq = combatEvt.BoundTurnSequence >= 0 ? combatEvt.BoundTurnSequence : combatEvt.PublishedTurnSequenceAtEvent;
                        bool recentOwnTurnBossFight = usesBossTarget &&
                                                      !combatEvt.BossLockEvaluated &&
                                                      combatEvt.OpponentId != 0 &&
                                                      (turnMatch == EventTurnMatch.Matched ||
                                                       (combatEvt.ActiveActor == mid && fightTurnSeq >= turnSeq - 1));

                        if (recentOwnTurnBossFight)
                        {
                            bool? oppMarkAtFight = heroId == 1018
                                ? combatEvt.OpponentHadProblemStudent
                                : combatEvt.OpponentHadMandarinDuck;
                            bool matchesLockedId =
                                (preUpdateEffectiveBossTargetId != 0 && combatEvt.OpponentId == preUpdateEffectiveBossTargetId) ||
                                (mem.EffectiveBossTargetId != 0 && combatEvt.OpponentId == mem.EffectiveBossTargetId);

                            // Defer unresolved clearance when fight-time mark evidence was unavailable and opponent
                            // does not match the current locked ID (never substituting a later-applied current-frame mark).
                            if (matchesLockedId || oppMarkAtFight.HasValue || turnMatch != EventTurnMatch.Matched)
                            {
                                combatEvt.BossLockEvaluated = true;
                                combatList[ci] = combatEvt;

                                // Deduplicate interleaved/repeated FightResolved + BattleUpdate deliveries by fight + assignment version.
                                bool isDuplicateClear = false;
                                for (int ri = 0; ri < mem.RecentClearedBossFights.Count; ri++)
                                {
                                    var rc = mem.RecentClearedBossFights[ri];
                                    if (rc.OpponentId == combatEvt.OpponentId &&
                                        rc.TurnSeq == fightTurnSeq &&
                                        rc.Version == mem.BossTargetAssignmentVersion)
                                    {
                                        isDuplicateClear = true;
                                        break;
                                    }
                                }

                                bool matchedBossTarget = !isDuplicateClear && (matchesLockedId || oppMarkAtFight == true);
                                if (matchedBossTarget)
                                {
                                    if (mem.RecentClearedBossFights.Count >= 8) mem.RecentClearedBossFights.RemoveAt(0);
                                    mem.RecentClearedBossFights.Add((combatEvt.OpponentId, fightTurnSeq, mem.BossTargetAssignmentVersion));

                                    // Protect ANY newer server, pending, or inferred assignment (including same-turn, same-ID reacquisition!).
                                    bool assignmentIsNewerThanFight =
                                        mem.EffectiveBossTargetId != 0 &&
                                        (mem.BossTargetAssignedTurnSeq > fightTurnSeq ||
                                         (combatEvt.CombatOrderSeq > 0 && mem.BossTargetAssignedOrderSeq > combatEvt.CombatOrderSeq));
                                    bool pendingIsNewerThanFight =
                                        combatEvt.CombatOrderSeq > 0 &&
                                        mem.PendingServerBossTargetOrderSeq > combatEvt.CombatOrderSeq;

                                    if (mem.HasPendingServerBossTarget &&
                                        !pendingIsNewerThanFight &&
                                        mem.PendingServerBossTargetId == combatEvt.OpponentId &&
                                        turnMatch == EventTurnMatch.Matched)
                                    {
                                        mem.PendingServerBossTargetId = 0;
                                        mem.PendingServerBossTargetOrderSeq = 0;
                                        mem.HasPendingServerBossTarget = false;
                                    }

                                    if (mem.InferredBossTargetId == combatEvt.OpponentId && !assignmentIsNewerThanFight)
                                    {
                                        mem.InferredBossTargetId = 0;
                                    }

                                    if (!assignmentIsNewerThanFight)
                                    {
                                        if (mem.EffectiveServerBossTargetId == combatEvt.OpponentId ||
                                            !mem.HasLastRawServerBossTarget ||
                                            mem.EffectiveServerBossTargetId == preUpdateEffectiveBossTargetId)
                                        {
                                            mem.EffectiveServerBossTargetId = 0;
                                        }
                                        mem.InferredBossTargetId = 0;
                                        if (turnMatch == EventTurnMatch.Matched)
                                        {
                                            mem.BossTargetClearedTurnSequence = turnSeq;
                                        }
                                    }
                                }
                            }
                        }

                        if (turnMatch == EventTurnMatch.Superseded)
                        {
                            combatList.RemoveAt(ci);
                            continue;
                        }

                        if (combatEvt.LayoutGeneration != curLayoutGen)
                        {
                            combatList.RemoveAt(ci);
                            continue;
                        }

                        if (!onBoard || !visualConfirmed)
                        {
                            ci++;
                            continue;
                        }

                        // Validate movement-occurrence and Place watermarks BEFORE accepting tile equality
                        // (0 is a valid initial PlaceSeq watermark; < 0 is unknown) so revisits to the same fork
                        // can never attach a stale combat marker.
                        bool movedPastCombat =
                            (combatEvt.PlaceSeqAtEvent >= 0 && mem.LastAppliedPlaceSeq > combatEvt.PlaceSeqAtEvent) ||
                            (combatEvt.LandId >= 0 && combatEvt.CreatedMoveSeq >= 0 &&
                             ((mem.MoveSequence > combatEvt.CreatedMoveSeq && combatEvt.CreatedLandId == combatEvt.LandId) ||
                              (mem.MoveSequence - combatEvt.CreatedMoveSeq) > 1)) ||
                            (combatEvt.LandId < 0 &&
                             (isNew || movedSinceLastSnapshot || (combatEvt.CreatedMoveSeq >= 0 && mem.MoveSequence > combatEvt.CreatedMoveSeq)));
                        if (movedPastCombat)
                        {
                            combatList.RemoveAt(ci);
                            continue;
                        }

                        int actualCombatTile = combatEvt.LandId >= 0 ? combatEvt.LandId : landId;
                        if (actualCombatTile == landId)
                        {
                            if (board.IsForkForContext(landId, fromLandId, arrivedFromIncomingEdge: true))
                            {
                                mem.JustFoughtOnFork = true;
                                mem.CombatForkLandId = landId;
                                mem.CombatTurnSequence = turnSeq;
                            }
                            combatList.RemoveAt(ci);
                            continue;
                        }

                        ci++;
                    }
                    if (combatList.Count == 0) _pendingCombatByMonster.Remove(mid);
                }

                // Infer nearest marked target ONLY after combat clearance, ONLY when no authoritative server target
                // exists, and ONLY commit the per-turn attempt flag once playerPositionsReady AND a valid turn-start
                // anchor on the current board exist (retrying incomplete attempts).
                if (usesBossTarget &&
                    onBoard &&
                    curActorId == mid &&
                    mem.EffectiveBossTargetId == 0 &&
                    mem.BossTargetLockTurnSequence != turnSeq &&
                    mem.BossTargetClearedTurnSequence != turnSeq &&
                    playersAuthoritative &&
                    playerPositionsReady &&
                    _activeActorStartLandId >= 0 &&
                    board.Contains(_activeActorStartLandId))
                {
                    mem.BossTargetLockTurnSequence = turnSeq;
                    long lockedMarkedId = FindNearestMarkedPlayerId(board, heroId, _activeActorStartLandId, players);
                    if (lockedMarkedId != 0)
                    {
                        mem.InferredBossTargetId = lockedMarkedId;
                        mem.BossTargetAssignedTurnSeq = turnSeq;
                        mem.BossTargetAssignedOrderSeq = Interlocked.Increment(ref _bossOrderSeq);
                        mem.BossTargetAssignmentVersion++;
                    }
                }

                // Process retained per-monster thief steals independently of combat with deferred classification.
                if (_pendingStealByMonster.TryGetValue(mid, out var stealList))
                {
                    for (int si = 0; si < stealList.Count; )
                    {
                        var stealEvt = stealList[si];
                        // If definitively NOT a thief (or victim definitively not a hero), discard.
                        if (knownThiefOpt == false || (stealEvt.VictimId > 0 && _memories.ContainsKey(stealEvt.VictimId)))
                        {
                            stealList.RemoveAt(si);
                            continue;
                        }
                        if (knownThiefOpt == null)
                        {
                            si++;
                            continue;
                        }
                        if (stealEvt.VictimId > 0 && !seenPlayerIds.Contains(stealEvt.VictimId) && !_knownPlayerIds.Contains(stealEvt.VictimId) && !TryIsUnitHero(stealEvt.VictimId))
                        {
                            // Victim classification temporarily unknown unless confirmed non-hero; retain briefly if pending.
                            if (playersAuthoritative)
                            {
                                stealList.RemoveAt(si);
                                continue;
                            }
                            si++;
                            continue;
                        }

                        var stealMatch = MatchEventTurn(
                            curActorId,
                            mid,
                            turnSeq,
                            stealEvt.ActiveActor,
                            stealEvt.PublishedActorAtEvent,
                            stealEvt.PublishedTurnSequenceAtEvent,
                            stealEvt.PublishedRoundAtEvent,
                            stealEvt.DiceSeqAtEvent,
                            stealEvt.Round,
                            ref stealEvt.BoundTurnSequence);
                        stealList[si] = stealEvt;

                        if (stealMatch == EventTurnMatch.Pending)
                        {
                            si++;
                            continue;
                        }
                        if (stealMatch == EventTurnMatch.Superseded)
                        {
                            stealList.RemoveAt(si);
                            continue;
                        }

                        // Turn-level steal flag applies immediately regardless of layout or visual lag.
                        mem.StolenTurnSequence = turnSeq;

                        if (stealEvt.LayoutGeneration != curLayoutGen)
                        {
                            stealList.RemoveAt(si);
                            continue;
                        }

                        if (!onBoard || !visualConfirmed)
                        {
                            si++;
                            continue;
                        }

                        // Validate movement-occurrence and Place watermarks BEFORE accepting tile equality
                        // (0 is a valid initial PlaceSeq watermark; < 0 is unknown).
                        bool movedPastSteal =
                            (stealEvt.PlaceSeqAtEvent >= 0 && mem.LastAppliedPlaceSeq > stealEvt.PlaceSeqAtEvent) ||
                            (stealEvt.LandId >= 0 && stealEvt.CreatedMoveSeq >= 0 &&
                             ((mem.MoveSequence > stealEvt.CreatedMoveSeq && stealEvt.CreatedLandId == stealEvt.LandId) ||
                              (mem.MoveSequence - stealEvt.CreatedMoveSeq) > 1)) ||
                            (stealEvt.LandId < 0 &&
                             (isNew || movedSinceLastSnapshot || (stealEvt.CreatedMoveSeq >= 0 && mem.MoveSequence > stealEvt.CreatedMoveSeq)));
                        if (movedPastSteal)
                        {
                            stealList.RemoveAt(si);
                            continue;
                        }

                        int actualStealTile = stealEvt.LandId >= 0 ? stealEvt.LandId : landId;
                        if (actualStealTile == landId)
                        {
                            if (board.IsForkForContext(landId, fromLandId, arrivedFromIncomingEdge: true))
                            {
                                mem.JustFoughtOnFork = true;
                                mem.CombatForkLandId = landId;
                                mem.CombatTurnSequence = turnSeq;
                            }
                            stealList.RemoveAt(si);
                            continue;
                        }

                        si++;
                    }
                    if (stealList.Count == 0) _pendingStealByMonster.Remove(mid);
                }

                var ch = chForFacing;
                int canStep = hasLiveChar ? SafeCanStep(ch) : 0;

                bool justFought = onBoard &&
                                  (curActorId == mid) &&
                                  mem.JustFoughtOnFork &&
                                  mem.CombatTurnSequence == turnSeq &&
                                  mem.CombatForkLandId == landId &&
                                  board.IsForkForContext(landId, fromLandId, arrivedFromIncomingEdge: true);
                bool isMovingMidTurn = onBoard && (curActorId == mid) && _activeActorSteppedThisTurn && !justFought;
                bool launchedFightThisTurn = (curActorId == mid) &&
                                             (SafeIsLaunchFight(pd) ||
                                              mem.LaunchedFightTurnSequence == turnSeq ||
                                              (isThief && mem.StolenTurnSequence == turnSeq));
                bool hasStartedMovingThisTurn = (curActorId == mid) &&
                                                (_activeActorSteppedThisTurn || mem.JustFoughtOnFork || launchedFightThisTurn);
                bool bossLockInferenceReady = (curActorId != mid) ||
                                              (playersAuthoritative &&
                                               playerPositionsReady &&
                                               _activeActorStartLandId >= 0 &&
                                               board.Contains(_activeActorStartLandId));

                bool isTaunted = TryReadTauntTarget(pd, heroIdToPlayerId, out long tauntTargetId);
                bool requiresCrimeTarget = IsCrimeRestrictedMonster(pd, heroId, anyPlayerHasTrueCulprit);
                SafeHp(pd, out int hp, out int maxHp, out bool hasHp, out _);
                bool bossTargetClearedThisTurn = (curActorId == mid) && (mem.BossTargetClearedTurnSequence == turnSeq);

                if (knownWeightOpt.HasValue && knownWeightOpt.Value > 0)
                {
                    mem.LastActionWeight = actionWeight;
                    mem.HasConfirmedActionWeight = true;
                }
                if (heroId > 0) mem.LastHeroId = heroId;
                if (knownAggOpt.HasValue || knownThiefOpt.HasValue)
                {
                    if (knownAggOpt.HasValue) mem.LastIsNormalAggressive = isNormalAggressive;
                    if (knownThiefOpt.HasValue) mem.LastIsThief = isThief;
                    mem.HasConfirmedClassification = true;
                }
                mem.LastHp = hp;
                mem.LastMaxHp = maxHp;
                mem.LastHasHp = hasHp;

                bool isCurrentlyActing = (curActorId == mid);
                if (isCurrentlyActing) _roundTracker.ClearActedThisRound(mid);
                bool hasActedThisRound = !isCurrentlyActing && _roundTracker.HasActedThisRound(mid);

                var state = new MonsterState
                {
                    MonsterId = mid,
                    Name = monsterName,
                    DisplayName = monsterName,
                    HeroId = heroId,
                    Hp = hp,
                    MaxHp = maxHp,
                    HasHp = hasHp,
                    IsNormalAggressive = isNormalAggressive,
                    IsThief = isThief,
                    ActionWeight = actionWeight,
                    SpawnSequence = mem.SpawnSequence,
                    NameOrdinal = mem.NameOrdinal,
                    NameGroupDisambiguated = _disambiguatedNames.Contains(mem.NameBucket),
                    IsCurrentlyActing = isCurrentlyActing,
                    HasActedThisRound = hasActedThisRound,
                    CurrentLandId = onBoard ? landId : -1,
                    FromLandId = fromLandId,
                    FrontNodeIds = frontIds,
                    CanStep = canStep,
                    IsCurrentlyMovingMidTurn = isMovingMidTurn,
                    HasStartedMovingThisTurn = hasStartedMovingThisTurn,
                    BossLockInferenceReady = bossLockInferenceReady,
                    TurnStartLandId = (curActorId == mid) ? _activeActorStartLandId : (onBoard ? landId : -1),
                    JustFoughtOnCurrentFork = justFought,
                    HasLaunchedFightThisTurn = launchedFightThisTurn,
                    JustSpawnedOnFork = onBoard && mem.JustSpawnedOnFork && board.IsFork(landId),
                    IsTaunted = isTaunted,
                    TauntTargetPlayerId = tauntTargetId,
                    RequiresCrimeTarget = requiresCrimeTarget,
                    ChasesProblemStudent = (heroId == 1018),
                    ChasesMandarinDuckMark = (heroId == 1047),
                    BossTargetPlayerId = bossTargetClearedThisTurn ? 0L : (usesBossTarget ? mem.EffectiveBossTargetId : 0L),
                    BossTargetClearedThisTurn = bossTargetClearedThisTurn,
                    SkillMoveKind = (onBoard && mem.SkillMoveLandId == landId) ? mem.SkillMoveKind : SkillMoveKind.None,
                    SkillMovePushDirectionLandId = (onBoard && mem.SkillMoveLandId == landId) ? mem.SkillMovePushDirId : -1,
                    SkillMovePushFromLandId = (onBoard && mem.SkillMoveLandId == landId) ? mem.SkillMovePushFromId : -1
                };

                Vector3 feetPos = Vector3.zero;
                MonsterVisualBounds visualBounds = default;
                bool canRender = onBoard && visualConfirmed;
                if (canRender)
                {
                    if (board.TryGetNode(landId, out var landNode))
                        feetPos = new Vector3(landNode.X, landNode.Y, landNode.Z);
                    else
                        feetPos = TransformService.GetPosition(UnityObject.GetTransform(ch));
                    visualBounds = SpriteBoundsReader.Capture(ch, feetPos, camera);
                }

                logicalMonsters.Add(new LiveMonsterInfo
                {
                    State = state,
                    CharacterRef = canRender ? ch : null,
                    FeetWorldPos = feetPos,
                    VisualBounds = visualBounds
                });
            }

            // Sort players by seat slot ASC (P1-P4) so virtual-move UI is always cleanly ordered.
            players.Sort((a, b) =>
            {
                int slotCmp = a.Slot.CompareTo(b.Slot);
                return slotCmp != 0 ? slotCmp : a.PlayerId.CompareTo(b.PlayerId);
            });

            double nowSec = MonotonicSeconds();
            // Explicitly dead monsters are retired immediately even if the rest of the frame was partial.
            foreach (long deadId in explicitDeadMonsterIds)
            {
                RetireMonster(deadId, nowSec);
            }

            // Reset absence counters for any known monster ID positively present in allUnits even if
            // its CharacterType or HP was temporarily unreadable on this frame.
            foreach (var kv in _memories)
            {
                if (presentUnitIds.Contains(kv.Key))
                {
                    kv.Value.MissingAuthoritativeFrames = 0;
                }
            }

            if (rosterAuthoritative)
            {
                List<long> stale = null;
                foreach (var kv in _memories)
                {
                    if (!rosterAliveIds.Contains(kv.Key) && !presentUnitIds.Contains(kv.Key))
                    {
                        kv.Value.MissingAuthoritativeFrames++;
                        if (kv.Value.MissingAuthoritativeFrames >= 2)
                        {
                            if (stale == null) stale = new List<long>();
                            stale.Add(kv.Key);
                        }
                    }
                }
                if (stale != null)
                {
                    for (int i = 0; i < stale.Count; i++)
                    {
                        RetireMonster(stale[i], nowSec);
                    }
                }

                _rosterAliveMonsterIds.Clear();
                foreach (long id in rosterAliveIds) _rosterAliveMonsterIds.Add(id);
                foreach (var kv in _memories) _rosterAliveMonsterIds.Add(kv.Key);

                PruneHeroPendingKeys(_pendingPlaceByMonster, seenPlayerIds);
                PruneHeroPendingKeys(_pendingCombatByMonster, seenPlayerIds);
                PruneHeroPendingKeys(_pendingStealByMonster, seenPlayerIds);
            }
            else
            {
                foreach (long id in rosterAliveIds) _rosterAliveMonsterIds.Add(id);
            }

            // Include unrendered logical placeholders for still-alive monsters in the 1-frame absence confirmation
            // window (or temporarily unreadable HP/type) so remaining monsters' action ranks never jump #2 -> #1 -> #2!
            foreach (var kv in _memories)
            {
                long mid = kv.Key;
                if (rosterAliveIds.Contains(mid)) continue;
                var m = kv.Value;
                bool isActing = (curActorId == mid);
                logicalMonsters.Add(new LiveMonsterInfo
                {
                    State = new MonsterState
                    {
                        MonsterId = mid,
                        Name = m.NameBucket,
                        DisplayName = m.NameBucket,
                        HeroId = m.LastHeroId,
                        Hp = m.LastHp,
                        MaxHp = m.LastMaxHp,
                        HasHp = m.LastHasHp,
                        IsNormalAggressive = m.LastIsNormalAggressive,
                        IsThief = m.LastIsThief,
                        ActionWeight = m.LastActionWeight,
                        SpawnSequence = m.SpawnSequence,
                        NameOrdinal = m.NameOrdinal,
                        NameGroupDisambiguated = _disambiguatedNames.Contains(m.NameBucket),
                        IsCurrentlyActing = isActing,
                        HasActedThisRound = !isActing && _roundTracker.HasActedThisRound(mid),
                        CurrentLandId = -1
                    },
                    CharacterRef = null
                });
            }

            // Compute action ranks & persistent display names across the FULL logical living monster roster,
            // then expose only rendered on-board monsters so a momentary model hiccup never flips ranks or suffixes.
            MonsterActionOrder.SortAndAssign(logicalMonsters, m => m.State);
            for (int i = 0; i < logicalMonsters.Count; i++)
            {
                var lm = logicalMonsters[i];
                if (lm.CharacterRef != null && lm.State.CurrentLandId >= 0)
                {
                    monsters.Add(lm);
                }
            }
        }

        private void RetireMonster(long monsterId, double nowSec)
        {
            _memories.Remove(monsterId);
            _rosterAliveMonsterIds.Remove(monsterId);
            _pendingPlaceByMonster.Remove(monsterId);
            _pendingCombatByMonster.Remove(monsterId);
            _pendingStealByMonster.Remove(monsterId);
            _roundTracker.RemoveDeadMonster(monsterId);
            _retiredMonsterSec[monsterId] = nowSec;
        }

        private static void PruneHeroPendingKeys<T>(Dictionary<long, List<T>> map, HashSet<long> heroIds)
        {
            if (map.Count == 0 || heroIds.Count == 0) return;
            List<long> toRemove = null;
            foreach (long k in map.Keys)
            {
                if (heroIds.Contains(k))
                {
                    if (toRemove == null) toRemove = new List<long>();
                    toRemove.Add(k);
                }
            }
            if (toRemove != null)
            {
                for (int i = 0; i < toRemove.Count; i++) map.Remove(toRemove[i]);
            }
        }

        private int AllocateNameOrdinal(string name)
        {
            string key = name ?? string.Empty;
            _nextNameOrdinalByName.TryGetValue(key, out int nextOrd);
            nextOrd++;
            _nextNameOrdinalByName[key] = nextOrd;
            if (nextOrd >= 2) _disambiguatedNames.Add(key);
            return nextOrd;
        }

        private bool IsMatchingEventStamp(long eventRoomId, long eventStartTime, int eventMatchGeneration)
        {
            if (_matchRoomId == 0 && _matchStartTime == 0)
                return eventMatchGeneration == Volatile.Read(ref _matchGeneration);
            if (eventRoomId != 0 || eventStartTime != 0)
                return eventRoomId == _matchRoomId && eventStartTime == _matchStartTime;
            // Failed-stamp (0,0) events must belong to the current match generation and cannot cross a confirmed match switch.
            return eventMatchGeneration == Volatile.Read(ref _matchGeneration);
        }

        private void ResetForNewMatch(long newRoomId, long newStartTime, bool confirmedMatchSwitch)
        {
            _memories.Clear();
            _nextNameOrdinalByName.Clear();
            _disambiguatedNames.Clear();
            _rosterAliveMonsterIds.Clear();
            _knownPlayerIds.Clear();
            _missingHeroAuthoritativeFrames.Clear();
            _lastKnownPlayerAlive.Clear();
            _lastHeroProtoNodeId.Clear();
            _preLayoutHeroProtoNodeId.Clear();
            _heroConfirmedOnCurrentLayout.Clear();
            _heroProtoConfirmedOnCurrentLayout.Clear();
            _confirmedHeroFields.Clear();
            _currentLayoutLandById.Clear();
            _awaitingPostLayoutHeroConfirmation = false;
            _retiredMonsterSec.Clear();
            _recentExactDisplacements.Clear();
            _lastAcceptedHeroIdToPlayerId.Clear();
            _lastAcceptedAnyPlayerHasTrueCulprit = false;
            _knownPlayerCount = 0;
            _nextSpawnSequence = 1;
            _localPlayerId = 0;
            _activeActorStartLandId = -1;
            _activeActorStartFromVisual = false;
            _activeActorLastSeenLandId = -1;
            _activeActorLastSeenFromVisual = false;
            _turnStartLayoutInvalidated = false;
            _activeActorSteppedThisTurn = false;
            _currentTurnOwnerStartDiceSeq = 0;
            _roundTracker.Reset(0);
            PublishTurnStampIfChanged();

            int prevMatchGen = Volatile.Read(ref _matchGeneration);
            _matchRoomId = newRoomId;
            _matchStartTime = newStartTime;
            int newMatchGen = confirmedMatchSwitch ? Interlocked.Increment(ref _matchGeneration) : prevMatchGen;
            bool hasNewIdentity = newRoomId != 0 || newStartTime != 0;

            bool IsIncomingMatchRecord(long rRoom, long rStart, int rGen)
            {
                if (hasNewIdentity && (rRoom != 0 || rStart != 0))
                    return rRoom == newRoomId && rStart == newStartTime;
                return !confirmedMatchSwitch && rGen == prevMatchGen;
            }

            // Atomically drain queued Places and advance layout epoch to an initially unvalidated layout
            // BEFORE deciding watermarks so incoming-match Places are adopted instead of discarded!
            List<SkillDisplacementRecord> drainedPlaces = null;
            int newLayoutGen;
            long latestSeqSnapshot;
            lock (_placeQueueLock)
            {
                if (_placeQueue.Count > 0)
                {
                    drainedPlaces = new List<SkillDisplacementRecord>(_placeQueue.Count);
                    while (_placeQueue.Count > 0) drainedPlaces.Add(_placeQueue.Dequeue());
                }
                latestSeqSnapshot = _nextPlaceSeq;
                _lastValidatedBoard = null;
                _previousBoardBeforeTopologyChange = null;
                newLayoutGen = Interlocked.Increment(ref _layoutGeneration);
            }
            Interlocked.Increment(ref _boardScanRevision);

            long minAdoptedPlaceSeq = long.MaxValue;
            // Partition existing main-thread pending Places by incoming match identity.
            List<long> emptyMonsters = null;
            foreach (var kv in _pendingPlaceByMonster)
            {
                var list = kv.Value;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var rec = list[i];
                    if (IsIncomingMatchRecord(rec.MatchRoomId, rec.MatchStartTime, rec.MatchGeneration))
                    {
                        rec.MatchGeneration = newMatchGen;
                        rec.LayoutGeneration = newLayoutGen;
                        rec.ValidatedForCurrentLayout = false;
                        rec.ObservedIncompatibleWithPreviousBoard = false;
                        rec.LastCheckedBoardRevision = 0;
                        if (rec.PlaceSeq < minAdoptedPlaceSeq) minAdoptedPlaceSeq = rec.PlaceSeq;
                    }
                    else
                    {
                        list.RemoveAt(i);
                    }
                }
                if (list.Count == 0)
                {
                    if (emptyMonsters == null) emptyMonsters = new List<long>();
                    emptyMonsters.Add(kv.Key);
                }
            }
            if (emptyMonsters != null)
            {
                for (int i = 0; i < emptyMonsters.Count; i++) _pendingPlaceByMonster.Remove(emptyMonsters[i]);
            }

            // Adopt positively identified incoming-match records from the drained Place queue.
            if (drainedPlaces != null)
            {
                const int MaxPendingPlacesPerMonster = 16;
                for (int i = 0; i < drainedPlaces.Count; i++)
                {
                    var pl = drainedPlaces[i];
                    if (pl == null || pl.TargetMonsterId == 0 || pl.NewLandId < 0) continue;
                    if (!IsIncomingMatchRecord(pl.MatchRoomId, pl.MatchStartTime, pl.MatchGeneration)) continue;
                    pl.MatchGeneration = newMatchGen;
                    pl.LayoutGeneration = newLayoutGen;
                    pl.ValidatedForCurrentLayout = false;
                    pl.ObservedIncompatibleWithPreviousBoard = false;
                    pl.LastCheckedBoardRevision = 0;
                    if (pl.PlaceSeq < minAdoptedPlaceSeq) minAdoptedPlaceSeq = pl.PlaceSeq;
                    if (!_pendingPlaceByMonster.TryGetValue(pl.TargetMonsterId, out var list))
                    {
                        list = new List<SkillDisplacementRecord>(4);
                        _pendingPlaceByMonster[pl.TargetMonsterId] = list;
                    }
                    if (list.Count >= MaxPendingPlacesPerMonster) list.RemoveAt(0);
                    list.Add(pl);
                }
            }

            // Set watermarks BELOW any adopted incoming-match Place so the new match's first complete
            // board scan validates them cleanly.
            long initialWatermark = minAdoptedPlaceSeq < long.MaxValue ? (minAdoptedPlaceSeq - 1) : latestSeqSnapshot;
            lock (_placeQueueLock)
            {
                _lastOldLayoutValidatedSeq = initialWatermark;
                _validatedPlaceSeq = initialWatermark;
            }

            // Partition existing + queued SkillUsed records by incoming match identity and re-stamp to newLayoutGen.
            for (int i = _recentSkills.Count - 1; i >= 0; i--)
            {
                var sk = _recentSkills[i];
                if (IsIncomingMatchRecord(sk.MatchRoomId, sk.MatchStartTime, sk.MatchGeneration))
                {
                    sk.MatchGeneration = newMatchGen;
                    sk.LayoutGeneration = newLayoutGen;
                }
                else
                {
                    _recentSkills.RemoveAt(i);
                }
            }
            while (_skillQueue.TryDequeue(out var skq))
            {
                if (skq != null && IsIncomingMatchRecord(skq.MatchRoomId, skq.MatchStartTime, skq.MatchGeneration))
                {
                    skq.MatchGeneration = newMatchGen;
                    skq.LayoutGeneration = newLayoutGen;
                    _recentSkills.Add(skq);
                }
            }

            // Partition BOTH already-drained pending dice rolls AND queued dice rolls by incoming match identity.
            List<DiceRollRecord> drainedDice = null;
            int latestDiceSeq;
            lock (_diceQueueLock)
            {
                if (_diceQueue.Count > 0)
                {
                    drainedDice = new List<DiceRollRecord>(_diceQueue.Count);
                    while (_diceQueue.Count > 0) drainedDice.Add(_diceQueue.Dequeue());
                }
                latestDiceSeq = _totalDiceSeq;
            }
            int minAdoptedDiceSeq = int.MaxValue;
            List<long> emptyDiceOwners = null;
            foreach (var kv in _pendingDiceByOwner)
            {
                var list = kv.Value;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var dr = list[i];
                    if (IsIncomingMatchRecord(dr.MatchRoomId, dr.MatchStartTime, dr.MatchGeneration))
                    {
                        dr.MatchGeneration = newMatchGen;
                        list[i] = dr;
                        if (dr.DiceSeq < minAdoptedDiceSeq) minAdoptedDiceSeq = dr.DiceSeq;
                    }
                    else
                    {
                        list.RemoveAt(i);
                    }
                }
                if (list.Count == 0)
                {
                    if (emptyDiceOwners == null) emptyDiceOwners = new List<long>();
                    emptyDiceOwners.Add(kv.Key);
                }
            }
            if (emptyDiceOwners != null)
            {
                for (int i = 0; i < emptyDiceOwners.Count; i++) _pendingDiceByOwner.Remove(emptyDiceOwners[i]);
            }
            if (drainedDice != null)
            {
                for (int i = 0; i < drainedDice.Count; i++)
                {
                    var dr = drainedDice[i];
                    if (dr.OwnerId <= 0) continue;
                    if (!IsIncomingMatchRecord(dr.MatchRoomId, dr.MatchStartTime, dr.MatchGeneration)) continue;
                    dr.MatchGeneration = newMatchGen;
                    if (dr.DiceSeq < minAdoptedDiceSeq) minAdoptedDiceSeq = dr.DiceSeq;
                    if (!_pendingDiceByOwner.TryGetValue(dr.OwnerId, out var seqs))
                    {
                        seqs = new List<DiceRollRecord>(4);
                        _pendingDiceByOwner[dr.OwnerId] = seqs;
                    }
                    if (seqs.Count >= 16) seqs.RemoveAt(0);
                    seqs.Add(dr);
                }
            }
            lock (_diceQueueLock)
            {
                _lastConsumedDiceSeq = minAdoptedDiceSeq < int.MaxValue ? (minAdoptedDiceSeq - 1) : latestDiceSeq;
            }

            // Partition BOTH already-drained pending combat/steal records AND queued records by incoming match identity,
            // rebinding positively identified incoming records to newLayoutGen and resetting turn/move bindings.
            const int MaxPendingCombatPerMonster = 16;
            List<long> emptyCombatUnits = null;
            foreach (var kv in _pendingCombatByMonster)
            {
                var list = kv.Value;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var pc = list[i];
                    if (IsIncomingMatchRecord(pc.MatchRoomId, pc.MatchStartTime, pc.MatchGeneration))
                    {
                        pc.MatchGeneration = newMatchGen;
                        pc.LayoutGeneration = newLayoutGen;
                        pc.PublishedActorAtEvent = 0;
                        pc.PublishedTurnSequenceAtEvent = 0;
                        pc.PublishedRoundAtEvent = -1;
                        pc.BoundTurnSequence = -1;
                        pc.CreatedMoveSeq = -1;
                        pc.CreatedLandId = -1;
                        pc.BossLockEvaluated = false;
                        list[i] = pc;
                    }
                    else
                    {
                        list.RemoveAt(i);
                    }
                }
                if (list.Count == 0)
                {
                    if (emptyCombatUnits == null) emptyCombatUnits = new List<long>();
                    emptyCombatUnits.Add(kv.Key);
                }
            }
            if (emptyCombatUnits != null)
            {
                for (int i = 0; i < emptyCombatUnits.Count; i++) _pendingCombatByMonster.Remove(emptyCombatUnits[i]);
            }
            while (_combatQueue.TryDequeue(out var cq))
            {
                if (!IsIncomingMatchRecord(cq.MatchRoomId, cq.MatchStartTime, cq.MatchGeneration)) continue;
                void AdoptCombat(long uid, PendingUnitCombat entry)
                {
                    if (uid == 0) return;
                    if (!_pendingCombatByMonster.TryGetValue(uid, out var list))
                    {
                        list = new List<PendingUnitCombat>(2);
                        _pendingCombatByMonster[uid] = list;
                    }
                    if (list.Count >= MaxPendingCombatPerMonster) list.RemoveAt(0);
                    list.Add(entry);
                }
                if (cq.AttackerId != 0)
                    AdoptCombat(cq.AttackerId, new PendingUnitCombat
                    {
                        CombatOrderSeq = cq.CombatOrderSeq,
                        IsAttacker = true,
                        LandId = cq.AttackerLandId,
                        OpponentId = cq.DefenderId,
                        OpponentHadProblemStudent = cq.DefenderHadProblemStudent,
                        OpponentHadMandarinDuck = cq.DefenderHadMandarinDuck,
                        ActiveActor = cq.ActiveActorAtEvent,
                        PublishedActorAtEvent = 0,
                        PublishedTurnSequenceAtEvent = 0,
                        PublishedRoundAtEvent = -1,
                        DiceSeqAtEvent = cq.DiceSeqAtEvent,
                        PlaceSeqAtEvent = cq.PlaceSeqAtEvent,
                        BoundTurnSequence = -1,
                        CreatedMoveSeq = -1,
                        CreatedLandId = -1,
                        Round = cq.Round,
                        MatchRoomId = cq.MatchRoomId,
                        MatchStartTime = cq.MatchStartTime,
                        MatchGeneration = newMatchGen,
                        LayoutGeneration = newLayoutGen
                    });
                if (cq.DefenderId != 0)
                    AdoptCombat(cq.DefenderId, new PendingUnitCombat
                    {
                        CombatOrderSeq = cq.CombatOrderSeq,
                        IsAttacker = false,
                        LandId = cq.DefenderLandId,
                        OpponentId = cq.AttackerId,
                        OpponentHadProblemStudent = cq.AttackerHadProblemStudent,
                        OpponentHadMandarinDuck = cq.AttackerHadMandarinDuck,
                        ActiveActor = cq.ActiveActorAtEvent,
                        PublishedActorAtEvent = 0,
                        PublishedTurnSequenceAtEvent = 0,
                        PublishedRoundAtEvent = -1,
                        DiceSeqAtEvent = cq.DiceSeqAtEvent,
                        PlaceSeqAtEvent = cq.PlaceSeqAtEvent,
                        BoundTurnSequence = -1,
                        CreatedMoveSeq = -1,
                        CreatedLandId = -1,
                        Round = cq.Round,
                        MatchRoomId = cq.MatchRoomId,
                        MatchStartTime = cq.MatchStartTime,
                        MatchGeneration = newMatchGen,
                        LayoutGeneration = newLayoutGen
                    });
            }

            List<long> emptyStealUnits = null;
            foreach (var kv in _pendingStealByMonster)
            {
                var list = kv.Value;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var ps = list[i];
                    if (IsIncomingMatchRecord(ps.MatchRoomId, ps.MatchStartTime, ps.MatchGeneration))
                    {
                        ps.MatchGeneration = newMatchGen;
                        ps.LayoutGeneration = newLayoutGen;
                        ps.PublishedActorAtEvent = 0;
                        ps.PublishedTurnSequenceAtEvent = 0;
                        ps.PublishedRoundAtEvent = -1;
                        ps.BoundTurnSequence = -1;
                        ps.CreatedMoveSeq = -1;
                        ps.CreatedLandId = -1;
                        list[i] = ps;
                    }
                    else
                    {
                        list.RemoveAt(i);
                    }
                }
                if (list.Count == 0)
                {
                    if (emptyStealUnits == null) emptyStealUnits = new List<long>();
                    emptyStealUnits.Add(kv.Key);
                }
            }
            if (emptyStealUnits != null)
            {
                for (int i = 0; i < emptyStealUnits.Count; i++) _pendingStealByMonster.Remove(emptyStealUnits[i]);
            }
            while (_thiefStealQueue.TryDequeue(out var tsq))
            {
                if (tsq.ThiefId == 0 || !IsIncomingMatchRecord(tsq.MatchRoomId, tsq.MatchStartTime, tsq.MatchGeneration)) continue;
                if (!_pendingStealByMonster.TryGetValue(tsq.ThiefId, out var sList))
                {
                    sList = new List<PendingThiefSteal>(2);
                    _pendingStealByMonster[tsq.ThiefId] = sList;
                }
                if (sList.Count >= MaxPendingCombatPerMonster) sList.RemoveAt(0);
                sList.Add(new PendingThiefSteal
                {
                    VictimId = tsq.VictimId,
                    LandId = tsq.LandId,
                    ActiveActor = tsq.ActiveActorAtEvent,
                    PublishedActorAtEvent = 0,
                    PublishedTurnSequenceAtEvent = 0,
                    PublishedRoundAtEvent = -1,
                    DiceSeqAtEvent = tsq.DiceSeqAtEvent,
                    PlaceSeqAtEvent = tsq.PlaceSeqAtEvent,
                    BoundTurnSequence = -1,
                    CreatedMoveSeq = -1,
                    CreatedLandId = -1,
                    Round = tsq.Round,
                    MatchRoomId = tsq.MatchRoomId,
                    MatchStartTime = tsq.MatchStartTime,
                    MatchGeneration = newMatchGen,
                    LayoutGeneration = newLayoutGen
                });
            }
        }

        public MatchSyncResult EnsureSameMatch()
        {
            if (!Players.TryGetMatch(out var info) || (info.RoomId == 0 && info.StartTime == 0))
            {
                return MatchSyncResult.Unavailable;
            }
            bool hadMatch = _matchRoomId != 0 || _matchStartTime != 0;
            if (_matchRoomId != info.RoomId || _matchStartTime != info.StartTime)
            {
                ResetForNewMatch(info.RoomId, info.StartTime, confirmedMatchSwitch: hadMatch);
                return hadMatch ? MatchSyncResult.Switched : MatchSyncResult.InitialAcquired;
            }
            return MatchSyncResult.Same;
        }

        private struct PendingUnitCombat
        {
            public long CombatOrderSeq;
            public bool IsAttacker;
            public int LandId;
            public long OpponentId;
            public bool? OpponentHadProblemStudent;
            public bool? OpponentHadMandarinDuck;
            public long ActiveActor;
            public long PublishedActorAtEvent;
            public int PublishedTurnSequenceAtEvent;
            public int PublishedRoundAtEvent;
            public int DiceSeqAtEvent;
            public long PlaceSeqAtEvent;
            public int BoundTurnSequence;
            public int CreatedMoveSeq;
            public int CreatedLandId;
            public int Round;
            public long MatchRoomId;
            public long MatchStartTime;
            public int MatchGeneration;
            public int LayoutGeneration;
            public bool BossLockEvaluated;
        }

        private struct PendingThiefSteal
        {
            public long VictimId;
            public int LandId;
            public long ActiveActor;
            public long PublishedActorAtEvent;
            public int PublishedTurnSequenceAtEvent;
            public int PublishedRoundAtEvent;
            public int DiceSeqAtEvent;
            public long PlaceSeqAtEvent;
            public int BoundTurnSequence;
            public int CreatedMoveSeq;
            public int CreatedLandId;
            public int Round;
            public long MatchRoomId;
            public long MatchStartTime;
            public int MatchGeneration;
            public int LayoutGeneration;
        }

        private EventTurnMatch MatchEventTurn(
            long curActorId,
            long unitId,
            int curTurnSeq,
            long activeActorAtEvent,
            long publishedActorAtEvent,
            int publishedTurnSeqAtEvent,
            int publishedRoundAtEvent,
            int diceSeqAtEvent,
            int eventRound,
            ref int boundTurnSeq)
        {
            // Check already-bound turns FIRST so a fight bound to a provisional next-round turn stays
            // matched across the Room.Round tick (e.g. 7 -> 8 while B remains on the same turnSeq).
            if (boundTurnSeq >= 0)
            {
                return (curActorId == unitId && boundTurnSeq == curTurnSeq)
                    ? EventTurnMatch.Matched
                    : EventTurnMatch.Superseded;
            }

            // Classify future-round events BEFORE treating a potentially stale live actor as final.
            int effectiveRound = _roundTracker.EffectiveRoundNumber;
            if (eventRound >= 0 && effectiveRound >= 0 && eventRound > effectiveRound)
            {
                return EventTurnMatch.Pending;
            }

            // If Room.Round had already ticked ahead of publishedRoundAtEvent while GetCurrentPlayer()
            // still temporarily reported the previous round's actor, treat that actor stamp as stale rather than rival.
            bool stalePrevRoundActor =
                eventRound >= 0 &&
                publishedRoundAtEvent >= 0 &&
                eventRound > publishedRoundAtEvent &&
                activeActorAtEvent != 0 &&
                activeActorAtEvent == publishedActorAtEvent;

            if (!stalePrevRoundActor && activeActorAtEvent != 0 && activeActorAtEvent != unitId)
                return EventTurnMatch.Superseded;

            if (eventRound >= 0 && effectiveRound >= 0 && eventRound < effectiveRound)
            {
                // Enforce strict 1-round lag ceiling and require positive provisional-turn evidence.
                if (eventRound != effectiveRound - 1)
                    return EventTurnMatch.Superseded;

                bool matchesCurrentProvisionalTurn =
                    curActorId == unitId &&
                    (_roundTracker.IsProvisionalNextRoundActive
                        ? (publishedTurnSeqAtEvent == curTurnSeq ||
                           (activeActorAtEvent == unitId && _currentTurnOwnerStartDiceSeq > 0 && diceSeqAtEvent >= _currentTurnOwnerStartDiceSeq))
                        : (publishedTurnSeqAtEvent == curTurnSeq && activeActorAtEvent == unitId));
                if (!matchesCurrentProvisionalTurn)
                    return EventTurnMatch.Superseded;
            }

            // Event fired 1 poll BEFORE ObserveTurnBoundary observed the actor switch to unitId: keep pending!
            if (curActorId != unitId)
            {
                if ((activeActorAtEvent == unitId || stalePrevRoundActor) && curTurnSeq == publishedTurnSeqAtEvent)
                    return EventTurnMatch.Pending;
                return EventTurnMatch.Superseded;
            }

            if (publishedActorAtEvent == unitId)
            {
                if (publishedTurnSeqAtEvent == curTurnSeq)
                {
                    boundTurnSeq = curTurnSeq;
                    return EventTurnMatch.Matched;
                }
                // Same-actor extra turn(s): handles 1, 2, or N extra rolls before polling AND unrelated
                // rolls by other owners afterwards, using the exact owner-specific roll start sequence!
                if (publishedTurnSeqAtEvent < curTurnSeq &&
                    activeActorAtEvent == unitId &&
                    _currentTurnOwnerStartDiceSeq > 0 &&
                    diceSeqAtEvent >= _currentTurnOwnerStartDiceSeq)
                {
                    boundTurnSeq = curTurnSeq;
                    return EventTurnMatch.Matched;
                }
                return EventTurnMatch.Superseded;
            }

            // Actor switch (A -> unitId): require explicit live-actor confirmation (or proven stale-previous-round actor
            // on the exact arrived round) so an event with unknown live actor (0) can never hijack the next defender.
            bool actorConfirmed = activeActorAtEvent == unitId ||
                                  (stalePrevRoundActor && eventRound >= 0 && eventRound == effectiveRound);
            if (actorConfirmed &&
                (curTurnSeq == publishedTurnSeqAtEvent + 1 ||
                 (curTurnSeq > publishedTurnSeqAtEvent && _currentTurnOwnerStartDiceSeq > 0 && diceSeqAtEvent >= _currentTurnOwnerStartDiceSeq)))
            {
                boundTurnSeq = curTurnSeq;
                return EventTurnMatch.Matched;
            }

            return EventTurnMatch.Superseded;
        }

        private bool IsRecentlyRetiredMonster(long monsterId, double nowSec)
        {
            if (!_retiredMonsterSec.TryGetValue(monsterId, out double retiredAt)) return false;
            if ((nowSec - retiredAt) <= RetiredMonsterGraceSec) return true;
            _retiredMonsterSec.Remove(monsterId);
            return false;
        }

        private void DrainPlaceQueue()
        {
            const int MaxPendingPlacesPerMonster = 16;
            double nowSec = MonotonicSeconds();
            int curLayoutGen;
            long oldValidatedSeq;
            long curValidatedSeq;
            BoardGraph oldBoard;
            BoardGraph curBoard;
            List<SkillDisplacementRecord> batch = null;
            lock (_placeQueueLock)
            {
                if (_placeQueue.Count == 0) return;
                batch = new List<SkillDisplacementRecord>(_placeQueue.Count);
                while (_placeQueue.Count > 0) batch.Add(_placeQueue.Dequeue());
                curLayoutGen = Volatile.Read(ref _layoutGeneration);
                oldValidatedSeq = _lastOldLayoutValidatedSeq;
                curValidatedSeq = _validatedPlaceSeq;
                oldBoard = _previousBoardBeforeTopologyChange;
                curBoard = _lastValidatedBoard;
            }

            for (int i = 0; i < batch.Count; i++)
            {
                var pl = batch[i];
                if (pl == null || pl.TargetMonsterId == 0 || pl.NewLandId < 0) continue;
                if (!IsMatchingEventStamp(pl.MatchRoomId, pl.MatchStartTime, pl.MatchGeneration)) continue;
                if (IsRecentlyRetiredMonster(pl.TargetMonsterId, nowSec)) continue;

                // Apply the exact same board-transition decision to late-enqueued old-generation records
                // as to records already in _pendingPlaceByMonster during NotifyBoardTopologyChanged.
                if (pl.LayoutGeneration < curLayoutGen)
                {
                    if (pl.LayoutGeneration == curLayoutGen - 1 &&
                        TryMigratePlaceAcrossLayoutChange(pl, oldBoard, curBoard, oldValidatedSeq, curLayoutGen, curValidatedSeq))
                    {
                        // Migrated cleanly via positive new-layout evidence.
                    }
                    else
                    {
                        continue;
                    }
                }

                if (!_pendingPlaceByMonster.TryGetValue(pl.TargetMonsterId, out var list))
                {
                    list = new List<SkillDisplacementRecord>(4);
                    _pendingPlaceByMonster[pl.TargetMonsterId] = list;
                }
                if (list.Count >= MaxPendingPlacesPerMonster)
                {
                    list.RemoveAt(0);
                }
                // Maintain strict PlaceSeq ordering even if an adopted record preceded a newer arrival.
                int insertPos = list.Count;
                while (insertPos > 0 && list[insertPos - 1].PlaceSeq > pl.PlaceSeq) insertPos--;
                list.Insert(insertPos, pl);
            }
        }

        private void DrainEventQueues()
        {
            const int MaxPendingCombatPerMonster = 16;
            double nowSec = MonotonicSeconds();
            int curLayoutGen = Volatile.Read(ref _layoutGeneration);

            for (int i = _recentExactDisplacements.Count - 1; i >= 0; i--)
            {
                if ((nowSec - _recentExactDisplacements[i].TimestampSec) > 2.5)
                    _recentExactDisplacements.RemoveAt(i);
            }

            while (_skillQueue.TryDequeue(out var sk))
            {
                if (sk != null &&
                    sk.LayoutGeneration == curLayoutGen &&
                    IsMatchingEventStamp(sk.MatchRoomId, sk.MatchStartTime, sk.MatchGeneration))
                {
                    // Reject known non-displacement skills (SkillId > 0 not matching 109/128/306 active skills)
                    // from matching receipts as wildcards, use event timestamps, and correlate ALL victims of one cast!
                    SkillMoveKind skKind = IdentifyDisplacementSkill(sk.SkillId, sk.CasterPlayerId, Array.Empty<BattlePlayerData>(), allowCasterHeroFallback: false);
                    bool knownNonDisplacement = sk.SkillId > 0 && skKind == SkillMoveKind.None;
                    if (!knownNonDisplacement)
                    {
                        for (int ri = _recentExactDisplacements.Count - 1; ri >= 0; ri--)
                        {
                            var r = _recentExactDisplacements[ri];
                            // Tight 1.0s same-cast receipt window prevents grouping victims from two distinct nearby casts.
                            if (Math.Abs(sk.TimestampSec - r.TimestampSec) > 1.0) continue;
                            if (sk.AppliedMonsterIds.Contains(r.MonsterId)) continue;
                            bool kindMatch = sk.SkillId <= 0 || skKind == r.Kind;
                            bool casterMatch = sk.SkillId <= 0
                                ? (sk.CasterPlayerId > 0 && r.CasterPlayerId > 0 && sk.CasterPlayerId == r.CasterPlayerId)
                                : (sk.CasterPlayerId == 0 || r.CasterPlayerId == 0 || sk.CasterPlayerId == r.CasterPlayerId);
                            if (kindMatch && casterMatch)
                            {
                                sk.AppliedMonsterIds.Add(r.MonsterId);
                                _recentExactDisplacements.RemoveAt(ri);
                            }
                        }
                    }
                    _recentSkills.Add(sk);
                }
            }
            for (int i = _recentSkills.Count - 1; i >= 0; i--)
            {
                if (_recentSkills[i].LayoutGeneration != curLayoutGen || (nowSec - _recentSkills[i].TimestampSec) > 2.5)
                {
                    _recentSkills.RemoveAt(i);
                }
            }

            DrainPlaceQueue();

            int GetMoveSeq(long unitId)
            {
                return _memories.TryGetValue(unitId, out var m) ? m.MoveSequence : -1;
            }
            int GetLastLand(long unitId)
            {
                return _memories.TryGetValue(unitId, out var m) ? m.LastLandId : -1;
            }

            void AddCombat(long unitId, PendingUnitCombat entry)
            {
                if (unitId == 0 || IsRecentlyRetiredMonster(unitId, nowSec)) return;
                if (!_pendingCombatByMonster.TryGetValue(unitId, out var list))
                {
                    list = new List<PendingUnitCombat>(2);
                    _pendingCombatByMonster[unitId] = list;
                }
                if (list.Count >= MaxPendingCombatPerMonster) list.RemoveAt(0);
                list.Add(entry);
            }

            while (_combatQueue.TryDequeue(out var c))
            {
                if (!IsMatchingEventStamp(c.MatchRoomId, c.MatchStartTime, c.MatchGeneration)) continue;
                if (c.AttackerId != 0)
                    AddCombat(c.AttackerId, new PendingUnitCombat
                    {
                        CombatOrderSeq = c.CombatOrderSeq,
                        IsAttacker = true,
                        LandId = c.AttackerLandId,
                        OpponentId = c.DefenderId,
                        OpponentHadProblemStudent = c.DefenderHadProblemStudent,
                        OpponentHadMandarinDuck = c.DefenderHadMandarinDuck,
                        ActiveActor = c.ActiveActorAtEvent,
                        PublishedActorAtEvent = c.PublishedActorAtEvent,
                        PublishedTurnSequenceAtEvent = c.PublishedTurnSequenceAtEvent,
                        PublishedRoundAtEvent = c.PublishedRoundAtEvent,
                        DiceSeqAtEvent = c.DiceSeqAtEvent,
                        PlaceSeqAtEvent = c.PlaceSeqAtEvent,
                        BoundTurnSequence = -1,
                        CreatedMoveSeq = GetMoveSeq(c.AttackerId),
                        CreatedLandId = GetLastLand(c.AttackerId),
                        Round = c.Round,
                        MatchRoomId = c.MatchRoomId,
                        MatchStartTime = c.MatchStartTime,
                        MatchGeneration = c.MatchGeneration,
                        LayoutGeneration = c.LayoutGeneration
                    });
                if (c.DefenderId != 0)
                    AddCombat(c.DefenderId, new PendingUnitCombat
                    {
                        CombatOrderSeq = c.CombatOrderSeq,
                        IsAttacker = false,
                        LandId = c.DefenderLandId,
                        OpponentId = c.AttackerId,
                        OpponentHadProblemStudent = c.AttackerHadProblemStudent,
                        OpponentHadMandarinDuck = c.AttackerHadMandarinDuck,
                        ActiveActor = c.ActiveActorAtEvent,
                        PublishedActorAtEvent = c.PublishedActorAtEvent,
                        PublishedTurnSequenceAtEvent = c.PublishedTurnSequenceAtEvent,
                        PublishedRoundAtEvent = c.PublishedRoundAtEvent,
                        DiceSeqAtEvent = c.DiceSeqAtEvent,
                        PlaceSeqAtEvent = c.PlaceSeqAtEvent,
                        BoundTurnSequence = -1,
                        CreatedMoveSeq = GetMoveSeq(c.DefenderId),
                        CreatedLandId = GetLastLand(c.DefenderId),
                        Round = c.Round,
                        MatchRoomId = c.MatchRoomId,
                        MatchStartTime = c.MatchStartTime,
                        MatchGeneration = c.MatchGeneration,
                        LayoutGeneration = c.LayoutGeneration
                    });
            }

            while (_thiefStealQueue.TryDequeue(out var ts))
            {
                if (!IsMatchingEventStamp(ts.MatchRoomId, ts.MatchStartTime, ts.MatchGeneration)) continue;
                if (ts.ThiefId != 0 && !IsRecentlyRetiredMonster(ts.ThiefId, nowSec))
                {
                    if (!_pendingStealByMonster.TryGetValue(ts.ThiefId, out var sList))
                    {
                        sList = new List<PendingThiefSteal>(2);
                        _pendingStealByMonster[ts.ThiefId] = sList;
                    }
                    if (sList.Count >= MaxPendingCombatPerMonster) sList.RemoveAt(0);
                    sList.Add(new PendingThiefSteal
                    {
                        VictimId = ts.VictimId,
                        LandId = ts.LandId,
                        ActiveActor = ts.ActiveActorAtEvent,
                        PublishedActorAtEvent = ts.PublishedActorAtEvent,
                        PublishedTurnSequenceAtEvent = ts.PublishedTurnSequenceAtEvent,
                        PublishedRoundAtEvent = ts.PublishedRoundAtEvent,
                        DiceSeqAtEvent = ts.DiceSeqAtEvent,
                        PlaceSeqAtEvent = ts.PlaceSeqAtEvent,
                        BoundTurnSequence = -1,
                        CreatedMoveSeq = GetMoveSeq(ts.ThiefId),
                        CreatedLandId = GetLastLand(ts.ThiefId),
                        Round = ts.Round,
                        MatchRoomId = ts.MatchRoomId,
                        MatchStartTime = ts.MatchStartTime,
                        MatchGeneration = ts.MatchGeneration,
                        LayoutGeneration = ts.LayoutGeneration
                    });
                }
            }
        }

        private ExactPlaceLookup TryConsumeExactPlaceDisplacement(
            BoardGraph board,
            long monsterId,
            MonsterMemory mem,
            int oldLandId,
            int newLandId,
            int liveFromLandId,
            int serverNodeId,
            bool leftPreviousTile,
            bool isInitialBoardObservation,
            Dictionary<long, int> playerPositions,
            IReadOnlyList<BattlePlayerData> allUnits,
            out int consumedTileHops)
        {
            consumedTileHops = 0;
            if (!_pendingPlaceByMonster.TryGetValue(monsterId, out var list) || list.Count == 0)
            {
                return ExactPlaceLookup.NotFound;
            }

            // Never consume records belonging to a superseded layout, and never consume records that have
            // not been explicitly validated as compatible with the current complete board scan.
            int curLayoutGen = Volatile.Read(ref _layoutGeneration);
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].LayoutGeneration < curLayoutGen)
                {
                    list.RemoveAt(i);
                }
            }
            if (list.Count == 0)
            {
                _pendingPlaceByMonster.Remove(monsterId);
                return ExactPlaceLookup.NotFound;
            }

            // Collect ALL validated indices for curLayoutGen so a quarantined future-layout head record
            // never blocks valid current-layout records behind it.
            var validatedIndices = new List<int>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].ValidatedForCurrentLayout) validatedIndices.Add(i);
            }
            if (validatedIndices.Count == 0) return ExactPlaceLookup.NotFound;

            bool HasPredecessorConflict(SkillDisplacementRecord r)
            {
                return liveFromLandId >= 0 &&
                       r.BackLandId >= 0 &&
                       board.Contains(r.BackLandId) &&
                       r.BackLandId != liveFromLandId;
            }

            int matchValidPos = -1;
            if (!leftPreviousTile)
            {
                var firstValid = list[validatedIndices[0]];
                if (firstValid.NewLandId >= 0 && firstValid.NewLandId == newLandId && !HasPredecessorConflict(firstValid))
                {
                    matchValidPos = 0;
                }
                else if (isInitialBoardObservation)
                {
                    for (int k = 0; k < validatedIndices.Count; k++)
                    {
                        var r = list[validatedIndices[k]];
                        if (r.NewLandId != newLandId || HasPredecessorConflict(r)) continue;
                        if (liveFromLandId >= 0 && r.BackLandId == liveFromLandId)
                        {
                            matchValidPos = k;
                            break;
                        }
                        if (serverNodeId == newLandId && k == validatedIndices.Count - 1)
                        {
                            matchValidPos = k;
                        }
                    }
                }
                else if (validatedIndices.Count >= 2 && serverNodeId == newLandId && liveFromLandId >= 0)
                {
                    for (int k = 1; k < validatedIndices.Count; k++)
                    {
                        var r = list[validatedIndices[k]];
                        if (r.NewLandId == newLandId && r.BackLandId == liveFromLandId)
                        {
                            matchValidPos = k;
                            break;
                        }
                    }
                }
            }
            else
            {
                // Visual model confirmed moving to newLandId: confirmed predecessor conflicts take precedence
                // over destination-only matches; with unknown liveFromLandId, require corroboration against oldLandId
                // or serverNodeId so a conflicting older occurrence is never blindly attached.
                int firstCompatibleDestPos = -1;
                for (int k = 0; k < validatedIndices.Count; k++)
                {
                    var r = list[validatedIndices[k]];
                    if (r.NewLandId != newLandId) continue;
                    if (HasPredecessorConflict(r)) continue;
                    if (liveFromLandId < 0)
                    {
                        bool corroborated = r.BackLandId < 0 ||
                                            r.BackLandId == oldLandId ||
                                            (serverNodeId == newLandId && k == validatedIndices.Count - 1);
                        if (!corroborated) continue;
                    }
                    if (firstCompatibleDestPos < 0) firstCompatibleDestPos = k;
                    if (liveFromLandId >= 0 && r.BackLandId == liveFromLandId)
                    {
                        matchValidPos = k;
                        break;
                    }
                }
                if (matchValidPos < 0) matchValidPos = firstCompatibleDestPos;
            }

            if (matchValidPos < 0)
            {
                if (leftPreviousTile && validatedIndices.Count > 0)
                {
                    // Retire overtaken leading validated Place: targeting oldLandId after stepping off it,
                    // OR targeting newLandId with a contradicting explicit predecessor (overtaken visit).
                    int headIdx = validatedIndices[0];
                    var head = list[headIdx];
                    bool overtakenOldTile = oldLandId >= 0 && head.NewLandId == oldLandId;
                    bool overtakenConflictingDest = head.NewLandId == newLandId && HasPredecessorConflict(head);
                    if (overtakenOldTile || overtakenConflictingDest)
                    {
                        if (overtakenOldTile &&
                            IdentifyDisplacementSkill(head.SkillId, head.CasterPlayerId, allUnits, allowCasterHeroFallback: false) == SkillMoveKind.None)
                        {
                            ReleaseLatestHeuristicClaimForLand(mem, monsterId, oldLandId, mem.MoveSequence);
                        }
                        list.RemoveAt(headIdx);
                        if (list.Count == 0) _pendingPlaceByMonster.Remove(monsterId);
                    }
                }
                return ExactPlaceLookup.NotFound;
            }

            // Sequentially fold validated entries 0..matchValidPos in arrival order, discarding any contradicted
            // prefix entries without mutating skill state, facing, or cast accounting.
            int simLand = oldLandId;
            for (int k = 0; k <= matchValidPos; k++)
            {
                var rec = list[validatedIndices[k]];
                if (rec.NewLandId < 0) continue;
                if (k < matchValidPos && rec.NewLandId == newLandId && HasPredecessorConflict(rec))
                {
                    continue;
                }
                int nextLand = rec.NewLandId;
                bool hopChangedTile = (nextLand != simLand);
                if (rec.PlaceSeq > mem.LastAppliedPlaceSeq) mem.LastAppliedPlaceSeq = rec.PlaceSeq;
                if (hopChangedTile)
                {
                    // Count EVERY actual tile-changing hop exactly once in one place.
                    mem.MoveSequence++;
                    consumedTileHops++;
                    mem.JustSpawnedOnFork = false;
                    mem.JustFoughtOnFork = false;
                    mem.CombatForkLandId = -1;
                    mem.SkillMoveKind = SkillMoveKind.None;
                    mem.SkillMoveFromHeuristic = false;
                    mem.HeuristicSkillCast = null;
                    mem.SkillMoveLandId = -1;
                    mem.SkillMovePushDirId = -1;
                    mem.SkillMovePushFromId = -1;
                    mem.SkillMoveCasterPlayerId = 0;
                    mem.SkillMoveTimestampSec = 0;
                    mem.ExplicitFrontNodeIds = null;
                    mem.ExplicitFrontLandId = -1;
                    mem.ExplicitFrontFromLandId = -1;
                }

                int validRecBack = (rec.BackLandId >= 0 && rec.BackLandId != nextLand && board.Contains(rec.BackLandId))
                    ? rec.BackLandId
                    : -1;

                if (rec.FrontNodeIds != null && rec.FrontNodeIds.Length > 0)
                {
                    int boundFrom = (k == matchValidPos)
                        ? liveFromLandId
                        : (validRecBack >= 0 ? validRecBack : (hopChangedTile ? simLand : liveFromLandId));
                    mem.ExplicitFrontNodeIds = rec.FrontNodeIds;
                    mem.ExplicitFrontLandId = nextLand;
                    mem.ExplicitFrontFromLandId = boundFrom;
                }

                // Exact 1040.Place records MUST specify an explicit active displacement SkillId;
                // never infer Shirley/Trashcan/Lingling from caster HeroId when Cause is absent (SkillId == 0).
                SkillMoveKind kind = IdentifyDisplacementSkill(rec.SkillId, rec.CasterPlayerId, allUnits, allowCasterHeroFallback: false);
                if (kind != SkillMoveKind.None)
                {
                    // Prefer a protocol BackLandId that validates as a direct incoming neighbor of nextLand
                    // over an ambiguous older multi-hop origin tile.
                    int directIncomingPredecessor = (validRecBack >= 0 && board.IsValidNeighbor(validRecBack, nextLand))
                        ? validRecBack
                        : -1;
                    int pushOrigin = directIncomingPredecessor >= 0
                        ? directIncomingPredecessor
                        : ((hopChangedTile && simLand >= 0) ? simLand : validRecBack);

                    // Deduplicate identical same-tile exact Lingling notifications BEFORE charging another cast,
                    // requiring matching caster & tight cast timestamp so a second Lingling caster isn't conflated.
                    bool duplicateSameTileLingling =
                        !hopChangedTile &&
                        kind == SkillMoveKind.LinglingPush &&
                        mem.SkillMoveKind == SkillMoveKind.LinglingPush &&
                        !mem.SkillMoveFromHeuristic &&
                        mem.SkillMoveLandId == nextLand &&
                        mem.SkillMovePushFromId >= 0 &&
                        (validRecBack < 0 || validRecBack == mem.SkillMovePushFromId) &&
                        Math.Abs(rec.TimestampSec - mem.SkillMoveTimestampSec) <= 1.0 &&
                        (rec.CasterPlayerId == 0 || mem.SkillMoveCasterPlayerId == 0 || rec.CasterPlayerId == mem.SkillMoveCasterPlayerId);

                    if (!duplicateSameTileLingling)
                    {
                        bool alreadyClaimedMatchingCast = false;
                        SkillCastRecord hCast = (!hopChangedTile && mem.SkillMoveFromHeuristic && mem.SkillMoveLandId == nextLand)
                            ? mem.HeuristicSkillCast
                            : null;
                        if (hCast == null)
                        {
                            hCast = TryTakeLiveHeuristicClaim(mem, nextLand, mem.MoveSequence);
                        }
                        else
                        {
                            RemoveHeuristicClaimEntry(mem, nextLand, mem.MoveSequence, hCast);
                        }
                        if (hCast != null && _recentSkills.Contains(hCast))
                        {
                            SkillMoveKind hKind = IdentifyDisplacementSkill(hCast.SkillId, hCast.CasterPlayerId, allUnits, allowCasterHeroFallback: true);
                            bool casterMatches = rec.CasterPlayerId == 0 || hCast.CasterPlayerId == 0 || hCast.CasterPlayerId == rec.CasterPlayerId;
                            bool timeMatches = Math.Abs(hCast.TimestampSec - rec.TimestampSec) <= 2.5;
                            if (hKind == kind && casterMatches && timeMatches)
                            {
                                alreadyClaimedMatchingCast = true;
                            }
                            else
                            {
                                hCast.AppliedMonsterIds.Remove(monsterId);
                            }
                        }
                        mem.HeuristicSkillCast = null;

                        if (!alreadyClaimedMatchingCast)
                        {
                            if (!ConsumeRecentSkillCastForMonster(monsterId, kind, rec.CasterPlayerId, rec.TimestampSec, allUnits))
                            {
                                // Exact Place arrived before SkillUsed: store a receipt with the original event timestamp
                                // so delayed rendering cannot correlate an old effect with a fresh cast.
                                if (_recentExactDisplacements.Count >= 32) _recentExactDisplacements.RemoveAt(0);
                                _recentExactDisplacements.Add(new ExactDisplacementReceipt
                                {
                                    MonsterId = monsterId,
                                    Kind = kind,
                                    CasterPlayerId = rec.CasterPlayerId,
                                    TimestampSec = rec.TimestampSec
                                });
                            }
                        }

                        // Compute candidate Lingling predecessor FIRST before overwriting an existing validated
                        // same-arrival predecessor so an inconclusive board-present non-adjacent BackLandId never erases it!
                        bool hasExistingSameArrivalLingling =
                            !hopChangedTile &&
                            kind == SkillMoveKind.LinglingPush &&
                            mem.SkillMoveKind == SkillMoveKind.LinglingPush &&
                            mem.SkillMoveLandId == nextLand &&
                            mem.SkillMovePushFromId >= 0;

                        if (hasExistingSameArrivalLingling)
                        {
                            int candPushNext = -1;
                            int candPushFrom = -1;
                            if (directIncomingPredecessor >= 0 && board.Contains(directIncomingPredecessor) && board.Contains(nextLand))
                            {
                                candPushFrom = directIncomingPredecessor;
                                var fwd = board.GetForwardNeighbors(nextLand, directIncomingPredecessor);
                                if (fwd.Count == 1) candPushNext = fwd[0];
                            }
                            else
                            {
                                InferLinglingPushDirection(board, pushOrigin, nextLand, rec.CasterPlayerId, playerPositions,
                                    out candPushNext, out candPushFrom);
                            }
                            if (candPushFrom >= 0)
                            {
                                mem.SkillMovePushFromId = candPushFrom;
                                mem.SkillMovePushDirId = candPushNext;
                            }
                            mem.SkillMoveFromHeuristic = false;
                            mem.SkillMoveCasterPlayerId = rec.CasterPlayerId;
                            mem.SkillMoveTimestampSec = rec.TimestampSec;
                        }
                        else
                        {
                            ApplyDisplacementTag(board, mem, kind, pushOrigin, nextLand, rec.CasterPlayerId, playerPositions, directIncomingPredecessor);
                            mem.SkillMoveFromHeuristic = false;
                            mem.SkillMoveCasterPlayerId = rec.CasterPlayerId;
                            mem.SkillMoveTimestampSec = rec.TimestampSec;
                        }
                    }
                }
                else
                {
                    // Even on a hop or after leaving a tile, a late authoritative ordinary Place releases any
                    // unresolved provisional heuristic claim for this movement occurrence!
                    ReleaseHeuristicClaim(mem, monsterId, nextLand, mem.MoveSequence);
                    if (!hopChangedTile && mem.SkillMoveFromHeuristic && mem.SkillMoveLandId == nextLand)
                    {
                        if (mem.HeuristicSkillCast != null)
                        {
                            mem.HeuristicSkillCast.AppliedMonsterIds.Remove(monsterId);
                            mem.HeuristicSkillCast = null;
                        }
                        mem.SkillMoveKind = SkillMoveKind.None;
                        mem.SkillMoveFromHeuristic = false;
                        mem.SkillMoveLandId = -1;
                        mem.SkillMovePushDirId = -1;
                        mem.SkillMovePushFromId = -1;
                        mem.SkillMoveCasterPlayerId = 0;
                        mem.SkillMoveTimestampSec = 0;
                    }
                }

                simLand = nextLand;
            }

            // Consume ONLY the validated entries 0..matchValidPos (leaving any quarantined future-layout records intact!).
            for (int k = matchValidPos; k >= 0; k--)
            {
                list.RemoveAt(validatedIndices[k]);
            }
            if (list.Count == 0) _pendingPlaceByMonster.Remove(monsterId);

            return (mem.SkillMoveKind != SkillMoveKind.None && mem.SkillMoveLandId == newLandId)
                ? ExactPlaceLookup.TaggedDisplacement
                : ExactPlaceLookup.KnownOtherCause;
        }

        private bool ConsumeRecentSkillCastForMonster(
            long monsterId,
            SkillMoveKind kind,
            long casterPlayerId,
            double recTimestampSec,
            IReadOnlyList<BattlePlayerData> allUnits)
        {
            if (_recentSkills.Count == 0 || kind == SkillMoveKind.None) return false;

            // Consume at most ONE matching cast whose original event timestamp lies within 2.5s of the
            // exact Place effect (preventing a delayed Place held for validation from consuming a fresh cast!).
            for (int i = 0; i < _recentSkills.Count; i++)
            {
                var sk = _recentSkills[i];
                if (sk.AppliedMonsterIds.Contains(monsterId)) continue;
                if (Math.Abs(sk.TimestampSec - recTimestampSec) > 2.5) continue;
                SkillMoveKind skKind = IdentifyDisplacementSkill(sk.SkillId, sk.CasterPlayerId, allUnits, allowCasterHeroFallback: true);
                if (skKind != kind) continue;
                if (casterPlayerId != 0 && sk.CasterPlayerId != 0 && sk.CasterPlayerId != casterPlayerId) continue;

                sk.AppliedMonsterIds.Add(monsterId);
                return true;
            }
            return false;
        }

        private void PruneExpiredHeuristicClaims(MonsterMemory mem, double nowSec, int curLayoutGen)
        {
            for (int i = mem.HeuristicClaims.Count - 1; i >= 0; i--)
            {
                var c = mem.HeuristicClaims[i].Cast;
                if (c == null || c.LayoutGeneration != curLayoutGen || (nowSec - c.TimestampSec) > 2.5 || !_recentSkills.Contains(c))
                {
                    mem.HeuristicClaims.RemoveAt(i);
                }
            }
        }

        private SkillCastRecord TryTakeLiveHeuristicClaim(MonsterMemory mem, int landId, int moveSeq)
        {
            for (int i = mem.HeuristicClaims.Count - 1; i >= 0; i--)
            {
                var entry = mem.HeuristicClaims[i];
                if (entry.LandId == landId && entry.MoveSeq == moveSeq)
                {
                    mem.HeuristicClaims.RemoveAt(i);
                    return entry.Cast;
                }
            }
            return null;
        }

        private static void RemoveHeuristicClaimEntry(MonsterMemory mem, int landId, int moveSeq, SkillCastRecord cast)
        {
            for (int i = mem.HeuristicClaims.Count - 1; i >= 0; i--)
            {
                var entry = mem.HeuristicClaims[i];
                if (entry.LandId == landId && entry.MoveSeq == moveSeq && ReferenceEquals(entry.Cast, cast))
                {
                    mem.HeuristicClaims.RemoveAt(i);
                }
            }
        }

        private static void ReleaseHeuristicClaim(MonsterMemory mem, long monsterId, int landId, int moveSeq)
        {
            for (int i = mem.HeuristicClaims.Count - 1; i >= 0; i--)
            {
                var entry = mem.HeuristicClaims[i];
                if (entry.LandId == landId && entry.MoveSeq == moveSeq)
                {
                    entry.Cast?.AppliedMonsterIds.Remove(monsterId);
                    mem.HeuristicClaims.RemoveAt(i);
                }
            }
        }

        private static void ReleaseLatestHeuristicClaimForLand(MonsterMemory mem, long monsterId, int landId, int maxMoveSeq)
        {
            for (int i = mem.HeuristicClaims.Count - 1; i >= 0; i--)
            {
                var entry = mem.HeuristicClaims[i];
                if (entry.LandId == landId && entry.MoveSeq <= maxMoveSeq)
                {
                    entry.Cast?.AppliedMonsterIds.Remove(monsterId);
                    mem.HeuristicClaims.RemoveAt(i);
                    return;
                }
            }
        }

        private bool TryTagSkillDisplacementFromWindow(
            BoardGraph board,
            long monsterId,
            MonsterMemory mem,
            int oldLandId,
            int newLandId,
            Dictionary<long, int> playerPositions,
            IReadOnlyList<BattlePlayerData> allUnits)
        {
            // Fallback to SkillUsed window ONLY when unambiguous: if multiple conflicting displacement kinds
            // (or different Lingling push casters) occurred within the same window, leave SkillMoveKind unknown
            // instead of cross-attributing Cast 2 onto Cast 1's victim.
            if (_recentSkills.Count == 0) return false;

            SkillCastRecord uniqueCast = null;
            SkillMoveKind uniqueKind = SkillMoveKind.None;
            for (int i = _recentSkills.Count - 1; i >= 0; i--)
            {
                var sk = _recentSkills[i];
                if (sk.AppliedMonsterIds.Contains(monsterId)) continue;

                SkillMoveKind kind = IdentifyDisplacementSkill(sk.SkillId, sk.CasterPlayerId, allUnits, allowCasterHeroFallback: true);
                if (kind == SkillMoveKind.None) continue;

                if (uniqueCast == null)
                {
                    uniqueCast = sk;
                    uniqueKind = kind;
                }
                else if (kind != uniqueKind || (kind == SkillMoveKind.LinglingPush && sk.CasterPlayerId != uniqueCast.CasterPlayerId))
                {
                    // Ambiguous concurrent displacement casts in window: do not guess.
                    return false;
                }
            }

            if (uniqueCast == null) return false;

            uniqueCast.AppliedMonsterIds.Add(monsterId);
            ApplyDisplacementTag(board, mem, uniqueKind, oldLandId, newLandId, uniqueCast.CasterPlayerId, playerPositions, directIncomingPredecessor: -1);
            mem.SkillMoveFromHeuristic = true;
            mem.HeuristicSkillCast = uniqueCast;
            mem.SkillMoveCasterPlayerId = uniqueCast.CasterPlayerId;
            mem.SkillMoveTimestampSec = uniqueCast.TimestampSec;
            PruneExpiredHeuristicClaims(mem, MonotonicSeconds(), Volatile.Read(ref _layoutGeneration));
            if (mem.HeuristicClaims.Count >= 4)
            {
                // Release BOTH sides when evicting an unresolved claim so victim isn't stranded in AppliedMonsterIds.
                var evicted = mem.HeuristicClaims[0];
                evicted.Cast?.AppliedMonsterIds.Remove(monsterId);
                mem.HeuristicClaims.RemoveAt(0);
            }
            mem.HeuristicClaims.Add(new HeuristicClaimEntry
            {
                MoveSeq = mem.MoveSequence,
                LandId = newLandId,
                Cast = uniqueCast
            });
            return true;
        }

        private static void ApplyDisplacementTag(
            BoardGraph board,
            MonsterMemory mem,
            SkillMoveKind kind,
            int oldLandId,
            int newLandId,
            long casterPlayerId,
            Dictionary<long, int> playerPositions,
            int directIncomingPredecessor)
        {
            mem.SkillMoveKind = kind;
            mem.SkillMoveLandId = newLandId;
            mem.SkillMovePushDirId = -1;
            mem.SkillMovePushFromId = -1;
            mem.JustSpawnedOnFork = false;

            if (kind == SkillMoveKind.LinglingPush)
            {
                if (directIncomingPredecessor >= 0 && board.Contains(directIncomingPredecessor) && board.Contains(newLandId))
                {
                    mem.SkillMovePushFromId = directIncomingPredecessor;
                    var fwd = board.GetForwardNeighbors(newLandId, directIncomingPredecessor);
                    if (fwd.Count == 1) mem.SkillMovePushDirId = fwd[0];
                }
                else
                {
                    InferLinglingPushDirection(board, oldLandId, newLandId, casterPlayerId, playerPositions,
                        out int pushNextId, out int pushFromId);
                    mem.SkillMovePushDirId = pushNextId;
                    mem.SkillMovePushFromId = pushFromId;
                }
            }
        }

        private static SkillMoveKind IdentifyDisplacementSkill(
            int skillId,
            long casterPlayerId,
            IReadOnlyList<BattlePlayerData> allUnits,
            bool allowCasterHeroFallback)
        {
            // Match exact active displacement SkillIds first (language-independent, excludes passives >= xx10):
            //   - 10901..10909: Z3000 (垃圾箱, HeroId 109) active pull
            //   - 12801..12809: Lingling (玲玲, HeroId 128) active push
            //   - 30601..30609: Sherry (橘雪莉, HeroId 306) active throw
            if (skillId > 0)
            {
                if (skillId >= 10000)
                {
                    int heroPrefix = skillId / 100;
                    int sub = skillId % 100;
                    if (sub >= 1 && sub <= 9)
                    {
                        if (heroPrefix == 109) return SkillMoveKind.TrashcanPull;
                        if (heroPrefix == 128) return SkillMoveKind.LinglingPush;
                        if (heroPrefix == 306) return SkillMoveKind.ShirleyThrow;
                    }
                }
                return SkillMoveKind.None;
            }

            if (!allowCasterHeroFallback || casterPlayerId == 0) return SkillMoveKind.None;

            // Fallback ONLY on confirmed SkillUsed events where SDK SkillId is unavailable (0):
            // match caster HeroId directly.
            for (int i = 0; i < allUnits.Count; i++)
            {
                var pd = allUnits[i];
                if (pd == null || pd.player == null || pd.player.Id != casterPlayerId) continue;
                int heroId = SafeHeroId(pd);
                if (heroId == 109) return SkillMoveKind.TrashcanPull;
                if (heroId == 128) return SkillMoveKind.LinglingPush;
                if (heroId == 306) return SkillMoveKind.ShirleyThrow;
            }
            return SkillMoveKind.None;
        }

        private static void InferLinglingPushDirection(
            BoardGraph board,
            int oldLandId,
            int newLandId,
            long casterPlayerId,
            Dictionary<long, int> playerPositions,
            out int pushNextId,
            out int pushFromId)
        {
            pushNextId = -1;
            pushFromId = -1;

            int origin = oldLandId;
            if (!board.Contains(origin) && playerPositions.TryGetValue(casterPlayerId, out int casterLand))
            {
                origin = casterLand;
            }
            if (!board.Contains(origin) || !board.Contains(newLandId)) return;

            if (board.TryInferForwardArrivalPredecessor(origin, newLandId, out int pred))
            {
                pushFromId = pred;
            }

            if (pushFromId >= 0)
            {
                var fwd = board.GetForwardNeighbors(newLandId, pushFromId);
                if (fwd.Count == 1) pushNextId = fwd[0];
            }
        }

        /// <summary>
        /// Tri-state unit liveness evaluator with strict source precedence and isolated field reads:
        /// live <c>Property.HealthState</c>, <c>Property.HP</c>, and <c>Property.maxHP</c> are read
        /// independently so a throwing getter never skips readable live evidence.
        /// </summary>
        private static bool? TryEvaluateUnitLiveness(BattlePlayerData pd)
        {
            if (pd == null) return null;
            bool propHealthNotDead = false;
            bool propHasPositiveMaxHp = false;
            bool propHpReadable = false;
            int propCurHp = 0;

            try
            {
                // Read pd.Property ONCE inside try and reuse the captured reference for all isolated property checks.
                var prop = pd.Property;
                if (prop != null)
                {
                    try
                    {
                        if (prop.HealthState == HealthState.Dead) return false;
                        propHealthNotDead = true;
                    }
                    catch { }

                    try
                    {
                        if (prop.maxHP > 0) propHasPositiveMaxHp = true;
                    }
                    catch { }

                    try
                    {
                        if (prop.HP != null)
                        {
                            propCurHp = prop.HP.Value;
                            propHpReadable = true;
                        }
                    }
                    catch { }

                    if (propHpReadable)
                    {
                        if (propCurHp > 0) return true;
                        if (propHasPositiveMaxHp && propCurHp <= 0) return false;
                        return null;
                    }
                }
            }
            catch { }

            // Only consult fallback protocol Hero.Hp when live Property.HP was unavailable;
            // isolate hero.Hp and hero.MaxHp reads so one throwing getter cannot mask the other.
            try
            {
                var hero = pd.player != null ? pd.player.Hero : null;
                if (hero != null)
                {
                    bool heroHpOk = false;
                    int heroHp = 0;
                    bool heroMaxOk = false;
                    int heroMax = 0;
                    try { heroHp = hero.Hp; heroHpOk = true; } catch { }
                    try { heroMax = hero.MaxHp; heroMaxOk = true; } catch { }
                    if (heroHpOk && heroHp > 0) return true;
                    if (heroHpOk && heroMaxOk && heroMax > 0 && heroHp <= 0) return false;
                }
            }
            catch { }

            return (propHealthNotDead && propHasPositiveMaxHp) ? (bool?)true : null;
        }

        private TrackedPlayer BuildTrackedPlayer(
            BattlePlayerData pd,
            BoardGraph board,
            PredictorSettings settings,
            out bool isTrueCulprit,
            out bool livenessConfirmed,
            out bool posConfirmedOnLayout)
        {
            isTrueCulprit = false;
            livenessConfirmed = false;
            posConfirmedOnLayout = false;
            try
            {
                var p = pd.player;
                if (p == null) return null;

                bool? aliveVerdict = TryEvaluateUnitLiveness(pd);
                bool dead;
                if (aliveVerdict.HasValue)
                {
                    dead = !aliveVerdict.Value;
                    _lastKnownPlayerAlive[p.Id] = aliveVerdict.Value;
                    livenessConfirmed = true;
                }
                else if (_lastKnownPlayerAlive.TryGetValue(p.Id, out bool prevAlive))
                {
                    // Unknown/inconclusive liveness on an already-known hero: retain last-known liveness
                    // for display continuity while withholding authoritative readiness (livenessConfirmed=false).
                    dead = !prevAlive;
                    livenessConfirmed = false;
                }
                else
                {
                    // First observation with unknown liveness: never invent alive & targetable!
                    livenessConfirmed = false;
                    return null;
                }

                _confirmedHeroFields.TryGetValue(p.Id, out var cache);
                if (cache == null)
                {
                    cache = new ConfirmedHeroCache();
                    _confirmedHeroFields[p.Id] = cache;
                }
                cache.IsAlive = !dead;

                bool notSelect = false;
                bool notSelectReadOk = false;
                try
                {
                    var prop = pd.Property;
                    if (prop != null && prop.NotSelect != null)
                    {
                        notSelect = prop.NotSelect.Value;
                        notSelectReadOk = true;
                    }
                }
                catch { }
                if (!notSelect)
                {
                    try
                    {
                        if (p.Hero != null)
                        {
                            notSelect = p.Hero.NotSelect;
                            notSelectReadOk = true;
                        }
                    }
                    catch { }
                }

                bool hasProblemStudent = false;
                bool hasMandarinDuck = false;
                bool hasUnchallengeableStealth = false;
                int crimeFromBuff = 0;
                bool anyBuffSourceRead = false;

                try
                {
                    var buffs = Players.BuffsOf(p.Id);
                    if (buffs != null)
                    {
                        anyBuffSourceRead = true;
                        for (int i = 0; i < buffs.Count; i++)
                        {
                            int bid = buffs[i].BuffId;
                            if (bid == 2101001) hasProblemStudent = true;
                            else if (bid == 10470101) hasMandarinDuck = true;
                            else if (bid == 10671302) isTrueCulprit = true;
                            else if (bid == 1270202) hasUnchallengeableStealth = true;

                            if (!notSelect && settings != null && settings.ExcludedPlayerBuffIds != null &&
                                settings.ExcludedPlayerBuffIds.Contains(bid))
                            {
                                notSelect = true;
                                notSelectReadOk = true;
                            }
                        }
                    }
                }
                catch { }

                try
                {
                    if (p.Hero != null && p.Hero.Buffs != null)
                    {
                        anyBuffSourceRead = true;
                        foreach (var kv in p.Hero.Buffs)
                        {
                            var b = kv.Value;
                            if (b == null) continue;
                            int bid = b.BuffId;
                            if (bid == 2101001) hasProblemStudent = true;
                            else if (bid == 10470101) hasMandarinDuck = true;
                            else if (bid == 10671302) isTrueCulprit = true;
                            else if (bid == 1270202) hasUnchallengeableStealth = true;
                            else if (bid == 10013 && b.Progress > crimeFromBuff) crimeFromBuff = b.Progress;

                            if (!notSelect && settings != null && settings.ExcludedPlayerBuffIds != null &&
                                settings.ExcludedPlayerBuffIds.Contains(bid))
                            {
                                notSelect = true;
                                notSelectReadOk = true;
                            }
                        }
                    }
                }
                catch { }

                // Merge buff-derived and targetability fields with last confirmed values when sources failed.
                if (anyBuffSourceRead)
                {
                    cache.HasBuffRead = true;
                    cache.HasProblemStudentMark = hasProblemStudent;
                    cache.HasMandarinDuckMark = hasMandarinDuck;
                    cache.CanBeAttacked = !hasUnchallengeableStealth;
                    cache.IsTrueCulprit = isTrueCulprit;
                }
                else if (cache.HasBuffRead)
                {
                    hasProblemStudent = cache.HasProblemStudentMark;
                    hasMandarinDuck = cache.HasMandarinDuckMark;
                    hasUnchallengeableStealth = !cache.CanBeAttacked;
                    isTrueCulprit = cache.IsTrueCulprit;
                }

                if (notSelectReadOk || anyBuffSourceRead)
                {
                    cache.HasTargetable = true;
                    cache.IsTargetable = !notSelect;
                }
                else if (cache.HasTargetable)
                {
                    notSelect = !cache.IsTargetable;
                }

                int slot = 0;
                bool slotReadOk = false;
                try
                {
                    slot = p.ChangeSlot >= 0 && p.ChangeSlot < 4 ? p.ChangeSlot : p.Slot;
                    slotReadOk = true;
                }
                catch
                {
                    try { slot = p.Slot; slotReadOk = true; } catch { }
                }
                if (slotReadOk)
                {
                    cache.HasSlot = true;
                    cache.Slot = slot;
                }
                else if (cache.HasSlot)
                {
                    slot = cache.Slot;
                }

                int landId = SafeAuthoritativeHeroLandId(pd, board, out posConfirmedOnLayout);
                if (dead) posConfirmedOnLayout = true;
                if (posConfirmedOnLayout && landId >= 0 && (board == null || board.Contains(landId)))
                {
                    cache.LandId = landId;
                }
                else if (!dead && !_awaitingPostLayoutHeroConfirmation && cache.LandId >= 0 && (board == null || board.Contains(cache.LandId)))
                {
                    // Same-layout transient tile read miss: preserve last confirmed same-layout position
                    // while leaving posConfirmedOnLayout=false so readiness reflects the transient gap.
                    landId = cache.LandId;
                }

                string rawNick = Players.SafeNick(pd);
                if (!string.IsNullOrEmpty(rawNick)) cache.Name = rawNick;
                string nick = !string.IsNullOrEmpty(rawNick)
                    ? rawNick
                    : (!string.IsNullOrEmpty(cache.Name) ? cache.Name : ("P" + (slot + 1)));

                int gold = SafeGold(pd);
                if (gold >= 0)
                {
                    cache.HasGold = true;
                    cache.Gold = gold;
                }
                else if (cache.HasGold)
                {
                    gold = cache.Gold;
                }

                int? crimeOpt = TryReadCrimeNum(pd);
                int crimeNum;
                if (crimeOpt.HasValue || anyBuffSourceRead)
                {
                    crimeNum = Math.Max(crimeOpt ?? 0, crimeFromBuff);
                    cache.HasCrimeNum = true;
                    cache.CrimeNum = crimeNum;
                }
                else if (cache.HasCrimeNum)
                {
                    crimeNum = cache.CrimeNum;
                }
                else
                {
                    crimeNum = 0;
                }

                return new TrackedPlayer
                {
                    PlayerId = p.Id,
                    Slot = slot,
                    Name = nick,
                    LandId = landId,
                    IsAlive = !dead,
                    IsTargetable = !notSelect,
                    CanBeAttacked = !hasUnchallengeableStealth,
                    Gold = gold,
                    CrimeNum = crimeNum,
                    HasProblemStudentMark = hasProblemStudent,
                    HasMandarinDuckMark = hasMandarinDuck
                };
            }
            catch
            {
                return null;
            }
        }

        private static void SafeHp(BattlePlayerData pd, out int hp, out int maxHp, out bool hasHp, out bool hasMaxHp)
        {
            hp = 0;
            maxHp = 0;
            hasHp = false;
            hasMaxHp = false;
            try
            {
                // Capture pd.Property ONCE inside try so a throwing getter never escapes or aborts snapshots.
                var prop = pd != null ? pd.Property : null;
                if (prop != null)
                {
                    try
                    {
                        if (prop.HP != null)
                        {
                            hp = prop.HP.Value;
                            hasHp = true;
                        }
                    }
                    catch { }
                    try
                    {
                        if (prop.maxHP > 0)
                        {
                            maxHp = prop.maxHP;
                            hasMaxHp = true;
                        }
                    }
                    catch { }
                }
            }
            catch { }

            if (!hasHp || !hasMaxHp)
            {
                try
                {
                    var hero = pd != null && pd.player != null ? pd.player.Hero : null;
                    if (hero != null)
                    {
                        // Isolate protocol hero.Hp and hero.MaxHp reads so a throwing Hp getter never skips a readable MaxHp.
                        bool heroHpOk = false;
                        int heroHp = 0;
                        bool heroMaxOk = false;
                        int heroMax = 0;
                        try { heroHp = hero.Hp; heroHpOk = true; } catch { }
                        try { heroMax = hero.MaxHp; heroMaxOk = true; } catch { }

                        if (!hasMaxHp && heroMaxOk && heroMax > 0)
                        {
                            maxHp = heroMax;
                            hasMaxHp = true;
                        }
                        // Shared evidence rule: only accept protocol Hero.Hp as known when Hero.Hp > 0 or Hero.MaxHp > 0
                        // (never treating an uninitialized protocol Hero.Hp=0, MaxHp=0 as invented "HP 0/max").
                        if (!hasHp && heroHpOk && (heroHp > 0 || (heroMaxOk && heroMax > 0)))
                        {
                            hp = heroHp;
                            hasHp = true;
                        }
                    }
                }
                catch { }
            }
        }

        private static bool TryCheckRosterMonsterAlive(BattlePlayerData pd, out bool isAlive)
        {
            isAlive = true;
            if (pd == null) return false;
            var verdict = TryEvaluateUnitLiveness(pd);
            if (!verdict.HasValue) return false;
            isAlive = verdict.Value;
            return true;
        }

        public bool IsMonsterConfirmedDead(long monsterId)
        {
            if (monsterId == 0) return true;
            try
            {
                var allUnits = Players.All();
                if (allUnits == null || allUnits.Count == 0) return false;

                bool allEntriesReadable = true;
                bool sawAnyMonster = false;
                var seenKnownHeroes = new HashSet<long>();
                BattlePlayerData matchedMonster = null;
                int matchedIdCount = 0;
                bool matchedNonMonster = false;

                // Scan the ENTIRE roster first so a same-type or cross-type duplicate ID conflict is always
                // detected regardless of enumeration order before declaring explicit death.
                for (int i = 0; i < allUnits.Count; i++)
                {
                    var pd = allUnits[i];
                    if (pd == null || pd.player == null)
                    {
                        allEntriesReadable = false;
                        continue;
                    }
                    CharacterType cType = CharacterType.None;
                    try { cType = pd.characterType; } catch { allEntriesReadable = false; continue; }

                    long uid = pd.player.Id;
                    if (cType == CharacterType.Hero && _knownPlayerIds.Contains(uid))
                    {
                        seenKnownHeroes.Add(uid);
                    }
                    else if (cType == CharacterType.Monster)
                    {
                        sawAnyMonster = true;
                    }

                    if (uid == monsterId)
                    {
                        matchedIdCount++;
                        if (cType == CharacterType.Monster)
                        {
                            matchedMonster = pd;
                        }
                        else
                        {
                            matchedNonMonster = true;
                        }
                    }
                }

                if (matchedIdCount > 1)
                {
                    // Duplicate ID across entries (same-type or cross-type): preserve last accepted state!
                    return false;
                }
                if (matchedIdCount == 1)
                {
                    if (matchedNonMonster || matchedMonster == null) return false;
                    return TryCheckRosterMonsterAlive(matchedMonster, out bool isAlive) && !isAlive;
                }

                // If absent from allUnits, confirm death ONLY when the roster sample is complete/authoritative
                // AND aligns with the tracker's retirement verdict (never returning dead on a partial/null-containing sample).
                if (!allEntriesReadable || seenKnownHeroes.Count < _knownPlayerIds.Count) return false;
                if (_retiredMonsterSec.ContainsKey(monsterId)) return true;
                return sawAnyMonster && !_memories.ContainsKey(monsterId) && !_rosterAliveMonsterIds.Contains(monsterId);
            }
            catch
            {
                return false;
            }
        }

        private static bool? TryReadMonsterIsThief(BattlePlayerData pd)
        {
            try
            {
                var hero = pd != null && pd.player != null ? pd.player.Hero : null;
                if (hero == null) return null;
                return hero.MonsterType == Hero.Types.MonsterType.Thief;
            }
            catch { return null; }
        }

        private static bool IsThiefMonster(BattlePlayerData pd)
        {
            return TryReadMonsterIsThief(pd) == true;
        }

        private static int SafeGold(BattlePlayerData pd)
        {
            try
            {
                var prop = pd.Property;
                if (prop != null && prop.gold != null) return prop.gold.Value;
            }
            catch { }
            try
            {
                if (pd.player != null && pd.player.Hero != null) return pd.player.Hero.Gold;
            }
            catch { }
            return -1;
        }

        private static bool _crimePropResolved;
        private static MemberInfo _crimePropMember;

        private static int? TryReadCrimeNum(BattlePlayerData pd)
        {
            try
            {
                var prop = pd.Property;
                if (prop != null)
                {
                    if (!_crimePropResolved)
                    {
                        _crimePropResolved = true;
                        var t = prop.GetType();
                        _crimePropMember = (MemberInfo)t.GetProperty("CrimeNum", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            ?? (MemberInfo)t.GetProperty("crimeNum", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            ?? (MemberInfo)t.GetField("CrimeNum", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            ?? t.GetField("crimeNum", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    }
                    if (_crimePropMember != null)
                    {
                        object raw = _crimePropMember is PropertyInfo pi ? pi.GetValue(prop) : ((FieldInfo)_crimePropMember).GetValue(prop);
                        if (raw is int iv) return Math.Max(0, iv);
                        if (raw != null)
                        {
                            var valProp = raw.GetType().GetProperty("Value");
                            if (valProp != null && valProp.GetValue(raw) is int rv) return Math.Max(0, rv);
                        }
                    }
                }
            }
            catch { }
            try
            {
                if (pd.player != null && pd.player.Hero != null) return Math.Max(0, pd.player.Hero.CrimeNum);
            }
            catch { }
            return null;
        }

        private static bool _bossTargetPropResolved;
        private static MemberInfo _bossTargetPropMember;

        private static long? TryReadBossTargetId(BattlePlayerData pd)
        {
            if (pd == null) return null;
            bool propReadOk = false;
            long propVal = 0;
            try
            {
                var prop = pd.Property;
                if (prop != null)
                {
                    if (!_bossTargetPropResolved)
                    {
                        _bossTargetPropResolved = true;
                        var t = prop.GetType();
                        _bossTargetPropMember = (MemberInfo)t.GetProperty("BossTargetId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            ?? (MemberInfo)t.GetProperty("bossTargetId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            ?? (MemberInfo)t.GetField("BossTargetId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            ?? t.GetField("bossTargetId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    }
                    if (_bossTargetPropMember != null)
                    {
                        object raw = _bossTargetPropMember is PropertyInfo pi ? pi.GetValue(prop) : ((FieldInfo)_bossTargetPropMember).GetValue(prop);
                        if (raw is long lv)
                        {
                            propVal = lv;
                            propReadOk = true;
                        }
                        else if (raw != null)
                        {
                            var valProp = raw.GetType().GetProperty("Value");
                            if (valProp != null && valProp.GetValue(raw) is long rv)
                            {
                                propVal = rv;
                                propReadOk = true;
                            }
                        }
                    }
                }
            }
            catch { }

            // Consistent source precedence: when live Property.BossTargetId is readable (whether 0 or nonzero),
            // honor it directly so a stale Hero.BossTargetId never resurrects an explicitly cleared 0!
            if (propReadOk) return propVal;

            try
            {
                if (pd.player != null && pd.player.Hero != null)
                    return pd.player.Hero.BossTargetId;
            }
            catch { }

            return null;
        }

        private static long FindNearestMarkedPlayerId(BoardGraph board, int monsterHeroId, int startLandId, List<TrackedPlayer> players)
        {
            if (board == null || !board.Contains(startLandId) || players == null) return 0;
            long bestId = 0;
            int bestDist = int.MaxValue;
            int bestSlot = int.MaxValue;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p == null || !p.IsAlive || !p.IsTargetable || !board.Contains(p.LandId) || p.LandId == startLandId) continue;
                bool marked = (monsterHeroId == 1018 && p.HasProblemStudentMark) ||
                              (monsterHeroId == 1047 && p.HasMandarinDuckMark);
                if (!marked) continue;
                int dist = board.ComputeShortestPathsTo(p.LandId).GetDistance(startLandId);
                if (dist == int.MaxValue || dist <= 0) continue;
                if (dist < bestDist ||
                    (dist == bestDist && (p.Slot < bestSlot || (p.Slot == bestSlot && p.PlayerId < bestId))))
                {
                    bestDist = dist;
                    bestSlot = p.Slot;
                    bestId = p.PlayerId;
                }
            }
            return bestId;
        }

        private static bool DidFightMarkedTarget(int monsterHeroId, long opponentId, List<TrackedPlayer> players)
        {
            if (players == null) return false;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p == null || p.PlayerId != opponentId) continue;
                if (monsterHeroId == 1018 && p.HasProblemStudentMark) return true;
                if (monsterHeroId == 1047 && p.HasMandarinDuckMark) return true;
                break;
            }
            return false;
        }

        private static bool IsCrimeRestrictedMonster(BattlePlayerData pd, int heroId, bool anyPlayerHasTrueCulprit)
        {
            if (heroId == 1067) // 典狱长: restricted to CrimeNum > 0 until 真凶揭露
            {
                if (anyPlayerHasTrueCulprit) return false;
                return !IsWardenTrueCulpritRevealed(pd);
            }
            if (heroId == 1068) // 看守: restricted to CrimeNum > 0 until 5 cumulative damage (buff 10681202)
            {
                return !HasMonsterBuff(pd, 10681202);
            }
            return false;
        }

        private static bool IsWardenTrueCulpritRevealed(BattlePlayerData pd)
        {
            try
            {
                long pid = pd.player != null ? pd.player.Id : 0;
                if (pid != 0)
                {
                    var buffs = Players.BuffsOf(pid);
                    if (buffs != null)
                    {
                        for (int i = 0; i < buffs.Count; i++)
                        {
                            if (buffs[i].BuffId == 10671303) return true;
                        }
                    }
                }
            }
            catch { }
            try
            {
                var hero = pd.player != null ? pd.player.Hero : null;
                if (hero != null && hero.Buffs != null)
                {
                    foreach (var kv in hero.Buffs)
                    {
                        var b = kv.Value;
                        if (b == null) continue;
                        if (b.BuffId == 10671303 || (b.BuffId == 10671301 && b.Progress > 0)) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static bool HasMonsterBuff(BattlePlayerData pd, int buffId)
        {
            try
            {
                long pid = pd.player != null ? pd.player.Id : 0;
                if (pid != 0)
                {
                    var buffs = Players.BuffsOf(pid);
                    if (buffs != null)
                    {
                        for (int i = 0; i < buffs.Count; i++)
                        {
                            if (buffs[i].BuffId == buffId) return true;
                        }
                    }
                }
            }
            catch { }
            try
            {
                var hero = pd.player != null ? pd.player.Hero : null;
                if (hero != null && hero.Buffs != null)
                {
                    foreach (var kv in hero.Buffs)
                    {
                        if (kv.Value != null && kv.Value.BuffId == buffId) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static bool TryReadTauntTarget(BattlePlayerData pd, Dictionary<int, long> heroIdToPlayerId, out long targetPlayerId)
        {
            targetPlayerId = 0;
            try
            {
                var hero = pd.player != null ? pd.player.Hero : null;
                if (hero == null || hero.Buffs == null) return false;
                foreach (var kv in hero.Buffs)
                {
                    var buff = kv.Value;
                    if (buff == null || buff.BuffId != 10011) continue;
                    int skillId = buff.Source != null ? buff.Source.Id : 0;
                    if (skillId >= 10000)
                    {
                        int casterHeroId = skillId / 100;
                        if (heroIdToPlayerId.TryGetValue(casterHeroId, out long pid))
                        {
                            targetPlayerId = pid;
                            return true;
                        }
                    }
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static bool? TryReadIsNormalAggressive(BattlePlayerData pd)
        {
            try
            {
                var hero = pd != null && pd.player != null ? pd.player.Hero : null;
                if (hero == null) return null;
                var mt = hero.MonsterType;
                return mt != Hero.Types.MonsterType.Thief && mt != Hero.Types.MonsterType.PveAllyMonster;
            }
            catch { return null; }
        }

        private static bool SafeIsLaunchFight(BattlePlayerData pd)
        {
            try { return pd.player != null && pd.player.Hero != null && pd.player.Hero.IsLaunchFight; }
            catch { return false; }
        }

        private static BattlePlayerData SafeGetCurrentPlayer(BattleLogic battle)
        {
            try { return battle.GetCurrentPlayer(); } catch { return null; }
        }

        private static int SafeLandId(BattlePlayerData pd)
        {
            return SafeLandId(pd, out _);
        }

        private static int SafeLandId(BattlePlayerData pd, out bool fromVisual)
        {
            fromVisual = false;
            if (pd == null) return -1;
            try
            {
                if (UnityObject.IsAlive(pd.CharacterInst) && pd.CharacterInst.standLand != null && UnityObject.IsAlive(pd.CharacterInst.standLand))
                {
                    // Set fromVisual ONLY AFTER standLand.Id succeeds so an exception never mislabels protocol fallback as visual!
                    int slId = pd.CharacterInst.standLand.Id;
                    fromVisual = true;
                    return slId;
                }
            }
            catch { }
            try
            {
                if (pd.player != null) return pd.player.NodeId;
            }
            catch { }
            return -1;
        }

        /// <summary>
        /// Reads a unit's board tile while verifying that any visual <c>standLand</c> object belongs to the
        /// currently captured <see cref="LandManager"/> (rejecting stale pre-replacement standLand instances
        /// with overlapping tile IDs).
        /// </summary>
        private int ReadUnitLandIdVerified(BattlePlayerData pd, out bool fromVerifiedVisual)
        {
            fromVerifiedVisual = false;
            if (pd == null) return -1;
            try
            {
                if (UnityObject.IsAlive(pd.CharacterInst) && pd.CharacterInst.standLand != null && UnityObject.IsAlive(pd.CharacterInst.standLand))
                {
                    var sl = pd.CharacterInst.standLand;
                    int slId = sl.Id;
                    bool verified = _currentLayoutLandById.Count == 0 ||
                                    (_currentLayoutLandById.TryGetValue(slId, out var expectedLand) && ReferenceEquals(sl, expectedLand));
                    if (verified)
                    {
                        fromVerifiedVisual = true;
                        return slId;
                    }
                }
            }
            catch { }
            try
            {
                if (pd.player != null) return pd.player.NodeId;
            }
            catch { }
            return -1;
        }

        /// <summary>
        /// Per-source authoritative hero tile resolver: tracks current-layout evidence for the protocol
        /// (<c>player.NodeId</c>) and visual (<c>standLand</c>) sources independently so verified visual evidence
        /// never certifies a disagreeing stale protocol tile, even after the global post-layout gate opens.
        /// </summary>
        private int SafeAuthoritativeHeroLandId(BattlePlayerData pd, BoardGraph board, out bool confirmedOnCurrentLayout)
        {
            confirmedOnCurrentLayout = false;
            if (pd == null) return -1;

            long pid = 0;
            int protoNode = -1;
            try
            {
                if (pd.player != null)
                {
                    pid = pd.player.Id;
                    protoNode = pd.player.NodeId;
                }
            }
            catch { }

            int standLandId = -1;
            bool standLandVerifiedOnCurrentManager = false;
            try
            {
                if (UnityObject.IsAlive(pd.CharacterInst) && pd.CharacterInst.standLand != null && UnityObject.IsAlive(pd.CharacterInst.standLand))
                {
                    var sl = pd.CharacterInst.standLand;
                    int idRead = sl.Id;
                    standLandId = idRead;
                    standLandVerifiedOnCurrentManager =
                        _currentLayoutLandById.Count > 0 &&
                        _currentLayoutLandById.TryGetValue(standLandId, out var expectedLand) &&
                        ReferenceEquals(sl, expectedLand);
                }
            }
            catch { }

            bool protoOnBoard = protoNode >= 0 && (board == null || board.Contains(protoNode));
            bool standOnBoard = standLandId >= 0 && standLandVerifiedOnCurrentManager && (board == null || board.Contains(standLandId));
            bool hasPriorLayout = _previousBoardBeforeTopologyChange != null || _preLayoutHeroProtoNodeId.Count > 0;

            bool protoConfirmed = protoOnBoard && (
                !hasPriorLayout ||
                (pid > 0 && _heroProtoConfirmedOnCurrentLayout.Contains(pid)) ||
                (_previousBoardBeforeTopologyChange != null && !_previousBoardBeforeTopologyChange.Contains(protoNode)) ||
                (pid > 0 && _preLayoutHeroProtoNodeId.TryGetValue(pid, out int oldProto) && protoNode != oldProto) ||
                (standOnBoard && protoNode == standLandId));

            if (hasPriorLayout && protoOnBoard && !protoConfirmed && pid > 0 && !_preLayoutHeroProtoNodeId.ContainsKey(pid))
            {
                // Baseline a newly sighted overlapping post-layout protocol ID so a later protocol change promotes it.
                _preLayoutHeroProtoNodeId[pid] = protoNode;
            }

            if (protoConfirmed)
            {
                if (pid > 0)
                {
                    _heroConfirmedOnCurrentLayout.Add(pid);
                    _heroProtoConfirmedOnCurrentLayout.Add(pid);
                    _lastHeroProtoNodeId[pid] = protoNode;
                }
                confirmedOnCurrentLayout = true;
                return protoNode;
            }

            if (standOnBoard)
            {
                // Visual standLand is verified on the replacement manager, while protocol is still stale/disagreeing:
                // return the verified visual tile and do NOT promote the unconfirmed protocol source!
                if (pid > 0)
                {
                    _heroConfirmedOnCurrentLayout.Add(pid);
                }
                confirmedOnCurrentLayout = true;
                return standLandId;
            }

            if (!hasPriorLayout && _currentLayoutLandById.Count == 0 && standLandId >= 0 && (board == null || board.Contains(standLandId)))
            {
                confirmedOnCurrentLayout = true;
                return standLandId;
            }

            return -1;
        }

        private static int SafeRoomPlayerNodeId(BattlePlayerData pd)
        {
            if (pd == null) return -1;
            try
            {
                if (pd.player != null) return pd.player.NodeId;
            }
            catch { }
            return -1;
        }

        private static int SafeFromLandId(BattlePlayerData pd)
        {
            if (pd == null) return -1;
            try
            {
                var ch = pd.CharacterInst;
                if (UnityObject.IsAlive(ch))
                    return ch.fromLandId; // Trust live Character.fromLandId directly (including explicit -1!)
            }
            catch { }
            try
            {
                if (pd.player != null && pd.player.BackNodeId >= 0)
                    return pd.player.BackNodeId;
            }
            catch { }
            try
            {
                if (pd.player != null && pd.player.Hero != null && pd.player.Hero.BackNodeId >= 0)
                    return pd.player.Hero.BackNodeId;
            }
            catch { }
            return -1;
        }

        private static readonly FieldInfo CharacterMoveField = typeof(Character).GetField(
            "move",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static FieldInfo _walkDirectionsField;
        private static bool _walkDirectionsFieldResolved;

        private static int[] TryReadLiveWalkDirectionArrows(BoardGraph board, Character ch, int landId)
        {
            if (board == null || !UnityObject.IsAlive(ch) || CharacterMoveField == null) return null;
            try
            {
                object moveComp = CharacterMoveField.GetValue(ch);
                if (moveComp == null) return null;
                if (!_walkDirectionsFieldResolved)
                {
                    _walkDirectionsFieldResolved = true;
                    _walkDirectionsField = moveComp.GetType().GetField(
                        "_walkDirections",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                }
                if (_walkDirectionsField == null) return null;
                if (!(_walkDirectionsField.GetValue(moveComp) is System.Collections.IList arrows) || arrows.Count == 0)
                    return null;

                var forwards = new List<(float ForwardX, float ForwardZ)>(arrows.Count);
                for (int i = 0; i < arrows.Count; i++)
                {
                    if (!(arrows[i] is UnityEngine.Object uObj) || !UnityObject.IsAlive(uObj)) continue;
                    var tr = UnityObject.GetTransform(uObj);
                    if (tr == null) continue;
                    Vector3 fwd = TransformService.GetForward(tr);
                    forwards.Add((fwd.x, fwd.z));
                }
                if (forwards.Count == 0) return null;
                return LiveFacingResolver.MatchVisualArrowsToNeighbors(board, landId, forwards);
            }
            catch
            {
                return null;
            }
        }

        private static int[] ResolveMonsterFrontNodeIds(
            BoardGraph board,
            Character ch,
            bool hasLiveChar,
            int landId,
            int fromLandId,
            MonsterMemory mem)
        {
            int[] visualArrows = hasLiveChar ? TryReadLiveWalkDirectionArrows(board, ch, landId) : null;
            int[] explicitFront = (mem != null &&
                                   mem.ExplicitFrontNodeIds != null &&
                                   mem.ExplicitFrontLandId == landId &&
                                   mem.ExplicitFrontFromLandId == fromLandId)
                ? mem.ExplicitFrontNodeIds
                : null;
            return LiveFacingResolver.ResolveLiveFrontNodeIds(
                board,
                landId,
                hasLiveChar,
                fromLandId,
                visualArrows,
                explicitFront);
        }

        private static int[] SafeRawProtocolFrontNodeIds(BattlePlayerData pd)
        {
            try
            {
                if (pd.player != null && pd.player.FrontIds != null && pd.player.FrontIds.Count > 0)
                {
                    var list = pd.player.FrontIds;
                    var arr = new int[list.Count];
                    for (int i = 0; i < list.Count; i++) arr[i] = list[i];
                    return arr;
                }
            }
            catch { }

            try
            {
                if (pd.player != null && pd.player.Hero != null && pd.player.Hero.FrontNodeIds != null && pd.player.Hero.FrontNodeIds.Count > 0)
                {
                    var list = pd.player.Hero.FrontNodeIds;
                    var arr = new int[list.Count];
                    for (int i = 0; i < list.Count; i++) arr[i] = list[i];
                    return arr;
                }
            }
            catch { }

            return Array.Empty<int>();
        }

        private static int SafeCanStep(Character ch)
        {
            try { return ch != null ? Math.Max(0, ch.canStep) : 0; } catch { return 0; }
        }

        private static int SafeHeroId(BattlePlayerData pd)
        {
            try
            {
                if (pd.player != null && pd.player.Hero != null && pd.player.Hero.HeroId > 0)
                    return pd.player.Hero.HeroId;
            }
            catch { }
            try
            {
                if (pd.player != null && pd.player.characterConfig != null)
                    return pd.player.characterConfig.Id;
            }
            catch { }
            return 0;
        }

        private static int? TryReadActionWeight(BattlePlayerData pd)
        {
            try
            {
                if (pd == null || pd.player == null || pd.player.Hero == null) return null;
                return pd.player.Hero.ActionWeight;
            }
            catch { return null; }
        }

        private static bool? SafeActionEnd(BattlePlayerData pd)
        {
            try
            {
                if (pd == null || pd.player == null || pd.player.Hero == null) return null;
                return pd.player.Hero.ActionEnd;
            }
            catch { return null; }
        }

        private static string ResolveMonsterName(BattlePlayerData pd, int heroId, out bool isProvisional)
        {
            isProvisional = false;
            try
            {
                if (heroId > 0)
                {
                    string n = CharacterHandle.GetCharacterName(heroId, CharacterType.Monster);
                    if (!string.IsNullOrEmpty(n)) return n;
                }
            }
            catch { }
            isProvisional = true;
            try
            {
                string n = Players.SafeNick(pd);
                if (!string.IsNullOrEmpty(n)) return n;
            }
            catch { }
            return "怪物";
        }
    }
}
