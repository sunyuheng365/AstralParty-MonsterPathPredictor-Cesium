using MonsterPathPredictor.Core;
using Xunit;

namespace MonsterPathPredictor.Tests
{
    public class HeroPositionResolverTests
    {
        private static BoardGraph BuildSampleBoard()
        {
            return new BoardGraph(new[]
            {
                new LandNodeSnapshot(0, new[] { 37, 1 }),
                new LandNodeSnapshot(1, new[] { 0, 2 }),
                new LandNodeSnapshot(2, new[] { 1 }),
                new LandNodeSnapshot(7, new[] { 12 }),
                new LandNodeSnapshot(12, new[] { 7, 22 }),
                new LandNodeSnapshot(22, new[] { 12 }),
                new LandNodeSnapshot(37, new[] { 0, 52 }),
                new LandNodeSnapshot(52, new[] { 37 })
            });
        }

        [Fact]
        public void PrefersVerifiedStandLandOverFrozenSpawnRoomPlayerNodeIdAfterNormalWalking()
        {
            // Regression for real match 1795704744600155 (Map 82017 R6):
            // P1 spawned on tile 0 and walked via MoveS2C (5028) to tile 52.
            // Because MoveS2C only updates Character.standLand and never updates RoomPlayer.NodeId,
            // protoNodeId stays frozen at 0 while verified standLandId is 52.
            var board = BuildSampleBoard();
            var r = HeroPositionResolver.Resolve(
                board,
                standLandId: 52,
                standLandVerifiedOnCurrentManager: true,
                hasCurrentManagerLandMap: true,
                protoNodeId: 0,
                hasPriorLayout: false,
                prevBoardContainsProto: false,
                preLayoutProtoNodeId: null,
                lastSeenProtoNodeId: 0,
                visualConfirmedOnCurrentLayout: true);

            Assert.True(r.ConfirmedOnCurrentLayout);
            Assert.True(r.UsedVerifiedVisual);
            Assert.Equal(52, r.LandId);
        }

        [Fact]
        public void TransientModelDisappearanceDoesNotSnapHeroBackToFrozenSpawnNodeId()
        {
            var board = BuildSampleBoard();

            // Model flickers unavailable while RoomPlayer.NodeId is still unchanged 0 -> withhold (-1)
            // so BuildTrackedPlayer preserves the last verified same-layout tile (52) instead of snapping to 0!
            var flicker = HeroPositionResolver.Resolve(
                board,
                standLandId: -1,
                standLandVerifiedOnCurrentManager: false,
                hasCurrentManagerLandMap: true,
                protoNodeId: 0,
                hasPriorLayout: false,
                prevBoardContainsProto: false,
                preLayoutProtoNodeId: null,
                lastSeenProtoNodeId: 0,
                visualConfirmedOnCurrentLayout: true);

            Assert.False(flicker.ConfirmedOnCurrentLayout);
            Assert.Equal(-1, flicker.LandId);

            // If a fresh 1040.Place teleport/respawn ACTUALLY updates RoomPlayer.NodeId (0 -> 22) while hidden,
            // accept the fresh destination tile immediately.
            var teleported = HeroPositionResolver.Resolve(
                board,
                standLandId: -1,
                standLandVerifiedOnCurrentManager: false,
                hasCurrentManagerLandMap: true,
                protoNodeId: 22,
                hasPriorLayout: false,
                prevBoardContainsProto: false,
                preLayoutProtoNodeId: null,
                lastSeenProtoNodeId: 0,
                visualConfirmedOnCurrentLayout: true);

            Assert.True(teleported.ConfirmedOnCurrentLayout);
            Assert.False(teleported.UsedVerifiedVisual);
            Assert.Equal(22, teleported.LandId);
        }

        [Fact]
        public void HandlesInitialSpawnBeforeModelsLoadAndPostLayoutReplacementSafety()
        {
            var board = BuildSampleBoard();

            // 1. Initial board before CharacterInst finishes spawning -> accepts initial RoomPlayer.NodeId (0).
            var initial = HeroPositionResolver.Resolve(
                board,
                standLandId: -1,
                standLandVerifiedOnCurrentManager: false,
                hasCurrentManagerLandMap: true,
                protoNodeId: 0,
                hasPriorLayout: false,
                prevBoardContainsProto: false,
                preLayoutProtoNodeId: null,
                lastSeenProtoNodeId: null,
                visualConfirmedOnCurrentLayout: false);
            Assert.True(initial.ConfirmedOnCurrentLayout);
            Assert.Equal(0, initial.LandId);

            // 2. After mid-match dynamic layout replacement: unverified old-manager standLand + unchanged overlapping proto 7 -> withhold!
            var unverifiedPost = HeroPositionResolver.Resolve(
                board,
                standLandId: 7,
                standLandVerifiedOnCurrentManager: false,
                hasCurrentManagerLandMap: true,
                protoNodeId: 7,
                hasPriorLayout: true,
                prevBoardContainsProto: true,
                preLayoutProtoNodeId: 7,
                lastSeenProtoNodeId: 7,
                visualConfirmedOnCurrentLayout: false);
            Assert.False(unverifiedPost.ConfirmedOnCurrentLayout);
            Assert.Equal(-1, unverifiedPost.LandId);

            // 3. Verified replacement-manager standLand at 12 beats stale overlapping proto 7!
            var verifiedPost = HeroPositionResolver.Resolve(
                board,
                standLandId: 12,
                standLandVerifiedOnCurrentManager: true,
                hasCurrentManagerLandMap: true,
                protoNodeId: 7,
                hasPriorLayout: true,
                prevBoardContainsProto: true,
                preLayoutProtoNodeId: 7,
                lastSeenProtoNodeId: 7,
                visualConfirmedOnCurrentLayout: false);
            Assert.True(verifiedPost.ConfirmedOnCurrentLayout);
            Assert.True(verifiedPost.UsedVerifiedVisual);
            Assert.Equal(12, verifiedPost.LandId);

            // 4. New-layout-only proto tile (not present on previous board) accepted even before model spawns.
            var newOnlyPost = HeroPositionResolver.Resolve(
                board,
                standLandId: -1,
                standLandVerifiedOnCurrentManager: false,
                hasCurrentManagerLandMap: true,
                protoNodeId: 52,
                hasPriorLayout: true,
                prevBoardContainsProto: false,
                preLayoutProtoNodeId: 7,
                lastSeenProtoNodeId: 7,
                visualConfirmedOnCurrentLayout: false);
            Assert.True(newOnlyPost.ConfirmedOnCurrentLayout);
            Assert.Equal(52, newOnlyPost.LandId);
        }
    }
}
