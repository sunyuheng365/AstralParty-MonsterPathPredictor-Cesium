using MonsterPathPredictor.Core;
using Xunit;

namespace MonsterPathPredictor.Tests
{
    public class PredictorSettingsTests
    {
        [Fact]
        public void ClampsOutOfRangeAndNonFiniteValues()
        {
            var s = new PredictorSettings
            {
                PreviewSteps = 999,
                DashLength = float.NaN,
                GapLength = -5f,
                DashSpeed = 999f,
                LineWidth = 100f,
                HeightOffset = float.PositiveInfinity,
                FontSize = 4,
                ClickRadius = 2f,
                PickerHotkey = "",
                ClearHotkey = "   "
            };

            s.ClampInPlace();

            Assert.Equal(30, s.PreviewSteps);
            Assert.Equal(12f, s.DashLength);
            Assert.Equal(2f, s.GapLength);
            Assert.Equal(150f, s.DashSpeed);
            Assert.Equal(14f, s.LineWidth);
            Assert.Equal(0.45f, s.HeightOffset);
            Assert.Equal(12, s.FontSize);
            Assert.Equal(8f, s.ClickRadius);
            Assert.Equal("F10", s.PickerHotkey);
            Assert.Equal("Escape", s.ClearHotkey);
            Assert.Equal(StyleColor.DefaultDeterministicHex, s.DeterministicColorHex);
            Assert.Equal(StyleColor.DefaultMajorBranchHex, s.MajorBranchColorHex);
            Assert.Equal(StyleColor.DefaultMinorBranchHex, s.MinorBranchColorHex);
            Assert.Equal(StyleColor.DefaultPostCombatHex, s.PostCombatColorHex);
            Assert.Equal(StyleColor.DefaultGuideRedHex, s.GuideRedColorHex);
            Assert.Equal(StyleColor.DefaultGuideBlueHex, s.GuideBlueColorHex);
        }

        [Fact]
        public void DefaultPickerHotkeyIsF10AndResetStylePreservesHotkey()
        {
            var s = new PredictorSettings();
            Assert.Equal("F10", s.PickerHotkey);

            s.PickerHotkey = "F7";
            s.LineWidth = 10f;
            s.DashSpeed = 90f;
            s.DashLength = 30f;
            s.GapLength = 20f;
            s.FontSize = 30;
            s.PostCombatMode = PostCombatMode.StopAtFirstCombat;
            s.ShowStepNumbers = false;
            s.DeterministicColorHex = "#11223344";

            s.ResetStyleToDefaults();

            Assert.Equal("F7", s.PickerHotkey);
            Assert.Equal(4.5f, s.LineWidth);
            Assert.Equal(26f, s.DashSpeed);
            Assert.Equal(12f, s.DashLength);
            Assert.Equal(8f, s.GapLength);
            Assert.Equal(18, s.FontSize);
            Assert.Equal(PostCombatMode.ContinueByGuide, s.PostCombatMode);
            Assert.True(s.ShowStepNumbers);
            Assert.Equal(StyleColor.DefaultDeterministicHex, s.DeterministicColorHex);
        }

        [Fact]
        public void ClampsAndNormalizesCustomColorHexes()
        {
            var s = new PredictorSettings
            {
                DeterministicColorHex = "3ce68c",
                MajorBranchColorHex = "invalid-hex",
                MinorBranchColorHex = " #ff64b4cc "
            };

            s.ClampInPlace();

            Assert.Equal("#3CE68CFF", s.DeterministicColorHex);
            Assert.Equal(StyleColor.DefaultMajorBranchHex, s.MajorBranchColorHex);
            Assert.Equal("#FF64B4CC", s.MinorBranchColorHex);
        }

        [Theory]
        [InlineData("continue", PostCombatMode.ContinueByGuide)]
        [InlineData("stop", PostCombatMode.StopAtFirstCombat)]
        [InlineData("redBlue", PostCombatMode.RedBlueAfterCombat)]
        [InlineData("RED_BLUE", PostCombatMode.RedBlueAfterCombat)]
        [InlineData("unknown", PostCombatMode.ContinueByGuide)]
        public void ParsesPostCombatModeCleanly(string raw, PostCombatMode expected)
        {
            Assert.Equal(expected, PredictorSettings.ParsePostCombatMode(raw));
        }

        [Fact]
        public void ParsesExcludedBuffCsvSafely()
        {
            var ids = PredictorSettings.ParseIdCsv("1001, 2002; invalid, -5, 0, 3003");
            Assert.Equal(3, ids.Count);
            Assert.Contains(1001, ids);
            Assert.Contains(2002, ids);
            Assert.Contains(3003, ids);
        }

        [Theory]
        [InlineData(1.0, "100%")]
        [InlineData(0.5, "50%")]
        [InlineData(0.625, "62.5%")]
        [InlineData(0.375, "37.5%")]
        [InlineData(1.0 / 3.0, "33.3%")]
        public void FormatsProbabilityPercentagesCleanly(double prob, string expected)
        {
            Assert.Equal(expected, ProbabilityFormat.Percent(prob));
        }
    }
}
