using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using MonsterPathPredictor.Core;

namespace MonsterPathPredictor.ReplayCertifier
{
    internal static partial class ReplayEvaluator
    {
        private static void HandleMove(
            dynamic mv, string replayId, int mapId, int curRound, int fi, BoardGraph board,
            Dictionary<long, UnitSim> players, Dictionary<long, UnitSim> monsters, Dictionary<long, int> spawnedAtTile,
            bool startedOnFork, int turnStartLandId, ref bool stepped, ref bool foughtOnTile, bool foughtTurn, ref bool thiefStoleOnTile,
            MapStats mapStat, CertificationSummary summary)
        {
            long moverId = (long)mv.PlayerId;
            int[] nodeIds = ToIntArray((IEnumerable)mv.NodeIds);
            if (nodeIds.Length == 0) return;

            if (players.TryGetValue(moverId, out var mp))
            {
                int prev = mp.NodeId;
                foreach (int nid in nodeIds) if (nid != prev) { mp.BackNodeId = prev; prev = nid; }
                mp.NodeId = prev;
                return;
            }

            if (!monsters.TryGetValue(moverId, out var m)) return;
            bool isNormalAggro = m.MonsterType == 2 || m.MonsterType == 10 || m.MonsterType == 11 || m.MonsterType == 12;
            bool isThief = m.MonsterType == 1;

            var pathSteps = new List<int>();
            foreach (int nid in nodeIds)
            {
                if (pathSteps.Count == 0 && nid == m.NodeId) continue;
                if (pathSteps.Count > 0 && pathSteps[^1] == nid) continue;
                pathSteps.Add(nid);
            }

            for (int s = 0; s < pathSteps.Count; s++)
            {
                int curTile = m.NodeId, nextTile = pathSteps[s], fromTile = m.BackNodeId;

                if (!m.DisplacedBySkill && (isNormalAggro || isThief) &&
                    board.IsFork(curTile) && board.IsValidNeighbor(curTile, nextTile))
                {
                    ScoreForkDecision(m, isNormalAggro, isThief, curTile, fromTile, nextTile,
                        replayId, mapId, curRound, fi, board, players, spawnedAtTile,
                        startedOnFork, turnStartLandId, stepped, foughtOnTile, foughtTurn, thiefStoleOnTile, mapStat, summary);
                }

                m.BackNodeId = curTile;
                m.NodeId = nextTile;
                m.FrontNodeIds = board.GetForwardNeighbors(nextTile, curTile).ToArray();
                m.DisplacedBySkill = false;
                stepped = true;
                foughtOnTile = false;
                thiefStoleOnTile = false;
                if (spawnedAtTile.TryGetValue(m.Id, out int spt) && spt != nextTile) spawnedAtTile.Remove(m.Id);
            }
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
                var fa = summary.FrontAudit;
                if (isPostCombat && vf.Length >= 2) { fa.PostCombatMulti++; if (rbNext == nextTile) fa.PostCombatMultiRedBlue++; }
                else if (isFreshSpawn && vf.Length == 1) { fa.SpawnF1++; if (vf[0] == nextTile) fa.SpawnF1Actual++; if (vf[0] == rbNext) fa.SpawnF1RedBlue++; }
                else if (!isFreshSpawn && !isPostCombat)
                {
                    if (vf.Length == 1) { fa.StopF1++; if (vf[0] == nextTile) fa.StopF1Actual++; if (vf[0] == rbNext) fa.StopF1RedBlue++; }
                    else if (vf.Length >= 2) { fa.StopMulti++; if (rbNext == nextTile) fa.StopMultiRedBlue++; }
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

            var forkPred = plan.Forks.FirstOrDefault(f => f.ForkLandId == curTile);
            if (forkPred == null || forkPred.Exits.Count == 0) return;

            string bucketKey = isThief ? "ThiefFork"
                : isPostCombat ? "PostCombatFork"
                : isFreshSpawn ? "SpawnFork"
                : isTurnStart ? "ForkStart"
                : foughtTurn ? "PostNormalCombatMidFork"
                : "MidWalkFork";

            var bStat = summary.Buckets[bucketKey];
            bStat.Total++;

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
            else if (isNormalAggro)
            {
                bStat.ProbTotal++;
                mapStat.ProbTotal++;
                foreach (var exit in forkPred.Exits)
                {
                    string binKey = ProbabilityFormat.Percent(exit.ConditionalProbability);
                    if (!summary.ProbBins.TryGetValue(binKey, out var pb)) summary.ProbBins[binKey] = pb = new ProbBin();
                    pb.Opp++;
                    pb.SumPred += exit.ConditionalProbability;
                    if (exit.NextLandId == nextTile) pb.Chosen++;
                }
            }
        }
    }
}
