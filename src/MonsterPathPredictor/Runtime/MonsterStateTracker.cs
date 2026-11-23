using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CesiumLoader.SDK.Engine;
using CesiumLoader.SDK.Gameplay;
using Core.Unit;
using GameLogic;
using party.model;
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
            public SkillMoveKind SkillMoveKind = SkillMoveKind.None;
            public int SkillMoveLandId = -1;
            public int SkillMovePushDirId = -1;
            public int SkillMovePushFromId = -1;
        }

        private struct SkillCastRecord
        {
            public long CasterPlayerId;
            public int SkillId;
            public DateTime TimestampUtc;
        }

        private struct CombatRecord
        {
            public long AttackerId;
            public long DefenderId;
        }

        private readonly ConcurrentQueue<SkillCastRecord> _skillQueue = new ConcurrentQueue<SkillCastRecord>();
        private readonly ConcurrentQueue<CombatRecord> _combatQueue = new ConcurrentQueue<CombatRecord>();

        private readonly Dictionary<long, MonsterMemory> _memories = new Dictionary<long, MonsterMemory>();
        private readonly List<SkillCastRecord> _recentSkills = new List<SkillCastRecord>();

        private long _activeActorId;
        private int _activeActorStartLandId = -1;
        private bool _activeActorSteppedThisTurn;

        private long _matchRoomId;
        private long _matchStartTime;
        private bool _hooked;

        public void Start()
        {
            if (_hooked) return;
            _hooked = true;
            GameEvents.FightResolved += OnFightResolved;
            GameEvents.BattleUpdate += OnBattleUpdate;
            GameEvents.SkillUsed += OnSkillUsed;
            GameEvents.StartAutoHook();
        }

        public void Stop()
        {
            if (!_hooked) return;
            _hooked = false;
            GameEvents.FightResolved -= OnFightResolved;
            GameEvents.BattleUpdate -= OnBattleUpdate;
            GameEvents.SkillUsed -= OnSkillUsed;
            Reset();
        }

        public void Reset()
        {
            _memories.Clear();
            _recentSkills.Clear();
            while (_skillQueue.TryDequeue(out _)) { }
            while (_combatQueue.TryDequeue(out _)) { }
            _activeActorId = 0;
            _activeActorStartLandId = -1;
            _activeActorSteppedThisTurn = false;
            _matchRoomId = 0;
            _matchStartTime = 0;
        }

        private void OnFightResolved(FightSnapshot snap)
        {
            if (snap.AttackerPlayerId == 0 && snap.DefenderPlayerId == 0) return;
            _combatQueue.Enqueue(new CombatRecord
            {
                AttackerId = snap.AttackerPlayerId,
                DefenderId = snap.DefenderPlayerId
            });
        }

        private void OnBattleUpdate(Battle b)
        {
            if (b == null) return;
            long atk = b.Attacker != null ? b.Attacker.PlayerId : 0;
            long def = b.Defender != null ? b.Defender.PlayerId : 0;
            if (atk != 0 || def != 0)
            {
                _combatQueue.Enqueue(new CombatRecord { AttackerId = atk, DefenderId = def });
            }
        }

        private void OnSkillUsed(long playerId, int skillId)
        {
            _skillQueue.Enqueue(new SkillCastRecord
            {
                CasterPlayerId = playerId,
                SkillId = skillId,
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
            out List<TrackedPlayer> players)
        {
            monsters = new List<LiveMonsterInfo>();
            players = new List<TrackedPlayer>();

            EnsureSameMatch();
            DrainEventQueues();

            if (board == null || battle == null) return;

            var allUnits = Players.All();
            if (allUnits == null || allUnits.Count == 0) return;

            var currentActor = SafeGetCurrentPlayer(battle);
            long curActorId = (currentActor != null && currentActor.player != null) ? currentActor.player.Id : 0;
            if (curActorId != _activeActorId)
            {
                _activeActorId = curActorId;
                _activeActorStartLandId = SafeLandId(currentActor);
                _activeActorSteppedThisTurn = false;
            }
            else if (curActorId != 0)
            {
                int nowLand = SafeLandId(currentActor);
                if (nowLand >= 0 && _activeActorStartLandId >= 0 && nowLand != _activeActorStartLandId)
                {
                    _activeActorSteppedThisTurn = true;
                }
            }

            var playerPositions = new Dictionary<long, int>();
            var aliveMonsterIds = new HashSet<long>();

            for (int i = 0; i < allUnits.Count; i++)
            {
                var pd = allUnits[i];
                if (pd == null || pd.player == null) continue;

                CharacterType cType = CharacterType.None;
                try { cType = pd.characterType; } catch { }

                if (cType == CharacterType.Hero)
                {
                    var tp = BuildTrackedPlayer(pd, settings);
                    if (tp != null)
                    {
                        players.Add(tp);
                        if (tp.LandId >= 0) playerPositions[tp.PlayerId] = tp.LandId;
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

                if (!IsMonsterAliveAndActive(pd)) continue;

                var ch = pd.CharacterInst;
                if (!UnityObject.IsAlive(ch)) continue;

                int landId = SafeLandId(pd);
                if (!board.Contains(landId)) continue;

                long mid = pd.player.Id;
                aliveMonsterIds.Add(mid);

                int fromLandId = SafeFromLandId(pd);
                int[] frontIds = SafeFrontNodeIds(pd);
                int canStep = SafeCanStep(ch);

                bool isNew = !_memories.TryGetValue(mid, out var mem);
                if (isNew)
                {
                    mem = new MonsterMemory
                    {
                        LastLandId = landId,
                        LastFromLandId = fromLandId,
                        JustSpawnedOnFork = board.IsFork(landId) && (frontIds.Length == 1 || !board.IsValidNeighbor(landId, fromLandId))
                    };
                    _memories[mid] = mem;
                }
                else if (mem.LastLandId != landId)
                {
                    bool isMonsterOwnTurn = (curActorId == mid);
                    bool movedNonAdjacently = mem.LastLandId >= 0 && !board.IsValidNeighbor(mem.LastLandId, landId);

                    if (!isMonsterOwnTurn || movedNonAdjacently)
                    {
                        TryTagSkillDisplacement(board, mem, mem.LastLandId, landId, playerPositions, allUnits);
                    }
                    else
                    {
                        mem.JustSpawnedOnFork = false;
                        if (mem.CombatForkLandId != landId)
                        {
                            mem.JustFoughtOnFork = false;
                            mem.CombatForkLandId = -1;
                        }
                        if (mem.SkillMoveLandId != landId)
                        {
                            mem.SkillMoveKind = SkillMoveKind.None;
                            mem.SkillMoveLandId = -1;
                            mem.SkillMovePushDirId = -1;
                            mem.SkillMovePushFromId = -1;
                        }
                    }

                    mem.LastLandId = landId;
                    mem.LastFromLandId = fromLandId;
                }

                bool justFought = mem.JustFoughtOnFork && mem.CombatForkLandId == landId && board.IsFork(landId);
                bool isMovingMidTurn = (curActorId == mid) && _activeActorSteppedThisTurn;
                bool launchedFightThisTurn = (curActorId == mid) && SafeIsLaunchFight(pd);

                int heroId = SafeHeroId(pd);
                string monsterName = ResolveMonsterName(pd, heroId);
                bool isNormalAggressive = IsNormalAggressiveMonster(pd);
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
                    ActionWeight = actionWeight,
                    CurrentLandId = landId,
                    FromLandId = fromLandId,
                    FrontNodeIds = frontIds,
                    CanStep = canStep,
                    IsCurrentlyMovingMidTurn = isMovingMidTurn,
                    JustFoughtOnCurrentFork = justFought,
                    HasLaunchedFightThisTurn = launchedFightThisTurn,
                    JustSpawnedOnFork = mem.JustSpawnedOnFork && board.IsFork(landId),
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

            if (_memories.Count > aliveMonsterIds.Count)
            {
                var stale = new List<long>();
                foreach (var k in _memories.Keys)
                {
                    if (!aliveMonsterIds.Contains(k)) stale.Add(k);
                }
                for (int i = 0; i < stale.Count; i++) _memories.Remove(stale[i]);
            }
        }

        private void EnsureSameMatch()
        {
            if (Players.TryGetMatch(out var info))
            {
                if (_matchRoomId != info.RoomId || _matchStartTime != info.StartTime)
                {
                    Reset();
                    _matchRoomId = info.RoomId;
                    _matchStartTime = info.StartTime;
                }
            }
        }

        private void DrainEventQueues()
        {
            var now = DateTime.UtcNow;
            while (_skillQueue.TryDequeue(out var sk))
            {
                _recentSkills.Add(sk);
            }
            for (int i = _recentSkills.Count - 1; i >= 0; i--)
            {
                if ((now - _recentSkills[i].TimestampUtc).TotalSeconds > 4.0)
                {
                    _recentSkills.RemoveAt(i);
                }
            }

            while (_combatQueue.TryDequeue(out var c))
            {
                MarkCombatIfMonster(c.AttackerId);
                MarkCombatIfMonster(c.DefenderId);
            }
        }

        private void MarkCombatIfMonster(long unitId)
        {
            if (unitId == 0) return;
            if (_memories.TryGetValue(unitId, out var mem) && mem.LastLandId >= 0)
            {
                mem.JustFoughtOnFork = true;
                mem.CombatForkLandId = mem.LastLandId;
            }
        }

        private void TryTagSkillDisplacement(
            BoardGraph board,
            MonsterMemory mem,
            int oldLandId,
            int newLandId,
            Dictionary<long, int> playerPositions,
            IReadOnlyList<BattlePlayerData> allUnits)
        {
            if (_recentSkills.Count == 0) return;

            for (int i = _recentSkills.Count - 1; i >= 0; i--)
            {
                var sk = _recentSkills[i];
                SkillMoveKind kind = IdentifySkillMoveHero(sk.CasterPlayerId, allUnits);
                if (kind == SkillMoveKind.None) continue;

                mem.SkillMoveKind = kind;
                mem.SkillMoveLandId = newLandId;
                mem.SkillMovePushDirId = -1;
                mem.SkillMovePushFromId = -1;
                mem.JustSpawnedOnFork = false;

                if (kind == SkillMoveKind.LinglingPush)
                {
                    InferLinglingPushDirection(board, oldLandId, newLandId, sk.CasterPlayerId, playerPositions,
                        out int pushNextId, out int pushFromId);
                    mem.SkillMovePushDirId = pushNextId;
                    mem.SkillMovePushFromId = pushFromId;
                }
                return;
            }
        }

        private static SkillMoveKind IdentifySkillMoveHero(long casterPlayerId, IReadOnlyList<BattlePlayerData> allUnits)
        {
            for (int i = 0; i < allUnits.Count; i++)
            {
                var pd = allUnits[i];
                if (pd == null || pd.player == null || pd.player.Id != casterPlayerId) continue;
                int heroId = SafeHeroId(pd);
                string heroName = string.Empty;
                try { heroName = Names.Character(heroId) ?? string.Empty; } catch { }

                if (heroName.Contains("垃圾箱") || heroName.Contains("垃圾桶") || heroName.Contains("Z3000"))
                    return SkillMoveKind.TrashcanPull;
                if (heroName.Contains("雪莉"))
                    return SkillMoveKind.ShirleyThrow;
                if (heroName.Contains("玲玲"))
                    return SkillMoveKind.LinglingPush;
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

            var field = board.ComputeShortestPathsTo(origin);
            int d = field.GetDistance(newLandId);
            if (d > 0 && d < int.MaxValue)
            {
                var neighbors = board.GetValidNeighbors(newLandId);
                for (int i = 0; i < neighbors.Count; i++)
                {
                    if (field.GetDistance(neighbors[i]) == d - 1)
                    {
                        pushFromId = neighbors[i];
                        break;
                    }
                }
            }

            if (pushFromId >= 0)
            {
                var fwd = board.GetForwardNeighbors(newLandId, pushFromId);
                if (fwd.Count == 1) pushNextId = fwd[0];
            }
        }

        private static TrackedPlayer BuildTrackedPlayer(BattlePlayerData pd, PredictorSettings settings)
        {
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

                if (!notSelect && settings != null && settings.ExcludedPlayerBuffIds != null && settings.ExcludedPlayerBuffIds.Count > 0)
                {
                    var buffs = Players.BuffsOf(p.Id);
                    for (int i = 0; i < buffs.Count; i++)
                    {
                        if (settings.ExcludedPlayerBuffIds.Contains(buffs[i].BuffId))
                        {
                            notSelect = true;
                            break;
                        }
                    }
                }

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

                return new TrackedPlayer
                {
                    PlayerId = p.Id,
                    Slot = slot,
                    Name = nick,
                    LandId = landId,
                    IsAlive = !dead,
                    IsTargetable = !notSelect
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
            try
            {
                var prop = pd.Property;
                if (prop != null)
                {
                    if (prop.HP != null) hp = prop.HP.Value;
                    maxHp = prop.maxHP;
                }
            }
            catch { }
            if (maxHp <= 0)
            {
                try
                {
                    var hero = pd.player != null ? pd.player.Hero : null;
                    if (hero != null) { hp = hero.Hp; maxHp = hero.MaxHp; }
                }
                catch { }
            }
        }

        private static bool IsMonsterAliveAndActive(BattlePlayerData pd)
        {
            try
            {
                var prop = pd.Property;
                if (prop != null)
                {
                    if (prop.HealthState == HealthState.Dead) return false;
                    if (prop.maxHP > 0 && prop.HP != null && prop.HP.Value <= 0) return false;
                }
                var ch = pd.CharacterInst;
                if (!UnityObject.IsAlive(ch)) return false;
                return true;
            }
            catch
            {
                return false;
            }
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

        private static int[] SafeFrontNodeIds(BattlePlayerData pd)
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
