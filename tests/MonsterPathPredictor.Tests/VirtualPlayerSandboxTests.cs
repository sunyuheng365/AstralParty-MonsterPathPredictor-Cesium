using System.Collections.Generic;
using MonsterPathPredictor.Core;
using Xunit;

namespace MonsterPathPredictor.Tests
{
    public class VirtualPlayerSandboxTests
    {
        private static BoardGraph BuildForkBoard()
        {
            // Line 0 - 1 - 2 (fork) -> branch A: 3 - 5, branch B: 4 - 6
            return new BoardGraph(new[]
            {
                new LandNodeSnapshot(0, new[] { 1 }),
                new LandNodeSnapshot(1, new[] { 0, 2 }),
                new LandNodeSnapshot(2, new[] { 3, 4, 1 }),
                new LandNodeSnapshot(3, new[] { 2, 5 }),
                new LandNodeSnapshot(4, new[] { 2, 6 }),
                new LandNodeSnapshot(5, new[] { 3 }),
                new LandNodeSnapshot(6, new[] { 4 }),
                new LandNodeSnapshot(99, new[] { 6 }, disabled: true)
            });
        }

        [Fact]
        public void VirtualPlayerMoveRedirectsMonsterForkDecisionWithoutMutatingLivePlayers()
        {
            var board = BuildForkBoard();
            var livePlayers = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1001, Slot = 0, Name = "P1", LandId = 5, IsAlive = true, IsTargetable = true }
            };
            var monster = new MonsterState
            {
                MonsterId = 5001,
                Name = "玩具恐龙",
                DisplayName = "玩具恐龙",
                CurrentLandId = 1,
                FromLandId = 0,
                FrontNodeIds = new[] { 2 },
                IsNormalAggressive = true
            };

            // Live position (P1 at #5): monster approaches fork #2 and turns 100% toward #3
            var livePlan = MonsterPathEngine.Plan(board, monster, livePlayers, new PlanOptions { MaxSteps = 6 });
            var liveFork = Assert.Single(livePlan.Forks);
            Assert.Equal(2, liveFork.ForkLandId);
            Assert.Single(liveFork.Exits);
            Assert.Equal(3, liveFork.Exits[0].NextLandId);

            // Virtually move P1 to #6: monster flips 100% toward #4 while livePlayers stays untouched!
            var sandbox = new VirtualPlayerSandbox();
            Assert.True(sandbox.SetVirtualLand(1001, targetLandId: 6, liveLandId: 5, board));
            Assert.True(sandbox.HasOverrides);

            var effectivePlayers = sandbox.ApplyOverrides(livePlayers, board);
            Assert.Equal(5, livePlayers[0].LandId);      // Live player unmutated
            Assert.Equal(6, effectivePlayers[0].LandId); // Cloned effective player moved

            var virtualPlan = MonsterPathEngine.Plan(board, monster, effectivePlayers, new PlanOptions { MaxSteps = 6 });
            var virtualFork = Assert.Single(virtualPlan.Forks);
            Assert.Equal(2, virtualFork.ForkLandId);
            Assert.Single(virtualFork.Exits);
            Assert.Equal(4, virtualFork.Exits[0].NextLandId);
        }

        [Fact]
        public void UndoAndResetRestoreLivePositionCleanly()
        {
            var board = BuildForkBoard();
            var sandbox = new VirtualPlayerSandbox();

            Assert.True(sandbox.SetVirtualLand(1001, 3, liveLandId: 5, board));
            Assert.True(sandbox.SetVirtualLand(1001, 2, liveLandId: 5, board));
            Assert.True(sandbox.SetVirtualLand(1001, 4, liveLandId: 5, board));
            Assert.False(sandbox.SetVirtualLand(1001, 99, liveLandId: 5, board)); // Disabled tile rejected

            Assert.True(sandbox.TryGetVirtualLand(1001, out int cur));
            Assert.Equal(4, cur);

            Assert.True(sandbox.Undo(1001, liveLandId: 5, board));
            Assert.True(sandbox.TryGetVirtualLand(1001, out cur));
            Assert.Equal(2, cur);

            Assert.True(sandbox.Undo(1001, liveLandId: 5, board));
            Assert.True(sandbox.TryGetVirtualLand(1001, out cur));
            Assert.Equal(3, cur);

            Assert.True(sandbox.Undo(1001, liveLandId: 5, board));
            Assert.False(sandbox.HasOverrides);
        }

        [Fact]
        public void ReconcilePrunesDeadPlayersAndSelectsLocalPlayerByDefault()
        {
            var board = BuildForkBoard();
            var sandbox = new VirtualPlayerSandbox();
            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1001, Slot = 0, Name = "A", LandId = 5, IsAlive = true },
                new TrackedPlayer { PlayerId = 1002, Slot = 1, Name = "B", LandId = 6, IsAlive = true }
            };

            sandbox.Reconcile(players, board, localPlayerId: 1002);
            Assert.Equal(1002, sandbox.SelectedPlayerId);

            Assert.True(sandbox.SetVirtualLand(1002, 2, liveLandId: 6, board));
            var ghosts = sandbox.BuildGhosts(players, board, localPlayerId: 1002);
            var g = Assert.Single(ghosts);
            Assert.True(g.IsLocalPlayer);
            Assert.True(g.IsSelectedForEdit);
            Assert.Equal(6, g.RealLandId);
            Assert.Equal(2, g.VirtualLandId);

            // Player 1002 dies -> override pruned and selection falls back to living player 1001
            players[1].IsAlive = false;
            Assert.True(sandbox.Reconcile(players, board, localPlayerId: 1002));
            Assert.False(sandbox.HasOverrides);
            Assert.Equal(1001, sandbox.SelectedPlayerId);
        }

        [Fact]
        public void BoardTileHitTesterPicksClosestTileOrderIndependently()
        {
            var tA = new ProjectedBoardTile(10, new Point2(110f, 100f), cameraDepth: 12f);    // distSq = 100
            var tB = new ProjectedBoardTile(11, new Point2(110.03f, 100f), cameraDepth: 8f);  // distSq = 100.6009
            var tC = new ProjectedBoardTile(12, new Point2(110.07f, 100f), cameraDepth: 4f);  // distSq = 101.4049 (> 100 + 1.0!)

            // Order-independence: tC is outside minDistSq + 1.0 of tA, so tB must win regardless of list permutation
            Assert.Equal(11, BoardTileHitTester.PickTile(new Point2(100f, 100f), new[] { tA, tB, tC }, maxRadiusPx: 40f));
            Assert.Equal(11, BoardTileHitTester.PickTile(new Point2(100f, 100f), new[] { tC, tB, tA }, maxRadiusPx: 40f));
            Assert.Equal(11, BoardTileHitTester.PickTile(new Point2(100f, 100f), new[] { tB, tC, tA }, maxRadiusPx: 40f));
            Assert.Equal(-1, BoardTileHitTester.PickTile(new Point2(300f, 300f), new[] { tA, tB, tC }, maxRadiusPx: 40f));

            // Boundary radius guard: shallower tile strictly outside radius 44 (distSq=1936.25) must NEVER win!
            var onEdge = new ProjectedBoardTile(20, new Point2(44f, 0f), cameraDepth: 10f);
            var outside = new ProjectedBoardTile(21, new Point2(44f, 0.5f), cameraDepth: 2f);
            Assert.Equal(20, BoardTileHitTester.PickTile(new Point2(0f, 0f), new[] { onEdge, outside }, maxRadiusPx: 44f));
            Assert.Equal(20, BoardTileHitTester.PickTile(new Point2(0f, 0f), new[] { outside, onEdge }, maxRadiusPx: 44f));

            // LandId == int.MaxValue must be returned cleanly (not treated as "no winner" sentinel)
            var maxIdTile = new ProjectedBoardTile(int.MaxValue, new Point2(5f, 5f), cameraDepth: 5f);
            Assert.Equal(int.MaxValue, BoardTileHitTester.PickTile(new Point2(5f, 5f), new[] { maxIdTile }, maxRadiusPx: 44f));
        }

        [Fact]
        public void SingleUndoConsumesExactlyOneValidHistoryEntryAndArmingRequiresLivingOwner()
        {
            var board = BuildForkBoard();
            var sandbox = new VirtualPlayerSandbox();

            // Live at 5 -> move 2 -> 3 -> 5 (history = [null, 2, 3], no active override)
            Assert.True(sandbox.SetVirtualLand(1001, 2, liveLandId: 5, board));
            Assert.True(sandbox.SetVirtualLand(1001, 3, liveLandId: 5, board));
            Assert.True(sandbox.SetVirtualLand(1001, 5, liveLandId: 5, board));
            Assert.False(sandbox.HasOverrides);

            // Real player walks to 3 -> 1st Undo consumes ONLY '3' (does NOT jump straight to '2'!)
            Assert.True(sandbox.Undo(1001, liveLandId: 3, board));
            Assert.False(sandbox.HasOverrides);
            Assert.True(sandbox.CanUndo(1001));

            // 2nd Undo moves back to '2'
            Assert.True(sandbox.Undo(1001, liveLandId: 3, board));
            Assert.True(sandbox.TryGetVirtualLand(1001, out int afterSecond));
            Assert.Equal(2, afterSecond);

            // Board-pick arming must reject an explicitly selected nonzero DEAD owner (not merely SelectedPlayerId == 0!)
            var deadPlayers = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1001, Slot = 0, Name = "Dead", LandId = 5, IsAlive = false }
            };
            sandbox.SelectPlayer(1001);
            Assert.Equal(1001, sandbox.SelectedPlayerId);
            Assert.False(sandbox.SetBoardPickArmed(true, deadPlayers));
            Assert.False(sandbox.BoardPickArmed);

            // Board-pick arming succeeds for a living selected owner
            var alivePlayers = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1001, Slot = 0, Name = "Alive", LandId = 5, IsAlive = true }
            };
            Assert.True(sandbox.SetBoardPickArmed(true, alivePlayers));
            Assert.True(sandbox.BoardPickArmed);
        }

        [Fact]
        public void UndoRestoresFollowLiveStateEvenAfterRealPlayerMoved()
        {
            var board = BuildForkBoard();
            var sandbox = new VirtualPlayerSandbox();

            // Live at 5 -> place virtually at 6 -> real player walks to 3 -> Undo must follow live (3), not override to 5
            Assert.True(sandbox.SetVirtualLand(1001, 6, liveLandId: 5, board));
            Assert.True(sandbox.Undo(1001, liveLandId: 3, board));
            Assert.False(sandbox.HasOverrides);
            Assert.False(sandbox.CanUndo(1001));

            // Starting from unknown tile -1 still creates a reversible undo entry
            Assert.True(sandbox.SetVirtualLand(1001, 4, liveLandId: -1, board));
            Assert.True(sandbox.CanUndo(1001));
            Assert.True(sandbox.Undo(1001, liveLandId: 3, board));
            Assert.False(sandbox.HasOverrides);
        }

        [Fact]
        public void ReconcilePreservesUndoWhenLivePlayerLandsOnVirtualTileAndDisarmsOnDeath()
        {
            var board = BuildForkBoard();
            var sandbox = new VirtualPlayerSandbox();
            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1001, Slot = 0, Name = "A", LandId = 5, IsAlive = true },
                new TrackedPlayer { PlayerId = 1002, Slot = 1, Name = "B", LandId = 6, IsAlive = true }
            };
            sandbox.Reconcile(players, board, localPlayerId: 1001);
            sandbox.BoardPickArmed = true;

            Assert.True(sandbox.SetVirtualLand(1001, 3, liveLandId: 5, board));
            Assert.True(sandbox.SetVirtualLand(1001, 2, liveLandId: 5, board));

            // Real player 1001 arrives at tile 2 -> redundant override cleared, but undo back to 3 survives!
            players[0].LandId = 2;
            Assert.True(sandbox.Reconcile(players, board, localPlayerId: 1001));
            Assert.False(sandbox.HasOverrides);
            Assert.True(sandbox.CanUndo(1001));
            Assert.True(sandbox.HasPlayerState(1001));
            Assert.True(sandbox.Undo(1001, liveLandId: 2, board));
            Assert.True(sandbox.TryGetVirtualLand(1001, out int restored));
            Assert.Equal(3, restored);

            // Selected player 1001 dies -> BoardPickArmed must automatically disarm to prevent moving 1002
            players[0].IsAlive = false;
            sandbox.Reconcile(players, board, localPlayerId: 1001);
            Assert.False(sandbox.BoardPickArmed);
            Assert.Equal(1002, sandbox.SelectedPlayerId);

            // Layout switch increments LayoutGeneration and clears all overrides/history
            int genBefore = sandbox.LayoutGeneration;
            Assert.True(sandbox.SetVirtualLand(1002, 4, liveLandId: 6, board));
            Assert.True(sandbox.NotifyBoardLayoutChanged());
            Assert.Equal(genBefore + 1, sandbox.LayoutGeneration);
            Assert.False(sandbox.HasAnyState);
        }

        [Fact]
        public void ApplyOverridesPreservesAllPlayerFieldsAndSupportsSpecialMonsterMechanics()
        {
            var board = BuildForkBoard();
            var livePlayers = new List<TrackedPlayer>
            {
                new TrackedPlayer
                {
                    PlayerId = 1001,
                    Slot = 0,
                    Name = "StealthP1",
                    LandId = 5,
                    IsAlive = true,
                    IsTargetable = true,
                    CanBeAttacked = false,
                    Gold = 0,
                    CrimeNum = 0,
                    HasProblemStudentMark = false,
                    HasMandarinDuckMark = false
                },
                new TrackedPlayer
                {
                    PlayerId = 1002,
                    Slot = 1,
                    Name = "CulpritP2",
                    LandId = 6,
                    IsAlive = true,
                    IsTargetable = true,
                    CanBeAttacked = true,
                    Gold = 25,
                    CrimeNum = 2,
                    HasProblemStudentMark = true,
                    HasMandarinDuckMark = true
                }
            };

            var sandbox = new VirtualPlayerSandbox();
            Assert.True(sandbox.SetVirtualLand(1002, 5, liveLandId: 6, board));
            var cloned = sandbox.ApplyOverrides(livePlayers, board);

            // Every field preserved accurately without mutating livePlayers
            Assert.Equal(6, livePlayers[1].LandId);
            Assert.Equal(5, cloned[1].LandId);
            Assert.False(cloned[0].CanBeAttacked);
            Assert.Equal(0, cloned[0].Gold);
            Assert.Equal(25, cloned[1].Gold);
            Assert.Equal(2, cloned[1].CrimeNum);
            Assert.True(cloned[1].HasProblemStudentMark);
            Assert.True(cloned[1].HasMandarinDuckMark);

            // Crime-restricted Warden (1067) ignores CrimeNum==0 P1 and chases CrimeNum>0 P2 at virtual tile 5 (branch 3)
            var warden = new MonsterState
            {
                MonsterId = 6001,
                Name = "典狱长",
                DisplayName = "典狱长",
                HeroId = 1067,
                CurrentLandId = 1,
                FromLandId = 0,
                FrontNodeIds = new[] { 2 },
                IsNormalAggressive = true,
                RequiresCrimeTarget = true
            };
            var planVirtual = MonsterPathEngine.Plan(board, warden, cloned, new PlanOptions { MaxSteps = 6 });
            Assert.Equal(3, Assert.Single(planVirtual.Forks).Exits[0].NextLandId);
            Assert.Contains(planVirtual.Combats, c => c.LandId == 5 && c.TargetLabel == "P2");

            // Undo virtual move of P2 -> Warden flips back to real P2 at tile 6 (branch 4)
            Assert.True(sandbox.Undo(1002, liveLandId: 6, board));
            var planLive = MonsterPathEngine.Plan(board, warden, sandbox.ApplyOverrides(livePlayers, board), new PlanOptions { MaxSteps = 6 });
            Assert.Equal(4, Assert.Single(planLive.Forks).Exits[0].NextLandId);
            Assert.Contains(planLive.Combats, c => c.LandId == 6 && c.TargetLabel == "P2");
        }

        [Fact]
        public void ClickingLiveTileClearsRedundantPinnedOverrideEvenBeforeReconcile()
        {
            var board = BuildForkBoard();
            var sandbox = new VirtualPlayerSandbox();

            // Live at 5 -> move virtually to 3
            Assert.True(sandbox.SetVirtualLand(1001, targetLandId: 3, liveLandId: 5, board));
            Assert.True(sandbox.HasOverrides);

            // Real player arrives at 3 between snapshots BEFORE Reconcile runs; user clicks tile 3:
            // must clear the redundant pinned override rather than returning false and leaving it pinned!
            Assert.True(sandbox.SetVirtualLand(1001, targetLandId: 3, liveLandId: 3, board));
            Assert.False(sandbox.HasOverrides);
            Assert.False(sandbox.TryGetVirtualLand(1001, out _));

            // If real player later walks to 4, sandbox follows live (4) instead of remaining pinned to 3!
            var playersMoved = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1001, Slot = 0, Name = "P1", LandId = 4, IsAlive = true }
            };
            var eff = sandbox.ApplyOverrides(playersMoved, board);
            Assert.Equal(4, eff[0].LandId);
        }
    }
}
