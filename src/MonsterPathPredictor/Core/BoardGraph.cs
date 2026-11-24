using System;
using System.Collections.Generic;

namespace MonsterPathPredictor.Core
{
    /// <summary>
    /// Immutable snapshot of a single board tile captured from <c>LandManager</c>.
    /// Preserves the raw neighbor order without sorting (index 0 = red priority, index 1 = blue priority).
    /// Zero is a valid tile ID.
    /// </summary>
    public sealed class LandNodeSnapshot
    {
        public int Id { get; }
        public bool Disabled { get; }
        public float X { get; }
        public float Y { get; }
        public float Z { get; }
        public int[] NeighborIds { get; }

        public LandNodeSnapshot(int id, int[] neighborIds, bool disabled = false, float x = 0f, float y = 0f, float z = 0f)
        {
            Id = id;
            Disabled = disabled;
            X = x;
            Y = y;
            Z = z;
            NeighborIds = neighborIds != null ? (int[])neighborIds.Clone() : Array.Empty<int>();
        }
    }

    /// <summary>
    /// Precomputed shortest-path distance and equal-length shortest-path counts
    /// from every reachable board tile to a specific target tile.
    /// </summary>
    public sealed class ShortestPathField
    {
        private readonly Dictionary<int, int> _distances;
        private readonly Dictionary<int, double> _exactPathCounts;
        private readonly Dictionary<int, double> _logPathCounts;

        public int TargetLandId { get; }

        internal ShortestPathField(
            int targetLandId,
            Dictionary<int, int> distances,
            Dictionary<int, double> exactPathCounts,
            Dictionary<int, double> logPathCounts)
        {
            TargetLandId = targetLandId;
            _distances = distances;
            _exactPathCounts = exactPathCounts;
            _logPathCounts = logPathCounts;
        }

        public bool IsReachable(int fromLandId) => _distances.ContainsKey(fromLandId);

        public int GetDistance(int fromLandId)
        {
            return _distances.TryGetValue(fromLandId, out int dist) ? dist : int.MaxValue;
        }

        /// <summary>
        /// Natural logarithm of the exact shortest-path count (never overflows IEEE-754 double even on
        /// arbitrarily deep diamond graphs). Returns <see cref="double.NegativeInfinity"/> if unreachable.
        /// </summary>
        public double GetLogPathCount(int fromLandId)
        {
            return _logPathCounts.TryGetValue(fromLandId, out double logCount) ? logCount : double.NegativeInfinity;
        }

        /// <summary>
        /// Exact shortest-path count for all integers representable in IEEE-754 double (exact up to 2^53,
        /// avoiding log/exp round-trip error such as 7.999999999999998 for 8). Overflows to +Infinity
        /// beyond double.MaxValue; use <see cref="GetLogPathCount"/> for overflow-free ratio math.
        /// </summary>
        public double GetPathCount(int fromLandId)
        {
            return _exactPathCounts.TryGetValue(fromLandId, out double count) ? count : 0.0;
        }
    }

    /// <summary>
    /// Pure managed board topology graph with exact raw neighbor priority preservation
    /// and multi-path shortest-distance BFS counting.
    /// </summary>
    public sealed class BoardGraph
    {
        private static readonly IReadOnlyList<int> EmptyIds = Array.Empty<int>();

        private readonly Dictionary<int, LandNodeSnapshot> _nodes = new Dictionary<int, LandNodeSnapshot>();
        private readonly Dictionary<int, int[]> _validNeighbors = new Dictionary<int, int[]>();
        private readonly Dictionary<int, List<int>> _reverseAdjacency = new Dictionary<int, List<int>>();

        public int Count => _nodes.Count;

        public BoardGraph(IEnumerable<LandNodeSnapshot> nodes)
        {
            if (nodes == null) throw new ArgumentNullException(nameof(nodes));

            foreach (var node in nodes)
            {
                if (node == null) continue;
                _nodes[node.Id] = node;
            }

            foreach (var pair in _nodes)
            {
                var node = pair.Value;
                if (node.Disabled)
                {
                    _validNeighbors[node.Id] = Array.Empty<int>();
                    continue;
                }

                var list = new List<int>(node.NeighborIds.Length);
                var seen = new HashSet<int>();
                for (int i = 0; i < node.NeighborIds.Length; i++)
                {
                    int nextId = node.NeighborIds[i];
                    if (nextId == node.Id) continue;
                    if (!seen.Add(nextId)) continue;
                    if (!_nodes.TryGetValue(nextId, out var nextNode) || nextNode.Disabled) continue;
                    list.Add(nextId);

                    if (!_reverseAdjacency.TryGetValue(nextId, out var rev))
                    {
                        rev = new List<int>();
                        _reverseAdjacency[nextId] = rev;
                    }
                    rev.Add(node.Id);
                }

                _validNeighbors[node.Id] = list.ToArray();
            }
        }

        public bool Contains(int landId) => _nodes.TryGetValue(landId, out var node) && !node.Disabled;

        public bool TryGetNode(int landId, out LandNodeSnapshot node)
        {
            if (_nodes.TryGetValue(landId, out node) && !node.Disabled) return true;
            node = null;
            return false;
        }

        public IEnumerable<LandNodeSnapshot> ActiveNodes()
        {
            foreach (var node in _nodes.Values)
            {
                if (!node.Disabled) yield return node;
            }
        }

        /// <summary>
        /// Valid active neighbors in exact raw game adjacency order (index 0 = red arrow, index 1 = blue arrow).
        /// </summary>
        public IReadOnlyList<int> GetValidNeighbors(int landId)
        {
            return _validNeighbors.TryGetValue(landId, out var arr) ? arr : EmptyIds;
        }

        public bool IsValidNeighbor(int landId, int candidateId)
        {
            var neighbors = GetValidNeighbors(landId);
            for (int i = 0; i < neighbors.Count; i++)
            {
                if (neighbors[i] == candidateId) return true;
            }
            return false;
        }

        /// <summary>
        /// A junction/fork tile has 3 or more valid active neighbors.
        /// </summary>
        public bool IsFork(int landId) => GetValidNeighbors(landId).Count >= 3;

        /// <summary>
        /// Context-aware fork predicate shared by planning and replay certification:
        /// true on standard junctions (&gt;= 3 neighbors) AND on directed mid-turn splits where
        /// arriving from <paramref name="fromLandId"/> leaves at least 2 forward exits.
        /// </summary>
        public bool IsForkForContext(int landId, int fromLandId, bool isCurrentlyMovingMidTurn)
        {
            if (IsFork(landId)) return true;
            return isCurrentlyMovingMidTurn && GetForwardNeighbors(landId, fromLandId).Count >= 2;
        }

        /// <summary>
        /// Forward neighbors from <paramref name="landId"/> excluding <paramref name="fromLandId"/> (no U-turn),
        /// preserving the exact raw priority order (first remaining = red priority, second = blue priority).
        /// </summary>
        public IReadOnlyList<int> GetForwardNeighbors(int landId, int fromLandId)
        {
            var all = GetValidNeighbors(landId);
            if (all.Count == 0) return EmptyIds;

            var list = new List<int>(all.Count);
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] != fromLandId) list.Add(all[i]);
            }
            return list;
        }

        /// <summary>
        /// Returns the highest-priority forward neighbor at <paramref name="landId"/> when arriving from
        /// <paramref name="fromLandId"/>, along with whether it is the junction's global Rank-0 (red) arrow.
        /// </summary>
        public bool TryGetPriorityForwardNeighbor(int landId, int fromLandId, out int nextLandId, out bool isGlobalRed)
        {
            nextLandId = -1;
            isGlobalRed = false;
            var all = GetValidNeighbors(landId);
            if (all.Count == 0) return false;

            int globalRed = all[0];
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] == fromLandId) continue;
                nextLandId = all[i];
                isGlobalRed = (nextLandId == globalRed);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Returns the highest-priority neighbor at <paramref name="landId"/> (in exact raw game adjacency order:
        /// index 0 = red, index 1 = blue, ...) restricted to <paramref name="allowedNeighbors"/>.
        /// </summary>
        public bool TryGetPriorityAmongCandidates(
            int landId,
            IReadOnlyList<int> allowedNeighbors,
            out int nextLandId,
            out bool isGlobalRed)
        {
            nextLandId = -1;
            isGlobalRed = false;
            if (allowedNeighbors == null || allowedNeighbors.Count == 0) return false;

            var all = GetValidNeighbors(landId);
            if (all.Count == 0) return false;

            int globalRed = all[0];
            for (int i = 0; i < all.Count; i++)
            {
                int candidate = all[i];
                for (int j = 0; j < allowedNeighbors.Count; j++)
                {
                    if (allowedNeighbors[j] == candidate)
                    {
                        nextLandId = candidate;
                        isGlobalRed = (candidate == globalRed);
                        return true;
                    }
                }
            }

            return false;
        }

        internal static double LogAddExp(double a, double b)
        {
            if (double.IsNegativeInfinity(a)) return b;
            if (double.IsNegativeInfinity(b)) return a;
            double hi = a >= b ? a : b;
            double lo = a >= b ? b : a;
            return hi + Math.Log(1.0 + Math.Exp(lo - hi));
        }

        /// <summary>
        /// Computes full-board shortest road distances and both exact integer (up to 2^53) and overflow-free
        /// log-scale equal-length shortest-path counts from every reachable tile to <paramref name="targetLandId"/>.
        /// </summary>
        public ShortestPathField ComputeShortestPathsTo(int targetLandId)
        {
            var distances = new Dictionary<int, int>();
            var exactCounts = new Dictionary<int, double>();
            var logCounts = new Dictionary<int, double>();

            if (!Contains(targetLandId))
            {
                return new ShortestPathField(targetLandId, distances, exactCounts, logCounts);
            }

            var queue = new Queue<int>();
            distances[targetLandId] = 0;
            exactCounts[targetLandId] = 1.0;
            logCounts[targetLandId] = 0.0; // ln(1) = 0
            queue.Enqueue(targetLandId);

            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                int curDist = distances[cur];
                double curExact = exactCounts[cur];
                double curLogCount = logCounts[cur];

                if (!_reverseAdjacency.TryGetValue(cur, out var predecessors)) continue;
                for (int i = 0; i < predecessors.Count; i++)
                {
                    int prev = predecessors[i];
                    if (!distances.TryGetValue(prev, out int prevDist))
                    {
                        distances[prev] = curDist + 1;
                        exactCounts[prev] = curExact;
                        logCounts[prev] = curLogCount;
                        queue.Enqueue(prev);
                    }
                    else if (prevDist == curDist + 1)
                    {
                        exactCounts[prev] += curExact;
                        logCounts[prev] = LogAddExp(logCounts[prev], curLogCount);
                    }
                }
            }

            return new ShortestPathField(targetLandId, distances, exactCounts, logCounts);
        }

        public bool SameTopology(BoardGraph other)
        {
            if (other == null || _nodes.Count != other._nodes.Count) return false;
            foreach (var pair in _nodes)
            {
                if (!other._nodes.TryGetValue(pair.Key, out var b)) return false;
                var a = pair.Value;
                if (a.Disabled != b.Disabled ||
                    a.X != b.X || a.Y != b.Y || a.Z != b.Z ||
                    a.NeighborIds.Length != b.NeighborIds.Length)
                {
                    return false;
                }
                for (int i = 0; i < a.NeighborIds.Length; i++)
                {
                    if (a.NeighborIds[i] != b.NeighborIds[i]) return false;
                }
            }
            return true;
        }
    }
}
