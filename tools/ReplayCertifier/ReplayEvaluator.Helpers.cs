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
            return cmd == 1003 || cmd == 1016 || cmd == 1113 || cmd == 1018 || cmd == 5022 ||
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
            }
            if (h.Buffs != null)
            {
                foreach (dynamic kv in (IEnumerable)h.Buffs)
                    if (kv?.Value != null)
                    {
                        int bid = (int)kv.Value.BuffId;
                        u.BuffIds.Add(bid);
                        if (bid == 10011 && kv.Value.Source != null) u.TauntSkillId = (int)kv.Value.Source.Id;
                        if (bid == 10013) u.CrimeBuffProgress = Math.Max(u.CrimeBuffProgress, (int)kv.Value.Progress);
                        if (bid == 10671303 || (bid == 10671301 && (int)kv.Value.Progress > 0)) u.WardenRevealed = true;
                    }
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
                if (!players.TryGetValue(pid, out var target) && !monsters.TryGetValue(pid, out target)) continue;

                if (eff.Hp != null)
                {
                    int currHp = (int)eff.Hp.CurrHp;
                    int maxHp = (int)eff.Hp.MaxHp;
                    int oriHp = (int)eff.Hp.OriHp;
                    int changeHp = (int)eff.Hp.ChangeHp;
                    if (maxHp > 0 || oriHp > 0 || changeHp != 0 || currHp > 0)
                        target.Hp = Math.Max(0, currHp);
                }
                if (eff.Gold != null)
                {
                    int chg = (int)eff.Gold.ChangeGold;
                    target.Gold = (int)eff.Gold.CurrGold;
                    if (actorIsThief && !target.IsMonster && chg < 0) thiefStole = true;
                }
                if (eff.CrimeNum != null) target.CrimeNum = Math.Max(0, (int)eff.CrimeNum.CurrNum);
                if (eff.NotSelect != null) target.NotSelect = (bool)eff.NotSelect.NotSelect;
                if (eff.Disappear != null && target.IsMonster) target.Hp = 0;

                if (eff.Buff != null)
                {
                    int op = Convert.ToInt32(eff.Buff.Op);
                    if (eff.Buff.Buff != null)
                    {
                        int bid = (int)eff.Buff.Buff.BuffId;
                        if (op == 2)
                        {
                            target.BuffIds.Remove(bid);
                            if (bid == 10011) target.TauntSkillId = 0;
                            if (bid == 10013) target.CrimeBuffProgress = 0;
                        }
                        else if (op == 1 || op == 3)
                        {
                            target.BuffIds.Add(bid);
                            if (bid == 10011 && eff.Buff.Buff.Source != null) target.TauntSkillId = (int)eff.Buff.Buff.Source.Id;
                            if (bid == 10013) target.CrimeBuffProgress = Math.Max(0, (int)eff.Buff.Buff.Progress);
                            if (bid == 10671303 || (bid == 10671301 && (int)eff.Buff.Buff.Progress > 0)) target.WardenRevealed = true;
                        }
                    }
                    else if (eff.Buff.Buffs != null)
                    {
                        foreach (dynamic kv in (IEnumerable)eff.Buff.Buffs)
                        {
                            if (kv?.Value == null) continue;
                            int bid = (int)kv.Value.BuffId;
                            if (op == 2)
                            {
                                target.BuffIds.Remove(bid);
                                if (bid == 10011) target.TauntSkillId = 0;
                                if (bid == 10013) target.CrimeBuffProgress = 0;
                            }
                            else if (op == 1 || op == 3)
                            {
                                target.BuffIds.Add(bid);
                                if (bid == 10011 && kv.Value.Source != null) target.TauntSkillId = (int)kv.Value.Source.Id;
                                if (bid == 10013) target.CrimeBuffProgress = Math.Max(0, (int)kv.Value.Progress);
                                if (bid == 10671303 || (bid == 10671301 && (int)kv.Value.Progress > 0)) target.WardenRevealed = true;
                            }
                        }
                    }
                }

                if (eff.Place != null && eff.Place.Place != null)
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
                        if (summary != null) summary.IllegalMoveEdges++;
                        continue;
                    }

                    bool tileChanged = newNode >= 0 && newNode != oldNode;
                    if (newNode >= 0) target.NodeId = newNode;
                    target.BackNodeId = newBack;
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
