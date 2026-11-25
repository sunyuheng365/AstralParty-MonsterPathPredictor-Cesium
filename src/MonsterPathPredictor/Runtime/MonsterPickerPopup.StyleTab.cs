using System;
using System.Collections.Generic;
using System.Globalization;
using FairyGUI;
using UnityEngine;
using MonsterPathPredictor.Core;

namespace MonsterPathPredictor.Runtime
{
    internal sealed partial class MonsterPickerPopup
    {
        private static void ComputeStyleTabLayout(
            float stageHeight,
            float contentW,
            bool hasSaveWarn,
            out CompactLayout layout,
            out List<PopupPagination.StyleRowItem> rows,
            out List<PageSlice> pages,
            out float rowGap)
        {
            var singleLayout = ComputeCompactLayout(stageHeight, showPager: false);
            var singleRows = PopupPagination.BuildStyleRows(singleLayout.MaxContentHeight, contentW, hasSaveWarn, out float singleGap);
            var singlePages = PopupPagination.PaginateStyleRows(singleRows, singleGap, singleLayout.MaxContentHeight);
            if (singlePages.Count <= 1)
            {
                layout = singleLayout;
                rows = singleRows;
                pages = singlePages;
                rowGap = singleGap;
                return;
            }

            layout = ComputeCompactLayout(stageHeight, showPager: true);
            rows = PopupPagination.BuildStyleRows(layout.MaxContentHeight, contentW, hasSaveWarn, out rowGap);
            pages = PopupPagination.PaginateStyleRows(rows, rowGap, layout.MaxContentHeight);
        }

        private float BuildStyleTab(
            PredictorSettings settings,
            CompactLayout layout,
            float contentW,
            IReadOnlyList<PopupPagination.StyleRowItem> rows,
            PageSlice slice,
            float rowGap)
        {
            float y = 0f;
            float maxH = layout.MaxContentHeight;
            if (rows == null || slice.Count == 0) return Math.Min(maxH, 32f);

            var activeRole = _selectedColorRole;
            var activeRgba = StyleColor.GetRoleColor(settings, activeRole);
            var activeUnityCol = new Color(activeRgba.Rf, activeRgba.Gf, activeRgba.Bf, 1f);
            int alphaPct = (int)Math.Round(activeRgba.A * 100.0 / 255.0);

            void AddStepper(string label, string valueText, float h, Action onMinus, Action onPlus)
            {
                var row = BuildStyleStepperRow(label, valueText, contentW, h, onMinus, onPlus);
                row.SetXY(0f, y);
                _contentHost.AddChild(row);
            }

            void AddColorRoleRow(int startRoleIndex, float chipH)
            {
                float cGap = 3f;
                float cW = (contentW - cGap * 2f) / 3f;
                for (int c = 0; c < 3; c++)
                {
                    int roleIdx = startRoleIndex + c;
                    if (roleIdx >= 6) break;
                    var role = (RouteColorRole)roleIdx;
                    bool sel = role == activeRole;
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

                    chip.SetXY(c * (cW + cGap), y);
                    _contentHost.AddChild(chip);
                }
            }

            int end = Math.Min(rows.Count, slice.StartIndex + slice.Count);
            for (int idx = slice.StartIndex; idx < end; idx++)
            {
                var item = rows[idx];
                if (idx > slice.StartIndex) y += rowGap;

                switch (item.Kind)
                {
                    case PopupPagination.StyleRowKind.SaveWarning:
                    {
                        var warn = new GTextField
                        {
                            touchable = false,
                            autoSize = AutoSizeType.None,
                            singleLine = true,
                            textFormat = new TextFormat { size = 11, bold = true, color = new Color(1f, 0.42f, 0.36f, 1f) },
                            text = "⚠ 样式写入 config.json 失败（仅本局临时生效）"
                        };
                        warn.SetXY(2f, y);
                        warn.SetSize(contentW - 4f, item.Height);
                        _contentHost.AddChild(warn);
                        break;
                    }
                    case PopupPagination.StyleRowKind.LineWidth:
                        AddStepper(
                            "线条宽度",
                            settings.LineWidth.ToString("0.0", CultureInfo.InvariantCulture) + " px",
                            item.Height,
                            () => StyleMutationRequested?.Invoke(s => s.LineWidth = (float)Math.Round(s.LineWidth - 0.5f, 1)),
                            () => StyleMutationRequested?.Invoke(s => s.LineWidth = (float)Math.Round(s.LineWidth + 0.5f, 1)));
                        break;
                    case PopupPagination.StyleRowKind.DashSpeed:
                        AddStepper(
                            "行进速度",
                            settings.DashSpeed <= 0.01f ? "静态 (0)" : ((int)Math.Round(settings.DashSpeed) + " px/s"),
                            item.Height,
                            () => StyleMutationRequested?.Invoke(s => s.DashSpeed = Math.Max(0f, (float)Math.Round(s.DashSpeed - 6f))),
                            () => StyleMutationRequested?.Invoke(s => s.DashSpeed = Math.Min(150f, (float)Math.Round(s.DashSpeed + 6f))));
                        break;
                    case PopupPagination.StyleRowKind.DashLength:
                        AddStepper(
                            "虚线长度",
                            ((int)Math.Round(settings.DashLength)) + " px",
                            item.Height,
                            () => StyleMutationRequested?.Invoke(s => s.DashLength = (float)Math.Round(s.DashLength - 2f)),
                            () => StyleMutationRequested?.Invoke(s => s.DashLength = (float)Math.Round(s.DashLength + 2f)));
                        break;
                    case PopupPagination.StyleRowKind.GapLength:
                        AddStepper(
                            "虚线间距",
                            ((int)Math.Round(settings.GapLength)) + " px",
                            item.Height,
                            () => StyleMutationRequested?.Invoke(s => s.GapLength = (float)Math.Round(s.GapLength - 2f)),
                            () => StyleMutationRequested?.Invoke(s => s.GapLength = (float)Math.Round(s.GapLength + 2f)));
                        break;
                    case PopupPagination.StyleRowKind.FontSize:
                        AddStepper(
                            "标签字号",
                            settings.FontSize + " pt",
                            item.Height,
                            () => StyleMutationRequested?.Invoke(s => s.FontSize -= 2),
                            () => StyleMutationRequested?.Invoke(s => s.FontSize += 2));
                        break;
                    case PopupPagination.StyleRowKind.HeightOffset:
                        AddStepper(
                            "悬浮高度",
                            settings.HeightOffset.ToString("0.00", CultureInfo.InvariantCulture),
                            item.Height,
                            () => StyleMutationRequested?.Invoke(s => s.HeightOffset = (float)Math.Round(s.HeightOffset - 0.1f, 2)),
                            () => StyleMutationRequested?.Invoke(s => s.HeightOffset = (float)Math.Round(s.HeightOffset + 0.1f, 2)));
                        break;
                    case PopupPagination.StyleRowKind.Toggles:
                    {
                        float tGap = 3f;
                        float tW = (contentW - tGap * 3f) / 4f;
                        string pcLabel = settings.PostCombatMode == PostCombatMode.StopAtFirstCombat
                            ? "战后:截断"
                            : (settings.PostCombatMode == PostCombatMode.RedBlueAfterCombat ? "战后:红蓝" : "战后:续行");

                        var pcBtn = CreateStyledButton(pcLabel, tW, item.Height, BtnBg, AccentCyan, AccentCyan, () =>
                        {
                            StyleMutationRequested?.Invoke(s =>
                            {
                                s.PostCombatMode = (PostCombatMode)(((int)s.PostCombatMode + 1) % 3);
                            });
                        }, fontSize: 11);
                        pcBtn.SetXY(0f, y);
                        _contentHost.AddChild(pcBtn);

                        AddToggleChip("步数", settings.ShowStepNumbers, tW + tGap, y, tW, item.Height, () => StyleMutationRequested?.Invoke(s => s.ShowStepNumbers = !s.ShowStepNumbers));
                        AddToggleChip("依据", settings.ShowReasonTag, (tW + tGap) * 2f, y, tW, item.Height, () => StyleMutationRequested?.Invoke(s => s.ShowReasonTag = !s.ShowReasonTag));
                        AddToggleChip("100%", settings.ShowDeterministicPercent, (tW + tGap) * 3f, y, tW, item.Height, () => StyleMutationRequested?.Invoke(s => s.ShowDeterministicPercent = !s.ShowDeterministicPercent));
                        break;
                    }
                    case PopupPagination.StyleRowKind.ColorHeader:
                    {
                        bool showSwatch = contentW >= 68f;
                        float titleW = showSwatch ? Math.Max(12f, contentW - 44f) : Math.Max(12f, contentW - 4f);
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
                        colTitle.SetSize(titleW, item.Height);
                        _contentHost.AddChild(colTitle);

                        if (showSwatch)
                        {
                            float swH = Math.Max(6f, Math.Min(16f, item.Height - 4f));
                            float swY = y + Math.Max(0f, (item.Height - swH) * 0.5f);
                            var swBackDark = new GGraph { touchable = false };
                            swBackDark.SetXY(contentW - 38f, swY);
                            swBackDark.SetSize(18f, swH);
                            swBackDark.DrawRect(18f, swH, 0, Color.clear, new Color(0.10f, 0.12f, 0.16f, 1f));
                            _contentHost.AddChild(swBackDark);

                            var swBackLight = new GGraph { touchable = false };
                            swBackLight.SetXY(contentW - 20f, swY);
                            swBackLight.SetSize(18f, swH);
                            swBackLight.DrawRect(18f, swH, 0, Color.clear, new Color(0.75f, 0.78f, 0.84f, 1f));
                            _contentHost.AddChild(swBackLight);

                            var previewSwatch = new GGraph { touchable = false };
                            previewSwatch.SetXY(contentW - 38f, swY);
                            previewSwatch.SetSize(36f, swH);
                            previewSwatch.DrawRect(36f, swH, 1, TextPrimary, new Color(activeRgba.Rf, activeRgba.Gf, activeRgba.Bf, activeRgba.Af));
                            _contentHost.AddChild(previewSwatch);
                        }
                        break;
                    }
                    case PopupPagination.StyleRowKind.ColorRolesRow1:
                        AddColorRoleRow(0, item.Height);
                        break;
                    case PopupPagination.StyleRowKind.ColorRolesRow2:
                        AddColorRoleRow(3, item.Height);
                        break;
                    case PopupPagination.StyleRowKind.Presets:
                        AddPresetSwatchRow(0, StyleColor.Presets.Length, y, contentW, item.Height, activeRole, activeRgba);
                        break;
                    case PopupPagination.StyleRowKind.PresetsRow1:
                        AddPresetSwatchRow(0, Math.Min(4, StyleColor.Presets.Length), y, contentW, item.Height, activeRole, activeRgba);
                        break;
                    case PopupPagination.StyleRowKind.PresetsRow2:
                        AddPresetSwatchRow(4, Math.Max(0, StyleColor.Presets.Length - 4), y, contentW, item.Height, activeRole, activeRgba);
                        break;
                    case PopupPagination.StyleRowKind.RgbaPairRG:
                    {
                        float halfGap = 4f;
                        float halfW = (contentW - halfGap) * 0.5f;
                        AddChannelControl("R", activeRgba.R.ToString(CultureInfo.InvariantCulture), 0, 16, 0, 0f, y, halfW, item.Height, activeRole, new Color(1f, 0.48f, 0.48f, 1f));
                        AddChannelControl("G", activeRgba.G.ToString(CultureInfo.InvariantCulture), 1, 16, 0, halfW + halfGap, y, halfW, item.Height, activeRole, new Color(0.48f, 1f, 0.62f, 1f));
                        break;
                    }
                    case PopupPagination.StyleRowKind.RgbaPairBA:
                    {
                        float halfGap = 4f;
                        float halfW = (contentW - halfGap) * 0.5f;
                        AddChannelControl("B", activeRgba.B.ToString(CultureInfo.InvariantCulture), 2, 16, 0, 0f, y, halfW, item.Height, activeRole, new Color(0.48f, 0.78f, 1f, 1f));
                        AddChannelControl("透", alphaPct + "%", 3, 15, 0, halfW + halfGap, y, halfW, item.Height, activeRole, activeUnityCol);
                        break;
                    }
                    case PopupPagination.StyleRowKind.RgbaSingleR:
                        AddChannelControl("R", activeRgba.R.ToString(CultureInfo.InvariantCulture), 0, 16, 0, 0f, y, contentW, item.Height, activeRole, new Color(1f, 0.48f, 0.48f, 1f));
                        break;
                    case PopupPagination.StyleRowKind.RgbaSingleG:
                        AddChannelControl("G", activeRgba.G.ToString(CultureInfo.InvariantCulture), 1, 16, 0, 0f, y, contentW, item.Height, activeRole, new Color(0.48f, 1f, 0.62f, 1f));
                        break;
                    case PopupPagination.StyleRowKind.RgbaSingleB:
                        AddChannelControl("B", activeRgba.B.ToString(CultureInfo.InvariantCulture), 2, 16, 0, 0f, y, contentW, item.Height, activeRole, new Color(0.48f, 0.78f, 1f, 1f));
                        break;
                    case PopupPagination.StyleRowKind.RgbaSingleA:
                        AddChannelControl("透", alphaPct + "%", 3, 15, 0, 0f, y, contentW, item.Height, activeRole, activeUnityCol);
                        break;
                    case PopupPagination.StyleRowKind.ResetDefaults:
                    {
                        var resetBtn = CreateStyledButton(
                            "恢复默认路线样式",
                            contentW,
                            item.Height,
                            BtnBg,
                            new Color(0.55f, 0.42f, 0.30f, 0.9f),
                            AccentGold,
                            () => StyleResetRequested?.Invoke(),
                            fontSize: 12);
                        resetBtn.SetXY(0f, y);
                        _contentHost.AddChild(resetBtn);
                        break;
                    }
                }

                y += item.Height;
            }

            return Math.Min(maxH, Math.Max(0f, y));
        }

        private void AddPresetSwatchRow(int startIdx, int count, float y, float contentW, float swatchH, RouteColorRole activeRole, RgbaColor activeRgba)
        {
            if (count <= 0) return;
            float sGap = contentW < 100f ? 1f : 3f;
            float sW = Math.Max(6f, (contentW - sGap * (count - 1)) / count);
            for (int i = 0; i < count; i++)
            {
                var preset = StyleColor.Presets[startIdx + i];
                var pc = preset.Color;
                bool matches = pc == activeRgba;
                var sw = CreateStyledButton(
                    sW < 18f ? string.Empty : preset.Name,
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
                    fontSize: sW < 26f ? 10 : 11);
                sw.SetXY(i * (sW + sGap), y);
                _contentHost.AddChild(sw);
            }
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

            PopupPagination.ComputeChannelGeometry(
                w,
                out float tagX, out float tagW,
                out float minusX, out float btnW,
                out float valX, out float valW,
                out float plusX);
            float btnH = Math.Max(8f, Math.Min(h - 2f, h - 4f));

            var tagTf = new GTextField
            {
                touchable = false,
                autoSize = AutoSizeType.None,
                singleLine = true,
                verticalAlign = VertAlignType.Middle,
                textFormat = new TextFormat { size = w < 100f ? 10 : 11, bold = true, color = tagColor },
                text = tag
            };
            tagTf.SetXY(tagX, 0f);
            tagTf.SetSize(tagW, h);
            host.AddChild(tagTf);

            var minus = CreateMiniButton("-", btnW, btnH, () =>
            {
                StyleMutationRequested?.Invoke(s =>
                {
                    var cur = StyleColor.GetRoleColor(s, role);
                    var next = StyleColor.AdjustChannel(cur, channelIndex, -step, minAlpha);
                    StyleColor.SetRoleHex(s, role, next.ToHex());
                });
            }, fontSize: w < 100f ? 10 : 12);
            minus.SetXY(minusX, (h - minus.height) * 0.5f);
            host.AddChild(minus);

            var plus = CreateMiniButton("+", btnW, btnH, () =>
            {
                StyleMutationRequested?.Invoke(s =>
                {
                    var cur = StyleColor.GetRoleColor(s, role);
                    var next = StyleColor.AdjustChannel(cur, channelIndex, step, minAlpha);
                    StyleColor.SetRoleHex(s, role, next.ToHex());
                });
            }, fontSize: w < 100f ? 10 : 12);
            plus.SetXY(plusX, (h - plus.height) * 0.5f);
            host.AddChild(plus);

            if (valW >= 8f)
            {
                var valTf = new GTextField
                {
                    touchable = false,
                    autoSize = AutoSizeType.None,
                    singleLine = true,
                    align = AlignType.Center,
                    verticalAlign = VertAlignType.Middle,
                    textFormat = new TextFormat { size = w < 100f ? 10 : 11, color = TextPrimary },
                    text = valText
                };
                valTf.SetXY(valX, 0f);
                valTf.SetSize(valW, h);
                host.AddChild(valTf);
            }

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

            float pad = w < 160f ? 2f : 4f;
            float btnW = w < 160f ? 18f : (w < 220f ? 22f : 26f);
            float btnH = Math.Max(15f, h - 4f);
            float btnY = (h - btnH) * 0.5f;
            float valW = w < 160f ? Math.Max(26f, w * 0.28f) : (w < 220f ? 52f : 68f);
            float plusX = Math.Max(pad, w - btnW - pad);
            float valX = Math.Max(pad, plusX - valW - pad);
            float minusX = Math.Max(pad, valX - btnW - pad);
            float lblX = pad + 1f;
            float lblW = Math.Max(0f, minusX - lblX - pad);

            var lbl = new GTextField
            {
                touchable = false,
                autoSize = AutoSizeType.None,
                singleLine = true,
                verticalAlign = VertAlignType.Middle,
                textFormat = new TextFormat { size = w < 160f ? 10 : 12, color = TextPrimary },
                text = label
            };
            lbl.SetXY(lblX, 0f);
            lbl.SetSize(lblW, h);
            lbl.visible = lblW >= 14f;
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
