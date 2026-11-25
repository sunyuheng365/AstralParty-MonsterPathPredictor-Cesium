using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
            public int LastLandId = -1;
            public int LastFromLandId = -1;
            public bool JustSpawnedOnFork;
            public bool JustFoughtOnFork;
            public int CombatForkLandId = -1;
            public int CombatTurnSequence = -1;
            public int StolenTurnSequence = -1;
            public SkillMoveKind SkillMoveKind = SkillMoveKind.None;
            public int SkillMoveLandId = -1;
            public int SkillMovePushDirId = -1;
            public int SkillMovePushFromId = -1;
        }

        private sealed class SkillCastRecord
        {
            public long CasterPlayerId;
            public int SkillId;
            public long MatchRoomId;
            public long MatchStartTime;
            public DateTime TimestampUtc;
            public HashSet<long> AppliedMonsterIds = new HashSet<long>();
        }

        private sealed class SkillDisplacementRecord
        {
            public long TargetMonsterId;
            public long CasterPlayerId;
            public int SkillId;
            public int NewLandId;
            public int BackLandId;
            public long MatchRoomId;
            public long MatchStartTime;
            public DateTime TimestampUtc;
        }

        private struct CombatRecord
        {
            public long AttackerId;
            public long DefenderId;
            public int AttackerLandId;
            public int DefenderLandId;
            public long ActiveActorAtEvent;
            public int TurnEpoch;
            public int Round;
            public long MatchRoomId;
            public long MatchStartTime;
        }

        private struct ThiefStealRecord
        {
            public long ThiefId;
            public int LandId;
            public int TurnEpoch;
            public int Round;
            public long MatchRoomId;
            public long MatchStartTime;
        }

        private const int MaxQueuedEvents = 256;

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
        private readonly HashSet<long> _rosterAliveMonsterIds = new HashSet<long>();
        private readonly List<SkillCastRecord> _recentSkills = new List<SkillCastRecord>();
        private readonly Dictionary<long, List<SkillDisplacementRecord>> _pendingPlaceByMonster = new Dictionary<long, List<SkillDisplacementRecord>>();

        private long _activeActorId;
        private int _activeActorStartLandId = -1;
        private bool _activeActorSteppedThisTurn;
        private int _turnSequence;
        private int _eventTurnEpoch;
        private int _lastObservedTurnEpoch;
        private int _lastObservedRound = -1;

        private long _matchRoomId;
        private long _matchStartTime;
        private bool _hooked;
        private volatile bool _acceptEvents;

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

        public void Reset()
        {
            _memories.Clear();
            _rosterAliveMonsterIds.Clear();
            _recentSkills.Clear();
            _pendingPlaceByMonster.Clear();
            while (_skillQueue.TryDequeue(out _)) { }
            while (_placeQueue.TryDequeue(out _)) { }
            while (_combatQueue.TryDequeue(out _)) { }
            while (_thiefStealQueue.TryDequeue(out _)) { }
            _activeActorId = 0;
            _activeActorStartLandId = -1;
            _activeActorSteppedThisTurn = false;
            _turnSequence = 0;
            Interlocked.Exchange(ref _eventTurnEpoch, 0);
            _lastObservedTurnEpoch = 0;
            _lastObservedRound = -1;
            _matchRoomId = 0;
            _matchStartTime = 0;
        }

        public bool IsMonsterInAliveRoster(long monsterId)
        {
            return monsterId != 0 && _rosterAliveMonsterIds.Contains(monsterId);
        }

        private static int TryReadLiveUnitLandId(long unitId)
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
                        return SafeLandId(pd);
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

        private void OnDiceResult(long playerId, int dice1, int dice2)
        {
            if (!_acceptEvents) return;
            Interlocked.Increment(ref _eventTurnEpoch);
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
            int curEpoch = Volatile.Read(ref _eventTurnEpoch);
            int curRound = TryReadCurrentRound();
            int causeId = attr.Cause != null ? (int)attr.Cause.Id : 0;
            TryStampCurrentMatch(out long matchRoomId, out long matchStartTime);

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
                        TurnEpoch = curEpoch,
                        Round = curRound,
                        MatchRoomId = matchRoomId,
                        MatchStartTime = matchStartTime
                    });
                }

                // Target-bound skill displacement (cmd 1040 Place effect carries exact victim MonsterId + caster + Cause).
                // Negative NodeId (< 0) is a protocol sentinel (no destination tile): never enqueue as a wildcard tile match.
                if (eff.Place != null && eff.Place.Place != null && eff.Place.Place.NodeId >= 0 && eff.PlayerId != 0 && !TryIsUnitHero(eff.PlayerId))
                {
                    if (_placeQueue.Count >= MaxQueuedEvents) _placeQueue.TryDequeue(out _);
                    _placeQueue.Enqueue(new SkillDisplacementRecord
                    {
                        TargetMonsterId = eff.PlayerId,
                        CasterPlayerId = ownerId,
                        SkillId = causeId,
                        NewLandId = eff.Place.Place.NodeId,
                        BackLandId = eff.Place.Place.BackNodeId,
                        MatchRoomId = matchRoomId,
                        MatchStartTime = matchStartTime,
                        TimestampUtc = DateTime.UtcNow
                    });
                }
            }
        }

        private void OnFightResolved(FightSnapshot snap)
        {
            if (!_acceptEvents) return;
            if (snap.AttackerPlayerId == 0 && snap.DefenderPlayerId == 0) return;
            TryStampCurrentMatch(out long matchRoomId, out long matchStartTime);
            if (_combatQueue.Count >= MaxQueuedEvents) _combatQueue.TryDequeue(out _);
            _combatQueue.Enqueue(new CombatRecord
            {
                AttackerId = snap.AttackerPlayerId,
                DefenderId = snap.DefenderPlayerId,
                AttackerLandId = TryReadLiveUnitLandId(snap.AttackerPlayerId),
                DefenderLandId = TryReadLiveUnitLandId(snap.DefenderPlayerId),
                ActiveActorAtEvent = TryReadCurrentActorId(),
                TurnEpoch = Volatile.Read(ref _eventTurnEpoch),
                Round = TryReadCurrentRound(),
                MatchRoomId = matchRoomId,
                MatchStartTime = matchStartTime
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
                if (_combatQueue.Count >= MaxQueuedEvents) _combatQueue.TryDequeue(out _);
                _combatQueue.Enqueue(new CombatRecord
                {
                    AttackerId = atk,
                    DefenderId = def,
                    AttackerLandId = TryReadLiveUnitLandId(atk),
                    DefenderLandId = TryReadLiveUnitLandId(def),
                    ActiveActorAtEvent = TryReadCurrentActorId(),
                    TurnEpoch = Volatile.Read(ref _eventTurnEpoch),
                    Round = TryReadCurrentRound(),
                    MatchRoomId = matchRoomId,
                    MatchStartTime = matchStartTime
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
            long curActorId = (currentActor != null && currentActor.player != null) ? currentActor.player.Id : 0;
            int curEpoch = Volatile.Read(ref _eventTurnEpoch);
            int curRound = TryReadCurrentRound();

            bool boundaryAdvanced =
                curActorId != _activeActorId ||
                curEpoch != _lastObservedTurnEpoch ||
                (curRound >= 0 && _lastObservedRound >= 0 && curRound != _lastObservedRound);

            if (boundaryAdvanced)
            {
                _turnSequence++;
                _activeActorId = curActorId;
                _lastObservedTurnEpoch = curEpoch;
                if (curRound >= 0) _lastObservedRound = curRound;
                _activeActorStartLandId = SafeLandId(currentActor);
                _activeActorSteppedThisTurn = false;
            }
            else if (curActorId != 0)
            {
                if (curRound >= 0 && _lastObservedRound < 0) _lastObservedRound = curRound;
                int nowLand = SafeLandId(currentActor);
                if (nowLand >= 0)
                {
                    if (_activeActorStartLandId < 0)
                    {
                        _activeActorStartLandId = nowLand;
                    }
                    else if (nowLand != _activeActorStartLandId)
                    {
                        _activeActorSteppedThisTurn = true;
                    }
                }
            }

            return matchChanged;
        }

        private void OnSkillUsed(long playerId, int skillId)
        {
            if (!_acceptEvents) return;
            TryStampCurrentMatch(out long matchRoomId, out long matchStartTime);
            if (_skillQueue.Count >= MaxQueuedEvents) _skillQueue.TryDequeue(out _);
            _skillQueue.Enqueue(new SkillCastRecord
            {
                CasterPlayerId = playerId,
                SkillId = skillId,
                MatchRoomId = matchRoomId,
                MatchStartTime = matchStartTime,
                TimestampUtc = DateTime.UtcNow
            });
        }

        public BoardGraph CaptureBoard(LandManager manager)
        {
            var nodes = new List<LandNodeSnapshot>();
            if (manager == null || manager.NodeDict == null) return new BoardGraph(nodes);

            foreach (var pair in manager.NodeDict)
            {
                var land = pair.Value;
                if (!UnityObject.IsAlive(land)) continue;

                var pos = TransformService.GetPosition(UnityObject.GetTransform(land));
                var nodeInfo = land.GetNodeInfo();
                int[] neighborIds = Array.Empty<int>();
                if (nodeInfo != null && nodeInfo.neighborLandIds != null)
                {
                    var raw = nodeInfo.neighborLandIds;
                    neighborIds = new int[raw.Count];
                    for (int i = 0; i < raw.Count; i++) neighborIds[i] = raw[i];
                }

                nodes.Add(new LandNodeSnapshot(pair.Key, neighborIds, land.Disable, pos.x, pos.y, pos.z));
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
            out bool matchChanged)
        {
            monsters = new List<LiveMonsterInfo>();
            players = new List<TrackedPlayer>();

            matchChanged = ObserveTurnBoundary(battle);

            if (board == null || battle == null) return;

            var allUnits = Players.All();
            if (allUnits == null || allUnits.Count == 0) return;

            // Only drain queued combat/steal/skill events AFTER confirming we have a populated live unit snapshot
            // so events are never lost on an empty-roster frame.
            DrainEventQueues(out var pendingCombatTiles, out var pendingThiefSteals);

            long curActorId = _activeActorId;
            int currentEpoch = Volatile.Read(ref _eventTurnEpoch);
            int currentRound = TryReadCurrentRound();

            var playerPositions = new Dictionary<long, int>();
            var heroIdToPlayerId = new Dictionary<int, long>();
            var rosterAliveIds = new HashSet<long>();
            bool anyPlayerHasTrueCulprit = false;

            for (int i = 0; i < allUnits.Count; i++)
            {
                var pd = allUnits[i];
                if (pd == null || pd.player == null) continue;

                CharacterType cType = CharacterType.None;
                try { cType = pd.characterType; } catch { }

                if (cType == CharacterType.Hero)
                {
                    var tp = BuildTrackedPlayer(pd, settings, out bool isTrueCulprit);
                    if (isTrueCulprit) anyPlayerHasTrueCulprit = true;
                    if (tp != null)
                    {
                        players.Add(tp);
                        if (tp.LandId >= 0) playerPositions[tp.PlayerId] = tp.LandId;
                        int pHeroId = SafeHeroId(pd);
                        if (pHeroId > 0 && tp.IsAlive && !heroIdToPlayerId.ContainsKey(pHeroId))
                            heroIdToPlayerId[pHeroId] = tp.PlayerId;
                    }
                }
            }

            for (int i = 0; i < allUnits.Count; i++)
            {
                var pd = allUnits[i];
                if (pd == null || pd.player == null) continue;

                CharacterType cType = CharacterType.None;
                try { cType = pd.characterType; } catch { }
                if (cType != CharacterType.Monster) continue;

                if (!IsRosterMonsterAlive(pd)) continue;

                long mid = pd.player.Id;
                rosterAliveIds.Add(mid);

                int landId = SafeLandId(pd);
                if (!board.Contains(landId)) continue;

                int fromLandId = SafeFromLandId(pd);
                int[] frontIds = SafeFrontNodeIds(pd);
                bool isThief = IsThiefMonster(pd);

                // Update logical monster memory & queued combat/steal/displacement events BEFORE visual model checks
                // so a momentarily unrendered CharacterInst never drops combat/steal flags.
                bool isNew = !_memories.TryGetValue(mid, out var mem);
                bool movedSinceLastSnapshot = false;
                if (isNew)
                {
                    mem = new MonsterMemory
                    {
                        LastLandId = landId,
                        LastFromLandId = fromLandId,
                        JustSpawnedOnFork = board.IsFork(landId) && (frontIds.Length == 1 || !board.IsValidNeighbor(landId, fromLandId))
                    };
                    _memories[mid] = mem;
                    // On first observation, consume a pending Place ONLY if it already matches landId;
                    // never delete an ahead-of-visual Place whose CharacterInst.standLand has not updated yet.
                    TryConsumeExactPlaceDisplacement(board, mid, mem, fromLandId, landId, leftPreviousTile: false, playerPositions, allUnits);
                }
                else
                {
                    // Invalidate any prior-turn post-combat / steal marker as soon as turn sequence advances,
                    // even when another actor is currently taking their turn (prevents stale preview pollution).
                    if (mem.JustFoughtOnFork && mem.CombatTurnSequence != _turnSequence)
                    {
                        mem.JustFoughtOnFork = false;
                        mem.CombatForkLandId = -1;
                    }
                    if (mem.StolenTurnSequence != -1 && mem.StolenTurnSequence != _turnSequence)
                    {
                        mem.StolenTurnSequence = -1;
                    }

                    if (mem.LastLandId != landId)
                    {
                        movedSinceLastSnapshot = true;
                        int prevLandId = mem.LastLandId;
                        bool isMonsterOwnTurn = (curActorId == mid);
                        bool movedNonAdjacently = prevLandId >= 0 && !board.IsValidNeighbor(prevLandId, landId);

                        // Leaving a tile ALWAYS clears prior tile-bound spawn/combat/skill tags unconditionally
                        // so revisiting the same fork later in the turn can NEVER reactivate an expired post-combat tag.
                        mem.JustSpawnedOnFork = false;
                        mem.JustFoughtOnFork = false;
                        mem.CombatForkLandId = -1;
                        mem.SkillMoveKind = SkillMoveKind.None;
                        mem.SkillMoveLandId = -1;
                        mem.SkillMovePushDirId = -1;
                        mem.SkillMovePushFromId = -1;

                        // Always check exact target-bound 1040.Place displacement first (even on own-turn adjacent moves);
                        // only fall back to the time-window SkillUsed heuristic when no matching exact Place record exists
                        // (never overwrite a KnownOtherCause exact Place with an unrelated cast from the time window).
                        var exactLookup = TryConsumeExactPlaceDisplacement(board, mid, mem, prevLandId, landId, leftPreviousTile: true, playerPositions, allUnits);
                        if (exactLookup == ExactPlaceLookup.NotFound && (!isMonsterOwnTurn || movedNonAdjacently))
                        {
                            TryTagSkillDisplacementFromWindow(board, mid, mem, prevLandId, landId, playerPositions, allUnits);
                        }

                        mem.LastLandId = landId;
                        mem.LastFromLandId = fromLandId;
                    }
                    else if (_pendingPlaceByMonster.ContainsKey(mid))
                    {
                        TryConsumeExactPlaceDisplacement(board, mid, mem, mem.LastLandId, landId, leftPreviousTile: false, playerPositions, allUnits);
                    }
                }

                // Stamp queued combat events ONLY when the monster is CURRENTLY still standing on the combat tile
                // (matching current turn epoch/round). If the monster already moved past the combat tile before this
                // snapshot, never write a stale CombatForkLandId that could fire on a later revisit to that fork.
                if (pendingCombatTiles.TryGetValue(mid, out var combatEvt) &&
                    combatEvt.TurnEpoch == currentEpoch &&
                    (combatEvt.Round < 0 || currentRound < 0 || combatEvt.Round == currentRound) &&
                    (combatEvt.ActiveActor == 0 || combatEvt.ActiveActor == mid) &&
                    curActorId == mid)
                {
                    int actualCombatTile = combatEvt.LandId >= 0
                        ? combatEvt.LandId
                        : ((!isNew && !movedSinceLastSnapshot) ? landId : -1);
                    if (actualCombatTile == landId && board.IsForkForContext(landId, fromLandId, arrivedFromIncomingEdge: true))
                    {
                        mem.JustFoughtOnFork = true;
                        mem.CombatForkLandId = landId;
                        mem.CombatTurnSequence = _turnSequence;
                    }
                }
                else if (isThief && curActorId == mid &&
                         pendingThiefSteals.TryGetValue(mid, out var stealEvt) &&
                         stealEvt.TurnEpoch == currentEpoch &&
                         (stealEvt.Round < 0 || currentRound < 0 || stealEvt.Round == currentRound))
                {
                    // Record turn-level steal occurrence regardless of tile type so RedBlueAfterCombat
                    // matches MonsterPathEngine's long forecast after normal-tile steals, while setting
                    // JustFoughtOnFork ONLY when the thief is still standing on that fork tile.
                    mem.StolenTurnSequence = _turnSequence;
                    int actualStealTile = stealEvt.LandId >= 0
                        ? stealEvt.LandId
                        : ((!isNew && !movedSinceLastSnapshot) ? landId : -1);
                    if (actualStealTile == landId && board.IsForkForContext(landId, fromLandId, arrivedFromIncomingEdge: true))
                    {
                        mem.JustFoughtOnFork = true;
                        mem.CombatForkLandId = landId;
                        mem.CombatTurnSequence = _turnSequence;
                    }
                }

                var ch = pd.CharacterInst;
                if (!UnityObject.IsAlive(ch)) continue;

                int canStep = SafeCanStep(ch);

                bool justFought = (curActorId == mid) &&
                                  mem.JustFoughtOnFork &&
                                  mem.CombatTurnSequence == _turnSequence &&
                                  mem.CombatForkLandId == landId &&
                                  board.IsForkForContext(landId, fromLandId, arrivedFromIncomingEdge: true);
                bool isMovingMidTurn = (curActorId == mid) && _activeActorSteppedThisTurn && !justFought;
                bool launchedFightThisTurn = (curActorId == mid) &&
                                             (SafeIsLaunchFight(pd) || (isThief && mem.StolenTurnSequence == _turnSequence));

                int heroId = SafeHeroId(pd);
                string monsterName = ResolveMonsterName(pd, heroId);
                bool isNormalAggressive = IsNormalAggressiveMonster(pd);
                bool isTaunted = TryReadTauntTarget(pd, heroIdToPlayerId, out long tauntTargetId);
                bool requiresCrimeTarget = IsCrimeRestrictedMonster(pd, heroId, anyPlayerHasTrueCulprit);
                int actionWeight = SafeActionWeight(pd);
                SafeHp(pd, out int hp, out int maxHp);

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
                    CurrentLandId = landId,
                    FromLandId = fromLandId,
                    FrontNodeIds = frontIds,
                    CanStep = canStep,
                    IsCurrentlyMovingMidTurn = isMovingMidTurn,
                    TurnStartLandId = (curActorId == mid) ? _activeActorStartLandId : landId,
                    JustFoughtOnCurrentFork = justFought,
                    HasLaunchedFightThisTurn = launchedFightThisTurn,
                    JustSpawnedOnFork = mem.JustSpawnedOnFork && board.IsFork(landId),
                    IsTaunted = isTaunted,
                    TauntTargetPlayerId = tauntTargetId,
                    RequiresCrimeTarget = requiresCrimeTarget,
                    ChasesProblemStudent = (heroId == 1018),
                    ChasesMandarinDuckMark = (heroId == 1047),
                    SkillMoveKind = mem.SkillMoveLandId == landId ? mem.SkillMoveKind : SkillMoveKind.None,
                    SkillMovePushDirectionLandId = mem.SkillMoveLandId == landId ? mem.SkillMovePushDirId : -1,
                    SkillMovePushFromLandId = mem.SkillMoveLandId == landId ? mem.SkillMovePushFromId : -1
                };

                Vector3 feetPos;
                if (board.TryGetNode(landId, out var landNode))
                    feetPos = new Vector3(landNode.X, landNode.Y, landNode.Z);
                else
                    feetPos = TransformService.GetPosition(UnityObject.GetTransform(ch));

                var visualBounds = SpriteBoundsReader.Capture(ch, feetPos, camera);

                monsters.Add(new LiveMonsterInfo
                {
                    State = state,
                    CharacterRef = ch,
                    FeetWorldPos = feetPos,
                    VisualBounds = visualBounds
                });
            }

            // Sort monsters by action order (ActionWeight ASC), then stable MonsterId ASC
            monsters.Sort((a, b) =>
            {
                int w = a.State.ActionWeight.CompareTo(b.State.ActionWeight);
                if (w != 0) return w;
                return a.State.MonsterId.CompareTo(b.State.MonsterId);
            });

            // Disambiguate duplicate monster names ("迎宾燕 1", "迎宾燕 2") matching game convention
            var nameCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < monsters.Count; i++)
            {
                string n = monsters[i].State.Name;
                nameCounts.TryGetValue(n, out int cnt);
                nameCounts[n] = cnt + 1;
            }
            var seenIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < monsters.Count; i++)
            {
                string n = monsters[i].State.Name;
                if (nameCounts[n] > 1)
                {
                    seenIndex.TryGetValue(n, out int idx);
                    idx++;
                    seenIndex[n] = idx;
                    monsters[i].State.DisplayName = n + " " + idx;
                }
            }

            _rosterAliveMonsterIds.Clear();
            foreach (long id in rosterAliveIds) _rosterAliveMonsterIds.Add(id);

            // Always prune memories for monsters removed/dead from the alive roster (never gated on Count comparison
            // so replacing dead monster A with newly spawned B immediately removes A's stale memory).
            List<long> stale = null;
            foreach (var k in _memories.Keys)
            {
                if (!rosterAliveIds.Contains(k))
                {
                    if (stale == null) stale = new List<long>();
                    stale.Add(k);
                }
            }
            if (stale != null)
            {
                for (int i = 0; i < stale.Count; i++)
                {
                    _memories.Remove(stale[i]);
                    _pendingPlaceByMonster.Remove(stale[i]);
                }
            }
            // Also prune any orphaned pending Place records for non-alive/hero units that never enter _memories.
            List<long> stalePlaces = null;
            foreach (var k in _pendingPlaceByMonster.Keys)
            {
                if (!rosterAliveIds.Contains(k))
                {
                    if (stalePlaces == null) stalePlaces = new List<long>();
                    stalePlaces.Add(k);
                }
            }
            if (stalePlaces != null)
            {
                for (int i = 0; i < stalePlaces.Count; i++) _pendingPlaceByMonster.Remove(stalePlaces[i]);
            }
        }

        private bool IsMatchingEventStamp(long eventRoomId, long eventStartTime)
        {
            if (_matchRoomId == 0 && _matchStartTime == 0) return true;
            if (eventRoomId == 0 && eventStartTime == 0) return true;
            return eventRoomId == _matchRoomId && eventStartTime == _matchStartTime;
        }

        private void ResetForNewMatch(long newRoomId, long newStartTime)
        {
            _memories.Clear();
            _rosterAliveMonsterIds.Clear();
            _activeActorId = 0;
            _activeActorStartLandId = -1;
            _activeActorSteppedThisTurn = false;
            _turnSequence = 0;
            Interlocked.Exchange(ref _eventTurnEpoch, 0);
            _lastObservedTurnEpoch = 0;
            _lastObservedRound = -1;
            _matchRoomId = newRoomId;
            _matchStartTime = newStartTime;

            // Prune ONLY events belonging to previous matches; preserve events already received
            // and stamped for the new match before the first LateUpdate observed the room switch.
            for (int i = _recentSkills.Count - 1; i >= 0; i--)
            {
                if (!IsMatchingEventStamp(_recentSkills[i].MatchRoomId, _recentSkills[i].MatchStartTime))
                    _recentSkills.RemoveAt(i);
            }

            List<long> emptyMonsters = null;
            foreach (var kv in _pendingPlaceByMonster)
            {
                var list = kv.Value;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (!IsMatchingEventStamp(list[i].MatchRoomId, list[i].MatchStartTime))
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

            int skillCount = _skillQueue.Count;
            for (int i = 0; i < skillCount && _skillQueue.TryDequeue(out var sk); i++)
            {
                if (sk != null && IsMatchingEventStamp(sk.MatchRoomId, sk.MatchStartTime))
                    _skillQueue.Enqueue(sk);
            }
            int placeCount = _placeQueue.Count;
            for (int i = 0; i < placeCount && _placeQueue.TryDequeue(out var pl); i++)
            {
                if (pl != null && IsMatchingEventStamp(pl.MatchRoomId, pl.MatchStartTime))
                    _placeQueue.Enqueue(pl);
            }
            int combatCount = _combatQueue.Count;
            for (int i = 0; i < combatCount && _combatQueue.TryDequeue(out var c); i++)
            {
                if (IsMatchingEventStamp(c.MatchRoomId, c.MatchStartTime))
                    _combatQueue.Enqueue(c);
            }
            int stealCount = _thiefStealQueue.Count;
            for (int i = 0; i < stealCount && _thiefStealQueue.TryDequeue(out var ts); i++)
            {
                if (IsMatchingEventStamp(ts.MatchRoomId, ts.MatchStartTime))
                    _thiefStealQueue.Enqueue(ts);
            }
        }

        private bool EnsureSameMatch()
        {
            if (Players.TryGetMatch(out var info))
            {
                bool hadMatch = _matchRoomId != 0 || _matchStartTime != 0;
                if (_matchRoomId != info.RoomId || _matchStartTime != info.StartTime)
                {
                    ResetForNewMatch(info.RoomId, info.StartTime);
                    return hadMatch;
                }
            }
            return false;
        }

        private struct PendingUnitCombat
        {
            public int LandId;
            public long ActiveActor;
            public int TurnEpoch;
            public int Round;
        }

        private struct PendingThiefSteal
        {
            public int LandId;
            public int TurnEpoch;
            public int Round;
        }

        private void DrainPlaceQueue()
        {
            const int MaxPendingPlacesPerMonster = 16;
            while (_placeQueue.TryDequeue(out var pl))
            {
                if (pl == null || pl.TargetMonsterId == 0 || pl.NewLandId < 0) continue;
                if (!IsMatchingEventStamp(pl.MatchRoomId, pl.MatchStartTime)) continue;
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

        private void DrainEventQueues(
            out Dictionary<long, PendingUnitCombat> combatByUnit,
            out Dictionary<long, PendingThiefSteal> thiefStealsByUnit)
        {
            var now = DateTime.UtcNow;
            while (_skillQueue.TryDequeue(out var sk))
            {
                if (sk != null && IsMatchingEventStamp(sk.MatchRoomId, sk.MatchStartTime))
                    _recentSkills.Add(sk);
            }
            for (int i = _recentSkills.Count - 1; i >= 0; i--)
            {
                if ((now - _recentSkills[i].TimestampUtc).TotalSeconds > 2.5)
                {
                    _recentSkills.RemoveAt(i);
                }
            }

            // Target-bound 1040.Place events do NOT expire on a wall-clock timeout: they persist across
            // arbitrary render pauses until consumed on tile confirmation, superseded, monster death, or reset.
            DrainPlaceQueue();

            combatByUnit = new Dictionary<long, PendingUnitCombat>();
            while (_combatQueue.TryDequeue(out var c))
            {
                if (!IsMatchingEventStamp(c.MatchRoomId, c.MatchStartTime)) continue;
                if (c.AttackerId != 0)
                    combatByUnit[c.AttackerId] = new PendingUnitCombat
                    {
                        LandId = c.AttackerLandId,
                        ActiveActor = c.ActiveActorAtEvent,
                        TurnEpoch = c.TurnEpoch,
                        Round = c.Round
                    };
                if (c.DefenderId != 0)
                    combatByUnit[c.DefenderId] = new PendingUnitCombat
                    {
                        LandId = c.DefenderLandId,
                        ActiveActor = c.ActiveActorAtEvent,
                        TurnEpoch = c.TurnEpoch,
                        Round = c.Round
                    };
            }

            thiefStealsByUnit = new Dictionary<long, PendingThiefSteal>();
            while (_thiefStealQueue.TryDequeue(out var ts))
            {
                if (!IsMatchingEventStamp(ts.MatchRoomId, ts.MatchStartTime)) continue;
                if (ts.ThiefId != 0)
                    thiefStealsByUnit[ts.ThiefId] = new PendingThiefSteal
                    {
                        LandId = ts.LandId,
                        TurnEpoch = ts.TurnEpoch,
                        Round = ts.Round
                    };
            }
        }

        private ExactPlaceLookup TryConsumeExactPlaceDisplacement(
            BoardGraph board,
            long monsterId,
            MonsterMemory mem,
            int oldLandId,
            int newLandId,
            bool leftPreviousTile,
            Dictionary<long, int> playerPositions,
            IReadOnlyList<BattlePlayerData> allUnits)
        {
            // Match exact target-bound protocol Place event(s) (cmd 1040 UpdateHeroAttrS2C) carrying exact victim MonsterId.
            // Preserves ordered multi-hop history (e.g. A -> B then Lingling B -> F in the same sampling interval)
            // and never discards ahead-of-visual Place events before CharacterInst.standLand updates.
            if (!_pendingPlaceByMonster.TryGetValue(monsterId, out var list) || list.Count == 0)
            {
                return ExactPlaceLookup.NotFound;
            }

            int matchIdx = -1;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].NewLandId >= 0 && list[i].NewLandId == newLandId)
                {
                    matchIdx = i;
                    break;
                }
            }

            if (matchIdx < 0)
            {
                // Do NOT discard unmatched ahead-of-visual entries on first sample or before leaving oldLandId;
                // only prune entries explicitly targeting oldLandId once we confirmed moving away from oldLandId.
                if (leftPreviousTile && oldLandId >= 0)
                {
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        if (list[i].NewLandId == oldLandId) list.RemoveAt(i);
                    }
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
                    mem.SkillMoveKind = SkillMoveKind.None;
                    mem.SkillMoveLandId = -1;
                    mem.SkillMovePushDirId = -1;
                    mem.SkillMovePushFromId = -1;
                }

                // Exact 1040.Place records MUST specify an explicit active displacement SkillId;
                // never infer Shirley/Trashcan/Lingling from caster HeroId when Cause is absent (SkillId == 0).
                SkillMoveKind kind = IdentifyDisplacementSkill(rec.SkillId, rec.CasterPlayerId, allUnits, allowCasterHeroFallback: false);
                if (kind != SkillMoveKind.None)
                {
                    ConsumeRecentSkillCastForMonster(monsterId, kind, rec.CasterPlayerId, allUnits);

                    bool duplicateSameTileLingling =
                        !hopChangedTile &&
                        kind == SkillMoveKind.LinglingPush &&
                        mem.SkillMoveKind == SkillMoveKind.LinglingPush &&
                        mem.SkillMoveLandId == nextLand &&
                        mem.SkillMovePushFromId >= 0;

                    if (!duplicateSameTileLingling)
                    {
                        int pushOrigin = hopChangedTile
                            ? simLand
                            : (rec.BackLandId >= 0 && rec.BackLandId != nextLand && board.Contains(rec.BackLandId) ? rec.BackLandId : -1);
                        ApplyDisplacementTag(board, mem, kind, pushOrigin, nextLand, rec.CasterPlayerId, playerPositions);
                    }
                }
                // Same-tile ordinary Place (!hopChangedTile && kind == None) intentionally preserves any
                // already-active skill displacement tag on nextLand (matching two-frame sampling).

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

            for (int i = _recentSkills.Count - 1; i >= 0; i--)
            {
                var sk = _recentSkills[i];
                if (sk.AppliedMonsterIds.Contains(monsterId)) continue;
                SkillMoveKind skKind = IdentifyDisplacementSkill(sk.SkillId, sk.CasterPlayerId, allUnits, allowCasterHeroFallback: true);
                if (skKind != kind) continue;
                if (casterPlayerId != 0 && sk.CasterPlayerId != 0 && sk.CasterPlayerId != casterPlayerId) continue;

                // Mark matching cast(s) as consumed for this monster so a subsequent non-Place move
                // within the 2.5s window cannot re-attribute the already-applied cast.
                sk.AppliedMonsterIds.Add(monsterId);
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
            ApplyDisplacementTag(board, mem, uniqueKind, oldLandId, newLandId, uniqueCast.CasterPlayerId, playerPositions);
        }

        private static void ApplyDisplacementTag(
            BoardGraph board,
            MonsterMemory mem,
            SkillMoveKind kind,
            int oldLandId,
            int newLandId,
            long casterPlayerId,
            Dictionary<long, int> playerPositions)
        {
            mem.SkillMoveKind = kind;
            mem.SkillMoveLandId = newLandId;
            mem.SkillMovePushDirId = -1;
            mem.SkillMovePushFromId = -1;
            mem.JustSpawnedOnFork = false;

            if (kind == SkillMoveKind.LinglingPush)
            {
                InferLinglingPushDirection(board, oldLandId, newLandId, casterPlayerId, playerPositions,
                    out int pushNextId, out int pushFromId);
                mem.SkillMovePushDirId = pushNextId;
                mem.SkillMovePushFromId = pushFromId;
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

        private static TrackedPlayer BuildTrackedPlayer(BattlePlayerData pd, PredictorSettings settings, out bool isTrueCulprit)
        {
            isTrueCulprit = false;
            try
            {
                var p = pd.player;
                var prop = pd.Property;
                SafeHp(pd, out int hp, out int maxHp);

                bool dead = false;
                try { dead = prop.HealthState == HealthState.Dead; } catch { }
                if (!dead && maxHp > 0 && hp <= 0) dead = true;

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

        private static void SafeHp(BattlePlayerData pd, out int hp, out int maxHp)
        {
            hp = 0;
            maxHp = 0;
            bool gotHp = false;
            bool gotMaxHp = false;
            try
            {
                var prop = pd.Property;
                if (prop != null)
                {
                    if (prop.HP != null)
                    {
                        hp = prop.HP.Value;
                        gotHp = true;
                    }
                    if (prop.maxHP > 0)
                    {
                        maxHp = prop.maxHP;
                        gotMaxHp = true;
                    }
                }
            }
            catch { }
            if (!gotHp || !gotMaxHp)
            {
                try
                {
                    var hero = pd.player != null ? pd.player.Hero : null;
                    if (hero != null)
                    {
                        if (!gotHp) hp = hero.Hp;
                        if (!gotMaxHp) maxHp = hero.MaxHp;
                    }
                }
                catch { }
            }
        }

        private static bool IsRosterMonsterAlive(BattlePlayerData pd)
        {
            try
            {
                var prop = pd.Property;
                if (prop != null)
                {
                    if (prop.HealthState == HealthState.Dead) return false;
                    if (prop.maxHP > 0 && prop.HP != null && prop.HP.Value <= 0) return false;
                }
                var hero = pd.player != null ? pd.player.Hero : null;
                if (hero != null && hero.MaxHp > 0 && hero.Hp <= 0) return false;
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

                bool sawAnyMonster = false;
                for (int i = 0; i < allUnits.Count; i++)
                {
                    var pd = allUnits[i];
                    if (pd == null || pd.player == null) continue;
                    try { if (pd.characterType == CharacterType.Monster) sawAnyMonster = true; } catch { }
                    if (pd.player.Id != monsterId) continue;

                    var prop = pd.Property;
                    if (prop != null)
                    {
                        if (prop.HealthState == HealthState.Dead) return true;
                        if (prop.maxHP > 0 && prop.HP != null && prop.HP.Value <= 0) return true;
                    }

                    var hero = pd.player.Hero;
                    if (hero != null && hero.MaxHp > 0 && hero.Hp <= 0) return true;

                    // Still listed in battle roster with positive HP -> NOT confirmed dead
                    // (even if CharacterInst is momentarily null/rebuilding during reconnect/animation).
                    return false;
                }

                // Roster is populated with monsters and this ID is gone -> confirmed dead/despawned.
                return sawAnyMonster;
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

        private static int SafeFromLandId(BattlePlayerData pd)
        {
            if (pd == null) return -1;
            try
            {
                if (pd.CharacterInst != null && pd.CharacterInst.fromLandId >= 0)
                    return pd.CharacterInst.fromLandId;
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

        private static readonly MethodInfo CharacterGetFrontNodeIds = typeof(Character).GetMethod(
            "GetFrontNodeIds",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);

        private static int[] SafeFrontNodeIds(BattlePlayerData pd)
        {
            // 1. Live visual facing arrows from Character.GetFrontNodeIds() (authoritative on forks!)
            try
            {
                var ch = pd.CharacterInst;
                if (CharacterGetFrontNodeIds != null && UnityObject.IsAlive(ch))
                {
                    object raw = CharacterGetFrontNodeIds.Invoke(ch, null);
                    if (raw is int[] intArr && intArr.Length > 0)
                    {
                        var copy = new int[intArr.Length];
                        Array.Copy(intArr, copy, intArr.Length);
                        return copy;
                    }
                    if (raw is IList<int> intList && intList.Count > 0)
                    {
                        var arr = new int[intList.Count];
                        for (int i = 0; i < intList.Count; i++) arr[i] = intList[i];
                        return arr;
                    }
                    if (raw is System.Collections.IEnumerable seq)
                    {
                        var buf = new List<int>();
                        foreach (var item in seq)
                        {
                            if (item != null) buf.Add(Convert.ToInt32(item));
                        }
                        if (buf.Count > 0) return buf.ToArray();
                    }
                }
            }
            catch { }

            // 2. RoomPlayer.FrontIds
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

            // 3. Protobuf Hero.FrontNodeIds
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

        private static string ResolveMonsterName(BattlePlayerData pd, int heroId)
        {
            try
            {
                if (heroId > 0)
                {
                    string n = CharacterHandle.GetCharacterName(heroId, CharacterType.Monster);
                    if (!string.IsNullOrEmpty(n)) return n;
                }
            }
            catch { }
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
