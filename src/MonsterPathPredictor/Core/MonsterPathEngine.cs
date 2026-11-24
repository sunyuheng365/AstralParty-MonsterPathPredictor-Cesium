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
            public int ClearedMarkLandId;

            public WalkStateKey(int landId, int fromLandId, bool hasEncounteredPlayer, int clearedMarkLandId = -1)
            {
                LandId = landId;
                FromLandId = fromLandId;
                HasEncounteredPlayer = hasEncounteredPlayer;
                ClearedMarkLandId = clearedMarkLandId;
            }

            public bool Equals(WalkStateKey other) =>
                LandId == other.LandId &&
                FromLandId == other.FromLandId &&
                HasEncounteredPlayer == other.HasEncounteredPlayer &&
                ClearedMarkLandId == other.ClearedMarkLandId;

            public override bool Equals(object obj) => obj is WalkStateKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = LandId;
                    h = (h * 397) ^ FromLandId;
                    h = (h * 397) ^ (HasEncounteredPlayer ? 1 : 0);
                    h = (h * 397) ^ ClearedMarkLandId;
                    return h;
                }
            }
        }

        private struct ForkRecordKey : IEquatable<ForkRecordKey>
        {
            public int LandId;
            public int FromLandId;
            public int StepIndex;
            public bool AfterCombat;
            public int ClearedMarkLandId;

            public ForkRecordKey(int landId, int fromLandId, int stepIndex, bool afterCombat, int clearedMarkLandId)
            {
                LandId = landId;
                FromLandId = fromLandId;
                StepIndex = stepIndex;
                AfterCombat = afterCombat;
                ClearedMarkLandId = clearedMarkLandId;
            }

            public bool Equals(ForkRecordKey other) =>
                LandId == other.LandId &&
                FromLandId == other.FromLandId &&
                StepIndex == other.StepIndex &&
                AfterCombat == other.AfterCombat &&
                ClearedMarkLandId == other.ClearedMarkLandId;

            public override bool Equals(object obj) => obj is ForkRecordKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = LandId;
                    h = (h * 397) ^ FromLandId;
                    h = (h * 397) ^ StepIndex;
                    h = (h * 397) ^ (AfterCombat ? 1 : 0);
                    h = (h * 397) ^ ClearedMarkLandId;
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
            int maxSteps = Math.Max(0, Math.Min(60, options.MaxSteps));
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

            var activePlayers = FilterEffectivePlayers(board, monster, players);
            var shortestPathCache = new Dictionary<int, ShortestPathField>();
            var combatPlayersByLand = BuildPlayersByLand(activePlayers, combatEligibleOnly: true);
            var stealPlayersByLand = BuildPlayersByLand(activePlayers, combatEligibleOnly: false);

            var edgeMap = new Dictionary<EdgeKey, PredictedEdge>();
            var edgeHasPreCombat = new HashSet<EdgeKey>();
            var forkMap = new Dictionary<ForkRecordKey, PredictedFork>();
            var combatMap = new Dictionary<int, PredictedCombat>();
            var endpointMap = new Dictionary<(int LandId, int Step, bool DeadEnd, bool StoppedAtCombat, bool AfterCombat), double>();
            var tileStepMap = new Dictionary<int, PredictedTileStep>();
            var tileStepHasPreCombat = new HashSet<int>();

            bool initialCombatState = monster.HasLaunchedFightThisTurn || monster.JustFoughtOnCurrentFork;
            RecordTileStep(tileStepMap, tileStepHasPreCombat, monster.CurrentLandId, step: 0, probability: 1.0, isAfterCombat: initialCombatState);

            var currentLayer = new Dictionary<WalkStateKey, double>
            {
                [new WalkStateKey(monster.CurrentLandId, monster.FromLandId, initialCombatState, -1)] = 1.0
            };

            for (int step = 0; step < maxSteps && currentLayer.Count > 0; step++)
            {
                var nextLayer = new Dictionary<WalkStateKey, double>();

                foreach (var entry in currentLayer)
                {
                    var state = entry.Key;
                    double arrivalProb = entry.Value;
                    if (arrivalProb <= 0.0) continue;
                    if (step > 0 && minProb > 0.0 && arrivalProb < minProb)
                    {
                        plan.PrunedProbability += arrivalProb;
                        continue;
                    }

                    bool postCombatNow = state.HasEncounteredPlayer;
                    int clearedMarkLandId = state.ClearedMarkLandId;
                    bool encounteredOnThisTile = false;

                    // When stepping onto a tile occupied by living player(s) during the walk (step > 0):
                    // - Aggressive monster initiates combat only against CanBeAttacked players (excludes stealth buff 1270202).
                    // - Thief steals coins from living player(s) with Gold != 0, interrupting movement on a fork
                    //   so leaving that fork switches to Red > Blue.
                    if (step > 0 && !postCombatNow)
                    {
                        if (monster.IsNormalAggressive && combatPlayersByLand.TryGetValue(state.LandId, out var targetsHere))
                        {
                            encounteredOnThisTile = true;
                            postCombatNow = true;
                            if (monster.ChasesProblemStudent && AnyHasProblemStudentMark(targetsHere))
                            {
                                clearedMarkLandId = state.LandId;
                            }
                            RecordCombat(combatMap, state.LandId, step, arrivalProb, targetsHere, isSteal: false);

                            if (options.PostCombatMode == PostCombatMode.StopAtFirstCombat)
                            {
                                AccumulateEndpoint(endpointMap, state.LandId, step, false, true, true, arrivalProb);
                                continue;
                            }
                        }
                        else if (monster.IsThief && stealPlayersByLand.TryGetValue(state.LandId, out var stealCandidates))
                        {
                            var stealTargets = FilterStealablePlayers(stealCandidates);
                            if (stealTargets.Count > 0)
                            {
                                encounteredOnThisTile = true;
                                postCombatNow = true;
                                RecordCombat(combatMap, state.LandId, step, arrivalProb, stealTargets, isSteal: true);
                            }
                        }
                    }

                    StepOutcome outcome = step == 0
                        ? DecideFirstStep(board, monster, activePlayers, shortestPathCache, options.PostCombatMode)
                        : DecideMidWalkStep(
                            board,
                            monster,
                            state.LandId,
                            state.FromLandId,
                            activePlayers,
                            encounteredOnThisTile,
                            postCombatNow,
                            clearedMarkLandId,
                            shortestPathCache,
                            options);

                    if (outcome.Transitions == null || outcome.Transitions.Count == 0)
                    {
                        AccumulateEndpoint(endpointMap, state.LandId, step, true, false, postCombatNow, arrivalProb);
                        continue;
                    }

                    if (outcome.IsForkDecision)
                    {
                        RecordFork(board, forkMap, state.LandId, state.FromLandId, step, arrivalProb, postCombatNow, clearedMarkLandId, outcome);
                        if (step == 0 && string.IsNullOrEmpty(plan.HeadlineNote))
                        {
                            plan.HeadlineNote = outcome.SummaryLabel;
                        }
                    }
                    else if (step == 0 && string.IsNullOrEmpty(plan.HeadlineNote) && !string.IsNullOrEmpty(outcome.SummaryLabel))
                    {
                        plan.HeadlineNote = outcome.SummaryLabel;
                    }

                    for (int i = 0; i < outcome.Transitions.Count; i++)
                    {
                        int nextId = outcome.Transitions[i].NextLandId;
                        double condProb = outcome.Transitions[i].Prob;
                        double branchProb = arrivalProb * condProb;
                        if (branchProb <= 0.0) continue;

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

                        pe.StepProbabilities.TryGetValue(step, out double prevEdgeStepProb);
                        double nextEdgeStepProb = Math.Min(1.0, prevEdgeStepProb + branchProb);
                        pe.StepProbabilities[step] = nextEdgeStepProb;
                        if (nextEdgeStepProb > pe.Probability) pe.Probability = nextEdgeStepProb;
                        if (step < pe.EarliestStep) pe.EarliestStep = step;

                        RecordTileStep(tileStepMap, tileStepHasPreCombat, nextId, step + 1, branchProb, postCombatNow);

                        var nextKey = new WalkStateKey(nextId, state.LandId, postCombatNow, clearedMarkLandId);
                        if (nextLayer.TryGetValue(nextKey, out double existing))
                            nextLayer[nextKey] = existing + branchProb;
                        else
                            nextLayer[nextKey] = branchProb;
                    }
                }

                currentLayer = nextLayer;
            }

            // Also record combat if the final step lands directly on a living player (when maxSteps > 0),
            // and detect final-step dead-ends consistently regardless of whether MaxSteps hit exact length.
            foreach (var finalState in currentLayer)
            {
                if (finalState.Value <= 0.0) continue;
                bool stoppedCombat = false;
                bool afterCombat = finalState.Key.HasEncounteredPlayer;
                if (maxSteps > 0 && !afterCombat)
                {
                    if (monster.IsNormalAggressive && combatPlayersByLand.TryGetValue(finalState.Key.LandId, out var endTargets))
                    {
                        RecordCombat(combatMap, finalState.Key.LandId, maxSteps, finalState.Value, endTargets, isSteal: false);
                        afterCombat = true;
                        stoppedCombat = (options.PostCombatMode == PostCombatMode.StopAtFirstCombat);
                    }
                    else if (monster.IsThief && stealPlayersByLand.TryGetValue(finalState.Key.LandId, out var endStealCandidates))
                    {
                        var stealTargets = FilterStealablePlayers(endStealCandidates);
                        if (stealTargets.Count > 0)
                        {
                            RecordCombat(combatMap, finalState.Key.LandId, maxSteps, finalState.Value, stealTargets, isSteal: true);
                            afterCombat = true;
                        }
                    }
                }

                bool isDeadEnd = false;
                if (!stoppedCombat && maxSteps > 0)
                {
                    isDeadEnd = board.GetForwardNeighbors(finalState.Key.LandId, finalState.Key.FromLandId).Count == 0;
                }

                AccumulateEndpoint(endpointMap, finalState.Key.LandId, maxSteps, isDeadEnd, stoppedCombat, afterCombat, finalState.Value);
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

                if (tileStepMap.TryGetValue(ep.Key.LandId, out var ts))
                {
                    ts.IsEndpoint = true;
                    if (ep.Key.DeadEnd) ts.IsDeadEnd = true;
                }
            }
            plan.Endpoints.Sort((a, b) => b.Probability.CompareTo(a.Probability));

            foreach (var ts in tileStepMap.Values)
            {
                ts.StepIndices.Sort();
                if (ts.StepIndices.Count > 0) ts.StepIndex = ts.StepIndices[0];
                plan.TileSteps.Add(ts);
            }
            plan.TileSteps.Sort((a, b) =>
            {
                int c = a.StepIndex.CompareTo(b.StepIndex);
                return c != 0 ? c : a.LandId.CompareTo(b.LandId);
            });

            if (string.IsNullOrEmpty(plan.HeadlineNote))
            {
                if (!monster.IsNormalAggressive)
                    plan.HeadlineNote = "非主动怪";
                else if (plan.Forks.Count > 0)
                    plan.HeadlineNote = plan.Forks[0].SummaryLabel;
                else if (plan.Combats.Count > 0)
                    plan.HeadlineNote = "遇" + plan.Combats[0].TargetLabel;
                else if (monster.RequiresCrimeTarget && activePlayers.Count == 0)
                    plan.HeadlineNote = "无罪证目标";
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

            // 0. Taunt override (嘲讽 — allows overriding facing / U-turning before combat at turn start or forks;
            //    matches DecideMidWalkStep so straight-road continuation past an unattackable taunter never diverges).
            if ((!monster.IsCurrentlyMovingMidTurn || isFork) &&
                !monster.JustFoughtOnCurrentFork && !monster.HasLaunchedFightThisTurn &&
                monster.IsTaunted && monster.TauntTargetPlayerId != 0 &&
                TryDecideTauntStep(board, cur, monster.TauntTargetPlayerId, activePlayers, cache, out var tauntOutcome))
            {
                return tauntOutcome;
            }

            // 0.5 Marked-target free-direction override:
            //   - 1018 学院安全官 (魔法学院/海选赛运动场) -> 【问题学生】 (buff 2101001)
            //   - 1047 铜盏鸳鸯 (园林中庭) -> buff 10470101
            //   Allows overriding facing / U-turning before combat at turn start (straight road or fork) and at mid-walk forks
            //   (excluding players standing on the monster's turn-start tile, which monsters never engage on that turn).
            int excludedTurnStartLand = ResolveExcludedTurnStartLandId(monster, isMidTurn: monster.IsCurrentlyMovingMidTurn);
            if ((!monster.IsCurrentlyMovingMidTurn || isFork) &&
                !monster.JustFoughtOnCurrentFork && !monster.HasLaunchedFightThisTurn &&
                monster.SkillMoveKind == SkillMoveKind.None &&
                (monster.ChasesProblemStudent || monster.ChasesMandarinDuckMark))
            {
                var marked = GetPriorityMarkedPlayers(monster, activePlayers, clearedMarkLandId: -1, excludedTurnStartLandId: excludedTurnStartLand);
                if (marked.Count > 0 &&
                    TryDecideMarkedTargetTurnStartStep(
                        board,
                        cur,
                        excludedTurnStartLand,
                        marked,
                        cache,
                        monster.ChasesProblemStudent ? "追问题生" : "追标记",
                        out var markOutcome))
                {
                    return markOutcome;
                }
            }

            // 1. Skill displacement rules (Bilibili video section 4)
            if (monster.SkillMoveKind != SkillMoveKind.None)
            {
                return DecideSkillDisplacedStep(board, monster, isFork);
            }

            var validFront = FilterValidNeighbors(board, cur, monster.FrontNodeIds);

            // 2. Standing on a fork:
            //    - Turn start on fork / spawned on fork / post-combat on fork:
            //      ALWAYS read the monster's current facing (FrontNodeIds) first!
            //        * If FrontNodeIds has 1 direction -> strictly follow that current facing arrow (NOT map red/blue).
            //        * If FrontNodeIds has 2+ directions -> choose Red > Blue strictly among FrontNodeIds.
            //        * Only if FrontNodeIds is empty -> fallback to Red > Blue excluding FromLandId.
            //    - Already mid-walk at fork (without combat on this fork) -> Mid-walk queue & lottery.
            if (isFork)
            {
                if (monster.JustFoughtOnCurrentFork || !monster.IsCurrentlyMovingMidTurn)
                {
                    if (validFront.Count == 1)
                    {
                        ForkReasonKind reason = monster.JustSpawnedOnFork
                            ? ForkReasonKind.SpawnDirection
                            : ForkReasonKind.CurrentFacing;
                        string label = monster.JustSpawnedOnFork ? "出生朝向" : "当前朝向";
                        return SingleTransition(validFront[0], isForkDecision: true, reason, label);
                    }

                    bool resolved;
                    int nextId;
                    bool isGlobalRed;
                    if (validFront.Count >= 2)
                    {
                        resolved = board.TryGetPriorityAmongCandidates(cur, validFront, out nextId, out isGlobalRed);
                    }
                    else
                    {
                        bool hasValidFrom = board.IsValidNeighbor(cur, from);
                        resolved = board.TryGetPriorityForwardNeighbor(cur, hasValidFrom ? from : -1, out nextId, out isGlobalRed);
                    }

                    if (resolved)
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

                return EvaluateMidWalkFork(
                    board,
                    monster,
                    cur,
                    from,
                    activePlayers,
                    clearedMarkLandId: -1,
                    excludedTurnStartLandId: excludedTurnStartLand,
                    cache: cache,
                    isAfterCombat: monster.HasLaunchedFightThisTurn);
            }

            // 3. Normal tile (non-fork, degree <= 2):
            //    If already moving mid-turn into a dead-end (only neighbor is FromLandId), stop cleanly
            //    like DecideMidWalkStep even when FrontNodeIds contains FromLandId.
            var forward = board.GetForwardNeighbors(cur, from);
            if (forward.Count == 0 && monster.IsCurrentlyMovingMidTurn && board.IsValidNeighbor(cur, from))
            {
                return EmptyOutcome();
            }

            if (validFront.Count == 1)
            {
                return SingleTransition(validFront[0], isForkDecision: false, ForkReasonKind.CurrentFacing, string.Empty);
            }

            if (forward.Count == 1)
            {
                return SingleTransition(forward[0], isForkDecision: false, ForkReasonKind.MidForkChasePlayer, string.Empty);
            }

            if (validFront.Count > 1)
            {
                return UniformSplit(validFront, ForkReasonKind.UnknownFacingSplit, "朝向未知");
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
            MonsterState monster,
            int landId,
            int fromLandId,
            List<TrackedPlayer> activePlayers,
            bool combatHappenedOnThisTile,
            bool isAfterCombat,
            int clearedMarkLandId,
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

            // Ongoing taunt override at mid-walk forks before combat (consistent with DecideFirstStep)
            if (!isAfterCombat && !combatHappenedOnThisTile && monster != null &&
                monster.IsTaunted && monster.TauntTargetPlayerId != 0 &&
                TryDecideTauntStep(board, landId, monster.TauntTargetPlayerId, activePlayers, cache, out var midTauntOutcome))
            {
                return midTauntOutcome;
            }

            // Marked-target free-direction chase at mid-walk forks BEFORE combat:
            // 1047 铜盏鸳鸯 and 1018 学院安全官 may U-turn at a pre-combat mid-walk fork toward a still-marked player
            // (excluding players standing on the monster's turn-start tile). Once combat occurs, no more U-turning,
            // while turn-start exclusion remains consistent with DecideFirstStep across both pre- and post-combat forks.
            int excludedTurnStartLand = ResolveExcludedTurnStartLandId(monster, isMidTurn: true);
            if (!isAfterCombat && monster != null && (monster.ChasesMandarinDuckMark || monster.ChasesProblemStudent))
            {
                var marked = GetPriorityMarkedPlayers(monster, activePlayers, clearedMarkLandId, excludedTurnStartLand);
                if (marked.Count > 0 &&
                    TryDecideMarkedTargetTurnStartStep(
                        board,
                        landId,
                        excludedTurnStartLand,
                        marked,
                        cache,
                        monster.ChasesProblemStudent ? "追问题生" : "追标记",
                        out var markOutcome))
                {
                    return markOutcome;
                }
            }

            // Canonical guide rule: "之后再遇岔，看当时站位"
            return EvaluateMidWalkFork(board, monster, landId, fromLandId, activePlayers, clearedMarkLandId, excludedTurnStartLand, cache, isAfterCombat);
        }

        private static StepOutcome EvaluateMidWalkFork(
            BoardGraph board,
            MonsterState monster,
            int forkLandId,
            int incomingLandId,
            List<TrackedPlayer> activePlayers,
            int clearedMarkLandId,
            int excludedTurnStartLandId,
            Dictionary<int, ShortestPathField> cache,
            bool isAfterCombat)
        {
            var forward = board.GetForwardNeighbors(forkLandId, incomingLandId);
            if (forward.Count == 0) return EmptyOutcome();

            var exitProbs = new Dictionary<int, double>(forward.Count);
            for (int i = 0; i < forward.Count; i++) exitProbs[forward[i]] = 0.0;

            var allNeighbors = board.GetValidNeighbors(forkLandId);
            bool incomingIsNeighbor = board.IsValidNeighbor(forkLandId, incomingLandId);

            // Rank reachable trackable players by:
            //   1. Priority mark tier (0 = marked 【问题学生】/【鸳鸯】 for 1018/1047, 1 = normal)
            //   2. Full-board shortest distance ASC
            //   3. Seat Slot P1->P4 ASC
            var ranked = new List<(TrackedPlayer Player, int PriorityTier, int Dist, ShortestPathField Field)>(activePlayers.Count);
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
                int tier = IsPriorityMarked(monster, p, clearedMarkLandId, excludedTurnStartLandId) ? 0 : 1;
                ranked.Add((p, tier, d, field));
            }

            ranked.Sort((a, b) =>
            {
                int tc = a.PriorityTier.CompareTo(b.PriorityTier);
                if (tc != 0) return tc;
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

            // Library (82016) crime-only pursuers (1067 before reveal, 1068 before 5 dmg) only evaluate
            // their single top-ranked CrimeNum > 0 target (ranked[0]); if it lies strictly behind, no cascade occurs.
            bool restrictToTopTargetOnly = monster != null && monster.RequiresCrimeTarget;

            double remainingProb = 1.0;
            TrackedPlayer primaryTarget = null;
            int primaryDist = 0;
            bool usedMultiShortestPath = false;

            for (int i = 0; i < ranked.Count && remainingProb > 0.0; i++)
            {
                if (restrictToTopTargetOnly && i > 0) break;
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
            if (remainingProb > 0.0)
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
                if (p > 0.0) transitions.Add((fwd, p));
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
                    ? (isAfterCombat ? "续行·追" : "追") + primaryTarget.SeatTag + "·距" + primaryDist
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
            int clearedMarkLandId,
            StepOutcome outcome)
        {
            var key = new ForkRecordKey(forkLandId, incomingLandId, stepIndex, isAfterCombat, clearedMarkLandId);
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

            double newTotalArrival = pf.ArrivalProbability + arrivalProb;
            pf.ArrivalProbability = Math.Min(1.0, newTotalArrival);

            for (int i = 0; i < outcome.Transitions.Count; i++)
            {
                var tr = outcome.Transitions[i];
                double addedMass = arrivalProb * tr.Prob;
                ForkExitPrediction existing = null;
                for (int j = 0; j < pf.Exits.Count; j++)
                {
                    if (pf.Exits[j].NextLandId == tr.NextLandId)
                    {
                        existing = pf.Exits[j];
                        break;
                    }
                }
                if (existing == null)
                {
                    pf.Exits.Add(new ForkExitPrediction
                    {
                        NextLandId = tr.NextLandId,
                        CumulativeProbability = Math.Min(1.0, addedMass),
                        IsGlobalRedArrow = (tr.NextLandId == globalRed)
                    });
                }
                else
                {
                    existing.CumulativeProbability = Math.Min(1.0, existing.CumulativeProbability + addedMass);
                }
            }

            if (newTotalArrival > 0.0)
            {
                for (int i = 0; i < pf.Exits.Count; i++)
                {
                    pf.Exits[i].ConditionalProbability = Math.Min(1.0, pf.Exits[i].CumulativeProbability / newTotalArrival);
                }
                pf.Exits.Sort((a, b) => b.ConditionalProbability.CompareTo(a.ConditionalProbability));
            }

            if (outcome.Reason != pf.ReasonKind || !string.Equals(outcome.SummaryLabel, pf.SummaryLabel, StringComparison.Ordinal))
            {
                if (outcome.Reason != pf.ReasonKind)
                {
                    pf.ReasonKind = ForkReasonKind.Mixed;
                }
                string other = outcome.SummaryLabel ?? string.Empty;
                if (!string.IsNullOrEmpty(other) && !string.Equals(pf.SummaryLabel, other, StringComparison.Ordinal))
                {
                    if (string.IsNullOrEmpty(pf.SummaryLabel))
                    {
                        pf.SummaryLabel = other;
                    }
                    else
                    {
                        var parts = new List<string>();
                        if (pf.SummaryLabel.StartsWith("混合(", StringComparison.Ordinal) && pf.SummaryLabel.EndsWith(")", StringComparison.Ordinal))
                        {
                            string inner = pf.SummaryLabel.Substring(3, pf.SummaryLabel.Length - 4);
                            foreach (var token in inner.Split('/'))
                            {
                                if (!string.IsNullOrEmpty(token) && !parts.Contains(token)) parts.Add(token);
                            }
                        }
                        else
                        {
                            parts.Add(pf.SummaryLabel);
                        }
                        if (!parts.Contains(other)) parts.Add(other);
                        parts.Sort(StringComparer.Ordinal);
                        pf.SummaryLabel = "混合(" + string.Join("/", parts) + ")";
                    }
                }
            }
        }

        private static bool TryDecideTauntStep(
            BoardGraph board,
            int curLandId,
            long tauntTargetPlayerId,
            List<TrackedPlayer> activePlayers,
            Dictionary<int, ShortestPathField> cache,
            out StepOutcome outcome)
        {
            outcome = default;
            TrackedPlayer target = null;
            for (int i = 0; i < activePlayers.Count; i++)
            {
                if (activePlayers[i].PlayerId == tauntTargetPlayerId)
                {
                    target = activePlayers[i];
                    break;
                }
            }
            if (target == null || target.LandId == curLandId) return false;

            if (!cache.TryGetValue(target.LandId, out var field))
            {
                field = board.ComputeShortestPathsTo(target.LandId);
                cache[target.LandId] = field;
            }
            int dist = field.GetDistance(curLandId);
            if (dist == int.MaxValue || dist <= 0) return false;

            var all = board.GetValidNeighbors(curLandId);
            var bestExits = new List<(int NextLandId, double Lots)>();
            double totalLots = 0.0;
            for (int i = 0; i < all.Count; i++)
            {
                int nb = all[i];
                if (field.GetDistance(nb) != dist - 1) continue;
                double lots = field.GetPathCount(nb);
                if (lots <= 0.0) continue;
                bestExits.Add((nb, lots));
                totalLots += lots;
            }
            if (bestExits.Count == 0 || totalLots <= 0.0) return false;

            var transitions = new List<(int NextLandId, double Prob)>(bestExits.Count);
            for (int i = 0; i < bestExits.Count; i++)
                transitions.Add((bestExits[i].NextLandId, bestExits[i].Lots / totalLots));
            transitions.Sort((a, b) => b.Prob.CompareTo(a.Prob));

            outcome = new StepOutcome
            {
                Transitions = transitions,
                IsForkDecision = board.IsFork(curLandId),
                Reason = ForkReasonKind.TauntChase,
                SummaryLabel = "嘲讽·追" + target.SeatTag + "·距" + dist
            };
            return true;
        }

        private static List<TrackedPlayer> FilterStealablePlayers(List<TrackedPlayer> targetsHere)
        {
            var result = new List<TrackedPlayer>(targetsHere.Count);
            for (int i = 0; i < targetsHere.Count; i++)
            {
                if (targetsHere[i].Gold != 0) result.Add(targetsHere[i]);
            }
            return result;
        }

        private static void RecordCombat(
            Dictionary<int, PredictedCombat> combatMap,
            int landId,
            int stepIndex,
            double probability,
            List<TrackedPlayer> targets,
            bool isSteal)
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
                    Probability = Math.Min(1.0, probability),
                    TargetLabel = string.Join("/", seats),
                    IsSteal = isSteal
                };
                c.StepProbabilities[stepIndex] = Math.Min(1.0, probability);
                combatMap[landId] = c;
            }
            else
            {
                c.Probability = Math.Min(1.0, c.Probability + probability);
                c.StepProbabilities.TryGetValue(stepIndex, out double prevStepProb);
                c.StepProbabilities[stepIndex] = Math.Min(1.0, prevStepProb + probability);
                if (stepIndex < c.StepIndex) c.StepIndex = stepIndex;
            }
        }

        private static Dictionary<int, List<TrackedPlayer>> BuildPlayersByLand(
            List<TrackedPlayer> activePlayers,
            bool combatEligibleOnly)
        {
            var dict = new Dictionary<int, List<TrackedPlayer>>();
            for (int i = 0; i < activePlayers.Count; i++)
            {
                var p = activePlayers[i];
                if (combatEligibleOnly && !p.CanBeAttacked) continue;
                if (!dict.TryGetValue(p.LandId, out var list))
                {
                    list = new List<TrackedPlayer>();
                    dict[p.LandId] = list;
                }
                list.Add(p);
            }
            return dict;
        }

        private static bool TryDecideMarkedTargetTurnStartStep(
            BoardGraph board,
            int curLandId,
            int excludeLandId,
            List<TrackedPlayer> markedPlayers,
            Dictionary<int, ShortestPathField> cache,
            string labelPrefix,
            out StepOutcome outcome)
        {
            outcome = default;
            TrackedPlayer bestTarget = null;
            ShortestPathField bestField = null;
            int bestDist = int.MaxValue;

            for (int i = 0; i < markedPlayers.Count; i++)
            {
                var p = markedPlayers[i];
                if (p.LandId == curLandId) continue;
                if (excludeLandId >= 0 && p.LandId == excludeLandId) continue;
                if (!cache.TryGetValue(p.LandId, out var field))
                {
                    field = board.ComputeShortestPathsTo(p.LandId);
                    cache[p.LandId] = field;
                }
                int d = field.GetDistance(curLandId);
                if (d == int.MaxValue || d <= 0) continue;

                if (d < bestDist ||
                    (d == bestDist && bestTarget != null &&
                     (p.Slot < bestTarget.Slot || (p.Slot == bestTarget.Slot && p.PlayerId < bestTarget.PlayerId))))
                {
                    bestDist = d;
                    bestTarget = p;
                    bestField = field;
                }
            }

            if (bestTarget == null || bestField == null) return false;

            var all = board.GetValidNeighbors(curLandId);
            var bestExits = new List<(int NextLandId, double Lots)>();
            double totalLots = 0.0;
            for (int i = 0; i < all.Count; i++)
            {
                int nb = all[i];
                if (bestField.GetDistance(nb) != bestDist - 1) continue;
                double lots = bestField.GetPathCount(nb);
                if (lots <= 0.0) continue;
                bestExits.Add((nb, lots));
                totalLots += lots;
            }
            if (bestExits.Count == 0 || totalLots <= 0.0) return false;

            var transitions = new List<(int NextLandId, double Prob)>(bestExits.Count);
            for (int i = 0; i < bestExits.Count; i++)
                transitions.Add((bestExits[i].NextLandId, bestExits[i].Lots / totalLots));
            transitions.Sort((a, b) => b.Prob.CompareTo(a.Prob));

            outcome = new StepOutcome
            {
                Transitions = transitions,
                IsForkDecision = board.IsFork(curLandId),
                Reason = ForkReasonKind.MarkedTargetChase,
                SummaryLabel = labelPrefix + "·" + bestTarget.SeatTag + "·距" + bestDist
            };
            return true;
        }

        private static List<TrackedPlayer> FilterEffectivePlayers(
            BoardGraph board,
            MonsterState monster,
            IReadOnlyList<TrackedPlayer> players)
        {
            var trackable = FilterTrackablePlayers(board, players);
            if (monster == null || !monster.RequiresCrimeTarget) return trackable;

            var culprits = new List<TrackedPlayer>(trackable.Count);
            for (int i = 0; i < trackable.Count; i++)
            {
                if (trackable[i].CrimeNum > 0) culprits.Add(trackable[i]);
            }
            return culprits;
        }

        private static int ResolveExcludedTurnStartLandId(MonsterState monster, bool isMidTurn)
        {
            if (monster == null || !isMidTurn) return -1;
            if (monster.TurnStartLandId >= 0) return monster.TurnStartLandId;
            return monster.IsCurrentlyMovingMidTurn ? -1 : monster.CurrentLandId;
        }

        private static List<TrackedPlayer> GetPriorityMarkedPlayers(
            MonsterState monster,
            List<TrackedPlayer> activePlayers,
            int clearedMarkLandId,
            int excludedTurnStartLandId)
        {
            var marked = new List<TrackedPlayer>();
            if (monster == null || (!monster.ChasesProblemStudent && !monster.ChasesMandarinDuckMark))
                return marked;

            for (int i = 0; i < activePlayers.Count; i++)
            {
                var p = activePlayers[i];
                if (IsPriorityMarked(monster, p, clearedMarkLandId, excludedTurnStartLandId)) marked.Add(p);
            }
            return marked;
        }

        private static bool IsPriorityMarked(MonsterState monster, TrackedPlayer p, int clearedMarkLandId, int excludedTurnStartLandId)
        {
            if (monster == null || p == null) return false;
            if (monster.ChasesProblemStudent)
            {
                if (!p.HasProblemStudentMark) return false;
                if (clearedMarkLandId >= 0 && p.LandId == clearedMarkLandId) return false;
                if (excludedTurnStartLandId >= 0 && p.LandId == excludedTurnStartLandId) return false;
                return true;
            }
            if (monster.ChasesMandarinDuckMark)
            {
                if (!p.HasMandarinDuckMark) return false;
                if (clearedMarkLandId >= 0 && p.LandId == clearedMarkLandId) return false;
                if (excludedTurnStartLandId >= 0 && p.LandId == excludedTurnStartLandId) return false;
                return true;
            }
            return false;
        }

        private static bool AnyHasProblemStudentMark(List<TrackedPlayer> targets)
        {
            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i].HasProblemStudentMark) return true;
            }
            return false;
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

        private static void RecordTileStep(
            Dictionary<int, PredictedTileStep> map,
            HashSet<int> hasPreCombat,
            int landId,
            int step,
            double probability,
            bool isAfterCombat)
        {
            if (!map.TryGetValue(landId, out var ts))
            {
                ts = new PredictedTileStep
                {
                    LandId = landId,
                    StepIndex = step,
                    Probability = Math.Min(1.0, probability),
                    IsAfterCombat = isAfterCombat
                };
                ts.StepIndices.Add(step);
                ts.StepProbabilities[step] = Math.Min(1.0, probability);
                map[landId] = ts;
            }
            else
            {
                if (!ts.StepIndices.Contains(step))
                {
                    ts.StepIndices.Add(step);
                }
                if (step < ts.StepIndex) ts.StepIndex = step;
                ts.StepProbabilities.TryGetValue(step, out double prevStepProb);
                double nextStepProb = Math.Min(1.0, prevStepProb + probability);
                ts.StepProbabilities[step] = nextStepProb;
                if (nextStepProb > ts.Probability) ts.Probability = nextStepProb;
            }

            if (!isAfterCombat)
            {
                hasPreCombat.Add(landId);
                ts.IsAfterCombat = false;
            }
            else if (!hasPreCombat.Contains(landId))
            {
                ts.IsAfterCombat = true;
            }
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
