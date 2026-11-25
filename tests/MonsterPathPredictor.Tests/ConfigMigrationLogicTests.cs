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
    }
}
