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
        }
    }
}
