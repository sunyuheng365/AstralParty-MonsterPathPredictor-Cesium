using System;
using System.Collections.Generic;

namespace MonsterPathPredictor.Core
{
    public readonly struct TurnBoundaryResult
    {
        public bool BoundaryAdvanced { get; }
        public bool RoundChanged { get; }
        public bool ShouldResetMovementState { get; }

        public TurnBoundaryResult(bool boundaryAdvanced, bool roundChanged, bool shouldResetMovementState)
        {
            BoundaryAdvanced = boundaryAdvanced;
            RoundChanged = roundChanged;
            ShouldResetMovementState = shouldResetMovementState;
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

        private long _activeActorId;
        private int _activeActorStartedRound = -1;
        private int _lastObservedRound = -1;
        private bool _pendingNullRoundReset;
        private bool _staleActorRoundPending;
        private bool _activeActorProvisionalNextRound;
        private bool _actorAwaitingFirstDice;
        private int _turnSequence;
        private int _lastConsumedDiceEpoch;

        public long ActiveActorId => _activeActorId;
        public long EffectiveActiveActorId => _staleActorRoundPending ? 0L : _activeActorId;
        public bool IsCurrentActorStalePendingRound => _staleActorRoundPending;
        public int ActiveActorStartedRound => _activeActorStartedRound;
        public int LastObservedRound => _lastObservedRound;
        public int TurnSequence => _turnSequence;

        public void Reset(int currentDiceEpoch = 0)
        {
            _inferredActedSawServerTrue.Clear();
            _serverActedMonsterIdsThisRound.Clear();
            _activeActorId = 0;
            _activeActorStartedRound = -1;
            _lastObservedRound = -1;
            _pendingNullRoundReset = false;
            _staleActorRoundPending = false;
            _activeActorProvisionalNextRound = false;
            _actorAwaitingFirstDice = false;
            _turnSequence = 0;
            _lastConsumedDiceEpoch = currentDiceEpoch;
        }

        public bool HasActedThisRound(long monsterId)
        {
            if (monsterId == 0 || monsterId == EffectiveActiveActorId) return false;
            return _inferredActedSawServerTrue.ContainsKey(monsterId) ||
                   _serverActedMonsterIdsThisRound.Contains(monsterId);
        }

        public void MarkActedThisRound(long monsterId)
        {
            if (monsterId == 0 || monsterId == EffectiveActiveActorId) return;
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
        }

        /// <summary>
        /// Reconciles protocol <c>Hero.ActionEnd</c> against observed turn completions without letting
        /// a stale/lagging <c>ActionEnd=false</c> immediately erase a completion observed via A -&gt; B.
        /// </summary>
        public void ReconcileServerActionEnd(long monsterId, bool serverActionEnd, bool sameRoundBaseline)
        {
            if (monsterId == 0) return;
            if (monsterId == EffectiveActiveActorId)
            {
                _inferredActedSawServerTrue.Remove(monsterId);
                _serverActedMonsterIdsThisRound.Remove(monsterId);
                return;
            }

            if (serverActionEnd)
            {
                if (_inferredActedSawServerTrue.ContainsKey(monsterId))
                {
                    _inferredActedSawServerTrue[monsterId] = true;
                }
                if (sameRoundBaseline)
                {
                    _serverActedMonsterIdsThisRound.Add(monsterId);
                }
            }
            else
            {
                _serverActedMonsterIdsThisRound.Remove(monsterId);
                if (_inferredActedSawServerTrue.TryGetValue(monsterId, out bool sawServerTrue) && sawServerTrue)
                {
                    // Server previously confirmed true in this round and now explicitly reset back to false.
                    _inferredActedSawServerTrue.Remove(monsterId);
                }
            }
        }

        public void RemoveDeadMonster(long monsterId)
        {
            if (monsterId == 0) return;
            _inferredActedSawServerTrue.Remove(monsterId);
            _serverActedMonsterIdsThisRound.Remove(monsterId);
        }

        private void ClearActedSetsForNewRound()
        {
            _inferredActedSawServerTrue.Clear();
            _serverActedMonsterIdsThisRound.Clear();
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
            if (ownedRolls > 0)
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
            // Reject backwards/regressing round numbers within the same match.
            int acceptedRound = curRound;
            if (acceptedRound >= 0 && _lastObservedRound >= 0 && acceptedRound < _lastObservedRound)
            {
                acceptedRound = _lastObservedRound;
            }

            bool roundChanged = acceptedRound >= 0 && _lastObservedRound >= 0 && acceptedRound > _lastObservedRound;

            // Transient unavailable GetCurrentPlayer() while an actor was active:
            // preserve active turn within same round; flag _pendingNullRoundReset if round advanced.
            if (!hasCurrentActor && _activeActorId != 0)
            {
                if (roundChanged)
                {
                    ClearActedSetsForNewRound();
                    _lastObservedRound = acceptedRound;
                    _pendingNullRoundReset = true;
                    _staleActorRoundPending = false;
                    _activeActorProvisionalNextRound = false;
                }
                else if (acceptedRound >= 0 && _lastObservedRound < 0)
                {
                    _lastObservedRound = acceptedRound;
                    if (_activeActorStartedRound < 0) _activeActorStartedRound = acceptedRound;
                }
                return new TurnBoundaryResult(boundaryAdvanced: false, roundChanged: roundChanged, shouldResetMovementState: false);
            }

            bool actorChanged = curActorId != _activeActorId;

            if (roundChanged)
            {
                ClearActedSetsForNewRound();
                _lastObservedRound = acceptedRound;

                // Actor-before-round reconciliation: if the currently active actor was already flagged
                // as having started the upcoming round early (e.g. (7,A) -> (7,B) -> (8,B)), promote its
                // started round to the newly arrived round instead of marking it stale!
                if (hasCurrentActor && !actorChanged && _activeActorProvisionalNextRound)
                {
                    _activeActorStartedRound = acceptedRound;
                    _activeActorProvisionalNextRound = false;
                    _staleActorRoundPending = false;
                    // Still consume any newly arrived owned rolls for this actor below.
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

            int effectiveRound = acceptedRound >= 0 ? acceptedRound : _lastObservedRound;
            int remainingRolls = hasCurrentActor ? Math.Max(0, ownedNewRollsForActor) : 0;

            // Coalesce an actor's very first owned dice roll that arrives slightly AFTER actorChanged / recovery.
            // If 2+ owned rolls arrived between polls (first roll + extra turn roll), consume the first here
            // and let the remaining roll(s) advance the turn boundary below!
            if (remainingRolls > 0 && !actorChanged && _actorAwaitingFirstDice && !roundChanged && !_staleActorRoundPending && !_pendingNullRoundReset)
            {
                _actorAwaitingFirstDice = false;
                remainingRolls--;
                if (remainingRolls == 0)
                {
                    return new TurnBoundaryResult(boundaryAdvanced: false, roundChanged: false, shouldResetMovementState: false);
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
            bool incomingAlreadyActedInThisRound = hasCurrentActor &&
                (_inferredActedSawServerTrue.ContainsKey(curActorId) || _serverActedMonsterIdsThisRound.Contains(curActorId));

            // Detect actor-before-round transition (e.g. (7,A) -> (7,B) before Room.Round ticks to 8):
            // re-entering an already-acted monster, stepping to an earlier-priority monster, or monster -> hero.
            bool looksLikeEarlyNextRound =
                !roundChanged && actorChanged && hasCurrentActor && _activeActorId != 0 &&
                (incomingAlreadyActedInThisRound ||
                 (outgoingWasAliveMonster && incomingIsHero) ||
                 (outgoingWasAliveMonster && isEarlierRoundPriorityThanPrevious));

            if (!roundChanged && actorChanged && outgoingWasAliveMonster)
            {
                bool startedInCurrentRound = !_staleActorRoundPending &&
                                             !_pendingNullRoundReset &&
                                             (_activeActorStartedRound < 0 || effectiveRound < 0 || _activeActorStartedRound == effectiveRound);
                if (startedInCurrentRound)
                {
                    _inferredActedSawServerTrue[_activeActorId] = false;
                }
            }

            if (looksLikeEarlyNextRound)
            {
                // Clear previous-round acted set when actor-before-round proves a new round started early,
                // and mark the new actor as provisional so the upcoming round tick adopts it cleanly.
                if (_lastObservedRound < 0)
                {
                    ClearActedSetsForNewRound();
                }
            }

            // Fallback round clear ONLY while session lacks any reliable Room.Round baseline.
            if (_lastObservedRound < 0 && (_inferredActedSawServerTrue.Count > 0 || _serverActedMonsterIdsThisRound.Count > 0) && incomingIsHero)
            {
                ClearActedSetsForNewRound();
            }

            _turnSequence += Math.Max(1, remainingRolls);
            _activeActorId = hasCurrentActor ? curActorId : 0;
            _activeActorStartedRound = hasCurrentActor ? effectiveRound : -1;
            _pendingNullRoundReset = false;
            _staleActorRoundPending = false;
            _activeActorProvisionalNextRound = looksLikeEarlyNextRound && _lastObservedRound >= 0;

            // Every confirmed turn started WITHOUT consuming a roll awaits its first owned roll.
            _actorAwaitingFirstDice = hasCurrentActor && !hasNewOwnedDice;

            if (hasCurrentActor)
            {
                _inferredActedSawServerTrue.Remove(curActorId);
                _serverActedMonsterIdsThisRound.Remove(curActorId);
            }

            return new TurnBoundaryResult(boundaryAdvanced: true, roundChanged: roundChanged, shouldResetMovementState: true);
        }
    }
}
