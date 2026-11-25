using System;
using System.Globalization;

namespace MonsterPathPredictor.Core
{
    public readonly struct RgbaColor : IEquatable<RgbaColor>
    {
        public byte R { get; }
        public byte G { get; }
        public byte B { get; }
        public byte A { get; }

        public float Rf => R / 255f;
        public float Gf => G / 255f;
        public float Bf => B / 255f;
        public float Af => A / 255f;

        public RgbaColor(byte r, byte g, byte b, byte a = 255)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }

        public RgbaColor WithAlpha(byte alpha) => new RgbaColor(R, G, B, alpha);

        public RgbaColor WithAlphaScale(float scale)
        {
            if (float.IsNaN(scale) || float.IsInfinity(scale)) scale = 1f;
            if (scale < 0f) scale = 0f;
            if (scale > 1f) scale = 1f;
            byte a = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(A * scale)));
            return new RgbaColor(R, G, B, a);
        }

        public string ToHex()
        {
            return "#" +
                   R.ToString("X2", CultureInfo.InvariantCulture) +
                   G.ToString("X2", CultureInfo.InvariantCulture) +
                   B.ToString("X2", CultureInfo.InvariantCulture) +
                   A.ToString("X2", CultureInfo.InvariantCulture);
        }

        public bool Equals(RgbaColor other) => R == other.R && G == other.G && B == other.B && A == other.A;
        public override bool Equals(object obj) => obj is RgbaColor other && Equals(other);
        public override int GetHashCode() => (R << 24) | (G << 16) | (B << 8) | A;
        public static bool operator ==(RgbaColor left, RgbaColor right) => left.Equals(right);
        public static bool operator !=(RgbaColor left, RgbaColor right) => !left.Equals(right);
    }

    public enum RouteColorRole
    {
        Deterministic = 0,
        MajorBranch = 1,
        MinorBranch = 2,
        PostCombat = 3,
        GuideRed = 4,
        GuideBlue = 5
    }

    public readonly struct ColorPreset
    {
        public string Name { get; }
        public string Hex { get; }
        public RgbaColor Color { get; }

        public ColorPreset(string name, string hex)
        {
            Name = name;
            Hex = StyleColor.NormalizeHex(hex, StyleColor.DefaultDeterministicHex);
            Color = StyleColor.ParseOrDefault(Hex, StyleColor.DefaultDeterministic);
        }
    }

    /// <summary>
    /// Pure-C# color parsing, normalization, channel adjustment, and route palette presets.
    /// </summary>
    public static class StyleColor
    {
        public const string DefaultDeterministicHex = "#3DE6FFF5";
        public const string DefaultMajorBranchHex = "#FFD138F5";
        public const string DefaultMinorBranchHex = "#FF7A47E0";
        public const string DefaultPostCombatHex = "#94C7F28F";
        public const string DefaultGuideRedHex = "#FF5252FA";
        public const string DefaultGuideBlueHex = "#42ADFFFA";
        public const string DefaultVirtualPlayerHex = "#D26BFFFA";

        public static readonly RgbaColor DefaultDeterministic = new RgbaColor(0x3D, 0xE6, 0xFF, 0xF5);
        public static readonly RgbaColor DefaultMajorBranch = new RgbaColor(0xFF, 0xD1, 0x38, 0xF5);
        public static readonly RgbaColor DefaultMinorBranch = new RgbaColor(0xFF, 0x7A, 0x47, 0xE0);
        public static readonly RgbaColor DefaultPostCombat = new RgbaColor(0x94, 0xC7, 0xF2, 0x8F);
        public static readonly RgbaColor DefaultGuideRed = new RgbaColor(0xFF, 0x52, 0x52, 0xFA);
        public static readonly RgbaColor DefaultGuideBlue = new RgbaColor(0x42, 0xAD, 0xFF, 0xFA);
        public static readonly RgbaColor DefaultVirtualPlayer = new RgbaColor(0xD2, 0x6B, 0xFF, 0xFA);

        public static readonly ColorPreset[] Presets =
        {
            new ColorPreset("青", "#3DE6FFF5"),
            new ColorPreset("金", "#FFD138F5"),
            new ColorPreset("橙", "#FF7A47F0"),
            new ColorPreset("绿", "#3CE68CF5"),
            new ColorPreset("紫", "#D26BFFF5"),
            new ColorPreset("粉", "#FF64B4F5"),
            new ColorPreset("红", "#FF4747FA"),
            new ColorPreset("白", "#EDF6FFF5")
        };

        public static bool TryParseHex(string raw, out RgbaColor color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            string s = raw.Trim();
            if (s[0] == '#') s = s.Substring(1);
            if (s.Length != 6 && s.Length != 8) return false;

            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                bool isHex = (ch >= '0' && ch <= '9') ||
                             (ch >= 'a' && ch <= 'f') ||
                             (ch >= 'A' && ch <= 'F');
                if (!isHex) return false;
            }

            if (!byte.TryParse(s.Substring(0, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte r)) return false;
            if (!byte.TryParse(s.Substring(2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte g)) return false;
            if (!byte.TryParse(s.Substring(4, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte b)) return false;
            byte a = 255;
            if (s.Length == 8 &&
                !byte.TryParse(s.Substring(6, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out a))
            {
                return false;
            }

            color = new RgbaColor(r, g, b, a);
            return true;
        }

        public static RgbaColor ParseOrDefault(string raw, RgbaColor fallback)
        {
            return TryParseHex(raw, out var parsed) ? parsed : fallback;
        }

        public static string NormalizeHex(string raw, string fallbackHex)
        {
            if (TryParseHex(raw, out var parsed)) return parsed.ToHex();
            if (TryParseHex(fallbackHex, out var fb)) return fb.ToHex();
            return DefaultDeterministicHex;
        }

        public static RgbaColor AdjustChannel(RgbaColor color, int channelIndex, int delta, byte minAlpha = 0)
        {
            long r = color.R;
            long g = color.G;
            long b = color.B;
            long a = color.A;
            switch (channelIndex)
            {
                case 0: r = ClampByte(r + delta, 0, 255); break;
                case 1: g = ClampByte(g + delta, 0, 255); break;
                case 2: b = ClampByte(b + delta, 0, 255); break;
                case 3:
                    // Never raise alpha when the user presses '-' on a low-alpha config color.
                    long effectiveMin = delta < 0 ? Math.Min(a, (long)minAlpha) : (long)minAlpha;
                    a = ClampByte(a + delta, effectiveMin, 255);
                    break;
            }
            return new RgbaColor((byte)r, (byte)g, (byte)b, (byte)a);
        }

        public static string GetRoleLabel(RouteColorRole role)
        {
            switch (role)
            {
                case RouteColorRole.Deterministic: return "主路线";
                case RouteColorRole.MajorBranch: return "高概率";
                case RouteColorRole.MinorBranch: return "低概率";
                case RouteColorRole.PostCombat: return "战后续行";
                case RouteColorRole.GuideRed: return "红箭";
                case RouteColorRole.GuideBlue: return "蓝箭";
                default: return "主路线";
            }
        }

        public static RgbaColor GetRoleDefault(RouteColorRole role)
        {
            switch (role)
            {
                case RouteColorRole.MajorBranch: return DefaultMajorBranch;
                case RouteColorRole.MinorBranch: return DefaultMinorBranch;
                case RouteColorRole.PostCombat: return DefaultPostCombat;
                case RouteColorRole.GuideRed: return DefaultGuideRed;
                case RouteColorRole.GuideBlue: return DefaultGuideBlue;
                default: return DefaultDeterministic;
            }
        }

        public static string GetRoleHex(PredictorSettings settings, RouteColorRole role)
        {
            if (settings == null) return GetRoleDefault(role).ToHex();
            switch (role)
            {
                case RouteColorRole.MajorBranch: return settings.MajorBranchColorHex;
                case RouteColorRole.MinorBranch: return settings.MinorBranchColorHex;
                case RouteColorRole.PostCombat: return settings.PostCombatColorHex;
                case RouteColorRole.GuideRed: return settings.GuideRedColorHex;
                case RouteColorRole.GuideBlue: return settings.GuideBlueColorHex;
                default: return settings.DeterministicColorHex;
            }
        }

        public static RgbaColor GetRoleColor(PredictorSettings settings, RouteColorRole role)
        {
            return ParseOrDefault(GetRoleHex(settings, role), GetRoleDefault(role));
        }

        public static void SetRoleHex(PredictorSettings settings, RouteColorRole role, string hex)
        {
            if (settings == null) return;
            string normalized = NormalizeHex(hex, GetRoleDefault(role).ToHex());
            switch (role)
            {
                case RouteColorRole.MajorBranch: settings.MajorBranchColorHex = normalized; break;
                case RouteColorRole.MinorBranch: settings.MinorBranchColorHex = normalized; break;
                case RouteColorRole.PostCombat: settings.PostCombatColorHex = normalized; break;
                case RouteColorRole.GuideRed: settings.GuideRedColorHex = normalized; break;
                case RouteColorRole.GuideBlue: settings.GuideBlueColorHex = normalized; break;
                default: settings.DeterministicColorHex = normalized; break;
            }
        }

        private static long ClampByte(long v, long min, long max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }
    }
}
