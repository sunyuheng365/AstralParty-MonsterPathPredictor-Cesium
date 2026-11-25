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
            sb.Append(p.MonsterId).Append('|').Append(p.MonsterName).Append('|').Append(p.StartLandId).Append('|')
              .Append(p.HeadlineNote).Append('|').Append(p.StepsRequested).Append('|')
              .Append(p.IsNormalAggressive ? '1' : '0').Append('|').Append(p.PrunedProbability.ToString("G17")).Append('|');
            foreach (var e in p.Edges)
            {
                sb.Append("E:").Append(e.FromLandId).Append('>').Append(e.ToLandId).Append('@').Append(e.EarliestStep)
                  .Append(':').Append(e.Probability.ToString("G17")).Append(e.IsAfterCombat ? 'C' : 'N').Append(e.HasNonCertainDominantSplit ? 'S' : 'D').Append('{');
                var eSteps = new List<int>(e.StepProbabilities.Keys);
                eSteps.Sort();
                foreach (int s in eSteps) sb.Append(s).Append('=').Append(e.StepProbabilities[s].ToString("G17")).Append(',');
                sb.Append("};");
            }
            foreach (var t in p.TileSteps)
            {
                sb.Append("T:").Append(t.LandId).Append('@').Append(t.StepIndex).Append(':').Append(t.Probability.ToString("G17"))
                  .Append(t.IsAfterCombat ? 'C' : 'N').Append(t.IsEndpoint ? 'E' : 'M').Append(t.IsDeadEnd ? 'X' : 'O').Append(t.HasNonCertainDominantSplit ? 'S' : 'D').Append('[');
                for (int i = 0; i < t.StepIndices.Count; i++)
                {
                    int step = t.StepIndices[i];
                    t.StepProbabilities.TryGetValue(step, out double sp);
                    sb.Append(step).Append('=').Append(sp.ToString("G17")).Append(',');
                }
                sb.Append("];");
            }
            foreach (var f in p.Forks)
            {
                sb.Append("F:").Append(f.ForkLandId).Append("<-").Append(f.IncomingLandId).Append('@').Append(f.StepIndex)
                  .Append(':').Append(f.ArrivalProbability.ToString("G17")).Append('/').Append(f.LogArrivalProbability.ToString("G17"))
                  .Append(':').Append(f.IsAfterCombat ? 'C' : 'N').Append(':').Append((int)f.ReasonKind)
                  .Append(':').Append(f.IsExactUniformSplit ? 'Q' : 'q').Append(':').Append(f.SummaryLabel).Append('[');
                foreach (var ex in f.Exits)
                    sb.Append(ex.NextLandId)
                      .Append('=').Append(ex.ConditionalProbability.ToString("G17"))
                      .Append('/').Append(ex.LogConditionalProbability.ToString("G17"))
                      .Append('/').Append(ex.CumulativeProbability.ToString("G17"))
                      .Append('/').Append(ex.LogCumulativeProbability.ToString("G17"))
                      .Append('/').Append(ex.FormatConditionalPercent())
                      .Append('/').Append(ex.FormatCumulativePercent())
                      .Append(ex.IsGlobalRedArrow ? 'R' : 'B')
                      .Append(ex.HasOtherPositiveExits ? 'H' : 'U')
                      .Append(ex.HasOtherCumulativeSupport ? 'h' : 'u').Append(',');
                sb.Append("];");
            }
            foreach (var c in p.Combats)
            {
                sb.Append("C:").Append(c.LandId).Append('@').Append(c.StepIndex).Append(':').Append(c.TargetLabel)
                  .Append(':').Append(c.Probability.ToString("G17")).Append(c.IsSteal ? 'T' : 'A').Append('{');
                var cSteps = new List<int>(c.StepProbabilities.Keys);
                cSteps.Sort();
                foreach (int s in cSteps) sb.Append(s).Append('=').Append(c.StepProbabilities[s].ToString("G17")).Append(',');
                sb.Append("};");
            }
            foreach (var ep in p.Endpoints)
                sb.Append("P:").Append(ep.LandId).Append('@').Append(ep.StepIndex)
                  .Append(':').Append(ep.Probability.ToString("G17"))
                  .Append(ep.IsDeadEnd ? 'D' : 'A')
                  .Append(ep.StoppedAtCombat ? 'X' : 'O')
                  .Append(ep.IsAfterCombat ? 'C' : 'N')
                  .Append(ep.HasNonCertainDominantSplit ? 'S' : 'D').Append(';');
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
                (m, p, o) => { m.DisplayName = ""; m.Name = "小偷"; },
                (m, p, o) => m.DisplayName = "安全官|2;extra",
                (m, p, o) => m.IsNormalAggressive = false,
                (m, p, o) => m.IsThief = true,
                (m, p, o) => m.ChasesProblemStudent = false,
                (m, p, o) => m.ChasesMandarinDuckMark = true,
                (m, p, o) => m.BossTargetPlayerId = 2,
                (m, p, o) => m.BossTargetClearedThisTurn = true,
                (m, p, o) => m.RequiresCrimeTarget = true,
                (m, p, o) => m.IsTaunted = true,
                (m, p, o) => m.TauntTargetPlayerId = 2,
                (m, p, o) => m.FrontNodeIds = new[] { 0 },
                (m, p, o) => m.TurnStartLandId = 5,
                (m, p, o) => m.IsCurrentlyMovingMidTurn = true,
                (m, p, o) => m.HasStartedMovingThisTurn = true,
                (m, p, o) => m.BossLockInferenceReady = false,
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

            // R6-L2: Metadata-only mutations (CanStep, HeroId, shadowed Name, clamped MaxSteps 60 vs 99) MUST reuse cache!
            var metaCache = new PlanSignatureCache();
            var mMeta = BaseMonster();
            var oClamped = BaseOpts();
            oClamped.MaxSteps = 60;
            var pMeta1 = metaCache.GetOrCompute(board, mMeta, BasePlayers(), oClamped, layoutGeneration: 1);
            mMeta.CanStep = 9;
            mMeta.HeroId = 9999;
            mMeta.Name = "ignored_when_display_present";
            var oOver = BaseOpts();
            oOver.MaxSteps = 99;
            metaCache.MarkInputsMaybeChanged();
            var pMeta2 = metaCache.GetOrCompute(board, mMeta, BasePlayers(), oOver, layoutGeneration: 1);
            Assert.Same(pMeta1, pMeta2);
            Assert.Equal(1, metaCache.PlanComputationCount);

            // R6-M19: Graph replacement with SAME layoutGen and NO MarkInputsMaybeChanged MUST replan!
            var altBoard = new BoardGraph(new[]
            {
                new LandNodeSnapshot(0, new[] { 1 }),
                new LandNodeSnapshot(1, new[] { 0, 2 }),
                new LandNodeSnapshot(2, new[] { 4, 1 }), // Removed branch 3
                new LandNodeSnapshot(4, new[] { 2, 6 }),
                new LandNodeSnapshot(6, new[] { 4 })
            });
            var graphCache = new PlanSignatureCache();
            var g1 = graphCache.GetOrCompute(board, BaseMonster(), BasePlayers(), BaseOpts(), layoutGeneration: 1);
            var g2 = graphCache.GetOrCompute(altBoard, BaseMonster(), BasePlayers(), BaseOpts(), layoutGeneration: 1);
            Assert.NotSame(g1, g2);
            Assert.Equal(2, graphCache.PlanComputationCount);
            Assert.Equal(SerializePlanForDiff(MonsterPathEngine.Plan(altBoard, BaseMonster(), BasePlayers(), BaseOpts())), SerializePlanForDiff(g2));

            // R6-L1: Exception during planning must NOT pair new signature with old plan on retry!
            var errCache = new PlanSignatureCache();
            var okPlan = errCache.GetOrCompute(board, BaseMonster(), BasePlayers(), BaseOpts(), layoutGeneration: 1);
            errCache.MarkInputsMaybeChanged();
            var badMonster = BaseMonster();
            badMonster.FrontNodeIds = new[] { 0 };
            Assert.Throws<InvalidOperationException>(() =>
                errCache.GetOrCompute(board, badMonster, BasePlayers(), BaseOpts(), layoutGeneration: 1,
                    planner: (_, _, _, _) => throw new InvalidOperationException("transient planner error")));
            var recovered = errCache.GetOrCompute(board, badMonster, BasePlayers(), BaseOpts(), layoutGeneration: 1);
            Assert.NotSame(okPlan, recovered);
            Assert.Equal(2, errCache.PlanComputationCount);
            Assert.Equal(SerializePlanForDiff(MonsterPathEngine.Plan(board, badMonster, BasePlayers(), BaseOpts())), SerializePlanForDiff(recovered));
        }

        [Fact]
        public void ExpiredMarkBossLockClearsOnContactAndReadinessGateAndTauntPrecedenceHold()
        {
            // R6-H9: 0 -> 1 (P1, unmarked, retained BossTargetPlayerId=1) -> 2 (fork: 1 behind, 3 ahead, 4 -> P2)
            // After fighting P1 at 1, boss lock MUST clear so at fork 2 boss does NOT U-turn back toward P1!
            var bossBoard = new BoardGraph(new[]
            {
                new LandNodeSnapshot(0, new[] { 1 }),
                new LandNodeSnapshot(1, new[] { 0, 2 }),
                new LandNodeSnapshot(2, new[] { 1, 3, 4 }),
                new LandNodeSnapshot(3, new[] { 2, 5 }),
                new LandNodeSnapshot(4, new[] { 2, 6 }),
                new LandNodeSnapshot(5, new[] { 3 }),
                new LandNodeSnapshot(6, new[] { 4 })
            });
            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, Name = "P1", LandId = 1, IsAlive = true, IsTargetable = true, CanBeAttacked = true, HasProblemStudentMark = false },
                new TrackedPlayer { PlayerId = 2, Slot = 1, Name = "P2", LandId = 6, IsAlive = true, IsTargetable = true, CanBeAttacked = true, HasProblemStudentMark = false }
            };
            var boss = new MonsterState
            {
                MonsterId = 9101,
                Name = "安全官",
                DisplayName = "安全官",
                CurrentLandId = 0,
                FromLandId = -1,
                FrontNodeIds = new[] { 1 },
                IsNormalAggressive = true,
                ChasesProblemStudent = true,
                BossTargetPlayerId = 1
            };
            // Fighting a MARKED BossTargetPlayerId clears BossTargetCleared so at fork 2 boss chases P2@6 via 4 (no U-turn),
            // whereas fighting an UNMARKED (mark-expired) retained BossTargetPlayerId preserves replay-verified behavior (1795593160172877).
            players[0].HasProblemStudentMark = true;
            var markedBossPlan = MonsterPathEngine.Plan(bossBoard, boss, players, new PlanOptions { MaxSteps = 4, PostCombatMode = PostCombatMode.ContinueByGuide });
            var fork2Marked = Assert.Single(markedBossPlan.Forks, f => f.ForkLandId == 2);
            Assert.Single(fork2Marked.Exits);
            Assert.Equal(4, fork2Marked.Exits[0].NextLandId);

            players[0].HasProblemStudentMark = false;
            var expiredBossPlan = MonsterPathEngine.Plan(bossBoard, boss, players, new PlanOptions { MaxSteps = 4, PostCombatMode = PostCombatMode.ContinueByGuide });
            var fork2Expired = Assert.Single(expiredBossPlan.Forks, f => f.ForkLandId == 2);
            Assert.Single(fork2Expired.Exits);
            Assert.Equal(1, fork2Expired.Exits[0].NextLandId);

            // R6-M11: BossLockInferenceReady=false OR HasStartedMovingThisTurn=true (paused after fork combat)
            // MUST prevent planner from freshly inferring a marked target at the fork!
            var playersWithFarMark = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, Name = "P1", LandId = 5, IsAlive = true, IsTargetable = true, CanBeAttacked = true, HasProblemStudentMark = false },
                new TrackedPlayer { PlayerId = 2, Slot = 1, Name = "P2", LandId = 6, IsAlive = true, IsTargetable = true, CanBeAttacked = true, HasProblemStudentMark = true }
            };
            // Put P1 closer (dist 1 at tile 3) and marked P2 farther (dist 2 at tile 6) from fork 2:
            playersWithFarMark[0].LandId = 3;
            var bossUnready = new MonsterState
            {
                MonsterId = 9102,
                Name = "安全官",
                DisplayName = "安全官",
                CurrentLandId = 2,
                FromLandId = 1,
                FrontNodeIds = new[] { 3, 4 },
                TurnStartLandId = 2,
                IsNormalAggressive = true,
                ChasesProblemStudent = true,
                BossTargetPlayerId = 0,
                BossLockInferenceReady = false
            };
            var unreadyPlan = MonsterPathEngine.Plan(bossBoard, bossUnready, playersWithFarMark, new PlanOptions { MaxSteps = 2 });
            Assert.Equal(3, Assert.Single(unreadyPlan.Forks).Exits[0].NextLandId); // Chases nearer P1, did NOT infer P2!

            bossUnready.BossLockInferenceReady = true;
            bossUnready.HasStartedMovingThisTurn = true;
            bossUnready.JustFoughtOnCurrentFork = true;
            var postForkPlan = MonsterPathEngine.Plan(bossBoard, bossUnready, playersWithFarMark, new PlanOptions { MaxSteps = 2 });
            Assert.Equal(3, Assert.Single(postForkPlan.Forks).Exits[0].NextLandId); // Still did NOT re-infer P2 mid-turn!

            // R6-M12: Taunt + ShirleyThrow on step 0 MUST follow ShirleyThrow random exits (consistent displacement precedence)!
            var tauntedThrown = new MonsterState
            {
                MonsterId = 9103,
                Name = "史莱姆",
                DisplayName = "史莱姆",
                CurrentLandId = 2,
                FromLandId = 1,
                FrontNodeIds = new[] { 3, 4 },
                IsNormalAggressive = true,
                IsTaunted = true,
                TauntTargetPlayerId = 2,
                SkillMoveKind = SkillMoveKind.ShirleyThrow
            };
            var throwPlan = MonsterPathEngine.Plan(bossBoard, tauntedThrown, playersWithFarMark, new PlanOptions { MaxSteps = 1 });
            var throwFork = Assert.Single(throwPlan.Forks, f => f.ForkLandId == 2);
            Assert.Equal(3, throwFork.Exits.Count); // Random across all 3 neighbors {1,3,4}, not overridden by Taunt!

            // R6-L3: Lingling push fork MUST report IncomingLandId == SkillMovePushFromLandId (not stale FromLandId)!
            var linglingMonster = new MonsterState
            {
                MonsterId = 9104,
                Name = "史莱姆",
                DisplayName = "史莱姆",
                CurrentLandId = 2,
                FromLandId = 4, // Stale facing predecessor
                FrontNodeIds = new[] { 3, 1 },
                IsNormalAggressive = true,
                SkillMoveKind = SkillMoveKind.LinglingPush,
                SkillMovePushFromLandId = 1 // Verified push predecessor
            };
            var lingPlan = MonsterPathEngine.Plan(bossBoard, linglingMonster, playersWithFarMark, new PlanOptions { MaxSteps = 1 });
            var lingFork = Assert.Single(lingPlan.Forks, f => f.ForkLandId == 2);
            Assert.Equal(1, lingFork.IncomingLandId);
        }

        [Fact]
        public void SixWayReconvergenceMergedForkSupportAndUnderflowProbabilityHold()
        {
            // 1. R6-M20: 6-way reconvergence (6 * 1/6 = 0.9999999999999999 in IEEE-754 double)
            //    MUST canonicalize post-reconvergence continuation & endpoint to exact 1.0 (certain)!
            var sixWayBoard = new BoardGraph(new[]
            {
                new LandNodeSnapshot(0, new[] { 1 }),
                new LandNodeSnapshot(1, new[] { 10, 11, 12, 13, 14, 15 }),
                new LandNodeSnapshot(10, new[] { 20 }),
                new LandNodeSnapshot(11, new[] { 20 }),
                new LandNodeSnapshot(12, new[] { 20 }),
                new LandNodeSnapshot(13, new[] { 20 }),
                new LandNodeSnapshot(14, new[] { 20 }),
                new LandNodeSnapshot(15, new[] { 20 }),
                new LandNodeSnapshot(20, new[] { 21 }),
                new LandNodeSnapshot(21, new[] { 22 }),
                new LandNodeSnapshot(22, Array.Empty<int>())
            });
            var mSix = new MonsterState
            {
                MonsterId = 9201,
                Name = "史莱姆",
                DisplayName = "史莱姆",
                CurrentLandId = 0,
                FromLandId = -1,
                FrontNodeIds = new[] { 1 },
                IsNormalAggressive = true
            };
            var sixPlan = MonsterPathEngine.Plan(sixWayBoard, mSix, Array.Empty<TrackedPlayer>(), new PlanOptions { MaxSteps = 5 });
            var e2021 = Assert.Single(sixPlan.Edges, e => e.FromLandId == 20 && e.ToLandId == 21);
            Assert.Equal(1.0, e2021.Probability);
            Assert.False(e2021.HasNonCertainDominantSplit);
            var ep22 = Assert.Single(sixPlan.Endpoints, ep => ep.LandId == 22);
            Assert.Equal(1.0, ep22.Probability);
            Assert.False(ep22.HasNonCertainDominantSplit);

            // 2. R6-L4: RecordFork merging exact-uniform {10,11} (1/2 each) and {10,11,12} (1/3 each)
            //    MUST clear IsExactUniformSplit in BOTH merge orders AND compute exact 5/12, 5/12, 2/12!
            var mergeBoard = new BoardGraph(new[]
            {
                new LandNodeSnapshot(4, new[] { 5 }),
                new LandNodeSnapshot(5, new[] { 4, 10, 11, 12 }),
                new LandNodeSnapshot(10, Array.Empty<int>()),
                new LandNodeSnapshot(11, Array.Empty<int>()),
                new LandNodeSnapshot(12, Array.Empty<int>())
            });
            var twoExitOutcome = (0.5, Math.Log(0.5), true, new[]
            {
                (10, 0.5, Math.Log(0.5)),
                (11, 0.5, Math.Log(0.5))
            });
            var threeExitOutcome = (0.5, Math.Log(0.5), true, new[]
            {
                (10, 1.0 / 3.0, -Math.Log(3.0)),
                (11, 1.0 / 3.0, -Math.Log(3.0)),
                (12, 1.0 / 3.0, -Math.Log(3.0))
            });
            foreach (var order in new[] { new[] { twoExitOutcome, threeExitOutcome }, new[] { threeExitOutcome, twoExitOutcome } })
            {
                var merged = MonsterPathEngine.MergeForkOutcomesForTest(mergeBoard, forkLandId: 5, incomingLandId: 4, order);
                Assert.Equal(3, merged.Exits.Count);
                Assert.False(merged.IsExactUniformSplit);
                var ex10 = Assert.Single(merged.Exits, e => e.NextLandId == 10);
                var ex12 = Assert.Single(merged.Exits, e => e.NextLandId == 12);
                Assert.Equal(5.0 / 12.0, ex10.ConditionalProbability, precision: 6);
                Assert.Equal(2.0 / 12.0, ex12.ConditionalProbability, precision: 6);
            }

            // 2b. R6-M21 (subnormal arrivals): two arrivals of double.Epsilon splitting 2/3 & 1/3 ->
            //     linear cumulative products round to 0.0 while total arrival = 2*double.Epsilon > 0,
            //     yet merged ConditionalProbability MUST come from log masses (66.7%/33.3%, NOT 0%/0%)!
            double eps = double.Epsilon;
            double logEps = Math.Log(eps);
            var subnormalMerged = MonsterPathEngine.MergeForkOutcomesForTest(mergeBoard, forkLandId: 5, incomingLandId: 4, new[]
            {
                (eps, logEps, false, new[] { (10, 2.0 / 3.0, Math.Log(2.0 / 3.0)), (11, 1.0 / 3.0, Math.Log(1.0 / 3.0)) }),
                (eps, logEps, false, new[] { (10, 2.0 / 3.0, Math.Log(2.0 / 3.0)), (11, 1.0 / 3.0, Math.Log(1.0 / 3.0)) })
            });
            var sub10 = Assert.Single(subnormalMerged.Exits, e => e.NextLandId == 10);
            var sub11 = Assert.Single(subnormalMerged.Exits, e => e.NextLandId == 11);
            Assert.Equal(2.0 / 3.0, sub10.ConditionalProbability, precision: 6);
            Assert.Equal(1.0 / 3.0, sub11.ConditionalProbability, precision: 6);
            Assert.Equal("66.7%", sub10.FormatConditionalPercent());
            Assert.Equal("33.3%", sub11.FormatConditionalPercent());
            Assert.Equal("<0.1%", sub10.FormatCumulativePercent());
            Assert.Equal("<0.1%", sub11.FormatCumulativePercent());

            // 3. R6-L11: Single-exit fork reached via uncertain upstream branch formats 100% conditional / 50% cumulative!
            var singleExitUncertainBoard = new BoardGraph(new[]
            {
                new LandNodeSnapshot(0, new[] { 1 }),
                new LandNodeSnapshot(1, new[] { 2, 9 }),
                new LandNodeSnapshot(2, new[] { 3 }),
                new LandNodeSnapshot(3, new[] { 4, 5 }),
                new LandNodeSnapshot(4, Array.Empty<int>()),
                new LandNodeSnapshot(5, Array.Empty<int>()),
                new LandNodeSnapshot(9, new[] { 10 }),
                new LandNodeSnapshot(10, new[] { 11 }),
                new LandNodeSnapshot(11, Array.Empty<int>())
            });
            var mSingle = new MonsterState
            {
                MonsterId = 9203,
                Name = "史莱姆",
                DisplayName = "史莱姆",
                CurrentLandId = 1,
                FromLandId = 0,
                FrontNodeIds = new[] { 2, 9 },
                IsNormalAggressive = true
            };
            var pSinglePlayers = new List<TrackedPlayer>
            {
                // Equal distance 3 from fork 1 along BOTH branch 2 (->4) and branch 9 (->11): 50/50 split at fork 1!
                new TrackedPlayer { PlayerId = 1, Slot = 0, Name = "P1", LandId = 4, IsAlive = true, IsTargetable = true, CanBeAttacked = true },
                new TrackedPlayer { PlayerId = 2, Slot = 1, Name = "P2", LandId = 11, IsAlive = true, IsTargetable = true, CanBeAttacked = true }
            };
            var pSingle = MonsterPathEngine.Plan(singleExitUncertainBoard, mSingle, pSinglePlayers, new PlanOptions { MaxSteps = 3 });
            var f3 = Assert.Single(pSingle.Forks, f => f.ForkLandId == 3);
            var onlyEx = Assert.Single(f3.Exits);
            Assert.Equal(4, onlyEx.NextLandId);
            Assert.Equal(1.0, onlyEx.ConditionalProbability);
            Assert.False(onlyEx.HasOtherPositiveExits);
            Assert.Equal("100%", onlyEx.FormatConditionalPercent());
            Assert.Equal(0.5, onlyEx.CumulativeProbability, precision: 6);
            Assert.True(onlyEx.HasOtherCumulativeSupport);
            Assert.Equal("50%", onlyEx.FormatCumulativePercent());

            // 4. R6-M21: Underflowed merged fork (arrivalProb == 0.0 in double, finite log mass)
            //    with NON-uniform 2/3 vs 1/3 split MUST still compute 66.7%/33.3% conditional and <0.1% cumulative!
            const int Diamonds = 1080;
            const int TargetId = 90000;
            var uNodes = new List<LandNodeSnapshot>();
            uNodes.Add(new LandNodeSnapshot(0, new[] { 1 }));
            uNodes.Add(new LandNodeSnapshot(1, new[] { 10, 20 }));

            // Big branch from 10: 1080 diamonds of total length 2*1080 = 2160 to TargetId (count = 2^1080)
            uNodes.Add(new LandNodeSnapshot(10, new[] { 10000, 10001 }));
            for (int k = 0; k < Diamonds; k++)
            {
                int l = 10000 + k * 2;
                int r = 10001 + k * 2;
                int merge = (k == Diamonds - 1) ? TargetId : (20000 + k);
                uNodes.Add(new LandNodeSnapshot(l, new[] { merge }));
                uNodes.Add(new LandNodeSnapshot(r, new[] { merge }));
                if (k < Diamonds - 1)
                {
                    uNodes.Add(new LandNodeSnapshot(merge, new[] { 10000 + (k + 1) * 2, 10001 + (k + 1) * 2 }));
                }
            }

            // Small branch from 20 -> {21, 22} -> 23 (merged fork!) -> {24 (2 paths to 28), 25 (1 path to 28)} -> chain to TargetId
            // Distance from 20 to TargetId: 20->21(1)->23(2)->24(3)->26a(4)->28(5) + 2155 = 2160!
            uNodes.Add(new LandNodeSnapshot(20, new[] { 21, 22 }));
            uNodes.Add(new LandNodeSnapshot(21, new[] { 23 }));
            uNodes.Add(new LandNodeSnapshot(22, new[] { 23 }));
            uNodes.Add(new LandNodeSnapshot(23, new[] { 24, 25 }));
            uNodes.Add(new LandNodeSnapshot(24, new[] { 26, 27 })); // 2 equal paths to 28 -> weight 2/3
            uNodes.Add(new LandNodeSnapshot(25, new[] { 29 }));     // 1 path to 28 -> weight 1/3
            uNodes.Add(new LandNodeSnapshot(26, new[] { 28 }));
            uNodes.Add(new LandNodeSnapshot(27, new[] { 28 }));
            uNodes.Add(new LandNodeSnapshot(29, new[] { 28 }));
            int tailLen = 2 * Diamonds - 5; // 2155 edges from 28 to TargetId
            int prevTail = 28;
            for (int i = 0; i < tailLen; i++)
            {
                int nextTail = (i == tailLen - 1) ? TargetId : (50000 + i);
                uNodes.Add(new LandNodeSnapshot(prevTail, new[] { nextTail }));
                prevTail = nextTail;
            }
            uNodes.Add(new LandNodeSnapshot(TargetId, Array.Empty<int>()));

            var underflowBoard = new BoardGraph(uNodes);
            var uPlayers = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, Name = "P1", LandId = TargetId, IsAlive = true, IsTargetable = true, CanBeAttacked = true }
            };
            var mUnder = new MonsterState
            {
                MonsterId = 9204,
                Name = "史莱姆",
                DisplayName = "史莱姆",
                CurrentLandId = 0,
                FromLandId = -1,
                FrontNodeIds = new[] { 1 },
                IsNormalAggressive = true
            };
            var uPlan = MonsterPathEngine.Plan(underflowBoard, mUnder, uPlayers, new PlanOptions { MaxSteps = 5, MinBranchProbability = 0.0 });
            var f23List = uPlan.Forks.FindAll(f => f.ForkLandId == 23);
            Assert.Equal(2, f23List.Count);
            foreach (var f23 in f23List)
            {
                Assert.Equal(0.0, f23.ArrivalProbability); // Confirmed linear underflow to 0.0!
                Assert.False(double.IsNegativeInfinity(f23.LogArrivalProbability));
                var ex24 = Assert.Single(f23.Exits, e => e.NextLandId == 24);
                var ex25 = Assert.Single(f23.Exits, e => e.NextLandId == 25);
                Assert.Equal(2.0 / 3.0, ex24.ConditionalProbability, precision: 6);
                Assert.Equal(1.0 / 3.0, ex25.ConditionalProbability, precision: 6);
                Assert.Equal("66.7%", ex24.FormatConditionalPercent());
                Assert.Equal("33.3%", ex25.FormatConditionalPercent());
                Assert.Equal(0.0, ex24.CumulativeProbability);
                Assert.True(ex24.IsPositiveSupport);
                Assert.Equal("<0.1%", ex24.FormatCumulativePercent());
                Assert.Equal("<0.1%", ex25.FormatCumulativePercent());
            }
        }
    }
}
