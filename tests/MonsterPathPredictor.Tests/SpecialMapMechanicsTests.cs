using System.Collections.Generic;
using System.Linq;
using MonsterPathPredictor.Core;
using Xunit;

namespace MonsterPathPredictor.Tests
{
    public sealed class SpecialMapMechanicsTests
    {
        private static BoardGraph BuildForkBoard()
        {
            // Straight approach: 10 - 11 - 28(fork: [33 Red, 27 Blue, 30 None])
            // Branch 33: 28 - 33 - 34 - 35
            // Branch 27: 28 - 27 - 26
            // Branch 30: 28 - 30 - 31
            return new BoardGraph(new[]
            {
                new LandNodeSnapshot(28, new[] { 33, 27, 30 }),
                new LandNodeSnapshot(33, new[] { 28, 34 }),
                new LandNodeSnapshot(34, new[] { 33, 35 }),
                new LandNodeSnapshot(35, new[] { 34 }),
                new LandNodeSnapshot(27, new[] { 28, 26 }),
                new LandNodeSnapshot(26, new[] { 27 }),
                new LandNodeSnapshot(30, new[] { 28, 31 }),
                new LandNodeSnapshot(31, new[] { 30 })
            });
        }

        [Fact]
        public void MagicAcademySecurityOfficerReversesAtTurnStartOnForkTowardProblemStudent()
        {
            var board = BuildForkBoard();
            var officer = new MonsterState
            {
                MonsterId = 6163,
                HeroId = 1018,
                CurrentLandId = 28,
                FromLandId = 33,
                FrontNodeIds = new[] { 27, 30 },
                ChasesProblemStudent = true
            };
            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = 26 },
                new TrackedPlayer { PlayerId = 2, Slot = 1, LandId = 35, HasProblemStudentMark = true }
            };

            var plan = MonsterPathEngine.Plan(board, officer, players, new PlanOptions { MaxSteps = 3 });
            var fork = plan.Forks.Single(f => f.ForkLandId == 28);
            Assert.Equal(ForkReasonKind.MarkedTargetChase, fork.ReasonKind);
            Assert.Single(fork.Exits);
            Assert.Equal(33, fork.Exits[0].NextLandId);
        }

        [Fact]
        public void MagicAcademySecurityOfficerReversesOnStraightRoadAndClearsMarkAfterCombat()
        {
            var board = BuildForkBoard();
            var officer = new MonsterState
            {
                MonsterId = 6164,
                HeroId = 1018,
                CurrentLandId = 34,
                FromLandId = 33,
                FrontNodeIds = new[] { 35 },
                ChasesProblemStudent = true
            };
            // Problem student P2 sits behind at 33; non-marked P1 sits on branch 30 (tile 31) and P3 on branch 27 (tile 26).
            // Officer reverses 34 -> 33 (fights P2, clearing ProblemStudent mark), then reaches fork 28 -> chases closest remaining P1/P3!
            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = 27 },
                new TrackedPlayer { PlayerId = 2, Slot = 1, LandId = 33, HasProblemStudentMark = true }
            };

            var plan = MonsterPathEngine.Plan(board, officer, players, new PlanOptions { MaxSteps = 4 });
            Assert.Contains(plan.Edges, e => e.FromLandId == 34 && e.ToLandId == 33);
            Assert.Contains(plan.Combats, c => c.LandId == 33 && !c.IsSteal);
            var fork28 = plan.Forks.Single(f => f.ForkLandId == 28);
            Assert.Single(fork28.Exits);
            Assert.Equal(27, fork28.Exits[0].NextLandId);
        }

        [Fact]
        public void GardenBronzeMandarinDuckReversesAtTurnStartTowardMarkedPlayer()
        {
            var board = BuildForkBoard();
            var duck = new MonsterState
            {
                MonsterId = 7001,
                HeroId = 1047,
                CurrentLandId = 34,
                FromLandId = 33,
                FrontNodeIds = new[] { 35 },
                ChasesMandarinDuckMark = true
            };
            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = 35 },
                new TrackedPlayer { PlayerId = 2, Slot = 1, LandId = 27, HasMandarinDuckMark = true }
            };

            var plan = MonsterPathEngine.Plan(board, duck, players, new PlanOptions { MaxSteps = 3 });
            Assert.Contains(plan.Edges, e => e.FromLandId == 34 && e.ToLandId == 33);
            Assert.Equal(27, plan.Forks.Single(f => f.ForkLandId == 28).Exits[0].NextLandId);
        }

        [Fact]
        public void GardenBronzeMandarinDuckUTurnsPreCombatMidWalkButNotPostCombatOrOntoTurnStartTile()
        {
            var board = BuildForkBoard();
            // 1. Pre-combat mid-walk at fork 28 (coming from 33, started at 34): marked P1@33 -> U-turns back to 33!
            var preCombat = new MonsterState
            {
                MonsterId = 7002,
                HeroId = 1047,
                CurrentLandId = 28,
                FromLandId = 33,
                TurnStartLandId = 34,
                IsCurrentlyMovingMidTurn = true,
                HasLaunchedFightThisTurn = false,
                ChasesMandarinDuckMark = true
            };
            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = 33, HasMandarinDuckMark = true },
                new TrackedPlayer { PlayerId = 2, Slot = 1, LandId = 26, HasMandarinDuckMark = true }
            };
            var planPre = MonsterPathEngine.Plan(board, preCombat, players, new PlanOptions { MaxSteps = 1 });
            Assert.Equal(33, planPre.Forks.Single(f => f.ForkLandId == 28).Exits[0].NextLandId);

            // 2. If P1@33 was standing on the monster's TurnStartLandId (33) -> skipped, continues forward to P2@26 (27)!
            preCombat.TurnStartLandId = 33;
            var planSkipStart = MonsterPathEngine.Plan(board, preCombat, players, new PlanOptions { MaxSteps = 1 });
            Assert.Equal(27, planSkipStart.Forks.Single(f => f.ForkLandId == 28).Exits[0].NextLandId);

            // 3a. After fighting an UNMARKED non-BossTarget player (BossTargetClearedThisTurn == false):
            //     1047 retains locked BossTargetId and STILL U-turns back toward P1@33!
            preCombat.TurnStartLandId = 34;
            preCombat.HasLaunchedFightThisTurn = true;
            preCombat.BossTargetPlayerId = 1;
            preCombat.BossTargetClearedThisTurn = false;
            var planPostUnmarked = MonsterPathEngine.Plan(board, preCombat, players, new PlanOptions { MaxSteps = 1 });
            Assert.Equal(33, planPostUnmarked.Forks.Single(f => f.ForkLandId == 28).Exits[0].NextLandId);

            // 3b. After fighting BossTargetId / a marked player (BossTargetClearedThisTurn == true):
            //     BossTargetId is cleared for the rest of the turn -> no more U-turning, continues forward to P2@26 (27)!
            preCombat.BossTargetPlayerId = 0;
            preCombat.BossTargetClearedThisTurn = true;
            var planPostCleared = MonsterPathEngine.Plan(board, preCombat, players, new PlanOptions { MaxSteps = 1 });
            Assert.Equal(27, planPostCleared.Forks.Single(f => f.ForkLandId == 28).Exits[0].NextLandId);
        }

        [Fact]
        public void LibraryWardenIgnoresZeroCrimePlayersForBothForkAndCombat()
        {
            var board = BuildForkBoard();
            var warden = new MonsterState
            {
                MonsterId = 8001,
                HeroId = 1067,
                CurrentLandId = 33,
                FromLandId = 34,
                RequiresCrimeTarget = true
            };
            // P1@27 has CrimeNum=0 (closer, right on branch 27) and P3@28 has CrimeNum=0 (on the fork itself).
            // P2@31 has CrimeNum=2 (farther, branch 30). Warden ignores P3@28 (no combat!) and chases P2 via 30!
            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = 27, CrimeNum = 0 },
                new TrackedPlayer { PlayerId = 2, Slot = 1, LandId = 31, CrimeNum = 2 },
                new TrackedPlayer { PlayerId = 3, Slot = 2, LandId = 28, CrimeNum = 0 }
            };

            var plan = MonsterPathEngine.Plan(board, warden, players, new PlanOptions { MaxSteps = 4 });
            Assert.DoesNotContain(plan.Combats, c => c.LandId == 28);
            Assert.Contains(plan.Combats, c => c.LandId == 31);
            var fork = plan.Forks.Single(f => f.ForkLandId == 28);
            Assert.Single(fork.Exits);
            Assert.Equal(30, fork.Exits[0].NextLandId);
        }

        [Fact]
        public void LibraryCrimePursuerDoesNotCascadePastBehindNearestCulprit()
        {
            var board = BuildForkBoard();
            var guard = new MonsterState
            {
                MonsterId = 8002,
                HeroId = 1068,
                CurrentLandId = 33,
                FromLandId = 34,
                RequiresCrimeTarget = true
            };
            // Entering fork 28 from 33: nearest culprit P1@33 (d=1, strictly behind!) vs farther culprit P2@31 (d=2, forward via 30).
            // Crime-restricted pursuer does NOT cascade to strictly farther culprit -> splits 50%/50% between 27 and 30.
            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = 33, CrimeNum = 1 },
                new TrackedPlayer { PlayerId = 2, Slot = 1, LandId = 31, CrimeNum = 3 }
            };

            var plan = MonsterPathEngine.Plan(board, guard, players, new PlanOptions { MaxSteps = 3 });
            var fork = plan.Forks.Single(f => f.ForkLandId == 28);
            Assert.Equal(2, fork.Exits.Count);
            Assert.Equal(0.5, fork.Exits[0].ConditionalProbability, 6);
            Assert.Equal(0.5, fork.Exits[1].ConditionalProbability, 6);
        }

        [Fact]
        public void LibraryCrimePursuerDoesNotCascadeEvenToTiedHigherSlotForwardCulprit()
        {
            var board = BuildForkBoard();
            var warden = new MonsterState
            {
                MonsterId = 8004,
                HeroId = 1067,
                CurrentLandId = 33,
                FromLandId = 34,
                RequiresCrimeTarget = true
            };
            // Verified on 82016 (1791712855646387 R5 F1676 & 1791792631456661 R5 F1858):
            // Entering fork 28 from 33: P1@33 (Slot 0, d=1, BEHIND) ties at d=1 with P2@27 (Slot 1, d=1, FORWARD).
            // Top-ranked target is P1 (Slot 0 < Slot 1), which lies strictly behind -> no cascade, splits 50%/50%.
            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = 33, CrimeNum = 1 },
                new TrackedPlayer { PlayerId = 2, Slot = 1, LandId = 27, CrimeNum = 2 }
            };

            var plan = MonsterPathEngine.Plan(board, warden, players, new PlanOptions { MaxSteps = 3 });
            var fork = plan.Forks.Single(f => f.ForkLandId == 28);
            Assert.Equal(2, fork.Exits.Count);
            Assert.Equal(0.5, fork.Exits[0].ConditionalProbability, 6);
            Assert.Equal(0.5, fork.Exits[1].ConditionalProbability, 6);
        }

        [Fact]
        public void LibraryGuardAfterFiveDamageRevertsToFullAggro()
        {
            var board = BuildForkBoard();
            var enragedGuard = new MonsterState
            {
                MonsterId = 8003,
                HeroId = 1068,
                CurrentLandId = 33,
                FromLandId = 34,
                RequiresCrimeTarget = false // buff 10681202 active (or 1067 after 真凶揭露)
            };
            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = 27, CrimeNum = 0 },
                new TrackedPlayer { PlayerId = 2, Slot = 1, LandId = 31, CrimeNum = 3 }
            };

            var plan = MonsterPathEngine.Plan(board, enragedGuard, players, new PlanOptions { MaxSteps = 3 });
            var fork = plan.Forks.Single(f => f.ForkLandId == 28);
            Assert.Single(fork.Exits);
            Assert.Equal(27, fork.Exits[0].NextLandId);
            Assert.Contains(plan.Combats, c => c.LandId == 27);
        }
    }
}
