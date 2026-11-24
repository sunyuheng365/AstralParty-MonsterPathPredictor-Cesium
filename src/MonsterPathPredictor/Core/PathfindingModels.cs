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

    /// <summary>
    /// Controls how the X-step forecast behaves after passing a tile occupied by a living, targetable player.
    /// </summary>
    public enum PostCombatMode
    {
        /// <summary>
        /// Strict replay-verified guide behavior ("之后再遇岔，看当时站位"):
        /// marks the combat tile, continues drawing subsequent steps with a distinct muted continuation style,
        /// and evaluates subsequent forks using living player positions.
        /// </summary>
        ContinueByGuide = 0,

        /// <summary>
        /// Truncates the forecast immediately at the first tile where the monster encounters a living player.
        /// </summary>
        StopAtFirstCombat = 1,

        /// <summary>
        /// After encountering a player once, treats subsequent forks in this forecast as non-targeting (Red > Blue).
        /// </summary>
        RedBlueAfterCombat = 2
    }

    public enum ForkReasonKind
    {
        ForkStartRed,
        ForkStartBlue,
        PostCombatRed,
        PostCombatBlue,
        SpawnDirection,
        CurrentFacing,
        TauntChase,
        MarkedTargetChase,
        SkillTrashcanRed,
        SkillShirleyRandom,
        SkillLinglingRedBlue,
        SkillLinglingPushDir,
        SkillNormalRandom,
        MidForkChasePlayer,
        MidForkMultiShortestPath,
        MidForkAllUTurnFallback,
        MidForkNoTargetFallback,
        UnknownFacingSplit,
        Mixed
    }

    public sealed class MonsterState
    {
        public long MonsterId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public int HeroId { get; set; }
        public int Hp { get; set; }
        public int MaxHp { get; set; }
        public bool IsNormalAggressive { get; set; } = true;
        public bool IsThief { get; set; }
        public int ActionWeight { get; set; }

        public int CurrentLandId { get; set; } = -1;
        public int FromLandId { get; set; } = -1;
        public int[] FrontNodeIds { get; set; } = Array.Empty<int>();
        public int CanStep { get; set; }

        public bool IsCurrentlyMovingMidTurn { get; set; }
        /// <summary>Tile where the monster started its current turn (-1 if unknown / turn has not started yet).</summary>
        public int TurnStartLandId { get; set; } = -1;
        public bool JustFoughtOnCurrentFork { get; set; }
        public bool HasLaunchedFightThisTurn { get; set; }
        public bool JustSpawnedOnFork { get; set; }

        public bool IsTaunted { get; set; }
        public long TauntTargetPlayerId { get; set; }

        /// <summary>
        /// Library (82016) Warden (1067, before true culprit reveal) and Guard (1068, before 5 dmg / buff 10681202):
        /// only pursues and attacks players with CrimeNum &gt; 0, and does not cascade past a behind nearest culprit.
        /// </summary>
        public bool RequiresCrimeTarget { get; set; }

        /// <summary>
        /// Magic Academy (82012) / Stadium (83001) Academy Security Officer (1018):
        /// prioritizes players marked with 【问题学生】 (buff 2101001) and may reverse at turn start to chase them.
        /// </summary>
        public bool ChasesProblemStudent { get; set; }

        /// <summary>
        /// Garden Courtyard (82015) Bronze Mandarin Duck (1047):
        /// prioritizes players marked with buff 10470101 and may reverse at turn start to chase them.
        /// </summary>
        public bool ChasesMandarinDuckMark { get; set; }

        public SkillMoveKind SkillMoveKind { get; set; } = SkillMoveKind.None;
        public int SkillMovePushDirectionLandId { get; set; } = -1;
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
        /// <summary>
        /// Whether aggressive monsters can initiate combat with this player on contact.
        /// False for stealth/unchallengeable buffs such as 1270202 (毒苹果隐匿) that still
        /// attract monster pathfinding at forks without triggering combat.
        /// </summary>
        public bool CanBeAttacked { get; set; } = true;
        /// <summary>Player's current stars/gold (-1 = unknown/assume positive for thief steal checks).</summary>
        public int Gold { get; set; } = -1;
        /// <summary>Library (82016) 【罪证】 stack count (buff 10013 / Hero.CrimeNum).</summary>
        public int CrimeNum { get; set; }
        /// <summary>Magic Academy (82012) / Stadium (83001) 【问题学生】 mark (buff 2101001).</summary>
        public bool HasProblemStudentMark { get; set; }
        /// <summary>Garden Courtyard (82015) Bronze Mandarin Duck mark (buff 10470101).</summary>
        public bool HasMandarinDuckMark { get; set; }

        public string SeatTag => "P" + (Slot + 1);
    }

    public sealed class PlanOptions
    {
        public int MaxSteps { get; set; } = 12;
        /// <summary>
        /// Optional branch pruning threshold (0.0 = exact probability conservation, no pruning).
        /// Any probability mass pruned when &gt; 0 is recorded in <see cref="PathPlan.PrunedProbability"/>.
        /// </summary>
        public double MinBranchProbability { get; set; } = 0.0;
        public bool SimulateCombatOnFork { get; set; } = true;
        public PostCombatMode PostCombatMode { get; set; } = PostCombatMode.ContinueByGuide;
    }

    public sealed class ForkExitPrediction
    {
        public int NextLandId { get; set; }
        public double ConditionalProbability { get; set; }
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
        public bool IsAfterCombat { get; set; }
        public ForkReasonKind ReasonKind { get; set; }
        public string SummaryLabel { get; set; } = string.Empty;
        public List<ForkExitPrediction> Exits { get; } = new List<ForkExitPrediction>();

        public bool HasMultipleBranches => Exits.Count > 1;
    }

    public sealed class PredictedEdge
    {
        public int FromLandId { get; set; }
        public int ToLandId { get; set; }
        /// <summary>
        /// Maximum per-step traversal probability across all predicted steps (exact for same-step
        /// branch merges; never inflated above a single looping branch's probability).
        /// </summary>
        public double Probability { get; set; }
        public int EarliestStep { get; set; }
        public bool IsAfterCombat { get; set; }
        public Dictionary<int, double> StepProbabilities { get; } = new Dictionary<int, double>();
    }

    public sealed class PredictedCombat
    {
        public int LandId { get; set; }
        public int StepIndex { get; set; }
        public double Probability { get; set; }
        public string TargetLabel { get; set; } = string.Empty;
        public bool IsSteal { get; set; }
        public Dictionary<int, double> StepProbabilities { get; } = new Dictionary<int, double>();
    }

    public sealed class PredictedEndpoint
    {
        public int LandId { get; set; }
        public int StepIndex { get; set; }
        public double Probability { get; set; }
        public bool IsDeadEnd { get; set; }
        public bool StoppedAtCombat { get; set; }
        public bool IsAfterCombat { get; set; }
    }

    public sealed class PredictedTileStep
    {
        public int LandId { get; set; }
        public int StepIndex { get; set; }
        public List<int> StepIndices { get; } = new List<int>();
        /// <summary>Maximum per-step arrival probability across steps visiting this tile.</summary>
        public double Probability { get; set; }
        public bool IsAfterCombat { get; set; }
        public bool IsEndpoint { get; set; }
        public bool IsDeadEnd { get; set; }
        public Dictionary<int, double> StepProbabilities { get; } = new Dictionary<int, double>();

        public string FormatStepBadge()
        {
            string stepsText = StepIndices.Count > 0
                ? string.Join("/", StepIndices)
                : StepIndex.ToString(CultureInfo.InvariantCulture);

            if (IsDeadEnd) return "终点(" + stepsText + "格)";
            if (IsEndpoint && StepIndex > 0) return stepsText + "格";
            return stepsText;
        }
    }

    public sealed class PathPlan
    {
        public long MonsterId { get; set; }
        public string MonsterName { get; set; } = string.Empty;
        public int StartLandId { get; set; } = -1;
        public int StepsRequested { get; set; }
        public bool IsNormalAggressive { get; set; } = true;
        public string HeadlineNote { get; set; } = string.Empty;
        /// <summary>Total probability mass pruned when <see cref="PlanOptions.MinBranchProbability"/> &gt; 0.</summary>
        public double PrunedProbability { get; set; }

        public List<PredictedEdge> Edges { get; } = new List<PredictedEdge>();
        public List<PredictedFork> Forks { get; } = new List<PredictedFork>();
        public List<PredictedCombat> Combats { get; } = new List<PredictedCombat>();
        public List<PredictedEndpoint> Endpoints { get; } = new List<PredictedEndpoint>();
        public List<PredictedTileStep> TileSteps { get; } = new List<PredictedTileStep>();
    }

    public static class ProbabilityFormat
    {
        public static string Percent(double probability)
        {
            if (double.IsNaN(probability) || probability <= 0.0) return "0%";
            if (probability >= 1.0) return "100%";

            double pct = probability * 100.0;
            double rounded = Math.Round(pct, 1, MidpointRounding.AwayFromZero);
            if (rounded <= 0.0) return "<0.1%";
            if (rounded >= 100.0) return ">99.9%";
            if (Math.Abs(rounded - Math.Round(rounded)) < 0.05)
            {
                return ((int)Math.Round(rounded)).ToString(CultureInfo.InvariantCulture) + "%";
            }
            return rounded.ToString("0.0", CultureInfo.InvariantCulture) + "%";
        }
    }
}
