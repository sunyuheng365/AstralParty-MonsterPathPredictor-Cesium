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
            public int BossTargetAssignmentVersion;
            public long InferredBossTargetId;
            public bool HasPendingServerBossTarget;
            public long PendingServerBossTargetId;
            public readonly List<(long OpponentId, int TurnSeq)> RecentClearedBossFights = new List<(long, int)>(8);
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
            public int[] ExplicitFrontNodeIds;
            public int ExplicitFrontLandId = -1;
            public int ExplicitFrontFromLandId = -1;

            public long EffectiveBossTargetId => EffectiveServerBossTargetId != 0 ? EffectiveServerBossTargetId : InferredBossTargetId;
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
        }

        private struct CombatRecord
        {
            public long AttackerId;
            public long DefenderId;
            public int AttackerLandId;
            public int DefenderLandId;
            public bool AttackerHadProblemStudent;
            public bool AttackerHadMandarinDuck;
            public bool DefenderHadProblemStudent;
            public bool DefenderHadMandarinDuck;
            public long ActiveActorAtEvent;
            public long PublishedActorAtEvent;
            public int PublishedTurnSequenceAtEvent;
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

        private readonly ConcurrentQueue<DiceRollRecord> _diceQueue = new ConcurrentQueue<DiceRollRecord>();
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
        private readonly Dictionary<int, object> _currentLayoutLandById = new Dictionary<int, object>();
        private bool _awaitingPostLayoutHeroConfirmation;
        private readonly Dictionary<long, double> _retiredMonsterSec = new Dictionary<long, double>();
        private readonly Dictionary<long, List<int>> _pendingDiceSeqsByOwner = new Dictionary<long, List<int>>();
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
        private int _currentTurnOwnerStartDiceSeq;
        private long _nextPlaceSeq;
        private long _validatedPlaceSeq;
        private long _lastOldLayoutValidatedSeq;
        private int _layoutGeneration;
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

        public long LatestPlaceSequence
        {
            get { lock (_placeQueueLock) return _nextPlaceSeq; }
        }

        public bool HasUnvalidatedPlace
        {
            get
            {
                lock (_placeQueueLock)
                {
                    if (_placeQueue.Count > 0 || _nextPlaceSeq > _validatedPlaceSeq) return true;
                }
                foreach (var list in _pendingPlaceByMonster.Values)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (!list[i].ValidatedForCurrentLayout) return true;
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
                    bool compatible = IsPlaceCompatibleWithBoard(rec, completeBoard);
                    if (compatible)
                    {
                        rec.ValidatedForCurrentLayout = true;
                    }
                    else
                    {
                        // Keep records incompatible with the currently scanned board pending with positive
                        // mismatch evidence so a subsequent layout replacement migrates them cleanly.
                        rec.ValidatedForCurrentLayout = false;
                        rec.ObservedIncompatibleWithPreviousBoard = true;
                    }
                }
            }
        }

        private static bool IsPlaceCompatibleWithBoard(SkillDisplacementRecord rec, BoardGraph board)
        {
            if (rec == null || board == null) return false;
            return board.Contains(rec.NewLandId) && (rec.BackLandId < 0 || board.Contains(rec.BackLandId));
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

            // Positive new-layout evidence: previously observed incompatible with the old board,
            // OR incompatible with oldBoard directly (e.g. a new-layout-only destination/back tile).
            bool positiveNewLayoutEvidence =
                rec.ObservedIncompatibleWithPreviousBoard ||
                (oldBoard != null && !IsPlaceCompatibleWithBoard(rec, oldBoard));

            if (positiveNewLayoutEvidence)
            {
                rec.LayoutGeneration = newLayoutGen;
                rec.ObservedIncompatibleWithPreviousBoard = false;
                rec.ValidatedForCurrentLayout = newBoardScanMaxSeq > 0 && rec.PlaceSeq <= newBoardScanMaxSeq;
                return true;
            }

            // Otherwise the record's tile IDs existed on oldBoard (or were already validated on oldBoard):
            // quarantine/discard ambiguous overlapping IDs rather than relabeling an old Place onto newBoard.
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
            _currentLayoutLandById.Clear();
            _awaitingPostLayoutHeroConfirmation = false;
            _retiredMonsterSec.Clear();
            _pendingDiceSeqsByOwner.Clear();
            _recentSkills.Clear();
            _recentExactDisplacements.Clear();
            _pendingPlaceByMonster.Clear();
            _pendingCombatByMonster.Clear();
            _pendingStealByMonster.Clear();
            _lastAcceptedHeroIdToPlayerId.Clear();
            _lastAcceptedAnyPlayerHasTrueCulprit = false;
            _knownPlayerCount = 0;
            while (_diceQueue.TryDequeue(out _)) { }
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
            _awaitingPostLayoutHeroConfirmation = newBoard != null;

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
                for (int i = 0; i < all.Count; i++)
                {
                    var pd = all[i];
                    if (pd == null || pd.player == null || pd.player.Id != playerId) continue;
                    if (pd.characterType != CharacterType.Hero) return false;
                    var verdict = TryEvaluateUnitLiveness(pd);
                    // Require a fresh authoritative liveness verdict for live-owner mutations (never fall back to cached alive).
                    if (!verdict.HasValue) return false;
                    isAlive = verdict.Value;
                    authoritativeLandId = SafeAuthoritativeHeroLandId(pd, board, out bool posConfirmed);
                    if (isAlive && !posConfirmed) authoritativeLandId = -1;
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Checks live monster roster presence &amp; eligibility at commit time so a press-deferred popup row
        /// or board click cannot replace a valid selection with a dead, conflicting, or now-filtered monster.
        /// </summary>
        public static bool TryIsMonsterLiveAndEligible(long monsterId, bool allowNonAggressive)
        {
            if (monsterId <= 0) return false;
            try
            {
                var all = Players.All();
                if (all == null) return false;
                BattlePlayerData matched = null;
                for (int i = 0; i < all.Count; i++)
                {
                    var pd = all[i];
                    if (pd == null || pd.player == null || pd.player.Id != monsterId) continue;
                    if (pd.characterType != CharacterType.Monster) return false;
                    if (matched != null) return false; // Conflicting duplicate monster ID
                    matched = pd;
                }
                if (matched == null) return false;
                if (!TryCheckRosterMonsterAlive(matched, out bool alive) || !alive) return false;
                if (!allowNonAggressive)
                {
                    // Restricted selection requires a fresh confirmed aggressive classification.
                    var aggOpt = TryReadIsNormalAggressive(matched);
                    if (aggOpt != true) return false;
                }
                return true;
            }
            catch { }
            return false;
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

        private static void TryReadUnitBossMarks(long unitId, out bool hasProblemStudent, out bool hasMandarinDuck)
        {
            hasProblemStudent = false;
            hasMandarinDuck = false;
            if (unitId == 0) return;
            try
            {
                var buffs = Players.BuffsOf(unitId);
                if (buffs != null)
                {
                    for (int i = 0; i < buffs.Count; i++)
                    {
                        int bid = buffs[i].BuffId;
                        if (bid == 2101001) hasProblemStudent = true;
                        else if (bid == 10470101) hasMandarinDuck = true;
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
                    foreach (var kv in hero.Buffs)
                    {
                        var b = kv.Value;
                        if (b == null) continue;
                        if (b.BuffId == 2101001) hasProblemStudent = true;
                        else if (b.BuffId == 10470101) hasMandarinDuck = true;
                    }
                }
            }
            catch { }
        }

        private void OnDiceResult(long playerId, int dice1, int dice2)
        {
            if (!_acceptEvents) return;
            TryStampCurrentMatch(out long matchRoomId, out long matchStartTime);
            int matchGen = Volatile.Read(ref _matchGeneration);
            int diceSeq = Interlocked.Increment(ref _totalDiceSeq);
            if (_diceQueue.Count >= MaxQueuedEvents) _diceQueue.TryDequeue(out _);
            _diceQueue.Enqueue(new DiceRollRecord
            {
                OwnerId = playerId,
                MatchRoomId = matchRoomId,
                MatchStartTime = matchStartTime,
                MatchGeneration = matchGen,
                DiceSeq = diceSeq
            });
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
            int curDiceSeq = Volatile.Read(ref _totalDiceSeq);
            long curPlaceSeq = LatestPlaceSequence;
            long liveActorId = TryReadCurrentActorId();
            int causeId = attr.Cause != null ? (int)attr.Cause.Id : 0;
            TryStampCurrentMatch(out long matchRoomId, out long matchStartTime);
            int matchGen = Volatile.Read(ref _matchGeneration);
            int layoutGen = Volatile.Read(ref _layoutGeneration);
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
                        DiceSeqAtEvent = curDiceSeq,
                        PlaceSeqAtEvent = curPlaceSeq,
                        Round = curRound,
                        MatchRoomId = matchRoomId,
                        MatchStartTime = matchStartTime,
                        MatchGeneration = matchGen,
                        LayoutGeneration = layoutGen
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
                    // Publish PlaceSeq, LayoutGeneration, and queue entry atomically under _placeQueueLock
                    // so consumer prefixes are strictly sequence-ordered and aligned with board transitions.
                    lock (_placeQueueLock)
                    {
                        long placeSeq = ++_nextPlaceSeq;
                        int atomicLayoutGen = Volatile.Read(ref _layoutGeneration);
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
                            LayoutGeneration = atomicLayoutGen,
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
            int curDiceSeq = Volatile.Read(ref _totalDiceSeq);
            long curPlaceSeq = LatestPlaceSequence;
            TryReadUnitBossMarks(snap.AttackerPlayerId, out bool atkPs, out bool atkMd);
            TryReadUnitBossMarks(snap.DefenderPlayerId, out bool defPs, out bool defMd);
            if (_combatQueue.Count >= MaxQueuedEvents) _combatQueue.TryDequeue(out _);
            _combatQueue.Enqueue(new CombatRecord
            {
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
                int curDiceSeq = Volatile.Read(ref _totalDiceSeq);
                long curPlaceSeq = LatestPlaceSequence;
                TryReadUnitBossMarks(atk, out bool atkPs, out bool atkMd);
                TryReadUnitBossMarks(def, out bool defPs, out bool defMd);
                if (_combatQueue.Count >= MaxQueuedEvents) _combatQueue.TryDequeue(out _);
                _combatQueue.Enqueue(new CombatRecord
                {
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
            while (_diceQueue.TryDequeue(out var dr))
            {
                if (!IsMatchingEventStamp(dr.MatchRoomId, dr.MatchStartTime, dr.MatchGeneration)) continue;
                if (dr.OwnerId <= 0) continue;
                if (!_pendingDiceSeqsByOwner.TryGetValue(dr.OwnerId, out var seqs))
                {
                    seqs = new List<int>(4);
                    _pendingDiceSeqsByOwner[dr.OwnerId] = seqs;
                }
                if (seqs.Count >= 16) seqs.RemoveAt(0);
                seqs.Add(dr.DiceSeq);
            }
        }

        private bool IsEarlierMonsterPriority(long incomingId, long previousId)
        {
            if (incomingId <= 0 || previousId <= 0 || incomingId == previousId) return false;
            if (!_memories.TryGetValue(incomingId, out var curMem) || !_memories.TryGetValue(previousId, out var prevMem))
                return false;
            int wa = curMem.LastActionWeight > 0 ? curMem.LastActionWeight : int.MaxValue;
            int wb = prevMem.LastActionWeight > 0 ? prevMem.LastActionWeight : int.MaxValue;
            if (wa != wb) return wa < wb;
            return curMem.SpawnSequence > 0 && prevMem.SpawnSequence > 0 && curMem.SpawnSequence < prevMem.SpawnSequence;
        }

        public bool ObserveTurnBoundary(BattleLogic battle)
        {
            bool matchChanged = EnsureSameMatch();
            // Drain exact target-bound 1040.Place records even while rendering is paused so they
            // remain latched until board validation, tile confirmation, superseding Place, death, or reset.
            DrainPlaceQueue();
            DrainDiceQueue();
            if (battle == null) return matchChanged;

            var currentActor = SafeGetCurrentPlayer(battle);
            bool hasCurrentActor = currentActor != null && currentActor.player != null && currentActor.player.Id != 0;
            long curActorId = hasCurrentActor ? currentActor.player.Id : 0;
            int curRound = TryReadCurrentRound();
            long prevRawActorId = _roundTracker.ActiveActorId;

            List<int> ownedRollSeqs = null;
            int ownedNewRolls = 0;
            if (hasCurrentActor && _pendingDiceSeqsByOwner.TryGetValue(curActorId, out var bufferedSeqs) && bufferedSeqs.Count > 0)
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
                // Regressing round sample: preserve independently stamped dice rolls for the next current observation.
                return matchChanged;
            }

            // Consume owner roll sequences only after the observation is accepted.
            int latestConsumedOwnerRollSeq = 0;
            if (ownedRollSeqs != null)
            {
                latestConsumedOwnerRollSeq = ownedRollSeqs[ownedRollSeqs.Count - 1];
                _pendingDiceSeqsByOwner.Remove(curActorId);
            }

            // Expire any stale unconsumed rolls from a superseded outgoing actor occurrence so they
            // cannot fabricate an extra turn the next time that actor becomes active.
            if (prevRawActorId > 0 && prevRawActorId != _roundTracker.ActiveActorId)
            {
                _pendingDiceSeqsByOwner.Remove(prevRawActorId);
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
                    int initLand = SafeLandId(currentActor, out bool initVisual);
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
                int nowLand = SafeLandId(currentActor, out bool nowFromVisual);
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
                        // First visual confirmation after a protocol-only observation.
                        if (nowLand == _activeActorLastSeenLandId)
                        {
                            _activeActorLastSeenFromVisual = true;
                        }
                        else if (!_activeActorSteppedThisTurn && !_activeActorStartFromVisual && _activeActorStartLandId == _activeActorLastSeenLandId)
                        {
                            // Anchor to confirmed visual start tile rather than treating protocol-ahead vs visual as a step.
                            _activeActorStartLandId = nowLand;
                            _activeActorStartFromVisual = true;
                            _activeActorLastSeenLandId = nowLand;
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

            return matchChanged;
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
            out bool matchChanged,
            out bool snapshotValid,
            out bool playersAuthoritative,
            out bool playerPositionsReady)
        {
            monsters = new List<LiveMonsterInfo>();
            players = new List<TrackedPlayer>();
            snapshotValid = false;
            playersAuthoritative = false;
            playerPositionsReady = false;

            matchChanged = ObserveTurnBoundary(battle);
            // Return immediately on a match switch BEFORE draining queues or interpreting any tile against the old board.
            if (matchChanged || board == null || battle == null) return;

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
            var seenMonsterIds = new HashSet<long>();
            var conflictingMonsterIds = new HashSet<long>();

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
                if (uid > 0) presentUnitIds.Add(uid);

                CharacterType cType = CharacterType.None;
                try { cType = pd.characterType; } catch { rosterAuthoritative = false; heroesReadable = false; continue; }

                if (cType == CharacterType.None)
                {
                    // Unknown/uninitialized characterType on an existing entry invalidates authoritative absence checks.
                    rosterAuthoritative = false;
                    heroesReadable = false;
                    continue;
                }

                if (cType == CharacterType.Monster)
                {
                    if (uid <= 0 || !seenMonsterIds.Add(uid))
                    {
                        if (uid > 0) conflictingMonsterIds.Add(uid);
                        rosterAuthoritative = false;
                    }
                    continue;
                }

                if (cType == CharacterType.Hero)
                {
                    // Reject sentinel ID <= 0 and duplicate hero entries before updating membership baselines.
                    if (uid <= 0 || !seenPlayerIds.Add(uid))
                    {
                        heroesReadable = false;
                        rosterAuthoritative = false;
                        continue;
                    }

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
                if (_lastAcceptedHeroIdToPlayerId.Count > 0)
                {
                    heroIdToPlayerId = _lastAcceptedHeroIdToPlayerId;
                    anyPlayerHasTrueCulprit = _lastAcceptedAnyPlayerHasTrueCulprit;
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
                if (mid <= 0 || conflictingMonsterIds.Contains(mid) || snapshotNameLookup.ContainsKey(mid)) continue;
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
                if (mid <= 0 || conflictingMonsterIds.Contains(mid))
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

                int landId = SafeLandId(pd, out bool landFromVisual);
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
                int actionWeight = knownWeightOpt.HasValue ? knownWeightOpt.Value : (mem.HasConfirmedActionWeight ? mem.LastActionWeight : 0);
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
                                leftPreviousTile: false, isInitialBoardObservation: true, playerPositions, allUnits);
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

                        // Only consume Places and advance movement sequence on confirmed visual arrival (or when no prior
                        // visual tile exists yet) so protocol-ahead NodeId during brief CharacterInst disappearance never
                        // prematurely consumes fork combat/Place or looks like a backward move when the model returns.
                        bool canAdvanceTileState = visualConfirmed || mem.LastVisualLandId < 0;
                        if (canAdvanceTileState && mem.LastLandId != landId)
                        {
                            movedSinceLastSnapshot = true;
                            mem.MoveSequence++;
                            int prevLandId = mem.LastLandId;
                            bool isMonsterOwnTurn = (curActorId == mid);
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
                            mem.ExplicitFrontNodeIds = null;
                            mem.ExplicitFrontLandId = -1;
                            mem.ExplicitFrontFromLandId = -1;

                            var exactLookup = TryConsumeExactPlaceDisplacement(
                                board, mid, mem, prevLandId, landId, fromLandId, serverNodeId,
                                leftPreviousTile: true, isInitialBoardObservation: false, playerPositions, allUnits);
                            if (exactLookup == ExactPlaceLookup.NotFound && (!isMonsterOwnTurn || movedNonAdjacently))
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

                            mem.LastLandId = landId;
                            mem.LastFromLandId = fromLandId;
                        }
                        else if (canAdvanceTileState && _pendingPlaceByMonster.ContainsKey(mid))
                        {
                            var sameTileLookup = TryConsumeExactPlaceDisplacement(
                                board, mid, mem, mem.LastLandId, landId, fromLandId, serverNodeId,
                                leftPreviousTile: false, isInitialBoardObservation: false, playerPositions, allUnits);
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
                        else if (canAdvanceTileState && mem.LastFromLandId != fromLandId)
                        {
                            mem.ExplicitFrontNodeIds = null;
                            mem.ExplicitFrontLandId = -1;
                            mem.ExplicitFrontFromLandId = -1;
                            mem.LastFromLandId = fromLandId;
                        }

                        // Re-evaluate a short-lived unmatched off-turn move when a late SkillUsed cast arrives.
                        if (canAdvanceTileState &&
                            mem.UnmatchedOffTurnMoveSeq == mem.MoveSequence &&
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

                        // Always latch LastVisualLandId on every confirmed visual sample (including stationary ones
                        // with no pending Place) so a subsequent protocol-only change never consumes Places.
                        if (visualConfirmed)
                        {
                            mem.LastVisualLandId = landId;
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
                        mem.BossTargetAssignmentVersion++;
                        if (mem.EffectiveServerBossTargetId != 0) mem.InferredBossTargetId = 0;
                        mem.PendingServerBossTargetId = 0;
                        mem.HasPendingServerBossTarget = false;
                    }

                    if (serverBossTargetOpt.HasValue)
                    {
                        long sv = serverBossTargetOpt.Value;
                        // Compare against the raw server observation (NEVER the locally zeroed effective target)
                        // so an unchanged server value cannot restore a cleared target, while any real transition
                        // (including P2 -> 0) replaces a pending replacement cleanly!
                        bool serverTransitioned = !mem.HasLastRawServerBossTarget || sv != mem.LastRawServerBossTargetId;
                        mem.HasLastRawServerBossTarget = true;
                        mem.LastRawServerBossTargetId = sv;

                        if (serverTransitioned)
                        {
                            if (mem.BossTargetClearedTurnSequence != turnSeq)
                            {
                                mem.EffectiveServerBossTargetId = sv;
                                mem.BossTargetAssignedTurnSeq = turnSeq;
                                mem.BossTargetAssignmentVersion++;
                                if (sv != 0) mem.InferredBossTargetId = 0;
                                mem.PendingServerBossTargetId = 0;
                                mem.HasPendingServerBossTarget = false;
                            }
                            else
                            {
                                mem.PendingServerBossTargetId = sv;
                                mem.HasPendingServerBossTarget = true;
                            }
                        }
                    }
                }

                // Evaluate retained per-monster combat records using tri-state turn matching (Pending/Matched/Superseded),
                // idempotent versioned boss-lock clearance, layout revision checks, and movement-occurrence validation.
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
                            combatEvt.BossLockEvaluated = true;
                            combatList[ci] = combatEvt;

                            // Deduplicate interleaved/repeated FightResolved + BattleUpdate deliveries over recent window.
                            bool isDuplicateClear = false;
                            for (int ri = 0; ri < mem.RecentClearedBossFights.Count; ri++)
                            {
                                var rc = mem.RecentClearedBossFights[ri];
                                if (rc.OpponentId == combatEvt.OpponentId && rc.TurnSeq == fightTurnSeq)
                                {
                                    isDuplicateClear = true;
                                    break;
                                }
                            }
                            bool opponentHadMarkAtCombat =
                                (heroId == 1018 && combatEvt.OpponentHadProblemStudent) ||
                                (heroId == 1047 && combatEvt.OpponentHadMandarinDuck);
                            bool matchedBossTarget =
                                !isDuplicateClear &&
                                (combatEvt.OpponentId == preUpdateEffectiveBossTargetId ||
                                 combatEvt.OpponentId == mem.EffectiveBossTargetId ||
                                 opponentHadMarkAtCombat ||
                                 DidFightMarkedTarget(heroId, combatEvt.OpponentId, players));

                            if (matchedBossTarget)
                            {
                                if (mem.RecentClearedBossFights.Count >= 8) mem.RecentClearedBossFights.RemoveAt(0);
                                mem.RecentClearedBossFights.Add((combatEvt.OpponentId, fightTurnSeq));

                                // A fight may clear ONLY an assignment that existed when the fight occurred
                                // (protecting any newer turn assignment — even if it reacquired the same opponent ID!).
                                bool assignmentIsNewerThanFight =
                                    mem.BossTargetAssignedTurnSeq > fightTurnSeq &&
                                    mem.EffectiveServerBossTargetId != 0;

                                if (mem.HasPendingServerBossTarget &&
                                    mem.PendingServerBossTargetId == combatEvt.OpponentId &&
                                    turnMatch == EventTurnMatch.Matched)
                                {
                                    mem.PendingServerBossTargetId = 0;
                                    mem.HasPendingServerBossTarget = false;
                                }

                                if (mem.InferredBossTargetId == combatEvt.OpponentId && mem.BossTargetAssignedTurnSeq <= fightTurnSeq)
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

                        int actualCombatTile = combatEvt.LandId >= 0
                            ? combatEvt.LandId
                            : ((!isNew && !movedSinceLastSnapshot) ? landId : -1);
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

                        // Invalidate tile marker once the monster has moved past this event occurrence so it can
                        // never attach to a later revisit of the same tile.
                        bool movedPastCombat =
                            combatEvt.LandId < 0 ||
                            (combatEvt.CreatedMoveSeq >= 0 &&
                             ((mem.MoveSequence > combatEvt.CreatedMoveSeq && combatEvt.CreatedLandId == combatEvt.LandId) ||
                              mem.MoveSequence - combatEvt.CreatedMoveSeq > 1)) ||
                            (combatEvt.PlaceSeqAtEvent > 0 && mem.LastAppliedPlaceSeq > combatEvt.PlaceSeqAtEvent);
                        if (movedPastCombat)
                        {
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

                        int actualStealTile = stealEvt.LandId >= 0
                            ? stealEvt.LandId
                            : ((!isNew && !movedSinceLastSnapshot) ? landId : -1);
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

                        bool movedPastSteal =
                            stealEvt.LandId < 0 ||
                            (stealEvt.CreatedMoveSeq >= 0 &&
                             ((mem.MoveSequence > stealEvt.CreatedMoveSeq && stealEvt.CreatedLandId == stealEvt.LandId) ||
                              mem.MoveSequence - stealEvt.CreatedMoveSeq > 1)) ||
                            (stealEvt.PlaceSeqAtEvent > 0 && mem.LastAppliedPlaceSeq > stealEvt.PlaceSeqAtEvent);
                        if (movedPastSteal)
                        {
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

                bool isTaunted = TryReadTauntTarget(pd, heroIdToPlayerId, out long tauntTargetId);
                bool requiresCrimeTarget = IsCrimeRestrictedMonster(pd, heroId, anyPlayerHasTrueCulprit);
                SafeHp(pd, out int hp, out int maxHp, out bool hasHp, out _);
                bool bossTargetClearedThisTurn = (curActorId == mid) && (mem.BossTargetClearedTurnSequence == turnSeq);

                if (knownWeightOpt.HasValue)
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
                if (onBoard && hasLiveChar)
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
                    CharacterRef = (onBoard && hasLiveChar) ? ch : null,
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
            _currentLayoutLandById.Clear();
            _awaitingPostLayoutHeroConfirmation = false;
            _retiredMonsterSec.Clear();
            _pendingDiceSeqsByOwner.Clear();
            _recentExactDisplacements.Clear();
            _pendingCombatByMonster.Clear();
            _pendingStealByMonster.Clear();
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

            // Partition queued Dice rolls by incoming match identity.
            while (_diceQueue.TryDequeue(out var dr))
            {
                if (dr.OwnerId <= 0) continue;
                if (!IsIncomingMatchRecord(dr.MatchRoomId, dr.MatchStartTime, dr.MatchGeneration)) continue;
                if (!_pendingDiceSeqsByOwner.TryGetValue(dr.OwnerId, out var seqs))
                {
                    seqs = new List<int>(4);
                    _pendingDiceSeqsByOwner[dr.OwnerId] = seqs;
                }
                if (seqs.Count >= 16) seqs.RemoveAt(0);
                seqs.Add(dr.DiceSeq);
            }
        }

        public bool EnsureSameMatch()
        {
            if (Players.TryGetMatch(out var info))
            {
                bool hadMatch = _matchRoomId != 0 || _matchStartTime != 0;
                if (_matchRoomId != info.RoomId || _matchStartTime != info.StartTime)
                {
                    ResetForNewMatch(info.RoomId, info.StartTime, confirmedMatchSwitch: hadMatch);
                    return hadMatch;
                }
            }
            return false;
        }

        private struct PendingUnitCombat
        {
            public bool IsAttacker;
            public int LandId;
            public long OpponentId;
            public bool OpponentHadProblemStudent;
            public bool OpponentHadMandarinDuck;
            public long ActiveActor;
            public long PublishedActorAtEvent;
            public int PublishedTurnSequenceAtEvent;
            public int DiceSeqAtEvent;
            public long PlaceSeqAtEvent;
            public int BoundTurnSequence;
            public int CreatedMoveSeq;
            public int CreatedLandId;
            public int Round;
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
            public int DiceSeqAtEvent;
            public long PlaceSeqAtEvent;
            public int BoundTurnSequence;
            public int CreatedMoveSeq;
            public int CreatedLandId;
            public int Round;
            public int LayoutGeneration;
        }

        private EventTurnMatch MatchEventTurn(
            long curActorId,
            long unitId,
            int curTurnSeq,
            long activeActorAtEvent,
            long publishedActorAtEvent,
            int publishedTurnSeqAtEvent,
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

            if (activeActorAtEvent != 0 && activeActorAtEvent != unitId)
                return EventTurnMatch.Superseded;

            int effectiveRound = _roundTracker.EffectiveRoundNumber;
            if (eventRound >= 0 && effectiveRound >= 0)
            {
                // Events ahead of the currently observed round are Pending (never prematurely discarded!).
                if (eventRound > effectiveRound)
                    return EventTurnMatch.Pending;

                if (eventRound < effectiveRound)
                {
                    // Allow a 1-round lagging event stamp ONLY when positively matched to the active turn
                    // (e.g. event stamped while Room.Round still read 7 right as provisional round 8 promoted).
                    bool matchesCurrentProvisionalTurn =
                        curActorId == unitId &&
                        (publishedTurnSeqAtEvent == curTurnSeq ||
                         (activeActorAtEvent == unitId && _currentTurnOwnerStartDiceSeq > 0 && diceSeqAtEvent >= _currentTurnOwnerStartDiceSeq));
                    if (!matchesCurrentProvisionalTurn)
                        return EventTurnMatch.Superseded;
                }
            }

            // Event fired 1 poll BEFORE ObserveTurnBoundary observed the actor switch to unitId: keep pending!
            if (curActorId != unitId)
            {
                if (activeActorAtEvent == unitId && curTurnSeq == publishedTurnSeqAtEvent)
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

            // Actor switch (A -> unitId): require explicit live-actor confirmation (activeActorAtEvent == unitId)
            // so an event with unknown live actor (0) can never hijack whichever defender becomes active next.
            if (activeActorAtEvent == unitId &&
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
                            if (Math.Abs(sk.TimestampSec - r.TimestampSec) > 2.5) continue;
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
                        IsAttacker = true,
                        LandId = c.AttackerLandId,
                        OpponentId = c.DefenderId,
                        OpponentHadProblemStudent = c.DefenderHadProblemStudent,
                        OpponentHadMandarinDuck = c.DefenderHadMandarinDuck,
                        ActiveActor = c.ActiveActorAtEvent,
                        PublishedActorAtEvent = c.PublishedActorAtEvent,
                        PublishedTurnSequenceAtEvent = c.PublishedTurnSequenceAtEvent,
                        DiceSeqAtEvent = c.DiceSeqAtEvent,
                        PlaceSeqAtEvent = c.PlaceSeqAtEvent,
                        BoundTurnSequence = -1,
                        CreatedMoveSeq = GetMoveSeq(c.AttackerId),
                        CreatedLandId = GetLastLand(c.AttackerId),
                        Round = c.Round,
                        LayoutGeneration = c.LayoutGeneration
                    });
                if (c.DefenderId != 0)
                    AddCombat(c.DefenderId, new PendingUnitCombat
                    {
                        IsAttacker = false,
                        LandId = c.DefenderLandId,
                        OpponentId = c.AttackerId,
                        OpponentHadProblemStudent = c.AttackerHadProblemStudent,
                        OpponentHadMandarinDuck = c.AttackerHadMandarinDuck,
                        ActiveActor = c.ActiveActorAtEvent,
                        PublishedActorAtEvent = c.PublishedActorAtEvent,
                        PublishedTurnSequenceAtEvent = c.PublishedTurnSequenceAtEvent,
                        DiceSeqAtEvent = c.DiceSeqAtEvent,
                        PlaceSeqAtEvent = c.PlaceSeqAtEvent,
                        BoundTurnSequence = -1,
                        CreatedMoveSeq = GetMoveSeq(c.DefenderId),
                        CreatedLandId = GetLastLand(c.DefenderId),
                        Round = c.Round,
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
                        DiceSeqAtEvent = ts.DiceSeqAtEvent,
                        PlaceSeqAtEvent = ts.PlaceSeqAtEvent,
                        BoundTurnSequence = -1,
                        CreatedMoveSeq = GetMoveSeq(ts.ThiefId),
                        CreatedLandId = GetLastLand(ts.ThiefId),
                        Round = ts.Round,
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
            IReadOnlyList<BattlePlayerData> allUnits)
        {
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

            // Determine the contiguous validated prefix [0..eligibleCount-1] for curLayoutGen.
            int eligibleCount = 0;
            while (eligibleCount < list.Count && list[eligibleCount].ValidatedForCurrentLayout)
            {
                eligibleCount++;
            }
            if (eligibleCount == 0) return ExactPlaceLookup.NotFound;

            bool HasPredecessorConflict(SkillDisplacementRecord r)
            {
                return liveFromLandId >= 0 &&
                       r.BackLandId >= 0 &&
                       board.Contains(r.BackLandId) &&
                       r.BackLandId != liveFromLandId;
            }

            int matchIdx = -1;
            if (!leftPreviousTile)
            {
                if (list[0].NewLandId >= 0 && list[0].NewLandId == newLandId && !HasPredecessorConflict(list[0]))
                {
                    matchIdx = 0;
                }
                else if (isInitialBoardObservation)
                {
                    for (int i = 0; i < eligibleCount; i++)
                    {
                        if (list[i].NewLandId != newLandId || HasPredecessorConflict(list[i])) continue;
                        if (liveFromLandId >= 0 && list[i].BackLandId == liveFromLandId)
                        {
                            matchIdx = i;
                            break;
                        }
                        if (serverNodeId == newLandId && i == eligibleCount - 1)
                        {
                            matchIdx = i;
                        }
                    }
                }
                else if (eligibleCount >= 2 && serverNodeId == newLandId && liveFromLandId >= 0)
                {
                    for (int i = 1; i < eligibleCount; i++)
                    {
                        if (list[i].NewLandId == newLandId && list[i].BackLandId == liveFromLandId)
                        {
                            matchIdx = i;
                            break;
                        }
                    }
                }
            }
            else
            {
                // Visual model confirmed moving to newLandId: confirmed predecessor conflicts take precedence
                // over destination-only matches so an old Place (e.g. A->B) can never attach to D->B!
                int firstCompatibleDestIdx = -1;
                for (int i = 0; i < eligibleCount; i++)
                {
                    if (list[i].NewLandId != newLandId) continue;
                    if (HasPredecessorConflict(list[i])) continue;
                    if (firstCompatibleDestIdx < 0) firstCompatibleDestIdx = i;
                    if (liveFromLandId >= 0 && list[i].BackLandId == liveFromLandId)
                    {
                        matchIdx = i;
                        break;
                    }
                }
                if (matchIdx < 0) matchIdx = firstCompatibleDestIdx;
            }

            if (matchIdx < 0)
            {
                if (leftPreviousTile && eligibleCount > 0)
                {
                    // Retire overtaken leading Places: either targeting oldLandId after stepping off it,
                    // OR targeting newLandId with a contradicting explicit predecessor (overtaken visit).
                    var head = list[0];
                    bool overtakenOldTile = oldLandId >= 0 && head.NewLandId == oldLandId;
                    bool overtakenConflictingDest = head.NewLandId == newLandId && HasPredecessorConflict(head);
                    if (overtakenOldTile || overtakenConflictingDest)
                    {
                        if (overtakenOldTile &&
                            IdentifyDisplacementSkill(head.SkillId, head.CasterPlayerId, allUnits, allowCasterHeroFallback: false) == SkillMoveKind.None)
                        {
                            ReleaseHeuristicClaim(mem, monsterId, oldLandId, mem.MoveSequence - 1);
                        }
                        list.RemoveAt(0);
                        if (list.Count == 0) _pendingPlaceByMonster.Remove(monsterId);
                    }
                }
                return ExactPlaceLookup.NotFound;
            }

            // Sequentially fold entries 0..matchIdx in exact arrival order so multi-event batches
            // (e.g. [A->F(30601), F->F(ordinary)] or [A->B, B->F(12801)]) match frame-by-frame sampling identically.
            int simLand = oldLandId;
            for (int i = 0; i <= matchIdx; i++)
            {
                var rec = list[i];
                if (rec.NewLandId < 0) continue;
                int nextLand = rec.NewLandId;
                bool hopChangedTile = (nextLand != simLand);
                if (rec.PlaceSeq > mem.LastAppliedPlaceSeq) mem.LastAppliedPlaceSeq = rec.PlaceSeq;
                if (hopChangedTile)
                {
                    // Advance MoveSequence for each additional folded tile-changing hop so combat/steal
                    // watermarks reflect actual hops taken rather than counting a multi-hop batch as 1.
                    if (i > 0) mem.MoveSequence++;
                    mem.JustSpawnedOnFork = false;
                    mem.JustFoughtOnFork = false;
                    mem.CombatForkLandId = -1;
                    mem.SkillMoveKind = SkillMoveKind.None;
                    mem.SkillMoveFromHeuristic = false;
                    mem.HeuristicSkillCast = null;
                    mem.SkillMoveLandId = -1;
                    mem.SkillMovePushDirId = -1;
                    mem.SkillMovePushFromId = -1;
                    mem.ExplicitFrontNodeIds = null;
                    mem.ExplicitFrontLandId = -1;
                    mem.ExplicitFrontFromLandId = -1;
                }

                int validRecBack = (rec.BackLandId >= 0 && rec.BackLandId != nextLand && board.Contains(rec.BackLandId))
                    ? rec.BackLandId
                    : -1;

                if (rec.FrontNodeIds != null && rec.FrontNodeIds.Length > 0)
                {
                    int boundFrom = (i == matchIdx)
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

                    // Deduplicate identical same-tile exact Lingling notifications BEFORE charging another cast!
                    bool duplicateSameTileLingling =
                        !hopChangedTile &&
                        kind == SkillMoveKind.LinglingPush &&
                        mem.SkillMoveKind == SkillMoveKind.LinglingPush &&
                        !mem.SkillMoveFromHeuristic &&
                        mem.SkillMoveLandId == nextLand &&
                        mem.SkillMovePushFromId >= 0 &&
                        (validRecBack < 0 || validRecBack == mem.SkillMovePushFromId);

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
                            if (hKind == kind && casterMatches)
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
                            if (!ConsumeRecentSkillCastForMonster(monsterId, kind, rec.CasterPlayerId, allUnits))
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
                        }
                        else
                        {
                            ApplyDisplacementTag(board, mem, kind, pushOrigin, nextLand, rec.CasterPlayerId, playerPositions, directIncomingPredecessor);
                            mem.SkillMoveFromHeuristic = false;
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
                    }
                }

                simLand = nextLand;
            }

            // Consume entries 0..matchIdx (retaining any newer ahead-of-visual entries after matchIdx).
            list.RemoveRange(0, matchIdx + 1);
            if (list.Count == 0) _pendingPlaceByMonster.Remove(monsterId);

            return (mem.SkillMoveKind != SkillMoveKind.None && mem.SkillMoveLandId == newLandId)
                ? ExactPlaceLookup.TaggedDisplacement
                : ExactPlaceLookup.KnownOtherCause;
        }

        private bool ConsumeRecentSkillCastForMonster(
            long monsterId,
            SkillMoveKind kind,
            long casterPlayerId,
            IReadOnlyList<BattlePlayerData> allUnits)
        {
            if (_recentSkills.Count == 0 || kind == SkillMoveKind.None) return false;

            // Consume at most ONE matching cast (earliest unapplied) so a second independent cast
            // of the same skill kind within the 2.5s window is never prematurely erased.
            for (int i = 0; i < _recentSkills.Count; i++)
            {
                var sk = _recentSkills[i];
                if (sk.AppliedMonsterIds.Contains(monsterId)) continue;
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
            PruneExpiredHeuristicClaims(mem, MonotonicSeconds(), Volatile.Read(ref _layoutGeneration));
            if (mem.HeuristicClaims.Count >= 4)
            {
                // Evict only the single oldest claim individually rather than clearing all still-live claims.
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

            object propObj = null;
            try { propObj = pd.Property; } catch { }
            if (propObj != null)
            {
                var prop = pd.Property;
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

            // Only consult fallback protocol Hero.Hp when live Property.HP was unavailable.
            try
            {
                var hero = pd.player != null ? pd.player.Hero : null;
                if (hero != null)
                {
                    if (hero.Hp > 0) return true;
                    if (hero.MaxHp > 0 && hero.Hp <= 0) return false;
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
                var prop = pd.Property;
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

                bool notSelect = false;
                try { if (prop != null && prop.NotSelect != null) notSelect = prop.NotSelect.Value; } catch { }
                if (!notSelect)
                {
                    try { if (p.Hero != null) notSelect = p.Hero.NotSelect; } catch { }
                }

                bool hasProblemStudent = false;
                bool hasMandarinDuck = false;
                bool hasUnchallengeableStealth = false;
                int crimeFromBuff = 0;

                try
                {
                    var buffs = Players.BuffsOf(p.Id);
                    if (buffs != null)
                    {
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
                            }
                        }
                    }
                }
                catch { }

                try
                {
                    if (p.Hero != null && p.Hero.Buffs != null)
                    {
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
                            }
                        }
                    }
                }
                catch { }

                int slot = 0;
                try
                {
                    slot = p.ChangeSlot >= 0 && p.ChangeSlot < 4 ? p.ChangeSlot : p.Slot;
                }
                catch
                {
                    try { slot = p.Slot; } catch { }
                }

                int landId = SafeAuthoritativeHeroLandId(pd, board, out posConfirmedOnLayout);
                if (dead) posConfirmedOnLayout = true;
                string nick = Players.SafeNick(pd) ?? ("P" + (slot + 1));
                int gold = SafeGold(pd);
                int crimeNum = Math.Max(SafeCrimeNum(pd), crimeFromBuff);

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
            object propObj = null;
            try { propObj = pd != null ? pd.Property : null; } catch { }
            if (propObj != null)
            {
                var prop = pd.Property;
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
            if (!hasHp || !hasMaxHp)
            {
                try
                {
                    var hero = pd != null && pd.player != null ? pd.player.Hero : null;
                    if (hero != null)
                    {
                        // Shared evidence rule: only accept protocol Hero.Hp as known when Hero.Hp > 0 or Hero.MaxHp > 0
                        // (never treating an uninitialized protocol Hero.Hp=0, MaxHp=0 as invented "HP 0/max").
                        if (!hasHp && (hero.Hp > 0 || hero.MaxHp > 0))
                        {
                            hp = hero.Hp;
                            hasHp = true;
                        }
                        if (!hasMaxHp && hero.MaxHp > 0)
                        {
                            maxHp = hero.MaxHp;
                            hasMaxHp = true;
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
                int seenKnownHeroes = 0;
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

                    if (cType == CharacterType.Hero && _knownPlayerIds.Contains(pd.player.Id))
                    {
                        seenKnownHeroes++;
                    }
                    else if (cType == CharacterType.Monster)
                    {
                        sawAnyMonster = true;
                    }

                    if (pd.player.Id != monsterId) continue;

                    if (TryCheckRosterMonsterAlive(pd, out bool isAlive))
                    {
                        return !isAlive;
                    }
                    return false;
                }

                // If absent from allUnits, confirm death ONLY when the roster sample is complete/authoritative
                // AND aligns with the tracker's retirement verdict (never returning dead on a partial/null-containing sample).
                if (!allEntriesReadable || seenKnownHeroes < _knownPlayerIds.Count) return false;
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
                if (pd.Property != null && pd.Property.gold != null) return pd.Property.gold.Value;
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

        private static int SafeCrimeNum(BattlePlayerData pd)
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
            return 0;
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
                if (UnityObject.IsAlive(pd.CharacterInst) && pd.CharacterInst.standLand != null)
                {
                    fromVisual = true;
                    return pd.CharacterInst.standLand.Id;
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
        /// Protocol-first authoritative tile definition verified against the current board/manager across layout
        /// transitions so stale old-layout <c>standLand</c> objects or unchanged overlapping protocol IDs never
        /// open post-layout readiness prematurely.
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
                    standLandId = sl.Id;
                    standLandVerifiedOnCurrentManager =
                        _currentLayoutLandById.Count > 0 &&
                        _currentLayoutLandById.TryGetValue(standLandId, out var expectedLand) &&
                        ReferenceEquals(sl, expectedLand);
                }
            }
            catch { }

            bool protoOnBoard = protoNode >= 0 && (board == null || board.Contains(protoNode));
            bool standOnBoard = standLandId >= 0 && standLandVerifiedOnCurrentManager && (board == null || board.Contains(standLandId));

            if (!_awaitingPostLayoutHeroConfirmation)
            {
                if (pid > 0 && protoNode >= 0) _lastHeroProtoNodeId[pid] = protoNode;
                if (protoOnBoard)
                {
                    confirmedOnCurrentLayout = true;
                    return protoNode;
                }
                if (standOnBoard)
                {
                    confirmedOnCurrentLayout = true;
                    return standLandId;
                }
                // If _currentLayoutLandById hasn't been populated yet (no manager captured), allow non-verified visual fallback only on initial board.
                if (_currentLayoutLandById.Count == 0 && standLandId >= 0 && (board == null || board.Contains(standLandId)))
                {
                    confirmedOnCurrentLayout = true;
                    return standLandId;
                }
                return -1;
            }

            // Post-layout confirmation mode: require positive evidence that this hero's coordinates belong
            // to the replacement board (already confirmed, live standLand object in replacement manager,
            // new-layout-only protocol NodeId, or changed protocol NodeId relative to pre-layout snapshot).
            bool hasNewLayoutEvidence =
                (pid > 0 && _heroConfirmedOnCurrentLayout.Contains(pid)) ||
                standOnBoard ||
                (protoOnBoard &&
                 (_previousBoardBeforeTopologyChange == null ||
                  !_previousBoardBeforeTopologyChange.Contains(protoNode) ||
                  (pid > 0 && _preLayoutHeroProtoNodeId.TryGetValue(pid, out int oldProto) && protoNode != oldProto)));

            if (!hasNewLayoutEvidence)
            {
                return -1;
            }

            if (pid > 0)
            {
                _heroConfirmedOnCurrentLayout.Add(pid);
                if (protoNode >= 0) _lastHeroProtoNodeId[pid] = protoNode;
            }

            if (protoOnBoard)
            {
                confirmedOnCurrentLayout = true;
                return protoNode;
            }
            if (standOnBoard)
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
