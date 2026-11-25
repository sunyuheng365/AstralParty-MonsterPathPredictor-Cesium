using System;
using System.Collections.Generic;

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

        /// <summary>
        /// Resolves effective ClearHotkey, PickerHotkey, and registered default PickerHotkey cleanly,
        /// guaranteeing PickerHotkey never collides with ClearHotkey even on sparse v3 configs.
        /// </summary>
        public static void ResolveEffectiveHotkeys(
            string rawClearKey,
            string rawPickerKey,
            Func<string, string> normalizeHotkeyOrNull,
            out string effectiveClear,
            out string effectivePicker,
            out string registeredPickerDefault)
        {
            string normClear = normalizeHotkeyOrNull != null ? normalizeHotkeyOrNull(rawClearKey) : null;
            effectiveClear = !string.IsNullOrEmpty(normClear) ? normClear : "Escape";
            registeredPickerDefault = string.Equals(effectiveClear, "F10", StringComparison.OrdinalIgnoreCase) ? "F8" : "F10";

            string normPicker = normalizeHotkeyOrNull != null ? normalizeHotkeyOrNull(rawPickerKey) : null;
            effectivePicker = !string.IsNullOrEmpty(normPicker) ? normPicker : registeredPickerDefault;
            if (string.Equals(effectivePicker, effectiveClear, StringComparison.OrdinalIgnoreCase))
            {
                effectivePicker = registeredPickerDefault;
            }
        }
    }

    /// <summary>
    /// Tracks unsaved per-key in-game style edits so a transient disk write failure followed by an
    /// external config edit never loses the user's pending edit or overwrites unrelated disk keys.
    /// </summary>
    public sealed class StylePendingPatch
    {
        private readonly Dictionary<string, object> _values = new Dictionary<string, object>(StringComparer.Ordinal);

        public bool HasPending => _values.Count > 0;
        public IReadOnlyDictionary<string, object> Entries => _values;

        public void Clear() => _values.Clear();

        public void RecordDiff(PredictorSettings before, PredictorSettings after)
        {
            if (after == null) return;
            after.ClampInPlace();
            if (before == null)
            {
                RecordAllStyleKeys(after);
                return;
            }

            if (before.PreviewSteps != after.PreviewSteps) _values["previewSteps"] = after.PreviewSteps;
            if (Math.Abs(before.DashLength - after.DashLength) >= 0.001f) _values["dashLength"] = after.DashLength;
            if (Math.Abs(before.GapLength - after.GapLength) >= 0.001f) _values["gapLength"] = after.GapLength;
            if (Math.Abs(before.DashSpeed - after.DashSpeed) >= 0.001f) _values["dashSpeed"] = after.DashSpeed;
            if (Math.Abs(before.LineWidth - after.LineWidth) >= 0.001f) _values["lineWidth"] = after.LineWidth;
            if (Math.Abs(before.HeightOffset - after.HeightOffset) >= 0.001f) _values["heightOffset"] = after.HeightOffset;
            if (before.FontSize != after.FontSize) _values["fontSize"] = after.FontSize;
            if (before.ShowReasonTag != after.ShowReasonTag) _values["showReasonTag"] = after.ShowReasonTag;
            if (before.ShowStepNumbers != after.ShowStepNumbers) _values["showStepNumbers"] = after.ShowStepNumbers;
            if (before.ShowDeterministicPercent != after.ShowDeterministicPercent) _values["showDeterministicPercent"] = after.ShowDeterministicPercent;
            if (before.PostCombatMode != after.PostCombatMode) _values["postCombatMode"] = PredictorSettings.FormatPostCombatMode(after.PostCombatMode);
            if (!string.Equals(before.DeterministicColorHex, after.DeterministicColorHex, StringComparison.Ordinal)) _values["deterministicColor"] = after.DeterministicColorHex;
            if (!string.Equals(before.MajorBranchColorHex, after.MajorBranchColorHex, StringComparison.Ordinal)) _values["majorBranchColor"] = after.MajorBranchColorHex;
            if (!string.Equals(before.MinorBranchColorHex, after.MinorBranchColorHex, StringComparison.Ordinal)) _values["minorBranchColor"] = after.MinorBranchColorHex;
            if (!string.Equals(before.PostCombatColorHex, after.PostCombatColorHex, StringComparison.Ordinal)) _values["postCombatColor"] = after.PostCombatColorHex;
            if (!string.Equals(before.GuideRedColorHex, after.GuideRedColorHex, StringComparison.Ordinal)) _values["guideRedColor"] = after.GuideRedColorHex;
            if (!string.Equals(before.GuideBlueColorHex, after.GuideBlueColorHex, StringComparison.Ordinal)) _values["guideBlueColor"] = after.GuideBlueColorHex;
        }

        public void RecordAllStyleKeys(PredictorSettings settings)
        {
            if (settings == null) return;
            settings.ClampInPlace();
            _values["previewSteps"] = settings.PreviewSteps;
            _values["dashLength"] = settings.DashLength;
            _values["gapLength"] = settings.GapLength;
            _values["dashSpeed"] = settings.DashSpeed;
            _values["lineWidth"] = settings.LineWidth;
            _values["heightOffset"] = settings.HeightOffset;
            _values["fontSize"] = settings.FontSize;
            _values["showReasonTag"] = settings.ShowReasonTag;
            _values["showStepNumbers"] = settings.ShowStepNumbers;
            _values["showDeterministicPercent"] = settings.ShowDeterministicPercent;
            _values["postCombatMode"] = PredictorSettings.FormatPostCombatMode(settings.PostCombatMode);
            _values["deterministicColor"] = settings.DeterministicColorHex;
            _values["majorBranchColor"] = settings.MajorBranchColorHex;
            _values["minorBranchColor"] = settings.MinorBranchColorHex;
            _values["postCombatColor"] = settings.PostCombatColorHex;
            _values["guideRedColor"] = settings.GuideRedColorHex;
            _values["guideBlueColor"] = settings.GuideBlueColorHex;
        }

        public void ApplyTo(PredictorSettings target)
        {
            if (target == null || _values.Count == 0) return;
            foreach (var kv in _values)
            {
                switch (kv.Key)
                {
                    case "previewSteps": target.PreviewSteps = (int)kv.Value; break;
                    case "dashLength": target.DashLength = (float)kv.Value; break;
                    case "gapLength": target.GapLength = (float)kv.Value; break;
                    case "dashSpeed": target.DashSpeed = (float)kv.Value; break;
                    case "lineWidth": target.LineWidth = (float)kv.Value; break;
                    case "heightOffset": target.HeightOffset = (float)kv.Value; break;
                    case "fontSize": target.FontSize = (int)kv.Value; break;
                    case "showReasonTag": target.ShowReasonTag = (bool)kv.Value; break;
                    case "showStepNumbers": target.ShowStepNumbers = (bool)kv.Value; break;
                    case "showDeterministicPercent": target.ShowDeterministicPercent = (bool)kv.Value; break;
                    case "postCombatMode": target.PostCombatMode = PredictorSettings.ParsePostCombatMode((string)kv.Value); break;
                    case "deterministicColor": target.DeterministicColorHex = (string)kv.Value; break;
                    case "majorBranchColor": target.MajorBranchColorHex = (string)kv.Value; break;
                    case "minorBranchColor": target.MinorBranchColorHex = (string)kv.Value; break;
                    case "postCombatColor": target.PostCombatColorHex = (string)kv.Value; break;
                    case "guideRedColor": target.GuideRedColorHex = (string)kv.Value; break;
                    case "guideBlueColor": target.GuideBlueColorHex = (string)kv.Value; break;
                }
            }
            target.ClampInPlace();
        }
    }
}
