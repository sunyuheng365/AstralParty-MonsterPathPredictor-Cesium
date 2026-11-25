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
    /// Pure-C# turn/round/acted-monster tracker that handles asynchronous delivery orders:
    ///   - (R, A) -> (R+1, A) -> (R+1, B): never marks A as acted in R+1!
    ///   - (R, A) -> (R, null) -> (R, A): transient null actor preserves A's active turn state!
    ///   - (R, A) -> (R+1, null) -> (R+1, A): pending round transition resets A's turn once identity recovers!
    ///   - Dice notification owner correlation: B's dice while A is still readable never resets A!
    /// </summary>
    public sealed class ActionRoundTracker
    {
        private readonly HashSet<long> _actedMonsterIdsThisRound = new HashSet<long>();

        private long _activeActorId;
        private int _activeActorStartedRound = -1;
        private int _lastObservedRound = -1;
        private bool _pendingRoundReset;
        private int _turnSequence;
        private int _lastConsumedDiceEpoch;
        private long _lastConsumedDiceActorId;

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
            _pendingRoundReset = false;
            _turnSequence = 0;
            _lastConsumedDiceEpoch = currentDiceEpoch;
            _lastConsumedDiceActorId = 0;
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
            // preserve active turn state within the same round, and record a pending reset if round changed.
            if (!hasCurrentActor && _activeActorId != 0)
            {
                if (roundChanged)
                {
                    _actedMonsterIdsThisRound.Clear();
                    _lastObservedRound = curRound;
                    _pendingRoundReset = true;
                }
                else if (curRound >= 0 && _lastObservedRound < 0)
                {
                    _lastObservedRound = curRound;
                }
                return new TurnBoundaryResult(boundaryAdvanced: false, roundChanged: roundChanged, shouldResetMovementState: false);
            }

            // Correlate dice notification strictly with its owning actor (ignore B's roll while A is still readable).
            bool ownDiceAdvanced = hasCurrentActor &&
                                   latestDiceEpoch != _lastConsumedDiceEpoch &&
                                   (latestDiceOwnerId == 0 || latestDiceOwnerId == curActorId) &&
                                   !(latestDiceEpoch == _lastConsumedDiceEpoch && curActorId == _lastConsumedDiceActorId);

            bool actorChanged = curActorId != _activeActorId;

            if (roundChanged)
            {
                _actedMonsterIdsThisRound.Clear();
                _lastObservedRound = curRound;
            }
            else if (curRound >= 0 && _lastObservedRound < 0)
            {
                _lastObservedRound = curRound;
            }

            // Case 1: Round ticked while GetCurrentPlayer() STILL returns the previous round's actor (with no new dice for it).
            // Keep _activeActorStartedRound at its original round so the upcoming actor switch won't mark it acted in the new round!
            if (roundChanged && !actorChanged && !ownDiceAdvanced && hasCurrentActor)
            {
                _pendingRoundReset = true;
                return new TurnBoundaryResult(boundaryAdvanced: false, roundChanged: true, shouldResetMovementState: false);
            }

            bool boundaryAdvanced = actorChanged || ownDiceAdvanced || (_pendingRoundReset && hasCurrentActor);
            if (!boundaryAdvanced)
            {
                return new TurnBoundaryResult(boundaryAdvanced: false, roundChanged: roundChanged, shouldResetMovementState: false);
            }

            if (!roundChanged && actorChanged && _activeActorId != 0 && isAliveMonster != null && isAliveMonster(_activeActorId))
            {
                // Credit completion ONLY if the outgoing monster actually started in the current round.
                bool startedInCurrentRound = _activeActorStartedRound < 0 || curRound < 0 || _activeActorStartedRound == curRound;
                if (startedInCurrentRound)
                {
                    _actedMonsterIdsThisRound.Add(_activeActorId);
                }
            }

            // Fallback round clear if Room.Round reflection is unavailable and turn enters Hero phase
            if (curRound < 0 && _actedMonsterIdsThisRound.Count > 0 && hasCurrentActor && isHeroUnit != null && isHeroUnit(curActorId))
            {
                _actedMonsterIdsThisRound.Clear();
            }

            _turnSequence++;
            _activeActorId = hasCurrentActor ? curActorId : 0;
            _activeActorStartedRound = hasCurrentActor ? curRound : -1;
            _pendingRoundReset = false;

            if (ownDiceAdvanced)
            {
                _lastConsumedDiceEpoch = latestDiceEpoch;
                _lastConsumedDiceActorId = curActorId;
            }
            else if (actorChanged && latestDiceOwnerId == curActorId)
            {
                _lastConsumedDiceEpoch = latestDiceEpoch;
                _lastConsumedDiceActorId = curActorId;
            }

            if (hasCurrentActor)
            {
                _actedMonsterIdsThisRound.Remove(curActorId);
            }

            return new TurnBoundaryResult(boundaryAdvanced: true, roundChanged: roundChanged, shouldResetMovementState: true);
        }
    }
}
