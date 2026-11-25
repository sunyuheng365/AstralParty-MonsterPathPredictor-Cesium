using MonsterPathPredictor.Core;
using Xunit;

namespace MonsterPathPredictor.Tests
{
    public class ConfigMigrationLogicTests
    {
        private static string FakeNormalizeKey(string raw)
        {
            if (!ConfigMigrationLogic.IsSingleNamedKeyToken(raw)) return null;
            string t = raw.Trim();
            if (t.Equals("F8", System.StringComparison.OrdinalIgnoreCase)) return "F8";
            if (t.Equals("F10", System.StringComparison.OrdinalIgnoreCase)) return "F10";
            if (t.Equals("F7", System.StringComparison.OrdinalIgnoreCase)) return "F7";
            if (t.Equals("Escape", System.StringComparison.OrdinalIgnoreCase)) return "Escape";
            return null;
        }

        [Theory]
        [InlineData("F10", true)]
        [InlineData("  Escape ", true)]
        [InlineData("F10, F10", false)]
        [InlineData("F8,Escape", false)]
        [InlineData("123", false)]
        [InlineData("-1", false)]
        [InlineData("", false)]
        [InlineData("   ", false)]
        public void ValidatesSingleNamedKeyTokensStrictly(string raw, bool expected)
        {
            Assert.Equal(expected, ConfigMigrationLogic.IsSingleNamedKeyToken(raw));
        }

        [Theory]
        [InlineData(false, null)]
        [InlineData(true, "")]
        [InlineData(true, "F8")]
        [InlineData(true, "InvalidKey")]
        public void PreventsF10CollisionWhenLegacyClearHotkeyIsF10(bool hasPickerKey, string rawPickerKey)
        {
            var plan = ConfigMigrationLogic.PlanMigration(
                hasAnyKnownKey: true,
                existingVersion: 2,
                hasPickerKey: hasPickerKey,
                rawPickerKey: rawPickerKey,
                normalizedClearKey: "F10",
                normalizeHotkeyOrNull: FakeNormalizeKey);

            Assert.True(plan.NeedsMigration);
            Assert.Equal(3, plan.TargetVersion);
            // Explicitly persists F8 so defaults cannot later assign F10 and collide with ClearHotkey=F10
            Assert.Equal("F8", plan.SetPickerHotkeyTo);
        }

        [Fact]
        public void MigratesLegacyF8AndSparseConfigsToF10WhilePreservingCustomHotkeyAndIdempotency()
        {
            Assert.Contains("heightOffset", ConfigMigrationLogic.AllKnownLegacyKeys);
            Assert.Contains("fontSize", ConfigMigrationLogic.AllKnownLegacyKeys);

            // Sparse v1 config with no pickerHotkey -> migrates to F10 and clearOnEmptyClick=false
            var sparse = ConfigMigrationLogic.PlanMigration(true, 1, false, null, "Escape", FakeNormalizeKey);
            Assert.True(sparse.NeedsMigration);
            Assert.True(sparse.SetClearOnEmptyClickFalse);
            Assert.Equal("F10", sparse.SetPickerHotkeyTo);
            Assert.Equal(3, sparse.TargetVersion);

            // Custom user pickerHotkey F7 preserved untouched
            var custom = ConfigMigrationLogic.PlanMigration(true, 2, true, "F7", "Escape", FakeNormalizeKey);
            Assert.True(custom.NeedsMigration);
            Assert.False(custom.SetClearOnEmptyClickFalse);
            Assert.Null(custom.SetPickerHotkeyTo);

            // Second pass on v3 is a no-op
            var v3 = ConfigMigrationLogic.PlanMigration(true, 3, true, "F8", "Escape", FakeNormalizeKey);
            Assert.False(v3.NeedsMigration);
        }

        [Fact]
        public void SparseV3WithClearHotkeyF10NeverCollidesPickerHotkey()
        {
            ConfigMigrationLogic.ResolveEffectiveHotkeys(
                rawClearKey: "F10",
                rawPickerKey: null,
                FakeNormalizeKey,
                out string effectiveClear,
                out string effectivePicker,
                out string registeredDefault);

            Assert.Equal("F10", effectiveClear);
            Assert.Equal("F8", registeredDefault);
            Assert.Equal("F8", effectivePicker);

            // Explicit same-key collision F10/F10 also resolves cleanly
            ConfigMigrationLogic.ResolveEffectiveHotkeys(
                rawClearKey: "F10",
                rawPickerKey: "F10",
                FakeNormalizeKey,
                out effectiveClear,
                out effectivePicker,
                out registeredDefault);
            Assert.Equal("F10", effectiveClear);
            Assert.Equal("F8", effectivePicker);
        }

        [Fact]
        public void StylePendingPatchPreservesUnsavedLocalEditsAcrossExternalReloadAndFullReset()
        {
            var patch = new StylePendingPatch();
            var initial = new PredictorSettings();
            var afterLocalWidthEdit = initial.Clone();
            afterLocalWidthEdit.LineWidth = 6.5f;

            // Local LineWidth edit fails to save -> recorded in patch
            patch.RecordDiff(initial, afterLocalWidthEdit);
            Assert.True(patch.HasPending);
            Assert.Single(patch.Entries);
            Assert.Equal(6.5f, patch.Entries["lineWidth"]);

            // External disk edit changes FontSize=24 while disk still has LineWidth=4.5
            var reloadedFromDisk = new PredictorSettings { FontSize = 24, LineWidth = 4.5f };
            patch.ApplyTo(reloadedFromDisk);
            Assert.Equal(24, reloadedFromDisk.FontSize);
            Assert.Equal(6.5f, reloadedFromDisk.LineWidth);

            // Second local edit changes DashSpeed=40 before retry
            var beforeSecond = reloadedFromDisk.Clone();
            reloadedFromDisk.DashSpeed = 40f;
            patch.RecordDiff(beforeSecond, reloadedFromDisk);
            Assert.Equal(2, patch.Entries.Count);
            Assert.Equal(6.5f, patch.Entries["lineWidth"]);
            Assert.Equal(40f, patch.Entries["dashSpeed"]);
            Assert.False(patch.Entries.ContainsKey("fontSize")); // External edit NOT overwritten!

            // Explicit full reset queues exact 16 route-style keys (deliberately EXCLUDING previewSteps so a
            // failed style reset never overwrites an external previewSteps edit!).
            patch.Clear();
            Assert.False(patch.HasPending);
            var resetWithCustomDepth = new PredictorSettings { PreviewSteps = 20 };
            resetWithCustomDepth.ResetStyleToDefaults();
            patch.RecordAllStyleKeys(resetWithCustomDepth);
            Assert.True(patch.HasPending);
            Assert.Equal(16, patch.Entries.Count);
            Assert.False(patch.Entries.ContainsKey("previewSteps"));

            // Simulate failed reset followed by external previewSteps=8 edit on disk -> replay preserves 8!
            var externalAfterFailedReset = new PredictorSettings { PreviewSteps = 8, LineWidth = 9.0f };
            patch.ApplyTo(externalAfterFailedReset);
            Assert.Equal(8, externalAfterFailedReset.PreviewSteps);
            Assert.Equal(4.5f, externalAfterFailedReset.LineWidth);
        }

        [Fact]
        public void StylePersistenceBoundaryPreservesExternalWritesAcrossFailedSaveAndRetry()
        {
            var disk = new System.Collections.Generic.Dictionary<string, object>
            {
                ["lineWidth"] = 4.5f,
                ["fontSize"] = 18,
                ["dashSpeed"] = 26f,
                ["previewSteps"] = 12
            };
            var patch = new StylePendingPatch();

            // 1. User changes lineWidth to 7.0 -> write fails (disk unchanged, patch retains lineWidth=7.0)
            var s1 = new PredictorSettings { LineWidth = (float)disk["lineWidth"], FontSize = (int)disk["fontSize"], DashSpeed = (float)disk["dashSpeed"], PreviewSteps = (int)disk["previewSteps"] };
            var b1 = s1.Clone();
            s1.LineWidth = 7.0f;
            patch.RecordDiff(b1, s1);

            // 2. External editor updates fontSize=24 and previewSteps=16 on disk
            disk["fontSize"] = 24;
            disk["previewSteps"] = 16;

            // 3. Reload from disk, merge pending patch, user edits dashSpeed=44, retry write succeeds
            var s2 = new PredictorSettings { LineWidth = (float)disk["lineWidth"], FontSize = (int)disk["fontSize"], DashSpeed = (float)disk["dashSpeed"], PreviewSteps = (int)disk["previewSteps"] };
            patch.ApplyTo(s2);
            var b2 = s2.Clone();
            s2.DashSpeed = 44f;
            patch.RecordDiff(b2, s2);
            foreach (var kv in patch.Entries) disk[kv.Key] = kv.Value;
            patch.Clear();

            Assert.Equal(7.0f, (float)disk["lineWidth"]);
            Assert.Equal(44f, (float)disk["dashSpeed"]);
            Assert.Equal(24, (int)disk["fontSize"]);
            Assert.Equal(16, (int)disk["previewSteps"]);
        }
    }
}
