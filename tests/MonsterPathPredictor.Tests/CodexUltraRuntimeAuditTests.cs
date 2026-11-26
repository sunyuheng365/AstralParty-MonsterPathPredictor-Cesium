using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using MonsterPathPredictor.Core;

namespace MonsterPathPredictor.Tests
{
    public class CodexUltraRuntimeAuditTests
    {
        private static BoardGraph LinearBoard(params int[] ids)
        {
            var nodes = new List<LandNodeSnapshot>();
            for (int i = 0; i < ids.Length; i++)
            {
                var nbs = new List<int>();
                if (i + 1 < ids.Length) nbs.Add(ids[i + 1]);
                if (i > 0) nbs.Add(ids[i - 1]);
                nodes.Add(new LandNodeSnapshot(ids[i], nbs.ToArray(), false, i * 2f, 0f, 0f));
            }
            return new BoardGraph(nodes);
        }

        [Fact]
        public void HeroPositionResolver_Finding1_SameNumberPlace_UnmovedVisual_AndLateEnablement()
        {
            var board = LinearBoard(0, 11, 22, 52);

            // 1. Real replay 1788448220380784: spawn 0 -> walk 11 -> 1040.Place back to 0 (same numeric value as spawn!).
            //    Even if model disappears OR still visibly stands on old tile 11, unacked Place seq=1 resolves to 0!
            var place0MissingModel = HeroPositionResolver.ResolveWithEvents(
                board, standLandId: -1, standLandVerifiedOnCurrentManager: false, hasCurrentManagerLandMap: true,
                protoNodeId: 0, hasPriorLayout: false, prevBoardContainsProto: false, preLayoutProtoNodeId: null,
                lastSeenProtoNodeId: 0, visualConfirmedOnCurrentLayout: true,
                pendingLogicalLandId: 0, pendingLogicalEventSeq: 1, acknowledgedLogicalEventSeq: 0,
                hasFreshFullSnapshot: true, observedWalkSinceSnapshot: true);
            Assert.True(place0MissingModel.ConfirmedOnCurrentLayout);
            Assert.Equal(0, place0MissingModel.LandId);
            Assert.True(place0MissingModel.UsedLogicalEvent);
            Assert.Equal(0, place0MissingModel.AcknowledgedEventSeq);

            // 2. Fresh Place 22 arrives while old visual still shows 11 -> returns 22 and does NOT ack seq=2!
            var place22OldVisual = HeroPositionResolver.ResolveWithEvents(
                board, standLandId: 11, standLandVerifiedOnCurrentManager: true, hasCurrentManagerLandMap: true,
                protoNodeId: 22, hasPriorLayout: false, prevBoardContainsProto: false, preLayoutProtoNodeId: null,
                lastSeenProtoNodeId: 0, visualConfirmedOnCurrentLayout: true,
                pendingLogicalLandId: 22, pendingLogicalEventSeq: 2, acknowledgedLogicalEventSeq: 1,
                hasFreshFullSnapshot: true, observedWalkSinceSnapshot: true);
            Assert.Equal(22, place22OldVisual.LandId);
            Assert.Equal(1, place22OldVisual.AcknowledgedEventSeq);

            // 3. Once visual reaches 22, seq=2 is cleanly acknowledged.
            var place22ArrivedVisual = HeroPositionResolver.ResolveWithEvents(
                board, standLandId: 22, standLandVerifiedOnCurrentManager: true, hasCurrentManagerLandMap: true,
                protoNodeId: 22, hasPriorLayout: false, prevBoardContainsProto: false, preLayoutProtoNodeId: null,
                lastSeenProtoNodeId: 0, visualConfirmedOnCurrentLayout: true,
                pendingLogicalLandId: 22, pendingLogicalEventSeq: 2, acknowledgedLogicalEventSeq: 1,
                hasFreshFullSnapshot: true, observedWalkSinceSnapshot: true);
            Assert.Equal(22, place22ArrivedVisual.LandId);
            Assert.True(place22ArrivedVisual.UsedVerifiedVisual);
            Assert.Equal(2, place22ArrivedVisual.AcknowledgedEventSeq);

            // 4. Fallback without 1040 hook: old visual 11 while proto jumped 0 -> 22 must NOT overwrite lastSeenProto!
            var unreflectedProtoChange = HeroPositionResolver.ResolveWithEvents(
                board, standLandId: 11, standLandVerifiedOnCurrentManager: true, hasCurrentManagerLandMap: true,
                protoNodeId: 22, hasPriorLayout: false, prevBoardContainsProto: false, preLayoutProtoNodeId: null,
                lastSeenProtoNodeId: 0, visualConfirmedOnCurrentLayout: true,
                pendingLogicalLandId: -1, pendingLogicalEventSeq: 0, acknowledgedLogicalEventSeq: 0,
                hasFreshFullSnapshot: true, observedWalkSinceSnapshot: true);
            Assert.Equal(11, unreflectedProtoChange.LandId);
            Assert.False(unreflectedProtoChange.ShouldUpdateLastSeenProto);

            // 5. Cold-start late enablement mid-match (no fresh snapshot, model missing): NEVER trust frozen spawn 0!
            var lateColdStart = HeroPositionResolver.ResolveWithEvents(
                board, standLandId: -1, standLandVerifiedOnCurrentManager: false, hasCurrentManagerLandMap: true,
                protoNodeId: 0, hasPriorLayout: false, prevBoardContainsProto: false, preLayoutProtoNodeId: null,
                lastSeenProtoNodeId: null, visualConfirmedOnCurrentLayout: false,
                pendingLogicalLandId: -1, pendingLogicalEventSeq: 0, acknowledgedLogicalEventSeq: 0,
                hasFreshFullSnapshot: false, observedWalkSinceSnapshot: false);
            Assert.False(lateColdStart.ConfirmedOnCurrentLayout);
            Assert.Equal(-1, lateColdStart.LandId);
        }

        [Fact]
        public void LiveFacingResolver_Finding2_StraightRoadTweenSelfLoop_NeverManufacturesUTurn()
        {
            // Real replay 1788414213302655 F312: monster walks 26 -> 25 -> 24.
            // During tween 25 -> 24, Character has (standLand=25, fromLandId=25) while arrows are deleted!
            var board = LinearBoard(26, 25, 24, 23);

            // Case A: With CharacterMove.nextStandLand = 24 known.
            LiveFacingResolver.SanitizeInFlightMovement(
                board, currentLandId: 25, rawFromLandId: 25, nextStandLandId: 24,
                lastCommittedLandId: 25, lastCommittedFromLandId: 26,
                out int effFromA, out int[] inFlightFrontA, out bool selfLoopA);
            Assert.True(selfLoopA);
            Assert.Equal(26, effFromA);
            Assert.Equal(new[] { 24 }, inFlightFrontA);

            // Case B: Without nextStandLand (-1), retaining lastCommittedFrom = 26 STILL prevents 50% U-turn!
            LiveFacingResolver.SanitizeInFlightMovement(
                board, currentLandId: 25, rawFromLandId: 25, nextStandLandId: -1,
                lastCommittedLandId: 25, lastCommittedFromLandId: 26,
                out int effFromB, out int[] inFlightFrontB, out bool selfLoopB);
            Assert.True(selfLoopB);
            Assert.Equal(26, effFromB);
            Assert.Null(inFlightFrontB);

            var monster = new MonsterState
            {
                MonsterId = 1733,
                CurrentLandId = 25,
                FromLandId = effFromB,
                FrontNodeIds = LiveFacingResolver.ResolveLiveFrontNodeIds(board, 25, true, effFromB, null, null, arrivedByNormalWalk: true),
                IsCurrentlyMovingMidTurn = true
            };
            var plan = MonsterPathEngine.Plan(board, monster, new List<TrackedPlayer>(), new PlanOptions { MaxSteps = 1 });
            Assert.Single(plan.Edges);
            Assert.Equal(24, plan.Edges[0].ToLandId);
            Assert.Equal(1.0, plan.Edges[0].Probability, 6);
        }

        [Fact]
        public void UnitPresenceLedger_Finding3_TracksDisappearAndAnimatorHideSeparatelyFromHp()
        {
            var ledger = new UnitPresenceLedger();
            Assert.False(ledger.IsEffectivelyAbsent(5001, animatorConfirmedHidden: false));

            // Disappear event marks unit absent even if animator hasn't scaled to 0 yet (and HP > 0).
            ledger.MarkDisappeared(5001);
            Assert.True(ledger.IsEffectivelyAbsent(5001, animatorConfirmedHidden: false));

            // Late-enabled client observing animator.IsHide()==true without seeing Disappear event still rejects it.
            Assert.True(ledger.IsEffectivelyAbsent(5002, animatorConfirmedHidden: true));

            // Reappearance / MonsterRefresh clears absence.
            ledger.MarkPresent(5001);
            Assert.False(ledger.IsEffectivelyAbsent(5001, animatorConfirmedHidden: false));
        }

        [Fact]
        public void ActionRoundTracker_Findings4And5_ExplicitActionStart_ABAWithinOneFrame_AndReconnectReseed()
        {
            var tracker = new ActionRoundTracker();
            Func<long, bool> isMonster = id => id == 100 || id == 200;
            Func<long, bool> isHero = id => id == 1;

            // Round 7: A(100) -> B(200) -> A(100) within one frame via explicit 1026 ActionStartNotifyS2C!
            tracker.ObserveExplicitRoundChange(7);
            var t1 = tracker.ObserveExplicitActionStart(100, -1, isMonster, isHero);
            int seq1 = tracker.TurnSequence;
            Assert.True(t1.BoundaryAdvanced);

            var t2 = tracker.ObserveExplicitActionStart(200, -1, isMonster, isHero);
            Assert.True(t2.BoundaryAdvanced);
            Assert.True(tracker.HasActedThisRound(100));

            var t3 = tracker.ObserveExplicitActionStart(100, -1, isMonster, isHero);
            Assert.True(t3.BoundaryAdvanced);
            Assert.True(tracker.TurnSequence >= seq1 + 2);

            // Same-match reconnect: old R7 ActionEnd=true AND fresh R8 snapshot ALSO says ActionEnd=true!
            var freshTracker = new ActionRoundTracker();
            freshTracker.ObserveExplicitRoundChange(7);
            freshTracker.SeedInitialServerActionEnd(100, true, 7);
            Assert.True(freshTracker.HasActedThisRound(100));

            // Same-room reconnect in Round 8 supplies fresh ActionEnd=true for 100 and false for 200.
            freshTracker.ReseedFromFullSnapshot(8, new[] { (100L, true), (200L, false) });
            Assert.Equal(8, freshTracker.LastObservedRound);
            Assert.True(freshTracker.HasActedThisRound(100));
            Assert.False(freshTracker.HasActedThisRound(200));
        }

        [Fact]
        public void LaunchFightTurnGuard_Finding6_ReconnectTrueExpiresAtNextAction()
        {
            // Real replay 1789211303827607: R4 F1423 has Hero.IsLaunchFight=true -> R5 F1602 is fresh turn!
            var guard = new LaunchFightTurnGuard();
            guard.SeedFromFreshSnapshot(snapshotGeneration: 2, currentTurnSequence: 10, heroIsLaunchFight: true, isActiveActorNow: true);
            Assert.True(guard.IsSeededLaunchFightActive(currentTurnSequence: 10));
            // Next action (turnSeq=11): frozen Hero.IsLaunchFight=true MUST NOT suppress encounters!
            Assert.False(guard.IsSeededLaunchFightActive(currentTurnSequence: 11));
        }

        [Fact]
        public void BossTargetLockState_Findings7And8_UnconditionalTurnStartRelock_ReconnectZero_AndExpiredMarkRetention()
        {
            var boss = new BossTargetLockState();
            // Fresh snapshot seeds retained lock P2.
            boss.SeedFromFreshSnapshot(serverBossTargetId: 2, snapshotGeneration: 1, currentTurnSequence: 1, orderSeq: 1);
            Assert.Equal(2, boss.ActiveBossTargetId);

            // Finding #7 (1795616950353775): Turn 2 starts with closer marked P1 -> replaces retained P2 lock!
            Assert.True(boss.EvaluateTurnStart(nearestMarkedPlayerId: 1, currentTurnSequence: 2, orderSeq: 2));
            Assert.Equal(1, boss.ActiveBossTargetId);

            // Finding #8 (1795593160172877 R10 F4138): Fighting locked P1 AFTER mark already expired MUST retain lock!
            Assert.False(boss.TryClearOnMarkedCombat(opponentHadPriorityMarkAtFight: false, fightTurnSequence: 2, combatOrderSequence: 3, currentTurnSequence: 2, isCurrentTurn: true));
            Assert.Equal(1, boss.ActiveBossTargetId);

            // Fighting opponent carrying active priority mark DOES clear lock!
            Assert.True(boss.TryClearOnMarkedCombat(opponentHadPriorityMarkAtFight: true, fightTurnSequence: 2, combatOrderSequence: 4, currentTurnSequence: 2, isCurrentTurn: true));
            Assert.Equal(0, boss.ActiveBossTargetId);
            Assert.Equal(2, boss.ClearedTurnSequence);

            // Turn 3 re-infers lock P3, then fresh reconnect snapshot with authoritative 0 MUST clear inferred lock!
            Assert.True(boss.EvaluateTurnStart(nearestMarkedPlayerId: 3, currentTurnSequence: 3, orderSeq: 5));
            Assert.Equal(3, boss.ActiveBossTargetId);
            boss.SeedFromFreshSnapshot(serverBossTargetId: 0, snapshotGeneration: 2, currentTurnSequence: 3, orderSeq: 6);
            Assert.Equal(0, boss.ActiveBossTargetId);
        }

        [Fact]
        public void CombatBattleRegistry_Finding9_DeduplicatesByBattleId()
        {
            var reg = new CombatBattleRegistry();
            Assert.True(reg.TryRegisterFirstReceipt(9001));   // First BattleUpdate(1007)
            Assert.False(reg.TryRegisterFirstReceipt(9001));  // Later FightResolved / phase update on same BattleId
            Assert.True(reg.TryRegisterFirstReceipt(9002));   // Distinct second battle against same opponent
            Assert.True(reg.TryRegisterFirstReceipt(0));      // Unkeyed fallback allowed
        }

        [Fact]
        public void LiveBuffMap_Findings11And17And20_Op0Replace_DuplicateUniqueIds_AndWardenRevealProgress()
        {
            // Finding #20 (1789211303827607 F377/F425): op 0 full replacement removes taunt 10011!
            var guardBuffs = new LiveBuffMap();
            guardBuffs.ApplyOperation(1, new[] { new LiveBuffEntry(101, 10011, 0, 12001) });
            Assert.True(guardBuffs.BuildSummary().HasTaunt);
            guardBuffs.ApplyOperation(0, new[]
            {
                new LiveBuffEntry(201, 10681201, 0, 0),
                new LiveBuffEntry(202, 10681101, 0, 0),
                new LiveBuffEntry(203, 10681202, 0, 0)
            });
            var afterReplace = guardBuffs.BuildSummary();
            Assert.False(afterReplace.HasTaunt);
            Assert.True(guardBuffs.ContainsBuff(10681202));

            // Finding #20 (1791447151858376 F3459): two instances of same BuffId with distinct UniqueIds.
            var dupMap = new LiveBuffMap();
            dupMap.ApplyOperation(1, new[]
            {
                new LiveBuffEntry(1001, 2000601, 1, 0),
                new LiveBuffEntry(1002, 2000601, 1, 0)
            });
            dupMap.ApplyOperation(2, new[] { new LiveBuffEntry(1001, 2000601, 0, 0) });
            Assert.True(dupMap.ContainsBuff(2000601)); // Second instance survives!
            dupMap.ApplyOperation(2, new[] { new LiveBuffEntry(1002, 2000601, 0, 0) });
            Assert.False(dupMap.ContainsBuff(2000601));

            // Finding #11: Warden reveal via positive 10671301.Progress alone (without 10671303) latches!
            var wardenBuffs = new LiveBuffMap();
            wardenBuffs.ApplyFullReplace(new[] { new LiveBuffEntry(301, 10671301, 1, 0) });
            Assert.True(wardenBuffs.BuildSummary().HasWardenReveal);
            Assert.True(wardenBuffs.WardenRevealedLatched);

            // Finding #17: Client HP formula (nonzero RealHp wins; otherwise prior + RealChangeHp, clamped to [0, maxHp]).
            Assert.Equal(12, LiveHpFormula.Apply(priorHp: 10, priorMaxHp: 20, realHp: 12, realChangeHp: -5, reportedMaxHp: 20));
            Assert.Equal(5, LiveHpFormula.Apply(priorHp: 10, priorMaxHp: 20, realHp: 0, realChangeHp: -5, reportedMaxHp: 20));
            Assert.Equal(0, LiveHpFormula.Apply(priorHp: 3, priorMaxHp: 20, realHp: 0, realChangeHp: -8, reportedMaxHp: 20));
        }

        [Fact]
        public void LiveFacingResolver_Finding15_PartialAsyncArrows_AndOneWayArrival()
        {
            // Fork 10 has outgoing [11, 12, 9]; arrived from 9 -> CanSelectedLandId(9) = [11, 12].
            var forkBoard = new BoardGraph(new[]
            {
                new LandNodeSnapshot(9, new[] { 10 }),
                new LandNodeSnapshot(10, new[] { 11, 12, 9 }),
                new LandNodeSnapshot(11, new[] { 10 }),
                new LandNodeSnapshot(12, new[] { 10 })
            });

            // 1. ShowWalkDirections async loop loaded 1 of 2 explicit arrows -> preserve complete explicit [11, 12]!
            var partialExplicit = LiveFacingResolver.ResolveLiveFrontNodeIds(
                forkBoard, 10, hasLiveCharacter: true, liveFromLandId: 9,
                visualArrowFrontIds: new[] { 11 }, explicitFrontNodeIds: new[] { 11, 12 });
            Assert.Equal(new[] { 11, 12 }, partialExplicit);

            // 2. Arrived by normal walk (ShowWalkDirections(null) renders [11, 12]): partial [11] keeps [11, 12]!
            var partialWalk = LiveFacingResolver.ResolveLiveFrontNodeIds(
                forkBoard, 10, hasLiveCharacter: true, liveFromLandId: 9,
                visualArrowFrontIds: new[] { 11 }, explicitFrontNodeIds: null, arrivedByNormalWalk: true);
            Assert.Equal(new[] { 11, 12 }, partialWalk);

            // 3. Genuine 1-arrow spawn (arrivedByNormalWalk=false, no explicit): keeps single arrow [11]!
            var singleSpawnArrow = LiveFacingResolver.ResolveLiveFrontNodeIds(
                forkBoard, 10, hasLiveCharacter: true, liveFromLandId: 9,
                visualArrowFrontIds: new[] { 11 }, explicitFrontNodeIds: null, arrivedByNormalWalk: false);
            Assert.Equal(new[] { 11 }, singleSpawnArrow);

            // 4. Directed one-way arrival 1 -> 2 where 2's outgoing neighbors are [3, 4] (1 absent from 2's adjacency!):
            //    UnitLand.CanSelectedLandId(1) returns ALL outgoing neighbors [3, 4]!
            var oneWayBoard = new BoardGraph(new[]
            {
                new LandNodeSnapshot(1, new[] { 2 }),
                new LandNodeSnapshot(2, new[] { 3, 4 }),
                new LandNodeSnapshot(3, new[] { 2 }),
                new LandNodeSnapshot(4, new[] { 2 })
            });
            var oneWayFront = LiveFacingResolver.ResolveLiveFrontNodeIds(
                oneWayBoard, 2, hasLiveCharacter: true, liveFromLandId: 1,
                visualArrowFrontIds: null, explicitFrontNodeIds: null, arrivedByNormalWalk: true);
            Assert.Equal(new[] { 3, 4 }, oneWayFront);
        }

        [Fact]
        public void SkillMoveEffectLedger_Finding16_ConsumesOnceOnMatchingArrival()
        {
            var ledger = new SkillMoveEffectLedger<string>();
            ledger.Enqueue(moverId: 286165, triggerLandId: 42, payload: "gold+1");
            Assert.False(ledger.TryConsumeOnArrival(286165, 41, out _));
            Assert.True(ledger.TryConsumeOnArrival(286165, 42, out var list));
            Assert.Single(list);
            Assert.Equal("gold+1", list[0]);
            // Consumed exactly once!
            Assert.False(ledger.TryConsumeOnArrival(286165, 42, out _));
        }

        [Fact]
        public void StopAtFirstCombat_Finding22_RefreshDuringConfirmedEncounter_EmitsZeroStepStop()
        {
            var board = new BoardGraph(new[]
            {
                new LandNodeSnapshot(0, new[] { 1 }),
                new LandNodeSnapshot(1, new[] { 2, 3, 0 }),
                new LandNodeSnapshot(2, new[] { 1 }),
                new LandNodeSnapshot(3, new[] { 1 })
            });
            var monster = new MonsterState
            {
                MonsterId = 700,
                CurrentLandId = 1,
                FromLandId = 0,
                JustFoughtOnCurrentFork = true,
                HasLaunchedFightThisTurn = true
            };
            var plan = MonsterPathEngine.Plan(
                board, monster, new List<TrackedPlayer>(),
                new PlanOptions { MaxSteps = 2, PostCombatMode = PostCombatMode.StopAtFirstCombat });
            Assert.Empty(plan.Edges);
            Assert.Single(plan.Endpoints);
            Assert.Equal(1, plan.Endpoints[0].LandId);
            Assert.Equal(0, plan.Endpoints[0].StepIndex);
            Assert.True(plan.Endpoints[0].StoppedAtCombat);
        }

        [Fact]
        public void StackedMarkedAndUnmarkedTile_Finding8_RetainsBothBossOutcomes()
        {
            // Boss 1047 walks 2 -> 3 (stacked: P1[unmarked] + P3[marked]) -> fork 5 (exits 3 [toward P1], 6 [toward P2]).
            // Because P1 (unmarked) and P3 (marked) share tile 3, 50% keeps lock on P1 (U-turns 5->3) and 50% clears lock (goes 5->6 toward P2)!
            var board = new BoardGraph(new[]
            {
                new LandNodeSnapshot(1, new[] { 2 }),
                new LandNodeSnapshot(2, new[] { 3, 1 }),
                new LandNodeSnapshot(3, new[] { 5, 2 }),
                new LandNodeSnapshot(5, new[] { 6, 7, 3 }),
                new LandNodeSnapshot(6, new[] { 5 }),
                new LandNodeSnapshot(7, new[] { 5 })
            });
            var players = new List<TrackedPlayer>
            {
                new TrackedPlayer { PlayerId = 1, Slot = 0, LandId = 3, IsAlive = true, IsTargetable = true, HasMandarinDuckMark = false },
                new TrackedPlayer { PlayerId = 3, Slot = 2, LandId = 3, IsAlive = true, IsTargetable = true, HasMandarinDuckMark = true },
                new TrackedPlayer { PlayerId = 2, Slot = 1, LandId = 6, IsAlive = true, IsTargetable = true, HasMandarinDuckMark = false }
            };
            var boss = new MonsterState
            {
                MonsterId = 801,
                HeroId = 1047,
                CurrentLandId = 2,
                FromLandId = 1,
                FrontNodeIds = new[] { 3 },
                ChasesMandarinDuckMark = true,
                BossTargetPlayerId = 1,
                IsCurrentlyMovingMidTurn = true
            };
            var plan = MonsterPathEngine.Plan(board, boss, players, new PlanOptions { MaxSteps = 3 });
            var edge5To3 = plan.Edges.Single(e => e.FromLandId == 5 && e.ToLandId == 3);
            var edge5To6 = plan.Edges.Single(e => e.FromLandId == 5 && e.ToLandId == 6);
            Assert.Equal(0.5, edge5To3.Probability, 6);
            Assert.Equal(0.5, edge5To6.Probability, 6);
        }

        [Fact]
        public void HitTester_BadgeFormatter_AndClassifier_Findings24To26()
        {
            // Finding #24: Offscreen indeterminate monster NEVER cancels clicking a living monster OR empty space!
            var offscreenUnknown = new MonsterHitBox { MonsterId = 1, Rect = new HitRect2D(500, 500, 540, 540), CameraDepth = 5f };
            var livingTarget = new MonsterHitBox { MonsterId = 2, Rect = new HitRect2D(10, 10, 50, 50), CameraDepth = 10f };
            var deadFront = new MonsterHitBox { MonsterId = 3, Rect = new HitRect2D(10, 10, 50, 50), CameraDepth = 4f };

            long hitLiving = MonsterHitTester.PickMonsterWithEligibility(
                new Point2(30, 30), new[] { offscreenUnknown, deadFront, livingTarget }, 20f,
                id => id == 1 ? -1 : (id == 3 ? 0 : 1), out bool deferred1);
            Assert.False(deferred1);
            Assert.Equal(2, hitLiving);

            long hitEmpty = MonsterHitTester.PickMonsterWithEligibility(
                new Point2(200, 200), new[] { offscreenUnknown, livingTarget }, 20f,
                id => id == 1 ? -1 : 1, out bool deferred2);
            Assert.False(deferred2);
            Assert.Equal(0, hitEmpty);

            // Overlapping foreground indeterminate monster DOES safely defer.
            var fgUnknown = new MonsterHitBox { MonsterId = 4, Rect = new HitRect2D(10, 10, 50, 50), CameraDepth = 2f };
            long hitDeferred = MonsterHitTester.PickMonsterWithEligibility(
                new Point2(30, 30), new[] { fgUnknown, livingTarget }, 20f,
                id => id == 4 ? -1 : 1, out bool deferred3);
            Assert.True(deferred3);
            Assert.Equal(0, hitDeferred);

            // Finding #25: Multi-step encounter badge prints sorted positive-support arrival steps.
            var multiCombat = new PredictedCombat { LandId = 7, StepIndex = 3, TargetLabel = "P1", IsSteal = false };
            multiCombat.StepProbabilities[5] = 0.5;
            multiCombat.StepProbabilities[3] = 0.5;
            Assert.Equal("⚔遇P1(第3/5格)", CombatBadgeFormatter.FormatEncounterLabel(multiCombat));
            var singleCombat = new PredictedCombat { LandId = 7, StepIndex = 3, TargetLabel = "P1", IsSteal = false };
            singleCombat.StepProbabilities[3] = 1.0;
            Assert.Equal("⚔遇P1(第3格)", CombatBadgeFormatter.FormatEncounterLabel(singleCombat));

            // Finding #26: MonsterType.None (0) returns unknown (null) instead of assuming aggressive!
            Assert.Null(MonsterClassifier.TryClassifyAggressive(MonsterClassifier.TypeNone));
            Assert.Null(MonsterClassifier.TryClassifyThief(MonsterClassifier.TypeNone));
            Assert.Equal(true, MonsterClassifier.TryClassifyAggressive(MonsterClassifier.TypePveBoss));
            Assert.Equal(false, MonsterClassifier.TryClassifyAggressive(MonsterClassifier.TypeThief));
            Assert.Equal(false, MonsterClassifier.TryClassifyAggressive(MonsterClassifier.TypePveAllyMonster));
            Assert.Equal(true, MonsterClassifier.TryClassifyThief(MonsterClassifier.TypeThief));
        }
    }
}
