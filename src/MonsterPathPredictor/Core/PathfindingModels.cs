using System;
using System.Collections.Generic;
using System.Globalization;

namespace MonsterPathPredictor.Core
{
    public enum SkillMoveKind
    {
        None = 0,
        /// <summary>垃圾桶/垃圾箱(Z3000) 拉过来：普通格方向随机，岔路口一定走红。</summary>
        TrashcanPull = 1,
        /// <summary>雪莉(橘雪莉) 扔出去：普通格方向随机，岔路口随机朝一个方向（不看红蓝）。</summary>
        ShirleyThrow = 2,
        /// <summary>玲玲(娇玲玲) 推开：普通格跟玲玲推的方向，岔路口有红走红没红走蓝。</summary>
        LinglingPush = 3
    }

    public enum ForkReasonKind
    {
        ForkStartRed,
        ForkStartBlue,
        PostCombatRed,
        PostCombatBlue,
        SpawnDirection,
        SkillTrashcanRed,
        SkillShirleyRandom,
        SkillLinglingRedBlue,
        SkillLinglingPushDir,
        SkillNormalRandom,
        MidForkChasePlayer,
        MidForkMultiShortestPath,
        MidForkAllUTurnFallback,
        MidForkNoTargetFallback,
        UnknownFacingSplit
    }

    public sealed class MonsterState
    {
        public long MonsterId { get; set; }
        public string Name { get; set; } = string.Empty;
        public int HeroId { get; set; }
        public bool IsNormalAggressive { get; set; } = true;
        public int ActionWeight { get; set; }

        public int CurrentLandId { get; set; } = -1;
        public int FromLandId { get; set; } = -1;
        public int[] FrontNodeIds { get; set; } = Array.Empty<int>();
        public int CanStep { get; set; }

        /// <summary>
        /// True when the monster is the active mover and has already stepped at least once in this turn
        /// (arrived mid-walk at <see cref="CurrentLandId"/> rather than starting its turn here).
        /// </summary>
        public bool IsCurrentlyMovingMidTurn { get; set; }

        /// <summary>
        /// True if the monster initiated combat while on <see cref="CurrentLandId"/> (a fork)
        /// and has not yet stepped off this fork.
        /// </summary>
        public bool JustFoughtOnCurrentFork { get; set; }

        /// <summary>
        /// True if the monster freshly spawned on a fork and has not moved yet,
        /// so its first step follows the spawn arrow (<see cref="FrontNodeIds"/>).
        /// </summary>
        public bool JustSpawnedOnFork { get; set; }

        /// <summary>Recent skill displacement state until the monster leaves the tile.</summary>
        public SkillMoveKind SkillMoveKind { get; set; } = SkillMoveKind.None;

        /// <summary>For Lingling push on a normal tile: target neighbor in push direction.</summary>
        public int SkillMovePushDirectionLandId { get; set; } = -1;

        /// <summary>For Lingling push onto a fork: tile behind push direction to avoid U-turn.</summary>
        public int SkillMovePushFromLandId { get; set; } = -1;
    }

    public sealed class TrackedPlayer
    {
        public long PlayerId { get; set; }
        /// <summary>0-based seat index (0 = P1, 1 = P2, 2 = P3, 3 = P4).</summary>
        public int Slot { get; set; }
        public string Name { get; set; } = string.Empty;
        public int LandId { get; set; } = -1;
        public bool IsAlive { get; set; } = true;
        public bool IsTargetable { get; set; } = true;

        public string SeatTag => "P" + (Slot + 1);
    }

    public sealed class PlanOptions
    {
        public int MaxSteps { get; set; } = 12;
        public double MinBranchProbability { get; set; } = 0.005;
        public bool SimulateCombatOnFork { get; set; } = true;
    }

    public sealed class ForkExitPrediction
    {
        public int NextLandId { get; set; }
        /// <summary>Conditional probability of choosing this exit given the monster reaches this fork state.</summary>
        public double ConditionalProbability { get; set; }
        /// <summary>Unconditional probability of taking this exit starting from Step 0.</summary>
        public double CumulativeProbability { get; set; }
        public bool IsGlobalRedArrow { get; set; }

        public string FormatConditionalPercent() => ProbabilityFormat.Percent(ConditionalProbability);
        public string FormatCumulativePercent() => ProbabilityFormat.Percent(CumulativeProbability);
    }

    public sealed class PredictedFork
    {
        public int ForkLandId { get; set; }
        public int IncomingLandId { get; set; }
        public int StepIndex { get; set; }
        public double ArrivalProbability { get; set; }
        public ForkReasonKind ReasonKind { get; set; }
        public string SummaryLabel { get; set; } = string.Empty;
        public List<ForkExitPrediction> Exits { get; } = new List<ForkExitPrediction>();

        public bool HasMultipleBranches => Exits.Count > 1;
    }

    public sealed class PredictedEdge
    {
        public int FromLandId { get; set; }
        public int ToLandId { get; set; }
        public double Probability { get; set; }
        public int EarliestStep { get; set; }
    }

    public sealed class PredictedEndpoint
    {
        public int LandId { get; set; }
        public int StepIndex { get; set; }
        public double Probability { get; set; }
        public bool IsDeadEnd { get; set; }
    }

    public sealed class PathPlan
    {
        public long MonsterId { get; set; }
        public string MonsterName { get; set; } = string.Empty;
        public int StartLandId { get; set; } = -1;
        public int StepsRequested { get; set; }
        public bool IsNormalAggressive { get; set; } = true;
        public string HeadlineNote { get; set; } = string.Empty;

        public List<PredictedEdge> Edges { get; } = new List<PredictedEdge>();
        public List<PredictedFork> Forks { get; } = new List<PredictedFork>();
        public List<PredictedEndpoint> Endpoints { get; } = new List<PredictedEndpoint>();
    }

    public static class ProbabilityFormat
    {
        public static string Percent(double probability)
        {
            double pct = Math.Max(0.0, Math.Min(100.0, probability * 100.0));
            double rounded = Math.Round(pct, 1, MidpointRounding.AwayFromZero);
            if (Math.Abs(rounded - Math.Round(rounded)) < 0.05)
            {
                return ((int)Math.Round(rounded)).ToString(CultureInfo.InvariantCulture) + "%";
            }
            return rounded.ToString("0.0", CultureInfo.InvariantCulture) + "%";
        }
    }
}
