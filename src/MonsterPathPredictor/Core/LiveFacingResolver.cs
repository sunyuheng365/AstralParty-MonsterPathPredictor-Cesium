using System;
using System.Collections.Generic;

namespace MonsterPathPredictor.Core
{
    /// <summary>
    /// Resolves authoritative live-client facing arrows (<c>FrontNodeIds</c>) without ever falling back to
    /// stale <c>RoomPlayer.FrontIds</c> / <c>Hero.FrontNodeIds</c> left behind after normal walking (<c>MoveS2C</c>).
    ///
    /// In Astral Party's Unity client (<c>Core.Unit.CharacterMove</c>):
    /// - Spawn (<c>1003</c>/<c>1018</c>) and <c>1040.Place</c> (<c>SendCharacter</c>) update <c>RoomPlayer.NodeId</c>
    ///   and <c>RoomPlayer.FrontIds</c>, then call <c>ShowWalkDirections(FrontIds)</c>.
    /// - Normal walking (<c>CharacterMove.Move</c>) and in-place facing reversals (<c>ResetFromLandId</c>) update ONLY
    ///   <c>Character.standLand</c> and <c>Character.fromLandId</c>, then call <c>ShowWalkDirections(null)</c>
    ///   which renders <c>standLand.CanSelectedLandId(fromLandId)</c> without updating <c>RoomPlayer.FrontIds</c>.
    /// </summary>
    public static class LiveFacingResolver
    {
        private const float MinArrowCosine = 0.85f;

        /// <summary>
        /// Accepts initial protocol <c>FrontIds</c> on first observation ONLY when the unit is still standing on
        /// <paramref name="serverNodeId"/> and the arrows do not point exclusively backwards toward a valid
        /// <paramref name="liveFromLandId"/> (which occurs if the unit looped back onto its spawn tile).
        /// </summary>
        public static int[] TryAcceptInitialProtocolFrontIds(
            BoardGraph board,
            int currentLandId,
            int serverNodeId,
            int liveFromLandId,
            int[] rawProtocolFrontIds)
        {
            if (board == null || !board.Contains(currentLandId)) return null;
            if (serverNodeId != currentLandId) return null;
            if (rawProtocolFrontIds == null || rawProtocolFrontIds.Length == 0) return null;

            var valid = FilterValidDistinct(board, currentLandId, rawProtocolFrontIds);
            if (valid.Count == 0) return null;

            // If Character.fromLandId is a valid incoming neighbor and the protocol FrontIds point ONLY to
            // that incoming neighbor (or include it while not covering all neighbors), treat as stale.
            if (liveFromLandId >= 0 && board.IsValidNeighbor(currentLandId, liveFromLandId))
            {
                bool containsBack = false;
                for (int i = 0; i < valid.Count; i++)
                {
                    if (valid[i] == liveFromLandId) { containsBack = true; break; }
                }
                if (containsBack)
                {
                    var allValid = board.GetValidNeighbors(currentLandId);
                    if (valid.Count < allValid.Count) return null;
                }
            }

            return valid.ToArray();
        }

        /// <summary>
        /// Maps live rendered <c>CharacterMove._walkDirections</c> XZ forward vectors to distinct valid neighbors
        /// of <paramref name="currentLandId"/>, ordered by board adjacency priority (Red &gt; Blue &gt; ...).
        /// Returns <c>null</c> if any arrow cannot be matched cleanly (caller falls back to topological state).
        /// </summary>
        public static int[] MatchVisualArrowsToNeighbors(
            BoardGraph board,
            int currentLandId,
            IReadOnlyList<(float ForwardX, float ForwardZ)> arrowForwards)
        {
            if (board == null || arrowForwards == null || arrowForwards.Count == 0) return null;
            if (!board.TryGetNode(currentLandId, out var curNode)) return null;

            var validNeighbors = board.GetValidNeighbors(currentLandId);
            if (validNeighbors.Count == 0) return null;

            var matchedSet = new HashSet<int>();
            for (int i = 0; i < arrowForwards.Count; i++)
            {
                float fx = arrowForwards[i].ForwardX;
                float fz = arrowForwards[i].ForwardZ;
                float flen = (float)Math.Sqrt((double)fx * fx + (double)fz * fz);
                if (flen < 1e-4f) return null;
                fx /= flen;
                fz /= flen;

                int bestNb = -1;
                float bestDot = MinArrowCosine;
                for (int j = 0; j < validNeighbors.Count; j++)
                {
                    int nbId = validNeighbors[j];
                    if (!board.TryGetNode(nbId, out var nbNode)) continue;
                    float dx = nbNode.X - curNode.X;
                    float dz = nbNode.Z - curNode.Z;
                    float dlen = (float)Math.Sqrt((double)dx * dx + (double)dz * dz);
                    if (dlen < 1e-4f) continue;
                    float dot = (fx * dx + fz * dz) / dlen;
                    if (dot > bestDot)
                    {
                        bestDot = dot;
                        bestNb = nbId;
                    }
                }

                if (bestNb < 0 || !matchedSet.Add(bestNb)) return null;
            }

            // Preserve board adjacency order (Red > Blue > ...)
            var ordered = new List<int>(matchedSet.Count);
            for (int j = 0; j < validNeighbors.Count; j++)
            {
                if (matchedSet.Contains(validNeighbors[j]))
                    ordered.Add(validNeighbors[j]);
            }
            return ordered.Count > 0 ? ordered.ToArray() : null;
        }

        /// <summary>
        /// Resolves live <c>FrontNodeIds</c> in strict authority order:
        /// 1. Rendered visual arrows (<paramref name="visualArrowFrontIds"/>) from <c>CharacterMove._walkDirections</c>.
        /// 2. Fresh explicit server arrows (<paramref name="explicitFrontNodeIds"/>) from spawn or <c>1040.Place</c>.
        /// 3. Exact client-equivalent <c>UnitLand.CanSelectedLandId(liveFromLandId)</c> from live <c>Character</c> state.
        /// </summary>
        public static int[] ResolveLiveFrontNodeIds(
            BoardGraph board,
            int currentLandId,
            bool hasLiveCharacter,
            int liveFromLandId,
            int[] visualArrowFrontIds,
            int[] explicitFrontNodeIds)
        {
            if (board == null || !board.Contains(currentLandId)) return Array.Empty<int>();

            if (visualArrowFrontIds != null && visualArrowFrontIds.Length > 0)
            {
                var vis = FilterValidDistinct(board, currentLandId, visualArrowFrontIds);
                if (vis.Count > 0) return vis.ToArray();
            }

            if (explicitFrontNodeIds != null && explicitFrontNodeIds.Length > 0)
            {
                var exp = FilterValidDistinct(board, currentLandId, explicitFrontNodeIds);
                if (exp.Count > 0) return exp.ToArray();
            }

            if (hasLiveCharacter)
            {
                if (liveFromLandId >= 0 && board.IsValidNeighbor(currentLandId, liveFromLandId))
                {
                    var fwd = board.GetForwardNeighbors(currentLandId, liveFromLandId);
                    var arr = new int[fwd.Count];
                    for (int i = 0; i < fwd.Count; i++) arr[i] = fwd[i];
                    return arr;
                }
                if (liveFromLandId == -1)
                {
                    var all = board.GetValidNeighbors(currentLandId);
                    var arr = new int[all.Count];
                    for (int i = 0; i < all.Count; i++) arr[i] = all[i];
                    return arr;
                }
            }

            return Array.Empty<int>();
        }

        private static List<int> FilterValidDistinct(BoardGraph board, int currentLandId, int[] candidates)
        {
            var result = new List<int>(candidates.Length);
            var seen = new HashSet<int>();
            for (int i = 0; i < candidates.Length; i++)
            {
                int id = candidates[i];
                if (seen.Add(id) && board.IsValidNeighbor(currentLandId, id))
                {
                    result.Add(id);
                }
            }
            return result;
        }
    }
}
