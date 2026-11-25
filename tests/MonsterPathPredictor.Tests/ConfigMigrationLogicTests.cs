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

        private sealed class FakeConfigStore : IConfigStore
        {
            public readonly System.Collections.Generic.Dictionary<string, object> Disk =
                new System.Collections.Generic.Dictionary<string, object>(System.StringComparer.Ordinal);
            private System.Collections.Generic.Dictionary<string, object> _mem =
                new System.Collections.Generic.Dictionary<string, object>(System.StringComparer.Ordinal);
            private bool _diskChangedExternally;
            private bool _memDirty;
            public bool FailSave;
            public bool ThrowOnReload;
            public int ReloadCallCount;
            public System.Action<FakeConfigStore> OnReloadBeforeCheck;

            public void SeedDisk(string key, object value)
            {
                Disk[key] = value;
                _mem[key] = value;
            }

            public void ExternalEditDisk(string key, object value)
            {
                Disk[key] = value;
                _diskChangedExternally = true;
            }

            public bool Has(string key) => _mem.ContainsKey(key);
            public bool GetBool(string key, bool defaultValue) => _mem.TryGetValue(key, out var v) && v is bool b ? b : defaultValue;
            public int GetInt(string key, int defaultValue) => _mem.TryGetValue(key, out var v) && v is int i ? i : defaultValue;
            public float GetFloat(string key, float defaultValue) => _mem.TryGetValue(key, out var v) && v is float f ? f : defaultValue;
            public string GetString(string key, string defaultValue) => _mem.TryGetValue(key, out var v) && v is string s ? s : defaultValue;
            public void Set(string key, bool value) { _mem[key] = value; _memDirty = true; }
            public void Set(string key, int value) { _mem[key] = value; _memDirty = true; }
            public void Set(string key, float value) { _mem[key] = value; _memDirty = true; }
            public void Set(string key, string value) { _mem[key] = value; _memDirty = true; }

            public bool ReloadIfChanged()
            {
                ReloadCallCount++;
                OnReloadBeforeCheck?.Invoke(this);
                if (ThrowOnReload) throw new System.IO.IOException("simulated disk read error");
                if (!_diskChangedExternally) return false;
                _mem = new System.Collections.Generic.Dictionary<string, object>(Disk, System.StringComparer.Ordinal);
                _diskChangedExternally = false;
                _memDirty = false;
                return true;
            }

            public bool Save()
            {
                if (FailSave) return false;
                Disk.Clear();
                foreach (var kv in _mem) Disk[kv.Key] = kv.Value;
                _memDirty = false;
                return true;
            }

            public bool SaveIfDirty() => _memDirty ? Save() : true;
        }

        [Fact]
        public void ProductionConfigCoordinatorHandlesNullStoreFailedMigrationRetryAndConcurrentExternalEdits()
        {
            var coord = new ConfigPersistenceCoordinator();
            var patch = new StylePendingPatch();

            // 1. Null storage MUST report saveOk=false on both Load and style edits while retaining pending patch.
            var nullSettings = coord.Load(null, isInitial: true, FakeNormalizeKey, null, out bool nullLoadSaveOk);
            Assert.False(nullLoadSaveOk);
            var nullEdit = coord.ApplyStyleMutationAndPersist(null, nullSettings, patch, s => s.LineWidth = 7.0f, isFullReset: false, FakeNormalizeKey);
            Assert.True(nullEdit.SaveAttempted);
            Assert.False(nullEdit.SaveOk);
            Assert.True(patch.HasPending);
            patch.Clear();

            // 2. Legacy v1 config migration with failed initial Save(): migration stays pending and disk stays untouched.
            var store = new FakeConfigStore { FailSave = true };
            store.SeedDisk("configVersion", 1);
            store.SeedDisk("pickerHotkey", "F8");
            store.SeedDisk("clearHotkey", "Escape");
            store.SeedDisk("clearOnEmptyClick", true);
            store.SeedDisk("lineWidth", 4.5f);
            store.SeedDisk("fontSize", 18);
            store.SeedDisk("previewSteps", 12);

            var loaded = coord.Load(store, isInitial: true, FakeNormalizeKey, null, out bool initSaveOk);
            Assert.False(initSaveOk);
            Assert.True(coord.HasPendingMigrationSave);
            Assert.Equal("F10", loaded.PickerHotkey);
            Assert.Equal(1, (int)store.Disk["configVersion"]); // Disk unchanged on failed save

            // 3. User edits lineWidth=7.0 while Save() still fails -> patch retained, disk unchanged.
            var r1 = coord.ApplyStyleMutationAndPersist(store, loaded, patch, s => s.LineWidth = 7.0f, isFullReset: false, FakeNormalizeKey);
            Assert.False(r1.SaveOk);
            Assert.True(patch.HasPending);
            Assert.Equal(4.5f, (float)store.Disk["lineWidth"]);

            // 4. External editor modifies fontSize=24 and previewSteps=16 on disk BEFORE next edit.
            store.ExternalEditDisk("fontSize", 24);
            store.ExternalEditDisk("previewSteps", 16);

            // 5. Simulate an additional external edit arriving BETWEEN the pre-mutate reload and the pre-save reload!
            int reloadCountBeforeEdit = store.ReloadCallCount;
            store.OnReloadBeforeCheck = st =>
            {
                if (st.ReloadCallCount == reloadCountBeforeEdit + 2)
                {
                    st.ExternalEditDisk("gapLength", 14.0f);
                }
            };

            // Save now succeeds: MUST preserve fontSize=24, previewSteps=16, gapLength=14.0,
            // apply pending lineWidth=7.0 + new dashSpeed=44.0, AND commit the pending v1->v3 migration!
            store.FailSave = false;
            var r2 = coord.ApplyStyleMutationAndPersist(store, r1.Settings, patch, s => s.DashSpeed = 44.0f, isFullReset: false, FakeNormalizeKey);
            Assert.True(r2.SaveOk);
            Assert.False(patch.HasPending);
            Assert.False(coord.HasPendingMigrationSave);

            Assert.Equal(3, (int)store.Disk["configVersion"]);
            Assert.Equal("F10", (string)store.Disk["pickerHotkey"]);
            Assert.False((bool)store.Disk["clearOnEmptyClick"]);
            Assert.Equal(7.0f, (float)store.Disk["lineWidth"]);
            Assert.Equal(44.0f, (float)store.Disk["dashSpeed"]);
            Assert.Equal(24, (int)store.Disk["fontSize"]);
            Assert.Equal(16, (int)store.Disk["previewSteps"]);
            Assert.Equal(14.0f, (float)store.Disk["gapLength"]);
            Assert.Equal(14.0f, r2.Settings.GapLength);
            Assert.Equal(24, r2.Settings.FontSize);
            Assert.Equal(16, r2.Settings.PreviewSteps);

            // 6. Reload failure aborts save cleanly without clobbering disk and retains pending edit for later retry.
            store.OnReloadBeforeCheck = null;
            store.ThrowOnReload = true;
            var r3 = coord.ApplyStyleMutationAndPersist(store, r2.Settings, patch, s => s.DashLength = 20.0f, isFullReset: false, FakeNormalizeKey);
            Assert.True(r3.ReloadFailed);
            Assert.False(r3.SaveOk);
            Assert.True(patch.HasPending);
            Assert.False(store.Disk.ContainsKey("dashLength"));
            store.ThrowOnReload = false;
            var r4 = coord.ApplyStyleMutationAndPersist(store, r3.Settings, patch, _ => { }, isFullReset: false, FakeNormalizeKey);
            Assert.True(r4.SaveOk);
            Assert.False(patch.HasPending);
            Assert.Equal(20.0f, (float)store.Disk["dashLength"]);
        }
    }
}
