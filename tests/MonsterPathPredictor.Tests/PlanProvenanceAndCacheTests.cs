using System;
using System.Collections.Generic;
using MonsterPathPredictor.Core;
using Xunit;

namespace MonsterPathPredictor.Tests
{
    public class PlanProvenanceAndCacheTests
    {
        [Fact]
        public void SplitProvenancePreservesPreForkDeterminismOnLoopAndMarksReconvergedContinuationCertain()
        {
            // Diamond reconvergence: 0 -> 1 -> 2 (fork) -> {3, 4} -> 5 -> 6 -> 7 (P1 at 5 or 7)
            var diamondBoard = new BoardGraph(new[]
            {
                new LandNodeSnapshot(0, new[] { 1 }),
                new LandNodeSnapshot(1, new[] { 0, 2 }),
                new LandNodeSnapshot(2, new[] { 3, 4, 1 }),
                new LandNodeSnapshot(3, new[] { 2, 5 }),
                new LandNodeSnapshot(4, new[] { 2, 5 }),
                new LandNodeSnapshot(5, new[] { 3, 4, 6 }),
                new LandNodeSnapshot(6, new[] { 5, 7 }),
                new LandNodeSnapshot(7, new[] { 6 })
            });

            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, Name = "P1", LandId = 7, IsAlive = true, IsTargetable = true }
            };
            var monster = new MonsterState
            {
                MonsterId = 9001,
                Name = "史莱姆",
                DisplayName = "史莱姆",
                CurrentLandId = 1,
                FromLandId = 0,
                FrontNodeIds = new[] { 2 },
                IsNormalAggressive = true
            };

            var plan = MonsterPathEngine.Plan(diamondBoard, monster, players, new PlanOptions { MaxSteps = 6 });

            // Pre-fork edge 1 -> 2 is 100% mathematically certain
            var e12 = Assert.Single(plan.Edges, e => e.FromLandId == 1 && e.ToLandId == 2);
            Assert.False(e12.HasNonCertainDominantSplit);

            // Split edges 2 -> 3 and 2 -> 4 are 50/50 non-certain
            var e23 = Assert.Single(plan.Edges, e => e.FromLandId == 2 && e.ToLandId == 3);
            var e24 = Assert.Single(plan.Edges, e => e.FromLandId == 2 && e.ToLandId == 4);
            Assert.True(e23.HasNonCertainDominantSplit);
            Assert.True(e24.HasNonCertainDominantSplit);

            // After 50% + 50% reconverge at tile 5 -> continuation edge 5 -> 6 and tile 5/6 are 100% certain!
            var e56 = Assert.Single(plan.Edges, e => e.FromLandId == 5 && e.ToLandId == 6);
            Assert.False(e56.HasNonCertainDominantSplit);
            var ts5 = Assert.Single(plan.TileSteps, t => t.LandId == 5);
            Assert.False(ts5.HasNonCertainDominantSplit);
        }

        [Fact]
        public void SplitLoopingBackToPreForkTileNeverMarksEarlierDeterministicDepartureAsSplit()
        {
            // Directed loop: 0 -> 1 -> 2 (fork: 3 loops back to 1, 4 goes to 5)
            // No players -> mid-walk at fork 2 splits 50/50 between 3 and 4!
            // Branch 3 returns to tile 1 at step 3, while step 0 (1->2) was strictly deterministic.
            var loopBoard = new BoardGraph(new[]
            {
                new LandNodeSnapshot(0, new[] { 1 }),
                new LandNodeSnapshot(1, new[] { 2 }),
                new LandNodeSnapshot(2, new[] { 3, 4 }),
                new LandNodeSnapshot(3, new[] { 1 }),
                new LandNodeSnapshot(4, new[] { 5 }),
                new LandNodeSnapshot(5, new[] { 4 })
            });

            var monster = new MonsterState
            {
                MonsterId = 9002,
                Name = "史莱姆",
                DisplayName = "史莱姆",
                CurrentLandId = 1,
                FromLandId = 0,
                FrontNodeIds = new[] { 2 },
                IsNormalAggressive = true
            };

            var plan = MonsterPathEngine.Plan(loopBoard, monster, Array.Empty<TrackedPlayer>(), new PlanOptions { MaxSteps = 5 });

            // Pre-fork departure 1 -> 2 was strictly deterministic at step 0
            var e12 = Assert.Single(plan.Edges, e => e.FromLandId == 1 && e.ToLandId == 2);
            Assert.False(e12.HasNonCertainDominantSplit);
            var ts1 = Assert.Single(plan.TileSteps, t => t.LandId == 1);
            Assert.False(ts1.HasNonCertainDominantSplit);

            // Post-fork branches remain non-certain
            var e23 = Assert.Single(plan.Edges, e => e.FromLandId == 2 && e.ToLandId == 3);
            var e24 = Assert.Single(plan.Edges, e => e.FromLandId == 2 && e.ToLandId == 4);
            Assert.True(e23.HasNonCertainDominantSplit);
            Assert.True(e24.HasNonCertainDominantSplit);
        }

        [Fact]
        public void InputSignatureDetectsEveryMonsterPlayerAndOptionMutationThatChangesPlanOutput()
        {
            var board = new BoardGraph(new[]
            {
                new LandNodeSnapshot(0, new[] { 1 }),
                new LandNodeSnapshot(1, new[] { 0, 2 }),
                new LandNodeSnapshot(2, new[] { 3, 4, 1 }),
                new LandNodeSnapshot(3, new[] { 2, 5 }),
                new LandNodeSnapshot(4, new[] { 2, 6 }),
                new LandNodeSnapshot(5, new[] { 3 }),
                new LandNodeSnapshot(6, new[] { 4 })
            });

            MonsterState BaseMonster() => new MonsterState
            {
                MonsterId = 500,
                HeroId = 1018,
                Name = "安全官",
                DisplayName = "安全官",
                CurrentLandId = 1,
                FromLandId = 0,
                FrontNodeIds = new[] { 2 },
                IsNormalAggressive = true,
                ChasesProblemStudent = true
            };

            List<TrackedPlayer> BasePlayers() => new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, Name = "P1", LandId = 5, IsAlive = true, IsTargetable = true, CanBeAttacked = true, Gold = 10, CrimeNum = 1 },
                new TrackedPlayer { PlayerId = 2, Slot = 1, Name = "P2", LandId = 6, IsAlive = true, IsTargetable = true, CanBeAttacked = true, Gold = 10, CrimeNum = 0 }
            };

            PlanOptions BaseOpts() => new PlanOptions { MaxSteps = 5, SimulateCombatOnFork = true, PostCombatMode = PostCombatMode.ContinueByGuide };

            string baseSig = MonsterPathEngine.ComputeInputSignature(BaseMonster(), BasePlayers(), BaseOpts(), layoutGen: 1);

            var mutations = new Action<MonsterState, List<TrackedPlayer>, PlanOptions>[]
            {
                (m, p, o) => m.DisplayName = "安全官 2",
                (m, p, o) => m.Name = "小偷",
                (m, p, o) => m.IsNormalAggressive = false,
                (m, p, o) => m.IsThief = true,
                (m, p, o) => m.ChasesProblemStudent = false,
                (m, p, o) => m.ChasesMandarinDuckMark = true,
                (m, p, o) => m.BossTargetPlayerId = 2,
                (m, p, o) => m.BossTargetClearedThisTurn = true,
                (m, p, o) => m.RequiresCrimeTarget = true,
                (m, p, o) => m.IsTaunted = true,
                (m, p, o) => m.TauntTargetPlayerId = 2,
                (m, p, o) => m.CanStep = 4,
                (m, p, o) => m.SkillMoveKind = SkillMoveKind.ShirleyThrow,
                (m, p, o) => p[1].HasProblemStudentMark = true,
                (m, p, o) => p[0].IsAlive = false,
                (m, p, o) => p[0].CanBeAttacked = false,
                (m, p, o) => p[0].Gold = 0,
                (m, p, o) => p[0].CrimeNum = 0,
                (m, p, o) => p[0].LandId = 6,
                (m, p, o) => o.MaxSteps = 6,
                (m, p, o) => o.PostCombatMode = PostCombatMode.StopAtFirstCombat,
                (m, p, o) => o.MinBranchProbability = 0.1
            };

            for (int i = 0; i < mutations.Length; i++)
            {
                var m = BaseMonster();
                var p = BasePlayers();
                var o = BaseOpts();
                mutations[i](m, p, o);
                string mutatedSig = MonsterPathEngine.ComputeInputSignature(m, p, o, layoutGen: 1);
                Assert.NotEqual(baseSig, mutatedSig);
                // Ensure planning with every mutated configuration runs cleanly without throwing
                var plan = MonsterPathEngine.Plan(board, m, p, o);
                Assert.NotNull(plan);
            }
        }
    }
}
