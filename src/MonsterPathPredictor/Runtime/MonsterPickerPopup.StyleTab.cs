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

            void AddSectionHeader(string title, float h)
            {
                var secTf = new GTextField
                {
                    touchable = false,
                    autoSize = AutoSizeType.Both,
                    singleLine = true,
                    textFormat = new TextFormat { size = 11, bold = true, letterSpacing = 1, color = TextSecondary },
                    text = title
                };
                float secY = y + Math.Max(0f, (h - secTf.height) * 0.5f);
                secTf.SetXY(2f, secY);
                _contentHost.AddChild(secTf);

                float lineX = 2f + Math.Max(secTf.textWidth, secTf.width) + 8f;
                float lineW = contentW - lineX;
                if (lineW >= 12f)
                {
                    var line = new GGraph { touchable = false };
                    line.SetXY(lineX, (float)Math.Round(y + h * 0.5f));
                    line.SetSize(lineW, 1f);
                    line.DrawRect(lineW, 1f, 0, Color.clear, Hairline);
                    _contentHost.AddChild(line);
                }
            }

            void AddStepper(string label, string shortLabel, string valueText, string shortValueText, float h, Action onMinus, Action onPlus)
            {
                var row = BuildStyleStepperRow(label, shortLabel, valueText, shortValueText, contentW, h, onMinus, onPlus);
                row.SetXY(0f, y);
                _contentHost.AddChild(row);
            }

            void AddColorRoleRow(int startRoleIndex, int count, float chipH)
            {
                int safeCount = Math.Max(1, Math.Min(count, 6 - startRoleIndex));
                float cGap = contentW < 100f ? 2f : 3f;
                float cW = (contentW - cGap * (safeCount - 1)) / safeCount;
                float dotSize = Math.Max(3f, Math.Min(8f, chipH - 4f));
                bool showDot = cW >= 28f && chipH >= 8f;
                int chipFont = (cW < 54f || chipH < 14f) ? Math.Max(8, Math.Min(10, (int)Math.Max(8f, chipH - 2f))) : 11;
                for (int c = 0; c < safeCount; c++)
                {
                    int roleIdx = startRoleIndex + c;
                    if (roleIdx >= 6) break;
                    var role = (RouteColorRole)roleIdx;
                    bool sel = role == activeRole;
                    var roleCol = StyleColor.GetRoleColor(settings, role);

                    string roleTxt = showDot ? ("  " + StyleColor.GetRoleLabel(role)) : StyleColor.GetRoleLabel(role);
                    var chip = CreateStyledButton(
                        roleTxt,
                        cW,
                        chipH,
                        sel ? new Color(1f, 1f, 1f, 0.08f) : new Color(1f, 1f, 1f, 0.03f),
                        sel ? WithAlpha(Accent, 0.80f) : Hairline,
                        sel ? TextPrimary : TextSecondary,
                        () => { _selectedColorRole = role; _popupLocalRevision++; _lastSignature = string.Empty; },
                        fontSize: chipFont,
                        radius: RadiusButton,
                        hoverFill: sel ? new Color(1f, 1f, 1f, 0.11f) : new Color(1f, 1f, 1f, 0.06f),
                        hoverBorder: sel ? Accent : HairlineStrong,
                        hoverText: TextPrimary);

                    if (showDot)
                    {
                        PopupPagination.ClampChildVertical(chipH, (chipH - dotSize) * 0.5f, dotSize, out float dotY, out float dotH);
                        var dot = new GGraph { touchable = false };
                        dot.SetXY(cW < 64f ? 4f : 8f, dotY);
                        DrawCircle(dot, dotH, new Color(roleCol.Rf, roleCol.Gf, roleCol.Bf, 1f));
                        chip.AddChild(dot);
                    }

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
                            textFormat = new TextFormat { size = 11, bold = true, color = Danger },
                            text = "⚠ 样式写入 config.json 失败（仅本局临时生效）"
                        };
                        warn.SetXY(2f, y);
                        warn.SetSize(contentW - 4f, item.Height);
                        _contentHost.AddChild(warn);
                        break;
                    }
                    case PopupPagination.StyleRowKind.SectionLine:
                        AddSectionHeader("路线线条", item.Height);
                        break;
                    case PopupPagination.StyleRowKind.SectionDisplay:
                        AddSectionHeader("标签与显示", item.Height);
                        break;
                    case PopupPagination.StyleRowKind.LineWidth:
                        AddStepper(
                            "线条宽度", "线宽",
                            settings.LineWidth.ToString("0.0", CultureInfo.InvariantCulture) + " px",
                            settings.LineWidth.ToString("0.0", CultureInfo.InvariantCulture),
                            item.Height,
                            () => StyleMutationRequested?.Invoke(s => s.LineWidth = (float)Math.Round(s.LineWidth - 0.5f, 1)),
                            () => StyleMutationRequested?.Invoke(s => s.LineWidth = (float)Math.Round(s.LineWidth + 0.5f, 1)));
                        break;
                    case PopupPagination.StyleRowKind.DashSpeed:
                        AddStepper(
                            "行进速度", "速度",
                            settings.DashSpeed <= 0.01f ? "静态 (0)" : ((int)Math.Round(settings.DashSpeed) + " px/s"),
                            settings.DashSpeed <= 0.01f ? "0" : ((int)Math.Round(settings.DashSpeed)).ToString(CultureInfo.InvariantCulture),
                            item.Height,
                            () => StyleMutationRequested?.Invoke(s => s.DashSpeed = Math.Max(0f, (float)Math.Round(s.DashSpeed - 6f))),
                            () => StyleMutationRequested?.Invoke(s => s.DashSpeed = Math.Min(150f, (float)Math.Round(s.DashSpeed + 6f))));
                        break;
                    case PopupPagination.StyleRowKind.DashLength:
                        AddStepper(
                            "虚线长度", "线长",
                            ((int)Math.Round(settings.DashLength)) + " px",
                            ((int)Math.Round(settings.DashLength)).ToString(CultureInfo.InvariantCulture),
                            item.Height,
                            () => StyleMutationRequested?.Invoke(s => s.DashLength = (float)Math.Round(s.DashLength - 2f)),
                            () => StyleMutationRequested?.Invoke(s => s.DashLength = (float)Math.Round(s.DashLength + 2f)));
                        break;
                    case PopupPagination.StyleRowKind.GapLength:
                        AddStepper(
                            "虚线间距", "间距",
                            ((int)Math.Round(settings.GapLength)) + " px",
                            ((int)Math.Round(settings.GapLength)).ToString(CultureInfo.InvariantCulture),
                            item.Height,
                            () => StyleMutationRequested?.Invoke(s => s.GapLength = (float)Math.Round(s.GapLength - 2f)),
                            () => StyleMutationRequested?.Invoke(s => s.GapLength = (float)Math.Round(s.GapLength + 2f)));
                        break;
                    case PopupPagination.StyleRowKind.FontSize:
                        AddStepper(
                            "标签字号", "字号",
                            settings.FontSize + " pt",
                            settings.FontSize.ToString(CultureInfo.InvariantCulture),
                            item.Height,
                            () => StyleMutationRequested?.Invoke(s => s.FontSize -= 2),
                            () => StyleMutationRequested?.Invoke(s => s.FontSize += 2));
                        break;
                    case PopupPagination.StyleRowKind.HeightOffset:
                        AddStepper(
                            "悬浮高度", "高度",
                            settings.HeightOffset.ToString("0.00", CultureInfo.InvariantCulture),
                            settings.HeightOffset.ToString("0.0", CultureInfo.InvariantCulture),
                            item.Height,
                            () => StyleMutationRequested?.Invoke(s => s.HeightOffset = (float)Math.Round(s.HeightOffset - 0.1f, 2)),
                            () => StyleMutationRequested?.Invoke(s => s.HeightOffset = (float)Math.Round(s.HeightOffset + 0.1f, 2)));
                        break;
                    case PopupPagination.StyleRowKind.Toggles:
                    {
                        int tStart = item.Count > 0 ? item.StartIndex : 0;
                        int tCount = item.Count > 0 ? item.Count : 4;
                        float tGap = contentW < 100f ? 2f : 3f;
                        float tW = (contentW - tGap * (tCount - 1)) / tCount;
                        int tFont = (tW < 54f || item.Height < 14f) ? Math.Max(8, Math.Min(10, (int)Math.Max(8f, item.Height - 2f))) : 11;
                        for (int c = 0; c < tCount; c++)
                        {
                            int tIdx = tStart + c;
                            float tx = c * (tW + tGap);
                            if (tIdx == 0)
                            {
                                string pcLabel = tW < 52f
                                    ? (settings.PostCombatMode == PostCombatMode.StopAtFirstCombat ? "截断" : (settings.PostCombatMode == PostCombatMode.RedBlueAfterCombat ? "红蓝" : "续行"))
                                    : (settings.PostCombatMode == PostCombatMode.StopAtFirstCombat ? "战后 · 截断" : (settings.PostCombatMode == PostCombatMode.RedBlueAfterCombat ? "战后 · 红蓝" : "战后 · 续行"));
                                var pcBtn = CreateStyledButton(
                                    pcLabel,
                                    tW,
                                    item.Height,
                                    new Color(1f, 1f, 1f, 0.04f),
                                    WithAlpha(Accent, 0.40f),
                                    Accent,
                                    () =>
                                    {
                                        StyleMutationRequested?.Invoke(s =>
                                        {
                                            s.PostCombatMode = (PostCombatMode)(((int)s.PostCombatMode + 1) % 3);
                                        });
                                    },
                                    fontSize: tFont,
                                    radius: RadiusButton,
                                    hoverFill: AccentSoft,
                                    hoverBorder: WithAlpha(Accent, 0.70f),
                                    hoverText: Accent);
                                pcBtn.SetXY(tx, y);
                                _contentHost.AddChild(pcBtn);
                            }
                            else if (tIdx == 1)
                                AddToggleChip("步数", settings.ShowStepNumbers, tx, y, tW, item.Height, tFont, () => StyleMutationRequested?.Invoke(s => s.ShowStepNumbers = !s.ShowStepNumbers));
                            else if (tIdx == 2)
                                AddToggleChip("依据", settings.ShowReasonTag, tx, y, tW, item.Height, tFont, () => StyleMutationRequested?.Invoke(s => s.ShowReasonTag = !s.ShowReasonTag));
                            else if (tIdx == 3)
                                AddToggleChip(tW < 46f ? "满%" : "100%", settings.ShowDeterministicPercent, tx, y, tW, item.Height, tFont, () => StyleMutationRequested?.Invoke(s => s.ShowDeterministicPercent = !s.ShowDeterministicPercent));
                        }
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
                            textFormat = new TextFormat { size = 12, bold = true, color = TextSecondary },
                            text = "配色 · " + StyleColor.GetRoleLabel(activeRole) + "  " + activeRgba.ToHex()
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
                            DrawRound(swBackDark, 18f, swH, 3f, 0f, 3f, 0f, new Color(0.10f, 0.12f, 0.16f, 1f), Color.clear);
                            _contentHost.AddChild(swBackDark);

                            var swBackLight = new GGraph { touchable = false };
                            swBackLight.SetXY(contentW - 20f, swY);
                            DrawRound(swBackLight, 18f, swH, 0f, 3f, 0f, 3f, new Color(0.75f, 0.78f, 0.84f, 1f), Color.clear);
                            _contentHost.AddChild(swBackLight);

                            var previewSwatch = new GGraph { touchable = false };
                            previewSwatch.SetXY(contentW - 38f, swY);
                            DrawRound(previewSwatch, 36f, swH, 3f, new Color(activeRgba.Rf, activeRgba.Gf, activeRgba.Bf, activeRgba.Af), HairlineStrong);
                            _contentHost.AddChild(previewSwatch);
                        }
                        break;
                    }
                    case PopupPagination.StyleRowKind.ColorRolesRow1:
                        AddColorRoleRow(item.Count > 0 ? item.StartIndex : 0, item.Count > 0 ? item.Count : 3, item.Height);
                        break;
                    case PopupPagination.StyleRowKind.ColorRolesRow2:
                        AddColorRoleRow(item.Count > 0 ? item.StartIndex : 3, item.Count > 0 ? item.Count : 3, item.Height);
                        break;
                    case PopupPagination.StyleRowKind.Presets:
                        AddPresetSwatchRow(item.Count > 0 ? item.StartIndex : 0, item.Count > 0 ? item.Count : StyleColor.Presets.Length, y, contentW, item.Height, activeRole, activeRgba);
                        break;
                    case PopupPagination.StyleRowKind.PresetsRow1:
                        AddPresetSwatchRow(item.Count > 0 ? item.StartIndex : 0, item.Count > 0 ? item.Count : Math.Min(4, StyleColor.Presets.Length), y, contentW, item.Height, activeRole, activeRgba);
                        break;
                    case PopupPagination.StyleRowKind.PresetsRow2:
                        AddPresetSwatchRow(item.Count > 0 ? item.StartIndex : 4, item.Count > 0 ? item.Count : Math.Max(0, StyleColor.Presets.Length - 4), y, contentW, item.Height, activeRole, activeRgba);
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
                        var resetBtn = CreateDangerButton("恢复默认样式", contentW, item.Height, () => StyleResetRequested?.Invoke(), fontSize: 12);
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
                Color baseFill = new Color(pc.Rf, pc.Gf, pc.Bf, 0.95f);
                var sw = CreateStyledButton(
                    sW < 26f ? string.Empty : preset.Name,
                    sW,
                    swatchH,
                    baseFill,
                    matches ? Color.white : new Color(0f, 0f, 0f, 0.25f),
                    (pc.R + pc.G + pc.B > 500) ? Color.black : Color.white,
                    () =>
                    {
                        var capturedHex = preset.Hex;
                        var capturedRole = activeRole;
                        StyleMutationRequested?.Invoke(s => StyleColor.SetRoleHex(s, capturedRole, capturedHex));
                    },
                    fontSize: 10,
                    radius: RadiusChip,
                    hoverFill: new Color(Math.Min(1f, pc.Rf + 0.08f), Math.Min(1f, pc.Gf + 0.08f), Math.Min(1f, pc.Bf + 0.08f), 1f),
                    hoverBorder: Color.white);
                sw.SetXY(i * (sW + sGap), y);
                _contentHost.AddChild(sw);
            }
        }

        private void AddToggleChip(string label, bool isOn, float x, float y, float w, float h, int fontSize, Action onClick)
        {
            string display = w < 44f
                ? (label + (isOn ? "✓" : "×"))
                : ((isOn ? "● " : "○ ") + label);
            var btn = CreateStyledButton(
                display,
                w,
                h,
                isOn ? AccentSoft : new Color(1f, 1f, 1f, 0.03f),
                isOn ? WithAlpha(Accent, 0.60f) : Hairline,
                isOn ? Accent : TextSecondary,
                onClick,
                fontSize: fontSize,
                radius: RadiusButton,
                hoverFill: isOn ? Lighten(AccentSoft, 0.05f) : new Color(1f, 1f, 1f, 0.06f),
                hoverBorder: isOn ? Accent : HairlineStrong,
                hoverText: isOn ? Accent : TextPrimary);
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
            DrawRound(bg, w, h, RadiusButton, Surface2, Color.clear);
            host.AddChild(bg);

            PopupPagination.ComputeChannelGeometry(
                w,
                out float tagX, out float tagW,
                out float minusX, out float btnW,
                out float valX, out float valW,
                out float plusX);
            PopupPagination.ClampChildVertical(h, 1f, Math.Max(1f, h - 2f), out float btnY, out float btnH);
            int smallFont = (w < 100f || h < 14f) ? Math.Max(8, Math.Min(10, (int)Math.Max(8f, h - 2f))) : 11;

            var tagTf = new GTextField
            {
                touchable = false,
                autoSize = AutoSizeType.None,
                singleLine = true,
                verticalAlign = VertAlignType.Middle,
                textFormat = new TextFormat { size = smallFont, bold = true, color = tagColor },
                text = tag
            };
            tagTf.SetXY(tagX, 0f);
            tagTf.SetSize(tagW, h);
            host.AddChild(tagTf);

            var minus = CreateGhostButton("－", btnW, btnH, () =>
            {
                StyleMutationRequested?.Invoke(s =>
                {
                    var cur = StyleColor.GetRoleColor(s, role);
                    var next = StyleColor.AdjustChannel(cur, channelIndex, -step, minAlpha);
                    StyleColor.SetRoleHex(s, role, next.ToHex());
                });
            }, fontSize: smallFont);
            minus.SetXY(minusX, btnY);
            host.AddChild(minus);

            var plus = CreateGhostButton("＋", btnW, btnH, () =>
            {
                StyleMutationRequested?.Invoke(s =>
                {
                    var cur = StyleColor.GetRoleColor(s, role);
                    var next = StyleColor.AdjustChannel(cur, channelIndex, step, minAlpha);
                    StyleColor.SetRoleHex(s, role, next.ToHex());
                });
            }, fontSize: smallFont);
            plus.SetXY(plusX, btnY);
            host.AddChild(plus);

            if (valW >= 6f)
            {
                var valTf = new GTextField
                {
                    touchable = false,
                    autoSize = AutoSizeType.None,
                    singleLine = true,
                    align = AlignType.Center,
                    verticalAlign = VertAlignType.Middle,
                    textFormat = new TextFormat { size = smallFont, color = TextPrimary },
                    text = valText
                };
                valTf.SetXY(valX, 0f);
                valTf.SetSize(valW, h);
                host.AddChild(valTf);
            }

            _contentHost.AddChild(host);
        }

        private static GComponent BuildStyleStepperRow(
            string label,
            string shortLabel,
            string valueText,
            string shortValueText,
            float w,
            float h,
            Action onMinus,
            Action onPlus)
        {
            var row = new GComponent { touchable = true };
            row.SetSize(w, h);

            var bg = new GGraph { touchable = false };
            DrawRound(bg, w, h, RadiusCard, Surface2, Color.clear);
            row.AddChild(bg);

            var geo = PopupPagination.ComputeStepperGeometry(w, h);
            int lblFont = geo.Stacked
                ? Math.Max(8, Math.Min(10, (int)Math.Max(8f, geo.LabelH - 1f)))
                : (w < 190f ? 10 : 12);
            int valFont = (geo.ValW < 34f || geo.ValH < 14f)
                ? Math.Max(8, Math.Min(10, (int)Math.Max(8f, geo.ValH - 1f)))
                : 12;
            int btnFont = (geo.BtnW < 16f || geo.BtnH < 14f)
                ? Math.Max(8, Math.Min(11, (int)Math.Max(8f, geo.BtnH - 1f)))
                : 12;

            if (geo.ShowLabel && geo.LabelW >= 8f && geo.LabelH >= 6f)
            {
                var lbl = new GTextField
                {
                    touchable = false,
                    autoSize = AutoSizeType.None,
                    singleLine = true,
                    align = geo.Stacked ? AlignType.Center : AlignType.Left,
                    verticalAlign = VertAlignType.Middle,
                    textFormat = new TextFormat { size = lblFont, color = TextSecondary },
                    text = geo.LabelW < 56f ? shortLabel : label
                };
                lbl.SetXY(geo.LabelX, geo.LabelY);
                lbl.SetSize(geo.LabelW, geo.LabelH);
                row.AddChild(lbl);
            }

            float btnRadius = Math.Min(5f, geo.BtnH * 0.5f);
            var minus = CreateGhostButton("－", geo.BtnW, geo.BtnH, onMinus, fontSize: btnFont, radius: btnRadius);
            minus.SetXY(geo.MinusX, geo.MinusY);
            row.AddChild(minus);

            if (geo.ValW >= 16f && geo.ValH >= 12f)
            {
                var valWell = new GGraph { touchable = false };
                valWell.SetXY(geo.ValX, geo.ValY);
                DrawRound(valWell, geo.ValW, geo.ValH, RadiusChip, Track, Hairline);
                row.AddChild(valWell);
            }

            string displayVal;
            if (!geo.ShowLabel)
            {
                // Extreme compact row (<18px tall): prefix 1-char setting identifier onto value so all 6 settings remain distinguishable!
                string tagPrefix = !string.IsNullOrEmpty(shortLabel) ? shortLabel.Substring(0, 1) : string.Empty;
                displayVal = tagPrefix + shortValueText;
            }
            else
            {
                displayVal = geo.ValW < 34f ? shortValueText : valueText;
            }
            var val = new GTextField
            {
                touchable = false,
                autoSize = AutoSizeType.None,
                singleLine = true,
                align = AlignType.Center,
                verticalAlign = VertAlignType.Middle,
                textFormat = new TextFormat { size = valFont, bold = true, color = Accent },
                text = displayVal
            };
            val.SetXY(geo.ValX, geo.ValY);
            val.SetSize(geo.ValW, geo.ValH);
            row.AddChild(val);

            var plus = CreateGhostButton("＋", geo.BtnW, geo.BtnH, onPlus, fontSize: btnFont, radius: btnRadius);
            plus.SetXY(geo.PlusX, geo.MinusY);
            row.AddChild(plus);

            return row;
        }
    }
}
