using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using MonsterPathPredictor.Core;

namespace MonsterPathPredictor.ReplayCertifier
{
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
            public bool DisplacedBySkill;
            public int TauntSkillId;
            public bool WardenRevealed;
            public HashSet<int> BuffIds = new();
        }

        public static CertificationSummary EvaluateBytes(string replayId, byte[] bytes, ProtocolDecoder decoder, BoardCatalog catalog)
        {
            var summary = new CertificationSummary { ReplaysAttempted = 1 };
            var (frames, isTruncated) = ReadFrames(bytes);
            if (frames.Count == 0)
            {
                summary.TruncatedOrCorrupt = 1;
                return summary;
            }

            var runningFrame = frames.FirstOrDefault(f => f.CmdId == 1003 && f.Payload.Length > 0);
            if (runningFrame.Payload == null)
            {
                summary.TruncatedOrCorrupt = 1;
                return summary;
            }

            dynamic running = decoder.TryDecode("party.protocol.RunningGameS2C", runningFrame.Payload);
            if (running?.Room == null)
            {
                summary.ParseErrors = 1;
                return summary;
            }

            int mapId = (int)running.Room.MapId;
            int mapIndex = (int)running.Room.MapIndex;
            if (mapId < 82000 || mapId >= 84000) { summary.SkippedNonPve = 1; return summary; }
            if (!catalog.TryGetGraph(mapId, mapIndex, out var board)) { summary.MissingMap = 1; return summary; }

            var mapStat = new MapStats();
            summary.ByMap[mapId] = mapStat;

            var players = new Dictionary<long, UnitSim>();
            var monsters = new Dictionary<long, UnitSim>();
            var seenMonsters = new HashSet<long>();
            var spawnedAtTile = new Dictionary<long, int>();
            bool unknownLayout = false;
            bool decodeFailed = false;

            void SyncRoom(dynamic room)
            {
                int newIdx = (int)room.MapIndex;
                if (newIdx != mapIndex)
                {
                    if (catalog.TryGetGraph(mapId, newIdx, out var nb)) { mapIndex = newIdx; board = nb; }
                    else { unknownLayout = true; return; }
                }
                var prevDisp = monsters.Where(kv => kv.Value.DisplacedBySkill).Select(kv => kv.Key).ToHashSet();
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
                            if (prevDisp.Contains(u.Id)) u.DisplacedBySkill = true;
                            monsters[u.Id] = u;
                            if (seenMonsters.Add(u.Id)) spawnedAtTile[u.Id] = u.NodeId;
                        }
            }

            SyncRoom(running.Room);

            int curRound = (int)running.Room.Round;
            long curTurnActor = 0;
            int turnStartLandId = -1;
            bool startedOnFork = false, stepped = false, foughtOnTile = false, foughtTurn = false, thiefStoleOnTile = false;

            for (int fi = 0; fi < frames.Count && !unknownLayout; fi++)
            {
                var (cmd, payload) = frames[fi];
                if (payload.Length == 0) continue;

                if (cmd == 1113)
                {
                    dynamic snap = decoder.TryDecode("party.protocol.ReplaySnapshotS2C", payload);
                    if (snap?.Room == null) { decodeFailed = true; continue; }
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
                    if (mr == null) { decodeFailed = true; continue; }
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
                    if (td == null) { decodeFailed = true; continue; }
                    long actorId = (long)td.PlayerId;
                    if (monsters.TryGetValue(actorId, out var ma))
                    {
                        curTurnActor = ma.Id;
                        stepped = foughtOnTile = foughtTurn = thiefStoleOnTile = false;
                        turnStartLandId = ma.NodeId;
                        startedOnFork = board.IsFork(ma.NodeId);
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
                    if (sm.EffectData != null)
                        foreach (dynamic kv in (IEnumerable)sm.EffectData)
                            if (kv?.Value?.Effects != null)
                                foreach (dynamic attr in (IEnumerable)kv.Value.Effects)
                                    ApplyAttr(attr, players, monsters, true, curTurnActor);
                }
                else if (cmd == 5214)
                {
                    dynamic mp = decoder.TryDecode("party.protocol.MonsterPursuitS2C", payload);
                    if (mp == null) { decodeFailed = true; continue; }
                    long mpid = (long)mp.PlayerId;
                    if (monsters.TryGetValue(mpid, out var pm))
                    {
                        int nid = (int)mp.NodeId;
                        if (nid >= 0) pm.NodeId = nid;
                        pm.BackNodeId = (int)mp.BackId;
                        int[] fids = ToIntArray((IEnumerable)mp.FrontIds);
                        if (fids.Length > 0) pm.FrontNodeIds = fids;
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
                    HandleMove(mv, replayId, mapId, curRound, fi, board, players, monsters, spawnedAtTile,
                        startedOnFork, turnStartLandId, ref stepped, ref foughtOnTile, foughtTurn, ref thiefStoleOnTile, mapStat, summary);
                }
            }

            if (unknownLayout)
            {
                summary.MissingMap = 1;
            }
            else if (isTruncated || decodeFailed)
            {
                summary.TruncatedOrCorrupt = 1;
            }
            else
            {
                summary.ReplaysVerified = 1;
                mapStat.Replays = 1;
            }

            return summary;
        }
    }
}
