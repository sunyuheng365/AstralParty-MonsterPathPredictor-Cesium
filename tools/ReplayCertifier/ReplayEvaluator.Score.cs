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
            BoardGraph board,
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
                ApplyAttr(attr, board, players, monsters, true, curTurnActor);
            }
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
            ref bool thiefStoleOnTile)
        {
            for (int hop = 0; hop < 16; hop++)
            {
                ConsumePendingMoveEffects(moverId, u.NodeId, board, pendingMoveEffects, players, monsters, curTurnActor, ref thiefStoleOnTile);
                if (u.SkillDisplacedToNode < 0 || u.NodeId != u.SkillDisplacedToNode)
                    break;

                int fromN = u.SkillDisplacedFromNode;
                int toN = u.SkillDisplacedToNode;
                bool consumedEcho = false;
                if (idx + 1 < nodeIds.Length && nodeIds[idx] == fromN && nodeIds[idx + 1] == toN)
                {
                    idx += 2;
                    consumedEcho = true;
                }
                else if (idx < nodeIds.Length && nodeIds[idx] == toN)
                {
                    idx += 1;
                    consumedEcho = true;
                }

                // Clear pending echo when consumed inline or when this frame continues with non-echo steps;
                // preserve it only if we reached the end of this frame so a split follow-up 5028 can strip it.
                if (consumedEcho || idx < nodeIds.Length)
                {
                    u.SkillDisplacedFromNode = -1;
                    u.SkillDisplacedToNode = -1;
                }
                if (!consumedEcho) break;
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

            // Strip any MoveS2C prefix echoing an exact (From -> To) skill displacement already applied by 1040/1096.Place.
            int idx = StripSkillDisplacementPrefix(mover, nodeIds);
            mover.SkillDisplacedFromNode = -1;
            mover.SkillDisplacedToNode = -1;

            if (idx >= nodeIds.Length)
            {
                ConsumeAndSkipInlineDisplacements(mover, moverId, nodeIds, ref idx, board, pendingMoveEffects, players, monsters, curTurnActor, ref thiefStoleOnTile);
                return true;
            }

            // If MoveS2C echoed a displacement landing (idx > 0) or begins at mover.NodeId, consume deferred 1096
            // effects at mover.NodeId before advancing so split and merged frames execute identically.
            if (idx > 0 || nodeIds[idx] == mover.NodeId)
            {
                if (nodeIds[idx] == mover.NodeId) idx++;
                ConsumeAndSkipInlineDisplacements(mover, moverId, nodeIds, ref idx, board, pendingMoveEffects, players, monsters, curTurnActor, ref thiefStoleOnTile);
            }

            while (idx < nodeIds.Length)
            {
                int nextTile = nodeIds[idx];
                int curTile = mover.NodeId;
                if (nextTile == curTile)
                {
                    idx++;
                    ConsumeAndSkipInlineDisplacements(mover, moverId, nodeIds, ref idx, board, pendingMoveEffects, players, monsters, curTurnActor, ref thiefStoleOnTile);
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
                    bool thiefStoleHere = isThief && stepped && !foughtOnTile && thiefStoleOnTile;
                    bool isMidTurnMoving = stepped && !foughtOnTile && !thiefStoleHere;
                    if (board.IsForkForContext(curTile, fromTile, isMidTurnMoving))
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
                ConsumeAndSkipInlineDisplacements(mover, moverId, nodeIds, ref idx, board, pendingMoveEffects, players, monsters, curTurnActor, ref thiefStoleOnTile);
            }

            return true;
        }

        private static int StripSkillDisplacementPrefix(UnitSim u, int[] nodeIds)
        {
            if (u.SkillDisplacedToNode < 0 || u.NodeId != u.SkillDisplacedToNode || nodeIds.Length == 0)
                return 0;
            // Merged or standalone echo [From, To, ...]: strip From so iteration begins cleanly at To (u.NodeId).
            if (nodeIds.Length >= 2 && nodeIds[0] == u.SkillDisplacedFromNode && nodeIds[1] == u.SkillDisplacedToNode)
                return nodeIds.Length == 2 ? 2 : 1;
            if (nodeIds.Length == 1 && nodeIds[0] == u.SkillDisplacedToNode)
                return 1;
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
                    inSupport = RecordMultiBranchCalibration(board, curTile, forkPred.Exits, nextTile, summary.ProbBins);
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
            IReadOnlyList<ForkExitPrediction> exits,
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

            positiveExits.Sort((a, b) =>
            {
                double diff = b.ConditionalProbability - a.ConditionalProbability;
                if (Math.Abs(diff) > 1e-9) return diff > 0 ? 1 : -1;
                if (a.ConditionalProbability < 1e-9 || b.ConditionalProbability < 1e-9)
                {
                    double ldiff = EffectiveLogProb(b) - EffectiveLogProb(a);
                    if (Math.Abs(ldiff) > 1e-6) return ldiff > 0 ? 1 : -1;
                }
                int ia = IndexOfNeighbor(rawNeighbors, a.NextLandId);
                int ib = IndexOfNeighbor(rawNeighbors, b.NextLandId);
                return ia != ib ? ia.CompareTo(ib) : a.NextLandId.CompareTo(b.NextLandId);
            });

            // Cluster exits by exact numeric equality (<= 1e-9 in probability, or <= 1e-6 in log-prob when < 1e-9).
            var clusters = new List<(double Prob, double LogProb, List<ForkExitPrediction> Exits)>();
            for (int i = 0; i < positiveExits.Count; i++)
            {
                var ex = positiveExits[i];
                double exLog = EffectiveLogProb(ex);
                bool sameCluster = false;
                if (clusters.Count > 0)
                {
                    var prev = clusters[^1];
                    if (prev.Prob >= 1e-9 || ex.ConditionalProbability >= 1e-9)
                        sameCluster = Math.Abs(prev.Prob - ex.ConditionalProbability) <= 1e-9;
                    else
                        sameCluster = Math.Abs(prev.LogProb - exLog) <= 1e-6;
                }
                if (sameCluster)
                {
                    clusters[^1].Exits.Add(ex);
                }
                else
                {
                    clusters.Add((ex.ConditionalProbability, exLog, new List<ForkExitPrediction> { ex }));
                }
            }

            string shape = string.Join("/", positiveExits.Select(e => e.FormatConditionalPercent().TrimEnd('%')));

            // Exact 2-way uniform 50/50 gets the dedicated "50%" bin (Red-most reference exit).
            if (positiveExits.Count == 2 && clusters.Count == 1)
            {
                AddCalibrationObs(probBins, "50%", clusters[0].Exits[0], actualNext, shape);
                return inSupport;
            }

            for (int c = 0; c < clusters.Count; c++)
            {
                string binKey = FormatCanonicalProbKey(clusters[c].Prob, clusters[c].LogProb, positiveExits.Count);
                var group = clusters[c].Exits;
                if (group.Count == 1)
                {
                    AddCalibrationObs(probBins, binKey, group[0], actualNext, shape);
                }
                else
                {
                    for (int r = 0; r < group.Count; r++)
                    {
                        AddCalibrationObs(probBins, $"{binKey}#{r + 1}", group[r], actualNext, shape);
                    }
                }
            }

            return inSupport;
        }

        private static string FormatCanonicalProbKey(double prob, double logProb, int totalPositiveExits)
        {
            if (prob < 1e-10)
            {
                double roundedLog = Math.Round(logProb, 3, MidpointRounding.AwayFromZero);
                return $"exp({roundedLog.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)})";
            }

            // Context-independent canonical percent key quantized to 1e-8% (= 1e-10 probability, 10x finer than
            // cluster tolerance 1e-9) with trailing zeros trimmed: 0.25 -> "25%", 0.2502 -> "25.02%", 0.50004 -> "50.004%".
            double pct = Math.Round(prob * 100.0, 8, MidpointRounding.AwayFromZero);
            string key = pct.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture) + "%";
            if (string.Equals(key, "50%", StringComparison.Ordinal))
            {
                return $"50%(k={totalPositiveExits})";
            }
            return key;
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
