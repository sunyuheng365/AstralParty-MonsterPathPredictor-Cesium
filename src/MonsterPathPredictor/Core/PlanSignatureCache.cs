using System;
using System.Collections.Generic;

namespace MonsterPathPredictor.Core
{
    /// <summary>
    /// Revision-gated signature cache for <see cref="MonsterPathEngine.Plan"/>.
    /// Separates snapshot revision checks (<see cref="MarkInputsMaybeChanged"/>) from forced replans
    /// (<see cref="InvalidateForceReplan"/>) so repeated identical snapshots perform zero redundant
    /// planner calls while any change in board/monster/player/option/layout inputs replans immediately.
    /// </summary>
    public sealed class PlanSignatureCache
    {
        private PathPlan _cachedPlan;
        private BoardGraph _cachedBoard;
        private int _cachedLayoutGen = int.MinValue;
        private string _lastInputSig = string.Empty;
        private int _inputRevision;
        private int _cachedRevision = -1;
        private bool _forceReplan;

        public PathPlan CachedPlan => _cachedPlan;
        public int InputRevision => _inputRevision;
        public int PlanComputationCount { get; private set; }

        public void MarkInputsMaybeChanged()
        {
            _inputRevision++;
        }

        public void InvalidateForceReplan()
        {
            _forceReplan = true;
            _cachedPlan = null;
            _cachedBoard = null;
            _cachedLayoutGen = int.MinValue;
            _lastInputSig = string.Empty;
            _inputRevision++;
        }

        public void Clear()
        {
            _forceReplan = false;
            _cachedPlan = null;
            _cachedBoard = null;
            _cachedLayoutGen = int.MinValue;
            _lastInputSig = string.Empty;
            _cachedRevision = -1;
        }

        public PathPlan GetOrCompute(
            BoardGraph board,
            MonsterState monster,
            IReadOnlyList<TrackedPlayer> effectivePlayers,
            PlanOptions options,
            int layoutGeneration,
            Func<BoardGraph, MonsterState, IReadOnlyList<TrackedPlayer>, PlanOptions, PathPlan> planner = null)
        {
            if (board == null || monster == null) return null;

            bool boardChanged = !ReferenceEquals(_cachedBoard, board) &&
                                (_cachedBoard == null || !_cachedBoard.SameTopology(board));
            bool layoutChanged = _cachedLayoutGen != layoutGeneration;
            bool needsCheck = _forceReplan ||
                              _cachedPlan == null ||
                              boardChanged ||
                              layoutChanged ||
                              _cachedPlan.MonsterId != monster.MonsterId ||
                              _cachedRevision != _inputRevision;

            if (needsCheck)
            {
                string sig = MonsterPathEngine.ComputeInputSignature(monster, effectivePlayers, options, layoutGeneration);
                if (_forceReplan ||
                    _cachedPlan == null ||
                    boardChanged ||
                    layoutChanged ||
                    _cachedPlan.MonsterId != monster.MonsterId ||
                    !string.Equals(sig, _lastInputSig, StringComparison.Ordinal))
                {
                    var fn = planner ?? MonsterPathEngine.Plan;
                    // Compute into a local first so an exception never pairs the new signature with an old plan.
                    var computed = fn(board, monster, effectivePlayers, options);
                    _cachedPlan = computed;
                    _cachedBoard = board;
                    _cachedLayoutGen = layoutGeneration;
                    _lastInputSig = sig;
                    PlanComputationCount++;
                }
                else
                {
                    _cachedBoard = board;
                    _cachedLayoutGen = layoutGeneration;
                }
                _forceReplan = false;
                _cachedRevision = _inputRevision;
            }

            return _cachedPlan;
        }
    }
}
