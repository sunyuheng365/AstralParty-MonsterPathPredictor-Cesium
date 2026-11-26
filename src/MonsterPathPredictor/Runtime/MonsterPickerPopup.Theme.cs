using System;
using FairyGUI;
using UnityEngine;

namespace MonsterPathPredictor.Runtime
{
    internal sealed partial class MonsterPickerPopup
    {
        // Midnight Glass palette
        private static readonly Color Surface0 = new Color(0.055f, 0.075f, 0.105f, 0.94f);
        private static readonly Color Surface1 = new Color(0.075f, 0.10f, 0.14f, 0.98f);
        private static readonly Color Surface2 = new Color(0.11f, 0.14f, 0.19f, 0.92f);
        private static readonly Color Surface2Hover = new Color(0.15f, 0.19f, 0.26f, 0.96f);
        private static readonly Color Track = new Color(0.04f, 0.055f, 0.08f, 0.90f);
        private static readonly Color Hairline = new Color(1f, 1f, 1f, 0.07f);
        private static readonly Color HairlineStrong = new Color(1f, 1f, 1f, 0.13f);
        private static readonly Color Accent = new Color(0.36f, 0.84f, 0.96f, 1f);
        private static readonly Color AccentSoft = new Color(0.36f, 0.84f, 0.96f, 0.16f);
        private static readonly Color Gold = new Color(1f, 0.79f, 0.30f, 1f);
        private static readonly Color GoldSoft = new Color(1f, 0.79f, 0.30f, 0.14f);
        private static readonly Color Violet = new Color(0.73f, 0.55f, 1f, 1f);
        private static readonly Color VioletSoft = new Color(0.73f, 0.55f, 1f, 0.17f);
        private static readonly Color Green = new Color(0.29f, 0.87f, 0.61f, 1f);
        private static readonly Color GreenSoft = new Color(0.29f, 0.87f, 0.61f, 0.16f);
        private static readonly Color Danger = new Color(1f, 0.48f, 0.42f, 1f);
        private static readonly Color DangerSoft = new Color(1f, 0.48f, 0.42f, 0.15f);
        private static readonly Color TextPrimary = new Color(0.91f, 0.93f, 0.96f, 1f);
        private static readonly Color TextSecondary = new Color(0.60f, 0.65f, 0.72f, 1f);
        private static readonly Color TextDim = new Color(0.40f, 0.44f, 0.51f, 1f);

        // Compatibility aliases mapped to the Midnight Glass palette
        private static readonly Color PanelBg = Surface0;
        private static readonly Color PanelBorder = WithAlpha(Accent, 0.55f);
        private static readonly Color HeaderBg = Surface1;
        private static readonly Color RowNormalBg = Surface2;
        private static readonly Color RowHoverBg = Surface2Hover;
        private static readonly Color RowSelectedBg = GoldSoft;
        private static readonly Color RowSelectedBorder = WithAlpha(Gold, 0.75f);
        private static readonly Color RowActedBg = new Color(0.10f, 0.13f, 0.17f, 0.55f);
        private static readonly Color BtnBg = new Color(1f, 1f, 1f, 0.045f);
        private static readonly Color BtnHoverBg = new Color(1f, 1f, 1f, 0.09f);
        private static readonly Color BtnActiveBg = AccentSoft;
        private static readonly Color BtnVioletBg = VioletSoft;
        private static readonly Color BtnVioletBorder = WithAlpha(Violet, 0.60f);
        private static readonly Color AccentGold = Gold;
        private static readonly Color AccentCyan = Accent;
        private static readonly Color AccentViolet = Violet;
        private static readonly Color AccentGreen = Green;
        private static readonly Color TextMuted = TextSecondary;

        private const float RadiusPanel = 10f;
        private const float RadiusCard = 6f;
        private const float RadiusButton = 5f;
        private const float RadiusChip = 4f;

        private static Color WithAlpha(Color c, float alpha)
        {
            return new Color(c.r, c.g, c.b, Math.Max(0f, Math.Min(1f, alpha)));
        }

        private static Color Lighten(Color c, float amount)
        {
            if (c.a <= 0.001f) return Color.clear;
            if (c.a < 0.5f)
            {
                return new Color(
                    Math.Min(1f, c.r + amount * 0.5f),
                    Math.Min(1f, c.g + amount * 0.5f),
                    Math.Min(1f, c.b + amount * 0.5f),
                    Math.Min(0.45f, c.a + amount));
            }
            return new Color(
                Math.Min(1f, c.r + amount),
                Math.Min(1f, c.g + amount),
                Math.Min(1f, c.b + amount),
                Math.Max(c.a, 0.85f));
        }

        private static void DrawRound(GGraph g, float w, float h, float radius, Color fill, Color border)
        {
            DrawRound(g, w, h, radius, radius, radius, radius, fill, border);
        }

        private static void DrawRound(
            GGraph g,
            float w,
            float h,
            float tl,
            float tr,
            float bl,
            float br,
            Color fill,
            Color border)
        {
            if (g == null || g.isDisposed) return;
            float safeW = Math.Max(1f, w);
            float safeH = Math.Max(1f, h);
            g.SetSize(safeW, safeH);

            float maxR = Math.Min(safeW, safeH) * 0.5f;
            float rTl = Math.Max(0f, Math.Min(tl, maxR));
            float rTr = Math.Max(0f, Math.Min(tr, maxR));
            float rBl = Math.Max(0f, Math.Min(bl, maxR));
            float rBr = Math.Max(0f, Math.Min(br, maxR));
            float maxApplied = Math.Max(Math.Max(rTl, rTr), Math.Max(rBl, rBr));

            bool hasBorder = border.a > 0.001f;
            if (maxApplied < 2f)
            {
                g.DrawRect(safeW, safeH, hasBorder ? 1 : 0, border, fill);
            }
            else
            {
                g.shape.DrawRoundRect(hasBorder ? 1f : 0f, border, fill, rTl, rTr, rBl, rBr);
            }
        }

        private static void DrawCircle(GGraph g, float diameter, Color fill)
        {
            if (g == null || g.isDisposed) return;
            float d = Math.Max(1f, diameter);
            g.SetSize(d, d);
            g.DrawEllipse(d, d, fill);
        }

        private sealed class ButtonSkin
        {
            public GComponent Root;
            public GGraph Bg;
            public GTextField Label;
            public Color Fill;
            public Color Border;
            public Color Text;
            public Color HoverFill;
            public Color HoverBorder;
            public Color HoverText;
            public float Radius;
            public bool Hover;
            public bool Pressed;
            public bool Enabled = true;

            public void Redraw()
            {
                if (Root == null || Root.isDisposed || Bg == null || Bg.isDisposed) return;
                bool activeHover = Hover && Enabled;
                Color drawFill = activeHover ? HoverFill : Fill;
                Color drawBorder = activeHover ? HoverBorder : Border;
                Color drawText = activeHover ? HoverText : Text;

                if (Pressed && Enabled)
                {
                    drawFill = drawFill.a > 0.001f
                        ? new Color(drawFill.r * 0.82f, drawFill.g * 0.82f, drawFill.b * 0.82f, drawFill.a)
                        : new Color(1f, 1f, 1f, 0.03f);
                }

                DrawRound(Bg, Root.width, Root.height, Radius, drawFill, drawBorder);
                if (Label != null && !Label.isDisposed)
                {
                    var fmt = Label.textFormat;
                    fmt.color = drawText;
                    Label.textFormat = fmt;
                }
            }

            public void Resize(float w, float h)
            {
                if (Root == null || Root.isDisposed) return;
                float safeW = Math.Max(1f, w);
                float safeH = Math.Max(1f, h);
                Root.SetSize(safeW, safeH);
                if (Bg != null && !Bg.isDisposed) Bg.SetSize(safeW, safeH);
                if (Label != null && !Label.isDisposed) Label.SetSize(safeW, safeH);
                Redraw();
            }
        }

        private static ButtonSkin GetSkin(GComponent btn)
        {
            if (btn == null || btn.isDisposed) return null;
            return btn.data as ButtonSkin;
        }

        private static GComponent CreateStyledButton(
            string text,
            float w,
            float h,
            Color fill,
            Color border,
            Color textColor,
            Action onClick,
            int fontSize = 13,
            float radius = RadiusButton,
            Color? hoverFill = null,
            Color? hoverBorder = null,
            Color? hoverText = null)
        {
            float safeW = Math.Max(1f, w);
            float safeH = Math.Max(1f, h);

            var btn = new GComponent { touchable = true, opaque = true };
            btn.SetSize(safeW, safeH);

            var bg = new GGraph { touchable = false };
            bg.SetSize(safeW, safeH);
            btn.AddChild(bg);

            var tf = new GTextField
            {
                touchable = false,
                autoSize = AutoSizeType.None,
                singleLine = true,
                align = AlignType.Center,
                verticalAlign = VertAlignType.Middle,
                textFormat = new TextFormat { size = fontSize, bold = true, color = textColor },
                text = text ?? string.Empty
            };
            tf.SetSize(safeW, safeH);
            btn.AddChild(tf);

            var skin = new ButtonSkin
            {
                Root = btn,
                Bg = bg,
                Label = tf,
                Fill = fill,
                Border = border,
                Text = textColor,
                HoverFill = hoverFill ?? Lighten(fill, 0.05f),
                HoverBorder = hoverBorder ?? Lighten(border, 0.20f),
                HoverText = hoverText ?? textColor,
                Radius = radius
            };
            btn.data = skin;
            skin.Redraw();

            btn.onRollOver.Add(() =>
            {
                skin.Hover = true;
                skin.Redraw();
            });
            btn.onRollOut.Add(() =>
            {
                skin.Hover = false;
                skin.Pressed = false;
                skin.Redraw();
            });
            btn.onTouchBegin.Add(() =>
            {
                skin.Pressed = true;
                skin.Redraw();
            });
            btn.onTouchEnd.Add(() =>
            {
                skin.Pressed = false;
                skin.Redraw();
            });
            btn.onClick.Add(() => onClick?.Invoke());
            return btn;
        }

        private static GComponent CreateMiniButton(string text, float w, float h, Action onClick, int fontSize = 13)
        {
            return CreateGhostButton(text, w, h, onClick, fontSize);
        }

        private static GComponent CreateGhostButton(
            string text,
            float w,
            float h,
            Action onClick,
            int fontSize = 13,
            Color? textColor = null,
            float radius = RadiusButton)
        {
            return CreateStyledButton(
                text,
                w,
                h,
                BtnBg,
                HairlineStrong,
                textColor ?? TextPrimary,
                onClick,
                fontSize: fontSize,
                radius: radius,
                hoverFill: BtnHoverBg,
                hoverBorder: Lighten(HairlineStrong, 0.12f),
                hoverText: textColor ?? TextPrimary);
        }

        private static GComponent CreateDangerButton(
            string text,
            float w,
            float h,
            Action onClick,
            int fontSize = 13,
            float radius = RadiusButton)
        {
            return CreateStyledButton(
                text,
                w,
                h,
                Color.clear,
                WithAlpha(Danger, 0.50f),
                Danger,
                onClick,
                fontSize: fontSize,
                radius: radius,
                hoverFill: DangerSoft,
                hoverBorder: Danger,
                hoverText: Danger);
        }

        private static void SetButtonEnabled(GComponent btn, bool enabled)
        {
            if (btn == null || btn.isDisposed) return;
            btn.touchable = enabled;
            btn.alpha = enabled ? 1f : 0.42f;
            var skin = GetSkin(btn);
            if (skin != null)
            {
                skin.Enabled = enabled;
                if (!enabled)
                {
                    skin.Hover = false;
                    skin.Pressed = false;
                }
                skin.Redraw();
            }
        }

        private static void ResizeMiniButton(GComponent btn, float w, float h)
        {
            if (btn == null || btn.isDisposed) return;
            var skin = GetSkin(btn);
            if (skin != null)
            {
                skin.Resize(w, h);
                return;
            }
            btn.SetSize(w, h);
            if (btn.numChildren >= 1 && btn.GetChildAt(0) is GGraph bg)
            {
                DrawRound(bg, w, h, RadiusButton, BtnBg, HairlineStrong);
            }
            if (btn.numChildren >= 2 && btn.GetChildAt(1) is GTextField tf)
            {
                tf.SetSize(w, h);
            }
        }

        private static void StyleTabButton(GComponent btn, string label, float w, float h, bool active, bool accentViolet)
        {
            if (btn == null || btn.isDisposed) return;
            var skin = GetSkin(btn);
            if (skin == null) return;

            Color activeFill = new Color(0.17f, 0.22f, 0.30f, 1f);
            Color activeBorder = accentViolet ? WithAlpha(Violet, 0.60f) : WithAlpha(Accent, 0.55f);
            Color activeText = accentViolet ? Violet : Accent;
            Color inactiveText = accentViolet ? Violet : TextSecondary;

            skin.Fill = active ? activeFill : Color.clear;
            skin.Border = active ? activeBorder : Color.clear;
            skin.Text = active ? activeText : inactiveText;
            skin.HoverFill = active ? Lighten(activeFill, 0.04f) : new Color(1f, 1f, 1f, 0.05f);
            skin.HoverBorder = active ? Lighten(activeBorder, 0.15f) : Color.clear;
            skin.HoverText = active ? activeText : TextPrimary;
            skin.Radius = RadiusButton;

            if (skin.Label != null && !skin.Label.isDisposed)
            {
                var fmt = skin.Label.textFormat;
                fmt.size = w < 64f ? 11 : 12;
                fmt.color = active ? activeText : inactiveText;
                skin.Label.textFormat = fmt;
                skin.Label.text = label ?? string.Empty;
            }

            skin.Resize(w, h);
        }
    }
}
