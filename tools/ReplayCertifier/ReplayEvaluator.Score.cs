using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using MonsterPathPredictor.Core;

namespace MonsterPathPredictor.ReplayCertifier
{
    internal static partial class ReplayEvaluator
    {
        private static bool ConsumePendingMoveEffects(
            long moverId,
            int tileId,
            BoardGraph board,
            Dictionary<long, Dictionary<int, List<dynamic>>> pendingMoveEffects,
            Dictionary<long, UnitSim> players,
            Dictionary<long, UnitSim> monsters,
            long curTurnActor,
            CertificationSummary summary,
            out bool curActorLeftTile)
        {
            curActorLeftTile = false;
            if (pendingMoveEffects == null || !pendingMoveEffects.TryGetValue(moverId, out var byTile)) return false;
            if (!byTile.TryGetValue(tileId, out var attrs)) return false;
            byTile.Remove(tileId);
            if (byTile.Count == 0) pendingMoveEffects.Remove(moverId);
            foreach (var attr in attrs)
            {
                // 1096 HeroSkillMoveEffectS2C pass-by effects (e.g., Forest thief aura 10701102)
                // do NOT trigger the fork-interrupting steal pause (only 1040 UpdateHeroAttrS2C does).
                ApplyAttr(attr, board, players, monsters, true, curTurnActor, summary, out bool left);
                if (left) curActorLeftTile = true;
            }
            return true;
        }

        private static void ConsumeAndSkipInlineDisplacements(
            UnitSim u,
            long moverId,
            int[] nodeIds,
            ref int idx,
            BoardGraph board,
            Dictionary<long, Dictionary<int, List<dynamic>>> pendingMoveEffects,
            Dictionary<long, UnitSim> players,
            Dictionary<long, UnitSim> monsters,
            long curTurnActor,
            ref bool foughtOnTile,
            ref bool thiefStoleOnTile,
            CertificationSummary summary)
        {
            // Decouple landing-tile 1096 effect execution from optional MoveS2C echo stripping:
            // whenever an effect moves u.NodeId (chained self-teleports 1->2->...->34+), ALWAYS continue
            // consuming pending 1096 effects on the new landing tile without any arbitrary hop cap.
            // Termination is guaranteed because ConsumePendingMoveEffects removes each tile bucket before applying it.
            while (true)
            {
                int beforeNode = u.NodeId;
                int hopsBefore = u.PendingDisplacementHops.Count;
                bool consumed = ConsumePendingMoveEffects(moverId, u.NodeId, board, pendingMoveEffects, players, monsters, curTurnActor, summary, out bool curActorLeft);
                if (!consumed) break;

                if ((curActorLeft || (u.NodeId != beforeNode && moverId == curTurnActor)))
                {
                    foughtOnTile = false;
                    thiefStoleOnTile = false;
                }

                if (u.PendingDisplacementHops.Count > hopsBefore)
                {
                    int newCount = u.PendingDisplacementHops.Count - hopsBefore;
                    int matchedInline = 0;
                    for (int h = hopsBefore; h < u.PendingDisplacementHops.Count; h++)
                    {
                        var (fromN, toN) = u.PendingDisplacementHops[h];
                        if (idx + 1 < nodeIds.Length && nodeIds[idx] == fromN && nodeIds[idx + 1] == toN)
                        {
                            idx += 2;
                            matchedInline++;
                        }
                        else if (idx < nodeIds.Length && nodeIds[idx] == toN)
                        {
                            idx += 1;
                            matchedInline++;
                        }
                        else
                        {
                            break;
                        }
                    }

                    // Remove inline hops echoed in this frame (or all newly added inline hops if normal steps
                    // still remain in this frame); preserve unmatched trailing hops if this frame ended mid-chain.
                    if (idx < nodeIds.Length)
                    {
                        u.PendingDisplacementHops.RemoveRange(hopsBefore, newCount);
                    }
                    else if (matchedInline > 0)
                    {
                        u.PendingDisplacementHops.RemoveRange(hopsBefore, matchedInline);
                    }
                }

                if (u.NodeId == beforeNode) break;
            }
        }

        private static bool HandleMove(
            dynamic mv, string replayId, int mapId, int curRound, int fi, BoardGraph board,
            Dictionary<long, UnitSim> players, Dictionary<long, UnitSim> monsters, Dictionary<long, int> spawnedAtTile,
            Dictionary<long, Dictionary<int, List<dynamic>>> pendingMoveEffects, long curTurnActor,
            bool startedOnFork, int turnStartLandId, ref bool stepped, ref bool foughtOnTile, bool foughtTurn, ref bool thiefStoleOnTile,
            MapStats mapStat, CertificationSummary summary)
        {
            long moverId = (long)mv.PlayerId;
            int[] nodeIds = ToIntArray((IEnumerable)mv.NodeIds);

            bool isKnownPlayer = players.TryGetValue(moverId, out var mp);
            UnitSim m = null;
            bool isKnownMonster = !isKnownPlayer && monsters.TryGetValue(moverId, out m);

            // Always verify mover identity BEFORE short-circuiting on empty NodeIds so an unknown mover
            // with NodeIds=[] cannot bypass integrity validation.
            if (!isKnownPlayer && !isKnownMonster)
            {
                if (nodeIds.Length == 1)
                {
                    if (!board.Contains(nodeIds[0])) summary.IllegalMoveEdges++;
                }
                else if (nodeIds.Length >= 2)
                {
                    int prev = nodeIds[0];
                    for (int i = 1; i < nodeIds.Length; i++)
                    {
                        int nid = nodeIds[i];
                        if (nid == prev) continue;
                        if (!board.Contains(prev) || !board.Contains(nid) || !board.IsValidNeighbor(prev, nid))
                        {
                            summary.IllegalMoveEdges++;
                        }
                        prev = nid;
                    }
                }
                return false;
            }

            if (nodeIds.Length == 0) return true;

            UnitSim mover = isKnownPlayer ? mp : m;
            bool isNormalAggro = isKnownMonster && (m.MonsterType == 2 || m.MonsterType == 10 || m.MonsterType == 11 || m.MonsterType == 12);
            bool isThief = isKnownMonster && m.MonsterType == 1;
            bool isActiveTurnMonster = isKnownMonster && (moverId == curTurnActor);

            // Strip any MoveS2C prefix echoing one or more ordered (From -> To) skill displacements applied by 1040/1096.Place.
            // StripSkillDisplacementPrefix removes ONLY confirmed matched hops so split echoes (e.g. [1,2] then [2,3])
            // keep remaining unconsumed hops for the follow-up frame.
            int idx = StripSkillDisplacementPrefix(mover, nodeIds);

            if (idx >= nodeIds.Length)
            {
                ConsumeAndSkipInlineDisplacements(mover, moverId, nodeIds, ref idx, board, pendingMoveEffects, players, monsters, curTurnActor, ref foughtOnTile, ref thiefStoleOnTile, summary);
                return true;
            }

            // Normal walking resumes in this frame: any older omitted displacement echoes are now superseded.
            mover.PendingDisplacementHops.Clear();

            // Skip optional start-tile repetition if MoveS2C begins at mover.NodeId, then UNCONDITIONALLY
            // consume any deferred 1096 effects queued for mover.NodeId before walking so 1113 landing
            // confirmations execute identically whether MoveS2C repeats the start tile ([2,4]) or omits it ([4]).
            if (nodeIds[idx] == mover.NodeId) idx++;
            ConsumeAndSkipInlineDisplacements(mover, moverId, nodeIds, ref idx, board, pendingMoveEffects, players, monsters, curTurnActor, ref foughtOnTile, ref thiefStoleOnTile, summary);

            while (idx < nodeIds.Length)
            {
                int nextTile = nodeIds[idx];
                int curTile = mover.NodeId;
                if (nextTile == curTile)
                {
                    idx++;
                    ConsumeAndSkipInlineDisplacements(mover, moverId, nodeIds, ref idx, board, pendingMoveEffects, players, monsters, curTurnActor, ref foughtOnTile, ref thiefStoleOnTile, summary);
                    continue;
                }

                int fromTile = mover.BackNodeId;
                bool validEdge = board.Contains(curTile) && board.Contains(nextTile) && board.IsValidNeighbor(curTile, nextTile);

                if (!validEdge)
                {
                    summary.IllegalMoveEdges++;
                }
                else if (isKnownMonster && (isNormalAggro || isThief) && isActiveTurnMonster)
                {
                    // Directed 2-exit arrival forks remain forks on mid-walk, post-combat/steal, AND
                    // Lingling directed pushes with a verified push predecessor (matching MonsterPathEngine.IsForkForFirstStep).
                    bool hasLinglingPushFrom = m.PendingSkillMoveLandId == curTile &&
                                               m.PendingSkillMoveKind == SkillMoveKind.LinglingPush &&
                                               m.PendingSkillMovePushFromId >= 0;
                    int forkContextFrom = hasLinglingPushFrom ? m.PendingSkillMovePushFromId : fromTile;
                    bool arrivedFromEdge = stepped || foughtOnTile || hasLinglingPushFrom;
                    if (board.IsForkForContext(curTile, forkContextFrom, arrivedFromEdge))
                    {
                        ScoreForkDecision(m, isNormalAggro, isThief, curTile, fromTile, nextTile,
                            replayId, mapId, curRound, fi, board, players, spawnedAtTile,
                            startedOnFork, turnStartLandId, stepped, foughtOnTile, foughtTurn, thiefStoleOnTile, mapStat, summary);
                    }
                }

                mover.BackNodeId = curTile;
                mover.NodeId = nextTile;
                if (isKnownMonster)
                {
                    m.FrontNodeIds = board.GetForwardNeighbors(nextTile, curTile).ToArray();
                    if (m.PendingSkillMoveLandId != nextTile)
                    {
                        m.PendingSkillMoveKind = SkillMoveKind.None;
                        m.PendingSkillMoveLandId = -1;
                        m.PendingSkillMovePushDirId = -1;
                        m.PendingSkillMovePushFromId = -1;
                    }
                    if (isActiveTurnMonster)
                    {
                        stepped = true;
                        foughtOnTile = false;
                        thiefStoleOnTile = false;
                    }
                    if (spawnedAtTile.TryGetValue(m.Id, out int spt) && spt != nextTile) spawnedAtTile.Remove(m.Id);
                }

                idx++;
                ConsumeAndSkipInlineDisplacements(mover, moverId, nodeIds, ref idx, board, pendingMoveEffects, players, monsters, curTurnActor, ref foughtOnTile, ref thiefStoleOnTile, summary);
            }

            return true;
        }

        private static int StripSkillDisplacementPrefix(UnitSim u, int[] nodeIds)
        {
            var hops = u.PendingDisplacementHops;
            if (hops.Count == 0 || u.NodeId != hops[^1].To || nodeIds.Length == 0)
                return 0;

            // Match either from hop 0 (full echo [1,2,3], split prefix [1,2], or single-node [2])
            // OR from a later hop startHop > 0 when an earlier hop echo was omitted (e.g. [2,3] when [(1,2),(2,3)] pending).
            for (int startHop = 0; startHop < hops.Count; startHop++)
            {
                int pos = 0;
                int lastMatchedHop = -1;
                for (int h = startHop; h < hops.Count; h++)
                {
                    var (fromN, toN) = hops[h];
                    if (pos == 0)
                    {
                        if (nodeIds.Length >= 2 && nodeIds[0] == fromN && nodeIds[1] == toN)
                        {
                            pos = 2;
                            lastMatchedHop = h;
                        }
                        else if (nodeIds.Length == 1 && nodeIds[0] == toN)
                        {
                            pos = 1;
                            lastMatchedHop = h;
                        }
                        else
                        {
                            break;
                        }
                    }
                    else if (pos >= nodeIds.Length)
                    {
                        // Frame ended cleanly mid-chain (split 5028 echo): keep remaining hops for the next frame.
                        break;
                    }
                    else if (nodeIds[pos - 1] == fromN && nodeIds[pos] == toN)
                    {
                        pos += 1;
                        lastMatchedHop = h;
                    }
                    else if (pos + 1 < nodeIds.Length && nodeIds[pos] == fromN && nodeIds[pos + 1] == toN)
                    {
                        pos += 2;
                        lastMatchedHop = h;
                    }
                    else
                    {
                        break;
                    }
                }

                if (lastMatchedHop >= 0 && (pos == nodeIds.Length || lastMatchedHop == hops.Count - 1))
                {
                    hops.RemoveRange(0, lastMatchedHop + 1);
                    return pos;
                }
            }

            return 0;
        }

        private static void ScoreForkDecision(
            UnitSim m, bool isNormalAggro, bool isThief, int curTile, int fromTile, int nextTile,
            string replayId, int mapId, int curRound, int fi, BoardGraph board,
            Dictionary<long, UnitSim> players, Dictionary<long, int> spawnedAtTile,
            bool startedOnFork, int turnStartLandId, bool stepped, bool foughtOnTile, bool foughtTurn, bool thiefStoleOnTile,
            MapStats mapStat, CertificationSummary summary)
        {
            bool isTurnStart = !stepped && startedOnFork;
            bool isFreshSpawn = spawnedAtTile.TryGetValue(m.Id, out int spTile) && spTile == curTile && !stepped;
            bool isPostCombat = foughtOnTile;

            if (isNormalAggro && (isTurnStart || isPostCombat))
            {
                var vf = (m.FrontNodeIds ?? Array.Empty<int>()).Where(id => board.IsValidNeighbor(curTile, id)).Distinct().ToArray();
                board.TryGetPriorityForwardNeighbor(curTile, board.IsValidNeighbor(curTile, fromTile) ? fromTile : -1, out int rbNext, out _);
                int vfRbNext = -1;
                if (vf.Length >= 2)
                {
                    board.TryGetPriorityAmongCandidates(curTile, vf, out vfRbNext, out _);
                }
                var fa = summary.FrontAudit;
                if (isPostCombat && vf.Length >= 2) { fa.PostCombatMulti++; if (vfRbNext == nextTile) fa.PostCombatMultiRedBlue++; }
                else if (isFreshSpawn && vf.Length == 1) { fa.SpawnF1++; if (vf[0] == nextTile) fa.SpawnF1Actual++; if (nextTile == rbNext) fa.SpawnF1RedBlue++; }
                else if (!isFreshSpawn && !isPostCombat)
                {
                    if (vf.Length == 1) { fa.StopF1++; if (vf[0] == nextTile) fa.StopF1Actual++; if (nextTile == rbNext) fa.StopF1RedBlue++; }
                    else if (vf.Length >= 2) { fa.StopMulti++; if (vfRbNext == nextTile) fa.StopMultiRedBlue++; }
                }
            }

            bool isTaunted = m.BuffIds.Contains(10011);
            long tauntTargetId = 0;
            if (isTaunted && m.TauntSkillId >= 10000)
            {
                int casterHeroId = m.TauntSkillId / 100;
                var caster = players.Values.FirstOrDefault(p => p.Hp > 0 && p.HeroId == casterHeroId);
                if (caster != null) tauntTargetId = caster.Id;
            }

            bool anyPlayerHasTrueCulprit = players.Values.Any(p => p.BuffIds.Contains(10671302));
            bool requiresCrimeTarget =
                (m.HeroId == 1067 && !m.WardenRevealed && !anyPlayerHasTrueCulprit) ||
                (m.HeroId == 1068 && !m.BuffIds.Contains(10681202));

            // Thief stealing on a fork interrupts movement and leaves that fork via Red > Blue
            bool thiefStoleHere = isThief && stepped && !isPostCombat && thiefStoleOnTile;

            var mState = new MonsterState
            {
                MonsterId = m.Id,
                HeroId = m.HeroId,
                IsNormalAggressive = isNormalAggro,
                IsThief = isThief,
                CurrentLandId = curTile,
                FromLandId = fromTile,
                FrontNodeIds = m.FrontNodeIds,
                IsCurrentlyMovingMidTurn = stepped && !thiefStoleHere,
                TurnStartLandId = turnStartLandId,
                JustFoughtOnCurrentFork = isPostCombat || thiefStoleHere,
                HasLaunchedFightThisTurn = foughtTurn,
                JustSpawnedOnFork = isFreshSpawn,
                IsTaunted = isTaunted,
                TauntTargetPlayerId = tauntTargetId,
                RequiresCrimeTarget = requiresCrimeTarget,
                ChasesProblemStudent = (m.HeroId == 1018),
                ChasesMandarinDuckMark = (m.HeroId == 1047),
                SkillMoveKind = m.PendingSkillMoveLandId == curTile ? m.PendingSkillMoveKind : SkillMoveKind.None,
                SkillMovePushDirectionLandId = m.PendingSkillMoveLandId == curTile ? m.PendingSkillMovePushDirId : -1,
                SkillMovePushFromLandId = m.PendingSkillMoveLandId == curTile ? m.PendingSkillMovePushFromId : -1
            };

            var trackedPlayers = players.Values.Select(p => new TrackedPlayer
            {
                PlayerId = p.Id,
                Slot = p.Slot,
                LandId = p.NodeId,
                IsAlive = p.Hp > 0,
                IsTargetable = !p.NotSelect,
                CanBeAttacked = !p.BuffIds.Contains(1270202),
                Gold = p.Gold,
                CrimeNum = Math.Max(p.CrimeNum, p.CrimeBuffProgress),
                HasProblemStudentMark = p.BuffIds.Contains(2101001),
                HasMandarinDuckMark = p.BuffIds.Contains(10470101)
            }).ToList();

            var plan = MonsterPathEngine.Plan(board, mState, trackedPlayers, new PlanOptions
            {
                MaxSteps = 1,
                SimulateCombatOnFork = false,
                PostCombatMode = PostCombatMode.ContinueByGuide
            });

            string bucketKey = isThief ? "ThiefFork"
                : isPostCombat ? "PostCombatFork"
                : isFreshSpawn ? "SpawnFork"
                : isTurnStart ? "ForkStart"
                : foughtTurn ? "PostNormalCombatMidFork"
                : "MidWalkFork";

            var bStat = summary.Buckets[bucketKey];
            bStat.Total++;

            var forkPred = plan.Forks.FirstOrDefault(f => f.ForkLandId == curTile);
            if (forkPred == null || forkPred.Exits.Count == 0)
            {
                summary.MissingPredictions++;
                bStat.DetTotal++;
                if (isThief) mapStat.ThiefDetTotal++; else mapStat.DetTotal++;
                if (bStat.Mismatches.Count < 15)
                {
                    bStat.Mismatches.Add(
                        $"{replayId} map={mapId} R{curRound} F{fi} m={m.Id}(h={m.HeroId},mt={m.MonsterType}) " +
                        $"tile={curTile} from={fromTile} front=[{string.Join(",", m.FrontNodeIds)}] pred=NONE actual={nextTile}");
                }
                return;
            }

            if (forkPred.Exits.Count == 1)
            {
                bStat.DetTotal++;
                if (isThief) mapStat.ThiefDetTotal++; else mapStat.DetTotal++;
                int predNext = forkPred.Exits[0].NextLandId;
                if (predNext == nextTile)
                {
                    bStat.DetHits++;
                    if (isThief) mapStat.ThiefDetHits++; else mapStat.DetHits++;
                }
                else if (bStat.Mismatches.Count < 15)
                {
                    string pDesc = string.Join(" ", trackedPlayers.OrderBy(p => p.Slot)
                        .Select(p => $"P{p.Slot + 1}@{p.LandId}(g={p.Gold}{(p.IsAlive ? "" : ",D")}{(p.IsTargetable ? "" : ",NS")})"));
                    bStat.Mismatches.Add(
                        $"{replayId} map={mapId} R{curRound} F{fi} m={m.Id}(h={m.HeroId},mt={m.MonsterType},buffs=[{string.Join(",", m.BuffIds)}]) " +
                        $"tile={curTile} from={fromTile} front=[{string.Join(",", m.FrontNodeIds)}] pred={predNext}({forkPred.SummaryLabel}) actual={nextTile} | {pDesc}");
                }
            }
            else
            {
                // Multi-branch decision for ALL evaluated monsters (both Aggressive AND Thief):
                // ALWAYS verify positive-probability support so out-of-support moves fail GATE 0.
                bStat.ProbTotal++;
                if (isThief) mapStat.ThiefProbTotal++; else mapStat.ProbTotal++;

                bool inSupport;
                if (isNormalAggro)
                {
                    inSupport = RecordMultiBranchCalibration(board, curTile, forkPred, nextTile, summary.ProbBins);
                }
                else
                {
                    inSupport = IsInPredictedSupport(forkPred.Exits, nextTile);
                }

                if (!inSupport)
                {
                    summary.OutOfSupportPredictions++;
                    bStat.OutOfSupport++;
                    if (bStat.Mismatches.Count < 15)
                    {
                        string predList = string.Join(",", forkPred.Exits.Select(e => $"{e.NextLandId}:{e.FormatConditionalPercent()}"));
                        bStat.Mismatches.Add(
                            $"{replayId} map={mapId} R{curRound} F{fi} m={m.Id}(h={m.HeroId},mt={m.MonsterType}) tile={curTile} from={fromTile} pred=[{predList}] actual={nextTile} (OUT_OF_SUPPORT)");
                    }
                }
            }
        }

        public static bool IsInPredictedSupport(IReadOnlyList<ForkExitPrediction> exits, int actualNext)
        {
            if (exits == null || exits.Count == 0) return false;
            for (int i = 0; i < exits.Count; i++)
            {
                if (exits[i].NextLandId == actualNext && exits[i].IsPositiveSupport)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Records uncensored probabilistic calibration observations for a multi-branch fork.
        /// 1. Every issued multi-branch prediction is recorded in calibration bins BEFORE returning whether
        ///    <paramref name="actualNext"/> was in the positive-probability support, so out-of-support outcomes
        ///    never censor calibration denominators (GATE 0 catches out-of-support AND GATE 3 sees true hit rates).
        /// 2. Each exit's bin key is a canonical function of its own probability alone (independent of sibling exits),
        ///    so identical probabilities (e.g. 0.25 in [0.25,0.75] and [0.25,0.2502,0.4998]) ALWAYS aggregate into
        ///    the exact same bin while distinct probabilities (>1e-9 apart) NEVER collide.
        /// 3. Exact 2-way uniform 50/50 forks use the dedicated "50%" bin (Red-most reference exit), while any 50% exit
        ///    inside a k&gt;=3 fork (e.g. [1/2, 1/4, 1/4]) is tagged "50%(k=3)" so "50%" contains pure 2-way 50/50 only.
        /// 4. For equal-probability clusters of size &gt;= 2 (outside pure 2-way 50/50), every member gets its own
        ///    rank-tagged bin ("&lt;key&gt;#1", "#2", ...).
        /// </summary>
        public static bool RecordMultiBranchCalibration(
            BoardGraph board,
            int forkLandId,
            PredictedFork fork,
            int actualNext,
            Dictionary<string, ProbBin> probBins)
        {
            if (fork == null || fork.Exits == null || fork.Exits.Count < 2) return false;
            return RecordMultiBranchCalibrationCore(board, forkLandId, fork.Exits, fork.IsExactUniformSplit, actualNext, probBins);
        }

        public static bool RecordMultiBranchCalibration(
            BoardGraph board,
            int forkLandId,
            IReadOnlyList<ForkExitPrediction> exits,
            int actualNext,
            Dictionary<string, ProbBin> probBins)
        {
            return RecordMultiBranchCalibrationCore(board, forkLandId, exits, isExactUniformOverride: null, actualNext, probBins);
        }

        private static bool RecordMultiBranchCalibrationCore(
            BoardGraph board,
            int forkLandId,
            IReadOnlyList<ForkExitPrediction> exits,
            bool? isExactUniformOverride,
            int actualNext,
            Dictionary<string, ProbBin> probBins)
        {
            if (exits == null || exits.Count < 2) return false;
            bool inSupport = IsInPredictedSupport(exits, actualNext);

            var rawNeighbors = board.GetValidNeighbors(forkLandId);
            var positiveExits = new List<ForkExitPrediction>(exits.Count);
            for (int i = 0; i < exits.Count; i++)
            {
                if (exits[i].IsPositiveSupport) positiveExits.Add(exits[i]);
            }
            if (positiveExits.Count < 2) return false;

            static double EffectiveLogProb(ForkExitPrediction e)
            {
                if (!double.IsNegativeInfinity(e.LogConditionalProbability)) return e.LogConditionalProbability;
                return e.ConditionalProbability > 0.0 ? Math.Log(e.ConditionalProbability) : double.NegativeInfinity;
            }

            // Compute each exit's OWN context-independent canonical key (equivalence-class grouping — reflexive,
            // symmetric, transitive, order-independent) so no exit inherits a sibling's probability key and
            // near-50/50 forks like (0.5 + 2^-42, 0.5 - 2^-42) NEVER contaminate the exact 2-way "50%" bin.
            var keyedExits = new List<(ForkExitPrediction Exit, string CanonicalKey, double LogProb)>(positiveExits.Count);
            for (int i = 0; i < positiveExits.Count; i++)
            {
                var ex = positiveExits[i];
                double lp = EffectiveLogProb(ex);
                keyedExits.Add((ex, FormatCanonicalBaseProbKey(ex.ConditionalProbability, lp), lp));
            }

            keyedExits.Sort((a, b) =>
            {
                int pc = b.Exit.ConditionalProbability.CompareTo(a.Exit.ConditionalProbability);
                if (pc != 0) return pc;
                int lc = b.LogProb.CompareTo(a.LogProb);
                if (lc != 0) return lc;
                int ia = IndexOfNeighbor(rawNeighbors, a.Exit.NextLandId);
                int ib = IndexOfNeighbor(rawNeighbors, b.Exit.NextLandId);
                return ia != ib ? ia.CompareTo(ib) : a.Exit.NextLandId.CompareTo(b.Exit.NextLandId);
            });

            string shape = string.Join("/", keyedExits.Select(e => e.Exit.FormatConditionalPercent(positiveExits.Count).TrimEnd('%')));

            // Exact 2-way uniform 50/50 ONLY: when called with a PredictedFork, honor the engine's mathematical
            // IsExactUniformSplit proof; when called directly with raw probabilities, require EXACT IEEE-754 0.5
            // (0.5 is exact in binary64, so 0.5 + 2^-42 is strictly excluded without any loose tolerance).
            bool isExactTwoWayFifty = keyedExits.Count == 2 && (isExactUniformOverride.HasValue
                ? isExactUniformOverride.Value
                : (keyedExits[0].Exit.ConditionalProbability == 0.5 && keyedExits[1].Exit.ConditionalProbability == 0.5));

            if (isExactTwoWayFifty)
            {
                // Reference exit is the Red-most exit in raw board adjacency order.
                var orderedPair = keyedExits
                    .OrderBy(k => IndexOfNeighbor(rawNeighbors, k.Exit.NextLandId))
                    .ThenBy(k => k.Exit.NextLandId)
                    .ToList();
                AddCalibrationObs(probBins, "50%", orderedPair[0].Exit, actualNext, shape);
                return inSupport;
            }

            // Group strictly by exact canonical key equality (never by loose non-transitive tolerance).
            var byKey = new Dictionary<string, List<ForkExitPrediction>>(StringComparer.Ordinal);
            var keyOrder = new List<string>();
            for (int i = 0; i < keyedExits.Count; i++)
            {
                string baseKey = keyedExits[i].CanonicalKey;
                if (string.Equals(baseKey, "50%", StringComparison.Ordinal))
                {
                    baseKey = $"50%(k={keyedExits.Count})";
                }
                if (!byKey.TryGetValue(baseKey, out var grp))
                {
                    grp = new List<ForkExitPrediction>();
                    byKey[baseKey] = grp;
                    keyOrder.Add(baseKey);
                }
                grp.Add(keyedExits[i].Exit);
            }

            for (int k = 0; k < keyOrder.Count; k++)
            {
                string binKey = keyOrder[k];
                var group = byKey[binKey];
                if (group.Count == 1)
                {
                    AddCalibrationObs(probBins, binKey, group[0], actualNext, shape);
                }
                else
                {
                    group.Sort((a, b) =>
                    {
                        int ia = IndexOfNeighbor(rawNeighbors, a.NextLandId);
                        int ib = IndexOfNeighbor(rawNeighbors, b.NextLandId);
                        return ia != ib ? ia.CompareTo(ib) : a.NextLandId.CompareTo(b.NextLandId);
                    });
                    for (int r = 0; r < group.Count; r++)
                    {
                        AddCalibrationObs(probBins, $"{binKey}#{r + 1}", group[r], actualNext, shape);
                    }
                }
            }

            return inSupport;
        }

        private static string FormatCanonicalBaseProbKey(double prob, double logProb)
        {
            if (prob == 0.5) return "50%";
            if (prob < 1e-10)
            {
                double roundedLog = Math.Round(logProb, 3, MidpointRounding.AwayFromZero);
                return $"exp({roundedLog.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)})";
            }

            // Context-independent canonical percent key quantized to 1e-8% (= 1e-10 probability) with trailing zeros trimmed;
            // if a non-0.5 probability (e.g. 0.5 + 2^-42) rounds to 50%, preserve full G17 precision so it never
            // collides with exact 50% or collapses opposite-signed near-50% exits into one bin.
            double pct = Math.Round(prob * 100.0, 8, MidpointRounding.AwayFromZero);
            if (pct == 50.0)
            {
                return (prob * 100.0).ToString("G17", System.Globalization.CultureInfo.InvariantCulture) + "%";
            }
            return pct.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture) + "%";
        }

        private static void AddCalibrationObs(Dictionary<string, ProbBin> probBins, string binKey, ForkExitPrediction exit, int actualNext, string shape)
        {
            if (!probBins.TryGetValue(binKey, out var pb)) probBins[binKey] = pb = new ProbBin();
            pb.Opp++;
            pb.SumPred += exit.ConditionalProbability;
            if (exit.NextLandId == actualNext) pb.Chosen++;
            pb.AddShape(shape);
        }

        private static int IndexOfNeighbor(IReadOnlyList<int> neighbors, int targetId)
        {
            for (int i = 0; i < neighbors.Count; i++)
                if (neighbors[i] == targetId) return i;
            return int.MaxValue;
        }
    }
}
