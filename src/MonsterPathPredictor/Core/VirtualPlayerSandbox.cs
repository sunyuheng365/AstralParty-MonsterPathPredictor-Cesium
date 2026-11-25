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
        private readonly Dictionary<long, List<int>> _undoStacks = new Dictionary<long, List<int>>();

        public long SelectedPlayerId { get; private set; }
        public bool BoardPickArmed { get; set; }

        public bool HasOverrides => _overrides.Count > 0;
        public int OverrideCount => _overrides.Count;

        public void SelectPlayer(long playerId)
        {
            SelectedPlayerId = playerId;
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

        /// <summary>
        /// Moves a player virtually to <paramref name="targetLandId"/> on <paramref name="board"/>.
        /// Returns true if the virtual state changed.
        /// </summary>
        public bool SetVirtualLand(long playerId, int targetLandId, int liveLandId, BoardGraph board)
        {
            if (playerId == 0 || board == null) return false;
            if (!board.TryGetNode(targetLandId, out var node) || node.Disabled) return false;

            int currentLand = _overrides.TryGetValue(playerId, out int existing) ? existing : liveLandId;
            if (currentLand == targetLandId) return false;

            if (currentLand >= 0)
            {
                if (!_undoStacks.TryGetValue(playerId, out var stack))
                {
                    stack = new List<int>();
                    _undoStacks[playerId] = stack;
                }
                if (stack.Count >= MaxUndoDepthPerPlayer) stack.RemoveAt(0);
                stack.Add(currentLand);
            }

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
        /// Steps one entry back in the player's virtual movement history.
        /// </summary>
        public bool Undo(long playerId, int liveLandId, BoardGraph board)
        {
            if (playerId == 0 || !_undoStacks.TryGetValue(playerId, out var stack) || stack.Count == 0)
                return false;

            while (stack.Count > 0)
            {
                int prevLand = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);

                if (prevLand == liveLandId)
                {
                    bool removed = _overrides.Remove(playerId);
                    if (removed || stack.Count == 0) return true;
                    continue;
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
                List<long> toRemove = null;
                foreach (var kv in _overrides)
                {
                    bool valid = aliveById.TryGetValue(kv.Key, out var liveP) &&
                                 board != null &&
                                 board.TryGetNode(kv.Value, out var node) &&
                                 !node.Disabled &&
                                 kv.Value != liveP.LandId;
                    if (!valid)
                    {
                        if (toRemove == null) toRemove = new List<long>();
                        toRemove.Add(kv.Key);
                    }
                }
                if (toRemove != null)
                {
                    for (int i = 0; i < toRemove.Count; i++)
                    {
                        _overrides.Remove(toRemove[i]);
                        _undoStacks.Remove(toRemove[i]);
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

            // Ensure SelectedPlayerId points to a valid player in the current roster
            bool selectedValid = SelectedPlayerId != 0 && aliveById.ContainsKey(SelectedPlayerId);
            if (!selectedValid)
            {
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
            if (livePlayers == null || board == null || _overrides.Count == 0) return ghosts;

            for (int i = 0; i < livePlayers.Count; i++)
            {
                var p = livePlayers[i];
                if (p == null || !p.IsAlive) continue;
                if (!_overrides.TryGetValue(p.PlayerId, out int virtLand)) continue;
                if (virtLand == p.LandId) continue;
                if (!board.TryGetNode(virtLand, out var node) || node.Disabled) continue;

                ghosts.Add(new VirtualPlayerGhost(
                    p.PlayerId,
                    p.SeatTag,
                    p.Name,
                    p.LandId,
                    virtLand,
                    p.PlayerId == localPlayerId,
                    p.PlayerId == SelectedPlayerId));
            }

            ghosts.Sort((a, b) => string.CompareOrdinal(a.SeatTag, b.SeatTag));
            return ghosts;
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
    /// Pure-C# screen-space board tile hit-tester for right-click virtual placement.
    /// </summary>
    public static class BoardTileHitTester
    {
        public static int PickTile(Point2 clickLocal, IReadOnlyList<ProjectedBoardTile> projectedTiles, float maxRadiusPx)
        {
            if (projectedTiles == null || projectedTiles.Count == 0 || maxRadiusPx <= 0f) return -1;

            double maxDistSq = (double)maxRadiusPx * maxRadiusPx;
            int bestLandId = -1;
            double bestDistSq = double.MaxValue;
            float bestDepth = float.MaxValue;

            for (int i = 0; i < projectedTiles.Count; i++)
            {
                var t = projectedTiles[i];
                double dx = clickLocal.X - t.ScreenPos.X;
                double dy = clickLocal.Y - t.ScreenPos.Y;
                double d2 = dx * dx + dy * dy;
                if (d2 > maxDistSq) continue;

                const double TieEps = 1.0; // 1 px^2 tie-break tolerance
                if (bestLandId < 0 ||
                    d2 < bestDistSq - TieEps ||
                    (Math.Abs(d2 - bestDistSq) <= TieEps &&
                     (t.CameraDepth < bestDepth - 0.01f ||
                      (Math.Abs(t.CameraDepth - bestDepth) <= 0.01f && t.LandId < bestLandId))))
                {
                    bestLandId = t.LandId;
                    bestDistSq = d2;
                    bestDepth = t.CameraDepth;
                }
            }

            return bestLandId;
        }
    }
}
