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
            public bool ShouldBaselineUnconfirmedPostLayoutProto;
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
            bool standOnBoard = standLandId >= 0 &&
                                standLandVerifiedOnCurrentManager &&
                                (board == null || board.Contains(standLandId));
            if (standOnBoard)
            {
                return new Result
                {
                    LandId = standLandId,
                    ConfirmedOnCurrentLayout = true,
                    UsedVerifiedVisual = true
                };
            }

            if (!hasPriorLayout && !hasCurrentManagerLandMap &&
                standLandId >= 0 && (board == null || board.Contains(standLandId)))
            {
                return new Result
                {
                    LandId = standLandId,
                    ConfirmedOnCurrentLayout = true,
                    UsedVerifiedVisual = true
                };
            }

            bool protoOnBoard = protoNodeId >= 0 && (board == null || board.Contains(protoNodeId));
            if (!protoOnBoard)
            {
                return new Result { LandId = -1 };
            }

            // Once this hero has been visually verified on the current layout, RoomPlayer.NodeId is NOT updated
            // by normal walking (MoveS2C). Only accept it during a temporary model disappearance if it ACTUALLY
            // changed (e.g., via a fresh 1040.Place teleport/respawn or reconnect UpdateFromServer).
            if (visualConfirmedOnCurrentLayout)
            {
                if (lastSeenProtoNodeId.HasValue && protoNodeId != lastSeenProtoNodeId.Value)
                {
                    return new Result
                    {
                        LandId = protoNodeId,
                        ConfirmedOnCurrentLayout = true
                    };
                }
                return new Result { LandId = -1 };
            }

            if (!hasPriorLayout)
            {
                return new Result
                {
                    LandId = protoNodeId,
                    ConfirmedOnCurrentLayout = true
                };
            }

            // Post-layout confirmation without verified visual standLand yet: require positive new-layout
            // evidence (new-layout-only tile ID, or changed RoomPlayer.NodeId relative to pre-layout snapshot).
            bool hasNewLayoutProtoEvidence =
                !prevBoardContainsProto ||
                (preLayoutProtoNodeId.HasValue && protoNodeId != preLayoutProtoNodeId.Value);
            if (hasNewLayoutProtoEvidence)
            {
                return new Result
                {
                    LandId = protoNodeId,
                    ConfirmedOnCurrentLayout = true
                };
            }

            return new Result
            {
                LandId = -1,
                ShouldBaselineUnconfirmedPostLayoutProto = !preLayoutProtoNodeId.HasValue
            };
        }
    }
}
