using System;

namespace MonsterPathPredictor.Core
{
    /// <summary>
    /// Pure-C# config key normalization and idempotent schema v1/v2 -> v3 migration planner.
    /// Completely independent of Unity/CesiumLoader so every migration edge case is unit-testable.
    /// </summary>
    public static class ConfigMigrationLogic
    {
        public static readonly string[] AllKnownLegacyKeys =
        {
            "configVersion", "enabled", "previewSteps", "dashLength", "gapLength", "dashSpeed",
            "lineWidth", "heightOffset", "fontSize", "clickRadius", "clearOnEmptyClick",
            "showReasonTag", "showStepNumbers", "showDeterministicPercent", "simulateCombatOnFork",
            "postCombatMode", "allowNonAggressiveMonsters", "pickerHotkey", "closePickerOnSelect",
            "clearHotkey", "excludedPlayerBuffIds"
        };

        /// <summary>
        /// Checks that a raw hotkey string is a single named token (no commas, no pure integer value).
        /// </summary>
        public static bool IsSingleNamedKeyToken(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            string s = raw.Trim();
            if (s.IndexOf(',') >= 0) return false;

            bool allDigits = true;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (i == 0 && (ch == '-' || ch == '+')) continue;
                if (ch < '0' || ch > '9') { allDigits = false; break; }
            }
            return !allDigits;
        }

        public readonly struct MigrationPlan
        {
            public bool NeedsMigration { get; }
            public bool SetClearOnEmptyClickFalse { get; }
            public string SetPickerHotkeyTo { get; }
            public int TargetVersion { get; }

            public MigrationPlan(bool needsMigration, bool setClearOnEmptyClickFalse, string setPickerHotkeyTo, int targetVersion)
            {
                NeedsMigration = needsMigration;
                SetClearOnEmptyClickFalse = setClearOnEmptyClickFalse;
                SetPickerHotkeyTo = setPickerHotkeyTo;
                TargetVersion = targetVersion;
            }
        }

        /// <summary>
        /// Computes an idempotent migration plan for an existing config file.
        /// Prevents colliding PickerHotkey with ClearHotkey when a legacy user bound ClearHotkey=F10
        /// (explicitly pinning PickerHotkey="F8" so default registration cannot overwrite it to F10!).
        /// </summary>
        public static MigrationPlan PlanMigration(
            bool hasAnyKnownKey,
            int existingVersion,
            bool hasPickerKey,
            string rawPickerKey,
            string normalizedClearKey,
            Func<string, string> normalizeHotkeyOrNull)
        {
            if (!hasAnyKnownKey || existingVersion >= 3)
            {
                return new MigrationPlan(false, false, null, existingVersion);
            }

            bool setClearFalse = existingVersion < 2;
            string normPicker = hasPickerKey && normalizeHotkeyOrNull != null
                ? normalizeHotkeyOrNull(rawPickerKey)
                : null;
            bool clearUsesF10 = string.Equals(normalizedClearKey, "F10", StringComparison.OrdinalIgnoreCase);

            string targetPicker = null;
            bool pickerIsMissingOrLegacyF8 = !hasPickerKey ||
                                             string.IsNullOrWhiteSpace(rawPickerKey) ||
                                             string.Equals(normPicker, "F8", StringComparison.OrdinalIgnoreCase);
            bool pickerIsInvalid = hasPickerKey && !string.IsNullOrWhiteSpace(rawPickerKey) && normPicker == null;

            if (clearUsesF10)
            {
                // Keep F8 explicitly persisted whenever default/fallback resolution would otherwise collide with ClearHotkey=F10!
                if (pickerIsMissingOrLegacyF8 || pickerIsInvalid)
                {
                    targetPicker = "F8";
                }
            }
            else if (pickerIsMissingOrLegacyF8)
            {
                targetPicker = "F10";
            }

            return new MigrationPlan(true, setClearFalse, targetPicker, 3);
        }
    }
}
