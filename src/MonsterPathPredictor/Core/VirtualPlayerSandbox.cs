using System;
using System.Collections.Generic;

namespace MonsterPathPredictor.Core
{
    public readonly struct VirtualPlayerGhost
    {
        public long PlayerId { get; }
        public string SeatTag { get; }
        public string Name { get; }
        public int RealLandId { get; }
        public int VirtualLandId { get; }
        public bool IsLocalPlayer { get; }
        public bool IsSelectedForEdit { get; }

        public VirtualPlayerGhost(
            long playerId,
            string seatTag,
            string name,
            int realLandId,
            int virtualLandId,
            bool isLocalPlayer,
            bool isSelectedForEdit)
        {
            PlayerId = playerId;
            SeatTag = seatTag ?? string.Empty;
            Name = name ?? string.Empty;
            RealLandId = realLandId;
            VirtualLandId = virtualLandId;
            IsLocalPlayer = isLocalPlayer;
            IsSelectedForEdit = isSelectedForEdit;
        }
    }

    public readonly struct ProjectedBoardTile
    {
        public int LandId { get; }
        public Point2 ScreenPos { get; }
        public float CameraDepth { get; }

        public ProjectedBoardTile(int landId, Point2 screenPos, float cameraDepth)
        {
            LandId = landId;
            ScreenPos = screenPos;
            CameraDepth = cameraDepth;
        }
    }

    /// <summary>
    /// Pure-C# virtual player position sandbox for "what-if" fork decision planning.
    /// Allows moving P1-P4 to hypothetical board tiles without touching real game state,
    /// then feeding cloned effective player positions into MonsterPathEngine.Plan.
    /// </summary>
    public sealed class VirtualPlayerSandbox
    {
        private const int MaxUndoDepthPerPlayer = 64;

        private readonly Dictionary<long, int> _overrides = new Dictionary<long, int>();
        // Each undo entry stores the prior OVERRIDE state (null = following live player, non-null = virtual tile ID).
        private readonly Dictionary<long, List<int?>> _undoStacks = new Dictionary<long, List<int?>>();

        public long SelectedPlayerId { get; private set; }
        public bool BoardPickArmed { get; set; }
        public int LayoutGeneration { get; private set; }

        public bool HasOverrides => _overrides.Count > 0;
        public bool HasAnyState => _overrides.Count > 0 || _undoStacks.Count > 0;
        public int OverrideCount => _overrides.Count;

        public bool SetBoardPickArmed(bool armed, IReadOnlyList<TrackedPlayer> livePlayers)
        {
            if (!armed)
            {
                bool wasArmed = BoardPickArmed;
                BoardPickArmed = false;
                return wasArmed;
            }

            if (SelectedPlayerId == 0 || livePlayers == null)
            {
                BoardPickArmed = false;
                return false;
            }

            bool ownerAlive = false;
            for (int i = 0; i < livePlayers.Count; i++)
            {
                var p = livePlayers[i];
                if (p != null && p.PlayerId == SelectedPlayerId && p.IsAlive)
                {
                    ownerAlive = true;
                    break;
                }
            }
            if (!ownerAlive)
            {
                BoardPickArmed = false;
                return false;
            }

            bool changed = !BoardPickArmed;
            BoardPickArmed = true;
            return changed;
        }

        public void SelectPlayer(long playerId)
        {
            if (SelectedPlayerId != playerId)
            {
                SelectedPlayerId = playerId;
                if (playerId == 0) BoardPickArmed = false;
            }
        }

        public bool TryGetVirtualLand(long playerId, out int landId)
        {
            return _overrides.TryGetValue(playerId, out landId);
        }

        public int GetEffectiveLandId(TrackedPlayer player)
        {
            if (player == null) return -1;
            if (player.IsAlive && _overrides.TryGetValue(player.PlayerId, out int virt))
                return virt;
            return player.LandId;
        }

        public bool CanUndo(long playerId)
        {
            return _undoStacks.TryGetValue(playerId, out var stack) && stack.Count > 0;
        }

        public bool HasPlayerState(long playerId)
        {
            return _overrides.ContainsKey(playerId) || CanUndo(playerId);
        }

        /// <summary>
        /// Clears all tile-scoped overrides and undo history when the board layout topology switches mid-match
        /// (e.g. Magic Academy 82012 dynamic map replacement) so old tile IDs never leak into a new layout.
        /// </summary>
        public bool NotifyBoardLayoutChanged()
        {
            LayoutGeneration++;
            bool changed = _overrides.Count > 0 || _undoStacks.Count > 0;
            _overrides.Clear();
            _undoStacks.Clear();
            return changed;
        }

        /// <summary>
        /// Moves a player virtually to <paramref name="targetLandId"/> on <paramref name="board"/>.
        /// Returns true if the virtual state changed.
        /// </summary>
        public bool SetVirtualLand(long playerId, int targetLandId, int liveLandId, BoardGraph board)
        {
            if (playerId == 0 || board == null) return false;
            if (!board.TryGetNode(targetLandId, out var node) || node.Disabled) return false;

            bool hadOverride = _overrides.TryGetValue(playerId, out int existingOverride);
            // If a redundant override already matches the player's current live tile, clicking that live tile
            // clears the override cleanly so the player unpins instead of staying stuck if they later move away.
            if (hadOverride && targetLandId == liveLandId && existingOverride == targetLandId)
            {
                _overrides.Remove(playerId);
                return true;
            }

            int currentLand = hadOverride ? existingOverride : liveLandId;
            if (currentLand == targetLandId) return false;

            if (!_undoStacks.TryGetValue(playerId, out var stack))
            {
                stack = new List<int?>();
                _undoStacks[playerId] = stack;
            }
            if (stack.Count >= MaxUndoDepthPerPlayer) stack.RemoveAt(0);
            stack.Add(hadOverride ? (int?)existingOverride : null);

            if (targetLandId == liveLandId)
            {
                _overrides.Remove(playerId);
            }
            else
            {
                _overrides[playerId] = targetLandId;
            }
            return true;
        }

        /// <summary>
        /// Steps one valid entry back in the player's virtual override history.
        /// Consumes exactly ONE valid prior state (even if its tile already matches liveLandId),
        /// skipping only invalid/disabled historical tiles.
        /// </summary>
        public bool Undo(long playerId, int liveLandId, BoardGraph board)
        {
            if (playerId == 0 || !_undoStacks.TryGetValue(playerId, out var stack) || stack.Count == 0)
                return false;

            while (stack.Count > 0)
            {
                int? prevOverride = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                if (stack.Count == 0) _undoStacks.Remove(playerId);

                if (!prevOverride.HasValue)
                {
                    _overrides.Remove(playerId);
                    return true;
                }

                int prevLand = prevOverride.Value;
                if (prevLand == liveLandId)
                {
                    _overrides.Remove(playerId);
                    return true;
                }

                if (board != null && board.TryGetNode(prevLand, out var node) && !node.Disabled)
                {
                    _overrides[playerId] = prevLand;
                    return true;
                }
            }

            return _overrides.Remove(playerId);
        }

        public bool ResetPlayer(long playerId)
        {
            bool changed = _overrides.Remove(playerId);
            if (_undoStacks.Remove(playerId)) changed = true;
            return changed;
        }

        public bool ResetAll(bool disarmBoardPick = false)
        {
            bool changed = _overrides.Count > 0 || _undoStacks.Count > 0;
            _overrides.Clear();
            _undoStacks.Clear();
            if (disarmBoardPick)
            {
                if (BoardPickArmed) changed = true;
                BoardPickArmed = false;
                SelectedPlayerId = 0;
            }
            return changed;
        }

        /// <summary>
        /// Removes stale overrides (dead/missing players or invalid/disabled tiles after layout switch)
        /// and keeps SelectedPlayerId anchored to a valid living player (preferring localPlayerId).
        /// Returns true if any override changed.
        /// </summary>
        public bool Reconcile(IReadOnlyList<TrackedPlayer> livePlayers, BoardGraph board, long localPlayerId)
        {
            bool changed = false;

            var aliveById = new Dictionary<long, TrackedPlayer>();
            if (livePlayers != null)
            {
                for (int i = 0; i < livePlayers.Count; i++)
                {
                    var p = livePlayers[i];
                    if (p != null && p.PlayerId != 0 && p.IsAlive)
                        aliveById[p.PlayerId] = p;
                }
            }

            if (_overrides.Count > 0)
            {
                List<long> toRemoveWithHistory = null;
                List<long> toRemoveRedundantOnly = null;
                foreach (var kv in _overrides)
                {
                    bool aliveAndValidTile = aliveById.TryGetValue(kv.Key, out var liveP) &&
                                             board != null &&
                                             board.TryGetNode(kv.Value, out var node) &&
                                             !node.Disabled;
                    if (!aliveAndValidTile)
                    {
                        if (toRemoveWithHistory == null) toRemoveWithHistory = new List<long>();
                        toRemoveWithHistory.Add(kv.Key);
                    }
                    else if (kv.Value == liveP.LandId)
                    {
                        // Real player arrived at the virtual tile: clear the now-redundant override
                        // while preserving valid undo entries so the user can still step back.
                        if (toRemoveRedundantOnly == null) toRemoveRedundantOnly = new List<long>();
                        toRemoveRedundantOnly.Add(kv.Key);
                    }
                }
                if (toRemoveWithHistory != null)
                {
                    for (int i = 0; i < toRemoveWithHistory.Count; i++)
                    {
                        _overrides.Remove(toRemoveWithHistory[i]);
                        _undoStacks.Remove(toRemoveWithHistory[i]);
                    }
                    changed = true;
                }
                if (toRemoveRedundantOnly != null)
                {
                    for (int i = 0; i < toRemoveRedundantOnly.Count; i++)
                    {
                        _overrides.Remove(toRemoveRedundantOnly[i]);
                    }
                    changed = true;
                }
            }

            if (_undoStacks.Count > 0)
            {
                List<long> deadStacks = null;
                foreach (var k in _undoStacks.Keys)
                {
                    if (!aliveById.ContainsKey(k))
                    {
                        if (deadStacks == null) deadStacks = new List<long>();
                        deadStacks.Add(k);
                    }
                }
                if (deadStacks != null)
                {
                    for (int i = 0; i < deadStacks.Count; i++) _undoStacks.Remove(deadStacks[i]);
                }
            }

            // Ensure SelectedPlayerId points to a valid player in the current roster.
            // If the previously edited player died/vanished and had board-picking armed, disarm to prevent
            // accidentally moving a different auto-selected player on the next right-click.
            bool hadSelected = SelectedPlayerId != 0;
            bool selectedValid = hadSelected && aliveById.ContainsKey(SelectedPlayerId);
            if (!selectedValid)
            {
                if (BoardPickArmed)
                {
                    BoardPickArmed = false;
                    changed = true;
                }
                if (localPlayerId != 0 && aliveById.ContainsKey(localPlayerId))
                {
                    SelectedPlayerId = localPlayerId;
                }
                else
                {
                    TrackedPlayer best = null;
                    if (livePlayers != null)
                    {
                        for (int i = 0; i < livePlayers.Count; i++)
                        {
                            var p = livePlayers[i];
                            if (p == null || p.PlayerId == 0 || !p.IsAlive) continue;
                            if (best == null || p.Slot < best.Slot || (p.Slot == best.Slot && p.PlayerId < best.PlayerId))
                                best = p;
                        }
                    }
                    SelectedPlayerId = best != null ? best.PlayerId : 0;
                }
            }

            return changed;
        }

        /// <summary>
        /// Returns an effective player list with virtual tile overrides applied to cloned TrackedPlayer instances.
        /// Never mutates <paramref name="livePlayers"/>.
        /// </summary>
        public List<TrackedPlayer> ApplyOverrides(IReadOnlyList<TrackedPlayer> livePlayers, BoardGraph board)
        {
            var result = new List<TrackedPlayer>(livePlayers != null ? livePlayers.Count : 0);
            if (livePlayers == null) return result;

            for (int i = 0; i < livePlayers.Count; i++)
            {
                var p = livePlayers[i];
                if (p == null) continue;

                int effectiveLand = p.LandId;
                if (p.IsAlive &&
                    board != null &&
                    _overrides.TryGetValue(p.PlayerId, out int virtLand) &&
                    board.TryGetNode(virtLand, out var node) &&
                    !node.Disabled)
                {
                    effectiveLand = virtLand;
                }

                result.Add(new TrackedPlayer
                {
                    PlayerId = p.PlayerId,
                    Slot = p.Slot,
                    Name = p.Name,
                    LandId = effectiveLand,
                    IsAlive = p.IsAlive,
                    IsTargetable = p.IsTargetable,
                    CanBeAttacked = p.CanBeAttacked,
                    Gold = p.Gold,
                    CrimeNum = p.CrimeNum,
                    HasProblemStudentMark = p.HasProblemStudentMark,
                    HasMandarinDuckMark = p.HasMandarinDuckMark
                });
            }

            return result;
        }

        public List<VirtualPlayerGhost> BuildGhosts(IReadOnlyList<TrackedPlayer> livePlayers, BoardGraph board, long localPlayerId)
        {
            var ghosts = new List<VirtualPlayerGhost>();
            BuildGhostsInto(livePlayers, board, localPlayerId, ghosts);
            return ghosts;
        }

        public void BuildGhostsInto(IReadOnlyList<TrackedPlayer> livePlayers, BoardGraph board, long localPlayerId, List<VirtualPlayerGhost> target)
        {
            if (target == null) return;
            target.Clear();
            if (livePlayers == null || board == null || _overrides.Count == 0) return;

            for (int i = 0; i < livePlayers.Count; i++)
            {
                var p = livePlayers[i];
                if (p == null || !p.IsAlive) continue;
                if (!_overrides.TryGetValue(p.PlayerId, out int virtLand)) continue;
                if (virtLand == p.LandId) continue;
                if (!board.TryGetNode(virtLand, out var node) || node.Disabled) continue;

                target.Add(new VirtualPlayerGhost(
                    p.PlayerId,
                    p.SeatTag,
                    p.Name,
                    p.LandId,
                    virtLand,
                    p.PlayerId == localPlayerId,
                    p.PlayerId == SelectedPlayerId));
            }

            target.Sort((a, b) => string.CompareOrdinal(a.SeatTag, b.SeatTag));
        }

        public static List<int> GetReachableNeighbors(BoardGraph board, int currentLandId)
        {
            var list = new List<int>();
            if (board == null || currentLandId < 0) return list;

            var neighbors = board.GetValidNeighbors(currentLandId);
            for (int i = 0; i < neighbors.Count; i++)
            {
                int nb = neighbors[i];
                if (board.TryGetNode(nb, out var node) && !node.Disabled && !list.Contains(nb))
                {
                    list.Add(nb);
                }
            }
            return list;
        }
    }

    /// <summary>
    /// Pure-C# order-independent screen-space board tile hit-tester for right-click virtual placement.
    /// </summary>
    public static class BoardTileHitTester
    {
        private const double DistanceTieEpsSq = 1.0;
        private const float DepthTieEps = 0.01f;

        public static int PickTile(Point2 clickLocal, IReadOnlyList<ProjectedBoardTile> projectedTiles, float maxRadiusPx)
        {
            if (projectedTiles == null || projectedTiles.Count == 0 || maxRadiusPx <= 0f) return -1;

            double maxDistSq = (double)maxRadiusPx * maxRadiusPx;

            // Pass 1: find true minimum squared distance across all eligible tiles (order-independent)
            double minDistSq = double.MaxValue;
            for (int i = 0; i < projectedTiles.Count; i++)
            {
                var t = projectedTiles[i];
                double dx = clickLocal.X - t.ScreenPos.X;
                double dy = clickLocal.Y - t.ScreenPos.Y;
                double d2 = dx * dx + dy * dy;
                if (d2 <= maxDistSq && d2 < minDistSq)
                {
                    minDistSq = d2;
                }
            }
            if (minDistSq == double.MaxValue) return -1;

            // Pass 2: among tiles strictly within maxDistSq AND within DistanceTieEpsSq of minDistSq, find true minimum CameraDepth
            double distCutoff = Math.Min(maxDistSq, minDistSq + DistanceTieEpsSq);
            float minDepth = float.MaxValue;
            for (int i = 0; i < projectedTiles.Count; i++)
            {
                var t = projectedTiles[i];
                double dx = clickLocal.X - t.ScreenPos.X;
                double dy = clickLocal.Y - t.ScreenPos.Y;
                if (dx * dx + dy * dy <= distCutoff && t.CameraDepth < minDepth)
                {
                    minDepth = t.CameraDepth;
                }
            }

            // Pass 3: among eligible tiles within DepthTieEps of minDepth, pick deterministic smallest LandId
            float depthCutoff = minDepth + DepthTieEps;
            bool found = false;
            int bestLandId = -1;
            for (int i = 0; i < projectedTiles.Count; i++)
            {
                var t = projectedTiles[i];
                double dx = clickLocal.X - t.ScreenPos.X;
                double dy = clickLocal.Y - t.ScreenPos.Y;
                if (dx * dx + dy * dy <= distCutoff && t.CameraDepth <= depthCutoff && (!found || t.LandId < bestLandId))
                {
                    bestLandId = t.LandId;
                    found = true;
                }
            }

            return found ? bestLandId : -1;
        }
    }
}
