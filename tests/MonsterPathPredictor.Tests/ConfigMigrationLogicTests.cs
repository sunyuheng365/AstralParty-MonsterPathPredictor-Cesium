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

            // Explicit full reset queues all style keys even if memory already matches defaults
            patch.Clear();
            Assert.False(patch.HasPending);
            patch.RecordAllStyleKeys(new PredictorSettings());
            Assert.True(patch.HasPending);
            Assert.Equal(17, patch.Entries.Count);
        }
    }
}
