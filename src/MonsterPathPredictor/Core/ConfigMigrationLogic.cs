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

        /// <summary>
        /// Records the exact 16 route-style keys reset by <see cref="PredictorSettings.ResetStyleToDefaults"/>
        /// (deliberately excluding <c>previewSteps</c> so a failed style reset never overwrites an external depth edit).
        /// </summary>
        public void RecordAllStyleKeys(PredictorSettings settings)
        {
            if (settings == null) return;
            settings.ClampInPlace();
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

    /// <summary>
    /// Minimal storage abstraction wrapping CesiumLoader's ModConfig so the full load / migrate /
    /// reload-if-changed / merge / save / retry persistence boundary is directly unit-testable.
    /// </summary>
    public interface IConfigStore
    {
        bool Has(string key);
        bool GetBool(string key, bool defaultValue);
        int GetInt(string key, int defaultValue);
        float GetFloat(string key, float defaultValue);
        string GetString(string key, string defaultValue);
        void Set(string key, bool value);
        void Set(string key, int value);
        void Set(string key, float value);
        void Set(string key, string value);
        bool ReloadIfChanged();
        bool Save();
        bool SaveIfDirty();
    }

    /// <summary>
    /// Production config persistence coordinator shared by <c>PredictorConfig</c>, <c>MonsterPathController</c>,
    /// and unit tests. Guarantees:
    ///   - Null/unavailable storage reports <c>saveOk = false</c> and retains pending patches for retry.
    ///   - Failed migration or style saves stay pending until a subsequent save succeeds.
    ///   - External edits between reload and save are preserved for all keys outside the pending patch.
    /// </summary>
    public sealed class ConfigPersistenceCoordinator
    {
        private bool _pendingMigrationSave;

        public bool HasPendingMigrationSave => _pendingMigrationSave;

        public void ResetState() => _pendingMigrationSave = false;

        private static bool HasAnyExistingKey(IConfigStore store)
        {
            for (int i = 0; i < ConfigMigrationLogic.AllKnownLegacyKeys.Length; i++)
            {
                if (store.Has(ConfigMigrationLogic.AllKnownLegacyKeys[i])) return true;
            }
            return false;
        }

        private static bool ApplyMigrationsIfNeeded(IConfigStore store, Func<string, string> normalizeHotkeyOrNull)
        {
            if (store == null) return false;
            bool hasAny = HasAnyExistingKey(store);
            int existingVer = store.GetInt("configVersion", 1);
            bool hasPicker = store.Has("pickerHotkey");
            string rawPicker = hasPicker ? store.GetString("pickerHotkey", "") : null;
            string rawClear = store.GetString("clearHotkey", "Escape");
            string normClear = (normalizeHotkeyOrNull != null ? normalizeHotkeyOrNull(rawClear) : null) ?? "Escape";

            var plan = ConfigMigrationLogic.PlanMigration(hasAny, existingVer, hasPicker, rawPicker, normClear, normalizeHotkeyOrNull);
            if (!plan.NeedsMigration) return false;

            if (plan.SetClearOnEmptyClickFalse)
            {
                store.Set("clearOnEmptyClick", false);
            }
            if (!string.IsNullOrEmpty(plan.SetPickerHotkeyTo))
            {
                store.Set("pickerHotkey", plan.SetPickerHotkeyTo);
            }
            store.Set("configVersion", plan.TargetVersion);
            return true;
        }

        public PredictorSettings Load(
            IConfigStore store,
            bool isInitial,
            Func<string, string> normalizeHotkeyOrNull,
            Action<IConfigStore, string> applyDefaultsAndDocs,
            out bool saveOk)
        {
            var settings = new PredictorSettings();
            if (store == null)
            {
                saveOk = false;
                settings.ClampInPlace();
                return settings;
            }

            saveOk = true;
            bool migrated = ApplyMigrationsIfNeeded(store, normalizeHotkeyOrNull);
            if (migrated) _pendingMigrationSave = true;

            string rawClearBefore = store.GetString("clearHotkey", "Escape");
            string rawPickerBefore = store.Has("pickerHotkey") ? store.GetString("pickerHotkey", "") : null;
            ConfigMigrationLogic.ResolveEffectiveHotkeys(
                rawClearBefore,
                rawPickerBefore,
                normalizeHotkeyOrNull,
                out _,
                out _,
                out string pickerDefault);

            if (isInitial)
            {
                applyDefaultsAndDocs?.Invoke(store, pickerDefault);
            }

            settings.Enabled = store.GetBool("enabled", settings.Enabled);
            settings.PreviewSteps = store.GetInt("previewSteps", settings.PreviewSteps);
            settings.DashLength = store.GetFloat("dashLength", settings.DashLength);
            settings.GapLength = store.GetFloat("gapLength", settings.GapLength);
            settings.DashSpeed = store.GetFloat("dashSpeed", settings.DashSpeed);
            settings.LineWidth = store.GetFloat("lineWidth", settings.LineWidth);
            settings.HeightOffset = store.GetFloat("heightOffset", settings.HeightOffset);
            settings.FontSize = store.GetInt("fontSize", settings.FontSize);
            settings.ClickRadius = store.GetFloat("clickRadius", settings.ClickRadius);
            settings.ClearOnEmptyClick = store.GetBool("clearOnEmptyClick", settings.ClearOnEmptyClick);
            settings.ShowReasonTag = store.GetBool("showReasonTag", settings.ShowReasonTag);
            settings.ShowStepNumbers = store.GetBool("showStepNumbers", settings.ShowStepNumbers);
            settings.ShowDeterministicPercent = store.GetBool("showDeterministicPercent", settings.ShowDeterministicPercent);
            settings.SimulateCombatOnFork = store.GetBool("simulateCombatOnFork", settings.SimulateCombatOnFork);
            settings.PostCombatMode = PredictorSettings.ParsePostCombatMode(store.GetString("postCombatMode", "continue"));
            settings.AllowNonAggressiveMonsters = store.GetBool("allowNonAggressiveMonsters", settings.AllowNonAggressiveMonsters);
            ConfigMigrationLogic.ResolveEffectiveHotkeys(
                store.GetString("clearHotkey", settings.ClearHotkey),
                store.Has("pickerHotkey") ? store.GetString("pickerHotkey", "") : null,
                normalizeHotkeyOrNull,
                out string effectiveClear,
                out string effectivePicker,
                out _);
            settings.ClearHotkey = effectiveClear;
            settings.PickerHotkey = effectivePicker;
            settings.ClosePickerOnSelect = store.GetBool("closePickerOnSelect", settings.ClosePickerOnSelect);
            settings.ExcludedPlayerBuffIds = PredictorSettings.ParseIdCsv(store.GetString("excludedPlayerBuffIds", ""));
            settings.DeterministicColorHex = store.GetString("deterministicColor", settings.DeterministicColorHex);
            settings.MajorBranchColorHex = store.GetString("majorBranchColor", settings.MajorBranchColorHex);
            settings.MinorBranchColorHex = store.GetString("minorBranchColor", settings.MinorBranchColorHex);
            settings.PostCombatColorHex = store.GetString("postCombatColor", settings.PostCombatColorHex);
            settings.GuideRedColorHex = store.GetString("guideRedColor", settings.GuideRedColorHex);
            settings.GuideBlueColorHex = store.GetString("guideBlueColor", settings.GuideBlueColorHex);

            settings.ClampInPlace();

            if (isInitial || migrated || _pendingMigrationSave)
            {
                try
                {
                    saveOk = _pendingMigrationSave ? store.Save() : store.SaveIfDirty();
                    _pendingMigrationSave = !saveOk;
                }
                catch
                {
                    saveOk = false;
                    _pendingMigrationSave = true;
                }
            }

            return settings;
        }

        public bool SaveStylePatch(
            IConfigStore store,
            StylePendingPatch patch,
            Func<string, string> normalizeHotkeyOrNull,
            out PredictorSettings mergedFromDisk)
        {
            mergedFromDisk = null;
            if (store == null) return false;

            try
            {
                // Always pick up any external edits on disk before applying the narrow pending patch
                // so unrelated keys (e.g. previewSteps, clickRadius, hotkeys) are never overwritten.
                if (store.ReloadIfChanged())
                {
                    bool migrated = ApplyMigrationsIfNeeded(store, normalizeHotkeyOrNull);
                    if (migrated) _pendingMigrationSave = true;
                    mergedFromDisk = ReadSettingsOnly(store, normalizeHotkeyOrNull);
                    patch?.ApplyTo(mergedFromDisk);
                }

                bool dirty = false;
                if (patch != null && patch.HasPending)
                {
                    foreach (var kv in patch.Entries)
                    {
                        if (kv.Value is int iv) { store.Set(kv.Key, iv); dirty = true; }
                        else if (kv.Value is float fv) { store.Set(kv.Key, fv); dirty = true; }
                        else if (kv.Value is bool bv) { store.Set(kv.Key, bv); dirty = true; }
                        else if (kv.Value is string sv) { store.Set(kv.Key, sv); dirty = true; }
                    }
                }

                if (!dirty && !_pendingMigrationSave) return true;
                bool ok = store.Save();
                if (ok)
                {
                    _pendingMigrationSave = false;
                    patch?.Clear();
                }
                return ok;
            }
            catch
            {
                return false;
            }
        }

        public readonly struct StylePersistOutcome
        {
            public PredictorSettings Settings { get; }
            public bool StyleChanged { get; }
            public bool PlanInputsChanged { get; }
            public bool ExternalChanged { get; }
            public bool ReloadFailed { get; }
            public bool SaveAttempted { get; }
            public bool SaveOk { get; }

            public StylePersistOutcome(
                PredictorSettings settings,
                bool styleChanged,
                bool planInputsChanged,
                bool externalChanged,
                bool reloadFailed,
                bool saveAttempted,
                bool saveOk)
            {
                Settings = settings;
                StyleChanged = styleChanged;
                PlanInputsChanged = planInputsChanged;
                ExternalChanged = externalChanged;
                ReloadFailed = reloadFailed;
                SaveAttempted = saveAttempted;
                SaveOk = saveOk;
            }
        }

        /// <summary>
        /// Production load/reload/merge/mutate/save sequence driven by <c>MonsterPathController</c>.
        /// </summary>
        public StylePersistOutcome ApplyStyleMutationAndPersist(
            IConfigStore store,
            PredictorSettings currentSettings,
            StylePendingPatch pendingPatch,
            Action<PredictorSettings> mutate,
            bool isFullReset,
            Func<string, string> normalizeHotkeyOrNull)
        {
            var working = (currentSettings ?? new PredictorSettings()).Clone();
            bool externalChanged = false;
            bool reloadFailed = false;

            if (store != null)
            {
                try
                {
                    if (store.ReloadIfChanged())
                    {
                        bool migrated = ApplyMigrationsIfNeeded(store, normalizeHotkeyOrNull);
                        if (migrated) _pendingMigrationSave = true;
                        var reloaded = ReadSettingsOnly(store, normalizeHotkeyOrNull);
                        pendingPatch?.ApplyTo(reloaded);
                        externalChanged = !reloaded.PlanInputsEqual(working) || !reloaded.StyleSettingsEqual(working);
                        working = reloaded;
                    }
                }
                catch
                {
                    reloadFailed = true;
                }
            }

            var before = working.Clone();
            mutate?.Invoke(working);
            working.ClampInPlace();

            bool styleChanged = !working.StyleSettingsEqual(before);
            bool planInputsChanged = externalChanged || !working.PlanInputsEqual(before);
            if (isFullReset)
            {
                pendingPatch?.RecordAllStyleKeys(working);
            }
            else if (styleChanged)
            {
                pendingPatch?.RecordDiff(before, working);
            }

            bool shouldPersist = isFullReset || styleChanged || (pendingPatch != null && pendingPatch.HasPending) || _pendingMigrationSave;
            if (!shouldPersist)
            {
                return new StylePersistOutcome(working, styleChanged, planInputsChanged, externalChanged, reloadFailed, saveAttempted: false, saveOk: true);
            }

            if (store == null || reloadFailed)
            {
                return new StylePersistOutcome(working, styleChanged, planInputsChanged, externalChanged, reloadFailed, saveAttempted: true, saveOk: false);
            }

            bool saved = SaveStylePatch(store, pendingPatch, normalizeHotkeyOrNull, out var mergedDisk);
            if (mergedDisk != null)
            {
                if (!mergedDisk.PlanInputsEqual(working)) planInputsChanged = true;
                externalChanged = true;
                working = mergedDisk;
            }

            return new StylePersistOutcome(working, styleChanged, planInputsChanged, externalChanged, reloadFailed: false, saveAttempted: true, saveOk: saved);
        }

        private static PredictorSettings ReadSettingsOnly(IConfigStore store, Func<string, string> normalizeHotkeyOrNull)
        {
            var settings = new PredictorSettings
            {
                Enabled = store.GetBool("enabled", true),
                PreviewSteps = store.GetInt("previewSteps", 12),
                DashLength = store.GetFloat("dashLength", 12.0f),
                GapLength = store.GetFloat("gapLength", 8.0f),
                DashSpeed = store.GetFloat("dashSpeed", 26.0f),
                LineWidth = store.GetFloat("lineWidth", 4.5f),
                HeightOffset = store.GetFloat("heightOffset", 0.45f),
                FontSize = store.GetInt("fontSize", 18),
                ClickRadius = store.GetFloat("clickRadius", 28.0f),
                ClearOnEmptyClick = store.GetBool("clearOnEmptyClick", false),
                ShowReasonTag = store.GetBool("showReasonTag", true),
                ShowStepNumbers = store.GetBool("showStepNumbers", true),
                ShowDeterministicPercent = store.GetBool("showDeterministicPercent", false),
                SimulateCombatOnFork = store.GetBool("simulateCombatOnFork", true),
                PostCombatMode = PredictorSettings.ParsePostCombatMode(store.GetString("postCombatMode", "continue")),
                AllowNonAggressiveMonsters = store.GetBool("allowNonAggressiveMonsters", true),
                ClosePickerOnSelect = store.GetBool("closePickerOnSelect", false),
                ExcludedPlayerBuffIds = PredictorSettings.ParseIdCsv(store.GetString("excludedPlayerBuffIds", "")),
                DeterministicColorHex = store.GetString("deterministicColor", StyleColor.DefaultDeterministicHex),
                MajorBranchColorHex = store.GetString("majorBranchColor", StyleColor.DefaultMajorBranchHex),
                MinorBranchColorHex = store.GetString("minorBranchColor", StyleColor.DefaultMinorBranchHex),
                PostCombatColorHex = store.GetString("postCombatColor", StyleColor.DefaultPostCombatHex),
                GuideRedColorHex = store.GetString("guideRedColor", StyleColor.DefaultGuideRedHex),
                GuideBlueColorHex = store.GetString("guideBlueColor", StyleColor.DefaultGuideBlueHex)
            };
            ConfigMigrationLogic.ResolveEffectiveHotkeys(
                store.GetString("clearHotkey", settings.ClearHotkey),
                store.Has("pickerHotkey") ? store.GetString("pickerHotkey", "") : null,
                normalizeHotkeyOrNull,
                out string effectiveClear,
                out string effectivePicker,
                out _);
            settings.ClearHotkey = effectiveClear;
            settings.PickerHotkey = effectivePicker;
            settings.ClampInPlace();
            return settings;
        }
    }
}
