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
        /// Sanitizes the in-flight <c>5028 MoveS2C</c> tween self-loop in <c>Core.Unit.CharacterMove</c>:
        /// <c>GetNextLandId</c> calls <c>ResetFromLandId(departure)</c> BEFORE the movement tween, while
        /// <c>MoveToPositionAsync</c> calls <c>ResetStandLand(nextStandLand)</c> AFTER the tween completes.
        /// During that interval, <c>(standLand, fromLandId) == (departure, departure)</c>.
        /// Retaining the last committed predecessor (or deriving the unique forward edge from
        /// <paramref name="nextStandLandId"/>) prevents manufacturing a 50% U-turn on a straight road.
        /// </summary>
        public static void SanitizeInFlightMovement(
            BoardGraph board,
            int currentLandId,
            int rawFromLandId,
            int nextStandLandId,
            int lastCommittedLandId,
            int lastCommittedFromLandId,
            out int effectiveFromLandId,
            out int[] inFlightFrontOverride,
            out bool isInFlightSelfLoop)
        {
            effectiveFromLandId = rawFromLandId;
            inFlightFrontOverride = null;
            isInFlightSelfLoop = false;

            if (board == null || currentLandId < 0 || !board.Contains(currentLandId))
                return;

            bool validNext = nextStandLandId >= 0 &&
                             nextStandLandId != currentLandId &&
                             board.IsValidNeighbor(currentLandId, nextStandLandId);

            if (rawFromLandId == currentLandId)
            {
                isInFlightSelfLoop = true;

                // Prefer the known in-flight destination edge if CharacterMove.nextStandLand is valid.
                if (validNext)
                {
                    inFlightFrontOverride = new[] { nextStandLandId };
                }

                // Retain the committed incoming edge while still standing on departure.
                if (lastCommittedLandId == currentLandId &&
                    lastCommittedFromLandId >= 0 &&
                    lastCommittedFromLandId != currentLandId &&
                    (board.IsValidNeighbor(currentLandId, lastCommittedFromLandId) ||
                     board.IsValidNeighbor(lastCommittedFromLandId, currentLandId)))
                {
                    effectiveFromLandId = lastCommittedFromLandId;
                    return;
                }

                // On a 2-exit straight road [prev, next], if we know the in-flight destination next,
                // the incoming predecessor is uniquely the other neighbor.
                if (validNext)
                {
                    var all = board.GetValidNeighbors(currentLandId);
                    if (all.Count == 2)
                    {
                        int other = all[0] == nextStandLandId ? all[1] : all[0];
                        if (other != currentLandId)
                        {
                            effectiveFromLandId = other;
                            return;
                        }
                    }
                }

                effectiveFromLandId = -1;
            }
        }

        /// <summary>
        /// Resolves live <c>FrontNodeIds</c> in strict authority order while guarding against partial async
        /// <c>CharacterMove.ShowWalkDirections</c> loads (which await each effect before appending to
        /// <c>_walkDirections</c>) and matching <c>UnitLand.CanSelectedLandId</c> on directed one-way arrivals.
        /// </summary>
        public static int[] ResolveLiveFrontNodeIds(
            BoardGraph board,
            int currentLandId,
            bool hasLiveCharacter,
            int liveFromLandId,
            int[] visualArrowFrontIds,
            int[] explicitFrontNodeIds,
            bool arrivedByNormalWalk = false)
        {
            if (board == null || !board.Contains(currentLandId)) return Array.Empty<int>();

            List<int> vis = (visualArrowFrontIds != null && visualArrowFrontIds.Length > 0)
                ? FilterValidDistinct(board, currentLandId, visualArrowFrontIds)
                : null;
            List<int> exp = (explicitFrontNodeIds != null && explicitFrontNodeIds.Length > 0)
                ? FilterValidDistinct(board, currentLandId, explicitFrontNodeIds)
                : null;

            List<int> expectedTopological = null;
            if (hasLiveCharacter && liveFromLandId != currentLandId)
            {
                if (liveFromLandId >= 0 && board.IsValidNeighbor(currentLandId, liveFromLandId))
                {
                    var fwd = board.GetForwardNeighbors(currentLandId, liveFromLandId);
                    expectedTopological = new List<int>(fwd.Count);
                    for (int i = 0; i < fwd.Count; i++) expectedTopological.Add(fwd[i]);
                }
                else if (liveFromLandId == -1 ||
                         (liveFromLandId >= 0 && board.IsValidNeighbor(liveFromLandId, currentLandId)))
                {
                    // Exact match to UnitLand.CanSelectedLandId(from): when from == -1 OR from arrived along a
                    // directed one-way edge (absent from currentLand's outgoing adjacency), all outgoing exits remain valid.
                    var all = board.GetValidNeighbors(currentLandId);
                    expectedTopological = new List<int>(all.Count);
                    for (int i = 0; i < all.Count; i++) expectedTopological.Add(all[i]);
                }
            }

            if (vis != null && vis.Count > 0)
            {
                // Guard against ShowWalkDirections mid-async-load:
                // - When explicit fronts are known for this occurrence (spawn/Place/Pursuit), a strict subset is mid-load.
                // - When the unit arrived via normal walking (ShowWalkDirections(null)), all CanSelectedLandId(from)
                //   arrows are spawned, so a strict subset of expectedTopological is also mid-load.
                if (exp != null && exp.Count > vis.Count && IsSubset(vis, exp))
                {
                    return exp.ToArray();
                }
                if (arrivedByNormalWalk &&
                    (exp == null || exp.Count == 0) &&
                    expectedTopological != null &&
                    expectedTopological.Count > vis.Count &&
                    IsSubset(vis, expectedTopological))
                {
                    return expectedTopological.ToArray();
                }
                return vis.ToArray();
            }

            if (exp != null && exp.Count > 0)
            {
                return exp.ToArray();
            }

            if (expectedTopological != null && expectedTopological.Count > 0)
            {
                return expectedTopological.ToArray();
            }

            return Array.Empty<int>();
        }

        private static bool IsSubset(List<int> sub, List<int> super)
        {
            for (int i = 0; i < sub.Count; i++)
            {
                if (!super.Contains(sub[i])) return false;
            }
            return true;
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
