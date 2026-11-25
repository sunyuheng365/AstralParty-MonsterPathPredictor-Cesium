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
        private sealed class MonsterMemory
        {
            public int SpawnSequence;
            public int NameOrdinal;
            public string NameBucket = string.Empty;
            public bool NameBucketIsProvisional;
            public bool HasBeenObservedOnBoard;
            public int MissingAuthoritativeFrames;
            public bool? LastServerActionEnd;
            public int LastServerActionEndRound = int.MinValue;
            public int LastLandId = -1;
            public int LastFromLandId = -1;
            public bool JustSpawnedOnFork;
            public bool JustFoughtOnFork;
            public int CombatForkLandId = -1;
            public int CombatTurnSequence = -1;
            public int BossTargetLockTurnSequence = -1;
            public int BossTargetClearedTurnSequence = -1;
            public bool HasServerBossTarget;
            public long LastServerBossTargetId;
            public long LastSeenBossTargetId;
            public bool HasPendingServerBossTarget;
            public long PendingServerBossTargetId;
            public int StolenTurnSequence = -1;
            public SkillMoveKind SkillMoveKind = SkillMoveKind.None;
            public bool SkillMoveFromHeuristic;
            public SkillCastRecord HeuristicSkillCast;
            public int SkillMoveLandId = -1;
            public int SkillMovePushDirId = -1;
            public int SkillMovePushFromId = -1;
            public int[] ExplicitFrontNodeIds;
            public int ExplicitFrontLandId = -1;
            public int ExplicitFrontFromLandId = -1;
        }

        private sealed class DiceStamp
        {
            public readonly int Epoch;
            public readonly long OwnerId;
            public DiceStamp(int epoch, long ownerId)
            {
                Epoch = epoch;
                OwnerId = ownerId;
            }
        }

        private sealed class TurnStamp
        {
            public readonly long ActiveActorId;
            public readonly int TurnSequence;
            public TurnStamp(long activeActorId, int turnSequence)
            {
                ActiveActorId = activeActorId;
                TurnSequence = turnSequence;
            }
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
            public int Round;
            public long MatchRoomId;
            public long MatchStartTime;
            public int MatchGeneration;
            public int LayoutGeneration;
        }

        private struct ThiefStealRecord
        {
            public long ThiefId;
            public int LandId;
            public long PublishedActorAtEvent;
            public int PublishedTurnSequenceAtEvent;
            public int Round;
            public long MatchRoomId;
            public long MatchStartTime;
            public int MatchGeneration;
            public int LayoutGeneration;
        }

        private const int MaxQueuedEvents = 256;
        private const double RetiredMonsterGraceSec = 30.0;

        private readonly ConcurrentQueue<SkillCastRecord> _skillQueue = new ConcurrentQueue<SkillCastRecord>();
        private readonly ConcurrentQueue<SkillDisplacementRecord> _placeQueue = new ConcurrentQueue<SkillDisplacementRecord>();
        private readonly ConcurrentQueue<CombatRecord> _combatQueue = new ConcurrentQueue<CombatRecord>();
        private readonly ConcurrentQueue<ThiefStealRecord> _thiefStealQueue = new ConcurrentQueue<ThiefStealRecord>();

        private enum ExactPlaceLookup
        {
            NotFound = 0,
            KnownOtherCause = 1,
            TaggedDisplacement = 2
        }

        private readonly Dictionary<long, MonsterMemory> _memories = new Dictionary<long, MonsterMemory>();
        private readonly Dictionary<string, int> _nextNameOrdinalByName = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<string> _disambiguatedNames = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<long> _rosterAliveMonsterIds = new HashSet<long>();
        private readonly HashSet<long> _knownPlayerIds = new HashSet<long>();
        private readonly Dictionary<long, bool> _lastKnownPlayerAlive = new Dictionary<long, bool>();
        private readonly Dictionary<long, double> _retiredMonsterSec = new Dictionary<long, double>();
        private readonly ActionRoundTracker _roundTracker = new ActionRoundTracker();
        private readonly List<SkillCastRecord> _recentSkills = new List<SkillCastRecord>();
        private readonly Dictionary<long, List<SkillDisplacementRecord>> _pendingPlaceByMonster = new Dictionary<long, List<SkillDisplacementRecord>>();
        private readonly Dictionary<long, List<PendingUnitCombat>> _pendingCombatByMonster = new Dictionary<long, List<PendingUnitCombat>>();
        private readonly Dictionary<long, List<PendingThiefSteal>> _pendingStealByMonster = new Dictionary<long, List<PendingThiefSteal>>();
        private readonly Dictionary<int, long> _lastAcceptedHeroIdToPlayerId = new Dictionary<int, long>();

        private bool _lastAcceptedAnyPlayerHasTrueCulprit;
        private int _knownPlayerCount;
        private int _nextSpawnSequence = 1;
        private long _localPlayerId;
        private int _activeActorStartLandId = -1;
        private int _activeActorLastSeenLandId = -1;
        private bool _turnStartLayoutInvalidated;
        private bool _activeActorSteppedThisTurn;
        private DiceStamp _latestDice = new DiceStamp(0, 0);
        private TurnStamp _publishedTurn = new TurnStamp(0, 0);
        private int _placeBoardValidationPending;
        private int _layoutGeneration;

        private long _matchRoomId;
        private long _matchStartTime;
        private int _matchGeneration;
        private bool _hooked;
        private volatile bool _acceptEvents;

        private static double MonotonicSeconds()
        {
            return Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        }

        public bool ConsumePendingPlaceBoardValidation()
        {
            return Interlocked.Exchange(ref _placeBoardValidationPending, 0) != 0;
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
                while (_skillQueue.TryDequeue(out _)) { }
                while (_placeQueue.TryDequeue(out _)) { }
                while (_combatQueue.TryDequeue(out _)) { }
                while (_thiefStealQueue.TryDequeue(out _)) { }
            }
        }

        public long LocalPlayerId => _localPlayerId;

        public void Reset()
        {
            _memories.Clear();
            _nextNameOrdinalByName.Clear();
            _disambiguatedNames.Clear();
            _rosterAliveMonsterIds.Clear();
            _knownPlayerIds.Clear();
            _lastKnownPlayerAlive.Clear();
            _retiredMonsterSec.Clear();
            _recentSkills.Clear();
            _pendingPlaceByMonster.Clear();
            _pendingCombatByMonster.Clear();
            _pendingStealByMonster.Clear();
            _lastAcceptedHeroIdToPlayerId.Clear();
            _lastAcceptedAnyPlayerHasTrueCulprit = false;
            _knownPlayerCount = 0;
            while (_skillQueue.TryDequeue(out _)) { }
            while (_placeQueue.TryDequeue(out _)) { }
            while (_combatQueue.TryDequeue(out _)) { }
            while (_thiefStealQueue.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _placeBoardValidationPending, 0);
            _nextSpawnSequence = 1;
            _localPlayerId = 0;
            _activeActorStartLandId = -1;
            _activeActorLastSeenLandId = -1;
            _turnStartLayoutInvalidated = false;
            _activeActorSteppedThisTurn = false;
            var dice = Volatile.Read(ref _latestDice);
            _roundTracker.Reset(dice.Epoch);
            Volatile.Write(ref _publishedTurn, new TurnStamp(_roundTracker.ActiveActorId, _roundTracker.TurnSequence));
            _matchRoomId = 0;
            _matchStartTime = 0;
            Interlocked.Increment(ref _layoutGeneration);
            Interlocked.Increment(ref _matchGeneration);
        }

        /// <summary>
        /// Clears old-layout turn-start anchor and tile-scoped memory when the board topology switches mid-match,
        /// while preserving active actor turn identity, turn-level progress, and very recent Place records
        /// attributable to <paramref name="newBoard"/>.
        /// </summary>
        public void NotifyBoardTopologyChanged(BoardGraph newBoard = null)
        {
            int newLayoutGen = Interlocked.Increment(ref _layoutGeneration);
            _activeActorStartLandId = -1;
            _activeActorLastSeenLandId = -1;
            _turnStartLayoutInvalidated = true;
            _recentSkills.Clear();

            // Drain any pending places from the queue first, then retain ONLY very recent records
            // (<= 1.0s) whose destination and optional back tile are valid on the accepted newBoard.
            DrainPlaceQueue();
            double nowSec = MonotonicSeconds();
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
                        bool recent = (nowSec - rec.TimestampSec) <= 1.0;
                        bool validOnNew = newBoard.Contains(rec.NewLandId) &&
                                          (rec.BackLandId < 0 || newBoard.Contains(rec.BackLandId));
                        if (recent && validOnNew)
                        {
                            rec.LayoutGeneration = newLayoutGen;
                        }
                        else
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
                // Rebaseline observed land IDs so remapped tiles in the new layout are not misinterpreted as skill moves.
                mem.LastLandId = -1;
                mem.LastFromLandId = -1;
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
        }

        public bool IsMonsterInAliveRoster(long monsterId)
        {
            return monsterId != 0 && _rosterAliveMonsterIds.Contains(monsterId);
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
            while (true)
            {
                var prev = Volatile.Read(ref _latestDice);
                var next = new DiceStamp(prev.Epoch + 1, playerId);
                if (ReferenceEquals(Interlocked.CompareExchange(ref _latestDice, next, prev), prev))
                    break;
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
            bool ownerIsThief = TryIsUnitThief(ownerId);
            var pubTurn = Volatile.Read(ref _publishedTurn);
            int curRound = TryReadCurrentRound();
            int causeId = attr.Cause != null ? (int)attr.Cause.Id : 0;
            TryStampCurrentMatch(out long matchRoomId, out long matchStartTime);
            int matchGen = Volatile.Read(ref _matchGeneration);
            int layoutGen = Volatile.Read(ref _layoutGeneration);
            double nowSec = MonotonicSeconds();

            for (int i = 0; i < attr.EffectDatas.Count; i++)
            {
                var eff = attr.EffectDatas[i];
                if (eff == null) continue;

                // Protocol-authoritative Thief gold steal (cmd 1040 ONLY; excludes 1096 pass-by aura effects)
                if (ownerIsThief && eff.Gold != null && eff.Gold.ChangeGold < 0 && eff.PlayerId != ownerId && TryIsUnitHero(eff.PlayerId))
                {
                    if (_thiefStealQueue.Count >= MaxQueuedEvents) _thiefStealQueue.TryDequeue(out _);
                    _thiefStealQueue.Enqueue(new ThiefStealRecord
                    {
                        ThiefId = ownerId,
                        LandId = TryReadLiveUnitLandId(ownerId),
                        PublishedActorAtEvent = pubTurn.ActiveActorId,
                        PublishedTurnSequenceAtEvent = pubTurn.TurnSequence,
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
                    if (_placeQueue.Count >= MaxQueuedEvents) _placeQueue.TryDequeue(out _);
                    _placeQueue.Enqueue(new SkillDisplacementRecord
                    {
                        TargetMonsterId = eff.PlayerId,
                        CasterPlayerId = ownerId,
                        SkillId = causeId,
                        NewLandId = eff.Place.Place.NodeId,
                        BackLandId = eff.Place.Place.BackNodeId,
                        FrontNodeIds = placeFrontIds,
                        MatchRoomId = matchRoomId,
                        MatchStartTime = matchStartTime,
                        MatchGeneration = matchGen,
                        LayoutGeneration = layoutGen,
                        TimestampSec = nowSec
                    });
                    Interlocked.Exchange(ref _placeBoardValidationPending, 1);
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
                PublishedActorAtEvent = pubTurn.ActiveActorId,
                PublishedTurnSequenceAtEvent = pubTurn.TurnSequence,
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
                    PublishedActorAtEvent = pubTurn.ActiveActorId,
                    PublishedTurnSequenceAtEvent = pubTurn.TurnSequence,
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
        public bool ObserveTurnBoundary(BattleLogic battle)
        {
            bool matchChanged = EnsureSameMatch();
            // Drain exact target-bound 1040.Place records even while rendering is paused so they
            // remain latched until tile confirmation, superseding Place, monster death, or match reset.
            DrainPlaceQueue();
            if (battle == null) return matchChanged;

            var currentActor = SafeGetCurrentPlayer(battle);
            bool hasCurrentActor = currentActor != null && currentActor.player != null && currentActor.player.Id != 0;
            long curActorId = hasCurrentActor ? currentActor.player.Id : 0;
            var dice = Volatile.Read(ref _latestDice);
            int curRound = TryReadCurrentRound();

            var boundary = _roundTracker.Observe(
                hasCurrentActor,
                curActorId,
                curRound,
                dice.Epoch,
                dice.OwnerId,
                id => _rosterAliveMonsterIds.Contains(id),
                TryIsUnitHero);

            Volatile.Write(ref _publishedTurn, new TurnStamp(_roundTracker.ActiveActorId, _roundTracker.TurnSequence));

            if (boundary.ShouldResetMovementState)
            {
                _turnStartLayoutInvalidated = false;
                _activeActorStartLandId = hasCurrentActor ? SafeLandId(currentActor) : -1;
                _activeActorLastSeenLandId = _activeActorStartLandId;
                _activeActorSteppedThisTurn = false;
            }
            else if (hasCurrentActor && curActorId == _roundTracker.ActiveActorId)
            {
                int nowLand = SafeLandId(currentActor);
                if (nowLand >= 0)
                {
                    if (!_turnStartLayoutInvalidated && _activeActorStartLandId < 0)
                    {
                        _activeActorStartLandId = nowLand;
                    }
                    if (_activeActorLastSeenLandId < 0)
                    {
                        _activeActorLastSeenLandId = nowLand;
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
            foreach (var pair in manager.NodeDict)
            {
                var land = pair.Value;
                if (!UnityObject.IsAlive(land))
                {
                    complete = false;
                    continue;
                }

                Vector3 pos;
                bool disabled;
                int[] neighborIds;
                try
                {
                    pos = TransformService.GetPosition(UnityObject.GetTransform(land));
                    var nodeInfo = land.GetNodeInfo();
                    disabled = land.Disable;
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
                nodes.Add(new LandNodeSnapshot(pair.Key, neighborIds, disabled, pos.x, pos.y, pos.z));
            }

            // Validate graph closure: every non-sentinel neighbor ID (>= 0) on every active node must exist
            // in the scanned node set so a half-populated rebuilding board never passes as complete.
            if (complete && nodes.Count > 0)
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    var node = nodes[i];
                    var nbs = node.NeighborIds;
                    for (int j = 0; j < nbs.Length; j++)
                    {
                        int nid = nbs[j];
                        if (nid >= 0 && !presentNodeIds.Contains(nid))
                        {
                            complete = false;
                            break;
                        }
                    }
                    if (!complete) break;
                }
            }

            isComplete = complete && nodes.Count > 0;
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
            out bool playersAuthoritative)
        {
            monsters = new List<LiveMonsterInfo>();
            players = new List<TrackedPlayer>();
            snapshotValid = false;
            playersAuthoritative = false;

            matchChanged = ObserveTurnBoundary(battle);
            // Return immediately on a match switch BEFORE draining queues or interpreting any tile against the old board.
            if (matchChanged || board == null || battle == null) return;

            var allUnits = Players.All();
            if (allUnits == null || allUnits.Count == 0) return;
            snapshotValid = true;

            DrainEventQueues();

            long curActorId = _roundTracker.ActiveActorId;
            int turnSeq = _roundTracker.TurnSequence;
            int currentRound = TryReadCurrentRound();
            int effectiveRound = currentRound >= 0 ? currentRound : _roundTracker.LastObservedRound;
            int curLayoutGen = Volatile.Read(ref _layoutGeneration);

            var playerPositions = new Dictionary<long, int>();
            var heroIdToPlayerId = new Dictionary<int, long>();
            var seenPlayerIds = new HashSet<long>();
            var rosterAliveIds = new HashSet<long>();
            var explicitDeadMonsterIds = new HashSet<long>();
            bool anyPlayerHasTrueCulprit = false;
            bool rosterAuthoritative = true;
            bool heroesReadable = true;

            for (int i = 0; i < allUnits.Count; i++)
            {
                var pd = allUnits[i];
                if (pd == null || pd.player == null)
                {
                    rosterAuthoritative = false;
                    heroesReadable = false;
                    continue;
                }

                CharacterType cType = CharacterType.None;
                try { cType = pd.characterType; } catch { rosterAuthoritative = false; heroesReadable = false; continue; }

                if (cType == CharacterType.Hero)
                {
                    var tp = BuildTrackedPlayer(pd, settings, out bool isTrueCulprit);
                    if (isTrueCulprit) anyPlayerHasTrueCulprit = true;
                    if (tp != null)
                    {
                        players.Add(tp);
                        seenPlayerIds.Add(tp.PlayerId);
                        if (tp.LandId >= 0) playerPositions[tp.PlayerId] = tp.LandId;
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
                                   players.Count > 0 &&
                                   players.Count >= _knownPlayerCount &&
                                   allKnownPlayersPresent;

            if (playersAuthoritative)
            {
                if (players.Count > _knownPlayerCount) _knownPlayerCount = players.Count;
                foreach (long pid in seenPlayerIds) _knownPlayerIds.Add(pid);
                _lastAcceptedAnyPlayerHasTrueCulprit = anyPlayerHasTrueCulprit;
                _lastAcceptedHeroIdToPlayerId.Clear();
                foreach (var kv in heroIdToPlayerId) _lastAcceptedHeroIdToPlayerId[kv.Key] = kv.Value;
            }
            else
            {
                // A partial player subset implies allUnits itself is incomplete: never treat absent monsters as dead.
                if (_knownPlayerCount > 0) rosterAuthoritative = false;
                if (_lastAcceptedHeroIdToPlayerId.Count > 0)
                {
                    heroIdToPlayerId = _lastAcceptedHeroIdToPlayerId;
                    anyPlayerHasTrueCulprit = _lastAcceptedAnyPlayerHasTrueCulprit;
                }
            }

            // Pre-assign chronological SpawnSequence & NameOrdinal for newly discovered monsters in this snapshot
            // using wraparound-safe server MonsterId ordering rather than arbitrary Players.All() enumeration order.
            List<long> newlyDiscoveredIds = null;
            Dictionary<long, string> newlyDiscoveredNames = null;
            for (int i = 0; i < allUnits.Count; i++)
            {
                var pd = allUnits[i];
                if (pd == null || pd.player == null) continue;
                CharacterType cType = CharacterType.None;
                try { cType = pd.characterType; } catch { continue; }
                if (cType != CharacterType.Monster) continue;
                if (!TryCheckRosterMonsterAlive(pd, out bool alive) || !alive) continue;
                long mid = pd.player.Id;
                if (!_memories.ContainsKey(mid))
                {
                    if (newlyDiscoveredIds == null)
                    {
                        newlyDiscoveredIds = new List<long>(4);
                        newlyDiscoveredNames = new Dictionary<long, string>(4);
                    }
                    if (!newlyDiscoveredNames.ContainsKey(mid))
                    {
                        newlyDiscoveredIds.Add(mid);
                        newlyDiscoveredNames[mid] = ResolveMonsterName(pd, SafeHeroId(pd), out _);
                    }
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
                    batchNameOrd[nid] = AllocateNameOrdinal(newlyDiscoveredNames[nid]);
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

                int heroId = SafeHeroId(pd);
                string monsterName = ResolveMonsterName(pd, heroId, out bool nameIsProvisional);
                bool? serverActionEnd = SafeActionEnd(pd);

                // Ensure chronological SpawnSequence and collision-free per-name ordinal BEFORE board/model checks.
                bool isNew = !_memories.TryGetValue(mid, out var mem);
                if (isNew)
                {
                    int nextOrd = (batchNameOrd != null && batchNameOrd.TryGetValue(mid, out int ord))
                        ? ord
                        : AllocateNameOrdinal(monsterName);
                    int assignedSeq = (batchSpawnSeq != null && batchSpawnSeq.TryGetValue(mid, out int seq))
                        ? seq
                        : _nextSpawnSequence++;
                    mem = new MonsterMemory
                    {
                        SpawnSequence = assignedSeq,
                        NameOrdinal = nextOrd,
                        NameBucket = monsterName,
                        NameBucketIsProvisional = nameIsProvisional,
                        LastServerActionEnd = serverActionEnd,
                        LastServerActionEndRound = effectiveRound
                    };
                    _memories[mid] = mem;
                    if (serverActionEnd == true && curActorId != mid)
                    {
                        _roundTracker.MarkActedThisRound(mid);
                    }
                }
                else
                {
                    mem.MissingAuthoritativeFrames = 0;

                    // Rebind any provisional name (fallback "怪物" or nickname) once the authoritative name resolves
                    // so it allocates a fresh unique ordinal in the destination bucket and never collides.
                    if (mem.NameBucketIsProvisional && !nameIsProvisional)
                    {
                        if (!string.Equals(mem.NameBucket, monsterName, StringComparison.Ordinal))
                        {
                            mem.NameBucket = monsterName;
                            mem.NameOrdinal = AllocateNameOrdinal(monsterName);
                        }
                        mem.NameBucketIsProvisional = false;
                    }
                    else if (string.Equals(mem.NameBucket, "怪物", StringComparison.Ordinal) &&
                             !string.Equals(monsterName, "怪物", StringComparison.Ordinal))
                    {
                        mem.NameBucket = monsterName;
                        mem.NameOrdinal = AllocateNameOrdinal(monsterName);
                        mem.NameBucketIsProvisional = nameIsProvisional;
                    }

                    if (serverActionEnd.HasValue)
                    {
                        if (!serverActionEnd.Value)
                        {
                            mem.LastServerActionEnd = false;
                            mem.LastServerActionEndRound = effectiveRound;
                            _roundTracker.ClearActedThisRound(mid);
                        }
                        else if (mem.LastServerActionEnd != true)
                        {
                            bool sameRoundBaseline =
                                effectiveRound < 0 ||
                                mem.LastServerActionEndRound < 0 ||
                                mem.LastServerActionEndRound == effectiveRound;
                            mem.LastServerActionEnd = true;
                            mem.LastServerActionEndRound = effectiveRound;
                            if (sameRoundBaseline && curActorId != mid)
                            {
                                _roundTracker.MarkActedThisRound(mid);
                            }
                        }
                        else if (effectiveRound >= 0 && mem.LastServerActionEndRound < 0)
                        {
                            mem.LastServerActionEndRound = effectiveRound;
                        }
                    }
                }

                int landId = SafeLandId(pd);
                int serverNodeId = SafeRoomPlayerNodeId(pd);
                bool onBoard = board.Contains(landId);
                var chForFacing = pd.CharacterInst;
                bool hasLiveChar = UnityObject.IsAlive(chForFacing);
                int fromLandId = onBoard ? SafeFromLandId(pd) : -1;
                bool isThief = IsThiefMonster(pd);
                bool movedSinceLastSnapshot = false;
                int[] frontIds = Array.Empty<int>();

                if (onBoard)
                {
                    if (!mem.HasBeenObservedOnBoard || mem.LastLandId < 0)
                    {
                        bool firstOnBoard = !mem.HasBeenObservedOnBoard;
                        mem.HasBeenObservedOnBoard = true;
                        int[] initialExplicitFront = LiveFacingResolver.TryAcceptInitialProtocolFrontIds(
                            board, landId, serverNodeId, fromLandId, SafeRawProtocolFrontNodeIds(pd));
                        mem.LastLandId = landId;
                        mem.LastFromLandId = fromLandId;
                        mem.ExplicitFrontNodeIds = initialExplicitFront;
                        mem.ExplicitFrontLandId = initialExplicitFront != null ? landId : -1;
                        mem.ExplicitFrontFromLandId = initialExplicitFront != null ? fromLandId : -1;

                        var initPlace = TryConsumeExactPlaceDisplacement(
                            board, mid, mem, fromLandId, landId, fromLandId, serverNodeId,
                            leftPreviousTile: false, isInitialBoardObservation: true, playerPositions, allUnits);
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

                        if (mem.LastLandId != landId)
                        {
                            movedSinceLastSnapshot = true;
                            int prevLandId = mem.LastLandId;
                            bool isMonsterOwnTurn = (curActorId == mid);
                            bool movedNonAdjacently = prevLandId >= 0 && !board.IsValidNeighbor(prevLandId, landId);

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

                            var exactLookup = TryConsumeExactPlaceDisplacement(
                                board, mid, mem, prevLandId, landId, fromLandId, serverNodeId,
                                leftPreviousTile: true, isInitialBoardObservation: false, playerPositions, allUnits);
                            if (exactLookup == ExactPlaceLookup.NotFound && (!isMonsterOwnTurn || movedNonAdjacently))
                            {
                                TryTagSkillDisplacementFromWindow(board, mid, mem, prevLandId, landId, playerPositions, allUnits);
                            }

                            mem.LastLandId = landId;
                            mem.LastFromLandId = fromLandId;
                        }
                        else if (_pendingPlaceByMonster.ContainsKey(mid))
                        {
                            var sameTileLookup = TryConsumeExactPlaceDisplacement(
                                board, mid, mem, mem.LastLandId, landId, fromLandId, serverNodeId,
                                leftPreviousTile: false, isInitialBoardObservation: false, playerPositions, allUnits);
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

                        frontIds = ResolveMonsterFrontNodeIds(board, chForFacing, hasLiveChar, landId, fromLandId, mem);
                    }
                }

                bool usesBossTarget = (heroId == 1018 || heroId == 1047);
                long preUpdateBossTargetId = mem.LastSeenBossTargetId;
                long? serverBossTargetOpt = usesBossTarget ? TryReadBossTargetId(pd) : null;

                if (usesBossTarget)
                {
                    // Promote the latest authoritative server BossTargetId (including explicit 0!) captured
                    // while the previous turn was locally suppressed.
                    if (mem.HasPendingServerBossTarget && mem.BossTargetClearedTurnSequence != turnSeq)
                    {
                        mem.LastSeenBossTargetId = mem.PendingServerBossTargetId;
                        mem.PendingServerBossTargetId = 0;
                        mem.HasPendingServerBossTarget = false;
                    }

                    if (serverBossTargetOpt.HasValue &&
                        (!mem.HasServerBossTarget || serverBossTargetOpt.Value != mem.LastServerBossTargetId))
                    {
                        long sv = serverBossTargetOpt.Value;
                        mem.HasServerBossTarget = true;
                        mem.LastServerBossTargetId = sv;
                        if (mem.BossTargetClearedTurnSequence != turnSeq)
                        {
                            mem.LastSeenBossTargetId = sv;
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

                // Evaluate retained per-monster combat records using actor-specific turn identity, event-time
                // opponent marks, layout revision checks, and tile-confirmation retention until visual model catches up.
                if (_pendingCombatByMonster.TryGetValue(mid, out var combatList))
                {
                    for (int ci = 0; ci < combatList.Count; )
                    {
                        var combatEvt = combatList[ci];
                        bool isSameActiveTurn = TryMatchActiveTurn(
                            curActorId,
                            mid,
                            turnSeq,
                            currentRound,
                            combatEvt.ActiveActor,
                            combatEvt.PublishedActorAtEvent,
                            combatEvt.PublishedTurnSequenceAtEvent,
                            combatEvt.Round,
                            ref combatEvt.BoundTurnSequence);
                        combatList[ci] = combatEvt;

                        bool wasBossOwnTurnFight = usesBossTarget &&
                                                   !combatEvt.BossLockEvaluated &&
                                                   combatEvt.OpponentId != 0 &&
                                                   (combatEvt.ActiveActor == mid || isSameActiveTurn);

                        if (wasBossOwnTurnFight)
                        {
                            combatEvt.BossLockEvaluated = true;
                            combatList[ci] = combatEvt;

                            bool opponentHadMarkAtCombat =
                                (heroId == 1018 && combatEvt.OpponentHadProblemStudent) ||
                                (heroId == 1047 && combatEvt.OpponentHadMandarinDuck);
                            bool matchedBossTarget =
                                combatEvt.OpponentId == preUpdateBossTargetId ||
                                combatEvt.OpponentId == mem.LastSeenBossTargetId ||
                                opponentHadMarkAtCombat ||
                                DidFightMarkedTarget(heroId, combatEvt.OpponentId, players);

                            if (matchedBossTarget)
                            {
                                if (mem.HasPendingServerBossTarget && mem.PendingServerBossTargetId == combatEvt.OpponentId)
                                {
                                    mem.PendingServerBossTargetId = 0;
                                    mem.HasPendingServerBossTarget = false;
                                }

                                // If the server already advanced to a NEW, distinct replacement target (e.g. P1 -> P2 + fight(P1)),
                                // clear only the old P1 lock and preserve the newer replacement P2!
                                bool hasNewerDistinctReplacement =
                                    mem.HasServerBossTarget &&
                                    mem.LastServerBossTargetId != 0 &&
                                    mem.LastServerBossTargetId != combatEvt.OpponentId &&
                                    mem.LastServerBossTargetId != preUpdateBossTargetId;

                                if (hasNewerDistinctReplacement)
                                {
                                    mem.LastSeenBossTargetId = mem.LastServerBossTargetId;
                                }
                                else
                                {
                                    mem.LastSeenBossTargetId = 0;
                                    if (isSameActiveTurn)
                                    {
                                        mem.BossTargetClearedTurnSequence = turnSeq;
                                    }
                                }
                            }
                        }

                        if (!isSameActiveTurn)
                        {
                            // Turn superseded or not this monster's turn: drop after boss-lock evaluation.
                            combatList.RemoveAt(ci);
                            continue;
                        }

                        if (combatEvt.LayoutGeneration != curLayoutGen)
                        {
                            // Tile-scoped evidence cannot cross a layout replacement.
                            combatList.RemoveAt(ci);
                            continue;
                        }

                        if (!onBoard)
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

                        if (combatEvt.LandId < 0)
                        {
                            // Unstamped fallback record cannot match a future tile once missed.
                            combatList.RemoveAt(ci);
                            continue;
                        }

                        // Protocol combat tile is ahead of the visual model: retain until the model reaches it
                        // or the turn/layout is superseded.
                        ci++;
                    }
                    if (combatList.Count == 0) _pendingCombatByMonster.Remove(mid);
                }

                // Infer nearest marked target ONLY after combat clearance, ONLY when no authoritative server target
                // exists, and ONLY commit the per-turn attempt flag once we have an authoritative player sample.
                if (usesBossTarget &&
                    onBoard &&
                    curActorId == mid &&
                    mem.LastSeenBossTargetId == 0 &&
                    mem.BossTargetLockTurnSequence != turnSeq &&
                    mem.BossTargetClearedTurnSequence != turnSeq &&
                    playersAuthoritative)
                {
                    mem.BossTargetLockTurnSequence = turnSeq;
                    int turnStartAnchor = _activeActorStartLandId >= 0 ? _activeActorStartLandId : landId;
                    long lockedMarkedId = FindNearestMarkedPlayerId(board, heroId, turnStartAnchor, players);
                    if (lockedMarkedId != 0)
                    {
                        mem.LastSeenBossTargetId = lockedMarkedId;
                    }
                }

                // Process retained per-monster thief steals independently of combat.
                if (_pendingStealByMonster.TryGetValue(mid, out var stealList))
                {
                    for (int si = 0; si < stealList.Count; )
                    {
                        var stealEvt = stealList[si];
                        bool isSameActiveTurn = isThief && TryMatchActiveTurn(
                            curActorId,
                            mid,
                            turnSeq,
                            currentRound,
                            activeActorAtEvent: mid,
                            stealEvt.PublishedActorAtEvent,
                            stealEvt.PublishedTurnSequenceAtEvent,
                            stealEvt.Round,
                            ref stealEvt.BoundTurnSequence);
                        stealList[si] = stealEvt;

                        if (!isSameActiveTurn)
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

                        if (!onBoard)
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

                        if (stealEvt.LandId < 0)
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
                                             (SafeIsLaunchFight(pd) || (isThief && mem.StolenTurnSequence == turnSeq));

                bool isNormalAggressive = IsNormalAggressiveMonster(pd);
                bool isTaunted = TryReadTauntTarget(pd, heroIdToPlayerId, out long tauntTargetId);
                bool requiresCrimeTarget = IsCrimeRestrictedMonster(pd, heroId, anyPlayerHasTrueCulprit);
                int actionWeight = SafeActionWeight(pd);
                SafeHp(pd, out int hp, out int maxHp, out _, out _);
                bool bossTargetClearedThisTurn = (curActorId == mid) && (mem.BossTargetClearedTurnSequence == turnSeq);

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
                    BossTargetPlayerId = bossTargetClearedThisTurn ? 0L : (usesBossTarget ? mem.LastSeenBossTargetId : 0L),
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

            if (rosterAuthoritative)
            {
                // Prune memories ONLY after explicit death OR 2 consecutive authoritative snapshots omitting the monster
                // so a single transient subset of allUnits never wipes spawn sequence, ordinal, or pending events.
                List<long> stale = null;
                foreach (var kv in _memories)
                {
                    if (!rosterAliveIds.Contains(kv.Key))
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
                // Keep 1-frame-missing monsters in _rosterAliveMonsterIds until confirmed retired.
                foreach (var kv in _memories) _rosterAliveMonsterIds.Add(kv.Key);

                // Prune pending Places/combats/steals for known hero units, never for unseen future monster spawns.
                PruneHeroPendingKeys(_pendingPlaceByMonster, seenPlayerIds);
                PruneHeroPendingKeys(_pendingCombatByMonster, seenPlayerIds);
                PruneHeroPendingKeys(_pendingStealByMonster, seenPlayerIds);
            }
            else
            {
                foreach (long id in rosterAliveIds) _rosterAliveMonsterIds.Add(id);
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
            _lastKnownPlayerAlive.Clear();
            _retiredMonsterSec.Clear();
            _pendingCombatByMonster.Clear();
            _pendingStealByMonster.Clear();
            _lastAcceptedHeroIdToPlayerId.Clear();
            _lastAcceptedAnyPlayerHasTrueCulprit = false;
            _knownPlayerCount = 0;
            Interlocked.Exchange(ref _placeBoardValidationPending, 0);
            _nextSpawnSequence = 1;
            _localPlayerId = 0;
            _activeActorStartLandId = -1;
            _activeActorLastSeenLandId = -1;
            _turnStartLayoutInvalidated = false;
            _activeActorSteppedThisTurn = false;
            var dice = Volatile.Read(ref _latestDice);
            _roundTracker.Reset(dice.Epoch);
            Volatile.Write(ref _publishedTurn, new TurnStamp(_roundTracker.ActiveActorId, _roundTracker.TurnSequence));
            _matchRoomId = newRoomId;
            _matchStartTime = newStartTime;
            Interlocked.Increment(ref _layoutGeneration);
            if (confirmedMatchSwitch)
            {
                Interlocked.Increment(ref _matchGeneration);
            }

            // Prune previous-match entries from main-thread collections. Concurrent queues are NOT
            // dequeued-and-reenqueued here so strict FIFO arrival order is never reversed; DrainPlaceQueue
            // and DrainEventQueues filter every dequeued record via IsMatchingEventStamp in arrival order.
            for (int i = _recentSkills.Count - 1; i >= 0; i--)
            {
                if (!IsMatchingEventStamp(_recentSkills[i].MatchRoomId, _recentSkills[i].MatchStartTime, _recentSkills[i].MatchGeneration))
                    _recentSkills.RemoveAt(i);
            }

            List<long> emptyMonsters = null;
            foreach (var kv in _pendingPlaceByMonster)
            {
                var list = kv.Value;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (!IsMatchingEventStamp(list[i].MatchRoomId, list[i].MatchStartTime, list[i].MatchGeneration))
                        list.RemoveAt(i);
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
        }

        private bool EnsureSameMatch()
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
            public int LandId;
            public long OpponentId;
            public bool OpponentHadProblemStudent;
            public bool OpponentHadMandarinDuck;
            public long ActiveActor;
            public long PublishedActorAtEvent;
            public int PublishedTurnSequenceAtEvent;
            public int BoundTurnSequence;
            public int Round;
            public int LayoutGeneration;
            public bool BossLockEvaluated;
        }

        private struct PendingThiefSteal
        {
            public int LandId;
            public long PublishedActorAtEvent;
            public int PublishedTurnSequenceAtEvent;
            public int BoundTurnSequence;
            public int Round;
            public int LayoutGeneration;
        }

        private static bool TryMatchActiveTurn(
            long curActorId,
            long unitId,
            int curTurnSeq,
            int curRound,
            long activeActorAtEvent,
            long publishedActorAtEvent,
            int publishedTurnSeqAtEvent,
            int eventRound,
            ref int boundTurnSeq)
        {
            if (curActorId != unitId) return false;
            if (eventRound >= 0 && curRound >= 0 && eventRound != curRound) return false;
            if (activeActorAtEvent != 0 && activeActorAtEvent != unitId) return false;

            if (boundTurnSeq >= 0)
            {
                return boundTurnSeq == curTurnSeq;
            }

            if (publishedActorAtEvent == unitId)
            {
                boundTurnSeq = publishedTurnSeqAtEvent;
                return boundTurnSeq == curTurnSeq;
            }

            // Event fired just after game switched CurrentPlayer to unitId, one frame before ObserveTurnBoundary ran.
            if (curTurnSeq == publishedTurnSeqAtEvent + 1)
            {
                boundTurnSeq = curTurnSeq;
                return true;
            }

            return false;
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
            while (_placeQueue.TryDequeue(out var pl))
            {
                if (pl == null || pl.TargetMonsterId == 0 || pl.NewLandId < 0) continue;
                if (!IsMatchingEventStamp(pl.MatchRoomId, pl.MatchStartTime, pl.MatchGeneration)) continue;
                if (IsRecentlyRetiredMonster(pl.TargetMonsterId, nowSec)) continue;
                if (!_pendingPlaceByMonster.TryGetValue(pl.TargetMonsterId, out var list))
                {
                    list = new List<SkillDisplacementRecord>(4);
                    _pendingPlaceByMonster[pl.TargetMonsterId] = list;
                }
                if (list.Count >= MaxPendingPlacesPerMonster)
                {
                    list.RemoveAt(0);
                }
                list.Add(pl);
            }
        }

        private void DrainEventQueues()
        {
            const int MaxPendingCombatPerMonster = 16;
            double nowSec = MonotonicSeconds();
            int curLayoutGen = Volatile.Read(ref _layoutGeneration);
            while (_skillQueue.TryDequeue(out var sk))
            {
                if (sk != null &&
                    sk.LayoutGeneration == curLayoutGen &&
                    IsMatchingEventStamp(sk.MatchRoomId, sk.MatchStartTime, sk.MatchGeneration))
                {
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

            // Target-bound 1040.Place events do NOT expire on a wall-clock timeout: they persist across
            // arbitrary render pauses until consumed on tile confirmation, superseded, monster death, or reset.
            DrainPlaceQueue();

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
                        LandId = c.AttackerLandId,
                        OpponentId = c.DefenderId,
                        OpponentHadProblemStudent = c.DefenderHadProblemStudent,
                        OpponentHadMandarinDuck = c.DefenderHadMandarinDuck,
                        ActiveActor = c.ActiveActorAtEvent,
                        PublishedActorAtEvent = c.PublishedActorAtEvent,
                        PublishedTurnSequenceAtEvent = c.PublishedTurnSequenceAtEvent,
                        BoundTurnSequence = -1,
                        Round = c.Round,
                        LayoutGeneration = c.LayoutGeneration
                    });
                if (c.DefenderId != 0)
                    AddCombat(c.DefenderId, new PendingUnitCombat
                    {
                        LandId = c.DefenderLandId,
                        OpponentId = c.AttackerId,
                        OpponentHadProblemStudent = c.AttackerHadProblemStudent,
                        OpponentHadMandarinDuck = c.AttackerHadMandarinDuck,
                        ActiveActor = c.ActiveActorAtEvent,
                        PublishedActorAtEvent = c.PublishedActorAtEvent,
                        PublishedTurnSequenceAtEvent = c.PublishedTurnSequenceAtEvent,
                        BoundTurnSequence = -1,
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
                        LandId = ts.LandId,
                        PublishedActorAtEvent = ts.PublishedActorAtEvent,
                        PublishedTurnSequenceAtEvent = ts.PublishedTurnSequenceAtEvent,
                        BoundTurnSequence = -1,
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

            int matchIdx = -1;
            if (!leftPreviousTile)
            {
                if (list[0].NewLandId >= 0 && list[0].NewLandId == newLandId)
                {
                    matchIdx = 0;
                }
                else if (isInitialBoardObservation)
                {
                    // Initial on-board observation after missed intermediate hops (e.g. [B, F] queued before
                    // first visible sample at F): match the earliest entry arriving at F whose predecessor agrees
                    // with liveFromLandId, or the final entry if protocol NodeId already confirmed F.
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i].NewLandId != newLandId) continue;
                        if (liveFromLandId >= 0 && list[i].BackLandId == liveFromLandId)
                        {
                            matchIdx = i;
                            break;
                        }
                        if (serverNodeId == newLandId && i == list.Count - 1)
                        {
                            matchIdx = i;
                        }
                    }
                }
                else if (list.Count >= 2 && serverNodeId == newLandId && liveFromLandId >= 0)
                {
                    // Completed round-trip loop (e.g. A -> B -> A) between visual samples: confirm ONLY when
                    // protocol NodeId is back at A AND the queued return record's BackLandId matches liveFromLandId.
                    for (int i = 1; i < list.Count; i++)
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
                // Visual model confirmed moving to newLandId: match the earliest pending Place reaching newLandId
                // (never jumping over a later loop [B, C, B] on the first arrival at B).
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].NewLandId >= 0 && list[i].NewLandId == newLandId)
                    {
                        matchIdx = i;
                        break;
                    }
                }
            }

            if (matchIdx < 0)
            {
                // Only drop a leading stale record for oldLandId once we confirmed moving away from oldLandId,
                // never deleting a queued future return to oldLandId further back in the list.
                if (leftPreviousTile && oldLandId >= 0 && list.Count > 0 && list[0].NewLandId == oldLandId)
                {
                    list.RemoveAt(0);
                    if (list.Count == 0) _pendingPlaceByMonster.Remove(monsterId);
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
                if (hopChangedTile)
                {
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
                    // If this exact Place confirms a provisional heuristic claim on the same tile, finalize that claim
                    // instead of charging a second cast; if it contradicts the provisional cast, release the wrong claim first.
                    bool alreadyClaimedMatchingCast = false;
                    if (!hopChangedTile && mem.SkillMoveFromHeuristic && mem.SkillMoveLandId == nextLand && mem.HeuristicSkillCast != null)
                    {
                        var hCast = mem.HeuristicSkillCast;
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
                        mem.HeuristicSkillCast = null;
                    }

                    if (!alreadyClaimedMatchingCast)
                    {
                        ConsumeRecentSkillCastForMonster(monsterId, kind, rec.CasterPlayerId, allUnits);
                    }

                    // Prefer a protocol BackLandId that validates as a direct incoming neighbor of nextLand
                    // over an ambiguous older multi-hop origin tile.
                    int directIncomingPredecessor = (validRecBack >= 0 && board.IsValidNeighbor(validRecBack, nextLand))
                        ? validRecBack
                        : -1;
                    int pushOrigin = directIncomingPredecessor >= 0
                        ? directIncomingPredecessor
                        : ((hopChangedTile && simLand >= 0) ? simLand : validRecBack);

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
                        ApplyDisplacementTag(board, mem, kind, pushOrigin, nextLand, rec.CasterPlayerId, playerPositions, directIncomingPredecessor);
                        mem.SkillMoveFromHeuristic = false;
                    }
                }
                else if (!hopChangedTile && mem.SkillMoveFromHeuristic && mem.SkillMoveLandId == nextLand)
                {
                    // A late authoritative ordinary Place retracts a provisional time-window heuristic guess
                    // and releases its claim on the SkillCastRecord so the real displacement can still consume it.
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

                simLand = nextLand;
            }

            // Consume entries 0..matchIdx (retaining any newer ahead-of-visual entries after matchIdx).
            list.RemoveRange(0, matchIdx + 1);
            if (list.Count == 0) _pendingPlaceByMonster.Remove(monsterId);

            return (mem.SkillMoveKind != SkillMoveKind.None && mem.SkillMoveLandId == newLandId)
                ? ExactPlaceLookup.TaggedDisplacement
                : ExactPlaceLookup.KnownOtherCause;
        }

        private void ConsumeRecentSkillCastForMonster(
            long monsterId,
            SkillMoveKind kind,
            long casterPlayerId,
            IReadOnlyList<BattlePlayerData> allUnits)
        {
            if (_recentSkills.Count == 0 || kind == SkillMoveKind.None) return;

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
                break;
            }
        }

        private void TryTagSkillDisplacementFromWindow(
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
            if (_recentSkills.Count == 0) return;

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
                    return;
                }
            }

            if (uniqueCast == null) return;

            uniqueCast.AppliedMonsterIds.Add(monsterId);
            ApplyDisplacementTag(board, mem, uniqueKind, oldLandId, newLandId, uniqueCast.CasterPlayerId, playerPositions, directIncomingPredecessor: -1);
            mem.SkillMoveFromHeuristic = true;
            mem.HeuristicSkillCast = uniqueCast;
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

        private TrackedPlayer BuildTrackedPlayer(BattlePlayerData pd, PredictorSettings settings, out bool isTrueCulprit)
        {
            isTrueCulprit = false;
            try
            {
                var p = pd.player;
                var prop = pd.Property;
                SafeHp(pd, out int hp, out int maxHp, out bool hasHp, out bool hasMaxHp);

                bool hasHealthState = false;
                bool dead = false;
                try
                {
                    if (prop != null)
                    {
                        dead = prop.HealthState == HealthState.Dead;
                        hasHealthState = true;
                    }
                }
                catch { }
                if (!dead && hasHp && hasMaxHp && maxHp > 0 && hp <= 0) dead = true;

                if (!hasHealthState && !hasHp)
                {
                    // Neither HealthState nor HP was readable: preserve last known liveness rather than assuming dead.
                    if (_lastKnownPlayerAlive.TryGetValue(p.Id, out bool prevAlive))
                        dead = !prevAlive;
                }
                else
                {
                    _lastKnownPlayerAlive[p.Id] = !dead;
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

                int landId = SafeLandId(pd);
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
            try
            {
                var prop = pd.Property;
                if (prop != null)
                {
                    if (prop.HP != null)
                    {
                        hp = prop.HP.Value;
                        hasHp = true;
                    }
                    if (prop.maxHP > 0)
                    {
                        maxHp = prop.maxHP;
                        hasMaxHp = true;
                    }
                }
            }
            catch { }
            if (!hasHp || !hasMaxHp)
            {
                try
                {
                    var hero = pd.player != null ? pd.player.Hero : null;
                    if (hero != null)
                    {
                        if (!hasHp)
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
            try
            {
                var prop = pd.Property;
                if (prop != null)
                {
                    if (prop.HealthState == HealthState.Dead) { isAlive = false; return true; }
                    if (prop.maxHP > 0 && prop.HP != null && prop.HP.Value <= 0) { isAlive = false; return true; }
                }
                var hero = pd.player != null ? pd.player.Hero : null;
                if (hero != null && hero.MaxHp > 0 && hero.Hp <= 0) { isAlive = false; return true; }
                return true;
            }
            catch
            {
                return false;
            }
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

        private static bool IsThiefMonster(BattlePlayerData pd)
        {
            try
            {
                var hero = pd.player != null ? pd.player.Hero : null;
                return hero != null && hero.MonsterType == Hero.Types.MonsterType.Thief;
            }
            catch { return false; }
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

            if (propReadOk && propVal != 0) return propVal;

            try
            {
                if (pd.player != null && pd.player.Hero != null)
                    return pd.player.Hero.BossTargetId;
            }
            catch { }

            // Distinguish a successful explicit zero read from an unavailable read.
            if (propReadOk) return 0L;
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

        private static bool IsNormalAggressiveMonster(BattlePlayerData pd)
        {
            try
            {
                var hero = pd.player != null ? pd.player.Hero : null;
                if (hero != null)
                {
                    var mt = hero.MonsterType;
                    if (mt == Hero.Types.MonsterType.Thief || mt == Hero.Types.MonsterType.PveAllyMonster)
                        return false;
                }
            }
            catch { }
            return true;
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
            if (pd == null) return -1;
            try
            {
                if (pd.CharacterInst != null && pd.CharacterInst.standLand != null)
                    return pd.CharacterInst.standLand.Id;
            }
            catch { }
            try
            {
                if (pd.player != null) return pd.player.NodeId;
            }
            catch { }
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

        private static int SafeActionWeight(BattlePlayerData pd)
        {
            try { return pd.player != null && pd.player.Hero != null ? pd.player.Hero.ActionWeight : 0; }
            catch { return 0; }
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
