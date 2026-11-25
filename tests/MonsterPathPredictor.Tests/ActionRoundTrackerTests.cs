using System.Collections.Generic;
using MonsterPathPredictor.Core;
using Xunit;

namespace MonsterPathPredictor.Tests
{
    public class ActionRoundTrackerTests
    {
        [Fact]
        public void RoundBeforeActorDeliveryNeverMarksPreviousRoundFinalMonsterAsActedInNewRound()
        {
            var aliveMonsters = new HashSet<long> { 101, 102 };
            var tracker = new ActionRoundTracker();

            // Round 7: Monster 101 is active
            var r1 = tracker.Observe(true, 101, curRound: 7, latestDiceEpoch: 0, latestDiceOwnerId: 0, aliveMonsters.Contains, _ => false);
            Assert.True(r1.BoundaryAdvanced);
            Assert.Equal(101, tracker.ActiveActorId);
            Assert.Equal(7, tracker.ActiveActorStartedRound);

            // Round ticks to 8 while GetCurrentPlayer() still returns 101 (no new dice for 101)
            var r2 = tracker.Observe(true, 101, curRound: 8, latestDiceEpoch: 0, latestDiceOwnerId: 0, aliveMonsters.Contains, _ => false);
            Assert.True(r2.RoundChanged);
            Assert.False(r2.BoundaryAdvanced);
            Assert.Equal(7, tracker.ActiveActorStartedRound); // Preserves original round 7 ownership!

            // Actor switches to hero 1 in round 8: monster 101 must remain UPCOMING (not acted) in round 8!
            var r3 = tracker.Observe(true, 1, curRound: 8, latestDiceEpoch: 0, latestDiceOwnerId: 0, aliveMonsters.Contains, id => id == 1);
            Assert.True(r3.BoundaryAdvanced);
            Assert.False(tracker.HasActedThisRound(101));
            Assert.Equal(1, tracker.ActiveActorId);
            Assert.Equal(8, tracker.ActiveActorStartedRound);
        }

        [Fact]
        public void TransientNullActorPreservesSameRoundTurnButResolvesPendingRoundChange()
        {
            var aliveMonsters = new HashSet<long> { 101 };
            var tracker = new ActionRoundTracker();

            tracker.Observe(true, 101, curRound: 7, latestDiceEpoch: 0, latestDiceOwnerId: 0, aliveMonsters.Contains, _ => false);
            int seqBefore = tracker.TurnSequence;

            // (7, 101) -> (7, null) -> (7, 101): preserves turn sequence & does not mark acted
            var gapSame = tracker.Observe(false, 0, curRound: 7, latestDiceEpoch: 0, latestDiceOwnerId: 0, aliveMonsters.Contains, _ => false);
            Assert.False(gapSame.BoundaryAdvanced);
            var recoverSame = tracker.Observe(true, 101, curRound: 7, latestDiceEpoch: 0, latestDiceOwnerId: 0, aliveMonsters.Contains, _ => false);
            Assert.False(recoverSame.BoundaryAdvanced);
            Assert.Equal(seqBefore, tracker.TurnSequence);
            Assert.False(tracker.HasActedThisRound(101));

            // (7, 101) -> (8, null) -> (8, 101): pending round transition advances boundary once identity recovers
            var gapNext = tracker.Observe(false, 0, curRound: 8, latestDiceEpoch: 0, latestDiceOwnerId: 0, aliveMonsters.Contains, _ => false);
            Assert.True(gapNext.RoundChanged);
            var recoverNext = tracker.Observe(true, 101, curRound: 8, latestDiceEpoch: 0, latestDiceOwnerId: 0, aliveMonsters.Contains, _ => false);
            Assert.True(recoverNext.BoundaryAdvanced);
            Assert.True(recoverNext.ShouldResetMovementState);
            Assert.Equal(seqBefore + 1, tracker.TurnSequence);
            Assert.Equal(8, tracker.ActiveActorStartedRound);
        }

        [Fact]
        public void DiceOwnerCorrelationIgnoresOtherActorsRollAndCreditsSameRoundCompletion()
        {
            var aliveMonsters = new HashSet<long> { 101, 102 };
            var tracker = new ActionRoundTracker();

            tracker.Observe(true, 101, curRound: 3, latestDiceEpoch: 1, latestDiceOwnerId: 101, aliveMonsters.Contains, _ => false);
            int seq101 = tracker.TurnSequence;

            // Monster 102's dice arrives while GetCurrentPlayer() still returns 101 -> must NOT reset 101!
            var otherDice = tracker.Observe(true, 101, curRound: 3, latestDiceEpoch: 2, latestDiceOwnerId: 102, aliveMonsters.Contains, _ => false);
            Assert.False(otherDice.BoundaryAdvanced);
            Assert.Equal(seq101, tracker.TurnSequence);

            // Switch to 102 in same round 3 -> 101 is credited as acted in round 3
            var sw = tracker.Observe(true, 102, curRound: 3, latestDiceEpoch: 2, latestDiceOwnerId: 102, aliveMonsters.Contains, _ => false);
            Assert.True(sw.BoundaryAdvanced);
            Assert.True(tracker.HasActedThisRound(101));
            Assert.False(tracker.HasActedThisRound(102));
        }
    }
}
