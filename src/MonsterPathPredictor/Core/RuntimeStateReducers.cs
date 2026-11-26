using System;
using System.Collections.Generic;
using System.Text;

namespace MonsterPathPredictor.Core
{
    /// <summary>
    /// Explicit shared monster classification matching <c>party.model.Hero.Types.MonsterType</c>
    /// (and <c>ReplayEvaluator.Score</c>): <c>None (0)</c> returns <c>null</c> (unknown) instead of
    /// assuming aggressive.
    /// </summary>
    public static class MonsterClassifier
    {
        public const int TypeNone = 0;
        public const int TypeThief = 1;
        public const int TypeBoss = 2;
        public const int TypePveBoss = 10;
        public const int TypePveMonster = 11;
        public const int TypePveEliteMonster = 12;
        public const int TypePveAllyMonster = 30;

        public static bool? TryClassifyAggressive(int monsterType)
        {
            switch (monsterType)
            {
                case TypeBoss:
                case TypePveBoss:
                case TypePveMonster:
                case TypePveEliteMonster:
                    return true;
                case TypeThief:
                case TypePveAllyMonster:
                    return false;
                default:
                    return null;
            }
        }

        public static bool? TryClassifyThief(int monsterType)
        {
            switch (monsterType)
            {
                case TypeThief:
                    return true;
                case TypeBoss:
                case TypePveBoss:
                case TypePveMonster:
                case TypePveEliteMonster:
                case TypePveAllyMonster:
                    return false;
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// Formats encounter / steal badge labels including all positive-support arrival step indices
    /// (e.g. <c>⚔遇P1(第3格)</c> or <c>⚔遇P1(第3/5格)</c>).
    /// </summary>
    public static class CombatBadgeFormatter
    {
        public static string FormatEncounterLabel(PredictedCombat combat)
        {
            if (combat == null) return string.Empty;
            string prefix = combat.IsSteal ? "偷" : "⚔遇";
            string target = combat.TargetLabel ?? string.Empty;

            if (combat.StepProbabilities != null && combat.StepProbabilities.Count > 1)
            {
                var steps = new List<int>(combat.StepProbabilities.Count);
                foreach (var kv in combat.StepProbabilities)
                {
                    if (kv.Key > 0 && kv.Value > 0.0) steps.Add(kv.Key);
                }
                if (steps.Count > 1)
                {
                    steps.Sort();
                    var sb = new StringBuilder(prefix.Length + target.Length + steps.Count * 3 + 6);
                    sb.Append(prefix).Append(target).Append("(第");
                    for (int i = 0; i < steps.Count; i++)
                    {
                        if (i > 0) sb.Append('/');
                        sb.Append(steps[i]);
                    }
                    sb.Append("格)");
                    return sb.ToString();
                }
            }

            return prefix + target + "(第" + combat.StepIndex + "格)";
        }
    }

    /// <summary>
    /// Tracks board disappearance (<c>HeroAttrEffect.Disappear</c> -> <c>BattlePlayerData.Absorb()</c>)
    /// separately from HP because <c>Absorb()</c> leaves the unit in <c>PlayerDatas</c> with positive HP.
    /// </summary>
    public sealed class UnitPresenceLedger
    {
        private readonly HashSet<long> _disappearedUnitIds = new HashSet<long>();

        public void Reset() => _disappearedUnitIds.Clear();

        public void MarkDisappeared(long unitId)
        {
            if (unitId > 0) _disappearedUnitIds.Add(unitId);
        }

        public void MarkPresent(long unitId)
        {
            if (unitId > 0) _disappearedUnitIds.Remove(unitId);
        }

        public bool IsMarkedDisappeared(long unitId)
        {
            return unitId > 0 && _disappearedUnitIds.Contains(unitId);
        }

        public bool IsEffectivelyAbsent(long unitId, bool animatorConfirmedHidden)
        {
            return (unitId > 0 && _disappearedUnitIds.Contains(unitId)) || animatorConfirmedHidden;
        }
    }

    /// <summary>
    /// Seeds frozen <c>Hero.IsLaunchFight</c> strictly for the active action of a fresh reconnect/full snapshot
    /// so it expires at the next action boundary and never suppresses encounters on later turns.
    /// </summary>
    public struct LaunchFightTurnGuard
    {
        public int SeededSnapshotGeneration;
        public int SeededTurnSequence;
        public bool SeededValue;

        public void SeedFromFreshSnapshot(int snapshotGeneration, int currentTurnSequence, bool heroIsLaunchFight, bool isActiveActorNow)
        {
            SeededSnapshotGeneration = snapshotGeneration;
            if (isActiveActorNow && heroIsLaunchFight)
            {
                SeededTurnSequence = currentTurnSequence;
                SeededValue = true;
            }
            else
            {
                SeededTurnSequence = -1;
                SeededValue = false;
            }
        }

        public bool IsSeededLaunchFightActive(int currentTurnSequence)
        {
            return SeededValue && SeededTurnSequence >= 0 && SeededTurnSequence == currentTurnSequence;
        }
    }

    /// <summary>
    /// Client-matching HP delta computation matching <c>BattleProperty.OnLifeChanged</c> IL:
    /// nonzero <c>RealHp</c> wins; otherwise <c>priorHp + realChangeHp</c>, clamped to <c>[0, maxHp]</c>.
    /// </summary>
    public static class LiveHpFormula
    {
        public static int Apply(int priorHp, int priorMaxHp, int realHp, int realChangeHp, int reportedMaxHp)
        {
            int effectiveMax = reportedMaxHp > 0 ? reportedMaxHp : priorMaxHp;
            int raw = realHp != 0 ? realHp : (priorHp + realChangeHp);
            int clamped = Math.Max(0, raw);
            if (effectiveMax > 0) clamped = Math.Min(effectiveMax, clamped);
            return clamped;
        }
    }
}
