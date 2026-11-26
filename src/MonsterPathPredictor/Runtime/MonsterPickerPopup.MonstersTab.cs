using System;
using System.Collections.Generic;
using FairyGUI;
using UnityEngine;
using MonsterPathPredictor.Core;

namespace MonsterPathPredictor.Runtime
{
    internal sealed partial class MonsterPickerPopup
    {
        private float BuildMonstersTab(
            IReadOnlyList<LiveMonsterInfo> visible,
            VirtualPlayerSandbox sandbox,
            long selectedMonsterId,
            CompactLayout layout,
            float rowW,
            float padX,
            float contentY,
            int rowsPerPage)
        {
            float y = 0f;
            bool hasVirtual = sandbox != null && sandbox.HasOverrides;
            float bannerH = rowW < 120f ? Math.Min(layout.MaxContentHeight, 28f) : (layout.RowH < 28f ? 22f : 26f);
            // Always reserve space for at least one selectable monster row FIRST before showing the virtual banner.
            float minForBannerAndOneRow = bannerH + layout.RowGap + (visible.Count > 0 ? layout.RowH : 22f);
            if (hasVirtual && layout.MaxContentHeight >= minForBannerAndOneRow)
            {
                var banner = BuildVirtualActiveBanner(sandbox.OverrideCount, rowW, bannerH, sandbox.LayoutGeneration);
                banner.SetXY(0f, y);
                _contentHost.AddChild(banner);
                y += bannerH + layout.RowGap;
            }

            float availListH = Math.Max(0f, layout.MaxContentHeight - y);
            if (visible.Count == 0)
            {
                float emptyH = Math.Min(42f, availListH);
                PopupPagination.ClampChildVertical(Math.Max(1f, emptyH), (emptyH - 18f) * 0.5f, 20f, out float relY, out float lblH);
                _emptyText.visible = emptyH >= 8f;
                _emptyText.SetXY(padX, contentY + y + relY);
                _emptyText.SetSize(rowW, lblH);
                return y + emptyH;
            }

            _emptyText.visible = false;
            int start = _monsterPageIndex * rowsPerPage;
            int count = Math.Min(rowsPerPage, visible.Count - start);
            float rowStep = layout.RowH + layout.RowGap;
            bool showRows = layout.RowH >= 10f && availListH >= layout.RowH;
            if (!showRows) return y;

            int fitRows = Math.Max(1, Math.Min(count, (int)((availListH + layout.RowGap) / rowStep)));
            for (int i = 0; i < fitRows; i++)
            {
                var row = BuildMonsterRow(visible[start + i], selectedMonsterId, layout.RowH, rowW);
                row.SetXY(0f, y + i * rowStep);
                _contentHost.AddChild(row);
            }
            return Math.Min(layout.MaxContentHeight, y + fitRows * rowStep + layout.RowGap);
        }

        private GComponent BuildVirtualActiveBanner(int overrideCount, float w, float h, int layoutGen)
        {
            var bar = new GComponent { touchable = true, opaque = true };
            bar.SetSize(w, h);

            var bg = new GGraph { touchable = false };
            DrawRound(bg, w, h, RadiusCard, VioletSoft, WithAlpha(Violet, 0.50f));
            bar.AddChild(bg);

            PopupPagination.ComputeVirtualBannerGeometry(
                w, h,
                out float textX, out float textY, out float textW, out float textH,
                out float btnX, out float btnY, out float btnW, out float btnH,
                out bool stacked);

            string bannerText = w < 120f
                ? ("虚(" + overrideCount + ")")
                : ("虚拟站位生效中 · " + overrideCount + " 人");
            int txtSize = (w < 120f || textH < 14f) ? Math.Max(8, Math.Min(10, (int)Math.Max(8f, textH - 1f))) : 12;

            var txt = new GTextField
            {
                touchable = false,
                autoSize = AutoSizeType.None,
                singleLine = true,
                align = stacked ? AlignType.Center : AlignType.Left,
                verticalAlign = VertAlignType.Middle,
                textFormat = new TextFormat { size = txtSize, bold = true, color = Violet },
                text = bannerText
            };
            txt.SetXY(textX, textY);
            txt.SetSize(textW, textH);
            bar.AddChild(txt);

            int capturedMatchGen = _currentMatchGen;
            string btnLabel = btnW < 28f ? "复" : "还原";
            int btnFont = (btnW < 30f || btnH < 14f) ? Math.Max(8, Math.Min(10, (int)Math.Max(8f, btnH - 1f))) : 11;
            var resetBtn = CreateStyledButton(
                btnLabel, btnW, btnH,
                VioletSoft, WithAlpha(Violet, 0.60f), Violet,
                () => VirtualResetAllRequested?.Invoke(capturedMatchGen, layoutGen),
                fontSize: btnFont,
                radius: RadiusChip);
            resetBtn.SetXY(btnX, btnY);
            bar.AddChild(resetBtn);
            return bar;
        }

        private GComponent BuildMonsterRow(LiveMonsterInfo info, long selectedMonsterId, float rowHeight, float rowW)
        {
            var st = info.State;
            bool isSelected = st.MonsterId == selectedMonsterId;
            bool isActing = st.IsCurrentlyActing;
            bool hasActed = st.HasActedThisRound;

            var row = new GComponent { touchable = true, opaque = true };
            row.SetSize(rowW, rowHeight);

            var bg = new GGraph { touchable = false };
            DrawRowBg(bg, rowW, rowHeight, isSelected, hasActed, isActing, hovered: false);
            row.AddChild(bg);

            string displayName = !string.IsNullOrEmpty(st.DisplayName) ? st.DisplayName : st.Name;

            if (rowHeight >= 38f && rowW >= 240f)
            {
                if (isSelected || isActing)
                {
                    var accentBar = new GGraph { touchable = false };
                    accentBar.SetXY(3f, 8f);
                    DrawRound(accentBar, 3f, rowHeight - 16f, 1.5f, isSelected ? Gold : Green, Color.clear);
                    row.AddChild(accentBar);
                }

                float badgeD = Math.Min(24f, rowHeight - 16f);
                float badgeY = (rowHeight - badgeD) * 0.5f;
                Color badgeFill = isActing
                    ? GreenSoft
                    : (isSelected ? GoldSoft : (hasActed ? new Color(1f, 1f, 1f, 0.04f) : new Color(1f, 1f, 1f, 0.07f)));
                Color badgeTextCol = isActing
                    ? Green
                    : (isSelected ? Gold : (hasActed ? TextDim : TextPrimary));

                var badgeCircle = new GGraph { touchable = false };
                badgeCircle.SetXY(12f, badgeY);
                DrawCircle(badgeCircle, badgeD, badgeFill);
                row.AddChild(badgeCircle);

                var badgeTf = new GTextField
                {
                    touchable = false,
                    autoSize = AutoSizeType.None,
                    singleLine = true,
                    align = AlignType.Center,
                    verticalAlign = VertAlignType.Middle,
                    textFormat = new TextFormat { size = 12, bold = true, color = badgeTextCol },
                    text = st.ActionOrderRank > 0 ? st.ActionOrderRank.ToString() : "·"
                };
                badgeTf.SetXY(12f, badgeY);
                badgeTf.SetSize(badgeD, badgeD);
                row.AddChild(badgeTf);

                float textX = 12f + badgeD + 10f;

                string chipText = null;
                Color chipTextCol = TextSecondary;
                Color chipFill = Color.clear;
                if (isActing)
                {
                    chipText = "行动中";
                    chipTextCol = Green;
                    chipFill = GreenSoft;
                }
                else if (hasActed)
                {
                    chipText = "已行动";
                    chipTextCol = TextDim;
                    chipFill = new Color(1f, 1f, 1f, 0.05f);
                }
                else if (!st.IsNormalAggressive)
                {
                    chipText = "非主动";
                    chipTextCol = Accent;
                    chipFill = AccentSoft;
                }

                bool hasChip = chipText != null;
                float chipW = 50f;
                float chipX = rowW - 8f - chipW;
                if (hasChip)
                {
                    var chipBg = new GGraph { touchable = false };
                    chipBg.SetXY(chipX, 5f);
                    DrawRound(chipBg, chipW, 18f, RadiusChip, chipFill, Color.clear);
                    row.AddChild(chipBg);

                    var chipTf = new GTextField
                    {
                        touchable = false,
                        autoSize = AutoSizeType.None,
                        singleLine = true,
                        align = AlignType.Center,
                        verticalAlign = VertAlignType.Middle,
                        textFormat = new TextFormat { size = 11, bold = true, color = chipTextCol },
                        text = chipText
                    };
                    chipTf.SetXY(chipX, 5f);
                    chipTf.SetSize(chipW, 18f);
                    row.AddChild(chipTf);
                }

                float nameW = Math.Max(20f, (hasChip ? (chipX - 6f) : (rowW - 10f)) - textX);
                var nameTf = new GTextField
                {
                    touchable = false,
                    autoSize = AutoSizeType.None,
                    singleLine = true,
                    verticalAlign = VertAlignType.Middle,
                    textFormat = new TextFormat
                    {
                        size = 14,
                        bold = isSelected || isActing,
                        color = (hasActed && !isSelected) ? TextDim : TextPrimary
                    },
                    text = displayName
                };
                nameTf.SetXY(textX, 5f);
                nameTf.SetSize(nameW, 20f);
                row.AddChild(nameTf);

                float line2Y = rowHeight - 19f;
                float hpLabelRight = rowW - 10f;

                if (st.MaxHp > 0)
                {
                    float barW = 72f;
                    float barX = rowW - 82f;
                    float barY = rowHeight - 12f;
                    var hpTrack = new GGraph { touchable = false };
                    hpTrack.SetXY(barX, barY);
                    DrawRound(hpTrack, barW, 4f, 2f, new Color(1f, 1f, 1f, 0.08f), Color.clear);
                    row.AddChild(hpTrack);

                    if (st.HasHp)
                    {
                        float ratio = Math.Max(0f, Math.Min(1f, (float)st.Hp / st.MaxHp));
                        if (ratio > 0f)
                        {
                            Color hpCol = ratio > 0.5f ? Green : (ratio > 0.25f ? Gold : Danger);
                            var hpFill = new GGraph { touchable = false };
                            hpFill.SetXY(barX, barY);
                            DrawRound(hpFill, Math.Max(2f, barW * ratio), 4f, 2f, hpCol, Color.clear);
                            row.AddChild(hpFill);
                        }
                    }

                    string hpText = st.HasHp ? ("HP " + st.Hp + "/" + st.MaxHp) : ("HP ?/" + st.MaxHp);
                    float hpTextX = barX - 6f - 90f;
                    hpLabelRight = barX - 6f;
                    var hpTf = new GTextField
                    {
                        touchable = false,
                        autoSize = AutoSizeType.None,
                        singleLine = true,
                        align = AlignType.Right,
                        verticalAlign = VertAlignType.Middle,
                        textFormat = new TextFormat { size = 11, color = TextSecondary },
                        text = hpText
                    };
                    hpTf.SetXY(hpTextX, line2Y);
                    hpTf.SetSize(90f, 16f);
                    row.AddChild(hpTf);
                    hpLabelRight = hpTextX;
                }
                else if (st.HasHp)
                {
                    float hpTextX = rowW - 10f - 90f;
                    var hpTf = new GTextField
                    {
                        touchable = false,
                        autoSize = AutoSizeType.None,
                        singleLine = true,
                        align = AlignType.Right,
                        verticalAlign = VertAlignType.Middle,
                        textFormat = new TextFormat { size = 11, color = TextSecondary },
                        text = "HP " + st.Hp
                    };
                    hpTf.SetXY(hpTextX, line2Y);
                    hpTf.SetSize(90f, 16f);
                    row.AddChild(hpTf);
                    hpLabelRight = hpTextX;
                }

                float tileW = Math.Max(20f, hpLabelRight - textX - 4f);
                var tileTf = new GTextField
                {
                    touchable = false,
                    autoSize = AutoSizeType.None,
                    singleLine = true,
                    verticalAlign = VertAlignType.Middle,
                    textFormat = new TextFormat { size = 11, color = TextSecondary },
                    text = "格 #" + st.CurrentLandId
                };
                tileTf.SetXY(textX, line2Y);
                tileTf.SetSize(tileW, 16f);
                row.AddChild(tileTf);
            }
            else
            {
                int nameFontSize = rowHeight < 30f ? Math.Max(8, Math.Min(13, (int)Math.Max(8f, rowHeight - 2f))) : 14;
                PopupPagination.ClampChildVertical(rowHeight, (rowHeight - 20f) * 0.5f, Math.Min(22f, rowHeight), out float nameY, out float nameH);
                PopupPagination.ComputeSplitRowGeometry(rowW, 6f, 0.54f, out float nameX, out float nameW, out float metaX, out float metaW);
                Color nameColor = isSelected ? Gold : (isActing ? Green : (hasActed ? TextDim : TextPrimary));

                string rankTag = st.ActionOrderRank > 0 ? ("#" + st.ActionOrderRank + " ") : string.Empty;
                string prefix = isSelected ? "▶" : (isActing ? "★" : string.Empty);

                var nameTf = new GTextField
                {
                    touchable = false,
                    autoSize = AutoSizeType.None,
                    singleLine = true,
                    verticalAlign = VertAlignType.Middle,
                    textFormat = new TextFormat { size = nameFontSize, bold = isSelected || isActing, color = nameColor },
                    text = prefix + rankTag + displayName
                };
                nameTf.SetXY(nameX, nameY);
                nameTf.SetSize(nameW, nameH);
                row.AddChild(nameTf);

                int metaFontSize = rowHeight < 30f ? Math.Max(8, Math.Min(11, (int)Math.Max(8f, rowHeight - 2f))) : 12;
                PopupPagination.ClampChildVertical(rowHeight, (rowHeight - 18f) * 0.5f, Math.Min(20f, rowHeight), out float metaY, out float metaH);
                Color metaColor = isActing ? Green : (!st.IsNormalAggressive ? Accent : TextSecondary);

                string hpPart = st.HasHp
                    ? (st.MaxHp > 0 ? ("HP " + st.Hp + "/" + st.MaxHp) : ("HP " + st.Hp))
                    : (st.MaxHp > 0 ? ("HP ?/" + st.MaxHp) : string.Empty);
                string tilePart = "格" + st.CurrentLandId;
                string statusPart = isActing
                    ? " 行动中"
                    : (hasActed ? " 已动" : (!st.IsNormalAggressive ? " 非主动" : string.Empty));

                if (metaW >= 14f)
                {
                    var metaTf = new GTextField
                    {
                        touchable = false,
                        autoSize = AutoSizeType.None,
                        singleLine = true,
                        align = AlignType.Right,
                        verticalAlign = VertAlignType.Middle,
                        textFormat = new TextFormat { size = metaFontSize, bold = isActing, color = metaColor },
                        text = tilePart + (hpPart.Length > 0 ? " " + hpPart : string.Empty) + statusPart
                    };
                    metaTf.SetXY(metaX, metaY);
                    metaTf.SetSize(metaW, metaH);
                    row.AddChild(metaTf);
                }
            }

            long capturedId = st.MonsterId;
            int capturedMatchGen = _currentMatchGen;
            row.onRollOver.Add(() => DrawRowBg(bg, rowW, rowHeight, isSelected, hasActed, isActing, hovered: true));
            row.onRollOut.Add(() => DrawRowBg(bg, rowW, rowHeight, isSelected, hasActed, isActing, hovered: false));
            row.onClick.Add(() =>
            {
                // Auto-close is owned by the controller after validating and committing the selection.
                MonsterSelected?.Invoke(capturedId, capturedMatchGen);
            });
            return row;
        }

        private static void DrawRowBg(GGraph bg, float w, float h, bool selected, bool acted, bool acting, bool hovered)
        {
            if (bg == null || bg.isDisposed) return;
            Color fill;
            Color border;
            if (selected)
            {
                fill = hovered ? WithAlpha(Gold, 0.20f) : GoldSoft;
                border = WithAlpha(Gold, 0.75f);
            }
            else if (acting)
            {
                fill = hovered ? Surface2Hover : Surface2;
                border = WithAlpha(Green, 0.45f);
            }
            else if (acted)
            {
                fill = hovered ? Surface2Hover : RowActedBg;
                border = Hairline;
            }
            else
            {
                fill = hovered ? Surface2Hover : Surface2;
                border = Hairline;
            }
            DrawRound(bg, w, h, RadiusCard, fill, border);
        }
    }
}
