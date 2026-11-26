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
        private static bool IsStateFrame(int cmd)
        {
            return cmd == 1003 || cmd == 1016 || cmd == 1113 || cmd == 1018 || cmd == 1042 || cmd == 5022 ||
                   cmd == 1040 || cmd == 1086 || cmd == 1096 || cmd == 5214 || cmd == 1007 || cmd == 1115 || cmd == 5028;
        }

        private static (List<(int CmdId, byte[] Payload)> Frames, bool IsTruncated) ReadFrames(byte[] bytes)
        {
            var list = new List<(int, byte[])>();
            if (bytes == null || bytes.Length < 6) return (list, true);

            int i = 0;
            bool truncated = false;
            while (i + 6 <= bytes.Length)
            {
                int cmd = BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(i, 2));
                int len = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(i + 2, 4));
                if (len < 0 || len > bytes.Length - i - 6)
                {
                    truncated = true;
                    break;
                }
                list.Add((cmd, len > 0 ? bytes.AsSpan(i + 6, len).ToArray() : Array.Empty<byte>()));
                i += 6 + len;
            }
            if (i != bytes.Length) truncated = true;
            return (list, truncated);
        }

        /// <summary>
        /// Full structural, lifecycle, and state-frame decode integrity check (no arbitrary byte-size cutoff).
        /// Verifies non-truncated framing, valid 1003 -> 1016 lifecycle, non-empty state payloads, decodability of
        /// EVERY intermediate state frame (not just 1003/1016), and matching non-empty 1016.ReplayId.
        /// </summary>
        public static bool HasValidFrameStream(byte[] bytes, IProtocolDecoder decoder = null, string expectedReplayId = null)
        {
            return HasValidFrameStream(bytes, decoder, expectedReplayId, out _);
        }

        public static bool HasValidFrameStream(
            byte[] bytes,
            IProtocolDecoder decoder,
            string expectedReplayId,
            out string verifiedReplayId)
        {
            verifiedReplayId = null;
            var (frames, isTruncated) = ReadFrames(bytes);
            if (isTruncated || frames.Count == 0) return false;

            var state = StreamLifecycleState.Unstarted;
            int expectedMapId = 0;
            for (int i = 0; i < frames.Count; i++)
            {
                var (cmd, payload) = frames[i];
                bool isState = IsStateFrame(cmd);
                if (payload.Length == 0)
                {
                    if (isState) return false;
                    continue;
                }

                if (cmd == 1003)
                {
                    if (state != StreamLifecycleState.Unstarted) return false;
                    if (decoder != null)
                    {
                        dynamic running = decoder.TryDecode("party.protocol.RunningGameS2C", payload);
                        if (running?.Room == null) return false;
                        expectedMapId = (int)running.Room.MapId;
                    }
                    state = StreamLifecycleState.Running;
                    continue;
                }

                if (state == StreamLifecycleState.Unstarted || state == StreamLifecycleState.Finished)
                {
                    if (cmd == 1040)
                    {
                        if (decoder != null && decoder.TryDecode("party.protocol.UpdateHeroAttrS2C", payload) == null)
                            return false;
                        continue;
                    }
                    if (isState) return false;
                    continue;
                }

                if (cmd == 1016)
                {
                    if (decoder != null)
                    {
                        dynamic fin = decoder.TryDecode("party.protocol.GameFinishS2C", payload);
                        if (fin == null) return false;
                        string finReplayId = null;
                        try { finReplayId = (string)fin.ReplayId; } catch { }
                        finReplayId = finReplayId?.Trim();
                        if (!string.IsNullOrEmpty(finReplayId))
                        {
                            verifiedReplayId = finReplayId;
                            if (!string.IsNullOrWhiteSpace(expectedReplayId) &&
                                !string.Equals(finReplayId, expectedReplayId, StringComparison.Ordinal))
                                return false;
                        }
                    }
                    state = StreamLifecycleState.Finished;
                    continue;
                }

                // Decode EVERY intermediate state frame during Running so corrupt mid-stream payloads
                // can never pass CDN preflight, block healthy backup CDNs, or hide under SkippedNonPve/MissingMap.
                if (decoder != null && isState)
                {
                    if (!CanDecodeRunningStateFrame(cmd, payload, decoder, expectedMapId)) return false;
                }
            }
            return state == StreamLifecycleState.Finished;
        }

        private static bool CanDecodeRunningStateFrame(int cmd, byte[] payload, IProtocolDecoder decoder, int expectedMapId)
        {
            switch (cmd)
            {
                case 1113:
                {
                    dynamic snap = decoder.TryDecode("party.protocol.ReplaySnapshotS2C", payload);
                    if (snap?.Room == null) return false;
                    int snapMapId = (int)snap.Room.MapId;
                    if (expectedMapId != 0 && snapMapId != 0 && snapMapId != expectedMapId) return false;
                    return true;
                }
                case 1018: return decoder.TryDecode("party.protocol.MonsterRefreshS2C", payload) != null;
                case 1042: return decoder.TryDecode("party.protocol.ChangePlayerSlotS2C", payload) != null;
                case 5022: return decoder.TryDecode("party.protocol.ThrowDiceS2C", payload) != null;
                case 1040: return decoder.TryDecode("party.protocol.UpdateHeroAttrS2C", payload) != null;
                case 1086: return decoder.TryDecode("party.protocol.MapIndexChangeS2C", payload) != null;
                case 1096: return decoder.TryDecode("party.protocol.HeroSkillMoveEffectS2C", payload) != null;
                case 5214: return decoder.TryDecode("party.protocol.MonsterPursuitS2C", payload) != null;
                case 1007: return decoder.TryDecode("party.protocol.BattleS2C", payload) != null;
                case 1115: return decoder.TryDecode("party.protocol.ReplayDieS2C", payload) != null;
                case 5028: return decoder.TryDecode("party.protocol.MoveS2C", payload) != null;
                default: return true;
            }
        }

        private static int[] ToIntArray(IEnumerable items)
        {
            if (items == null) return Array.Empty<int>();
            var list = new List<int>();
            foreach (var x in items) list.Add(Convert.ToInt32(x));
            return list.ToArray();
        }

        private static UnitSim BuildUnit(dynamic unitObj, bool isMonster)
        {
            dynamic h = unitObj.Hero;
            int slot = (int)unitObj.Slot;
            if (!isMonster)
            {
                try { int cs = (int)unitObj.ChangeSlot; if (cs >= 0 && cs < 4) slot = cs; } catch { }
            }
            var u = new UnitSim
            {
                Id = (long)unitObj.Id,
                Slot = slot,
                HeroId = (int)h.HeroId,
                IsMonster = isMonster,
                MonsterType = isMonster ? Convert.ToInt32(h.MonsterType) : 0,
                NodeId = (int)h.NodeId,
                BackNodeId = (int)h.BackNodeId,
                FrontNodeIds = ToIntArray((IEnumerable)h.FrontNodeIds),
                Hp = (int)h.Hp,
                Gold = (int)h.Gold,
                CrimeNum = (int)h.CrimeNum,
                NotSelect = (bool)h.NotSelect
            };
            if (isMonster)
            {
                try { u.BossTargetId = (long)h.BossTargetId; } catch { }
                try { u.IsLaunchFight = (bool)h.IsLaunchFight; } catch { }
            }
            if (h.Buffs != null)
            {
                var initBuffs = new List<LiveBuffEntry>();
                foreach (dynamic kv in (IEnumerable)h.Buffs)
                {
                    if (kv?.Value == null) continue;
                    dynamic bv = kv.Value;
                    long uid = 0;
                    try { uid = (long)bv.UniqueId; } catch { }
                    if (uid == 0) { try { uid = Convert.ToInt64(kv.Key); } catch { } }
                    int bid = (int)bv.BuffId;
                    int prog = 0;
                    try { prog = (int)bv.Progress; } catch { }
                    int srcSkill = 0;
                    try { if (bv.Source != null) srcSkill = (int)bv.Source.Id; } catch { }
                    initBuffs.Add(new LiveBuffEntry(uid, bid, prog, srcSkill));
                }
                u.BuffMap.ApplyFullReplace(initBuffs);
                u.SyncBuffSummary();
            }
            return u;
        }

        private static bool HasEmptyCausePlaceEffect(dynamic attr)
        {
            if (attr?.EffectDatas == null) return false;
            int causeId = 0;
            try { if (attr.Cause != null) causeId = (int)attr.Cause.Id; } catch { }
            if (causeId != 0) return false;
            foreach (dynamic eff in (IEnumerable)attr.EffectDatas)
            {
                if (eff?.Place?.Place != null && (int)eff.Place.Place.NodeId >= 0) return true;
            }
            return false;
        }

        private static void TryLockTurnStartBossTarget(UnitSim m, BoardGraph board, Dictionary<long, UnitSim> players, int turnStartLandId)
        {
            if (m == null || board == null || players == null) return;
            if (m.HeroId != 1018 && m.HeroId != 1047) return;
            int startLand = turnStartLandId >= 0 ? turnStartLandId : m.NodeId;
            if (!board.Contains(startLand)) return;

            int requiredBuff = m.HeroId == 1018 ? 2101001 : 10470101;
            long bestId = 0;
            int bestDist = int.MaxValue;
            int bestSlot = int.MaxValue;
            foreach (var p in players.Values)
            {
                if (p == null || p.Hp <= 0 || p.NotSelect) continue;
                if (!board.Contains(p.NodeId) || p.NodeId == startLand) continue;
                if (!p.BuffIds.Contains(requiredBuff)) continue;
                int dist = board.ComputeShortestPathsTo(p.NodeId).GetDistance(startLand);
                if (dist == int.MaxValue || dist <= 0) continue;
                if (dist < bestDist ||
                    (dist == bestDist && (p.Slot < bestSlot || (p.Slot == bestSlot && p.Id < bestId))))
                {
                    bestDist = dist;
                    bestSlot = p.Slot;
                    bestId = p.Id;
                }
            }
            if (bestId != 0)
            {
                m.BossTargetId = bestId;
            }
        }

        private static SkillMoveKind IdentifyActiveDisplacementSkill(int skillId)
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

        private static void InferLinglingPushDirection(
            BoardGraph board,
            int oldLandId,
            int newLandId,
            int casterLandId,
            out int pushNextId,
            out int pushFromId)
        {
            pushNextId = -1;
            pushFromId = -1;
            if (board == null) return;
            int origin = board.Contains(oldLandId) ? oldLandId : casterLandId;
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

        private static bool ApplyAttr(dynamic attr, BoardGraph board, Dictionary<long, UnitSim> players, Dictionary<long, UnitSim> monsters, bool fromSkillMove, long curTurnActor, CertificationSummary summary = null)
        {
            return ApplyAttr(attr, board, players, monsters, fromSkillMove, curTurnActor, summary, out bool _);
        }

        private static bool ApplyAttr(dynamic attr, BoardGraph board, Dictionary<long, UnitSim> players, Dictionary<long, UnitSim> monsters, bool fromSkillMove, long curTurnActor, CertificationSummary summary, out bool curActorLeftTile)
        {
            bool thiefStole = false;
            curActorLeftTile = false;
            if (attr?.EffectDatas == null) return false;
            long attrOwner = (long)attr.PlayerId;
            int causeSkillId = 0;
            try { if (attr.Cause != null) causeSkillId = (int)attr.Cause.Id; } catch { }
            SkillMoveKind displacementKind = IdentifyActiveDisplacementSkill(causeSkillId);

            bool actorIsThief = curTurnActor != 0 && attrOwner == curTurnActor &&
                monsters.TryGetValue(curTurnActor, out var curM) && curM.MonsterType == 1;

            foreach (dynamic eff in (IEnumerable)attr.EffectDatas)
            {
                if (eff == null) continue;
                long pid = (long)eff.PlayerId;
                UnitSim target = null;
                if (!players.TryGetValue(pid, out target)) monsters.TryGetValue(pid, out target);

                if (target != null && eff.Hp != null)
                {
                    int currHp = (int)eff.Hp.CurrHp;
                    int maxHp = (int)eff.Hp.MaxHp;
                    int oriHp = (int)eff.Hp.OriHp;
                    int changeHp = (int)eff.Hp.ChangeHp;
                    if (maxHp > 0 || oriHp > 0 || changeHp != 0 || currHp > 0)
                        target.Hp = Math.Max(0, currHp);
                }
                if (target != null && eff.Gold != null)
                {
                    int chg = (int)eff.Gold.ChangeGold;
                    target.Gold = (int)eff.Gold.CurrGold;
                    if (actorIsThief && !target.IsMonster && chg < 0) thiefStole = true;
                }
                if (target != null && eff.CrimeNum != null) target.CrimeNum = Math.Max(0, (int)eff.CrimeNum.CurrNum);
                if (target != null && eff.NotSelect != null) target.NotSelect = (bool)eff.NotSelect.NotSelect;
                if (eff.Disappear != null)
                {
                    long disPid = pid;
                    try { long nested = (long)eff.Disappear.PlayerId; if (nested > 0) disPid = nested; } catch { }
                    if (monsters.TryGetValue(disPid, out var disMonster)) disMonster.Hp = 0;
                    else if (players.TryGetValue(disPid, out var disPlayer)) disPlayer.Hp = 0;
                }

                if (eff.Buff != null)
                {
                    long buffPid = pid;
                    try { long nested = (long)eff.Buff.PlayerId; if (nested > 0) buffPid = nested; } catch { }
                    if (players.TryGetValue(buffPid, out var buffTarget) || monsters.TryGetValue(buffPid, out buffTarget))
                    {
                        int op = Convert.ToInt32(eff.Buff.Op);
                        var entries = new List<LiveBuffEntry>();
                        if (eff.Buff.Buff != null)
                        {
                            dynamic bv = eff.Buff.Buff;
                            long uid = 0;
                            try { uid = (long)bv.UniqueId; } catch { }
                            int prog = 0;
                            try { prog = (int)bv.Progress; } catch { }
                            int srcSkill = 0;
                            try { if (bv.Source != null) srcSkill = (int)bv.Source.Id; } catch { }
                            entries.Add(new LiveBuffEntry(uid, (int)bv.BuffId, prog, srcSkill));
                        }
                        else if (eff.Buff.Buffs != null)
                        {
                            foreach (dynamic kv in (IEnumerable)eff.Buff.Buffs)
                            {
                                if (kv?.Value == null) continue;
                                dynamic bv = kv.Value;
                                long uid = 0;
                                try { uid = (long)bv.UniqueId; } catch { }
                                if (uid == 0) { try { uid = Convert.ToInt64(kv.Key); } catch { } }
                                int prog = 0;
                                try { prog = (int)bv.Progress; } catch { }
                                int srcSkill = 0;
                                try { if (bv.Source != null) srcSkill = (int)bv.Source.Id; } catch { }
                                entries.Add(new LiveBuffEntry(uid, (int)bv.BuffId, prog, srcSkill));
                            }
                        }
                        // Handles op 0 (full replace), op 1 (insert), op 2 (delete by UniqueId), op 3 (change).
                        buffTarget.BuffMap.ApplyOperation(op, entries);
                        buffTarget.SyncBuffSummary();
                    }
                }

                if (target != null && eff.Place != null && eff.Place.Place != null)
                {
                    dynamic pl = eff.Place.Place;
                    int oldNode = target.NodeId;
                    int newNode = (int)pl.NodeId;
                    int newBack = (int)pl.BackNodeId;
                    int[] newFront = ToIntArray((IEnumerable)pl.FrontNodeIds);

                    // Teleports/displacements may cross non-adjacent tiles, but any non-negative destination
                    // on a loaded board layout MUST exist on that layout; otherwise fail GATE 0 integrity.
                    if (newNode >= 0 && board != null && !board.Contains(newNode))
                    {
                        summary?.RecordIllegalMoveEdge($"{summary.RequestedReplayId} ApplyAttr.Place(fromSkillMove={fromSkillMove},cause={causeSkillId}) unit={target.Id}(m={target.IsMonster},h={target.HeroId}) {oldNode}->{newNode}");
                        continue;
                    }

                    bool tileChanged = newNode >= 0 && newNode != oldNode;
                    int oldBack = target.BackNodeId;
                    if (newNode >= 0) target.NodeId = newNode;
                    target.BackNodeId = newBack;
                    if (!target.IsMonster)
                    {
                        if (tileChanged && newBack < 0 && oldBack >= 0 && board != null && board.Contains(oldBack))
                        {
                            target.PreTeleportBackNodeId = oldBack;
                            target.PreTeleportLandingNodeId = newNode;
                        }
                        else if (tileChanged || newBack >= 0)
                        {
                            target.PreTeleportBackNodeId = -1;
                            target.PreTeleportLandingNodeId = -1;
                        }
                    }
                    if (tileChanged)
                    {
                        // Always replace FrontNodeIds on tile change (including empty []) so stale old-tile
                        // facing can never leak onto the new tile.
                        target.FrontNodeIds = newFront;
                    }
                    else if (newFront.Length > 0)
                    {
                        target.FrontNodeIds = newFront;
                    }

                    // Record displacement (From -> To) pair for BOTH 1040.Place and 1096.Place so subsequent
                    // MoveS2C echoes (standalone or prefix-merged) are stripped accurately without false illegal edges.
                    // Leaving a tile ALWAYS clears any prior PendingSkillMove* tag immediately so returning to the old
                    // tile via an ordinary Place can NEVER reactivate an expired skill tag.
                    if (tileChanged)
                    {
                        target.PendingDisplacementHops.Add((oldNode, newNode));
                        target.PendingSkillMoveKind = SkillMoveKind.None;
                        target.PendingSkillMoveLandId = -1;
                        target.PendingSkillMovePushDirId = -1;
                        target.PendingSkillMovePushFromId = -1;
                        if (curTurnActor != 0 && target.Id == curTurnActor)
                        {
                            // Leaving the current tile invalidates tile-scoped combat/steal flags IMMEDIATELY
                            // even if a subsequent effect in the same 1040 moves the actor back (F->G->F).
                            curActorLeftTile = true;
                            thiefStole = false;
                        }
                    }

                    if (target.IsMonster && displacementKind != SkillMoveKind.None && newNode >= 0)
                    {
                        bool sameTileDuplicateLingling =
                            !tileChanged &&
                            displacementKind == SkillMoveKind.LinglingPush &&
                            target.PendingSkillMoveKind == SkillMoveKind.LinglingPush &&
                            target.PendingSkillMoveLandId == newNode &&
                            target.PendingSkillMovePushFromId >= 0;

                        target.PendingSkillMoveKind = displacementKind;
                        target.PendingSkillMoveLandId = newNode;
                        if (!sameTileDuplicateLingling)
                        {
                            target.PendingSkillMovePushDirId = -1;
                            target.PendingSkillMovePushFromId = -1;
                            if (displacementKind == SkillMoveKind.LinglingPush)
                            {
                                // Unified predecessor priority matching Runtime MonsterStateTracker:
                                // (1) existing confirmed Lingling direction on duplicate same-tile Place,
                                // (2) previous tile when tileChanged,
                                // (3) valid in-board protocol BackNodeId != newNode,
                                // (4) fallback to caster position.
                                int pushOrigin = tileChanged
                                    ? oldNode
                                    : (newBack >= 0 && newBack != newNode && board != null && board.Contains(newBack) ? newBack : -1);
                                int casterLand = players.TryGetValue(attrOwner, out var casterUnit) ? casterUnit.NodeId : -1;
                                InferLinglingPushDirection(board, pushOrigin, newNode, casterLand, out int pushDir, out int pushFrom);
                                target.PendingSkillMovePushDirId = pushDir;
                                target.PendingSkillMovePushFromId = pushFrom;
                            }
                        }
                    }
                }
            }
            return thiefStole;
        }
    }
}
