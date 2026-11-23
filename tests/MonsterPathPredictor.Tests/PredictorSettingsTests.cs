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
                LineWidth = 100f,
                HeightOffset = float.PositiveInfinity,
                FontSize = 4,
                ClickRadius = 5f,
                ClearHotkey = "   "
            };

            s.ClampInPlace();

            Assert.Equal(30, s.PreviewSteps);
            Assert.Equal(12f, s.DashLength);
            Assert.Equal(2f, s.GapLength);
            Assert.Equal(14f, s.LineWidth);
            Assert.Equal(0.45f, s.HeightOffset);
            Assert.Equal(12, s.FontSize);
            Assert.Equal(20f, s.ClickRadius);
            Assert.Equal("Escape", s.ClearHotkey);
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
