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
            int seqBefore = tracker.TurnSequence;
            var r2 = tracker.Observe(true, 101, curRound: 8, latestDiceEpoch: 0, latestDiceOwnerId: 0, aliveMonsters.Contains, _ => false);
            Assert.True(r2.RoundChanged);
            Assert.False(r2.BoundaryAdvanced);
            Assert.Equal(seqBefore, tracker.TurnSequence);
            Assert.Equal(7, tracker.ActiveActorStartedRound); // Preserves original round 7 ownership!

            // Repeated observation of the SAME stale actor (8, 101) in the same/next frame must NOT create a phantom turn!
            // Furthermore, EffectiveActiveActorId must withhold active status while stale!
            var r2Repeat = tracker.Observe(true, 101, curRound: 8, latestDiceEpoch: 0, latestDiceOwnerId: 0, aliveMonsters.Contains, _ => false);
            Assert.False(r2Repeat.RoundChanged);
            Assert.False(r2Repeat.BoundaryAdvanced);
            Assert.Equal(seqBefore, tracker.TurnSequence);
            Assert.Equal(7, tracker.ActiveActorStartedRound);
            Assert.True(tracker.IsCurrentActorStalePendingRound);
            Assert.Equal(0, tracker.EffectiveActiveActorId);

            // Actor switches to hero 1 in round 8: monster 101 must remain UPCOMING (not acted) in round 8!
            var r3 = tracker.Observe(true, 1, curRound: 8, latestDiceEpoch: 0, latestDiceOwnerId: 0, aliveMonsters.Contains, id => id == 1);
            Assert.True(r3.BoundaryAdvanced);
            Assert.Equal(seqBefore + 1, tracker.TurnSequence);
            Assert.False(tracker.HasActedThisRound(101));
            Assert.Equal(1, tracker.ActiveActorId);
            Assert.Equal(8, tracker.ActiveActorStartedRound);
        }

        [Fact]
        public void UnavailableRoundAfterEstablishedRoundAdvanceDoesNotCreditStalePreviousRoundActor()
        {
            var aliveMonsters = new HashSet<long> { 101, 102 };
            var tracker = new ActionRoundTracker();

            tracker.Observe(true, 101, curRound: 7, latestDiceEpoch: 0, latestDiceOwnerId: 0, aliveMonsters.Contains, _ => false);
            // Round advances to 8 while 101 is still stale active actor
            tracker.Observe(true, 101, curRound: 8, latestDiceEpoch: 0, latestDiceOwnerId: 0, aliveMonsters.Contains, _ => false);
            // Switch to 102 while curRound temporarily reads -1: must NOT credit 101 in round 8!
            var sw = tracker.Observe(true, 102, curRound: -1, latestDiceEpoch: 0, latestDiceOwnerId: 0, aliveMonsters.Contains, _ => false);
            Assert.True(sw.BoundaryAdvanced);
            Assert.False(tracker.HasActedThisRound(101));
            Assert.Equal(8, tracker.ActiveActorStartedRound);
        }

        [Fact]
        public void ActorFirstDiceDeliveryCoalescesIntoSameTurnAndZeroOwnerRollNeverAdvancesUnchangedActor()
        {
            var aliveMonsters = new HashSet<long> { 101, 102 };
            var tracker = new ActionRoundTracker();

            tracker.Observe(true, 101, curRound: 4, latestDiceEpoch: 1, latestDiceOwnerId: 101, aliveMonsters.Contains, _ => false);

            // Switch actor to 102 BEFORE 102's dice arrives
            var sw = tracker.Observe(true, 102, curRound: 4, latestDiceEpoch: 1, latestDiceOwnerId: 101, aliveMonsters.Contains, _ => false);
            Assert.True(sw.BoundaryAdvanced);
            Assert.True(tracker.HasActedThisRound(101));
            int seq102 = tracker.TurnSequence;

            // 102's first dice notification arrives on next poll: must coalesce with existing turn (not double-count!)
            var firstRoll = tracker.Observe(true, 102, curRound: 4, latestDiceEpoch: 2, latestDiceOwnerId: 102, aliveMonsters.Contains, _ => false);
            Assert.False(firstRoll.BoundaryAdvanced);
            Assert.False(firstRoll.ShouldResetMovementState);
            Assert.Equal(seq102, tracker.TurnSequence);

            // Unowned dice (ownerId == 0) must never advance unchanged actor 102
            var unownedRoll = tracker.Observe(true, 102, curRound: 4, latestDiceEpoch: 3, latestDiceOwnerId: 0, aliveMonsters.Contains, _ => false);
            Assert.False(unownedRoll.BoundaryAdvanced);
            Assert.Equal(seq102, tracker.TurnSequence);

            // A SECOND owned roll for 102 (extra turn) DOES advance the boundary cleanly
            var extraTurnRoll = tracker.Observe(true, 102, curRound: 4, latestDiceEpoch: 4, latestDiceOwnerId: 102, aliveMonsters.Contains, _ => false);
            Assert.True(extraTurnRoll.BoundaryAdvanced);
            Assert.Equal(seq102 + 1, tracker.TurnSequence);
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

        [Fact]
        public void StaleServerActionEndFalseNeverErasesObservedTurnCompletionUntilAuthoritativeRefresh()
        {
            var aliveMonsters = new HashSet<long> { 101, 102 };
            var tracker = new ActionRoundTracker();

            tracker.Observe(true, 101, curRound: 3, latestDiceEpoch: 1, latestDiceOwnerId: 101, aliveMonsters.Contains, _ => false);
            tracker.Observe(true, 102, curRound: 3, latestDiceEpoch: 2, latestDiceOwnerId: 102, aliveMonsters.Contains, _ => false);
            Assert.True(tracker.HasActedThisRound(101));

            // Immediately following snapshot still reads lagging ActionEnd=false for 101: must keep 101 acted!
            tracker.ReconcileServerActionEnd(101, serverActionEnd: false, sameRoundBaseline: true);
            Assert.True(tracker.HasActedThisRound(101));

            // Once server catches up with ActionEnd=true and later explicitly resets to false, clear cleanly.
            tracker.ReconcileServerActionEnd(101, serverActionEnd: true, sameRoundBaseline: true);
            Assert.True(tracker.HasActedThisRound(101));
            tracker.ReconcileServerActionEnd(101, serverActionEnd: false, sameRoundBaseline: true);
            Assert.False(tracker.HasActedThisRound(101));
        }

        [Fact]
        public void ActorBeforeRoundDeliveryReconcilesFirstActorOfNewRoundCleanly()
        {
            var aliveMonsters = new HashSet<long> { 101, 102, 103 };
            var tracker = new ActionRoundTracker();

            // Round 7: 101 acts -> 102 acts (so 101 is already acted in round 7)
            tracker.ObserveWithOwnedRolls(true, 101, 7, 1, aliveMonsters.Contains, _ => false);
            tracker.ObserveWithOwnedRolls(true, 102, 7, 1, aliveMonsters.Contains, _ => false);
            Assert.True(tracker.HasActedThisRound(101));

            // Actor-before-round: 101 starts round 8 BEFORE Room.Round ticks to 8!
            tracker.ObserveWithOwnedRolls(true, 101, 7, 1, aliveMonsters.Contains, _ => false, isEarlierRoundPriorityThanPrevious: true);
            // Room.Round ticks to 8 while 101 is still acting -> reconciled to round 8 (not marked stale!)
            var tick = tracker.ObserveWithOwnedRolls(true, 101, 8, 0, aliveMonsters.Contains, _ => false);
            Assert.True(tick.RoundChanged);
            Assert.False(tracker.IsCurrentActorStalePendingRound);
            Assert.Equal(8, tracker.ActiveActorStartedRound);

            // Switch to 103 in round 8 -> 101 MUST be credited as acted in round 8!
            tracker.ObserveWithOwnedRolls(true, 103, 8, 1, aliveMonsters.Contains, _ => false);
            Assert.True(tracker.HasActedThisRound(101));
            Assert.False(tracker.HasActedThisRound(102));
            Assert.False(tracker.HasActedThisRound(103));
        }

        [Fact]
        public void BufferedMultiRollAdvancesExtraTurnAndNullRecoveryAndBackwardsRoundStaySafe()
        {
            var aliveMonsters = new HashSet<long> { 101, 102 };
            var tracker = new ActionRoundTracker();

            // (-1, 101) -> (7, 101) -> (8, null) -> (8, 102): 101 must NOT be credited in round 8!
            tracker.ObserveWithOwnedRolls(true, 101, -1, 0, aliveMonsters.Contains, _ => false);
            tracker.ObserveWithOwnedRolls(true, 101, 7, 0, aliveMonsters.Contains, _ => false);
            Assert.Equal(7, tracker.ActiveActorStartedRound);
            tracker.ObserveWithOwnedRolls(false, 0, 8, 0, aliveMonsters.Contains, _ => false);
            tracker.ObserveWithOwnedRolls(true, 102, 8, 0, aliveMonsters.Contains, _ => false);
            Assert.False(tracker.HasActedThisRound(101));

            // 102 started without a roll; TWO owned rolls arrive between polls (first roll + extra turn roll)
            int seqBefore = tracker.TurnSequence;
            var twoRolls = tracker.ObserveWithOwnedRolls(true, 102, 8, 2, aliveMonsters.Contains, _ => false);
            Assert.True(twoRolls.BoundaryAdvanced);
            Assert.Equal(seqBefore + 1, tracker.TurnSequence);

            // (8, 102) -> (9, null) -> (9, 102) -> first roll for 102 in round 9 must coalesce!
            tracker.ObserveWithOwnedRolls(false, 0, 9, 0, aliveMonsters.Contains, _ => false);
            var rec = tracker.ObserveWithOwnedRolls(true, 102, 9, 0, aliveMonsters.Contains, _ => false);
            Assert.True(rec.BoundaryAdvanced);
            int seqR9 = tracker.TurnSequence;
            var firstR9Roll = tracker.ObserveWithOwnedRolls(true, 102, 9, 1, aliveMonsters.Contains, _ => false);
            Assert.False(firstR9Roll.BoundaryAdvanced);
            Assert.Equal(seqR9, tracker.TurnSequence);

            // Regressing round sample (8 after accepted 9) must be ignored and keep round 9 acted set intact
            tracker.ObserveWithOwnedRolls(true, 101, 9, 1, aliveMonsters.Contains, _ => false);
            Assert.True(tracker.HasActedThisRound(102));
            var reg = tracker.ObserveWithOwnedRolls(true, 101, 8, 0, aliveMonsters.Contains, _ => false);
            Assert.False(reg.RoundChanged);
            Assert.Equal(9, tracker.LastObservedRound);
            Assert.True(tracker.HasActedThisRound(102));
        }
    }
}
