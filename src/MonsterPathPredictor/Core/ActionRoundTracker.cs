using System;
using System.Collections.Generic;

namespace MonsterPathPredictor.Core
{
    public readonly struct TurnBoundaryResult
    {
        public bool BoundaryAdvanced { get; }
        public bool RoundChanged { get; }
        public bool ShouldResetMovementState { get; }
        public bool SampleRejected { get; }

        public TurnBoundaryResult(
            bool boundaryAdvanced,
            bool roundChanged,
            bool shouldResetMovementState,
            bool sampleRejected = false)
        {
            BoundaryAdvanced = boundaryAdvanced;
            RoundChanged = roundChanged;
            ShouldResetMovementState = shouldResetMovementState;
            SampleRejected = sampleRejected;
        }
    }

    /// <summary>
    /// Pure-C# turn/round/acted-monster tracker handling all asynchronous client delivery orders:
    ///   - (7,A) -> (8,A) -> (8,A) -> (8,B): repeated stale-actor reads never fabricate a round-8 turn for A.
    ///   - (7,A) -> (7,null) -> (7,A): transient null actor preserves A's active turn state.
    ///   - (7,A) -> (8,null) -> (8,A): pending null-round reset advances cleanly once identity returns.
    ///   - (3,A) -> (3,B,dice=1) -> (3,B,dice=2,owner=B): actor-first first dice roll coalesces with B's turn.
    ///   - (7,A) -> (8,A) -> (-1,B): unavailable round read uses _lastObservedRound=8, never miscrediting A.
    /// </summary>
    public sealed class ActionRoundTracker
    {
        // Inferred completions observed from actor switches (A -> B): value tracks whether server ActionEnd=true
        // has caught up yet, preventing a lagging server ActionEnd=false from erasing a just-completed turn.
        private readonly Dictionary<long, bool> _inferredActedSawServerTrue = new Dictionary<long, bool>();
        private readonly HashSet<long> _serverActedMonsterIdsThisRound = new HashSet<long>();
        private readonly Dictionary<long, bool> _provisionalInferredActedSawServerTrue = new Dictionary<long, bool>();
        private readonly Dictionary<long, (bool Value, int Round, bool IsCarriedStale)> _serverActionEndHistory =
            new Dictionary<long, (bool Value, int Round, bool IsCarriedStale)>();

        private long _activeActorId;
        private int _activeActorStartedRound = -1;
        private int _lastObservedRound = -1;
        private bool _pendingNullRoundReset;
        private bool _staleActorRoundPending;
        private bool _activeActorProvisionalNextRound;
        private bool _hasProvisionalNextRound;
        private int _provisionalNextRoundNumber = -1;
        private bool _activeActorSawServerActionEndTrue;
        private bool _actorAwaitingFirstDice;
        private int _turnSequence;
        private int _lastConsumedDiceEpoch;

        public long ActiveActorId => _activeActorId;
        public long EffectiveActiveActorId => (_staleActorRoundPending || _pendingNullRoundReset) ? 0L : _activeActorId;
        public bool IsCurrentActorStalePendingRound => _staleActorRoundPending;
        public bool IsPendingNullRoundReset => _pendingNullRoundReset;
        public bool IsProvisionalNextRoundActive => _hasProvisionalNextRound;
        public int ActiveActorStartedRound => _activeActorStartedRound;
        public int LastObservedRound => _lastObservedRound;
        public int EffectiveRoundNumber => _hasProvisionalNextRound ? _provisionalNextRoundNumber : _lastObservedRound;
        public int TurnSequence => _turnSequence;

        public void Reset(int currentDiceEpoch = 0)
        {
            _inferredActedSawServerTrue.Clear();
            _serverActedMonsterIdsThisRound.Clear();
            _provisionalInferredActedSawServerTrue.Clear();
            _serverActionEndHistory.Clear();
            _activeActorId = 0;
            _activeActorStartedRound = -1;
            _lastObservedRound = -1;
            _pendingNullRoundReset = false;
            _staleActorRoundPending = false;
            _activeActorProvisionalNextRound = false;
            _hasProvisionalNextRound = false;
            _provisionalNextRoundNumber = -1;
            _activeActorSawServerActionEndTrue = false;
            _actorAwaitingFirstDice = false;
            _turnSequence = 0;
            _lastConsumedDiceEpoch = currentDiceEpoch;
        }

        public bool HasActedThisRound(long monsterId)
        {
            if (monsterId == 0 || monsterId == EffectiveActiveActorId) return false;
            if (_hasProvisionalNextRound)
            {
                return _provisionalInferredActedSawServerTrue.ContainsKey(monsterId);
            }
            return _inferredActedSawServerTrue.ContainsKey(monsterId) ||
                   _serverActedMonsterIdsThisRound.Contains(monsterId);
        }

        public void MarkActedThisRound(long monsterId)
        {
            if (monsterId == 0 || monsterId == EffectiveActiveActorId) return;
            if (_hasProvisionalNextRound)
            {
                _provisionalInferredActedSawServerTrue[monsterId] = true;
                return;
            }
            if (_inferredActedSawServerTrue.ContainsKey(monsterId))
            {
                _inferredActedSawServerTrue[monsterId] = true;
            }
            _serverActedMonsterIdsThisRound.Add(monsterId);
        }

        public void ClearActedThisRound(long monsterId)
        {
            if (monsterId == 0) return;
            _inferredActedSawServerTrue.Remove(monsterId);
            _serverActedMonsterIdsThisRound.Remove(monsterId);
            _provisionalInferredActedSawServerTrue.Remove(monsterId);
        }

        /// <summary>
        /// Reconciles protocol <c>Hero.ActionEnd</c> against observed turn completions without letting
        /// a stale/lagging <c>ActionEnd=false</c> erase a just-completed turn or a carried prior-round
        /// <c>ActionEnd=true</c> miscredit the new round, while still crediting genuine cross-round
        /// <c>false -&gt; true</c> completions and preserving confirmations sampled while active.
        /// </summary>
        public void ReconcileServerActionEnd(long monsterId, bool serverActionEnd, int currentRound)
        {
            if (monsterId == 0) return;

            // Ignore samples stamped with a regressing round number.
            if (currentRound >= 0 && _lastObservedRound >= 0 && currentRound < _lastObservedRound)
            {
                return;
            }

            // While waiting for Room.Round to catch up to an already-started provisional next round,
            // non-active monsters' protocol ActionEnd still reflects the previous round.
            bool laggingRoundDuringProvisional =
                _hasProvisionalNextRound && currentRound >= 0 && currentRound < _provisionalNextRoundNumber;

            int roundKey = currentRound >= 0 ? currentRound : EffectiveRoundNumber;
            bool hasPrev = _serverActionEndHistory.TryGetValue(monsterId, out var prev);

            if (monsterId == EffectiveActiveActorId)
            {
                _inferredActedSawServerTrue.Remove(monsterId);
                _serverActedMonsterIdsThisRound.Remove(monsterId);
                _provisionalInferredActedSawServerTrue.Remove(monsterId);

                int activeRoundKey = _activeActorStartedRound >= 0 ? _activeActorStartedRound : roundKey;
                bool isCarriedStaleTrue = serverActionEnd && hasPrev && prev.Value &&
                    (prev.IsCarriedStale || (activeRoundKey >= 0 && prev.Round >= 0 && prev.Round < activeRoundKey));

                _serverActionEndHistory[monsterId] = (serverActionEnd, isCarriedStaleTrue ? prev.Round : activeRoundKey, isCarriedStaleTrue);

                if (serverActionEnd)
                {
                    if (!isCarriedStaleTrue)
                    {
                        _activeActorSawServerActionEndTrue = true;
                    }
                }
                else
                {
                    _activeActorSawServerActionEndTrue = false;
                }
                return;
            }

            if (laggingRoundDuringProvisional)
            {
                // Still record raw history on the lagging round without mutating provisional next-round acted state.
                if (!hasPrev || prev.Round <= currentRound)
                {
                    _serverActionEndHistory[monsterId] = (serverActionEnd, currentRound, false);
                }
                return;
            }

            bool isCarriedStale = serverActionEnd && hasPrev && prev.Value &&
                (prev.IsCarriedStale || (roundKey >= 0 && prev.Round >= 0 && prev.Round < roundKey));
            _serverActionEndHistory[monsterId] = (serverActionEnd, isCarriedStale ? prev.Round : roundKey, isCarriedStale);

            if (serverActionEnd)
            {
                if (!isCarriedStale)
                {
                    if (_inferredActedSawServerTrue.ContainsKey(monsterId))
                    {
                        _inferredActedSawServerTrue[monsterId] = true;
                    }
                    _serverActedMonsterIdsThisRound.Add(monsterId);
                }
            }
            else
            {
                _serverActedMonsterIdsThisRound.Remove(monsterId);
                if (_inferredActedSawServerTrue.TryGetValue(monsterId, out bool sawServerTrue) && sawServerTrue)
                {
                    // Server previously confirmed true in this round (or while active) and now explicitly reset back to false.
                    _inferredActedSawServerTrue.Remove(monsterId);
                }
            }
        }

        public void SeedInitialServerActionEnd(long monsterId, bool serverActionEnd, int currentRound)
        {
            if (monsterId == 0) return;
            // Reject regressing round samples on initial seeding as well.
            if (currentRound >= 0 && _lastObservedRound >= 0 && currentRound < _lastObservedRound)
            {
                return;
            }

            bool laggingRoundDuringProvisional =
                _hasProvisionalNextRound && currentRound >= 0 && currentRound < _provisionalNextRoundNumber;

            int roundKey = currentRound >= 0 ? currentRound : EffectiveRoundNumber;
            if (monsterId == EffectiveActiveActorId)
            {
                int activeRoundKey = _activeActorStartedRound >= 0 ? _activeActorStartedRound : roundKey;
                _serverActionEndHistory[monsterId] = (serverActionEnd, activeRoundKey, false);
                _activeActorSawServerActionEndTrue = serverActionEnd;
                return;
            }

            if (laggingRoundDuringProvisional)
            {
                // Record raw baseline on the lagging round without miscrediting provisional next-round acted set!
                _serverActionEndHistory[monsterId] = (serverActionEnd, currentRound, false);
                return;
            }

            _serverActionEndHistory[monsterId] = (serverActionEnd, roundKey, false);
            if (serverActionEnd)
            {
                MarkActedThisRound(monsterId);
            }
        }

        public void RemoveDeadMonster(long monsterId)
        {
            if (monsterId == 0) return;
            _inferredActedSawServerTrue.Remove(monsterId);
            _serverActedMonsterIdsThisRound.Remove(monsterId);
            _provisionalInferredActedSawServerTrue.Remove(monsterId);
            _serverActionEndHistory.Remove(monsterId);
        }

        private void ClearActedSetsForNewRound()
        {
            _inferredActedSawServerTrue.Clear();
            _serverActedMonsterIdsThisRound.Clear();
        }

        private void PromoteProvisionalRound(int acceptedRound)
        {
            ClearActedSetsForNewRound();
            if (acceptedRound == _provisionalNextRoundNumber)
            {
                foreach (var kv in _provisionalInferredActedSawServerTrue)
                {
                    _inferredActedSawServerTrue[kv.Key] = kv.Value;
                }
                if (_activeActorProvisionalNextRound)
                {
                    _activeActorStartedRound = acceptedRound;
                    _staleActorRoundPending = false;
                }
            }
            _provisionalInferredActedSawServerTrue.Clear();
            _hasProvisionalNextRound = false;
            _provisionalNextRoundNumber = -1;
            _activeActorProvisionalNextRound = false;
        }

        public TurnBoundaryResult Observe(
            bool hasCurrentActor,
            long curActorId,
            int curRound,
            int latestDiceEpoch,
            long latestDiceOwnerId,
            Func<long, bool> isAliveMonster,
            Func<long, bool> isHeroUnit,
            bool isEarlierRoundPriorityThanPrevious = false)
        {
            int ownedRolls = 0;
            if (hasCurrentActor &&
                latestDiceEpoch != _lastConsumedDiceEpoch &&
                latestDiceOwnerId > 0 &&
                latestDiceOwnerId == curActorId)
            {
                ownedRolls = 1;
            }
            var res = ObserveWithOwnedRolls(
                hasCurrentActor,
                curActorId,
                curRound,
                ownedRolls,
                isAliveMonster,
                isHeroUnit,
                isEarlierRoundPriorityThanPrevious);
            if (!res.SampleRejected && ownedRolls > 0)
            {
                _lastConsumedDiceEpoch = latestDiceEpoch;
            }
            return res;
        }

        public TurnBoundaryResult ObserveWithOwnedRolls(
            bool hasCurrentActor,
            long curActorId,
            int curRound,
            int ownedNewRollsForActor,
            Func<long, bool> isAliveMonster,
            Func<long, bool> isHeroUnit,
            bool isEarlierRoundPriorityThanPrevious = false)
        {
            // Reject regressing round numbers within the same match completely so a stale sample
            // like (8, A) after accepted (9, B) can never roll the actor backwards or consume rolls.
            if (curRound >= 0 && _lastObservedRound >= 0 && curRound < _lastObservedRound)
            {
                return new TurnBoundaryResult(
                    boundaryAdvanced: false,
                    roundChanged: false,
                    shouldResetMovementState: false,
                    sampleRejected: true);
            }

            int acceptedRound = curRound;
            bool roundChanged = acceptedRound >= 0 && _lastObservedRound >= 0 && acceptedRound > _lastObservedRound;

            // Transient unavailable GetCurrentPlayer() while an actor was active:
            // preserve active turn within same round; promote matching provisional round cleanly,
            // or flag _pendingNullRoundReset if a genuinely new round arrived without an actor.
            if (!hasCurrentActor && _activeActorId != 0)
            {
                if (roundChanged)
                {
                    if (_hasProvisionalNextRound && acceptedRound == _provisionalNextRoundNumber)
                    {
                        PromoteProvisionalRound(acceptedRound);
                        _lastObservedRound = acceptedRound;
                    }
                    else
                    {
                        PromoteProvisionalRound(-1);
                        _lastObservedRound = acceptedRound;
                        _pendingNullRoundReset = true;
                        _staleActorRoundPending = false;
                    }
                }
                else if (acceptedRound >= 0 && _lastObservedRound < 0)
                {
                    _lastObservedRound = acceptedRound;
                    if (_activeActorStartedRound < 0) _activeActorStartedRound = acceptedRound;
                }
                return new TurnBoundaryResult(boundaryAdvanced: false, roundChanged: roundChanged, shouldResetMovementState: false);
            }

            bool actorChanged = curActorId != _activeActorId;
            bool promotedProvisionalRound = false;

            if (roundChanged)
            {
                _lastObservedRound = acceptedRound;
                if (_hasProvisionalNextRound && acceptedRound == _provisionalNextRoundNumber)
                {
                    // Actor-before-round reconciliation: promote the provisional round and its
                    // completions BEFORE processing the incoming actor or coalescing its first roll.
                    PromoteProvisionalRound(acceptedRound);
                    promotedProvisionalRound = true;
                }
                else
                {
                    PromoteProvisionalRound(-1);
                }
            }
            else if (acceptedRound >= 0 && _lastObservedRound < 0)
            {
                _lastObservedRound = acceptedRound;
                if (_activeActorId != 0 && _activeActorStartedRound < 0)
                {
                    _activeActorStartedRound = acceptedRound;
                }
            }

            int effectiveRound = _hasProvisionalNextRound
                ? _provisionalNextRoundNumber
                : (acceptedRound >= 0 ? acceptedRound : _lastObservedRound);
            int remainingRolls = hasCurrentActor ? Math.Max(0, ownedNewRollsForActor) : 0;

            // Coalesce an actor's very first owned dice roll that arrives slightly AFTER actorChanged,
            // recovery, or provisional-round promotion. If 2+ owned rolls arrived between polls
            // (first roll + extra turn roll), consume the first here and advance on the remainder.
            if (remainingRolls > 0 && !actorChanged && _actorAwaitingFirstDice &&
                (!roundChanged || promotedProvisionalRound) &&
                !_staleActorRoundPending && !_pendingNullRoundReset)
            {
                _actorAwaitingFirstDice = false;
                remainingRolls--;
                if (remainingRolls == 0)
                {
                    return new TurnBoundaryResult(boundaryAdvanced: false, roundChanged: roundChanged, shouldResetMovementState: false);
                }
            }

            bool hasNewOwnedDice = remainingRolls > 0;

            // Round ticked while GetCurrentPlayer() STILL returns the previous round's actor (with no owned new roll
            // and NOT reconciled from provisional next-round start): keep _staleActorRoundPending latched across
            // repeated reads until actor changes or an owned roll arrives!
            bool sameActorStartedBeforeCurrentRound =
                hasCurrentActor && !actorChanged && effectiveRound >= 0 &&
                _activeActorStartedRound >= 0 && _activeActorStartedRound < effectiveRound;
            if ((roundChanged || _staleActorRoundPending) && sameActorStartedBeforeCurrentRound && !hasNewOwnedDice)
            {
                _staleActorRoundPending = true;
                return new TurnBoundaryResult(boundaryAdvanced: false, roundChanged: roundChanged, shouldResetMovementState: false);
            }

            bool boundaryAdvanced = actorChanged || hasNewOwnedDice || (_pendingNullRoundReset && hasCurrentActor);
            if (!boundaryAdvanced)
            {
                return new TurnBoundaryResult(boundaryAdvanced: false, roundChanged: roundChanged, shouldResetMovementState: false);
            }

            bool outgoingWasAliveMonster = _activeActorId != 0 && isAliveMonster != null && isAliveMonster(_activeActorId);
            bool incomingIsHero = hasCurrentActor && isHeroUnit != null && isHeroUnit(curActorId);
            bool incomingAlreadyActedInActiveRound = hasCurrentActor && (_hasProvisionalNextRound
                ? _provisionalInferredActedSawServerTrue.ContainsKey(curActorId)
                : (_inferredActedSawServerTrue.ContainsKey(curActorId) || _serverActedMonsterIdsThisRound.Contains(curActorId)));

            // Detect actor-before-round transition (e.g. (7,A) -> (7,B) before Room.Round ticks to 8):
            // re-entering an already-acted monster, stepping to an earlier-priority monster, or monster -> hero.
            // Never trigger when recovering from a stale previous-round actor or null-round reset (round already ticked!).
            bool looksLikeEarlyNextRound =
                !roundChanged && actorChanged && hasCurrentActor && _activeActorId != 0 &&
                !_hasProvisionalNextRound && !_staleActorRoundPending && !_pendingNullRoundReset &&
                (incomingAlreadyActedInActiveRound ||
                 (outgoingWasAliveMonster && incomingIsHero) ||
                 (outgoingWasAliveMonster && isEarlierRoundPriorityThanPrevious));

            if ((!roundChanged || promotedProvisionalRound) && actorChanged && outgoingWasAliveMonster)
            {
                bool startedInActiveRound = !_staleActorRoundPending &&
                                            !_pendingNullRoundReset &&
                                            (_activeActorStartedRound < 0 || effectiveRound < 0 || _activeActorStartedRound == effectiveRound);
                if (startedInActiveRound)
                {
                    if (_hasProvisionalNextRound)
                    {
                        _provisionalInferredActedSawServerTrue[_activeActorId] = _activeActorSawServerActionEndTrue;
                    }
                    else
                    {
                        _inferredActedSawServerTrue[_activeActorId] = _activeActorSawServerActionEndTrue;
                    }
                }
            }

            if (looksLikeEarlyNextRound)
            {
                if (_lastObservedRound < 0)
                {
                    ClearActedSetsForNewRound();
                }
                else
                {
                    _hasProvisionalNextRound = true;
                    _provisionalNextRoundNumber = _lastObservedRound + 1;
                    _provisionalInferredActedSawServerTrue.Clear();
                    _activeActorProvisionalNextRound = true;
                }
            }
            else if (_hasProvisionalNextRound)
            {
                // Preserve provisional round membership across same-actor extra rolls and additional
                // early-round transitions until Room.Round catches up.
                _activeActorProvisionalNextRound = hasCurrentActor;
            }
            else
            {
                _activeActorProvisionalNextRound = false;
            }

            // Fallback round clear ONLY while session lacks any reliable Room.Round baseline.
            if (_lastObservedRound < 0 && (_inferredActedSawServerTrue.Count > 0 || _serverActedMonsterIdsThisRound.Count > 0) && incomingIsHero)
            {
                ClearActedSetsForNewRound();
            }

            _turnSequence += Math.Max(1, remainingRolls);
            _activeActorId = hasCurrentActor ? curActorId : 0;
            int nextActorRound = _hasProvisionalNextRound ? _provisionalNextRoundNumber : effectiveRound;
            _activeActorStartedRound = hasCurrentActor ? nextActorRound : -1;
            _pendingNullRoundReset = false;
            _staleActorRoundPending = false;
            _activeActorSawServerActionEndTrue = false;

            // Every confirmed turn started WITHOUT consuming a roll awaits its first owned roll.
            _actorAwaitingFirstDice = hasCurrentActor && !hasNewOwnedDice;

            if (hasCurrentActor)
            {
                _inferredActedSawServerTrue.Remove(curActorId);
                _serverActedMonsterIdsThisRound.Remove(curActorId);
                _provisionalInferredActedSawServerTrue.Remove(curActorId);
            }

            return new TurnBoundaryResult(boundaryAdvanced: true, roundChanged: roundChanged, shouldResetMovementState: true);
        }
    }
}
