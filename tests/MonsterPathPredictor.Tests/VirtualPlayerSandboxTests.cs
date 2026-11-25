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
        public void BoardTileHitTesterPicksClosestTileWithDepthTieBreak()
        {
            var tiles = new[]
            {
                new ProjectedBoardTile(10, new Point2(100f, 100f), cameraDepth: 12f),
                new ProjectedBoardTile(11, new Point2(100f, 100f), cameraDepth: 8f),
                new ProjectedBoardTile(12, new Point2(160f, 100f), cameraDepth: 5f)
            };

            Assert.Equal(11, BoardTileHitTester.PickTile(new Point2(102f, 101f), tiles, maxRadiusPx: 40f));
            Assert.Equal(12, BoardTileHitTester.PickTile(new Point2(150f, 100f), tiles, maxRadiusPx: 40f));
            Assert.Equal(-1, BoardTileHitTester.PickTile(new Point2(300f, 300f), tiles, maxRadiusPx: 40f));
        }
    }
}
