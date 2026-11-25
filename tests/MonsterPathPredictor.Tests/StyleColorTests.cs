using MonsterPathPredictor.Core;
using Xunit;

namespace MonsterPathPredictor.Tests
{
    public class StyleColorTests
    {
        [Theory]
        [InlineData("#3DE6FF", 0x3D, 0xE6, 0xFF, 0xFF)]
        [InlineData("3de6ff80", 0x3D, 0xE6, 0xFF, 0x80)]
        [InlineData(" #FF5252FA ", 0xFF, 0x52, 0x52, 0xFA)]
        public void TryParseHexAcceptsValidFormats(string raw, byte r, byte g, byte b, byte a)
        {
            Assert.True(StyleColor.TryParseHex(raw, out var color));
            Assert.Equal(new RgbaColor(r, g, b, a), color);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("#FFF")]
        [InlineData("#GGGGGG")]
        [InlineData("#1234567")]
        [InlineData("#F FFFF")]
        [InlineData("#3D E6FF")]
        [InlineData("#3DE6FF 0")]
        public void TryParseHexRejectsMalformedInputs(string raw)
        {
            Assert.False(StyleColor.TryParseHex(raw, out _));
        }

        [Fact]
        public void AdjustChannelClampsWithinBoundsAndNeverRaisesAlphaOnMinus()
        {
            var c = new RgbaColor(250, 10, 128, 60);
            Assert.Equal(255, StyleColor.AdjustChannel(c, 0, 20).R);
            Assert.Equal(0, StyleColor.AdjustChannel(c, 1, -20).G);
            Assert.Equal(144, StyleColor.AdjustChannel(c, 2, 16).B);
            Assert.Equal(51, StyleColor.AdjustChannel(c, 3, -30, minAlpha: 51).A);
            Assert.Equal(255, StyleColor.AdjustChannel(c, 3, 250, minAlpha: 51).A);

            // Low-alpha config color (16): pressing '-' must never jump UP to minAlpha (51)
            var lowAlpha = new RgbaColor(100, 150, 200, 16);
            Assert.Equal(16, StyleColor.AdjustChannel(lowAlpha, 3, -15, minAlpha: 51).A);
            Assert.Equal(1, StyleColor.AdjustChannel(lowAlpha, 3, -15, minAlpha: 0).A);
        }

        [Fact]
        public void RoleGetAndSetRoundTripAllSixRoles()
        {
            var settings = new PredictorSettings();
            for (int i = 0; i < 6; i++)
            {
                var role = (RouteColorRole)i;
                string hex = "#102030" + (0xA0 + i).ToString("X2");
                StyleColor.SetRoleHex(settings, role, hex);
                Assert.Equal(hex, StyleColor.GetRoleHex(settings, role));
                Assert.Equal(hex, StyleColor.GetRoleColor(settings, role).ToHex());
            }
        }

        [Fact]
        public void PresetsAreAllValidAndNormalized()
        {
            Assert.Equal(8, StyleColor.Presets.Length);
            foreach (var p in StyleColor.Presets)
            {
                Assert.False(string.IsNullOrWhiteSpace(p.Name));
                Assert.True(StyleColor.TryParseHex(p.Hex, out var parsed));
                Assert.Equal(p.Color, parsed);
            }
        }
    }
}
