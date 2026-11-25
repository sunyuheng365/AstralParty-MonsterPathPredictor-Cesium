using System;
using System.Collections.Generic;
using System.Globalization;

namespace MonsterPathPredictor.Core
{
    /// <summary>
    /// Pure managed configuration model with safe clamping, PostCombatMode & CSV buff ID parsing.
    /// </summary>
    public sealed class PredictorSettings
    {
        public bool Enabled { get; set; } = true;
        public int PreviewSteps { get; set; } = 12;
        public float DashLength { get; set; } = 12f;
        public float GapLength { get; set; } = 8f;
        public float DashSpeed { get; set; } = 26f;
        public float LineWidth { get; set; } = 4.5f;
        public float HeightOffset { get; set; } = 0.45f;
        public int FontSize { get; set; } = 18;
        public float ClickRadius { get; set; } = 28f;
        public bool ClearOnEmptyClick { get; set; } = false;
        public bool ShowReasonTag { get; set; } = true;
        public bool ShowStepNumbers { get; set; } = true;
        public bool ShowDeterministicPercent { get; set; } = false;
        public bool SimulateCombatOnFork { get; set; } = true;
        public PostCombatMode PostCombatMode { get; set; } = PostCombatMode.ContinueByGuide;
        public bool AllowNonAggressiveMonsters { get; set; } = true;
        public string PickerHotkey { get; set; } = "F10";
        public bool ClosePickerOnSelect { get; set; } = false;
        public string ClearHotkey { get; set; } = "Escape";
        public HashSet<int> ExcludedPlayerBuffIds { get; set; } = new HashSet<int>();

        public string DeterministicColorHex { get; set; } = StyleColor.DefaultDeterministicHex;
        public string MajorBranchColorHex { get; set; } = StyleColor.DefaultMajorBranchHex;
        public string MinorBranchColorHex { get; set; } = StyleColor.DefaultMinorBranchHex;
        public string PostCombatColorHex { get; set; } = StyleColor.DefaultPostCombatHex;
        public string GuideRedColorHex { get; set; } = StyleColor.DefaultGuideRedHex;
        public string GuideBlueColorHex { get; set; } = StyleColor.DefaultGuideBlueHex;

        public void ClampInPlace()
        {
            PreviewSteps = ClampInt(PreviewSteps, 1, 30);
            DashLength = ClampFloat(DashLength, 4f, 40f, 12f);
            GapLength = ClampFloat(GapLength, 2f, 30f, 8f);
            DashSpeed = ClampFloat(DashSpeed, 0f, 150f, 26f);
            LineWidth = ClampFloat(LineWidth, 1.5f, 14f, 4.5f);
            HeightOffset = ClampFloat(HeightOffset, 0f, 5f, 0.45f);
            FontSize = ClampInt(FontSize, 12, 36);
            ClickRadius = ClampFloat(ClickRadius, 8f, 120f, 28f);
            if (string.IsNullOrWhiteSpace(PickerHotkey)) PickerHotkey = "F10";
            if (string.IsNullOrWhiteSpace(ClearHotkey)) ClearHotkey = "Escape";
            if (ExcludedPlayerBuffIds == null) ExcludedPlayerBuffIds = new HashSet<int>();

            DeterministicColorHex = StyleColor.NormalizeHex(DeterministicColorHex, StyleColor.DefaultDeterministicHex);
            MajorBranchColorHex = StyleColor.NormalizeHex(MajorBranchColorHex, StyleColor.DefaultMajorBranchHex);
            MinorBranchColorHex = StyleColor.NormalizeHex(MinorBranchColorHex, StyleColor.DefaultMinorBranchHex);
            PostCombatColorHex = StyleColor.NormalizeHex(PostCombatColorHex, StyleColor.DefaultPostCombatHex);
            GuideRedColorHex = StyleColor.NormalizeHex(GuideRedColorHex, StyleColor.DefaultGuideRedHex);
            GuideBlueColorHex = StyleColor.NormalizeHex(GuideBlueColorHex, StyleColor.DefaultGuideBlueHex);
        }

        public void ResetStyleToDefaults()
        {
            DashLength = 12f;
            GapLength = 8f;
            DashSpeed = 26f;
            LineWidth = 4.5f;
            HeightOffset = 0.45f;
            FontSize = 18;
            ShowReasonTag = true;
            ShowStepNumbers = true;
            ShowDeterministicPercent = false;
            PostCombatMode = PostCombatMode.ContinueByGuide;
            DeterministicColorHex = StyleColor.DefaultDeterministicHex;
            MajorBranchColorHex = StyleColor.DefaultMajorBranchHex;
            MinorBranchColorHex = StyleColor.DefaultMinorBranchHex;
            PostCombatColorHex = StyleColor.DefaultPostCombatHex;
            GuideRedColorHex = StyleColor.DefaultGuideRedHex;
            GuideBlueColorHex = StyleColor.DefaultGuideBlueHex;
        }

        public bool StyleSettingsEqual(PredictorSettings other)
        {
            if (other == null) return false;
            return PreviewSteps == other.PreviewSteps &&
                   Math.Abs(DashLength - other.DashLength) < 0.001f &&
                   Math.Abs(GapLength - other.GapLength) < 0.001f &&
                   Math.Abs(DashSpeed - other.DashSpeed) < 0.001f &&
                   Math.Abs(LineWidth - other.LineWidth) < 0.001f &&
                   Math.Abs(HeightOffset - other.HeightOffset) < 0.001f &&
                   FontSize == other.FontSize &&
                   ShowReasonTag == other.ShowReasonTag &&
                   ShowStepNumbers == other.ShowStepNumbers &&
                   ShowDeterministicPercent == other.ShowDeterministicPercent &&
                   PostCombatMode == other.PostCombatMode &&
                   string.Equals(DeterministicColorHex, other.DeterministicColorHex, StringComparison.Ordinal) &&
                   string.Equals(MajorBranchColorHex, other.MajorBranchColorHex, StringComparison.Ordinal) &&
                   string.Equals(MinorBranchColorHex, other.MinorBranchColorHex, StringComparison.Ordinal) &&
                   string.Equals(PostCombatColorHex, other.PostCombatColorHex, StringComparison.Ordinal) &&
                   string.Equals(GuideRedColorHex, other.GuideRedColorHex, StringComparison.Ordinal) &&
                   string.Equals(GuideBlueColorHex, other.GuideBlueColorHex, StringComparison.Ordinal);
        }

        public bool PlanInputsEqual(PredictorSettings other)
        {
            if (other == null) return false;
            return PreviewSteps == other.PreviewSteps &&
                   SimulateCombatOnFork == other.SimulateCombatOnFork &&
                   PostCombatMode == other.PostCombatMode;
        }

        public PredictorSettings Clone()
        {
            return new PredictorSettings
            {
                Enabled = Enabled,
                PreviewSteps = PreviewSteps,
                DashLength = DashLength,
                GapLength = GapLength,
                DashSpeed = DashSpeed,
                LineWidth = LineWidth,
                HeightOffset = HeightOffset,
                FontSize = FontSize,
                ClickRadius = ClickRadius,
                ClearOnEmptyClick = ClearOnEmptyClick,
                ShowReasonTag = ShowReasonTag,
                ShowStepNumbers = ShowStepNumbers,
                ShowDeterministicPercent = ShowDeterministicPercent,
                SimulateCombatOnFork = SimulateCombatOnFork,
                PostCombatMode = PostCombatMode,
                AllowNonAggressiveMonsters = AllowNonAggressiveMonsters,
                PickerHotkey = PickerHotkey,
                ClosePickerOnSelect = ClosePickerOnSelect,
                ClearHotkey = ClearHotkey,
                ExcludedPlayerBuffIds = ExcludedPlayerBuffIds != null ? new HashSet<int>(ExcludedPlayerBuffIds) : new HashSet<int>(),
                DeterministicColorHex = DeterministicColorHex,
                MajorBranchColorHex = MajorBranchColorHex,
                MinorBranchColorHex = MinorBranchColorHex,
                PostCombatColorHex = PostCombatColorHex,
                GuideRedColorHex = GuideRedColorHex,
                GuideBlueColorHex = GuideBlueColorHex
            };
        }

        public static PostCombatMode ParsePostCombatMode(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return PostCombatMode.ContinueByGuide;
            string s = raw.Trim().ToLowerInvariant();
            if (s == "stop" || s == "truncate" || s == "stopatcombat" || s == "stopatfirstcombat")
                return PostCombatMode.StopAtFirstCombat;
            if (s == "redblue" || s == "red_blue" || s == "redblueaftercombat" || s == "noaggro")
                return PostCombatMode.RedBlueAfterCombat;
            return PostCombatMode.ContinueByGuide;
        }

        public static string FormatPostCombatMode(PostCombatMode mode)
        {
            switch (mode)
            {
                case PostCombatMode.StopAtFirstCombat: return "stop";
                case PostCombatMode.RedBlueAfterCombat: return "redBlue";
                default: return "continue";
            }
        }

        public static HashSet<int> ParseIdCsv(string csv)
        {
            var set = new HashSet<int>();
            if (string.IsNullOrWhiteSpace(csv)) return set;
            var parts = csv.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                if (int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) && id > 0)
                {
                    set.Add(id);
                }
            }
            return set;
        }

        public static int ClampInt(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public static float ClampFloat(float value, float min, float max, float fallback)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return fallback;
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
