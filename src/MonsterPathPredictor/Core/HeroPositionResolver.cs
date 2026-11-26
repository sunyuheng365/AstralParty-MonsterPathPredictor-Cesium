namespace MonsterPathPredictor.Core
{
    /// <summary>
    /// Resolves a hero's authoritative live board tile according to <c>AstralParty.Runtime.dll</c> runtime semantics:
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///       Normal dice walking (<c>MoveS2C</c> / <c>CharacterMove.Move</c>) ONLY updates
    ///       <c>Character.standLand</c> (via <c>ResetStandLand</c>) and NEVER updates <c>RoomPlayer.NodeId</c>.
    ///       <c>RoomPlayer.NodeId</c> is only written on match start/reconnect (<c>UpdateFromServer</c>) and
    ///       <c>cmd=1040</c> <c>Place</c> effects (<c>UpdateHeroPlace</c>).
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Therefore, a <c>Character.standLand</c> verified against the current <c>LandManager</c> is ALWAYS
    ///       the primary authoritative live position. <c>RoomPlayer.NodeId</c> is used strictly as a fallback
    ///       when <c>standLand</c> is unavailable AND <c>RoomPlayer.NodeId</c> has not gone stale after normal walking
    ///       or an unconfirmed dynamic layout replacement.
    ///     </description>
    ///   </item>
    /// </list>
    /// </summary>
    public static class HeroPositionResolver
    {
        public struct Result
        {
            public int LandId;
            public bool ConfirmedOnCurrentLayout;
            public bool UsedVerifiedVisual;
            public bool UsedLogicalEvent;
            public bool ShouldBaselineUnconfirmedPostLayoutProto;
            public bool ShouldUpdateLastSeenProto;
            public long AcknowledgedEventSeq;
        }

        public static Result Resolve(
            BoardGraph board,
            int standLandId,
            bool standLandVerifiedOnCurrentManager,
            bool hasCurrentManagerLandMap,
            int protoNodeId,
            bool hasPriorLayout,
            bool prevBoardContainsProto,
            int? preLayoutProtoNodeId,
            int? lastSeenProtoNodeId,
            bool visualConfirmedOnCurrentLayout)
        {
            return ResolveWithEvents(
                board,
                standLandId,
                standLandVerifiedOnCurrentManager,
                hasCurrentManagerLandMap,
                protoNodeId,
                hasPriorLayout,
                prevBoardContainsProto,
                preLayoutProtoNodeId,
                lastSeenProtoNodeId,
                visualConfirmedOnCurrentLayout,
                pendingLogicalLandId: -1,
                pendingLogicalEventSeq: 0,
                acknowledgedLogicalEventSeq: 0,
                hasFreshFullSnapshot: true,
                observedWalkSinceSnapshot: false);
        }

        /// <summary>
        /// Sequence-aware hero position resolution combining ordered protocol events (<c>1040.Place</c>,
        /// <c>5214.Pursuit</c>, <c>5028.MoveS2C</c>), verified current-manager <c>Character.standLand</c>,
        /// and fresh full-snapshot <c>RoomPlayer.NodeId</c> fallback.
        /// </summary>
        public static Result ResolveWithEvents(
            BoardGraph board,
            int standLandId,
            bool standLandVerifiedOnCurrentManager,
            bool hasCurrentManagerLandMap,
            int protoNodeId,
            bool hasPriorLayout,
            bool prevBoardContainsProto,
            int? preLayoutProtoNodeId,
            int? lastSeenProtoNodeId,
            bool visualConfirmedOnCurrentLayout,
            int pendingLogicalLandId,
            long pendingLogicalEventSeq,
            long acknowledgedLogicalEventSeq,
            bool hasFreshFullSnapshot,
            bool observedWalkSinceSnapshot)
        {
            bool standOnBoard = standLandId >= 0 &&
                                standLandVerifiedOnCurrentManager &&
                                (board == null || board.Contains(standLandId));
            bool fallbackUnmappedStand = !standOnBoard &&
                                         !hasPriorLayout &&
                                         !hasCurrentManagerLandMap &&
                                         standLandId >= 0 &&
                                         (board == null || board.Contains(standLandId));
            bool hasUsableVisual = standOnBoard || fallbackUnmappedStand;

            bool hasUnackedLogicalEvent =
                pendingLogicalEventSeq > acknowledgedLogicalEventSeq &&
                pendingLogicalLandId >= 0 &&
                (board == null || board.Contains(pendingLogicalLandId));

            // 1. An unacknowledged protocol relocation (1040.Place / 5214.Pursuit / 5028.MoveS2C) takes
            //    precedence over an older, not-yet-moved visual standLand and survives temporary model loss,
            //    including same-number teleports (e.g., spawn 0 -> walk 11 -> Place 0).
            if (hasUnackedLogicalEvent)
            {
                if (hasUsableVisual && standLandId == pendingLogicalLandId)
                {
                    return new Result
                    {
                        LandId = standLandId,
                        ConfirmedOnCurrentLayout = true,
                        UsedVerifiedVisual = true,
                        ShouldUpdateLastSeenProto = (protoNodeId == standLandId),
                        AcknowledgedEventSeq = pendingLogicalEventSeq
                    };
                }

                return new Result
                {
                    LandId = pendingLogicalLandId,
                    ConfirmedOnCurrentLayout = true,
                    UsedLogicalEvent = true,
                    ShouldUpdateLastSeenProto = (protoNodeId == pendingLogicalLandId),
                    AcknowledgedEventSeq = acknowledgedLogicalEventSeq
                };
            }

            // 2. Verified current-manager visual standLand is authoritative for normal movement.
            //    Never acknowledge an unreflected RoomPlayer.NodeId change while standLand still shows another tile!
            if (hasUsableVisual)
            {
                bool protoChangedUnreflected =
                    lastSeenProtoNodeId.HasValue &&
                    protoNodeId >= 0 &&
                    protoNodeId != lastSeenProtoNodeId.Value &&
                    protoNodeId != standLandId;
                return new Result
                {
                    LandId = standLandId,
                    ConfirmedOnCurrentLayout = true,
                    UsedVerifiedVisual = true,
                    ShouldUpdateLastSeenProto = !protoChangedUnreflected,
                    AcknowledgedEventSeq = acknowledgedLogicalEventSeq
                };
            }

            bool protoOnBoard = protoNodeId >= 0 && (board == null || board.Contains(protoNodeId));
            if (!protoOnBoard)
            {
                return new Result { LandId = -1, AcknowledgedEventSeq = acknowledgedLogicalEventSeq };
            }

            // 3. After visual confirmation on the current layout (or an observed walk since the last snapshot),
            //    RoomPlayer.NodeId is frozen across normal walking; accept it ONLY if it genuinely changed.
            if (visualConfirmedOnCurrentLayout || observedWalkSinceSnapshot)
            {
                if (lastSeenProtoNodeId.HasValue && protoNodeId != lastSeenProtoNodeId.Value)
                {
                    return new Result
                    {
                        LandId = protoNodeId,
                        ConfirmedOnCurrentLayout = true,
                        ShouldUpdateLastSeenProto = true,
                        AcknowledgedEventSeq = acknowledgedLogicalEventSeq
                    };
                }
                return new Result { LandId = -1, AcknowledgedEventSeq = acknowledgedLogicalEventSeq };
            }

            // 4. Cold start on the initial layout before any visual confirmation: accept RoomPlayer.NodeId
            //    ONLY when backed by a fresh match-start/reconnect full snapshot (never stale late-enablement).
            if (!hasPriorLayout)
            {
                if (!hasFreshFullSnapshot)
                {
                    return new Result { LandId = -1, AcknowledgedEventSeq = acknowledgedLogicalEventSeq };
                }
                return new Result
                {
                    LandId = protoNodeId,
                    ConfirmedOnCurrentLayout = true,
                    ShouldUpdateLastSeenProto = true,
                    AcknowledgedEventSeq = acknowledgedLogicalEventSeq
                };
            }

            // 5. Post-layout confirmation without verified visual standLand yet: require positive new-layout
            //    evidence (new-layout-only tile ID, or changed RoomPlayer.NodeId relative to pre-layout snapshot).
            bool hasNewLayoutProtoEvidence =
                !prevBoardContainsProto ||
                (preLayoutProtoNodeId.HasValue && protoNodeId != preLayoutProtoNodeId.Value);
            if (hasNewLayoutProtoEvidence)
            {
                return new Result
                {
                    LandId = protoNodeId,
                    ConfirmedOnCurrentLayout = true,
                    ShouldUpdateLastSeenProto = true,
                    AcknowledgedEventSeq = acknowledgedLogicalEventSeq
                };
            }

            return new Result
            {
                LandId = -1,
                ShouldBaselineUnconfirmedPostLayoutProto = !preLayoutProtoNodeId.HasValue,
                AcknowledgedEventSeq = acknowledgedLogicalEventSeq
            };
        }
    }
}
