using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using MonsterPathPredictor.Core;

namespace MonsterPathPredictor.ReplayCertifier
{
    internal static partial class ReplayEvaluator
    {
        private static void ConsumePendingMoveEffects(
            long moverId,
            int tileId,
            Dictionary<long, Dictionary<int, List<dynamic>>> pendingMoveEffects,
            Dictionary<long, UnitSim> players,
            Dictionary<long, UnitSim> monsters,
            long curTurnActor,
            ref bool thiefStoleOnTile)
        {
            if (pendingMoveEffects == null || !pendingMoveEffects.TryGetValue(moverId, out var byTile)) return;
            if (!byTile.TryGetValue(tileId, out var attrs)) return;
            byTile.Remove(tileId);
            if (byTile.Count == 0) pendingMoveEffects.Remove(moverId);
            foreach (var attr in attrs)
            {
                // 1096 HeroSkillMoveEffectS2C pass-by effects (e.g., Forest thief aura 10701102)
                // do NOT trigger the fork-interrupting steal pause (only 1040 UpdateHeroAttrS2C does).
                ApplyAttr(attr, players, monsters, true, curTurnActor);
            }
        }

        private static void HandleMove(
            dynamic mv, string replayId, int mapId, int curRound, int fi, BoardGraph board,
            Dictionary<long, UnitSim> players, Dictionary<long, UnitSim> monsters, Dictionary<long, int> spawnedAtTile,
            Dictionary<long, Dictionary<int, List<dynamic>>> pendingMoveEffects, long curTurnActor,
            bool startedOnFork, int turnStartLandId, ref bool stepped, ref bool foughtOnTile, bool foughtTurn, ref bool thiefStoleOnTile,
            MapStats mapStat, CertificationSummary summary)
        {
            long moverId = (long)mv.PlayerId;
            int[] nodeIds = ToIntArray((IEnumerable)mv.NodeIds);
            if (nodeIds.Length == 0) return;

            if (players.TryGetValue(moverId, out var mp))
            {
                if (IsExactSkillDisplacementEcho(mp, nodeIds))
                {
                    mp.SkillDisplacedFromNode = -1;
                    mp.SkillDisplacedToNode = -1;
                    ConsumePendingMoveEffects(moverId, mp.NodeId, pendingMoveEffects, players, monsters, curTurnActor, ref thiefStoleOnTile);
                    return;
                }
                mp.SkillDisplacedFromNode = -1;
                mp.SkillDisplacedToNode = -1;

                int prev = mp.NodeId;
                foreach (int nid in nodeIds)
                {
                    if (nid == prev)
                    {
                        ConsumePendingMoveEffects(moverId, prev, pendingMoveEffects, players, monsters, curTurnActor, ref thiefStoleOnTile);
                        continue;
                    }
                    bool validPlayerEdge = board.Contains(prev) && board.Contains(nid) && board.IsValidNeighbor(prev, nid);
                    if (!validPlayerEdge)
                    {
                        summary.IllegalMoveEdges++;
                    }
                    mp.BackNodeId = prev;
                    prev = nid;
                    mp.NodeId = prev;
                    ConsumePendingMoveEffects(moverId, prev, pendingMoveEffects, players, monsters, curTurnActor, ref thiefStoleOnTile);
                }
                return;
            }

            if (!monsters.TryGetValue(moverId, out var m)) return;
            bool isNormalAggro = m.MonsterType == 2 || m.MonsterType == 10 || m.MonsterType == 11 || m.MonsterType == 12;
            bool isThief = m.MonsterType == 1;

            // Only deduplicate a MoveS2C that echoes the exact (From -> To) skill displacement already applied by 1096.Place;
            // any subsequent normal move leaving the landing tile MUST be validated and scored normally.
            if (IsExactSkillDisplacementEcho(m, nodeIds))
            {
                m.SkillDisplacedFromNode = -1;
                m.SkillDisplacedToNode = -1;
                ConsumePendingMoveEffects(moverId, m.NodeId, pendingMoveEffects, players, monsters, curTurnActor, ref thiefStoleOnTile);
                return;
            }
            m.SkillDisplacedFromNode = -1;
            m.SkillDisplacedToNode = -1;

            var pathSteps = new List<int>();
            foreach (int nid in nodeIds)
            {
                if (pathSteps.Count == 0 && nid == m.NodeId) continue;
                if (pathSteps.Count > 0 && pathSteps[^1] == nid) continue;
                pathSteps.Add(nid);
            }

            bool isActiveTurnMonster = (moverId == curTurnActor);
            for (int s = 0; s < pathSteps.Count; s++)
            {
                int curTile = m.NodeId, nextTile = pathSteps[s], fromTile = m.BackNodeId;
                bool validEdge = board.Contains(curTile) && board.Contains(nextTile) && board.IsValidNeighbor(curTile, nextTile);

                if (isNormalAggro || isThief)
                {
                    if (!validEdge)
                    {
                        summary.IllegalMoveEdges++;
                    }
                    else if (isActiveTurnMonster && board.IsFork(curTile))
                    {
                        ScoreForkDecision(m, isNormalAggro, isThief, curTile, fromTile, nextTile,
                            replayId, mapId, curRound, fi, board, players, spawnedAtTile,
                            startedOnFork, turnStartLandId, stepped, foughtOnTile, foughtTurn, thiefStoleOnTile, mapStat, summary);
                    }
                }

                m.BackNodeId = curTile;
                m.NodeId = nextTile;
                m.FrontNodeIds = board.GetForwardNeighbors(nextTile, curTile).ToArray();
                if (isActiveTurnMonster)
                {
                    stepped = true;
                    foughtOnTile = false;
                    thiefStoleOnTile = false;
                }
                if (spawnedAtTile.TryGetValue(m.Id, out int spt) && spt != nextTile) spawnedAtTile.Remove(m.Id);
                ConsumePendingMoveEffects(moverId, nextTile, pendingMoveEffects, players, monsters, curTurnActor, ref thiefStoleOnTile);
            }
        }

        private static bool IsExactSkillDisplacementEcho(UnitSim u, int[] nodeIds)
        {
            if (u.SkillDisplacedToNode < 0 || u.NodeId != u.SkillDisplacedToNode || nodeIds.Length == 0)
                return false;
            if (nodeIds.Length == 1 && nodeIds[0] == u.SkillDisplacedToNode)
                return true;
            if (nodeIds.Length == 2 && nodeIds[0] == u.SkillDisplacedFromNode && nodeIds[1] == u.SkillDisplacedToNode)
                return true;
            return false;
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
                else if (isFreshSpawn && vf.Length == 1) { fa.SpawnF1++; if (vf[0] == nextTile) fa.SpawnF1Actual++; if (vf[0] == rbNext) fa.SpawnF1RedBlue++; }
                else if (!isFreshSpawn && !isPostCombat)
                {
                    if (vf.Length == 1) { fa.StopF1++; if (vf[0] == nextTile) fa.StopF1Actual++; if (vf[0] == rbNext) fa.StopF1RedBlue++; }
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
                ChasesMandarinDuckMark = (m.HeroId == 1047)
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
                CrimeNum = p.CrimeNum,
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
                    inSupport = RecordMultiBranchCalibration(board, curTile, forkPred.Exits, nextTile, summary.ProbBins);
                }
                else
                {
                    inSupport = IsInPredictedSupport(forkPred.Exits, nextTile);
                }

                if (!inSupport)
                {
                    summary.OutOfSupportPredictions++;
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
                if (exits[i].NextLandId == actualNext && exits[i].ConditionalProbability > 0.0)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Records probabilistic calibration observations for a multi-branch fork.
        /// 1. First verifies that <paramref name="actualNext"/> is in the predicted support (positive probability);
        ///    returns false if out-of-support so GATE 0 fails instead of hiding zero-probability outcomes.
        /// 2. For a 2-exit equal-probability fork (50%/50%), scores ONE deterministically chosen reference exit
        ///    selected BEFORE observing actualNext (earliest in raw BoardGraph Red>Blue order); the complementary
        ///    exit has identical |z| and opposite sign.
        /// 3. For k>=3 exits, every exit in any equal-probability subgroup is scored in its own rank-tagged bin
        ///    ("<pct>#1", "<pct>#2", ..., "<pct>#m") ordered by raw neighbor priority — omitting zero subgroups
        ///    so biases like 1/3,1/3,1/6,1/6 -> 40/60/20/0 or 32/32/16/40 can NEVER hide!
        /// </summary>
        public static bool RecordMultiBranchCalibration(
            BoardGraph board,
            int forkLandId,
            IReadOnlyList<ForkExitPrediction> exits,
            int actualNext,
            Dictionary<string, ProbBin> probBins)
        {
            if (exits == null || exits.Count < 2) return false;
            if (!IsInPredictedSupport(exits, actualNext)) return false;

            var rawNeighbors = board.GetValidNeighbors(forkLandId);
            var byBin = new Dictionary<string, List<ForkExitPrediction>>(StringComparer.Ordinal);
            int positiveExitsCount = 0;
            for (int i = 0; i < exits.Count; i++)
            {
                if (exits[i].ConditionalProbability <= 0.0) continue;
                positiveExitsCount++;
                string binKey = ProbabilityFormat.Percent(exits[i].ConditionalProbability);
                if (!byBin.TryGetValue(binKey, out var group)) byBin[binKey] = group = new List<ForkExitPrediction>();
                group.Add(exits[i]);
            }

            foreach (var kv in byBin)
            {
                string binKey = kv.Key;
                var group = kv.Value;
                if (group.Count == 1)
                {
                    AddCalibrationObs(probBins, binKey, group[0], actualNext);
                }
                else
                {
                    group.Sort((a, b) =>
                    {
                        int ia = IndexOfNeighbor(rawNeighbors, a.NextLandId);
                        int ib = IndexOfNeighbor(rawNeighbors, b.NextLandId);
                        return ia != ib ? ia.CompareTo(ib) : a.NextLandId.CompareTo(b.NextLandId);
                    });

                    if (positiveExitsCount == 2 && group.Count == 2)
                    {
                        // Exact binary complement: z(exit2) = -z(exit1), so scoring reference exit #1 covers both without self-cancellation.
                        AddCalibrationObs(probBins, binKey, group[0], actualNext);
                    }
                    else
                    {
                        // Score EVERY member of an equal-probability subgroup in its own rank-tagged bin
                        // so no bias across multiple equal-prob subgroups (e.g. 1/3,1/3,1/6,1/6) can ever be omitted.
                        for (int r = 0; r < group.Count; r++)
                        {
                            AddCalibrationObs(probBins, $"{binKey}#{r + 1}", group[r], actualNext);
                        }
                    }
                }
            }

            return true;
        }

        private static void AddCalibrationObs(Dictionary<string, ProbBin> probBins, string binKey, ForkExitPrediction exit, int actualNext)
        {
            if (!probBins.TryGetValue(binKey, out var pb)) probBins[binKey] = pb = new ProbBin();
            pb.Opp++;
            pb.SumPred += exit.ConditionalProbability;
            if (exit.NextLandId == actualNext) pb.Chosen++;
        }

        private static int IndexOfNeighbor(IReadOnlyList<int> neighbors, int targetId)
        {
            for (int i = 0; i < neighbors.Count; i++)
                if (neighbors[i] == targetId) return i;
            return int.MaxValue;
        }
    }
}
