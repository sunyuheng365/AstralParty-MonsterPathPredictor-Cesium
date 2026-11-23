using System.Collections.Generic;
using System.Linq;
using MonsterPathPredictor.Core;
using Xunit;

namespace MonsterPathPredictor.Tests
{
    public class MonsterPathEngineTests
    {
        /// <summary>
        /// Builds a simple T-junction at tile 23:
        ///   24 -- 23 -- 22 -- 21 -- 20
        ///         |
        ///         55 -- 56 -- 57
        /// Raw neighbor order at 23: [22 (Red), 55 (Blue), 24 (None)].
        /// </summary>
        private static BoardGraph BuildTJunctionBoard()
        {
            return new BoardGraph(new[]
            {
                new LandNodeSnapshot(25, new[] { 24 }),
                new LandNodeSnapshot(24, new[] { 25, 23 }),
                new LandNodeSnapshot(23, new[] { 22, 55, 24 }),
                new LandNodeSnapshot(22, new[] { 23, 21 }),
                new LandNodeSnapshot(21, new[] { 22, 20 }),
                new LandNodeSnapshot(20, new[] { 21 }),
                new LandNodeSnapshot(55, new[] { 23, 56 }),
                new LandNodeSnapshot(56, new[] { 55, 57 }),
                new LandNodeSnapshot(57, new[] { 56 })
            });
        }

        [Fact]
        public void ForkStartIgnoresClosestPlayerAndFollowsRedThenBlue()
        {
            var board = BuildTJunctionBoard();
            // Player P1 stands right next to 23 on Blue branch (tile 55), yet fork-start ignores players!
            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = 55, IsAlive = true, IsTargetable = true }
            };

            // Coming from 24 -> Red arrow is 22
            var monsterFrom24 = new MonsterState
            {
                MonsterId = 100,
                Name = "玩具恐龙",
                CurrentLandId = 23,
                FromLandId = 24,
                IsCurrentlyMovingMidTurn = false
            };
            var planRed = MonsterPathEngine.Plan(board, monsterFrom24, players, new PlanOptions { MaxSteps = 3 });
            Assert.Single(planRed.Forks);
            Assert.Equal(ForkReasonKind.ForkStartRed, planRed.Forks[0].ReasonKind);
            Assert.Single(planRed.Forks[0].Exits);
            Assert.Equal(22, planRed.Forks[0].Exits[0].NextLandId);
            Assert.Equal(1.0, planRed.Forks[0].Exits[0].ConditionalProbability, 6);

            // Coming from 22 (Red occupied by incoming) -> falls back to Blue arrow 55
            var monsterFrom22 = new MonsterState
            {
                MonsterId = 100,
                Name = "玩具恐龙",
                CurrentLandId = 23,
                FromLandId = 22,
                IsCurrentlyMovingMidTurn = false
            };
            var planBlue = MonsterPathEngine.Plan(board, monsterFrom22, players, new PlanOptions { MaxSteps = 3 });
            Assert.Single(planBlue.Forks);
            Assert.Equal(ForkReasonKind.ForkStartBlue, planBlue.Forks[0].ReasonKind);
            Assert.Equal(55, planBlue.Forks[0].Exits[0].NextLandId);
        }

        [Fact]
        public void PostCombatOnForkFollowsRedBlueEvenWhileMidTurn()
        {
            var board = BuildTJunctionBoard();
            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = 55, IsAlive = true, IsTargetable = true }
            };

            var monster = new MonsterState
            {
                MonsterId = 101,
                Name = "玩具恐龙",
                CurrentLandId = 23,
                FromLandId = 24,
                IsCurrentlyMovingMidTurn = true,
                JustFoughtOnCurrentFork = true
            };

            var plan = MonsterPathEngine.Plan(board, monster, players, new PlanOptions { MaxSteps = 2 });
            Assert.Single(plan.Forks);
            Assert.Equal(ForkReasonKind.PostCombatRed, plan.Forks[0].ReasonKind);
            Assert.Equal(22, plan.Forks[0].Exits[0].NextLandId);
        }

        [Fact]
        public void MidWalkEqualDistanceTieBreaksBySeatOrderP1ToP4NotRedArrow()
        {
            // Canonical Bilibili diff test:
            // Monster walks 25 -> 24 -> 23 (mid-walk!).
            // P1 (Slot 0) is at tile 56 (distance 2 via BLUE arrow 55).
            // P3 (Slot 2) is at tile 21 (distance 2 via RED arrow 22).
            // Bilibili rule: same distance breaks ties by P1 -> P4 (NOT Red arrow) -> must chase P1 via 55!
            var board = BuildTJunctionBoard();
            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 3, Slot = 2, LandId = 21, IsAlive = true, IsTargetable = true },
                new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = 56, IsAlive = true, IsTargetable = true }
            };

            var monster = new MonsterState
            {
                MonsterId = 102,
                Name = "玩具恐龙",
                CurrentLandId = 24,
                FromLandId = 25
            };

            var plan = MonsterPathEngine.Plan(board, monster, players, new PlanOptions { MaxSteps = 3 });
            Assert.Single(plan.Forks);
            var fork = plan.Forks[0];
            Assert.Equal(23, fork.ForkLandId);
            Assert.Equal(ForkReasonKind.MidForkChasePlayer, fork.ReasonKind);
            Assert.Single(fork.Exits);
            Assert.Equal(55, fork.Exits[0].NextLandId);
            Assert.Equal(1.0, fork.Exits[0].ConditionalProbability, 6);
        }

        [Fact]
        public void MidWalkSkipsUTurnCandidatesAndChasesNextPlayer()
        {
            // Mirrors Bilibili video 森秋幻境 第9轮:
            // P1 (closest, behind monster -> U-turn) -> skipped
            // P4 (2nd closest, behind monster -> U-turn) -> skipped
            // P3 (3rd closest, forward on 55 branch) -> chased 100%!
            var board = new BoardGraph(new[]
            {
                new LandNodeSnapshot(1, new[] { 2 }),
                new LandNodeSnapshot(2, new[] { 1, 3 }),
                new LandNodeSnapshot(3, new[] { 2, 4 }),
                new LandNodeSnapshot(4, new[] { 3, 23 }),
                new LandNodeSnapshot(23, new[] { 22, 55, 4 }),
                new LandNodeSnapshot(22, new[] { 23, 21 }),
                new LandNodeSnapshot(21, new[] { 22 }),
                new LandNodeSnapshot(55, new[] { 23, 56 }),
                new LandNodeSnapshot(56, new[] { 55, 57 }),
                new LandNodeSnapshot(57, new[] { 56, 58 }),
                new LandNodeSnapshot(58, new[] { 57 })
            });

            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = 3, IsAlive = true, IsTargetable = true },  // dist 2 (U-turn)
                new TrackedPlayer { PlayerId = 4, Slot = 3, LandId = 2, IsAlive = true, IsTargetable = true },  // dist 3 (U-turn)
                new TrackedPlayer { PlayerId = 3, Slot = 2, LandId = 58, IsAlive = true, IsTargetable = true }  // dist 4 (forward via 55)
            };

            var monster = new MonsterState
            {
                MonsterId = 103,
                CurrentLandId = 4,
                FromLandId = 3
            };

            var plan = MonsterPathEngine.Plan(board, monster, players, new PlanOptions { MaxSteps = 3 });
            Assert.Single(plan.Forks);
            Assert.Equal(23, plan.Forks[0].ForkLandId);
            Assert.Single(plan.Forks[0].Exits);
            Assert.Equal(55, plan.Forks[0].Exits[0].NextLandId);
            Assert.Equal(1.0, plan.Forks[0].Exits[0].ConditionalProbability, 6);
        }

        [Fact]
        public void MidWalkAllUTurnFallbackSplitsFiftyFifty()
        {
            var board = BuildTJunctionBoard();
            // Only player is behind the monster on tile 25 -> requires U-turn at fork 23 -> 50% / 50%
            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = 25, IsAlive = true, IsTargetable = true }
            };

            var monster = new MonsterState
            {
                MonsterId = 104,
                CurrentLandId = 24,
                FromLandId = 25
            };

            var plan = MonsterPathEngine.Plan(board, monster, players, new PlanOptions { MaxSteps = 3 });
            Assert.Single(plan.Forks);
            var fork = plan.Forks[0];
            Assert.Equal(ForkReasonKind.MidForkAllUTurnFallback, fork.ReasonKind);
            Assert.Equal(2, fork.Exits.Count);
            Assert.Equal(0.5, fork.Exits.Single(e => e.NextLandId == 22).ConditionalProbability, 6);
            Assert.Equal(0.5, fork.Exits.Single(e => e.NextLandId == 55).ConditionalProbability, 6);
        }

        [Fact]
        public void LoopMultiShortestPathMatchesSenqiuFork23Exact62Point5Percent()
        {
            // Exact mathematical reproduction of Bilibili video Section 3 (森秋幻境 23号岔路):
            // From fork 23 (arriving from 24) to target T=4 (distance 4):
            //   - Via 55: 2 distinct shortest paths (23->55->101->102->4 and 23->55->103->102->4) -> 2 lots
            //   - Via 22: 1 shortest path (23->22->201->202->4) -> 1 lot
            //   - Via 24 (U-turn): 1 shortest path (23->24->301->302->4) -> 1 lot
            // Total = 4 lots.
            //   draw 55: 2/4 = 0.50
            //   draw 22: 1/4 = 0.25
            //   draw U-turn 24: 1/4 -> no next player -> splits equally +1/8 each
            // Exact result: 55 = 5/8 = 62.5%, 22 = 3/8 = 37.5%!
            var board = new BoardGraph(new[]
            {
                new LandNodeSnapshot(23, new[] { 22, 55, 24 }),
                // 55 branch (2 shortest paths to 4)
                new LandNodeSnapshot(55, new[] { 23, 101, 103 }),
                new LandNodeSnapshot(101, new[] { 55, 102 }),
                new LandNodeSnapshot(103, new[] { 55, 102 }),
                new LandNodeSnapshot(102, new[] { 101, 103, 4 }),
                // 22 branch (1 shortest path to 4)
                new LandNodeSnapshot(22, new[] { 23, 201 }),
                new LandNodeSnapshot(201, new[] { 22, 202 }),
                new LandNodeSnapshot(202, new[] { 201, 4 }),
                // 24 incoming branch (1 shortest path to 4 via U-turn)
                new LandNodeSnapshot(24, new[] { 23, 301 }),
                new LandNodeSnapshot(301, new[] { 24, 302 }),
                new LandNodeSnapshot(302, new[] { 301, 4 }),
                // Target tile 4
                new LandNodeSnapshot(4, new[] { 102, 202, 302 })
            });

            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = 4, IsAlive = true, IsTargetable = true }
            };

            // Monster starts at 24 moving toward 23 (from 301 -> 24 -> 23)
            var monster = new MonsterState
            {
                MonsterId = 105,
                CurrentLandId = 24,
                FromLandId = 301
            };

            var plan = MonsterPathEngine.Plan(board, monster, players, new PlanOptions { MaxSteps = 3 });
            var fork23 = plan.Forks.Single(f => f.ForkLandId == 23);
            Assert.True(fork23.HasMultipleBranches);

            var exit55 = fork23.Exits.Single(e => e.NextLandId == 55);
            var exit22 = fork23.Exits.Single(e => e.NextLandId == 22);
            Assert.Equal(0.625, exit55.ConditionalProbability, 6);
            Assert.Equal(0.375, exit22.ConditionalProbability, 6);
            Assert.Equal("62.5%", exit55.FormatConditionalPercent());
            Assert.Equal("37.5%", exit22.FormatConditionalPercent());
        }

        [Fact]
        public void PartialUTurnProbabilityCascadesToSecondPlayer()
        {
            // From fork 10 (arriving from 1):
            //   - P1 at tile 5 is at distance 2 via TWO shortest paths: 10->2->5 (forward) and 10->1->5 (U-turn)
            //     -> 50% goes to 2, 50% U-turns and cascades to P2!
            //   - P2 at tile 30 is at distance 3 via 10->3->20->30 (forward)
            //     -> receives the remaining 50% on exit 3!
            var board = new BoardGraph(new[]
            {
                new LandNodeSnapshot(0, new[] { 1 }),
                new LandNodeSnapshot(1, new[] { 0, 10, 5 }),
                new LandNodeSnapshot(10, new[] { 2, 3, 1 }),
                new LandNodeSnapshot(2, new[] { 10, 5 }),
                new LandNodeSnapshot(5, new[] { 1, 2 }),
                new LandNodeSnapshot(3, new[] { 10, 20 }),
                new LandNodeSnapshot(20, new[] { 3, 30 }),
                new LandNodeSnapshot(30, new[] { 20 })
            });

            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = 5, IsAlive = true, IsTargetable = true },
                new TrackedPlayer { PlayerId = 2, Slot = 1, LandId = 30, IsAlive = true, IsTargetable = true }
            };

            var monster = new MonsterState { MonsterId = 106, CurrentLandId = 1, FromLandId = 0 };
            var plan = MonsterPathEngine.Plan(board, monster, players, new PlanOptions { MaxSteps = 3 });
            var fork = plan.Forks.Single(f => f.ForkLandId == 10);

            Assert.Equal(0.5, fork.Exits.Single(e => e.NextLandId == 2).ConditionalProbability, 6);
            Assert.Equal(0.5, fork.Exits.Single(e => e.NextLandId == 3).ConditionalProbability, 6);
        }

        [Fact]
        public void SpawnOnForkFollowsExplicitFrontNodeArrow()
        {
            var board = BuildTJunctionBoard();
            var monster = new MonsterState
            {
                MonsterId = 107,
                CurrentLandId = 23,
                FromLandId = -1,
                FrontNodeIds = new[] { 55 }, // Explicit spawn triangle points to Blue (55), overriding Red (22)
                JustSpawnedOnFork = true
            };

            var plan = MonsterPathEngine.Plan(board, monster, new List<TrackedPlayer>(), new PlanOptions { MaxSteps = 2 });
            Assert.Single(plan.Forks);
            Assert.Equal(ForkReasonKind.SpawnDirection, plan.Forks[0].ReasonKind);
            Assert.Equal(55, plan.Forks[0].Exits[0].NextLandId);
        }

        [Fact]
        public void SkillDisplacedMonstersFollowExactRulesForTrashcanShirleyLingling()
        {
            var board = BuildTJunctionBoard();
            var noPlayers = new List<TrackedPlayer>();

            // 1. 垃圾桶拉到岔路口 -> 一定走红 (22)
            var trashcanFork = new MonsterState
            {
                MonsterId = 201,
                CurrentLandId = 23,
                SkillMoveKind = SkillMoveKind.TrashcanPull
            };
            var p1 = MonsterPathEngine.Plan(board, trashcanFork, noPlayers, new PlanOptions { MaxSteps = 1 });
            Assert.Single(p1.Forks[0].Exits);
            Assert.Equal(22, p1.Forks[0].Exits[0].NextLandId);

            // 2. 垃圾桶拉到普通格 -> 两方向随机 50%/50%
            var trashcanNormal = new MonsterState
            {
                MonsterId = 202,
                CurrentLandId = 24,
                SkillMoveKind = SkillMoveKind.TrashcanPull
            };
            var p2 = MonsterPathEngine.Plan(board, trashcanNormal, noPlayers, new PlanOptions { MaxSteps = 1 });
            Assert.Equal(2, p2.Forks[0].Exits.Count);
            Assert.All(p2.Forks[0].Exits, e => Assert.Equal(0.5, e.ConditionalProbability, 6));

            // 3. 雪莉扔到岔路口 -> 三个方向等概率随机 (1/3 each, ignoring Red/Blue)
            var shirleyFork = new MonsterState
            {
                MonsterId = 203,
                CurrentLandId = 23,
                SkillMoveKind = SkillMoveKind.ShirleyThrow
            };
            var p3 = MonsterPathEngine.Plan(board, shirleyFork, noPlayers, new PlanOptions { MaxSteps = 1 });
            Assert.Equal(3, p3.Forks[0].Exits.Count);
            Assert.All(p3.Forks[0].Exits, e => Assert.Equal(1.0 / 3.0, e.ConditionalProbability, 6));

            // 4. 玲玲推到普通格 -> 跟着推的方向
            var linglingNormal = new MonsterState
            {
                MonsterId = 204,
                CurrentLandId = 24,
                SkillMoveKind = SkillMoveKind.LinglingPush,
                SkillMovePushDirectionLandId = 23
            };
            var p4 = MonsterPathEngine.Plan(board, linglingNormal, noPlayers, new PlanOptions { MaxSteps = 1 });
            Assert.Single(p4.Forks[0].Exits);
            Assert.Equal(23, p4.Forks[0].Exits[0].NextLandId);

            // 5. 玲玲推到岔路口（从红箭 22 方向推入） -> 排除来路 22，无红走蓝 (55)
            var linglingFork = new MonsterState
            {
                MonsterId = 205,
                CurrentLandId = 23,
                SkillMoveKind = SkillMoveKind.LinglingPush,
                SkillMovePushFromLandId = 22
            };
            var p5 = MonsterPathEngine.Plan(board, linglingFork, noPlayers, new PlanOptions { MaxSteps = 1 });
            Assert.Single(p5.Forks[0].Exits);
            Assert.Equal(55, p5.Forks[0].Exits[0].NextLandId);
        }

        [Fact]
        public void DeadAndUntargetablePlayersAreExcludedFromTracking()
        {
            var board = BuildTJunctionBoard();
            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = 55, IsAlive = false, IsTargetable = true },  // dead
                new TrackedPlayer { PlayerId = 2, Slot = 1, LandId = 56, IsAlive = true, IsTargetable = false },  // stealth / NotSelect
                new TrackedPlayer { PlayerId = 3, Slot = 2, LandId = 20, IsAlive = true, IsTargetable = true }    // alive & trackable via 22
            };

            var monster = new MonsterState { MonsterId = 108, CurrentLandId = 24, FromLandId = 25 };
            var plan = MonsterPathEngine.Plan(board, monster, players, new PlanOptions { MaxSteps = 3 });
            Assert.Single(plan.Forks[0].Exits);
            Assert.Equal(22, plan.Forks[0].Exits[0].NextLandId);
        }

        [Fact]
        public void MultiStepXHorizonPreservesExactProbabilityConservation()
        {
            // Two consecutive forks where every player is behind -> 50/50 at fork 1, then another 50/50 at fork 2
            var board = new BoardGraph(new[]
            {
                new LandNodeSnapshot(1, new[] { 2 }),
                new LandNodeSnapshot(2, new[] { 1, 3 }),
                new LandNodeSnapshot(3, new[] { 4, 5, 2 }),  // fork 1
                new LandNodeSnapshot(4, new[] { 3, 6 }),
                new LandNodeSnapshot(6, new[] { 4, 7 }),
                new LandNodeSnapshot(7, new[] { 6 }),
                new LandNodeSnapshot(5, new[] { 8, 9, 3 }),  // fork 2
                new LandNodeSnapshot(8, new[] { 5, 10 }),
                new LandNodeSnapshot(10, new[] { 8 }),
                new LandNodeSnapshot(9, new[] { 5, 11 }),
                new LandNodeSnapshot(11, new[] { 9 })
            });

            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = 1, IsAlive = true, IsTargetable = true }
            };

            var monster = new MonsterState { MonsterId = 109, CurrentLandId = 2, FromLandId = 1 };
            var plan = MonsterPathEngine.Plan(board, monster, players, new PlanOptions { MaxSteps = 4 });

            // Total endpoint probabilities at step 4 must sum to 1.0 (0.5 on 7, 0.25 on 10, 0.25 on 11)
            double totalProb = plan.Endpoints.Sum(e => e.Probability);
            Assert.Equal(1.0, totalProb, 6);
            Assert.Equal(0.5, plan.Endpoints.Single(e => e.LandId == 7).Probability, 6);
            Assert.Equal(0.25, plan.Endpoints.Single(e => e.LandId == 10).Probability, 6);
            Assert.Equal(0.25, plan.Endpoints.Single(e => e.LandId == 11).Probability, 6);
        }

        [Fact]
        public void MidWalkHittingPlayerStandingOnForkAppliesPostCombatRedBlue()
        {
            var board = BuildTJunctionBoard();
            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = 23, IsAlive = true, IsTargetable = true },
                new TrackedPlayer { PlayerId = 2, Slot = 1, LandId = 55, IsAlive = true, IsTargetable = true }
            };

            var monster = new MonsterState { MonsterId = 110, CurrentLandId = 24, FromLandId = 25 };
            var plan = MonsterPathEngine.Plan(board, monster, players, new PlanOptions
            {
                MaxSteps = 3,
                SimulateCombatOnFork = true
            });

            var fork = plan.Forks.Single(f => f.ForkLandId == 23);
            Assert.Equal(ForkReasonKind.PostCombatRed, fork.ReasonKind);
            Assert.Single(fork.Exits);
            Assert.Equal(22, fork.Exits[0].NextLandId);
        }

        [Fact]
        public void PostCombatModesHandleEncounterOnNormalTileBeforeSecondFork()
        {
            // Reproduces in-game screenshot scenario:
            //   1 -> 2 -> Fork1(3) -> 4(P1 stands here!) -> 5 -> Fork2(6)
            //   Fork2(6) raw neighbors: [7 (Red), 8 (Blue), 5 (incoming)]
            //   P2 stands on Blue branch (tile 9 via 8).
            var board = new BoardGraph(new[]
            {
                new LandNodeSnapshot(1, new[] { 2 }),
                new LandNodeSnapshot(2, new[] { 1, 3 }),
                new LandNodeSnapshot(3, new[] { 4, 10, 2 }),
                new LandNodeSnapshot(10, new[] { 3 }),
                new LandNodeSnapshot(4, new[] { 3, 5 }),
                new LandNodeSnapshot(5, new[] { 4, 6 }),
                new LandNodeSnapshot(6, new[] { 7, 8, 5 }), // Red=7, Blue=8
                new LandNodeSnapshot(7, new[] { 6, 11 }),
                new LandNodeSnapshot(11, new[] { 7 }),
                new LandNodeSnapshot(8, new[] { 6, 9 }),
                new LandNodeSnapshot(9, new[] { 8 })
            });

            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = 4, IsAlive = true, IsTargetable = true },
                new TrackedPlayer { PlayerId = 2, Slot = 1, LandId = 9, IsAlive = true, IsTargetable = true }
            };

            var monster = new MonsterState { MonsterId = 301, CurrentLandId = 2, FromLandId = 1 };

            // 1. ContinueByGuide (default): marks combat at tile 4, continues past it with IsAfterCombat=true,
            //    and at Fork2(6) skips behind-player P1 to follow P2 on Blue(8) with "续行→P2(2格)"
            var planContinue = MonsterPathEngine.Plan(board, monster, players, new PlanOptions
            {
                MaxSteps = 6,
                PostCombatMode = PostCombatMode.ContinueByGuide
            });
            Assert.Single(planContinue.Combats);
            Assert.Equal(4, planContinue.Combats[0].LandId);
            Assert.Equal("P1", planContinue.Combats[0].TargetLabel);
            var fork2Continue = planContinue.Forks.Single(f => f.ForkLandId == 6);
            Assert.True(fork2Continue.IsAfterCombat);
            Assert.Equal(8, fork2Continue.Exits[0].NextLandId);
            Assert.StartsWith("续行→P2", fork2Continue.SummaryLabel);
            Assert.True(planContinue.Edges.Single(e => e.FromLandId == 6 && e.ToLandId == 8).IsAfterCombat);
            Assert.False(planContinue.Edges.Single(e => e.FromLandId == 2 && e.ToLandId == 3).IsAfterCombat);

            // 2. StopAtFirstCombat: truncates right at tile 4 (zero edges past tile 4, Fork2 never evaluated)
            var planStop = MonsterPathEngine.Plan(board, monster, players, new PlanOptions
            {
                MaxSteps = 6,
                PostCombatMode = PostCombatMode.StopAtFirstCombat
            });
            Assert.Single(planStop.Combats);
            Assert.Equal(4, planStop.Combats[0].LandId);
            Assert.DoesNotContain(planStop.Forks, f => f.ForkLandId == 6);
            Assert.Single(planStop.Endpoints);
            Assert.True(planStop.Endpoints[0].StoppedAtCombat);
            Assert.Equal(4, planStop.Endpoints[0].LandId);

            // 3. RedBlueAfterCombat: at Fork2(6) ignores P2 on Blue(8) and takes Red(7)
            var planRedBlue = MonsterPathEngine.Plan(board, monster, players, new PlanOptions
            {
                MaxSteps = 6,
                PostCombatMode = PostCombatMode.RedBlueAfterCombat
            });
            var fork2Red = planRedBlue.Forks.Single(f => f.ForkLandId == 6);
            Assert.True(fork2Red.IsAfterCombat);
            Assert.Equal(ForkReasonKind.PostCombatRed, fork2Red.ReasonKind);
            Assert.Equal(7, fork2Red.Exits[0].NextLandId);
        }
    }
}
