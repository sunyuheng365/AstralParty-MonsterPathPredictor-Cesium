using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using MonsterPathPredictor.Core;

namespace MonsterPathPredictor.ReplayCertifier
{
    internal enum StreamLifecycleState
    {
        Unstarted = 0,
        Running = 1,
        Finished = 2
    }

    internal static partial class ReplayEvaluator
    {
        private sealed class UnitSim
        {
            public long Id;
            public int Slot;
            public int HeroId;
            public bool IsMonster;
            public int MonsterType; // 1=Thief, 2=Boss, 10=PveBoss, 11=PveMonster, 12=PveElite, 30=PveAlly
            public int NodeId;
            public int BackNodeId;
            public int[] FrontNodeIds = Array.Empty<int>();
            public int Hp;
            public int Gold;
            public int CrimeNum;
            public bool NotSelect;
            public int SkillDisplacedFromNode = -1;
            public int SkillDisplacedToNode = -1;
            public int TauntSkillId;
            public bool WardenRevealed;
            public HashSet<int> BuffIds = new();
        }

        public static CertificationSummary EvaluateBytes(string replayId, byte[] bytes, IProtocolDecoder decoder, BoardCatalog catalog)
        {
            var summary = new CertificationSummary { ReplaysAttempted = 1 };
            var (frames, isTruncated) = ReadFrames(bytes);
            if (frames.Count == 0)
            {
                summary.TruncatedOrCorrupt = 1;
                return summary;
            }

            BoardGraph board = null;
            MapStats mapStat = null;
            int mapId = 0;
            int mapIndex = 0;
            int curRound = 0;
            int turnStartLandId = -1;
            bool startedOnFork = false, stepped = false, foughtOnTile = false, foughtTurn = false, thiefStoleOnTile = false;

            var players = new Dictionary<long, UnitSim>();
            var monsters = new Dictionary<long, UnitSim>();
            var seenMonsters = new HashSet<long>();
            var spawnedAtTile = new Dictionary<long, int>();
            var pendingMoveEffects = new Dictionary<long, Dictionary<int, List<dynamic>>>();
            long curTurnActor = 0;
            bool unknownLayout = false;
            bool parseError = false;
            bool decodeFailed = false;
            var lifecycle = StreamLifecycleState.Unstarted;

            void SyncRoom(dynamic room)
            {
                int newIdx = (int)room.MapIndex;
                if (board == null || newIdx != mapIndex)
                {
                    if (catalog.TryGetGraph(mapId, newIdx, out var nb)) { mapIndex = newIdx; board = nb; }
                    else { unknownLayout = true; return; }
                }
                pendingMoveEffects.Clear();
                var prevDisp = new Dictionary<long, (int From, int To)>();
                foreach (var kv in monsters)
                {
                    if (kv.Value.SkillDisplacedToNode >= 0)
                        prevDisp[kv.Key] = (kv.Value.SkillDisplacedFromNode, kv.Value.SkillDisplacedToNode);
                }
                players.Clear();
                monsters.Clear();
                if (room.Players != null)
                    foreach (dynamic p in (IEnumerable)room.Players)
                        if (p?.Hero != null) { UnitSim u = BuildUnit(p, false); players[u.Id] = u; }
                if (room.Monsters != null)
                    foreach (dynamic m in (IEnumerable)room.Monsters)
                        if (m?.Hero != null)
                        {
                            UnitSim u = BuildUnit(m, true);
                            if (prevDisp.TryGetValue(u.Id, out var disp) && u.NodeId == disp.To)
                            {
                                u.SkillDisplacedFromNode = disp.From;
                                u.SkillDisplacedToNode = disp.To;
                            }
                            monsters[u.Id] = u;
                            if (seenMonsters.Add(u.Id)) spawnedAtTile[u.Id] = u.NodeId;
                        }
            }

            for (int fi = 0; fi < frames.Count && !unknownLayout && !decodeFailed && !parseError; fi++)
            {
                var (cmd, payload) = frames[fi];
                bool isStateFrame = cmd == 1003 || cmd == 1016 || cmd == 1113 || cmd == 1018 || cmd == 5022 ||
                                    cmd == 1040 || cmd == 1096 || cmd == 5214 || cmd == 1007 || cmd == 1115 || cmd == 5028;
                if (payload.Length == 0)
                {
                    // State-carrying protocol frames must never have empty payload
                    if (isStateFrame) decodeFailed = true;
                    continue;
                }

                // Strict stream lifecycle state machine: Unstarted -> (1003) -> Running -> (1016) -> Finished
                if (cmd == 1003)
                {
                    if (lifecycle != StreamLifecycleState.Unstarted) { decodeFailed = true; break; }
                    dynamic running = decoder.TryDecode("party.protocol.RunningGameS2C", payload);
                    if (running?.Room == null) { parseError = true; break; }

                    mapId = (int)running.Room.MapId;
                    mapIndex = (int)running.Room.MapIndex;
                    if (mapId < 82000 || mapId >= 84000) { summary.SkippedNonPve = 1; return summary; }
                    if (!catalog.TryGetGraph(mapId, mapIndex, out board)) { unknownLayout = true; break; }

                    mapStat = new MapStats();
                    summary.ByMap[mapId] = mapStat;
                    SyncRoom(running.Room);
                    if (unknownLayout) break;
                    curRound = (int)running.Room.Round;
                    lifecycle = StreamLifecycleState.Running;
                    continue;
                }

                if (lifecycle == StreamLifecycleState.Unstarted)
                {
                    if (cmd == 1040)
                    {
                        // Real replays may prepend pre-match setup 1040 attr frames before 1003; verify decodable only.
                        if (decoder.TryDecode("party.protocol.UpdateHeroAttrS2C", payload) == null)
                        {
                            decodeFailed = true;
                            break;
                        }
                        continue;
                    }
                    if (isStateFrame) { decodeFailed = true; break; }
                    continue;
                }

                if (lifecycle == StreamLifecycleState.Finished)
                {
                    if (cmd == 1040)
                    {
                        // Real replays may append post-finish settlement 1040 attr frames; verify decodable only.
                        if (decoder.TryDecode("party.protocol.UpdateHeroAttrS2C", payload) == null)
                        {
                            decodeFailed = true;
                            break;
                        }
                        continue;
                    }
                    if (isStateFrame) { decodeFailed = true; break; }
                    continue;
                }

                if (cmd == 1016)
                {
                    dynamic fin = decoder.TryDecode("party.protocol.GameFinishS2C", payload);
                    if (fin == null) { decodeFailed = true; break; }
                    lifecycle = StreamLifecycleState.Finished;
                }
                else if (cmd == 1113)
                {
                    dynamic snap = decoder.TryDecode("party.protocol.ReplaySnapshotS2C", payload);
                    if (snap?.Room == null) { decodeFailed = true; break; }
                    SyncRoom(snap.Room);
                    if (unknownLayout) break;
                    curRound = (int)snap.Room.Round;
                    curTurnActor = (long)snap.PlayerId;
                    stepped = foughtOnTile = foughtTurn = thiefStoleOnTile = false;
                    turnStartLandId = monsters.TryGetValue(curTurnActor, out var am) ? am.NodeId : -1;
                    startedOnFork = am != null && board.IsFork(am.NodeId);
                }
                else if (cmd == 1018)
                {
                    dynamic mr = decoder.TryDecode("party.protocol.MonsterRefreshS2C", payload);
                    if (mr == null) { decodeFailed = true; break; }
                    if (mr.Monster?.Hero != null)
                    {
                        UnitSim u = BuildUnit(mr.Monster, true);
                        monsters[u.Id] = u;
                        seenMonsters.Add(u.Id);
                        spawnedAtTile[u.Id] = u.NodeId;
                    }
                }
                else if (cmd == 5022)
                {
                    dynamic td = decoder.TryDecode("party.protocol.ThrowDiceS2C", payload);
                    if (td == null) { decodeFailed = true; break; }
                    long actorId = (long)td.PlayerId;
                    // Discard unreached deferred 1096 tile effects from the previous actor instead of falsely applying them.
                    if (curTurnActor != 0) pendingMoveEffects.Remove(curTurnActor);
                    // Always advance active turn context even when a player (non-monster) rolls dice.
                    curTurnActor = actorId;
                    stepped = foughtOnTile = foughtTurn = thiefStoleOnTile = false;
                    if (monsters.TryGetValue(actorId, out var ma))
                    {
                        turnStartLandId = ma.NodeId;
                        startedOnFork = board.IsFork(ma.NodeId);
                    }
                    else
                    {
                        turnStartLandId = -1;
                        startedOnFork = false;
                    }
                }
                else if (cmd == 1040)
                {
                    dynamic attr = decoder.TryDecode("party.protocol.UpdateHeroAttrS2C", payload);
                    if (attr == null) { decodeFailed = true; continue; }
                    if (ApplyAttr(attr, players, monsters, false, curTurnActor))
                        thiefStoleOnTile = true;
                }
                else if (cmd == 1096)
                {
                    dynamic sm = decoder.TryDecode("party.protocol.HeroSkillMoveEffectS2C", payload);
                    if (sm == null) { decodeFailed = true; continue; }
                    long moverId = (long)sm.PlayerId;
                    UnitSim moverUnit = null;
                    if (moverId != 0 && !players.TryGetValue(moverId, out moverUnit))
                        monsters.TryGetValue(moverId, out moverUnit);

                    if (sm.EffectData != null)
                    {
                        foreach (dynamic kv in (IEnumerable)sm.EffectData)
                        {
                            if (kv?.Value?.Effects == null) continue;
                            int triggerTile = Convert.ToInt32(kv.Key);
                            bool applyNow = moverUnit == null || triggerTile < 0 || moverUnit.NodeId == triggerTile;
                            foreach (dynamic attr in (IEnumerable)kv.Value.Effects)
                            {
                                if (applyNow)
                                {
                                    ApplyAttr(attr, players, monsters, true, curTurnActor);
                                }
                                else
                                {
                                    if (!pendingMoveEffects.TryGetValue(moverId, out var byTile))
                                        pendingMoveEffects[moverId] = byTile = new Dictionary<int, List<dynamic>>();
                                    if (!byTile.TryGetValue(triggerTile, out var list))
                                        byTile[triggerTile] = list = new List<dynamic>();
                                    list.Add(attr);
                                }
                            }
                        }
                    }
                }
                else if (cmd == 5214)
                {
                    dynamic mp = decoder.TryDecode("party.protocol.MonsterPursuitS2C", payload);
                    if (mp == null) { decodeFailed = true; continue; }
                    if ((bool)mp.Exit) continue;

                    long mpid = (long)mp.PlayerId;
                    UnitSim targetUnit = null;
                    if (!players.TryGetValue(mpid, out targetUnit))
                        monsters.TryGetValue(mpid, out targetUnit);
                    if (targetUnit != null)
                    {
                        int nid = (int)mp.NodeId;
                        if (nid >= 0) targetUnit.NodeId = nid;
                        targetUnit.BackNodeId = (int)mp.BackId;
                        targetUnit.FrontNodeIds = ToIntArray((IEnumerable)mp.FrontIds);
                    }
                }
                else if (cmd == 1007)
                {
                    dynamic b = decoder.TryDecode("party.protocol.BattleS2C", payload);
                    if (b == null) { decodeFailed = true; continue; }
                    if (b.Battle != null)
                    {
                        long atk = b.Battle.Attacker != null ? (long)b.Battle.Attacker.PlayerId : 0;
                        long def = b.Battle.Defender != null ? (long)b.Battle.Defender.PlayerId : 0;
                        if (atk == curTurnActor || def == curTurnActor) foughtOnTile = foughtTurn = true;
                    }
                }
                else if (cmd == 1115)
                {
                    dynamic die = decoder.TryDecode("party.protocol.ReplayDieS2C", payload);
                    if (die == null) { decodeFailed = true; continue; }
                    long pid = (long)die.PlayerId;
                    if (players.TryGetValue(pid, out var dp)) dp.Hp = 0;
                    if (monsters.TryGetValue(pid, out var dm)) dm.Hp = 0;
                }
                else if (cmd == 5028)
                {
                    dynamic mv = decoder.TryDecode("party.protocol.MoveS2C", payload);
                    if (mv == null) { decodeFailed = true; continue; }
                    HandleMove(mv, replayId, mapId, curRound, fi, board, players, monsters, spawnedAtTile, pendingMoveEffects,
                        curTurnActor, startedOnFork, turnStartLandId, ref stepped, ref foughtOnTile, foughtTurn, ref thiefStoleOnTile, mapStat, summary);
                }
            }

            if (unknownLayout)
            {
                summary.MissingMap = 1;
            }
            else if (parseError)
            {
                summary.ParseErrors = 1;
            }
            else if (isTruncated || decodeFailed || lifecycle != StreamLifecycleState.Finished)
            {
                summary.TruncatedOrCorrupt = 1;
            }
            else
            {
                summary.ReplaysVerified = 1;
                if (mapStat != null) mapStat.Replays = 1;
            }

            return summary;
        }
    }
}
