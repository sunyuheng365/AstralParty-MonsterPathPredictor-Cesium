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
        private readonly struct StepTransition
        {
            public readonly int NextLandId;
            public readonly double Prob;
            public readonly double LogProb;

            public StepTransition(int nextLandId, double prob, double logProb)
            {
                NextLandId = nextLandId;
                Prob = prob;
                LogProb = logProb;
            }
        }

        private struct StepOutcome
        {
            public List<StepTransition> Transitions;
            public bool IsForkDecision;
            public ForkReasonKind Reason;
            public string SummaryLabel;
            public bool IsExactUniformSplit;
        }

        private struct WalkStateKey : IEquatable<WalkStateKey>
        {
            public int LandId;
            public int FromLandId;
            public bool HasEncounteredPlayer;
            public int ClearedMarkLandId;
            public bool BossTargetCleared;

            public WalkStateKey(int landId, int fromLandId, bool hasEncounteredPlayer, int clearedMarkLandId = -1, bool bossTargetCleared = false)
            {
                LandId = landId;
                FromLandId = fromLandId;
                HasEncounteredPlayer = hasEncounteredPlayer;
                ClearedMarkLandId = clearedMarkLandId;
                BossTargetCleared = bossTargetCleared;
            }

            public bool Equals(WalkStateKey other) =>
                LandId == other.LandId &&
                FromLandId == other.FromLandId &&
                HasEncounteredPlayer == other.HasEncounteredPlayer &&
                ClearedMarkLandId == other.ClearedMarkLandId &&
                BossTargetCleared == other.BossTargetCleared;

            public override bool Equals(object obj) => obj is WalkStateKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = LandId;
                    h = (h * 397) ^ FromLandId;
                    h = (h * 397) ^ (HasEncounteredPlayer ? 1 : 0);
                    h = (h * 397) ^ ClearedMarkLandId;
                    h = (h * 397) ^ (BossTargetCleared ? 1 : 0);
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
            public bool BossTargetCleared;

            public ForkRecordKey(int landId, int fromLandId, int stepIndex, bool afterCombat, int clearedMarkLandId, bool bossTargetCleared)
            {
                LandId = landId;
                FromLandId = fromLandId;
                StepIndex = stepIndex;
                AfterCombat = afterCombat;
                ClearedMarkLandId = clearedMarkLandId;
                BossTargetCleared = bossTargetCleared;
            }

            public bool Equals(ForkRecordKey other) =>
                LandId == other.LandId &&
                FromLandId == other.FromLandId &&
                StepIndex == other.StepIndex &&
                AfterCombat == other.AfterCombat &&
                ClearedMarkLandId == other.ClearedMarkLandId &&
                BossTargetCleared == other.BossTargetCleared;

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
                    h = (h * 397) ^ (BossTargetCleared ? 1 : 0);
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

            var trackablePlayers = FilterTrackablePlayers(board, players);
            var activePlayers = FilterEffectivePlayers(monster, trackablePlayers);
            var shortestPathCache = new Dictionary<int, ShortestPathField>();
            var combatPlayersByLand = BuildPlayersByLand(activePlayers, combatEligibleOnly: true);
            if (monster.IsNormalAggressive && !monster.IsThief && monster.IsTaunted && monster.TauntTargetPlayerId != 0 && monster.RequiresCrimeTarget)
            {
                AddTauntTargetToCombatMapIfEligible(combatPlayersByLand, trackablePlayers, monster.TauntTargetPlayerId);
            }
            var stealPlayersByLand = BuildPlayersByLand(activePlayers, combatEligibleOnly: false);

            var edgeMap = new Dictionary<EdgeKey, PredictedEdge>();
            var edgeHasPreCombat = new HashSet<EdgeKey>();
            var forkMap = new Dictionary<ForkRecordKey, PredictedFork>();
            var combatMap = new Dictionary<int, PredictedCombat>();
            var endpointMap = new Dictionary<(int LandId, int Step, bool DeadEnd, bool StoppedAtCombat, bool AfterCombat), double>();
            var tileStepMap = new Dictionary<int, PredictedTileStep>();
            var tileStepHasPreCombat = new HashSet<int>();

            bool initialCombatState = monster.HasLaunchedFightThisTurn || monster.JustFoughtOnCurrentFork;
            long turnBossTargetId = ResolveTurnBossTargetId(board, monster, activePlayers, shortestPathCache, out bool freshMarkedAtTurnStart);
            bool initialBossTargetCleared = monster.BossTargetClearedThisTurn;
            RecordTileStep(tileStepMap, tileStepHasPreCombat, monster.CurrentLandId, step: 0, probability: 1.0, isAfterCombat: initialCombatState);

            var currentLayer = new Dictionary<WalkStateKey, (double Prob, double LogProb)>
            {
                [new WalkStateKey(monster.CurrentLandId, monster.FromLandId, initialCombatState, -1, initialBossTargetCleared)] = (1.0, 0.0)
            };

            for (int step = 0; step < maxSteps && currentLayer.Count > 0; step++)
            {
                var nextLayer = new Dictionary<WalkStateKey, (double Prob, double LogProb)>();

                foreach (var entry in currentLayer)
                {
                    var state = entry.Key;
                    double arrivalProb = entry.Value.Prob;
                    double arrivalLogProb = entry.Value.LogProb;
                    if (double.IsNegativeInfinity(arrivalLogProb)) continue;
                    if (step > 0 && minProb > 0.0 && arrivalProb < minProb)
                    {
                        plan.PrunedProbability += arrivalProb;
                        continue;
                    }

                    bool postCombatNow = state.HasEncounteredPlayer;
                    int clearedMarkLandId = state.ClearedMarkLandId;
                    bool bossTargetClearedNow = state.BossTargetCleared;
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
                            if ((monster.ChasesProblemStudent || monster.ChasesMandarinDuckMark) &&
                                AnyHasPriorityMark(monster, targetsHere))
                            {
                                bossTargetClearedNow = true;
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

                                if (options.PostCombatMode == PostCombatMode.StopAtFirstCombat)
                                {
                                    AccumulateEndpoint(endpointMap, state.LandId, step, false, true, true, arrivalProb);
                                    continue;
                                }
                            }
                        }
                    }

                    StepOutcome outcome = step == 0
                        ? DecideFirstStep(board, monster, players, trackablePlayers, activePlayers, turnBossTargetId, freshMarkedAtTurnStart, bossTargetClearedNow, shortestPathCache, options.PostCombatMode)
                        : DecideMidWalkStep(
                            board,
                            monster,
                            state.LandId,
                            state.FromLandId,
                            players,
                            trackablePlayers,
                            activePlayers,
                            encounteredOnThisTile,
                            postCombatNow,
                            clearedMarkLandId,
                            turnBossTargetId,
                            bossTargetClearedNow,
                            shortestPathCache,
                            options);

                    if (outcome.Transitions == null || outcome.Transitions.Count == 0)
                    {
                        AccumulateEndpoint(endpointMap, state.LandId, step, true, false, postCombatNow, arrivalProb);
                        continue;
                    }

                    if (outcome.IsForkDecision)
                    {
                        RecordFork(board, forkMap, state.LandId, state.FromLandId, step, arrivalProb, arrivalLogProb, postCombatNow, clearedMarkLandId, bossTargetClearedNow, outcome);
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
                        var tr = outcome.Transitions[i];
                        int nextId = tr.NextLandId;
                        double branchLogProb = arrivalLogProb + tr.LogProb;
                        if (double.IsNegativeInfinity(branchLogProb)) continue;
                        double branchProb = arrivalProb * tr.Prob;

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

                        var nextKey = new WalkStateKey(nextId, state.LandId, postCombatNow, clearedMarkLandId, bossTargetClearedNow);
                        if (nextLayer.TryGetValue(nextKey, out var existing))
                            nextLayer[nextKey] = (existing.Prob + branchProb, BoardGraph.LogAddExp(existing.LogProb, branchLogProb));
                        else
                            nextLayer[nextKey] = (branchProb, branchLogProb);
                    }
                }

                currentLayer = nextLayer;
            }

            // Also record combat if the final step lands directly on a living player (when maxSteps > 0),
            // and detect final-step dead-ends consistently regardless of whether MaxSteps hit exact length.
            foreach (var finalState in currentLayer)
            {
                if (double.IsNegativeInfinity(finalState.Value.LogProb)) continue;
                double finalProb = finalState.Value.Prob;
                bool stoppedCombat = false;
                bool afterCombat = finalState.Key.HasEncounteredPlayer;
                if (maxSteps > 0 && !afterCombat)
                {
                    if (monster.IsNormalAggressive && combatPlayersByLand.TryGetValue(finalState.Key.LandId, out var endTargets))
                    {
                        RecordCombat(combatMap, finalState.Key.LandId, maxSteps, finalProb, endTargets, isSteal: false);
                        afterCombat = true;
                        stoppedCombat = (options.PostCombatMode == PostCombatMode.StopAtFirstCombat);
                    }
                    else if (monster.IsThief && stealPlayersByLand.TryGetValue(finalState.Key.LandId, out var endStealCandidates))
                    {
                        var stealTargets = FilterStealablePlayers(endStealCandidates);
                        if (stealTargets.Count > 0)
                        {
                            RecordCombat(combatMap, finalState.Key.LandId, maxSteps, finalProb, stealTargets, isSteal: true);
                            afterCombat = true;
                            stoppedCombat = (options.PostCombatMode == PostCombatMode.StopAtFirstCombat);
                        }
                    }
                }

                bool isDeadEnd = false;
                if (!stoppedCombat && maxSteps > 0)
                {
                    isDeadEnd = board.GetForwardNeighbors(finalState.Key.LandId, finalState.Key.FromLandId).Count == 0;
                }

                AccumulateEndpoint(endpointMap, finalState.Key.LandId, maxSteps, isDeadEnd, stoppedCombat, afterCombat, finalProb);
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

        /// <summary>
        /// Context-aware fork check for a monster's first step, shared by the planner and ReplayCertifier.
        /// Treats Lingling skill pushes with a verified incoming predecessor (<see cref="MonsterState.SkillMovePushFromLandId"/>)
        /// as arriving along that directed edge so 2-exit directed splits deterministically follow Red &gt; Blue.
        /// </summary>
        public static bool IsForkForFirstStep(BoardGraph board, MonsterState monster)
        {
            if (board == null || monster == null) return false;
            int cur = monster.CurrentLandId;
            bool hasLinglingPushFrom = monster.SkillMoveKind == SkillMoveKind.LinglingPush &&
                                       monster.SkillMovePushFromLandId >= 0;
            int contextFrom = hasLinglingPushFrom ? monster.SkillMovePushFromLandId : monster.FromLandId;
            bool arrivedFromEdge = monster.IsCurrentlyMovingMidTurn || monster.JustFoughtOnCurrentFork || hasLinglingPushFrom;
            return board.IsForkForContext(cur, contextFrom, arrivedFromEdge);
        }

        private static StepOutcome DecideFirstStep(
            BoardGraph board,
            MonsterState monster,
            IReadOnlyList<TrackedPlayer> allPlayers,
            List<TrackedPlayer> trackablePlayers,
            List<TrackedPlayer> activePlayers,
            long lockedBossTargetId,
            bool freshMarkedAtTurnStart,
            bool bossTargetCleared,
            Dictionary<int, ShortestPathField> cache,
            PostCombatMode postCombatMode)
        {
            int cur = monster.CurrentLandId;
            int from = monster.FromLandId;
            var forward = board.GetForwardNeighbors(cur, from);
            bool isFork = IsForkForFirstStep(board, monster);
            int excludedTurnStartLand = ResolveExcludedTurnStartLandId(monster, isMidTurn: monster.IsCurrentlyMovingMidTurn);

            // 0a. Turn-start fresh marked-target lock-on (1018 学院安全官 / 1047 铜盏鸳鸯):
            //     When starting a turn with a living marked player (2101001 / 10470101), the server's
            //     turn-start lock-on overrides facing, ForkStart Red>Blue, AND Taunt (10011) on Step 0
            //     (verified on 1792397536442877 F3569 and 1795260241852966 F3969/F4024).
            if (!monster.IsCurrentlyMovingMidTurn &&
                freshMarkedAtTurnStart &&
                !monster.JustFoughtOnCurrentFork &&
                !bossTargetCleared &&
                !(monster.HasLaunchedFightThisTurn && postCombatMode == PostCombatMode.RedBlueAfterCombat) &&
                monster.SkillMoveKind == SkillMoveKind.None &&
                (monster.ChasesProblemStudent || monster.ChasesMandarinDuckMark) &&
                lockedBossTargetId != 0 &&
                TryDecideLockedBossTargetStep(
                    board,
                    cur,
                    isFork,
                    lockedBossTargetId,
                    allPlayers,
                    cache,
                    monster.ChasesProblemStudent ? "追问题生" : "追标记",
                    out var turnStartBossOutcome))
            {
                return turnStartBossOutcome;
            }

            // 0b. Taunt override (嘲讽 10011):
            //     - At turn start (!IsCurrentlyMovingMidTurn): ALL monsters (including Thieves, verified on
            //       1794740333298128 F1237 & 12 other Thief turn-start Taunt replays) obey Taunt.
            //     - At mid-walk forks (IsCurrentlyMovingMidTurn && isFork): Thieves (MonsterType == Thief)
            //       ignore Taunt and chase the nearest player normally (verified on 1794740333298128 F1244
            //       & 24 other Thief mid-walk Taunt replays), whereas aggressive monsters obey Taunt
            //       and Taunt takes precedence over retained BossTargetId (1795260241852966 F3972 & F4030).
            //     - Uses unfiltered trackablePlayers (NOT filtered by RequiresCrimeTarget) so 1067/1068
            //       still chase a CrimeNum == 0 taunter (1789211303827607 / 1789455248221734).
            if ((!monster.IsCurrentlyMovingMidTurn || (isFork && !monster.IsThief)) &&
                !monster.JustFoughtOnCurrentFork && !monster.HasLaunchedFightThisTurn &&
                monster.IsTaunted && monster.TauntTargetPlayerId != 0 &&
                TryDecideTauntStep(board, cur, isFork, monster.TauntTargetPlayerId, trackablePlayers, cache, out var tauntOutcome))
            {
                return tauntOutcome;
            }

            // 0c. Mid-walk fork BossTargetId free-direction chase (1018 / 1047):
            //     Freely U-turns toward lockedBossTargetId (including PostNormalCombatMidFork after fighting
            //     an unmarked player) until fighting a marked target clears BossTargetId.
            if (monster.IsCurrentlyMovingMidTurn && isFork &&
                !monster.JustFoughtOnCurrentFork &&
                !bossTargetCleared &&
                !(monster.HasLaunchedFightThisTurn && postCombatMode == PostCombatMode.RedBlueAfterCombat) &&
                monster.SkillMoveKind == SkillMoveKind.None &&
                (monster.ChasesProblemStudent || monster.ChasesMandarinDuckMark) &&
                lockedBossTargetId != 0 &&
                TryDecideLockedBossTargetStep(
                    board,
                    cur,
                    isFork,
                    lockedBossTargetId,
                    allPlayers,
                    cache,
                    monster.ChasesProblemStudent ? "追问题生" : "追标记",
                    out var bossOutcome))
            {
                return bossOutcome;
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
                    bossTargetCleared: bossTargetCleared,
                    cache: cache,
                    isAfterCombat: monster.HasLaunchedFightThisTurn);
            }

            // 3. Normal tile (non-fork, at most 1 forward neighbor when moving mid-turn):
            //    When already moving mid-turn from a known incoming edge, honor the unique forward neighbor
            //    (or dead-end stop) BEFORE consulting FrontNodeIds so a stale Front=[from] cannot trigger a U-turn.
            if (monster.IsCurrentlyMovingMidTurn && from >= 0 &&
                (board.IsValidNeighbor(cur, from) || board.IsValidNeighbor(from, cur)))
            {
                if (forward.Count == 0) return EmptyOutcome();
                if (forward.Count == 1)
                    return SingleTransition(forward[0], isForkDecision: false, ForkReasonKind.MidForkChasePlayer, string.Empty);
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
                        var frontOnFork = FilterValidNeighbors(board, cur, monster.FrontNodeIds);
                        bool resolved;
                        int nextId;
                        bool isGlobalRed;
                        if (frontOnFork.Count > 0)
                        {
                            resolved = board.TryGetPriorityAmongCandidates(cur, frontOnFork, out nextId, out isGlobalRed);
                        }
                        else
                        {
                            int exclude = board.IsValidNeighbor(cur, monster.SkillMovePushFromLandId)
                                ? monster.SkillMovePushFromLandId
                                : -1;
                            resolved = board.TryGetPriorityForwardNeighbor(cur, exclude, out nextId, out isGlobalRed);
                        }
                        if (resolved)
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
            IReadOnlyList<TrackedPlayer> allPlayers,
            List<TrackedPlayer> trackablePlayers,
            List<TrackedPlayer> activePlayers,
            bool combatHappenedOnThisTile,
            bool isAfterCombat,
            int clearedMarkLandId,
            long lockedBossTargetId,
            bool bossTargetCleared,
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

            // Ongoing taunt override at mid-walk forks before combat (consistent with DecideFirstStep;
            // Thieves ignore Taunt; crime-restricted monsters still chase a CrimeNum == 0 taunter).
            if (!isAfterCombat && !combatHappenedOnThisTile && monster != null &&
                !monster.IsThief && monster.IsTaunted && monster.TauntTargetPlayerId != 0 &&
                TryDecideTauntStep(board, landId, isForkDecision: true, monster.TauntTargetPlayerId, trackablePlayers, cache, out var midTauntOutcome))
            {
                return midTauntOutcome;
            }

            // BossTargetId / Marked-target free-direction chase at mid-walk forks:
            // 1047 铜盏鸳鸯 and 1018 学院安全官 freely U-turn toward their locked BossTargetId
            // (even after fighting an unmarked player on a normal tile, or if mark expired / target died),
            // until they engage the locked/marked target itself (bossTargetCleared == true).
            int excludedTurnStartLand = ResolveExcludedTurnStartLandId(monster, isMidTurn: true);
            if (!combatHappenedOnThisTile && !bossTargetCleared &&
                monster != null && (monster.ChasesMandarinDuckMark || monster.ChasesProblemStudent) &&
                lockedBossTargetId != 0 &&
                TryDecideLockedBossTargetStep(
                    board,
                    landId,
                    isForkDecision: true,
                    lockedBossTargetId,
                    allPlayers,
                    cache,
                    monster.ChasesProblemStudent ? "追问题生" : "追标记",
                    out var bossOutcome))
            {
                return bossOutcome;
            }

            // Canonical guide rule: "之后再遇岔，看当时站位"
            return EvaluateMidWalkFork(board, monster, landId, fromLandId, activePlayers, clearedMarkLandId, excludedTurnStartLand, bossTargetCleared, cache, isAfterCombat);
        }

        private static StepOutcome EvaluateMidWalkFork(
            BoardGraph board,
            MonsterState monster,
            int forkLandId,
            int incomingLandId,
            List<TrackedPlayer> activePlayers,
            int clearedMarkLandId,
            int excludedTurnStartLandId,
            bool bossTargetCleared,
            Dictionary<int, ShortestPathField> cache,
            bool isAfterCombat)
        {
            var forward = board.GetForwardNeighbors(forkLandId, incomingLandId);
            if (forward.Count == 0) return EmptyOutcome();

            var exitLogProbs = new Dictionary<int, double>(forward.Count);
            for (int i = 0; i < forward.Count; i++) exitLogProbs[forward[i]] = double.NegativeInfinity;

            var allNeighbors = board.GetValidNeighbors(forkLandId);
            bool incomingIsNeighbor = board.IsValidNeighbor(forkLandId, incomingLandId);

            // Library (82016) crime-only pursuers (1067 before reveal, 1068 before 5 dmg) only evaluate
            // their single top-ranked CrimeNum > 0 target (ranked[0]); if it lies strictly behind or stands
            // directly on forkLandId (dist == 0, e.g. stealth/unattackable or post-combat), no cascade occurs.
            bool restrictToTopTargetOnly = monster != null && monster.RequiresCrimeTarget;
            if (restrictToTopTargetOnly)
            {
                for (int i = 0; i < activePlayers.Count; i++)
                {
                    if (activePlayers[i].LandId == forkLandId)
                    {
                        return UniformSplit(forward, ForkReasonKind.MidForkAllUTurnFallback, isAfterCombat ? "续行·平分" : "同格平分");
                    }
                }
            }

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
                int tier = !bossTargetCleared && IsPriorityMarked(monster, p, clearedMarkLandId, excludedTurnStartLandId) ? 0 : 1;
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

            double logRemainingProb = 0.0; // ln(1.0) = 0; stays finite even when Exp underflows < 4.9e-324
            bool exactOk = true;
            long remNum = 1, remDen = 1;
            var exactNums = new long[forward.Count];
            var exactDens = new long[forward.Count];
            for (int i = 0; i < forward.Count; i++) exactDens[i] = 1;

            TrackedPlayer primaryTarget = null;
            int primaryDist = 0;
            bool usedMultiShortestPath = false;

            for (int i = 0; i < ranked.Count && !double.IsNegativeInfinity(logRemainingProb); i++)
            {
                if (restrictToTopTargetOnly && i > 0) break;
                var item = ranked[i];
                int targetDist = item.Dist;
                var field = item.Field;

                double logTotalLots = double.NegativeInfinity;
                double logUTurnLots = double.NegativeInfinity;
                long exactTotalLots = 0;
                long exactUTurnLots = 0;
                int forwardLotExits = 0;

                for (int j = 0; j < allNeighbors.Count; j++)
                {
                    int nb = allNeighbors[j];
                    if (field.GetDistance(nb) != targetDist - 1) continue;
                    double logC = field.GetLogPathCount(nb);
                    if (double.IsNegativeInfinity(logC)) continue;

                    logTotalLots = BoardGraph.LogAddExp(logTotalLots, logC);
                    if (incomingIsNeighbor && nb == incomingLandId)
                        logUTurnLots = BoardGraph.LogAddExp(logUTurnLots, logC);
                    else
                        forwardLotExits++;

                    if (exactOk)
                    {
                        if (!field.TryGetExactPathCount(nb, out long cL) || cL <= 0 || exactTotalLots > long.MaxValue - cL)
                        {
                            exactOk = false;
                        }
                        else
                        {
                            exactTotalLots += cL;
                            if (incomingIsNeighbor && nb == incomingLandId)
                            {
                                exactUTurnLots += cL;
                            }
                        }
                    }
                }

                if (double.IsNegativeInfinity(logTotalLots)) continue;

                if (forwardLotExits > 0)
                {
                    if (primaryTarget == null)
                    {
                        primaryTarget = item.Player;
                        primaryDist = targetDist;
                    }
                    if (forwardLotExits > 1 || !double.IsNegativeInfinity(logUTurnLots))
                    {
                        usedMultiShortestPath = true;
                    }

                    for (int j = 0; j < forward.Count; j++)
                    {
                        int fwd = forward[j];
                        if (field.GetDistance(fwd) != targetDist - 1) continue;
                        double logC = field.GetLogPathCount(fwd);
                        if (double.IsNegativeInfinity(logC)) continue;
                        double logContrib = logRemainingProb + (logC - logTotalLots);
                        exitLogProbs[fwd] = BoardGraph.LogAddExp(exitLogProbs[fwd], logContrib);

                        if (exactOk)
                        {
                            if (!field.TryGetExactPathCount(fwd, out long cL) || cL <= 0 ||
                                !TryMulRational(remNum, remDen, cL, exactTotalLots, out long addN, out long addD) ||
                                !TryAddRational(exactNums[j], exactDens[j], addN, addD, out exactNums[j], out exactDens[j]))
                            {
                                exactOk = false;
                            }
                        }
                    }
                }

                logRemainingProb = double.IsNegativeInfinity(logUTurnLots)
                    ? double.NegativeInfinity
                    : logRemainingProb + (logUTurnLots - logTotalLots);

                if (exactOk)
                {
                    if (exactUTurnLots == 0)
                    {
                        remNum = 0;
                        remDen = 1;
                    }
                    else if (!TryMulRational(remNum, remDen, exactUTurnLots, exactTotalLots, out remNum, out remDen))
                    {
                        exactOk = false;
                    }
                }
            }

            bool usedUTurnFallback = false;
            if (!double.IsNegativeInfinity(logRemainingProb))
            {
                usedUTurnFallback = true;
                double logShare = logRemainingProb - Math.Log(forward.Count);
                for (int i = 0; i < forward.Count; i++)
                {
                    int fwd = forward[i];
                    exitLogProbs[fwd] = BoardGraph.LogAddExp(exitLogProbs[fwd], logShare);
                    if (exactOk && remNum > 0)
                    {
                        if (!TryMulRational(remNum, remDen, 1, forward.Count, out long shareN, out long shareD) ||
                            !TryAddRational(exactNums[i], exactDens[i], shareN, shareD, out exactNums[i], out exactDens[i]))
                        {
                            exactOk = false;
                        }
                    }
                }
            }

            var transitions = new List<StepTransition>(forward.Count);
            for (int i = 0; i < forward.Count; i++)
            {
                int fwd = forward[i];
                double logP = exitLogProbs[fwd];
                if (!double.IsNegativeInfinity(logP))
                {
                    double prob = exactOk && exactNums[i] > 0 && exactDens[i] > 0
                        ? Math.Min(1.0, (double)exactNums[i] / exactDens[i])
                        : Math.Min(1.0, Math.Exp(logP));
                    transitions.Add(new StepTransition(fwd, prob, logP));
                }
            }

            bool isExactUniform = false;
            if (transitions.Count >= 2)
            {
                if (primaryTarget == null && usedUTurnFallback)
                {
                    isExactUniform = true;
                }
                else if (exactOk)
                {
                    int exactPosCount = 0;
                    bool allExactEqual = true;
                    for (int i = 0; i < forward.Count; i++)
                    {
                        if (exactNums[i] <= 0) continue;
                        exactPosCount++;
                        if (exactNums[i] != 1 || exactDens[i] != transitions.Count)
                        {
                            allExactEqual = false;
                            break;
                        }
                    }
                    isExactUniform = allExactEqual && exactPosCount == transitions.Count;
                }
            }

            transitions.Sort((a, b) => b.LogProb.CompareTo(a.LogProb));

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
                SummaryLabel = label,
                IsExactUniformSplit = isExactUniform
            };
        }

        private static void RecordFork(
            BoardGraph board,
            Dictionary<ForkRecordKey, PredictedFork> forkMap,
            int forkLandId,
            int incomingLandId,
            int stepIndex,
            double arrivalProb,
            double arrivalLogProb,
            bool isAfterCombat,
            int clearedMarkLandId,
            bool bossTargetCleared,
            StepOutcome outcome)
        {
            var key = new ForkRecordKey(forkLandId, incomingLandId, stepIndex, isAfterCombat, clearedMarkLandId, bossTargetCleared);
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
                    SummaryLabel = outcome.SummaryLabel,
                    IsExactUniformSplit = outcome.IsExactUniformSplit
                };

                for (int i = 0; i < outcome.Transitions.Count; i++)
                {
                    var tr = outcome.Transitions[i];
                    pf.Exits.Add(new ForkExitPrediction
                    {
                        NextLandId = tr.NextLandId,
                        ConditionalProbability = tr.Prob,
                        LogConditionalProbability = tr.LogProb,
                        CumulativeProbability = Math.Min(1.0, arrivalProb * tr.Prob),
                        IsGlobalRedArrow = (tr.NextLandId == globalRed)
                    });
                }

                UpdateExitPositiveFlags(pf.Exits);
                forkMap[key] = pf;
                return;
            }

            double oldArrival = pf.ArrivalProbability;
            double newTotalArrival = oldArrival + arrivalProb;
            pf.ArrivalProbability = Math.Min(1.0, newTotalArrival);
            double oldLogArrival = oldArrival > 0.0 ? Math.Log(oldArrival) : double.NegativeInfinity;
            double newLogArrival = arrivalLogProb;
            double totalLogArrival = BoardGraph.LogAddExp(oldLogArrival, newLogArrival);

            var updatedExitIds = new HashSet<int>();
            for (int i = 0; i < outcome.Transitions.Count; i++)
            {
                var tr = outcome.Transitions[i];
                updatedExitIds.Add(tr.NextLandId);
                double addedMass = arrivalProb * tr.Prob;
                double addedLogMass = newLogArrival + tr.LogProb;
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
                        LogConditionalProbability = double.IsNegativeInfinity(totalLogArrival) ? tr.LogProb : addedLogMass - totalLogArrival,
                        CumulativeProbability = Math.Min(1.0, addedMass),
                        IsGlobalRedArrow = (tr.NextLandId == globalRed)
                    });
                }
                else
                {
                    existing.CumulativeProbability = Math.Min(1.0, existing.CumulativeProbability + addedMass);
                    if (!double.IsNegativeInfinity(totalLogArrival))
                    {
                        double prevLogMass = oldLogArrival + existing.LogConditionalProbability;
                        existing.LogConditionalProbability = BoardGraph.LogAddExp(prevLogMass, addedLogMass) - totalLogArrival;
                    }
                }
            }

            if (!double.IsNegativeInfinity(totalLogArrival))
            {
                for (int j = 0; j < pf.Exits.Count; j++)
                {
                    if (!updatedExitIds.Contains(pf.Exits[j].NextLandId))
                    {
                        pf.Exits[j].LogConditionalProbability = (oldLogArrival + pf.Exits[j].LogConditionalProbability) - totalLogArrival;
                    }
                }
            }

            if (newTotalArrival > 0.0)
            {
                for (int i = 0; i < pf.Exits.Count; i++)
                {
                    pf.Exits[i].ConditionalProbability = Math.Min(1.0, pf.Exits[i].CumulativeProbability / newTotalArrival);
                }
                pf.Exits.Sort((a, b) =>
                {
                    int lc = b.LogConditionalProbability.CompareTo(a.LogConditionalProbability);
                    return lc != 0 ? lc : b.ConditionalProbability.CompareTo(a.ConditionalProbability);
                });
            }

            pf.IsExactUniformSplit = pf.IsExactUniformSplit &&
                                     outcome.IsExactUniformSplit &&
                                     updatedExitIds.Count == pf.Exits.Count &&
                                     outcome.Transitions.Count == pf.Exits.Count;
            UpdateExitPositiveFlags(pf.Exits);

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
            bool isForkDecision,
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
            var bestExits = new List<(int NextLandId, double LogC, long ExactC)>();
            double logTotal = double.NegativeInfinity;
            bool exactAllEqual = true;
            long firstExactC = -1;
            for (int i = 0; i < all.Count; i++)
            {
                int nb = all[i];
                if (field.GetDistance(nb) != dist - 1) continue;
                double logC = field.GetLogPathCount(nb);
                if (double.IsNegativeInfinity(logC)) continue;
                long exactC = -1;
                if (field.TryGetExactPathCount(nb, out long cL) && cL > 0)
                {
                    exactC = cL;
                    if (firstExactC < 0) firstExactC = cL;
                    else if (cL != firstExactC) exactAllEqual = false;
                }
                else
                {
                    exactAllEqual = false;
                }
                bestExits.Add((nb, logC, exactC));
                logTotal = BoardGraph.LogAddExp(logTotal, logC);
            }
            if (bestExits.Count == 0 || double.IsNegativeInfinity(logTotal)) return false;

            bool isExactUniform = bestExits.Count >= 2 && exactAllEqual && firstExactC > 0;
            var transitions = new List<StepTransition>(bestExits.Count);
            for (int i = 0; i < bestExits.Count; i++)
            {
                double logP = bestExits[i].LogC - logTotal;
                double prob = isExactUniform ? (1.0 / bestExits.Count) : Math.Min(1.0, Math.Exp(logP));
                transitions.Add(new StepTransition(bestExits[i].NextLandId, prob, logP));
            }
            transitions.Sort((a, b) => b.LogProb.CompareTo(a.LogProb));

            outcome = new StepOutcome
            {
                Transitions = transitions,
                IsForkDecision = isForkDecision,
                Reason = ForkReasonKind.TauntChase,
                SummaryLabel = "嘲讽·追" + target.SeatTag + "·距" + dist,
                IsExactUniformSplit = isExactUniform
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

        private static void AddTauntTargetToCombatMapIfEligible(
            Dictionary<int, List<TrackedPlayer>> combatPlayersByLand,
            List<TrackedPlayer> trackablePlayers,
            long tauntTargetPlayerId)
        {
            for (int i = 0; i < trackablePlayers.Count; i++)
            {
                var p = trackablePlayers[i];
                if (p.PlayerId != tauntTargetPlayerId || !p.CanBeAttacked) continue;
                if (!combatPlayersByLand.TryGetValue(p.LandId, out var list))
                {
                    list = new List<TrackedPlayer>();
                    combatPlayersByLand[p.LandId] = list;
                }
                bool exists = false;
                for (int j = 0; j < list.Count; j++)
                {
                    if (list[j].PlayerId == p.PlayerId) { exists = true; break; }
                }
                if (!exists) list.Add(p);
                break;
            }
        }

        private static List<TrackedPlayer> FilterEffectivePlayers(
            MonsterState monster,
            List<TrackedPlayer> trackable)
        {
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

        private static long ResolveTurnBossTargetId(
            BoardGraph board,
            MonsterState monster,
            List<TrackedPlayer> activePlayers,
            Dictionary<int, ShortestPathField> cache,
            out bool freshMarkedAtTurnStart)
        {
            freshMarkedAtTurnStart = false;
            if (monster == null || monster.BossTargetClearedThisTurn) return 0;
            if (!monster.ChasesProblemStudent && !monster.ChasesMandarinDuckMark) return 0;

            // Mid-turn: once the server has locked BossTargetId, honor it directly until cleared by fighting it.
            if (monster.IsCurrentlyMovingMidTurn && monster.BossTargetPlayerId != 0)
            {
                return monster.BossTargetPlayerId;
            }

            // Turn start (or mid-turn without an explicit server BossTargetId): pick nearest living marked player
            // outside the monster's turn-start tile; if none exists, retain any previously locked BossTargetPlayerId
            // (which persists across turns for mid-walk forks even after mark expiration or target death,
            // while NOT overriding turn-start Front/ForkStart Red>Blue).
            int excludedTurnStartLand = ResolveExcludedTurnStartLandId(monster, isMidTurn: monster.IsCurrentlyMovingMidTurn);
            var marked = GetPriorityMarkedPlayers(monster, activePlayers, clearedMarkLandId: -1, excludedTurnStartLandId: excludedTurnStartLand);
            if (marked.Count > 0)
            {
                TrackedPlayer bestTarget = null;
                int bestDist = int.MaxValue;
                int cur = monster.CurrentLandId;
                for (int i = 0; i < marked.Count; i++)
                {
                    var p = marked[i];
                    if (p.LandId == cur) continue;
                    if (excludedTurnStartLand >= 0 && p.LandId == excludedTurnStartLand) continue;
                    if (!cache.TryGetValue(p.LandId, out var field))
                    {
                        field = board.ComputeShortestPathsTo(p.LandId);
                        cache[p.LandId] = field;
                    }
                    int d = field.GetDistance(cur);
                    if (d == int.MaxValue || d <= 0) continue;
                    if (d < bestDist ||
                        (d == bestDist && bestTarget != null &&
                         (p.Slot < bestTarget.Slot || (p.Slot == bestTarget.Slot && p.PlayerId < bestTarget.PlayerId))))
                    {
                        bestDist = d;
                        bestTarget = p;
                    }
                }
                if (bestTarget != null)
                {
                    freshMarkedAtTurnStart = !monster.IsCurrentlyMovingMidTurn;
                    return bestTarget.PlayerId;
                }
            }

            return monster.BossTargetPlayerId;
        }

        private static bool TryDecideLockedBossTargetStep(
            BoardGraph board,
            int curLandId,
            bool isForkDecision,
            long bossTargetPlayerId,
            IReadOnlyList<TrackedPlayer> allPlayers,
            Dictionary<int, ShortestPathField> cache,
            string labelPrefix,
            out StepOutcome outcome)
        {
            outcome = default;
            if (bossTargetPlayerId == 0 || allPlayers == null) return false;
            TrackedPlayer target = null;
            for (int i = 0; i < allPlayers.Count; i++)
            {
                var p = allPlayers[i];
                if (p != null && p.PlayerId == bossTargetPlayerId)
                {
                    target = p;
                    break;
                }
            }
            if (target == null || !board.Contains(target.LandId) || target.LandId == curLandId) return false;

            if (!cache.TryGetValue(target.LandId, out var field))
            {
                field = board.ComputeShortestPathsTo(target.LandId);
                cache[target.LandId] = field;
            }
            int bestDist = field.GetDistance(curLandId);
            if (bestDist == int.MaxValue || bestDist <= 0) return false;

            var all = board.GetValidNeighbors(curLandId);
            var bestExits = new List<(int NextLandId, double LogC, long ExactC)>();
            double logTotal = double.NegativeInfinity;
            bool exactAllEqual = true;
            long firstExactC = -1;
            for (int i = 0; i < all.Count; i++)
            {
                int nb = all[i];
                if (field.GetDistance(nb) != bestDist - 1) continue;
                double logC = field.GetLogPathCount(nb);
                if (double.IsNegativeInfinity(logC)) continue;
                long exactC = -1;
                if (field.TryGetExactPathCount(nb, out long cL) && cL > 0)
                {
                    exactC = cL;
                    if (firstExactC < 0) firstExactC = cL;
                    else if (cL != firstExactC) exactAllEqual = false;
                }
                else
                {
                    exactAllEqual = false;
                }
                bestExits.Add((nb, logC, exactC));
                logTotal = BoardGraph.LogAddExp(logTotal, logC);
            }
            if (bestExits.Count == 0 || double.IsNegativeInfinity(logTotal)) return false;

            bool isExactUniform = bestExits.Count >= 2 && exactAllEqual && firstExactC > 0;
            var transitions = new List<StepTransition>(bestExits.Count);
            for (int i = 0; i < bestExits.Count; i++)
            {
                double logP = bestExits[i].LogC - logTotal;
                double prob = isExactUniform ? (1.0 / bestExits.Count) : Math.Min(1.0, Math.Exp(logP));
                transitions.Add(new StepTransition(bestExits[i].NextLandId, prob, logP));
            }
            transitions.Sort((a, b) => b.LogProb.CompareTo(a.LogProb));

            outcome = new StepOutcome
            {
                Transitions = transitions,
                IsForkDecision = isForkDecision,
                Reason = ForkReasonKind.MarkedTargetChase,
                SummaryLabel = labelPrefix + "·" + target.SeatTag + "·距" + bestDist,
                IsExactUniformSplit = isExactUniform
            };
            return true;
        }

        private static bool AnyHasPriorityMark(MonsterState monster, List<TrackedPlayer> targets)
        {
            for (int i = 0; i < targets.Count; i++)
            {
                var p = targets[i];
                if (monster.ChasesProblemStudent && p.HasProblemStudentMark) return true;
                if (monster.ChasesMandarinDuckMark && p.HasMandarinDuckMark) return true;
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

        private static StepOutcome EmptyOutcome() => new StepOutcome { Transitions = new List<StepTransition>(0) };

        private static StepOutcome SingleTransition(int nextLandId, bool isForkDecision, ForkReasonKind reason, string label)
        {
            return new StepOutcome
            {
                Transitions = new List<StepTransition>(1) { new StepTransition(nextLandId, 1.0, 0.0) },
                IsForkDecision = isForkDecision,
                Reason = reason,
                SummaryLabel = label
            };
        }

        private static StepOutcome UniformSplit(IReadOnlyList<int> exits, ForkReasonKind reason, string label)
        {
            var list = new List<StepTransition>(exits.Count);
            double p = 1.0 / exits.Count;
            double logP = -Math.Log(exits.Count);
            for (int i = 0; i < exits.Count; i++) list.Add(new StepTransition(exits[i], p, logP));
            return new StepOutcome
            {
                Transitions = list,
                IsForkDecision = true,
                Reason = reason,
                SummaryLabel = label,
                IsExactUniformSplit = exits.Count >= 2
            };
        }

        private static void UpdateExitPositiveFlags(List<ForkExitPrediction> exits)
        {
            int positiveCount = 0;
            for (int i = 0; i < exits.Count; i++)
            {
                if (exits[i].IsPositiveSupport) positiveCount++;
            }
            bool hasOthers = positiveCount > 1;
            for (int i = 0; i < exits.Count; i++)
            {
                exits[i].HasOtherPositiveExits = hasOthers;
            }
        }

        private static long Gcd(long a, long b)
        {
            if (a < 0) a = -a;
            if (b < 0) b = -b;
            while (b != 0)
            {
                long t = a % b;
                a = b;
                b = t;
            }
            return a == 0 ? 1 : a;
        }

        private static bool TryMulChecked(long a, long b, out long prod)
        {
            prod = 0;
            if (a == 0 || b == 0) return true;
            if (a > long.MaxValue / b) return false;
            prod = a * b;
            return true;
        }

        private static bool TryMulRational(long n1, long d1, long n2, long d2, out long rn, out long rd)
        {
            rn = 0;
            rd = 1;
            if (n1 == 0 || n2 == 0) return true;
            // Pre-reduce each input fraction independently before cross-reducing so large reducible
            // inputs (e.g. 2^61 / 2^62 = 1/2 multiplied by 1/3) never overflow intermediate denominators.
            long ga = Gcd(n1, d1);
            n1 /= ga; d1 /= ga;
            long gb = Gcd(n2, d2);
            n2 /= gb; d2 /= gb;
            long g1 = Gcd(n1, d2);
            long g2 = Gcd(n2, d1);
            if (!TryMulChecked(n1 / g1, n2 / g2, out rn)) return false;
            if (!TryMulChecked(d1 / g2, d2 / g1, out rd)) return false;
            long gOut = Gcd(rn, rd);
            rn /= gOut;
            rd /= gOut;
            return true;
        }

        private static bool TryAddRational(long n1, long d1, long n2, long d2, out long rn, out long rd)
        {
            rn = 0;
            rd = 1;
            if (n1 == 0)
            {
                if (n2 == 0) return true;
                long g2 = Gcd(n2, d2);
                rn = n2 / g2;
                rd = d2 / g2;
                return true;
            }
            if (n2 == 0)
            {
                long g1 = Gcd(n1, d1);
                rn = n1 / g1;
                rd = d1 / g1;
                return true;
            }
            long g = Gcd(d1, d2);
            long s1 = d1 / g;
            long s2 = d2 / g;
            if (!TryMulChecked(n1, s2, out long term1)) return false;
            if (!TryMulChecked(n2, s1, out long term2)) return false;
            if (term1 > long.MaxValue - term2) return false;
            long sum = term1 + term2;
            if (!TryMulChecked(s1, d2, out long den)) return false;
            long gOut = Gcd(sum, den);
            rn = sum / gOut;
            rd = den / gOut;
            return true;
        }
    }
}
