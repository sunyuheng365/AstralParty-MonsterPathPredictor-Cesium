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
        private readonly HashSet<long> _actedMonsterIdsThisRound = new HashSet<long>();

        private long _activeActorId;
        private int _activeActorStartedRound = -1;
        private int _lastObservedRound = -1;
        private bool _pendingNullRoundReset;
        private bool _staleActorRoundPending;
        private bool _actorAwaitingFirstDice;
        private int _turnSequence;
        private int _lastConsumedDiceEpoch;

        public long ActiveActorId => _activeActorId;
        public int ActiveActorStartedRound => _activeActorStartedRound;
        public int LastObservedRound => _lastObservedRound;
        public int TurnSequence => _turnSequence;

        public void Reset(int currentDiceEpoch = 0)
        {
            _actedMonsterIdsThisRound.Clear();
            _activeActorId = 0;
            _activeActorStartedRound = -1;
            _lastObservedRound = -1;
            _pendingNullRoundReset = false;
            _staleActorRoundPending = false;
            _actorAwaitingFirstDice = false;
            _turnSequence = 0;
            _lastConsumedDiceEpoch = currentDiceEpoch;
        }

        public bool HasActedThisRound(long monsterId) => monsterId != 0 && _actedMonsterIdsThisRound.Contains(monsterId);

        public void MarkActedThisRound(long monsterId)
        {
            if (monsterId != 0 && monsterId != _activeActorId)
                _actedMonsterIdsThisRound.Add(monsterId);
        }

        public void ClearActedThisRound(long monsterId)
        {
            if (monsterId != 0) _actedMonsterIdsThisRound.Remove(monsterId);
        }

        public void RemoveDeadMonster(long monsterId)
        {
            if (monsterId != 0) _actedMonsterIdsThisRound.Remove(monsterId);
        }

        public TurnBoundaryResult Observe(
            bool hasCurrentActor,
            long curActorId,
            int curRound,
            int latestDiceEpoch,
            long latestDiceOwnerId,
            Func<long, bool> isAliveMonster,
            Func<long, bool> isHeroUnit)
        {
            bool roundChanged = curRound >= 0 && _lastObservedRound >= 0 && curRound != _lastObservedRound;

            // Transient unavailable GetCurrentPlayer() while an actor was active:
            // preserve active turn within same round; flag _pendingNullRoundReset if round advanced.
            if (!hasCurrentActor && _activeActorId != 0)
            {
                if (roundChanged)
                {
                    _actedMonsterIdsThisRound.Clear();
                    _lastObservedRound = curRound;
                    _pendingNullRoundReset = true;
                    _staleActorRoundPending = false;
                }
                else if (curRound >= 0 && _lastObservedRound < 0)
                {
                    _lastObservedRound = curRound;
                }
                return new TurnBoundaryResult(boundaryAdvanced: false, roundChanged: roundChanged, shouldResetMovementState: false);
            }

            if (roundChanged)
            {
                _actedMonsterIdsThisRound.Clear();
                _lastObservedRound = curRound;
            }
            else if (curRound >= 0 && _lastObservedRound < 0)
            {
                _lastObservedRound = curRound;
            }

            int effectiveRound = curRound >= 0 ? curRound : _lastObservedRound;
            bool actorChanged = curActorId != _activeActorId;

            // Require explicit positive ownership (latestDiceOwnerId == curActorId) to advance an unchanged actor.
            bool hasNewOwnedDice = hasCurrentActor &&
                                   latestDiceEpoch != _lastConsumedDiceEpoch &&
                                   latestDiceOwnerId > 0 &&
                                   latestDiceOwnerId == curActorId;

            // Coalesce an actor's very first owned dice roll that arrives slightly AFTER actorChanged!
            if (hasNewOwnedDice && !actorChanged && _actorAwaitingFirstDice && !roundChanged && !_staleActorRoundPending && !_pendingNullRoundReset)
            {
                _actorAwaitingFirstDice = false;
                _lastConsumedDiceEpoch = latestDiceEpoch;
                return new TurnBoundaryResult(boundaryAdvanced: false, roundChanged: false, shouldResetMovementState: false);
            }

            // Round ticked while GetCurrentPlayer() STILL returns the previous round's actor (with no owned new roll):
            // keep _staleActorRoundPending latched across repeated reads until actor changes or an owned roll arrives!
            if ((roundChanged || _staleActorRoundPending) && !actorChanged && !hasNewOwnedDice && hasCurrentActor)
            {
                _staleActorRoundPending = true;
                return new TurnBoundaryResult(boundaryAdvanced: false, roundChanged: roundChanged, shouldResetMovementState: false);
            }

            bool boundaryAdvanced = actorChanged || hasNewOwnedDice || (_pendingNullRoundReset && hasCurrentActor);
            if (!boundaryAdvanced)
            {
                return new TurnBoundaryResult(boundaryAdvanced: false, roundChanged: roundChanged, shouldResetMovementState: false);
            }

            if (!roundChanged && actorChanged && _activeActorId != 0 && isAliveMonster != null && isAliveMonster(_activeActorId))
            {
                // Credit completion ONLY if the outgoing monster actually started in effectiveRound
                // (never credit a monster whose round ticked ahead of actor switch!).
                bool startedInCurrentRound = !_staleActorRoundPending &&
                                             (_activeActorStartedRound < 0 || effectiveRound < 0 || _activeActorStartedRound == effectiveRound);
                if (startedInCurrentRound)
                {
                    _actedMonsterIdsThisRound.Add(_activeActorId);
                }
            }

            // Fallback round clear ONLY while session lacks any reliable Room.Round baseline.
            if (_lastObservedRound < 0 && _actedMonsterIdsThisRound.Count > 0 && hasCurrentActor && isHeroUnit != null && isHeroUnit(curActorId))
            {
                _actedMonsterIdsThisRound.Clear();
            }

            _turnSequence++;
            _activeActorId = hasCurrentActor ? curActorId : 0;
            _activeActorStartedRound = hasCurrentActor ? effectiveRound : -1;
            _pendingNullRoundReset = false;
            _staleActorRoundPending = false;

            if (hasNewOwnedDice)
            {
                _lastConsumedDiceEpoch = latestDiceEpoch;
                _actorAwaitingFirstDice = false;
            }
            else if (actorChanged && hasCurrentActor)
            {
                // Actor switched before its first dice notification arrived: coalesce that incoming first roll.
                _actorAwaitingFirstDice = true;
            }
            else
            {
                _actorAwaitingFirstDice = false;
            }

            if (hasCurrentActor)
            {
                _actedMonsterIdsThisRound.Remove(curActorId);
            }

            return new TurnBoundaryResult(boundaryAdvanced: true, roundChanged: roundChanged, shouldResetMovementState: true);
        }
    }
}
