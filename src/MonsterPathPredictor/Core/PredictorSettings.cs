using System;
using System.Collections.Generic;
using System.Globalization;

namespace MonsterPathPredictor.Core
{
    /// <summary>
    /// Pure managed configuration model with safe clamping & CSV buff ID parsing.
    /// </summary>
    public sealed class PredictorSettings
    {
        public bool Enabled { get; set; } = true;
        public int PreviewSteps { get; set; } = 12;
        public float DashLength { get; set; } = 12f;
        public float GapLength { get; set; } = 8f;
        public float LineWidth { get; set; } = 4.5f;
        public float HeightOffset { get; set; } = 0.45f;
        public int FontSize { get; set; } = 18;
        public float ClickRadius { get; set; } = 48f;
        public bool ClearOnEmptyClick { get; set; } = true;
        public bool ShowReasonTag { get; set; } = true;
        public bool ShowDeterministicPercent { get; set; } = false;
        public bool SimulateCombatOnFork { get; set; } = true;
        public bool AllowNonAggressiveMonsters { get; set; } = true;
        public string ClearHotkey { get; set; } = "Escape";
        public HashSet<int> ExcludedPlayerBuffIds { get; set; } = new HashSet<int>();

        public void ClampInPlace()
        {
            PreviewSteps = ClampInt(PreviewSteps, 1, 30);
            DashLength = ClampFloat(DashLength, 4f, 40f, 12f);
            GapLength = ClampFloat(GapLength, 2f, 30f, 8f);
            LineWidth = ClampFloat(LineWidth, 1.5f, 14f, 4.5f);
            HeightOffset = ClampFloat(HeightOffset, 0f, 5f, 0.45f);
            FontSize = ClampInt(FontSize, 12, 36);
            ClickRadius = ClampFloat(ClickRadius, 20f, 120f, 48f);
            if (string.IsNullOrWhiteSpace(ClearHotkey)) ClearHotkey = "Escape";
            if (ExcludedPlayerBuffIds == null) ExcludedPlayerBuffIds = new HashSet<int>();
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
