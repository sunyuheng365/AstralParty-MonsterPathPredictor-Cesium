using System;
using System.Collections.Generic;

namespace MonsterPathPredictor.Core
{
    /// <summary>
    /// Pure-C# reducer for Boss (<c>1018</c> 教官 / <c>1047</c> 鸳鸯大盗) target locking:
    /// <list type="bullet">
    ///   <item><description>Seeds from a fresh roster/reconnect snapshot exactly once, including authoritative <c>0</c> clearing any stale inferred lock.</description></item>
    ///   <item><description>At every actual turn start, evaluates nearest marked target UNCONDITIONALLY (replacing a retained prior lock when a closer/fresh marked target exists, and retaining the prior lock when none exists).</description></item>
    ///   <item><description>Clears on combat ONLY when the actual opponent carried the priority mark at fight time (<c>1795593160172877</c> R10 F4138: fighting a locked target whose mark already expired MUST retain the lock).</description></item>
    /// </list>
    /// </summary>
    public struct BossTargetLockState
    {
        public long ActiveBossTargetId;
        public int SeededSnapshotGeneration;
        public int LastEvaluatedTurnSequence;
        public int AssignedTurnSequence;
        public long AssignedOrderSequence;
        public int ClearedTurnSequence;
        public int AssignmentVersion;

        public void SeedFromFreshSnapshot(long serverBossTargetId, int snapshotGeneration, int currentTurnSequence, long orderSeq)
        {
            if (snapshotGeneration <= SeededSnapshotGeneration) return;
            SeededSnapshotGeneration = snapshotGeneration;
            ActiveBossTargetId = serverBossTargetId;
            AssignedTurnSequence = currentTurnSequence;
            AssignedOrderSequence = orderSeq;
            AssignmentVersion++;
            if (serverBossTargetId == 0 && ClearedTurnSequence == currentTurnSequence)
            {
                // Keep cleared status for current turn if already cleared; fresh 0 simply confirms no lock.
            }
            else if (serverBossTargetId != 0)
            {
                ClearedTurnSequence = -1;
            }
        }

        public bool EvaluateTurnStart(long nearestMarkedPlayerId, int currentTurnSequence, long orderSeq)
        {
            if (LastEvaluatedTurnSequence == currentTurnSequence) return false;
            if (ClearedTurnSequence == currentTurnSequence) return false;

            LastEvaluatedTurnSequence = currentTurnSequence;
            if (nearestMarkedPlayerId != 0)
            {
                if (ActiveBossTargetId != nearestMarkedPlayerId)
                {
                    ActiveBossTargetId = nearestMarkedPlayerId;
                    AssignedTurnSequence = currentTurnSequence;
                    AssignedOrderSequence = orderSeq;
                    AssignmentVersion++;
                }
                else
                {
                    AssignedTurnSequence = currentTurnSequence;
                    AssignedOrderSequence = orderSeq;
                }
                return true;
            }
            return false;
        }

        public bool TryClearOnMarkedCombat(
            bool? opponentHadPriorityMarkAtFight,
            int fightTurnSequence,
            long combatOrderSequence,
            int currentTurnSequence,
            bool isCurrentTurn)
        {
            // Replay-verified (1795593160172877 R10 F4138): fighting a target whose mark already expired
            // MUST NOT clear BossTargetId. Clear ONLY when the opponent carried the priority mark at combat!
            if (opponentHadPriorityMarkAtFight != true) return false;

            bool assignmentIsNewer =
                ActiveBossTargetId != 0 &&
                (AssignedTurnSequence > fightTurnSequence ||
                 (combatOrderSequence > 0 && AssignedOrderSequence > combatOrderSequence));
            if (assignmentIsNewer) return false;

            ActiveBossTargetId = 0;
            AssignmentVersion++;
            if (isCurrentTurn)
            {
                ClearedTurnSequence = currentTurnSequence;
            }
            return true;
        }
    }

    /// <summary>
    /// Deduplicates combat records across <c>BattleUpdate (1007)</c> and delayed <c>FightResolved</c>
    /// polls by <c>BattleId</c>, binding actor/turn/tile/marks strictly on first receipt.
    /// </summary>
    public sealed class CombatBattleRegistry
    {
        private const int MaxEntries = 256;
        private readonly HashSet<long> _seenBattleIds = new HashSet<long>();
        private readonly Queue<long> _fifo = new Queue<long>();

        public void Reset()
        {
            _seenBattleIds.Clear();
            _fifo.Clear();
        }

        /// <summary>
        /// Returns <c>true</c> if this <paramref name="battleId"/> is newly observed (first receipt)
        /// or unkeyed (<c>battleId &lt;= 0</c>); returns <c>false</c> for subsequent phase/result updates
        /// of an already-bound battle so late deliveries cannot hijack a later actor's turn.
        /// </summary>
        public bool TryRegisterFirstReceipt(long battleId)
        {
            if (battleId <= 0) return true;
            if (!_seenBattleIds.Add(battleId)) return false;
            _fifo.Enqueue(battleId);
            while (_fifo.Count > MaxEntries)
            {
                long oldest = _fifo.Dequeue();
                _seenBattleIds.Remove(oldest);
            }
            return true;
        }
    }
}
