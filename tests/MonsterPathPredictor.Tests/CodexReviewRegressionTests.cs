using System;
using System.Collections.Generic;
using System.Linq;
using MonsterPathPredictor.Core;
using Xunit;

namespace MonsterPathPredictor.Tests
{
    public sealed class CodexReviewRegressionTests
    {
        [Fact]
        public void Stealth1270202_AttractsAtFork_NoCombatNoStopNoPostCombatRedBlue()
        {
            // Board: 1 -> 2(Fork: Red=3, Blue=4) -> 4 -> 5(Fork: Red=6, Blue=7) -> 7
            // P1 sits at tile 4 with IsTargetable=true, CanBeAttacked=false (buff 1270202 毒苹果隐匿).
            // P2 sits at tile 7 (normal).
            // Monster walks 1 -> 2, chases P1 onto Blue(4), passes through tile 4 WITHOUT combat,
            // so StopAtFirstCombat does NOT truncate at 4, and RedBlueAfterCombat does NOT trigger at Fork 5.
            var board = new BoardGraph(new[]
            {
                new LandNodeSnapshot(1, new[] { 2 }),
                new LandNodeSnapshot(2, new[] { 3, 4, 1 }),
                new LandNodeSnapshot(3, new[] { 2 }),
                new LandNodeSnapshot(4, new[] { 5, 2 }),
                new LandNodeSnapshot(5, new[] { 6, 7, 4 }),
                new LandNodeSnapshot(6, new[] { 5 }),
                new LandNodeSnapshot(7, new[] { 5 })
            });

            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = 4, IsAlive = true, IsTargetable = true, CanBeAttacked = false },
                new TrackedPlayer { PlayerId = 2, Slot = 1, LandId = 7, IsAlive = true, IsTargetable = true, CanBeAttacked = true }
            };

            var monster = new MonsterState { MonsterId = 901, CurrentLandId = 1, FromLandId = -1, FrontNodeIds = new[] { 2 } };

            var planStop = MonsterPathEngine.Plan(board, monster, players, new PlanOptions
            {
                MaxSteps = 4,
                PostCombatMode = PostCombatMode.StopAtFirstCombat
            });

            // Still attracted onto Blue(4) at Fork 2
            Assert.Equal(4, planStop.Forks.Single(f => f.ForkLandId == 2).Exits[0].NextLandId);
            // Zero combat on tile 4; reaches Fork 5 and chases P2 onto Blue(7) and engages P2 at step 4
            Assert.DoesNotContain(planStop.Combats, c => c.LandId == 4);
            Assert.Equal(7, planStop.Forks.Single(f => f.ForkLandId == 5).Exits[0].NextLandId);
            Assert.Single(planStop.Combats);
            Assert.Equal(7, planStop.Combats[0].LandId);

            // Final-step landing directly on stealth player must also emit zero combat
            var planLandOnStealth = MonsterPathEngine.Plan(board, monster, players, new PlanOptions { MaxSteps = 2 });
            Assert.Empty(planLandOnStealth.Combats);
        }

        [Fact]
        public void TriangleLoopRevisitFork_RecordsSecondVisitTwoExitsWithoutOverwritingFirst()
        {
            // Exact 4-step counterexample: triangle F(1)-B(2)-A(3)-F(1) + dead-end C(4) off F(1).
            // Start at F(1) with From=A(3), Front=[B(2)], no players:
            // - Step 0 at F: single facing -> B(2) 100%.
            // - Step 1: B(2) -> A(3).
            // - Step 2: A(3) -> F(1).
            // - Step 3 revisit at F(1) from A(3): mid-walk fork with no players -> B(2) 50% / C(4) 50%.
            var board = new BoardGraph(new[]
            {
                new LandNodeSnapshot(1, new[] { 2, 4, 3 }),
                new LandNodeSnapshot(2, new[] { 3, 1 }),
                new LandNodeSnapshot(3, new[] { 1, 2 }),
                new LandNodeSnapshot(4, new[] { 1 })
            });

            var monster = new MonsterState
            {
                MonsterId = 902,
                CurrentLandId = 1,
                FromLandId = 3,
                FrontNodeIds = new[] { 2 }
            };

            var plan = MonsterPathEngine.Plan(board, monster, new List<TrackedPlayer>(), new PlanOptions { MaxSteps = 4 });

            var forksAtF = plan.Forks.Where(f => f.ForkLandId == 1).OrderBy(f => f.StepIndex).ToList();
            Assert.Equal(2, forksAtF.Count);

            Assert.Equal(0, forksAtF[0].StepIndex);
            Assert.Single(forksAtF[0].Exits);
            Assert.Equal(2, forksAtF[0].Exits[0].NextLandId);
            Assert.Equal(1.0, forksAtF[0].Exits[0].ConditionalProbability, 6);

            Assert.Equal(3, forksAtF[1].StepIndex);
            Assert.Equal(2, forksAtF[1].Exits.Count);
            Assert.Contains(forksAtF[1].Exits, e => e.NextLandId == 2 && Math.Abs(e.ConditionalProbability - 0.5) < 1e-6);
            Assert.Contains(forksAtF[1].Exits, e => e.NextLandId == 4 && Math.Abs(e.ConditionalProbability - 0.5) < 1e-6);
        }

        [Fact]
        public void Looping50PercentBranch_NeverInflatesEdgeOrTileProbabilityTo100()
        {
            // 0 -> 1(Fork: 50% -> loop 2-3-4-5-2, 50% -> line 10..15)
            // No players -> at Fork 1, splits 50% to 2 and 50% to 10.
            // Even when the 50% branch cycles through nodes 2..5 multiple times, no edge/tile inside it
            // may ever exceed 0.50!
            var loopBoard = new BoardGraph(new[]
            {
                new LandNodeSnapshot(0, new[] { 1 }),
                new LandNodeSnapshot(1, new[] { 2, 10, 0 }),
                new LandNodeSnapshot(2, new[] { 3, 5, 1 }),
                new LandNodeSnapshot(3, new[] { 4, 2 }),
                new LandNodeSnapshot(4, new[] { 5, 3 }),
                new LandNodeSnapshot(5, new[] { 2, 4 }),
                new LandNodeSnapshot(10, new[] { 11, 1 }),
                new LandNodeSnapshot(11, new[] { 12, 10 }),
                new LandNodeSnapshot(12, new[] { 13, 11 }),
                new LandNodeSnapshot(13, new[] { 14, 12 }),
                new LandNodeSnapshot(14, new[] { 15, 13 }),
                new LandNodeSnapshot(15, new[] { 14 })
            });
            var m = new MonsterState { MonsterId = 903, CurrentLandId = 0, FromLandId = -1, FrontNodeIds = new[] { 1 } };
            var plan = MonsterPathEngine.Plan(loopBoard, m, new List<TrackedPlayer>(), new PlanOptions { MaxSteps = 8 });

            foreach (var e in plan.Edges.Where(e => e.FromLandId >= 2 && e.FromLandId <= 5))
            {
                Assert.True(e.Probability <= 0.5 + 1e-6, $"Edge {e.FromLandId}->{e.ToLandId} inflated to {e.Probability}");
            }
            foreach (var ts in plan.TileSteps.Where(t => t.LandId >= 2 && t.LandId <= 5))
            {
                Assert.True(ts.Probability <= 0.5 + 1e-6, $"TileStep {ts.LandId} inflated to {ts.Probability}");
            }

            // Contrast: Diamond merge (0->1->(2|3)->4->5) where two 50% branches meet at step 3 on tile 4 -> 1.0!
            var diamond = new BoardGraph(new[]
            {
                new LandNodeSnapshot(0, new[] { 1 }),
                new LandNodeSnapshot(1, new[] { 2, 3, 0 }),
                new LandNodeSnapshot(2, new[] { 4, 1 }),
                new LandNodeSnapshot(3, new[] { 4, 1 }),
                new LandNodeSnapshot(4, new[] { 5, 2, 3 }),
                new LandNodeSnapshot(5, new[] { 4 })
            });
            var dPlan = MonsterPathEngine.Plan(diamond, m, new List<TrackedPlayer>(), new PlanOptions { MaxSteps = 4 });
            Assert.Equal(1.0, dPlan.TileSteps.Single(t => t.LandId == 4).Probability, 6);
        }

        [Fact]
        public void EightConsecutiveSplits_ExactDefaultPreservesFullProbabilityMass_AndPruningTracksPrunedMass()
        {
            // Complete binary tree of depth 8 (root=1, children 2i and 2i+1, start=0->1):
            // 256 leaves each with probability 1/256 ≈ 0.00390625 (< old default 0.005).
            var nodes = new List<LandNodeSnapshot> { new LandNodeSnapshot(0, new[] { 1 }) };
            for (int i = 1; i <= 511; i++)
            {
                var nbs = new List<int>();
                if (i <= 255)
                {
                    nbs.Add(i * 2);
                    nbs.Add(i * 2 + 1);
                }
                nbs.Add(i == 1 ? 0 : i / 2);
                nodes.Add(new LandNodeSnapshot(i, nbs.ToArray()));
            }
            var board = new BoardGraph(nodes);
            var m = new MonsterState { MonsterId = 904, CurrentLandId = 0, FromLandId = -1, FrontNodeIds = new[] { 1 } };

            // 1. Default PlanOptions (MinBranchProbability=0.0): exact conservation across all 256 leaves
            var exactPlan = MonsterPathEngine.Plan(board, m, new List<TrackedPlayer>(), new PlanOptions { MaxSteps = 9 });
            Assert.Equal(256, exactPlan.Endpoints.Count);
            Assert.Equal(1.0, exactPlan.Endpoints.Sum(e => e.Probability), 6);
            Assert.Equal(0.0, exactPlan.PrunedProbability, 6);

            // 2. Explicit pruning threshold 0.005: endpoints + PrunedProbability == 1.0 (no silent loss!)
            var prunedPlan = MonsterPathEngine.Plan(board, m, new List<TrackedPlayer>(), new PlanOptions
            {
                MaxSteps = 10,
                MinBranchProbability = 0.005
            });
            Assert.True(prunedPlan.PrunedProbability > 0.99);
            Assert.Equal(1.0, prunedPlan.Endpoints.Sum(e => e.Probability) + prunedPlan.PrunedProbability, 6);
        }

        [Fact]
        public void OngoingTaunt_PersistsAcrossStraightRoadToMidWalkFork()
        {
            // Straight road 1 -> 2(Fork: Red=3, Blue=4) -> 3(P2 closer) / 4 -> 5(P1 taunter farther)
            // Monster starts at 1 heading to 2 while taunted by P1(5). Must turn onto Blue(4) toward P1, not Red(3) toward closer P2!
            var board = new BoardGraph(new[]
            {
                new LandNodeSnapshot(1, new[] { 2 }),
                new LandNodeSnapshot(2, new[] { 3, 4, 1 }),
                new LandNodeSnapshot(3, new[] { 2 }),
                new LandNodeSnapshot(4, new[] { 5, 2 }),
                new LandNodeSnapshot(5, new[] { 4 })
            });

            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 10, Slot = 0, LandId = 5, IsAlive = true, IsTargetable = true },
                new TrackedPlayer { PlayerId = 20, Slot = 1, LandId = 3, IsAlive = true, IsTargetable = true }
            };

            var monster = new MonsterState
            {
                MonsterId = 905,
                CurrentLandId = 1,
                FromLandId = -1,
                FrontNodeIds = new[] { 2 },
                IsTaunted = true,
                TauntTargetPlayerId = 10
            };

            var plan = MonsterPathEngine.Plan(board, monster, players, new PlanOptions { MaxSteps = 3 });
            var fork = plan.Forks.Single(f => f.ForkLandId == 2);
            Assert.Equal(ForkReasonKind.TauntChase, fork.ReasonKind);
            Assert.Single(fork.Exits);
            Assert.Equal(4, fork.Exits[0].NextLandId);
        }

        [Fact]
        public void ZeroMaxSteps_ReturnsOnlyStartTile_AndExactLengthDeadEndMarksIsDeadEnd()
        {
            var line = new BoardGraph(new[]
            {
                new LandNodeSnapshot(10, new[] { 11 }),
                new LandNodeSnapshot(11, new[] { 10, 12 }),
                new LandNodeSnapshot(12, new[] { 11, 13 }),
                new LandNodeSnapshot(13, new[] { 12 })
            });
            var m = new MonsterState { MonsterId = 906, CurrentLandId = 10, FromLandId = -1, FrontNodeIds = new[] { 11 } };

            // MaxSteps=0 -> only start tile, zero edges/forks
            var plan0 = MonsterPathEngine.Plan(line, m, new List<TrackedPlayer>(), new PlanOptions { MaxSteps = 0 });
            Assert.Equal(0, plan0.StepsRequested);
            Assert.Empty(plan0.Edges);
            Assert.Empty(plan0.Forks);
            Assert.Single(plan0.TileSteps);
            Assert.Equal(0, plan0.TileSteps[0].StepIndex);
            Assert.Single(plan0.Endpoints);
            Assert.Equal(10, plan0.Endpoints[0].LandId);

            // Exact 3-step dead end: BOTH MaxSteps=3 and MaxSteps=4 consistently mark IsDeadEnd=true
            var plan3 = MonsterPathEngine.Plan(line, m, new List<TrackedPlayer>(), new PlanOptions { MaxSteps = 3 });
            var plan4 = MonsterPathEngine.Plan(line, m, new List<TrackedPlayer>(), new PlanOptions { MaxSteps = 4 });
            Assert.True(plan3.Endpoints.Single().IsDeadEnd);
            Assert.True(plan4.Endpoints.Single().IsDeadEnd);
            Assert.Equal("终点(3格)", plan3.TileSteps.Single(t => t.LandId == 13).FormatStepBadge());
            Assert.Equal("终点(3格)", plan4.TileSteps.Single(t => t.LandId == 13).FormatStepBadge());
        }

        [Fact]
        public void StackedMonstersHitTest_IsEnumerationOrderIndependentAcrossPermutations()
        {
            // Three overlapping monsters with depths 10.0, 10.25, 10.5 (within/across CoplanarDepthThreshold=0.35):
            // Order-independent algorithm must return the exact same winner for all 6 permutations.
            var click = new Point2(100f, 100f);
            var a = new MonsterHitBox { MonsterId = 1, Rect = new HitRect2D(80, 80, 120, 120), CameraDepth = 10.0f };
            var b = new MonsterHitBox { MonsterId = 2, Rect = new HitRect2D(90, 90, 110, 110), CameraDepth = 10.25f };
            var c = new MonsterHitBox { MonsterId = 3, Rect = new HitRect2D(95, 95, 105, 105), CameraDepth = 10.50f };

            var perms = new[]
            {
                new[] { a, b, c }, new[] { a, c, b },
                new[] { b, a, c }, new[] { b, c, a },
                new[] { c, a, b }, new[] { c, b, a }
            };

            long expected = MonsterHitTester.PickMonster(click, perms[0], 28f);
            Assert.True(expected == 1 || expected == 2); // c (10.50 > 10.0 + 0.35) must NEVER win!
            foreach (var p in perms)
            {
                Assert.Equal(expected, MonsterHitTester.PickMonster(click, p, 28f));
            }

            // Round 2 counterexample (#16): equal depth 10, half-width 50, center X = 0, 1.4, 2, IDs = 3, 2, 1
            // Non-transitive pairwise tolerance previously returned 1 in forward order and 3 in reverse order!
            var eqA = new MonsterHitBox { MonsterId = 3, Rect = new HitRect2D(-50f, -50f, 50f, 50f), CameraDepth = 10f };
            var eqB = new MonsterHitBox { MonsterId = 2, Rect = new HitRect2D(-48.6f, -50f, 51.4f, 50f), CameraDepth = 10f };
            var eqC = new MonsterHitBox { MonsterId = 1, Rect = new HitRect2D(-48f, -50f, 52f, 50f), CameraDepth = 10f };
            var eqPerms = new[]
            {
                new[] { eqA, eqB, eqC }, new[] { eqA, eqC, eqB },
                new[] { eqB, eqA, eqC }, new[] { eqB, eqC, eqA },
                new[] { eqC, eqA, eqB }, new[] { eqC, eqB, eqA }
            };
            long eqWinner = MonsterHitTester.PickMonster(new Point2(0f, 0f), eqPerms[0], 28f);
            foreach (var p in eqPerms)
            {
                Assert.Equal(eqWinner, MonsterHitTester.PickMonster(new Point2(0f, 0f), p, 28f));
            }
        }

        [Fact]
        public void PostCombatTauntPrecedence_LongForecastMatchesArrivalRecomputation()
        {
            // Round 2 (#11): RedBlueAfterCombat + Taunted monster fights P2 on normal tile 2,
            // then reaches Fork 3 (Red=4, Blue=5 -> Taunter P1 at 6).
            // Long forecast from 1 AND fresh arrival recomputation at 3 must BOTH choose Red(4)!
            var board = new BoardGraph(new[]
            {
                new LandNodeSnapshot(1, new[] { 2 }),
                new LandNodeSnapshot(2, new[] { 3, 1 }),
                new LandNodeSnapshot(3, new[] { 4, 5, 2 }),
                new LandNodeSnapshot(4, new[] { 3 }),
                new LandNodeSnapshot(5, new[] { 6, 3 }),
                new LandNodeSnapshot(6, new[] { 5 })
            });
            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = 6, IsAlive = true, IsTargetable = true },
                new TrackedPlayer { PlayerId = 2, Slot = 1, LandId = 2, IsAlive = true, IsTargetable = true }
            };
            var opts = new PlanOptions { MaxSteps = 4, PostCombatMode = PostCombatMode.RedBlueAfterCombat };

            var fromStart = new MonsterState
            {
                MonsterId = 911, CurrentLandId = 1, FromLandId = -1, FrontNodeIds = new[] { 2 },
                IsTaunted = true, TauntTargetPlayerId = 1
            };
            var longPlan = MonsterPathEngine.Plan(board, fromStart, players, opts);
            int longChoice = longPlan.Forks.Single(f => f.ForkLandId == 3).Exits[0].NextLandId;

            var atForkAfterCombat = new MonsterState
            {
                MonsterId = 911, CurrentLandId = 3, FromLandId = 2,
                IsCurrentlyMovingMidTurn = true, HasLaunchedFightThisTurn = true,
                IsTaunted = true, TauntTargetPlayerId = 1
            };
            var arrivalPlan = MonsterPathEngine.Plan(board, atForkAfterCombat, players, opts);
            int arrivalChoice = arrivalPlan.Forks.Single(f => f.ForkLandId == 3).Exits[0].NextLandId;

            Assert.Equal(4, longChoice);
            Assert.Equal(longChoice, arrivalChoice);
        }

        [Fact]
        public void ExcludedTurnStartMark_DoesNotRegainPriorityInFallbackMidWalkRanking()
        {
            // Round 2 (#12): 1047 started turn on tile 1 where ONLY marked player P1 stands.
            // Monster walks 1 -> 2 -> 3(Fork: Red=4->1 (dist 2 to P1), Blue=5(P2 unmarked, dist 1 to P2)).
            // P1 on TurnStartLandId(1) must NOT regain PriorityTier=0 over closer unmarked P2(5)!
            var board = new BoardGraph(new[]
            {
                new LandNodeSnapshot(1, new[] { 2, 4 }),
                new LandNodeSnapshot(2, new[] { 3, 1 }),
                new LandNodeSnapshot(3, new[] { 4, 5, 2 }),
                new LandNodeSnapshot(4, new[] { 1, 3 }),
                new LandNodeSnapshot(5, new[] { 3 })
            });
            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = 1, IsAlive = true, IsTargetable = true, HasMandarinDuckMark = true },
                new TrackedPlayer { PlayerId = 2, Slot = 1, LandId = 5, IsAlive = true, IsTargetable = true, HasMandarinDuckMark = false }
            };
            var monster = new MonsterState
            {
                MonsterId = 912, HeroId = 1047, ChasesMandarinDuckMark = true,
                CurrentLandId = 3, FromLandId = 2, TurnStartLandId = 1,
                IsCurrentlyMovingMidTurn = true
            };

            var plan = MonsterPathEngine.Plan(board, monster, players, new PlanOptions { MaxSteps = 1 });
            Assert.Equal(5, plan.Forks.Single(f => f.ForkLandId == 3).Exits[0].NextLandId);
        }

        [Fact]
        public void MidTurnDeadEndRecomputation_StopsCleanlyInsteadOfUTurning()
        {
            // Round 2 (#13): minimal board A(1)-B(2). Arriving at B(2) mid-turn with From=A(1), Front=[]
            // must stop as a dead-end (zero edges, IsDeadEnd=true) instead of U-turning to A(1)!
            var board = new BoardGraph(new[]
            {
                new LandNodeSnapshot(1, new[] { 2 }),
                new LandNodeSnapshot(2, new[] { 1 })
            });
            var atB = new MonsterState
            {
                MonsterId = 913, CurrentLandId = 2, FromLandId = 1,
                FrontNodeIds = Array.Empty<int>(), IsCurrentlyMovingMidTurn = true
            };
            var plan = MonsterPathEngine.Plan(board, atB, new List<TrackedPlayer>(), new PlanOptions { MaxSteps = 3 });
            Assert.Empty(plan.Edges);
            Assert.True(plan.Endpoints.Single().IsDeadEnd);
        }

        [Fact]
        public void ThirtyBinarySplits_ExactModePreservesFullProbabilityDownToTwoToMinus30()
        {
            // Round 2 (#14): linear spine of 30 binary forks (0 -> 1 -> ... -> 30), each fork i has side dead-end (100+i).
            // With MinBranchProbability=0.0, even 2^-30 (~9.31e-10 < 1e-9) must NEVER be silently dropped!
            var nodes = new List<LandNodeSnapshot> { new LandNodeSnapshot(0, new[] { 1 }) };
            for (int i = 1; i <= 30; i++)
            {
                int side = 100 + i;
                int next = i + 1;
                nodes.Add(new LandNodeSnapshot(i, new[] { next, side, i - 1 }));
                nodes.Add(new LandNodeSnapshot(side, new[] { i }));
            }
            nodes.Add(new LandNodeSnapshot(31, new[] { 30 }));
            var board = new BoardGraph(nodes);
            var m = new MonsterState { MonsterId = 914, CurrentLandId = 0, FromLandId = -1, FrontNodeIds = new[] { 1 } };

            var plan = MonsterPathEngine.Plan(board, m, new List<TrackedPlayer>(), new PlanOptions { MaxSteps = 31, MinBranchProbability = 0.0 });
            Assert.Equal(0.0, plan.PrunedProbability, 12);
            Assert.Equal(1.0, plan.Endpoints.Sum(e => e.Probability), 12);
            Assert.Contains(plan.Endpoints, e => e.LandId == 31 && Math.Abs(e.Probability - Math.Pow(0.5, 30)) < 1e-15);
        }

        [Fact]
        public void MergedForkWithDifferingReasons_CombinesLabelInsteadOfShowingOnlyFirstPath()
        {
            // Round 2 (#15): ShirleyThrow on Fork 1 sends equal mass onto upper (2 has P3) and lower (3 empty).
            // At Fork 4, P1 (Slot 0, tile 5) beats P3 (Slot 2, tile 2), so BOTH paths advance 4 -> 5 at step 3:
            //   - Upper path already fought P3 at tile 2 -> at Fork 5 chases P2(8) onto Blue(7).
            //   - Lower path fights P1 for the first time at Fork 5 -> leaves Fork 5 via PostCombat Red(6).
            // Both merge at Fork 5 (AfterCombat=true, step=3) -> 50% Red / 50% Blue with combined "混合(...)" label!
            var board = new BoardGraph(new[]
            {
                new LandNodeSnapshot(0, new[] { 1 }),
                new LandNodeSnapshot(1, new[] { 2, 3, 0 }),
                new LandNodeSnapshot(2, new[] { 4, 1 }),
                new LandNodeSnapshot(3, new[] { 4, 1 }),
                new LandNodeSnapshot(4, new[] { 5, 2, 3 }),
                new LandNodeSnapshot(5, new[] { 6, 7, 4 }),
                new LandNodeSnapshot(6, new[] { 5 }),
                new LandNodeSnapshot(7, new[] { 8, 5 }),
                new LandNodeSnapshot(8, new[] { 7 })
            });
            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = 5, IsAlive = true, IsTargetable = true },
                new TrackedPlayer { PlayerId = 2, Slot = 1, LandId = 8, IsAlive = true, IsTargetable = true },
                new TrackedPlayer { PlayerId = 3, Slot = 2, LandId = 2, IsAlive = true, IsTargetable = true }
            };
            var m = new MonsterState
            {
                MonsterId = 915, CurrentLandId = 1, FromLandId = -1,
                SkillMoveKind = SkillMoveKind.ShirleyThrow
            };

            var plan = MonsterPathEngine.Plan(board, m, players, new PlanOptions { MaxSteps = 4, SimulateCombatOnFork = true });
            var fork5 = plan.Forks.Single(f => f.ForkLandId == 5 && f.StepIndex == 3);
            Assert.Equal(2, fork5.Exits.Count);
            Assert.Equal(ForkReasonKind.Mixed, fork5.ReasonKind);
            Assert.StartsWith("混合(", fork5.SummaryLabel);
        }

        [Fact]
        public void SingleFork30DiamondsVsSingleChain_ExactModeKeepsTinyPositiveExit()
        {
            // Round 3 (#8): Fork 1 (from 0) has two equal-length paths to Target (999):
            //   - Path A: 30 chained diamonds (length 60, 2^30 shortest paths)
            //   - Path B: single chain (length 60, 1 shortest path)
            // Conditional probability of Path B is 1 / (2^30 + 1) ≈ 9.3132e-10 (< 1e-9). Exact mode MUST preserve it!
            var adj = new Dictionary<int, List<int>>();
            void Link(int u, int v)
            {
                if (!adj.TryGetValue(u, out var lu)) adj[u] = lu = new List<int>();
                if (!adj.TryGetValue(v, out var lv)) adj[v] = lv = new List<int>();
                if (!lu.Contains(v)) lu.Add(v);
                if (!lv.Contains(u)) lv.Add(u);
            }

            Link(0, 1);
            // Path A: 1 -> (1000..1030 hubs with 2 parallel bridges per segment -> dist 60 to 999)
            // Segment k (0..29): hub(k) -> top(k), bot(k) -> hub(k+1), where hub(0)=1, hub(30)=999
            // Wait: to have two distinct forward exits from Fork 1, split 1 -> A_start(10) and 1 -> B_start(20),
            // each of length 61 to 999: A_start(10) runs 30 diamonds (dist 60 to 999), B_start(20) runs 60-step chain to 999.
            Link(1, 10);
            Link(1, 20);
            int curA = 10;
            for (int k = 0; k < 30; k++)
            {
                int top = 100 + k * 3;
                int bot = 101 + k * 3;
                int nxt = (k == 29) ? 999 : (102 + k * 3);
                Link(curA, top);
                Link(curA, bot);
                Link(top, nxt);
                Link(bot, nxt);
                curA = nxt;
            }
            int curB = 20;
            for (int k = 0; k < 60; k++)
            {
                int nxt = (k == 59) ? 999 : (500 + k);
                Link(curB, nxt);
                curB = nxt;
            }

            var nodes = adj.Select(kv => new LandNodeSnapshot(kv.Key, kv.Value.ToArray())).ToArray();
            var board = new BoardGraph(nodes);
            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = 999, IsAlive = true, IsTargetable = true }
            };
            var m = new MonsterState { MonsterId = 930, CurrentLandId = 1, FromLandId = 0, IsCurrentlyMovingMidTurn = true };

            var plan = MonsterPathEngine.Plan(board, m, players, new PlanOptions { MaxSteps = 1, MinBranchProbability = 0.0 });
            var fork = plan.Forks.Single(f => f.ForkLandId == 1);
            Assert.Equal(2, fork.Exits.Count);
            double expectedB = 1.0 / (Math.Pow(2.0, 30) + 1.0);
            Assert.Contains(fork.Exits, e => e.NextLandId == 20 && Math.Abs(e.ConditionalProbability - expectedB) < 1e-16);
            Assert.Equal(1.0, fork.Exits.Sum(e => e.ConditionalProbability), 12);
            Assert.Equal(0.0, plan.PrunedProbability, 12);
        }

        [Fact]
        public void MidTurnDeadEndRecomputation_WithNonEmptyFrontContainingFrom_StillStopsCleanly()
        {
            // Round 3 (#9): A(1)-B(2), arriving at B mid-turn with From=A(1) and Front=[A(1)] MUST stop (not U-turn).
            var board = new BoardGraph(new[]
            {
                new LandNodeSnapshot(1, new[] { 2 }),
                new LandNodeSnapshot(2, new[] { 1 })
            });
            var atB = new MonsterState
            {
                MonsterId = 931, CurrentLandId = 2, FromLandId = 1,
                FrontNodeIds = new[] { 1 }, IsCurrentlyMovingMidTurn = true
            };
            var plan = MonsterPathEngine.Plan(board, atB, new List<TrackedPlayer>(), new PlanOptions { MaxSteps = 3 });
            Assert.Empty(plan.Edges);
            Assert.True(plan.Endpoints.Single().IsDeadEnd);
        }

        [Fact]
        public void PostCombatTurnStartMarkExclusion_LongForecastMatchesArrivalRecomputation()
        {
            // Round 3 (#10): 1047 walks 1 -> 2 (fights unmarked P3) -> Fork 3 (Red=4->1[marked P1], Blue=5[unmarked P2]).
            // Both long forecast from 1 and fresh arrival recomputation at 3 must agree 100%!
            var board = new BoardGraph(new[]
            {
                new LandNodeSnapshot(1, new[] { 2, 4 }),
                new LandNodeSnapshot(2, new[] { 3, 1 }),
                new LandNodeSnapshot(3, new[] { 4, 5, 2 }),
                new LandNodeSnapshot(4, new[] { 1, 3 }),
                new LandNodeSnapshot(5, new[] { 3 })
            });
            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = 1, IsAlive = true, IsTargetable = true, HasMandarinDuckMark = true },
                new TrackedPlayer { PlayerId = 2, Slot = 1, LandId = 5, IsAlive = true, IsTargetable = true },
                new TrackedPlayer { PlayerId = 3, Slot = 2, LandId = 2, IsAlive = true, IsTargetable = true }
            };

            var fromStart = new MonsterState
            {
                MonsterId = 932, HeroId = 1047, ChasesMandarinDuckMark = true,
                CurrentLandId = 1, FromLandId = -1, FrontNodeIds = new[] { 2 }, TurnStartLandId = 1
            };
            var longPlan = MonsterPathEngine.Plan(board, fromStart, players, new PlanOptions { MaxSteps = 3 });
            var longExits = longPlan.Forks.Single(f => f.ForkLandId == 3).Exits;

            var atFork = new MonsterState
            {
                MonsterId = 932, HeroId = 1047, ChasesMandarinDuckMark = true,
                CurrentLandId = 3, FromLandId = 2, TurnStartLandId = 1,
                IsCurrentlyMovingMidTurn = true, HasLaunchedFightThisTurn = true
            };
            var arrivalPlan = MonsterPathEngine.Plan(board, atFork, players, new PlanOptions { MaxSteps = 1 });
            var arrivalExits = arrivalPlan.Forks.Single(f => f.ForkLandId == 3).Exits;

            Assert.Single(longExits);
            Assert.Single(arrivalExits);
            Assert.Equal(5, longExits[0].NextLandId);
            Assert.Equal(longExits[0].NextLandId, arrivalExits[0].NextLandId);
        }

        [Fact]
        public void StraightRoadPassingUnattackableTaunter_LongForecastMatchesArrivalRecomputation()
        {
            // Round 3 (#11): A(1)-B(2)-C(3)-D(4), stealth taunter (CanBeAttacked=false) at B(2).
            // Monster walks A -> B (no combat) -> C: both long forecast from A and arrival recomputation at C continue C -> D!
            var board = new BoardGraph(new[]
            {
                new LandNodeSnapshot(1, new[] { 2 }),
                new LandNodeSnapshot(2, new[] { 3, 1 }),
                new LandNodeSnapshot(3, new[] { 4, 2 }),
                new LandNodeSnapshot(4, new[] { 3 })
            });
            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = 2, IsAlive = true, IsTargetable = true, CanBeAttacked = false }
            };

            var fromA = new MonsterState
            {
                MonsterId = 933, CurrentLandId = 1, FromLandId = -1, FrontNodeIds = new[] { 2 },
                IsTaunted = true, TauntTargetPlayerId = 1
            };
            var longPlan = MonsterPathEngine.Plan(board, fromA, players, new PlanOptions { MaxSteps = 3 });
            Assert.Contains(longPlan.Edges, e => e.FromLandId == 3 && e.ToLandId == 4);

            var atC = new MonsterState
            {
                MonsterId = 933, CurrentLandId = 3, FromLandId = 2, FrontNodeIds = new[] { 4 },
                IsCurrentlyMovingMidTurn = true, IsTaunted = true, TauntTargetPlayerId = 1
            };
            var arrivalPlan = MonsterPathEngine.Plan(board, atC, players, new PlanOptions { MaxSteps = 1 });
            Assert.Single(arrivalPlan.Edges);
            Assert.Equal(4, arrivalPlan.Edges[0].ToLandId);
        }

        [Fact]
        public void SingleExitMergedForkWithDifferingReasons_SetsReasonKindMixed()
        {
            // Round 3 (#12): Moving P2 to tile 6 makes BOTH upper (chase P2) and lower (PostCombat Red) exit to tile 6.
            // Even with a single merged exit, ReasonKind MUST become Mixed (not first-path-dependent)!
            var board = new BoardGraph(new[]
            {
                new LandNodeSnapshot(0, new[] { 1 }),
                new LandNodeSnapshot(1, new[] { 2, 3, 0 }),
                new LandNodeSnapshot(2, new[] { 4, 1 }),
                new LandNodeSnapshot(3, new[] { 4, 1 }),
                new LandNodeSnapshot(4, new[] { 5, 2, 3 }),
                new LandNodeSnapshot(5, new[] { 6, 7, 4 }),
                new LandNodeSnapshot(6, new[] { 5 }),
                new LandNodeSnapshot(7, new[] { 5 })
            });
            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = 5, IsAlive = true, IsTargetable = true },
                new TrackedPlayer { PlayerId = 2, Slot = 1, LandId = 6, IsAlive = true, IsTargetable = true },
                new TrackedPlayer { PlayerId = 3, Slot = 2, LandId = 2, IsAlive = true, IsTargetable = true }
            };
            var m = new MonsterState
            {
                MonsterId = 934, CurrentLandId = 1, FromLandId = -1,
                SkillMoveKind = SkillMoveKind.ShirleyThrow
            };

            var plan = MonsterPathEngine.Plan(board, m, players, new PlanOptions { MaxSteps = 4, SimulateCombatOnFork = true });
            var fork5 = plan.Forks.Single(f => f.ForkLandId == 5 && f.StepIndex == 3);
            Assert.Single(fork5.Exits);
            Assert.Equal(6, fork5.Exits[0].NextLandId);
            Assert.Equal(ForkReasonKind.Mixed, fork5.ReasonKind);
            Assert.StartsWith("混合(", fork5.SummaryLabel);
        }

        [Fact]
        public void DirectHitExactRect_AlwaysBeatsCloserDepthOutsideNearMiss()
        {
            // Round 3 (#17): Click (0,0) is strictly inside A [-20,20]x[-20,20] (depth 10),
            // and 2px outside B [2,32]x[-20,20] (depth 9). Direct hit A MUST win over near-miss B!
            var a = new MonsterHitBox { MonsterId = 10, Rect = new HitRect2D(-20f, -20f, 20f, 20f), CameraDepth = 10f };
            var b = new MonsterHitBox { MonsterId = 20, Rect = new HitRect2D(2f, -20f, 32f, 20f), CameraDepth = 9f };
            Assert.Equal(10, MonsterHitTester.PickMonster(new Point2(0f, 0f), new[] { a, b }, 28f));
            Assert.Equal(10, MonsterHitTester.PickMonster(new Point2(0f, 0f), new[] { b, a }, 28f));
        }

        [Fact]
        public void ProbabilityFormat_TinyPositiveAndComplement_NeverDisplayAsExactZeroOrHundred()
        {
            // Round 4 (#14): exact endpoints remain 0% / 100%, while strictly interior tiny/complement
            // probabilities (such as 1/(2^30+1)) render as "<0.1%" and ">99.9%".
            Assert.Equal("0%", ProbabilityFormat.Percent(0.0));
            Assert.Equal("100%", ProbabilityFormat.Percent(1.0));
            Assert.Equal("50%", ProbabilityFormat.Percent(0.5));
            double tiny = 1.0 / (Math.Pow(2.0, 30) + 1.0);
            Assert.Equal("<0.1%", ProbabilityFormat.Percent(tiny));
            Assert.Equal(">99.9%", ProbabilityFormat.Percent(1.0 - tiny));
        }

        [Fact]
        public void ThiefNormalTileSteal_RedBlueAfterCombat_LongForecastMatchesArrivalRecomputation()
        {
            // Round 4 (#12): Thief walks 1 -> 2 (steals from P1 with Gold=10) -> Fork 3 (Red=4, Blue=5[P2]).
            // Under PostCombatMode.RedBlueAfterCombat, both long forecast from 1 and arrival recomputation
            // at Fork 3 (with HasLaunchedFightThisTurn=true from normal-tile steal) must select Red(4).
            var board = new BoardGraph(new[]
            {
                new LandNodeSnapshot(1, new[] { 2 }),
                new LandNodeSnapshot(2, new[] { 3, 1 }),
                new LandNodeSnapshot(3, new[] { 4, 5, 2 }),
                new LandNodeSnapshot(4, new[] { 3 }),
                new LandNodeSnapshot(5, new[] { 3 })
            });
            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = 2, IsAlive = true, IsTargetable = true, Gold = 10 },
                new TrackedPlayer { PlayerId = 2, Slot = 1, LandId = 5, IsAlive = true, IsTargetable = true, Gold = 10 }
            };
            var opts = new PlanOptions { MaxSteps = 3, PostCombatMode = PostCombatMode.RedBlueAfterCombat };

            var fromStart = new MonsterState
            {
                MonsterId = 940, IsNormalAggressive = false, IsThief = true,
                CurrentLandId = 1, FromLandId = -1, FrontNodeIds = new[] { 2 }
            };
            var longPlan = MonsterPathEngine.Plan(board, fromStart, players, opts);
            int longChoice = longPlan.Forks.Single(f => f.ForkLandId == 3).Exits[0].NextLandId;

            var atForkAfterSteal = new MonsterState
            {
                MonsterId = 940, IsNormalAggressive = false, IsThief = true,
                CurrentLandId = 3, FromLandId = 2,
                IsCurrentlyMovingMidTurn = true, HasLaunchedFightThisTurn = true
            };
            var arrivalPlan = MonsterPathEngine.Plan(board, atForkAfterSteal, players, opts);
            int arrivalChoice = arrivalPlan.Forks.Single(f => f.ForkLandId == 3).Exits[0].NextLandId;

            Assert.Equal(4, longChoice);
            Assert.Equal(longChoice, arrivalChoice);
        }

        [Fact]
        public void DirectedAdjacencyGraph_LongForecastMatchesArrivalRecomputation()
        {
            // Round 5 (#11): Directed 0 -> 1, 1 -> [2, 3] (no 1 -> 0 back-edge), single player at 2.
            // Both 2-step forecast from 0 and mid-turn arrival recomputation at 1 (From=0, Front=[]) MUST pick 2 at 100%.
            var board = new BoardGraph(new[]
            {
                new LandNodeSnapshot(0, new[] { 1 }),
                new LandNodeSnapshot(1, new[] { 2, 3 }),
                new LandNodeSnapshot(2, Array.Empty<int>()),
                new LandNodeSnapshot(3, Array.Empty<int>())
            });
            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = 2, IsAlive = true, IsTargetable = true }
            };

            var fromStart = new MonsterState { MonsterId = 951, CurrentLandId = 0, FromLandId = -1, FrontNodeIds = new[] { 1 } };
            var longPlan = MonsterPathEngine.Plan(board, fromStart, players, new PlanOptions { MaxSteps = 2 });
            var longExits = longPlan.Forks.Single(f => f.ForkLandId == 1).Exits;

            var atOne = new MonsterState
            {
                MonsterId = 951, CurrentLandId = 1, FromLandId = 0,
                FrontNodeIds = Array.Empty<int>(), IsCurrentlyMovingMidTurn = true
            };
            var arrivalPlan = MonsterPathEngine.Plan(board, atOne, players, new PlanOptions { MaxSteps = 1 });
            var arrivalExits = arrivalPlan.Forks.Single(f => f.ForkLandId == 1).Exits;

            Assert.Single(longExits);
            Assert.Equal(2, longExits[0].NextLandId);
            Assert.Single(arrivalExits);
            Assert.Equal(2, arrivalExits[0].NextLandId);
        }

        [Fact]
        public void LargeDiamondGraph_1023DiamondsPerBranch_DoesNotOverflowToDeadEnd()
        {
            // Round 5 (#16): 2 equal-length exits merging into 1,023 chained diamonds (2^1023 shortest paths each).
            // Raw double addition overflows to Infinity; log-domain shortest-path counts must return exact 50%/50%.
            var adj = new Dictionary<int, List<int>>();
            void Link(int u, int v)
            {
                if (!adj.TryGetValue(u, out var lu)) adj[u] = lu = new List<int>();
                if (!adj.TryGetValue(v, out var lv)) adj[v] = lv = new List<int>();
                if (!lu.Contains(v)) lu.Add(v);
                if (!lv.Contains(u)) lv.Add(u);
            }

            Link(0, 1);
            Link(1, 2);
            Link(1, 3);
            Link(2, 4);
            Link(3, 4);
            int cur = 4;
            int nextId = 10;
            for (int d = 0; d < 1023; d++)
            {
                int top = nextId++;
                int bot = nextId++;
                int nxt = nextId++;
                Link(cur, top);
                Link(cur, bot);
                Link(top, nxt);
                Link(bot, nxt);
                cur = nxt;
            }

            var board = new BoardGraph(adj.Select(kv => new LandNodeSnapshot(kv.Key, kv.Value.ToArray())).ToArray());
            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = cur, IsAlive = true, IsTargetable = true }
            };
            var m = new MonsterState { MonsterId = 952, CurrentLandId = 1, FromLandId = 0, IsCurrentlyMovingMidTurn = true };

            var plan = MonsterPathEngine.Plan(board, m, players, new PlanOptions { MaxSteps = 1 });
            var fork = plan.Forks.Single(f => f.ForkLandId == 1);
            Assert.Equal(2, fork.Exits.Count);
            Assert.Equal(0.5, fork.Exits[0].ConditionalProbability, 6);
            Assert.Equal(0.5, fork.Exits[1].ConditionalProbability, 6);
        }

        [Fact]
        public void DashGeometry_ClipsOffscreenEndpointAndCoversFullLongEdgeWithoutTruncation()
        {
            // Round 5 (#17): Segment (200,200)->(1200,200) against viewport [0,0,800,600] keeps visible [200..800].
            bool visible = DashGeometry.TryClipSegmentToRect(
                new Point2(200f, 200f), new Point2(1200f, 200f),
                0f, 0f, 800f, 600f,
                out var ca, out var cb, out float trim);
            Assert.True(visible);
            Assert.Equal(200f, ca.X, 3);
            Assert.Equal(800f, cb.X, 3);
            Assert.Equal(0f, trim, 3);

            // Round 5 (#18): Long edge (0,0)->(1920,0) with valid config dash=4, gap=2 must span all the way to x=1920.
            var segs = new List<DashQuad>();
            DashGeometry.BuildDashedSegmentsInto(segs, new Point2(0f, 0f), new Point2(1920f, 0f), 4f, 2f, 4f, 0f, 0f, 0f);
            Assert.True(segs.Count > 256);
            var last = segs[segs.Count - 1];
            float maxX = Math.Max(Math.Max(last.P0.X, last.P1.X), Math.Max(last.P2.X, last.P3.X));
            Assert.True(maxX >= 1916f, $"Last dash stopped early at x={maxX}");

            // Extreme 50,000px edge also downscales proportionally to cover the full visible length.
            DashGeometry.BuildDashedSegmentsInto(segs, new Point2(0f, 0f), new Point2(50000f, 0f), 4f, 2f, 4f, 0f, 0f, 0f);
            var lastBig = segs[segs.Count - 1];
            float maxBigX = Math.Max(Math.Max(lastBig.P0.X, lastBig.P1.X), Math.Max(lastBig.P2.X, lastBig.P3.X));
            Assert.True(maxBigX >= 49900f, $"Extreme edge dash stopped early at x={maxBigX}");
        }

        [Fact]
        public void ThiefStopAtFirstCombat_StopsImmediatelyAtStealTileAndMarksFinalStep()
        {
            // Round 6 (#14): 1-2-3, Thief starts at 1 facing 2, player with Gold=1 at 2.
            var board = new BoardGraph(new[]
            {
                new LandNodeSnapshot(1, new[] { 2 }),
                new LandNodeSnapshot(2, new[] { 3, 1 }),
                new LandNodeSnapshot(3, new[] { 2 })
            });
            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = 2, IsAlive = true, IsTargetable = true, Gold = 1 }
            };
            var thief = new MonsterState
            {
                MonsterId = 960, IsNormalAggressive = false, IsThief = true,
                CurrentLandId = 1, FromLandId = -1, FrontNodeIds = new[] { 2 }
            };

            // MaxSteps=2 -> stops at tile 2 (no edge 2->3) with StoppedAtCombat=true
            var plan2 = MonsterPathEngine.Plan(board, thief, players, new PlanOptions { MaxSteps = 2, PostCombatMode = PostCombatMode.StopAtFirstCombat });
            Assert.Single(plan2.Edges);
            Assert.Equal(2, plan2.Edges[0].ToLandId);
            Assert.True(plan2.Combats.Single().IsSteal);
            var ep2 = plan2.Endpoints.Single();
            Assert.Equal(2, ep2.LandId);
            Assert.True(ep2.StoppedAtCombat);

            // MaxSteps=1 (final step lands on steal tile) -> also sets StoppedAtCombat=true
            var plan1 = MonsterPathEngine.Plan(board, thief, players, new PlanOptions { MaxSteps = 1, PostCombatMode = PostCombatMode.StopAtFirstCombat });
            Assert.True(plan1.Endpoints.Single().StoppedAtCombat);
        }

        [Fact]
        public void Extreme1076DiamondsVsSingleChain_RetainsUnderflowedExitInPositiveSupport()
        {
            // Round 6 (#15): Exit A has 2^1076 shortest paths, Exit B has 1 shortest path.
            // 1/(2^1076+1) underflows IEEE-754 double to 0.0, yet B MUST remain in fork.Exits with IsPositiveSupport=true!
            var adj = new Dictionary<int, List<int>>();
            void Link(int u, int v)
            {
                if (!adj.TryGetValue(u, out var lu)) adj[u] = lu = new List<int>();
                if (!adj.TryGetValue(v, out var lv)) adj[v] = lv = new List<int>();
                if (!lu.Contains(v)) lu.Add(v);
                if (!lv.Contains(u)) lv.Add(u);
            }

            Link(0, 1);
            Link(1, 2); // Branch A start
            Link(1, 3); // Branch B start
            int nextId = 10;
            int curA = 2;
            const int diamonds = 1076;
            const int target = 999999;
            for (int d = 0; d < diamonds; d++)
            {
                int top = nextId++;
                int bot = nextId++;
                int nxt = (d == diamonds - 1) ? target : nextId++;
                Link(curA, top);
                Link(curA, bot);
                Link(top, nxt);
                Link(bot, nxt);
                curA = nxt;
            }
            int curB = 3;
            for (int s = 0; s < diamonds * 2; s++)
            {
                int nxt = (s == diamonds * 2 - 1) ? target : nextId++;
                Link(curB, nxt);
                curB = nxt;
            }

            var board = new BoardGraph(adj.Select(kv => new LandNodeSnapshot(kv.Key, kv.Value.ToArray())).ToArray());
            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = target, IsAlive = true, IsTargetable = true }
            };
            var m = new MonsterState { MonsterId = 961, CurrentLandId = 1, FromLandId = 0, IsCurrentlyMovingMidTurn = true };

            var plan = MonsterPathEngine.Plan(board, m, players, new PlanOptions { MaxSteps = 1, MinBranchProbability = 0.0 });
            var fork = plan.Forks.Single(f => f.ForkLandId == 1);
            Assert.Equal(2, fork.Exits.Count);
            Assert.True(fork.HasMultipleBranches);
            var exitB = fork.Exits.Single(e => e.NextLandId == 3);
            Assert.True(exitB.IsPositiveSupport);
            Assert.Equal("<0.1%", exitB.FormatConditionalPercent());

            // Round 7 (#19): Positive-support underflowed exit 1->3 MUST also propagate into Edges, TileSteps, and Endpoints!
            Assert.Contains(plan.Edges, e => e.FromLandId == 1 && e.ToLandId == 3);
            Assert.Contains(plan.TileSteps, ts => ts.LandId == 3 && ts.StepIndex == 1);
            Assert.Contains(plan.Endpoints, ep => ep.LandId == 3 && ep.StepIndex == 1);
        }

        [Fact]
        public void GeometryRound6_NearPlaneClip_StaticDashPhase_DoublePrecisionClip_AndExactSmallPathCount()
        {
            // Round 6 (#16): 3D near-plane clip preserves visible part of (0,0,-1)->(1,0,4) against near=0.3
            bool okNear = DashGeometry.TryClipSegmentToNearPlane(
                0f, 0f, -1f, 1f, 0f, 4f,
                0f, 0f, 0f, 0f, 0f, 1f, 0.3f,
                out float ax, out _, out float az, out float bx, out _, out float bz,
                out bool clipS, out bool clipE);
            Assert.True(okNear);
            Assert.True(clipS);
            Assert.False(clipE);
            Assert.Equal(0.3f, az, 3);
            Assert.Equal(4f, bz, 3);
            Assert.Equal(0.26f, ax, 3);
            Assert.Equal(1f, bx, 3);

            // Round 6 (#17): Viewport clip preserves static dash phase (origTrimStart=10, dash=12, gap=8, phase=0, clipStart=20)
            var fullQuads = new List<DashQuad>();
            DashGeometry.BuildDashedSegmentsInto(fullQuads, new Point2(0f, 0f), new Point2(100f, 0f), 12f, 8f, 4f, trimStart: 10f, trimEnd: 0f, phaseOffset: 0f);
            float newTrim = Math.Max(0f, 10f - 20f);
            float clippedPhase = DashGeometry.ComputeClippedDashPhase(0f, 10f, 20f, newTrim);
            var clippedQuads = new List<DashQuad>();
            DashGeometry.BuildDashedSegmentsInto(clippedQuads, new Point2(20f, 0f), new Point2(100f, 0f), 12f, 8f, 4f, trimStart: newTrim, trimEnd: 0f, phaseOffset: clippedPhase);
            // Both unclipped and clipped must draw exact dashes [50,62] and [70,82]
            Assert.Contains(fullQuads, q => Math.Abs(Math.Min(q.P0.X, q.P1.X) - 50f) < 0.1f && Math.Abs(Math.Max(q.P0.X, q.P1.X) - 62f) < 0.1f);
            Assert.Contains(clippedQuads, q => Math.Abs(Math.Min(q.P0.X, q.P1.X) - 50f) < 0.1f && Math.Abs(Math.Max(q.P0.X, q.P1.X) - 62f) < 0.1f);

            // Round 6 (#18): (-2^36, 50) -> (2^36, 50) clipped to [0,0,800,100] preserves full 800px segment
            float huge = (float)Math.Pow(2.0, 36);
            bool okClip = DashGeometry.TryClipSegmentToRect(new Point2(-huge, 50f), new Point2(huge, 50f), 0f, 0f, 800f, 100f, out var ca, out var cb, out double _);
            Assert.True(okClip);
            Assert.Equal(0f, ca.X, 3);
            Assert.Equal(800f, cb.X, 3);

            // Round 6 (#19): 3 chained diamonds have EXACT count 8.0 (not 7.999999999999998)
            var diamondBoard = new BoardGraph(new[]
            {
                new LandNodeSnapshot(0, new[] { 1, 2 }),
                new LandNodeSnapshot(1, new[] { 3, 0 }), new LandNodeSnapshot(2, new[] { 3, 0 }),
                new LandNodeSnapshot(3, new[] { 4, 5, 1, 2 }),
                new LandNodeSnapshot(4, new[] { 6, 3 }), new LandNodeSnapshot(5, new[] { 6, 3 }),
                new LandNodeSnapshot(6, new[] { 7, 8, 4, 5 }),
                new LandNodeSnapshot(7, new[] { 9, 6 }), new LandNodeSnapshot(8, new[] { 9, 6 }),
                new LandNodeSnapshot(9, new[] { 7, 8 })
            });
            var field = diamondBoard.ComputeShortestPathsTo(9);
            Assert.Equal(8.0, field.GetPathCount(0));
            Assert.Equal(8, (int)field.GetPathCount(0));
        }

        [Fact]
        public void Round7CoreRegressions_DirectedPostCombat_StraightMidTurn_UnknownFrom_LinglingDirected_ExtremeClip_DoublePhase()
        {
            // Round 7 (#4): Directed 0->1, 1->[2,3], P1 at 1, P2 at 3. Post-combat arrival recomputation at 1
            // (JustFoughtOnCurrentFork=true, From=0) MUST match long forecast from 0 -> Red(2).
            var dirBoard = new BoardGraph(new[]
            {
                new LandNodeSnapshot(0, new[] { 1 }),
                new LandNodeSnapshot(1, new[] { 2, 3 }),
                new LandNodeSnapshot(2, Array.Empty<int>()),
                new LandNodeSnapshot(3, Array.Empty<int>())
            });
            var dirPlayers = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = 1, IsAlive = true, IsTargetable = true },
                new TrackedPlayer { PlayerId = 2, Slot = 1, LandId = 3, IsAlive = true, IsTargetable = true }
            };
            var longDir = MonsterPathEngine.Plan(dirBoard, new MonsterState { MonsterId = 970, CurrentLandId = 0, FromLandId = -1, FrontNodeIds = new[] { 1 } }, dirPlayers, new PlanOptions { MaxSteps = 2 });
            var postCombatDir = MonsterPathEngine.Plan(dirBoard, new MonsterState { MonsterId = 970, CurrentLandId = 1, FromLandId = 0, JustFoughtOnCurrentFork = true, HasLaunchedFightThisTurn = true }, dirPlayers, new PlanOptions { MaxSteps = 1 });
            Assert.Equal(2, longDir.Forks.Single(f => f.ForkLandId == 1).Exits.Single().NextLandId);
            Assert.Equal(2, postCombatDir.Forks.Single(f => f.ForkLandId == 1).Exits.Single().NextLandId);

            // Round 7 (#8): Lingling 1-hop push 0->1 on directed 0->1, 1->[2,3,4], 2->0:
            // TryInferForwardArrivalPredecessor(0, 1) MUST return 0 (NOT 2), so Lingling picks Red(2)!
            var lingBoard = new BoardGraph(new[]
            {
                new LandNodeSnapshot(0, new[] { 1 }),
                new LandNodeSnapshot(1, new[] { 2, 3, 4 }),
                new LandNodeSnapshot(2, new[] { 0 }),
                new LandNodeSnapshot(3, Array.Empty<int>()),
                new LandNodeSnapshot(4, Array.Empty<int>())
            });
            Assert.True(lingBoard.TryInferForwardArrivalPredecessor(0, 1, out int pred));
            Assert.Equal(0, pred);
            var lingPlan = MonsterPathEngine.Plan(lingBoard, new MonsterState { MonsterId = 971, CurrentLandId = 1, SkillMoveKind = SkillMoveKind.LinglingPush, SkillMovePushFromLandId = pred }, new List<TrackedPlayer>(), new PlanOptions { MaxSteps = 1 });
            Assert.Equal(2, lingPlan.Forks.Single(f => f.ForkLandId == 1).Exits.Single().NextLandId);

            // Round 7 (#9 & #10): Bidirectional straight road 0-1-2:
            //   #9: At 1, From=0, Front=[0], IsCurrentlyMovingMidTurn=true MUST continue to 2 (no U-turn!).
            //   #10: At 1, From=-1, Front=[2], IsCurrentlyMovingMidTurn=true with player at 0 MUST follow Front=[2] (NOT treat as directed fork!).
            var lineBoard = new BoardGraph(new[]
            {
                new LandNodeSnapshot(0, new[] { 1 }),
                new LandNodeSnapshot(1, new[] { 0, 2 }),
                new LandNodeSnapshot(2, new[] { 1 })
            });
            var midLine = MonsterPathEngine.Plan(lineBoard, new MonsterState { MonsterId = 972, CurrentLandId = 1, FromLandId = 0, FrontNodeIds = new[] { 0 }, IsCurrentlyMovingMidTurn = true }, new List<TrackedPlayer>(), new PlanOptions { MaxSteps = 1 });
            Assert.Equal(2, midLine.Edges.Single().ToLandId);
            var unkFromLine = MonsterPathEngine.Plan(lineBoard, new MonsterState { MonsterId = 973, CurrentLandId = 1, FromLandId = -1, FrontNodeIds = new[] { 2 }, IsCurrentlyMovingMidTurn = true }, new List<TrackedPlayer> { new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = 0, IsAlive = true, IsTargetable = true } }, new PlanOptions { MaxSteps = 1 });
            Assert.Equal(2, unkFromLine.Edges.Single().ToLandId);
            Assert.Empty(unkFromLine.Forks);

            // Round 7 (#21): Double-precision dash phase modulo at huge clip distance 67,108,864 px (period=20 -> exact 6.0f, not 4.0f)
            float phaseBig = DashGeometry.ComputeClippedDashPhase(0.0, 10.0, 67108864.0, 0.0, 20.0);
            Assert.Equal(6.0f, phaseBig, 3);

            // Round 7 (#22): H = 2^64 diagonal (-H,-H)->(H,H) clipped to [0,800]x[0,100] MUST yield (0,0)->(100,100), NOT (0,0)->(800,0)!
            float h64 = (float)Math.Pow(2.0, 64);
            Assert.True(DashGeometry.TryClipSegmentToRect(new Point2(-h64, -h64), new Point2(h64, h64), 0f, 0f, 800f, 100f, out var ca, out var cb, out double _));
            Assert.Equal(0f, ca.X, 2);
            Assert.Equal(0f, ca.Y, 2);
            Assert.Equal(100f, cb.X, 2);
            Assert.Equal(100f, cb.Y, 2);
        }
    }
}
