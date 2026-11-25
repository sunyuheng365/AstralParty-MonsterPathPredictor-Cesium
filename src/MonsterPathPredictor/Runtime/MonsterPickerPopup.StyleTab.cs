using System;
using System.Globalization;
using FairyGUI;
using UnityEngine;
using MonsterPathPredictor.Core;

namespace MonsterPathPredictor.Runtime
{
    internal sealed partial class MonsterPickerPopup
    {
        private float BuildStyleTab(PredictorSettings settings, CompactLayout layout, float contentW)
        {
            float y = 0f;
            float maxH = layout.MaxContentHeight;
            float rowH = maxH < 360f ? 22f : 25f;
            float gap = 3f;

            void AddStepper(string label, string valueText, Action onMinus, Action onPlus)
            {
                if (y + rowH > maxH) return;
                var row = BuildStyleStepperRow(label, valueText, contentW, rowH, onMinus, onPlus);
                row.SetXY(0f, y);
                _contentHost.AddChild(row);
                y += rowH + gap;
            }

            AddStepper(
                "线条宽度",
                settings.LineWidth.ToString("0.0", CultureInfo.InvariantCulture) + " px",
                () => StyleMutationRequested?.Invoke(s => s.LineWidth = (float)Math.Round(s.LineWidth - 0.5f, 1)),
                () => StyleMutationRequested?.Invoke(s => s.LineWidth = (float)Math.Round(s.LineWidth + 0.5f, 1)));

            AddStepper(
                "行进速度",
                settings.DashSpeed <= 0.01f ? "静态 (0)" : ((int)Math.Round(settings.DashSpeed) + " px/s"),
                () => StyleMutationRequested?.Invoke(s => s.DashSpeed = Math.Max(0f, (float)Math.Round(s.DashSpeed - 6f))),
                () => StyleMutationRequested?.Invoke(s => s.DashSpeed = Math.Min(150f, (float)Math.Round(s.DashSpeed + 6f))));

            AddStepper(
                "虚线长度",
                ((int)Math.Round(settings.DashLength)) + " px",
                () => StyleMutationRequested?.Invoke(s => s.DashLength = (float)Math.Round(s.DashLength - 2f)),
                () => StyleMutationRequested?.Invoke(s => s.DashLength = (float)Math.Round(s.DashLength + 2f)));

            AddStepper(
                "虚线间距",
                ((int)Math.Round(settings.GapLength)) + " px",
                () => StyleMutationRequested?.Invoke(s => s.GapLength = (float)Math.Round(s.GapLength - 2f)),
                () => StyleMutationRequested?.Invoke(s => s.GapLength = (float)Math.Round(s.GapLength + 2f)));

            AddStepper(
                "标签字号",
                settings.FontSize + " pt",
                () => StyleMutationRequested?.Invoke(s => s.FontSize -= 2),
                () => StyleMutationRequested?.Invoke(s => s.FontSize += 2));

            // Toggles + PostCombatMode row
            if (y + rowH <= maxH)
            {
                float tGap = 3f;
                float tW = (contentW - tGap * 3f) / 4f;
                string pcLabel = settings.PostCombatMode == PostCombatMode.StopAtFirstCombat
                    ? "战后:截断"
                    : (settings.PostCombatMode == PostCombatMode.RedBlueAfterCombat ? "战后:红蓝" : "战后:续行");

                var pcBtn = CreateStyledButton(pcLabel, tW, rowH, BtnBg, AccentCyan, AccentCyan, () =>
                {
                    StyleMutationRequested?.Invoke(s =>
                    {
                        s.PostCombatMode = (PostCombatMode)(((int)s.PostCombatMode + 1) % 3);
                    });
                }, fontSize: 11);
                pcBtn.SetXY(0f, y);
                _contentHost.AddChild(pcBtn);

                AddToggleChip("步数", settings.ShowStepNumbers, tW + tGap, y, tW, rowH, () => StyleMutationRequested?.Invoke(s => s.ShowStepNumbers = !s.ShowStepNumbers));
                AddToggleChip("依据", settings.ShowReasonTag, (tW + tGap) * 2f, y, tW, rowH, () => StyleMutationRequested?.Invoke(s => s.ShowReasonTag = !s.ShowReasonTag));
                AddToggleChip("100%", settings.ShowDeterministicPercent, (tW + tGap) * 3f, y, tW, rowH, () => StyleMutationRequested?.Invoke(s => s.ShowDeterministicPercent = !s.ShowDeterministicPercent));

                y += rowH + gap + 3f;
            }

            // Color customization header + live preview swatch
            var activeRole = _selectedColorRole;
            var activeRgba = StyleColor.GetRoleColor(settings, activeRole);
            var activeUnityCol = new Color(activeRgba.Rf, activeRgba.Gf, activeRgba.Bf, 1f);

            if (y + 20f <= maxH)
            {
                var colTitle = new GTextField
                {
                    touchable = false,
                    autoSize = AutoSizeType.None,
                    singleLine = true,
                    verticalAlign = VertAlignType.Middle,
                    textFormat = new TextFormat { size = 12, bold = true, color = AccentCyan },
                    text = "配色：" + StyleColor.GetRoleLabel(activeRole) + " " + activeRgba.ToHex()
                };
                colTitle.SetXY(2f, y);
                colTitle.SetSize(Math.Max(60f, contentW - 44f), 20f);
                _contentHost.AddChild(colTitle);

                var previewSwatch = new GGraph { touchable = false };
                previewSwatch.SetXY(contentW - 38f, y + 2f);
                previewSwatch.SetSize(36f, 16f);
                previewSwatch.DrawRect(36f, 16f, 1, TextPrimary, new Color(activeRgba.Rf, activeRgba.Gf, activeRgba.Bf, Math.Max(0.25f, activeRgba.Af)));
                _contentHost.AddChild(previewSwatch);
                y += 22f;
            }

            // 6 color role selector chips (2 rows x 3 cols)
            float chipH = Math.Min(rowH, 23f);
            if (y + chipH * 2f + gap <= maxH)
            {
                float cGap = 3f;
                float cW = (contentW - cGap * 2f) / 3f;
                for (int i = 0; i < 6; i++)
                {
                    var role = (RouteColorRole)i;
                    bool sel = role == activeRole;
                    int colIdx = i % 3;
                    int rowIdx = i / 3;
                    var roleCol = StyleColor.GetRoleColor(settings, role);

                    var chip = CreateStyledButton(
                        "  " + StyleColor.GetRoleLabel(role),
                        cW,
                        chipH,
                        sel ? BtnActiveBg : BtnBg,
                        sel ? AccentGold : new Color(0.32f, 0.40f, 0.52f, 0.8f),
                        sel ? AccentGold : TextPrimary,
                        () => { _selectedColorRole = role; _lastSignature = string.Empty; },
                        fontSize: 11);

                    var dot = new GGraph { touchable = false };
                    dot.SetXY(5f, (chipH - 10f) * 0.5f);
                    dot.SetSize(10f, 10f);
                    dot.DrawRect(10f, 10f, 1, Color.black, new Color(roleCol.Rf, roleCol.Gf, roleCol.Bf, 1f));
                    chip.AddChild(dot);

                    chip.SetXY(colIdx * (cW + cGap), y + rowIdx * (chipH + gap));
                    _contentHost.AddChild(chip);
                }
                y += (chipH + gap) * 2f + 2f;
            }

            // 8 vivid 1-click preset swatches
            float swatchH = Math.Min(rowH, 22f);
            if (y + swatchH <= maxH)
            {
                int count = StyleColor.Presets.Length;
                float sGap = 3f;
                float sW = (contentW - sGap * (count - 1)) / count;
                for (int i = 0; i < count; i++)
                {
                    var preset = StyleColor.Presets[i];
                    var pc = preset.Color;
                    bool matches = pc.R == activeRgba.R && pc.G == activeRgba.G && pc.B == activeRgba.B;
                    var sw = CreateStyledButton(
                        preset.Name,
                        sW,
                        swatchH,
                        new Color(pc.Rf * 0.72f, pc.Gf * 0.72f, pc.Bf * 0.72f, 0.95f),
                        matches ? Color.white : new Color(pc.Rf, pc.Gf, pc.Bf, 0.95f),
                        (pc.R + pc.G + pc.B > 500) ? Color.black : Color.white,
                        () =>
                        {
                            var capturedHex = preset.Hex;
                            var capturedRole = activeRole;
                            StyleMutationRequested?.Invoke(s => StyleColor.SetRoleHex(s, capturedRole, capturedHex));
                        },
                        fontSize: 11);
                    sw.SetXY(i * (sW + sGap), y);
                    _contentHost.AddChild(sw);
                }
                y += swatchH + gap + 2f;
            }

            // R/G/B/A fine-tune steppers (2 rows x 2 cols)
            if (y + chipH * 2f + gap <= maxH)
            {
                float halfGap = 4f;
                float halfW = (contentW - halfGap) * 0.5f;
                AddChannelControl("R", activeRgba.R.ToString(CultureInfo.InvariantCulture), 0, 16, 0, 0f, y, halfW, chipH, activeRole, new Color(1f, 0.48f, 0.48f, 1f));
                AddChannelControl("G", activeRgba.G.ToString(CultureInfo.InvariantCulture), 1, 16, 0, halfW + halfGap, y, halfW, chipH, activeRole, new Color(0.48f, 1f, 0.62f, 1f));
                y += chipH + gap;

                int alphaPct = (int)Math.Round(activeRgba.A * 100.0 / 255.0);
                AddChannelControl("B", activeRgba.B.ToString(CultureInfo.InvariantCulture), 2, 16, 0, 0f, y, halfW, chipH, activeRole, new Color(0.48f, 0.78f, 1f, 1f));
                AddChannelControl("透", alphaPct + "%", 3, 15, 51, halfW + halfGap, y, halfW, chipH, activeRole, activeUnityCol);
                y += chipH + gap + 3f;
            }

            // Reset to default style button
            if (y + rowH <= maxH)
            {
                var resetBtn = CreateStyledButton(
                    "恢复默认路线样式",
                    contentW,
                    rowH,
                    BtnBg,
                    new Color(0.55f, 0.42f, 0.30f, 0.9f),
                    AccentGold,
                    () => StyleResetRequested?.Invoke(),
                    fontSize: 12);
                resetBtn.SetXY(0f, y);
                _contentHost.AddChild(resetBtn);
                y += rowH + 2f;
            }

            return Math.Min(maxH, Math.Max(32f, y));
        }

        private void AddToggleChip(string label, bool isOn, float x, float y, float w, float h, Action onClick)
        {
            var btn = CreateStyledButton(
                label + (isOn ? ":开" : ":关"),
                w,
                h,
                isOn ? BtnActiveBg : BtnBg,
                isOn ? AccentGreen : new Color(0.32f, 0.38f, 0.48f, 0.75f),
                isOn ? AccentGreen : TextMuted,
                onClick,
                fontSize: 11);
            btn.SetXY(x, y);
            _contentHost.AddChild(btn);
        }

        private void AddChannelControl(
            string tag,
            string valText,
            int channelIndex,
            int step,
            byte minAlpha,
            float x,
            float y,
            float w,
            float h,
            RouteColorRole role,
            Color tagColor)
        {
            var host = new GComponent { touchable = true };
            host.SetXY(x, y);
            host.SetSize(w, h);

            var bg = new GGraph { touchable = false };
            bg.SetSize(w, h);
            bg.DrawRect(w, h, 1, new Color(0.24f, 0.30f, 0.40f, 0.65f), RowNormalBg);
            host.AddChild(bg);

            float btnW = Math.Min(24f, Math.Max(18f, w * 0.22f));
            var tagTf = new GTextField
            {
                touchable = false,
                autoSize = AutoSizeType.None,
                singleLine = true,
                verticalAlign = VertAlignType.Middle,
                textFormat = new TextFormat { size = 11, bold = true, color = tagColor },
                text = tag
            };
            tagTf.SetXY(5f, 0f);
            tagTf.SetSize(20f, h);
            host.AddChild(tagTf);

            var minus = CreateMiniButton("-", btnW, Math.Max(14f, h - 4f), () =>
            {
                StyleMutationRequested?.Invoke(s =>
                {
                    var cur = StyleColor.GetRoleColor(s, role);
                    var next = StyleColor.AdjustChannel(cur, channelIndex, -step, minAlpha);
                    StyleColor.SetRoleHex(s, role, next.ToHex());
                });
            }, fontSize: 12);
            minus.SetXY(24f, (h - minus.height) * 0.5f);
            host.AddChild(minus);

            var plus = CreateMiniButton("+", btnW, Math.Max(14f, h - 4f), () =>
            {
                StyleMutationRequested?.Invoke(s =>
                {
                    var cur = StyleColor.GetRoleColor(s, role);
                    var next = StyleColor.AdjustChannel(cur, channelIndex, step, minAlpha);
                    StyleColor.SetRoleHex(s, role, next.ToHex());
                });
            }, fontSize: 12);
            plus.SetXY(w - btnW - 3f, (h - plus.height) * 0.5f);
            host.AddChild(plus);

            float valX = 24f + btnW + 2f;
            float valW = Math.Max(16f, plus.x - valX - 2f);
            var valTf = new GTextField
            {
                touchable = false,
                autoSize = AutoSizeType.None,
                singleLine = true,
                align = AlignType.Center,
                verticalAlign = VertAlignType.Middle,
                textFormat = new TextFormat { size = 11, color = TextPrimary },
                text = valText
            };
            valTf.SetXY(valX, 0f);
            valTf.SetSize(valW, h);
            host.AddChild(valTf);

            _contentHost.AddChild(host);
        }

        private static GComponent BuildStyleStepperRow(string label, string valueText, float w, float h, Action onMinus, Action onPlus)
        {
            var row = new GComponent { touchable = true };
            row.SetSize(w, h);

            var bg = new GGraph { touchable = false };
            bg.SetSize(w, h);
            bg.DrawRect(w, h, 1, new Color(0.24f, 0.30f, 0.40f, 0.65f), RowNormalBg);
            row.AddChild(bg);

            float btnW = w < 220f ? 22f : 26f;
            float btnH = Math.Max(15f, h - 4f);
            float btnY = (h - btnH) * 0.5f;
            float valW = w < 220f ? 52f : 68f;
            float plusX = w - btnW - 4f;
            float valX = plusX - valW - 4f;
            float minusX = valX - btnW - 4f;

            var lbl = new GTextField
            {
                touchable = false,
                autoSize = AutoSizeType.None,
                singleLine = true,
                verticalAlign = VertAlignType.Middle,
                textFormat = new TextFormat { size = 12, color = TextPrimary },
                text = label
            };
            lbl.SetXY(6f, 0f);
            lbl.SetSize(Math.Max(36f, minusX - 10f), h);
            row.AddChild(lbl);

            var minus = CreateMiniButton("-", btnW, btnH, onMinus, fontSize: 13);
            minus.SetXY(minusX, btnY);
            row.AddChild(minus);

            var val = new GTextField
            {
                touchable = false,
                autoSize = AutoSizeType.None,
                singleLine = true,
                align = AlignType.Center,
                verticalAlign = VertAlignType.Middle,
                textFormat = new TextFormat { size = 12, bold = true, color = AccentGold },
                text = valueText
            };
            val.SetXY(valX, 0f);
            val.SetSize(valW, h);
            row.AddChild(val);

            var plus = CreateMiniButton("+", btnW, btnH, onPlus, fontSize: 13);
            plus.SetXY(plusX, btnY);
            row.AddChild(plus);

            return row;
        }
    }
}
