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
            _lastInputSig = string.Empty;
            _inputRevision++;
        }

        public void Clear()
        {
            _forceReplan = false;
            _cachedPlan = null;
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

            bool needsCheck = _forceReplan ||
                              _cachedPlan == null ||
                              _cachedPlan.MonsterId != monster.MonsterId ||
                              _cachedRevision != _inputRevision;

            if (needsCheck)
            {
                string sig = MonsterPathEngine.ComputeInputSignature(monster, effectivePlayers, options, layoutGeneration);
                if (_forceReplan ||
                    _cachedPlan == null ||
                    _cachedPlan.MonsterId != monster.MonsterId ||
                    !string.Equals(sig, _lastInputSig, StringComparison.Ordinal))
                {
                    _lastInputSig = sig;
                    var fn = planner ?? MonsterPathEngine.Plan;
                    _cachedPlan = fn(board, monster, effectivePlayers, options);
                    PlanComputationCount++;
                }
                _forceReplan = false;
                _cachedRevision = _inputRevision;
            }

            return _cachedPlan;
        }
    }
}
