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
        public void DominantSplitRoundedToOneFormatsNonCertainConditionallyCumulativelyAndOnEndpoints()
        {
            // Construct a 56-diamond graph from exit A (2^56 shortest paths to target) vs single chain from exit B (1 shortest path):
            // In IEEE-754 double, 2^56 / (2^56 + 1) rounds to 1.0, while sibling exit B has positive probability 2^-56 (~1.38e-17).
            const int Diamonds = 56;
            var nodes = new List<LandNodeSnapshot>();
            int targetId = 10000;
            int forkId = 1;
            int startId = 0;

            // Chain A: fork(1) -> a0(10) -> [diamond k: (100+2k, 101+2k) -> merge(200+k)] -> targetId
            int firstA = 10;
            int firstB = 5000;
            nodes.Add(new LandNodeSnapshot(startId, new[] { forkId }));
            nodes.Add(new LandNodeSnapshot(forkId, new[] { firstA, firstB, startId }));

            // Diamond path length from firstA to targetId: 2 * Diamonds edges (non-overlapping ID bands!)
            nodes.Add(new LandNodeSnapshot(firstA, new[] { 1000, 1001 }));
            for (int k = 0; k < Diamonds; k++)
            {
                int left = 1000 + k * 2;
                int right = 1001 + k * 2;
                int merge = (k == Diamonds - 1) ? targetId : (2000 + k);
                nodes.Add(new LandNodeSnapshot(left, new[] { merge }));
                nodes.Add(new LandNodeSnapshot(right, new[] { merge }));
                if (k < Diamonds - 1)
                {
                    int nextLeft = 1000 + (k + 1) * 2;
                    int nextRight = 1001 + (k + 1) * 2;
                    nodes.Add(new LandNodeSnapshot(merge, new[] { nextLeft, nextRight }));
                }
            }

            // Single chain B of exact equal length (2 * Diamonds edges from firstB to targetId)
            int bLen = 2 * Diamonds;
            for (int i = 0; i < bLen; i++)
            {
                int id = firstB + i;
                int next = (i == bLen - 1) ? targetId : (firstB + i + 1);
                nodes.Add(new LandNodeSnapshot(id, new[] { next }));
            }
            nodes.Add(new LandNodeSnapshot(targetId, Array.Empty<int>()));

            var board = new BoardGraph(nodes);
            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, Name = "P1", LandId = targetId, IsAlive = true, IsTargetable = true }
            };
            var monster = new MonsterState
            {
                MonsterId = 9056,
                Name = "史莱姆",
                DisplayName = "史莱姆",
                CurrentLandId = startId,
                FromLandId = -1,
                FrontNodeIds = new[] { forkId },
                IsNormalAggressive = true
            };

            var plan = MonsterPathEngine.Plan(board, monster, players, new PlanOptions { MaxSteps = 2 });

            var fork = Assert.Single(plan.Forks, f => f.ForkLandId == forkId);
            var domExit = Assert.Single(fork.Exits, e => e.NextLandId == firstA);
            var tinyExit = Assert.Single(fork.Exits, e => e.NextLandId == firstB);

            // Dominant exit rounds to 1.0 in IEEE-754 double yet MUST format >99.9% BOTH conditionally and cumulatively!
            Assert.Equal(1.0, domExit.ConditionalProbability);
            Assert.Equal(1.0, domExit.CumulativeProbability);
            Assert.True(domExit.HasOtherPositiveExits);
            Assert.True(domExit.HasOtherCumulativeSupport);
            Assert.Equal(">99.9%", domExit.FormatConditionalPercent());
            Assert.Equal(">99.9%", domExit.FormatCumulativePercent());

            Assert.True(tinyExit.IsPositiveSupport);
            Assert.Equal("<0.1%", tinyExit.FormatConditionalPercent());
            Assert.Equal("<0.1%", tinyExit.FormatCumulativePercent());

            // Dominant edge, tile step, and endpoint MUST all carry HasNonCertainDominantSplit=true!
            var domEdge = Assert.Single(plan.Edges, e => e.FromLandId == forkId && e.ToLandId == firstA);
            Assert.Equal(1.0, domEdge.Probability);
            Assert.True(domEdge.HasNonCertainDominantSplit);

            var domTile = Assert.Single(plan.TileSteps, t => t.LandId == firstA);
            Assert.True(domTile.HasNonCertainDominantSplit);

            var domEndpoint = Assert.Single(plan.Endpoints, ep => ep.LandId == firstA);
            Assert.Equal(1.0, domEndpoint.Probability);
            Assert.True(domEndpoint.HasNonCertainDominantSplit);
        }

        private static string SerializePlanForDiff(PathPlan p)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(p.MonsterId).Append('|').Append(p.MonsterName).Append('|').Append(p.HeadlineNote).Append('|')
              .Append(p.StepsRequested).Append('|').Append(p.IsNormalAggressive ? '1' : '0').Append('|');
            foreach (var e in p.Edges)
                sb.Append("E:").Append(e.FromLandId).Append('>').Append(e.ToLandId).Append('@').Append(e.EarliestStep)
                  .Append(':').Append(e.Probability.ToString("G12")).Append(e.IsAfterCombat ? 'C' : 'N').Append(e.HasNonCertainDominantSplit ? 'S' : 'D').Append(';');
            foreach (var f in p.Forks)
            {
                sb.Append("F:").Append(f.ForkLandId).Append('@').Append(f.StepIndex).Append(':').Append(f.SummaryLabel).Append('[');
                foreach (var ex in f.Exits)
                    sb.Append(ex.NextLandId).Append('=').Append(ex.FormatConditionalPercent()).Append('/').Append(ex.FormatCumulativePercent()).Append(',');
                sb.Append("];");
            }
            foreach (var c in p.Combats)
                sb.Append("C:").Append(c.LandId).Append('@').Append(c.StepIndex).Append(':').Append(c.TargetLabel).Append(c.IsSteal ? 'T' : 'A').Append(';');
            foreach (var ep in p.Endpoints)
                sb.Append("P:").Append(ep.LandId).Append('@').Append(ep.StepIndex).Append(':').Append(ep.StoppedAtCombat ? 'X' : 'O').Append(ep.HasNonCertainDominantSplit ? 'S' : 'D').Append(';');
            return sb.ToString();
        }

        [Fact]
        public void SignatureCacheSkipsRedundantPlansOnIdenticalSnapshotsAndMatchesFreshPlanAcrossEveryMutation()
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
                TurnStartLandId = 1,
                IsNormalAggressive = true,
                ChasesProblemStudent = true
            };

            List<TrackedPlayer> BasePlayers() => new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, Name = "P1", LandId = 5, IsAlive = true, IsTargetable = true, CanBeAttacked = true, Gold = 10, CrimeNum = 1 },
                new TrackedPlayer { PlayerId = 2, Slot = 1, Name = "P2", LandId = 6, IsAlive = true, IsTargetable = true, CanBeAttacked = true, Gold = 10, CrimeNum = 0 }
            };

            PlanOptions BaseOpts() => new PlanOptions { MaxSteps = 5, SimulateCombatOnFork = true, PostCombatMode = PostCombatMode.ContinueByGuide };

            // 1. Repeated identical snapshots MUST invoke MonsterPathEngine.Plan ZERO additional times!
            var cache = new PlanSignatureCache();
            var initialPlan = cache.GetOrCompute(board, BaseMonster(), BasePlayers(), BaseOpts(), layoutGeneration: 1);
            Assert.Equal(1, cache.PlanComputationCount);
            for (int rep = 0; rep < 5; rep++)
            {
                cache.MarkInputsMaybeChanged();
                var reused = cache.GetOrCompute(board, BaseMonster(), BasePlayers(), BaseOpts(), layoutGeneration: 1);
                Assert.Same(initialPlan, reused);
            }
            Assert.Equal(1, cache.PlanComputationCount);

            // 2. Every mutation (including FrontNodeIds, SkillMovePushFromLandId, TurnStartLandId, IsTargetable,
            //    SimulateCombatOnFork, and layoutGen) MUST invalidate the signature and match a fresh Plan() output!
            string baseSig = MonsterPathEngine.ComputeInputSignature(BaseMonster(), BasePlayers(), BaseOpts(), layoutGen: 1);
            Assert.NotEqual(baseSig, MonsterPathEngine.ComputeInputSignature(BaseMonster(), BasePlayers(), BaseOpts(), layoutGen: 2));

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
                (m, p, o) => m.FrontNodeIds = new[] { 0 },
                (m, p, o) => m.TurnStartLandId = 5,
                (m, p, o) => m.IsCurrentlyMovingMidTurn = true,
                (m, p, o) => m.JustFoughtOnCurrentFork = true,
                (m, p, o) => m.HasLaunchedFightThisTurn = true,
                (m, p, o) => m.JustSpawnedOnFork = true,
                (m, p, o) => { m.SkillMoveKind = SkillMoveKind.LinglingPush; m.SkillMovePushFromLandId = 0; },
                (m, p, o) => { m.SkillMoveKind = SkillMoveKind.LinglingPush; m.SkillMovePushDirectionLandId = 2; },
                (m, p, o) => m.SkillMoveKind = SkillMoveKind.ShirleyThrow,
                (m, p, o) => p[1].HasProblemStudentMark = true,
                (m, p, o) => p[0].IsAlive = false,
                (m, p, o) => p[0].IsTargetable = false,
                (m, p, o) => p[0].CanBeAttacked = false,
                (m, p, o) => p[0].Gold = 0,
                (m, p, o) => p[0].CrimeNum = 0,
                (m, p, o) => p[0].LandId = 6,
                (m, p, o) => o.MaxSteps = 6,
                (m, p, o) => o.SimulateCombatOnFork = false,
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

                var stepCache = new PlanSignatureCache();
                stepCache.GetOrCompute(board, BaseMonster(), BasePlayers(), BaseOpts(), layoutGeneration: 1);
                stepCache.MarkInputsMaybeChanged();
                var cachedMutated = stepCache.GetOrCompute(board, m, p, o, layoutGeneration: 1);
                var freshMutated = MonsterPathEngine.Plan(board, m, p, o);

                Assert.Equal(2, stepCache.PlanComputationCount);
                Assert.Equal(SerializePlanForDiff(freshMutated), SerializePlanForDiff(cachedMutated));
            }
        }
    }
}
