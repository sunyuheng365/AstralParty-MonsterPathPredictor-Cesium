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

        private static bool HandleMove(
            dynamic mv, string replayId, int mapId, int curRound, int fi, BoardGraph board,
            Dictionary<long, UnitSim> players, Dictionary<long, UnitSim> monsters, Dictionary<long, int> spawnedAtTile,
            Dictionary<long, Dictionary<int, List<dynamic>>> pendingMoveEffects, long curTurnActor,
            bool startedOnFork, int turnStartLandId, ref bool stepped, ref bool foughtOnTile, bool foughtTurn, ref bool thiefStoleOnTile,
            MapStats mapStat, CertificationSummary summary)
        {
            long moverId = (long)mv.PlayerId;
            int[] nodeIds = ToIntArray((IEnumerable)mv.NodeIds);
            if (nodeIds.Length == 0) return true;

            if (players.TryGetValue(moverId, out var mp))
            {
                int startIdx = StripSkillDisplacementPrefix(mp, nodeIds);
                mp.SkillDisplacedFromNode = -1;
                mp.SkillDisplacedToNode = -1;
                if (startIdx >= nodeIds.Length)
                {
                    ConsumePendingMoveEffects(moverId, mp.NodeId, board, pendingMoveEffects, players, monsters, curTurnActor, ref thiefStoleOnTile);
                    return true;
                }

                int prev = mp.NodeId;
                for (int idx = startIdx; idx < nodeIds.Length; idx++)
                {
                    int nid = nodeIds[idx];
                    if (nid == prev)
                    {
                        ConsumePendingMoveEffects(moverId, prev, board, pendingMoveEffects, players, monsters, curTurnActor, ref thiefStoleOnTile);
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
                    ConsumePendingMoveEffects(moverId, prev, board, pendingMoveEffects, players, monsters, curTurnActor, ref thiefStoleOnTile);
                }
                return true;
            }

            if (!monsters.TryGetValue(moverId, out var m))
            {
                // Unresolved mover in a state-carrying 5028 frame: still validate every consecutive edge
                // independently on the board AND flag an integrity violation.
                if (nodeIds.Length == 1)
                {
                    if (!board.Contains(nodeIds[0])) summary.IllegalMoveEdges++;
                }
                else
                {
                    int prev = nodeIds[0];
                    for (int idx = 1; idx < nodeIds.Length; idx++)
                    {
                        int nid = nodeIds[idx];
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
            bool isNormalAggro = m.MonsterType == 2 || m.MonsterType == 10 || m.MonsterType == 11 || m.MonsterType == 12;
            bool isThief = m.MonsterType == 1;

            // Strip any MoveS2C prefix echoing the exact (From -> To) skill displacement already applied by 1040/1096.Place;
            // any remaining normal steps leaving the landing tile MUST be validated and scored normally.
            int mStartIdx = StripSkillDisplacementPrefix(m, nodeIds);
            m.SkillDisplacedFromNode = -1;
            m.SkillDisplacedToNode = -1;
            if (mStartIdx >= nodeIds.Length)
            {
                ConsumePendingMoveEffects(moverId, m.NodeId, board, pendingMoveEffects, players, monsters, curTurnActor, ref thiefStoleOnTile);
                return true;
            }

            // When MoveS2C echoes a displacement landing (mStartIdx > 0) or begins at the monster's current tile,
            // consume deferred 1096 tile effects for m.NodeId BEFORE traversing subsequent steps so split
            // [1,2]+[2,3,4] and merged [1,2,3,4] frames execute identically.
            if (mStartIdx > 0 || nodeIds[0] == m.NodeId)
            {
                ConsumePendingMoveEffects(moverId, m.NodeId, board, pendingMoveEffects, players, monsters, curTurnActor, ref thiefStoleOnTile);
            }

            var pathSteps = new List<int>();
            for (int idx = mStartIdx; idx < nodeIds.Length; idx++)
            {
                int nid = nodeIds[idx];
                if (pathSteps.Count == 0 && nid == m.NodeId) continue;
                if (pathSteps.Count > 0 && pathSteps[^1] == nid) continue;
                pathSteps.Add(nid);
            }

            bool isActiveTurnMonster = (moverId == curTurnActor);
            for (int s = 0; s < pathSteps.Count; s++)
            {
                int curTile = m.NodeId, nextTile = pathSteps[s], fromTile = m.BackNodeId;
                bool validEdge = board.Contains(curTile) && board.Contains(nextTile) && board.IsValidNeighbor(curTile, nextTile);

                // Validate edge legality for ALL recognized monsters (including MonsterType=30 friendly allies);
                // restrict fork prediction scoring to aggressive monsters and thieves.
                if (!validEdge)
                {
                    summary.IllegalMoveEdges++;
                }
                else if ((isNormalAggro || isThief) && isActiveTurnMonster && board.IsFork(curTile))
                {
                    ScoreForkDecision(m, isNormalAggro, isThief, curTile, fromTile, nextTile,
                        replayId, mapId, curRound, fi, board, players, spawnedAtTile,
                        startedOnFork, turnStartLandId, stepped, foughtOnTile, foughtTurn, thiefStoleOnTile, mapStat, summary);
                }

                m.BackNodeId = curTile;
                m.NodeId = nextTile;
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
                ConsumePendingMoveEffects(moverId, nextTile, board, pendingMoveEffects, players, monsters, curTurnActor, ref thiefStoleOnTile);
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
                if (exits[i].NextLandId == actualNext && exits[i].ConditionalProbability > 0.0)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Records uncensored probabilistic calibration observations for a multi-branch fork.
        /// 1. Every issued multi-branch prediction is recorded in calibration bins BEFORE returning whether
        ///    <paramref name="actualNext"/> was in the positive-probability support, so out-of-support outcomes
        ///    never censor calibration denominators (GATE 0 catches out-of-support AND GATE 3 sees true hit rates).
        /// 2. Equal-probability grouping is strictly numeric (|pA - pB| &lt;= 1e-9), never by rounded percentage string,
        ///    so near-50% forks (0.5004/0.4996) are not collapsed into 50/50.
        /// 3. Exact 2-way uniform 50/50 forks use the dedicated "50%" bin (Red-most reference exit), while any 50% exit
        ///    inside a k&gt;=3 fork (e.g. [1/2, 1/4, 1/4]) is tagged "50%(k=3)" so "50%" contains pure 2-way 50/50 only.
        /// 4. For k&gt;=3 forks, every member of any equal-probability cluster gets its own rank-tagged bin ("&lt;pct&gt;#1", "#2", ...).
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
                if (exits[i].ConditionalProbability > 0.0) positiveExits.Add(exits[i]);
            }
            if (positiveExits.Count < 2) return false;

            positiveExits.Sort((a, b) =>
            {
                double diff = b.ConditionalProbability - a.ConditionalProbability;
                if (Math.Abs(diff) > 1e-9) return diff > 0 ? 1 : -1;
                int ia = IndexOfNeighbor(rawNeighbors, a.NextLandId);
                int ib = IndexOfNeighbor(rawNeighbors, b.NextLandId);
                return ia != ib ? ia.CompareTo(ib) : a.NextLandId.CompareTo(b.NextLandId);
            });

            // Cluster exits by exact numeric equality (<= 1e-9), NOT formatted percentage strings.
            var clusters = new List<(double Prob, List<ForkExitPrediction> Exits)>();
            for (int i = 0; i < positiveExits.Count; i++)
            {
                var ex = positiveExits[i];
                if (clusters.Count > 0 && Math.Abs(clusters[^1].Prob - ex.ConditionalProbability) <= 1e-9)
                {
                    clusters[^1].Exits.Add(ex);
                }
                else
                {
                    clusters.Add((ex.ConditionalProbability, new List<ForkExitPrediction> { ex }));
                }
            }

            string shape = string.Join("/", positiveExits.Select(e => ProbabilityFormat.Percent(e.ConditionalProbability).TrimEnd('%')));

            // Exact 2-way uniform 50/50 gets the dedicated "50%" bin (Red-most reference exit).
            if (positiveExits.Count == 2 && clusters.Count == 1)
            {
                AddCalibrationObs(probBins, "50%", clusters[0].Exits[0], actualNext, shape);
                return inSupport;
            }

            // Resolve pairwise-distinct keys across ALL distinct numeric clusters so near-equal probabilities
            // (e.g. 0.5004/0.4996 or 0.50004/0.49996) NEVER collide into the same calibration bin and self-cancel.
            var clusterKeys = ResolveDistinctClusterKeys(clusters, positiveExits.Count);

            for (int c = 0; c < clusters.Count; c++)
            {
                string binKey = clusterKeys[c];
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

        private static string[] ResolveDistinctClusterKeys(
            List<(double Prob, List<ForkExitPrediction> Exits)> clusters,
            int totalPositiveExits)
        {
            var keys = new string[clusters.Count];
            for (int c = 0; c < clusters.Count; c++)
            {
                keys[c] = ProbabilityFormat.Percent(clusters[c].Prob);
            }

            // Escalate decimal precision for any colliding clusters until every distinct numeric cluster has a unique key.
            string[] precisionFormats = { "0.00", "0.000", "0.0000", "0.00000", "0.000000", "0.0000000", "0.00000000", "G15" };
            for (int p = 0; p < precisionFormats.Length; p++)
            {
                var dupIndices = FindCollidingIndices(keys);
                if (dupIndices.Count == 0) break;
                foreach (int idx in dupIndices)
                {
                    keys[idx] = (clusters[idx].Prob * 100.0).ToString(precisionFormats[p], System.Globalization.CultureInfo.InvariantCulture) + "%";
                }
            }

            // Never mix non-2-way 50% clusters (e.g. [1/2,1/4,1/4]) into the dedicated 2-way "50%" bin.
            for (int c = 0; c < keys.Length; c++)
            {
                if (string.Equals(keys[c], "50%", StringComparison.Ordinal))
                {
                    keys[c] = $"50%(k={totalPositiveExits})";
                }
            }

            // Absolute fallback guarantee: if any key still collides across distinct clusters, disambiguate deterministically.
            var remainingDups = FindCollidingIndices(keys);
            if (remainingDups.Count > 0)
            {
                foreach (int idx in remainingDups)
                {
                    keys[idx] = $"{keys[idx]}~{idx + 1}";
                }
            }
            return keys;
        }

        private static List<int> FindCollidingIndices(string[] keys)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < keys.Length; i++)
            {
                counts.TryGetValue(keys[i], out int cnt);
                counts[keys[i]] = cnt + 1;
            }
            var dups = new List<int>();
            for (int i = 0; i < keys.Length; i++)
            {
                if (counts[keys[i]] > 1) dups.Add(i);
            }
            return dups;
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
