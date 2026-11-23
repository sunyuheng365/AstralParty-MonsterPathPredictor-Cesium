using System;
using System.Collections.Generic;

namespace MonsterPathPredictor.Core
{
    /// <summary>
    /// Implements Astral Party PVE monster pathfinding prediction over X steps.
    ///
    /// Canonical rule hierarchy (verified against BV1Kxsq67E9C replay analytics & 7 official 3.3.0+ maps):
    /// 1. Skill-displaced monsters (垃圾箱/雪莉/玲玲) follow their specific displacement rules on the first step.
    /// 2. Freshly spawned monsters on a fork follow their spawn direction arrow (FrontNodeIds).
    /// 3. Starting on a fork OR continuing right after combat on that fork ("一步索敌"): ignores player positions;
    ///    follows Red > Blue > None (raw neighbor order) without U-turning.
    /// 4. Mid-walk arrival at a fork ("之后再遇岔，看当时站位"): ranks all living, targetable players by full-board
    ///    shortest road distance (including U-turn paths, unlimited distance), breaking ties by seat order P1 -> P4.
    ///    Draws uniformly from all equal-length shortest paths ("每条最短路=1支签"); U-turn probability cascades
    ///    to the next candidate; remaining probability after all candidates splits equally across forward exits (50%/50%).
    /// 5. Encountering a living player along the walk emits a PredictedCombat marker and honors PostCombatMode:
    ///    - ContinueByGuide: follows "之后再遇岔，看当时站位", marking continuation edges/forks with IsAfterCombat=true
    ///    - StopAtFirstCombat: truncates the forecast at the first encounter tile
    ///    - RedBlueAfterCombat: switches subsequent forks after combat to Red > Blue priority
    /// </summary>
    public static class MonsterPathEngine
    {
        private const double Epsilon = 1e-9;

        private struct StepOutcome
        {
            public List<(int NextLandId, double Prob)> Transitions;
            public bool IsForkDecision;
            public ForkReasonKind Reason;
            public string SummaryLabel;
        }

        private struct WalkStateKey : IEquatable<WalkStateKey>
        {
            public int LandId;
            public int FromLandId;
            public bool HasEncounteredPlayer;

            public WalkStateKey(int landId, int fromLandId, bool hasEncounteredPlayer)
            {
                LandId = landId;
                FromLandId = fromLandId;
                HasEncounteredPlayer = hasEncounteredPlayer;
            }

            public bool Equals(WalkStateKey other) =>
                LandId == other.LandId &&
                FromLandId == other.FromLandId &&
                HasEncounteredPlayer == other.HasEncounteredPlayer;

            public override bool Equals(object obj) => obj is WalkStateKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = LandId;
                    h = (h * 397) ^ FromLandId;
                    h = (h * 397) ^ (HasEncounteredPlayer ? 1 : 0);
                    return h;
                }
            }
        }

        private struct ForkRecordKey : IEquatable<ForkRecordKey>
        {
            public int LandId;
            public int FromLandId;
            public bool AfterCombat;

            public ForkRecordKey(int landId, int fromLandId, bool afterCombat)
            {
                LandId = landId;
                FromLandId = fromLandId;
                AfterCombat = afterCombat;
            }

            public bool Equals(ForkRecordKey other) =>
                LandId == other.LandId && FromLandId == other.FromLandId && AfterCombat == other.AfterCombat;

            public override bool Equals(object obj) => obj is ForkRecordKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = LandId;
                    h = (h * 397) ^ FromLandId;
                    h = (h * 397) ^ (AfterCombat ? 1 : 0);
                    return h;
                }
            }
        }

        private struct EdgeKey : IEquatable<EdgeKey>
        {
            public int From;
            public int To;

            public EdgeKey(int from, int to)
            {
                From = from;
                To = to;
            }

            public bool Equals(EdgeKey other) => From == other.From && To == other.To;
            public override bool Equals(object obj) => obj is EdgeKey other && Equals(other);
            public override int GetHashCode() => (From * 397) ^ To;
        }

        public static PathPlan Plan(
            BoardGraph board,
            MonsterState monster,
            IReadOnlyList<TrackedPlayer> players,
            PlanOptions options = null)
        {
            if (board == null) throw new ArgumentNullException(nameof(board));
            if (monster == null) throw new ArgumentNullException(nameof(monster));

            options = options ?? new PlanOptions();
            int maxSteps = Math.Max(1, Math.Min(60, options.MaxSteps));
            double minProb = Math.Max(0.0, options.MinBranchProbability);

            string displayName = !string.IsNullOrEmpty(monster.DisplayName) ? monster.DisplayName : (monster.Name ?? string.Empty);
            var plan = new PathPlan
            {
                MonsterId = monster.MonsterId,
                MonsterName = displayName,
                StartLandId = monster.CurrentLandId,
                StepsRequested = maxSteps,
                IsNormalAggressive = monster.IsNormalAggressive
            };

            if (!board.Contains(monster.CurrentLandId))
            {
                plan.HeadlineNote = "怪物地块无效";
                return plan;
            }

            var activePlayers = FilterTrackablePlayers(board, players);
            var shortestPathCache = new Dictionary<int, ShortestPathField>();
            var playersByLand = BuildPlayersByLand(activePlayers);

            var edgeMap = new Dictionary<EdgeKey, PredictedEdge>();
            var edgeHasPreCombat = new HashSet<EdgeKey>();
            var forkMap = new Dictionary<ForkRecordKey, PredictedFork>();
            var combatMap = new Dictionary<int, PredictedCombat>();
            var endpointMap = new Dictionary<(int LandId, int Step, bool DeadEnd, bool StoppedAtCombat, bool AfterCombat), double>();

            bool initialCombatState = monster.HasLaunchedFightThisTurn || monster.JustFoughtOnCurrentFork;
            var currentLayer = new Dictionary<WalkStateKey, double>
            {
                [new WalkStateKey(monster.CurrentLandId, monster.FromLandId, initialCombatState)] = 1.0
            };

            for (int step = 0; step < maxSteps && currentLayer.Count > 0; step++)
            {
                var nextLayer = new Dictionary<WalkStateKey, double>();

                foreach (var entry in currentLayer)
                {
                    var state = entry.Key;
                    double arrivalProb = entry.Value;
                    if (arrivalProb <= Epsilon) continue;
                    if (step > 0 && arrivalProb < minProb) continue;

                    bool postCombatNow = state.HasEncounteredPlayer;
                    bool encounteredOnThisTile = false;

                    // When stepping onto a tile occupied by living player(s) during the walk (step > 0),
                    // an aggressive monster initiates combat on this tile (once per turn).
                    if (step > 0 && monster.IsNormalAggressive && !postCombatNow &&
                        playersByLand.TryGetValue(state.LandId, out var targetsHere))
                    {
                        encounteredOnThisTile = true;
                        postCombatNow = true;
                        RecordCombat(combatMap, state.LandId, step, arrivalProb, targetsHere);

                        if (options.PostCombatMode == PostCombatMode.StopAtFirstCombat)
                        {
                            AccumulateEndpoint(endpointMap, state.LandId, step, false, true, true, arrivalProb);
                            continue;
                        }
                    }

                    StepOutcome outcome = step == 0
                        ? DecideFirstStep(board, monster, activePlayers, shortestPathCache, options.PostCombatMode)
                        : DecideMidWalkStep(
                            board,
                            state.LandId,
                            state.FromLandId,
                            activePlayers,
                            encounteredOnThisTile,
                            postCombatNow,
                            shortestPathCache,
                            options);

                    if (outcome.Transitions == null || outcome.Transitions.Count == 0)
                    {
                        AccumulateEndpoint(endpointMap, state.LandId, step, true, false, postCombatNow, arrivalProb);
                        continue;
                    }

                    if (outcome.IsForkDecision)
                    {
                        RecordFork(board, forkMap, state.LandId, state.FromLandId, step, arrivalProb, postCombatNow, outcome);
                        if (step == 0 && string.IsNullOrEmpty(plan.HeadlineNote))
                        {
                            plan.HeadlineNote = outcome.SummaryLabel;
                        }
                    }

                    for (int i = 0; i < outcome.Transitions.Count; i++)
                    {
                        int nextId = outcome.Transitions[i].NextLandId;
                        double condProb = outcome.Transitions[i].Prob;
                        double branchProb = arrivalProb * condProb;
                        if (branchProb <= Epsilon) continue;

                        var ek = new EdgeKey(state.LandId, nextId);
                        if (!edgeMap.TryGetValue(ek, out var pe))
                        {
                            pe = new PredictedEdge
                            {
                                FromLandId = state.LandId,
                                ToLandId = nextId,
                                Probability = 0.0,
                                EarliestStep = step,
                                IsAfterCombat = postCombatNow
                            };
                            edgeMap[ek] = pe;
                        }
                        if (!postCombatNow)
                        {
                            edgeHasPreCombat.Add(ek);
                            pe.IsAfterCombat = false;
                        }
                        else if (!edgeHasPreCombat.Contains(ek))
                        {
                            pe.IsAfterCombat = true;
                        }

                        pe.Probability = Math.Min(1.0, pe.Probability + branchProb);
                        if (step < pe.EarliestStep) pe.EarliestStep = step;

                        var nextKey = new WalkStateKey(nextId, state.LandId, postCombatNow);
                        if (nextLayer.TryGetValue(nextKey, out double existing))
                            nextLayer[nextKey] = existing + branchProb;
                        else
                            nextLayer[nextKey] = branchProb;
                    }
                }

                currentLayer = nextLayer;
            }

            // Also record combat if the final step lands directly on a living player
            foreach (var finalState in currentLayer)
            {
                if (finalState.Value <= Epsilon) continue;
                bool stoppedCombat = false;
                bool afterCombat = finalState.Key.HasEncounteredPlayer;
                if (monster.IsNormalAggressive && !afterCombat &&
                    playersByLand.TryGetValue(finalState.Key.LandId, out var endTargets))
                {
                    RecordCombat(combatMap, finalState.Key.LandId, maxSteps, finalState.Value, endTargets);
                    afterCombat = true;
                    stoppedCombat = (options.PostCombatMode == PostCombatMode.StopAtFirstCombat);
                }

                AccumulateEndpoint(endpointMap, finalState.Key.LandId, maxSteps, false, stoppedCombat, afterCombat, finalState.Value);
            }

            plan.Edges.AddRange(edgeMap.Values);
            plan.Edges.Sort((a, b) =>
            {
                int c = a.EarliestStep.CompareTo(b.EarliestStep);
                return c != 0 ? c : b.Probability.CompareTo(a.Probability);
            });

            plan.Forks.AddRange(forkMap.Values);
            plan.Forks.Sort((a, b) =>
            {
                int c = a.StepIndex.CompareTo(b.StepIndex);
                return c != 0 ? c : b.ArrivalProbability.CompareTo(a.ArrivalProbability);
            });

            plan.Combats.AddRange(combatMap.Values);
            plan.Combats.Sort((a, b) => a.StepIndex.CompareTo(b.StepIndex));

            foreach (var ep in endpointMap)
            {
                plan.Endpoints.Add(new PredictedEndpoint
                {
                    LandId = ep.Key.LandId,
                    StepIndex = ep.Key.Step,
                    IsDeadEnd = ep.Key.DeadEnd,
                    StoppedAtCombat = ep.Key.StoppedAtCombat,
                    IsAfterCombat = ep.Key.AfterCombat,
                    Probability = Math.Min(1.0, ep.Value)
                });
            }
            plan.Endpoints.Sort((a, b) => b.Probability.CompareTo(a.Probability));

            if (string.IsNullOrEmpty(plan.HeadlineNote))
            {
                if (!monster.IsNormalAggressive)
                    plan.HeadlineNote = "非主动怪";
                else if (plan.Forks.Count > 0)
                    plan.HeadlineNote = plan.Forks[0].SummaryLabel;
                else if (plan.Combats.Count > 0)
                    plan.HeadlineNote = "遇" + plan.Combats[0].TargetLabel;
                else
                    plan.HeadlineNote = "沿路直行";
            }
            else if (!monster.IsNormalAggressive)
            {
                plan.HeadlineNote = "非主动 · " + plan.HeadlineNote;
            }

            return plan;
        }

        private static StepOutcome DecideFirstStep(
            BoardGraph board,
            MonsterState monster,
            List<TrackedPlayer> activePlayers,
            Dictionary<int, ShortestPathField> cache,
            PostCombatMode postCombatMode)
        {
            int cur = monster.CurrentLandId;
            int from = monster.FromLandId;
            bool isFork = board.IsFork(cur);

            // 1. Skill displacement rules (Bilibili video section 4)
            if (monster.SkillMoveKind != SkillMoveKind.None)
            {
                return DecideSkillDisplacedStep(board, monster, isFork);
            }

            // 2. Freshly spawned on a fork: follow explicit spawn triangle arrow (FrontNodeIds)
            if (monster.JustSpawnedOnFork && isFork)
            {
                var validFront = FilterValidNeighbors(board, cur, monster.FrontNodeIds);
                if (validFront.Count == 1)
                {
                    return SingleTransition(validFront[0], isForkDecision: true, ForkReasonKind.SpawnDirection, "出生朝向");
                }
            }

            // 3. Standing on a fork:
            //    - Turn start on fork OR post-combat on fork -> Red > Blue (ignores players)
            //    - Already mid-walk at fork (without combat on this fork) -> Mid-walk queue & lottery (or Red>Blue if configured)
            if (isFork)
            {
                if (monster.JustFoughtOnCurrentFork || !monster.IsCurrentlyMovingMidTurn)
                {
                    bool hasValidFrom = board.IsValidNeighbor(cur, from);
                    if (!hasValidFrom)
                    {
                        var validFront = FilterValidNeighbors(board, cur, monster.FrontNodeIds);
                        if (validFront.Count == 1)
                        {
                            return SingleTransition(validFront[0], isForkDecision: true, ForkReasonKind.SpawnDirection, "朝向指示");
                        }
                    }

                    if (board.TryGetPriorityForwardNeighbor(cur, hasValidFrom ? from : -1, out int nextId, out bool isGlobalRed))
                    {
                        ForkReasonKind reason = monster.JustFoughtOnCurrentFork
                            ? (isGlobalRed ? ForkReasonKind.PostCombatRed : ForkReasonKind.PostCombatBlue)
                            : (isGlobalRed ? ForkReasonKind.ForkStartRed : ForkReasonKind.ForkStartBlue);
                        string label = monster.JustFoughtOnCurrentFork
                            ? (isGlobalRed ? "战后·红箭" : "战后·蓝箭")
                            : (isGlobalRed ? "起步·红箭" : "起步·蓝箭");
                        return SingleTransition(nextId, isForkDecision: true, reason, label);
                    }
                    return EmptyOutcome();
                }

                if (monster.HasLaunchedFightThisTurn && postCombatMode == PostCombatMode.RedBlueAfterCombat)
                {
                    if (board.TryGetPriorityForwardNeighbor(cur, from, out int nextId, out bool isGlobalRed))
                    {
                        return SingleTransition(
                            nextId,
                            isForkDecision: true,
                            isGlobalRed ? ForkReasonKind.PostCombatRed : ForkReasonKind.PostCombatBlue,
                            isGlobalRed ? "战后续行·红箭" : "战后续行·蓝箭");
                    }
                }

                return EvaluateMidWalkFork(board, cur, from, activePlayers, cache, isAfterCombat: monster.HasLaunchedFightThisTurn);
            }

            // 4. Normal tile (non-fork, degree <= 2)
            var forward = board.GetForwardNeighbors(cur, from);
            if (forward.Count == 1)
            {
                return SingleTransition(forward[0], isForkDecision: false, ForkReasonKind.MidForkChasePlayer, string.Empty);
            }

            var frontOnNormal = FilterValidNeighbors(board, cur, monster.FrontNodeIds);
            if (frontOnNormal.Count == 1)
            {
                return SingleTransition(frontOnNormal[0], isForkDecision: false, ForkReasonKind.SpawnDirection, string.Empty);
            }
            if (frontOnNormal.Count > 1)
            {
                return UniformSplit(frontOnNormal, ForkReasonKind.UnknownFacingSplit, "朝向未知");
            }

            var all = board.GetValidNeighbors(cur);
            if (all.Count == 1)
            {
                return SingleTransition(all[0], isForkDecision: false, ForkReasonKind.SpawnDirection, string.Empty);
            }
            if (all.Count > 1)
            {
                return UniformSplit(all, ForkReasonKind.UnknownFacingSplit, "朝向未知");
            }
            return EmptyOutcome();
        }

        private static StepOutcome DecideSkillDisplacedStep(BoardGraph board, MonsterState monster, bool isFork)
        {
            int cur = monster.CurrentLandId;
            var all = board.GetValidNeighbors(cur);
            if (all.Count == 0) return EmptyOutcome();

            switch (monster.SkillMoveKind)
            {
                case SkillMoveKind.TrashcanPull:
                    if (isFork)
                        return SingleTransition(all[0], isForkDecision: true, ForkReasonKind.SkillTrashcanRed, "垃圾箱·红箭");
                    return UniformSplit(all, ForkReasonKind.SkillNormalRandom, "垃圾箱·随机");

                case SkillMoveKind.ShirleyThrow:
                    if (isFork)
                        return UniformSplit(all, ForkReasonKind.SkillShirleyRandom, "雪莉·随机");
                    return UniformSplit(all, ForkReasonKind.SkillNormalRandom, "雪莉·随机");

                case SkillMoveKind.LinglingPush:
                    if (isFork)
                    {
                        int exclude = board.IsValidNeighbor(cur, monster.SkillMovePushFromLandId)
                            ? monster.SkillMovePushFromLandId
                            : -1;
                        if (board.TryGetPriorityForwardNeighbor(cur, exclude, out int nextId, out bool isGlobalRed))
                        {
                            string tag = isGlobalRed ? "玲玲·红箭" : "玲玲·蓝箭";
                            return SingleTransition(nextId, isForkDecision: true, ForkReasonKind.SkillLinglingRedBlue, tag);
                        }
                        return EmptyOutcome();
                    }
                    if (board.IsValidNeighbor(cur, monster.SkillMovePushDirectionLandId))
                    {
                        return SingleTransition(monster.SkillMovePushDirectionLandId, isForkDecision: true, ForkReasonKind.SkillLinglingPushDir, "玲玲·推向");
                    }
                    var front = FilterValidNeighbors(board, cur, monster.FrontNodeIds);
                    if (front.Count == 1)
                    {
                        return SingleTransition(front[0], isForkDecision: true, ForkReasonKind.SkillLinglingPushDir, "玲玲·推向");
                    }
                    return UniformSplit(all, ForkReasonKind.SkillNormalRandom, "玲玲·推向");

                default:
                    return EmptyOutcome();
            }
        }

        private static StepOutcome DecideMidWalkStep(
            BoardGraph board,
            int landId,
            int fromLandId,
            List<TrackedPlayer> activePlayers,
            bool combatHappenedOnThisTile,
            bool isAfterCombat,
            Dictionary<int, ShortestPathField> cache,
            PlanOptions options)
        {
            var forward = board.GetForwardNeighbors(landId, fromLandId);
            if (forward.Count == 0) return EmptyOutcome();
            if (forward.Count == 1)
            {
                return SingleTransition(forward[0], isForkDecision: false, ForkReasonKind.MidForkChasePlayer, string.Empty);
            }

            // Combat occurred RIGHT ON this fork -> leaving this fork ALWAYS uses Red > Blue ("在岔路口打完架离开那一步")
            if (combatHappenedOnThisTile && options.SimulateCombatOnFork)
            {
                if (board.TryGetPriorityForwardNeighbor(landId, fromLandId, out int nextId, out bool isGlobalRed))
                {
                    return SingleTransition(
                        nextId,
                        isForkDecision: true,
                        isGlobalRed ? ForkReasonKind.PostCombatRed : ForkReasonKind.PostCombatBlue,
                        isGlobalRed ? "战后·红箭" : "战后·蓝箭");
                }
            }

            // Optional user mode: after combat anywhere, treat remaining forks as Red > Blue
            if (isAfterCombat && options.PostCombatMode == PostCombatMode.RedBlueAfterCombat)
            {
                if (board.TryGetPriorityForwardNeighbor(landId, fromLandId, out int nextId, out bool isGlobalRed))
                {
                    return SingleTransition(
                        nextId,
                        isForkDecision: true,
                        isGlobalRed ? ForkReasonKind.PostCombatRed : ForkReasonKind.PostCombatBlue,
                        isGlobalRed ? "战后续行·红箭" : "战后续行·蓝箭");
                }
            }

            // Canonical guide rule: "之后再遇岔，看当时站位"
            return EvaluateMidWalkFork(board, landId, fromLandId, activePlayers, cache, isAfterCombat);
        }

        private static StepOutcome EvaluateMidWalkFork(
            BoardGraph board,
            int forkLandId,
            int incomingLandId,
            List<TrackedPlayer> activePlayers,
            Dictionary<int, ShortestPathField> cache,
            bool isAfterCombat)
        {
            var forward = board.GetForwardNeighbors(forkLandId, incomingLandId);
            if (forward.Count == 0) return EmptyOutcome();

            var exitProbs = new Dictionary<int, double>(forward.Count);
            for (int i = 0; i < forward.Count; i++) exitProbs[forward[i]] = 0.0;

            var allNeighbors = board.GetValidNeighbors(forkLandId);
            bool incomingIsNeighbor = board.IsValidNeighbor(forkLandId, incomingLandId);

            // Rank reachable trackable players by (full-board shortest distance ASC, Slot P1->P4 ASC)
            var ranked = new List<(TrackedPlayer Player, int Dist, ShortestPathField Field)>(activePlayers.Count);
            for (int i = 0; i < activePlayers.Count; i++)
            {
                var p = activePlayers[i];
                if (p.LandId == forkLandId) continue;
                if (!cache.TryGetValue(p.LandId, out var field))
                {
                    field = board.ComputeShortestPathsTo(p.LandId);
                    cache[p.LandId] = field;
                }
                int d = field.GetDistance(forkLandId);
                if (d == int.MaxValue || d <= 0) continue;
                ranked.Add((p, d, field));
            }

            ranked.Sort((a, b) =>
            {
                int dc = a.Dist.CompareTo(b.Dist);
                if (dc != 0) return dc;
                int sc = a.Player.Slot.CompareTo(b.Player.Slot);
                if (sc != 0) return sc;
                return a.Player.PlayerId.CompareTo(b.Player.PlayerId);
            });

            if (ranked.Count == 0)
            {
                return UniformSplit(forward, ForkReasonKind.MidForkNoTargetFallback, isAfterCombat ? "续行·均分" : "无目标·均分");
            }

            double remainingProb = 1.0;
            TrackedPlayer primaryTarget = null;
            int primaryDist = 0;
            bool usedMultiShortestPath = false;

            for (int i = 0; i < ranked.Count && remainingProb > Epsilon; i++)
            {
                var item = ranked[i];
                int targetDist = item.Dist;
                var field = item.Field;

                double totalLots = 0.0;
                double uTurnLots = 0.0;
                int forwardLotExits = 0;

                for (int j = 0; j < allNeighbors.Count; j++)
                {
                    int nb = allNeighbors[j];
                    if (field.GetDistance(nb) != targetDist - 1) continue;
                    double lots = field.GetPathCount(nb);
                    if (lots <= 0.0) continue;

                    totalLots += lots;
                    if (incomingIsNeighbor && nb == incomingLandId)
                        uTurnLots += lots;
                    else
                        forwardLotExits++;
                }

                if (totalLots <= 0.0) continue;

                if (forwardLotExits > 0)
                {
                    if (primaryTarget == null)
                    {
                        primaryTarget = item.Player;
                        primaryDist = targetDist;
                    }
                    if (forwardLotExits > 1 || uTurnLots > 0.0)
                    {
                        usedMultiShortestPath = true;
                    }

                    for (int j = 0; j < forward.Count; j++)
                    {
                        int fwd = forward[j];
                        if (field.GetDistance(fwd) != targetDist - 1) continue;
                        double lots = field.GetPathCount(fwd);
                        if (lots <= 0.0) continue;
                        exitProbs[fwd] += remainingProb * (lots / totalLots);
                    }
                }

                remainingProb *= (uTurnLots / totalLots);
            }

            bool usedUTurnFallback = false;
            if (remainingProb > Epsilon)
            {
                usedUTurnFallback = true;
                double share = remainingProb / forward.Count;
                for (int i = 0; i < forward.Count; i++)
                {
                    exitProbs[forward[i]] += share;
                }
            }

            var transitions = new List<(int NextLandId, double Prob)>(forward.Count);
            for (int i = 0; i < forward.Count; i++)
            {
                int fwd = forward[i];
                double p = exitProbs[fwd];
                if (p > Epsilon) transitions.Add((fwd, p));
            }

            transitions.Sort((a, b) => b.Prob.CompareTo(a.Prob));

            string prefix = isAfterCombat ? "续行" : string.Empty;
            ForkReasonKind reason;
            string label;
            if (primaryTarget == null && usedUTurnFallback)
            {
                reason = ForkReasonKind.MidForkAllUTurnFallback;
                label = isAfterCombat ? "续行·平分" : "掉头平分";
            }
            else if (transitions.Count > 1)
            {
                reason = ForkReasonKind.MidForkMultiShortestPath;
                if (primaryTarget != null)
                    label = (isAfterCombat ? "续行·多路(" : (usedMultiShortestPath ? "多路(" : "抽签(")) + primaryTarget.SeatTag + ")";
                else
                    label = isAfterCombat ? "续行·分流" : "分流";
            }
            else
            {
                reason = ForkReasonKind.MidForkChasePlayer;
                label = primaryTarget != null
                    ? prefix + "→" + primaryTarget.SeatTag + "(" + primaryDist + "格)"
                    : "直行";
            }

            return new StepOutcome
            {
                Transitions = transitions,
                IsForkDecision = true,
                Reason = reason,
                SummaryLabel = label
            };
        }

        private static void RecordFork(
            BoardGraph board,
            Dictionary<ForkRecordKey, PredictedFork> forkMap,
            int forkLandId,
            int incomingLandId,
            int stepIndex,
            double arrivalProb,
            bool isAfterCombat,
            StepOutcome outcome)
        {
            var key = new ForkRecordKey(forkLandId, incomingLandId, isAfterCombat);
            var validAll = board.GetValidNeighbors(forkLandId);
            int globalRed = validAll.Count > 0 ? validAll[0] : -1;

            if (!forkMap.TryGetValue(key, out var pf))
            {
                pf = new PredictedFork
                {
                    ForkLandId = forkLandId,
                    IncomingLandId = incomingLandId,
                    StepIndex = stepIndex,
                    ArrivalProbability = arrivalProb,
                    IsAfterCombat = isAfterCombat,
                    ReasonKind = outcome.Reason,
                    SummaryLabel = outcome.SummaryLabel
                };

                for (int i = 0; i < outcome.Transitions.Count; i++)
                {
                    var tr = outcome.Transitions[i];
                    pf.Exits.Add(new ForkExitPrediction
                    {
                        NextLandId = tr.NextLandId,
                        ConditionalProbability = tr.Prob,
                        CumulativeProbability = Math.Min(1.0, arrivalProb * tr.Prob),
                        IsGlobalRedArrow = (tr.NextLandId == globalRed)
                    });
                }

                forkMap[key] = pf;
                return;
            }

            pf.ArrivalProbability = Math.Min(1.0, pf.ArrivalProbability + arrivalProb);
            for (int i = 0; i < pf.Exits.Count; i++)
            {
                pf.Exits[i].CumulativeProbability = Math.Min(1.0, pf.ArrivalProbability * pf.Exits[i].ConditionalProbability);
            }
        }

        private static void RecordCombat(
            Dictionary<int, PredictedCombat> combatMap,
            int landId,
            int stepIndex,
            double probability,
            List<TrackedPlayer> targets)
        {
            if (!combatMap.TryGetValue(landId, out var c))
            {
                var seats = new List<string>(targets.Count);
                for (int i = 0; i < targets.Count; i++) seats.Add(targets[i].SeatTag);
                seats.Sort(StringComparer.Ordinal);
                c = new PredictedCombat
                {
                    LandId = landId,
                    StepIndex = stepIndex,
                    Probability = probability,
                    TargetLabel = string.Join("/", seats)
                };
                combatMap[landId] = c;
            }
            else
            {
                c.Probability = Math.Min(1.0, c.Probability + probability);
                if (stepIndex < c.StepIndex) c.StepIndex = stepIndex;
            }
        }

        private static Dictionary<int, List<TrackedPlayer>> BuildPlayersByLand(List<TrackedPlayer> activePlayers)
        {
            var dict = new Dictionary<int, List<TrackedPlayer>>();
            for (int i = 0; i < activePlayers.Count; i++)
            {
                var p = activePlayers[i];
                if (!dict.TryGetValue(p.LandId, out var list))
                {
                    list = new List<TrackedPlayer>();
                    dict[p.LandId] = list;
                }
                list.Add(p);
            }
            return dict;
        }

        private static List<TrackedPlayer> FilterTrackablePlayers(BoardGraph board, IReadOnlyList<TrackedPlayer> players)
        {
            var result = new List<TrackedPlayer>();
            if (players == null) return result;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p == null || !p.IsAlive || !p.IsTargetable) continue;
                if (!board.Contains(p.LandId)) continue;
                result.Add(p);
            }
            return result;
        }

        private static List<int> FilterValidNeighbors(BoardGraph board, int landId, int[] candidates)
        {
            var list = new List<int>();
            if (candidates == null) return list;
            var seen = new HashSet<int>();
            for (int i = 0; i < candidates.Length; i++)
            {
                int id = candidates[i];
                if (seen.Add(id) && board.IsValidNeighbor(landId, id))
                {
                    list.Add(id);
                }
            }
            return list;
        }

        private static void AccumulateEndpoint(
            Dictionary<(int LandId, int Step, bool DeadEnd, bool StoppedAtCombat, bool AfterCombat), double> map,
            int landId,
            int step,
            bool deadEnd,
            bool stoppedAtCombat,
            bool afterCombat,
            double prob)
        {
            var key = (landId, step, deadEnd, stoppedAtCombat, afterCombat);
            if (map.TryGetValue(key, out double existing))
                map[key] = existing + prob;
            else
                map[key] = prob;
        }

        private static StepOutcome EmptyOutcome() => new StepOutcome { Transitions = new List<(int, double)>(0) };

        private static StepOutcome SingleTransition(int nextLandId, bool isForkDecision, ForkReasonKind reason, string label)
        {
            return new StepOutcome
            {
                Transitions = new List<(int, double)>(1) { (nextLandId, 1.0) },
                IsForkDecision = isForkDecision,
                Reason = reason,
                SummaryLabel = label
            };
        }

        private static StepOutcome UniformSplit(IReadOnlyList<int> exits, ForkReasonKind reason, string label)
        {
            var list = new List<(int, double)>(exits.Count);
            double p = 1.0 / exits.Count;
            for (int i = 0; i < exits.Count; i++) list.Add((exits[i], p));
            return new StepOutcome
            {
                Transitions = list,
                IsForkDecision = true,
                Reason = reason,
                SummaryLabel = label
            };
        }
    }
}
