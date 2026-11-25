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
            bg.SetSize(w, h);
            bg.DrawRect(w, h, 1, BtnVioletBorder, BtnVioletBg);
            bar.AddChild(bg);

            PopupPagination.ComputeVirtualBannerGeometry(
                w, h,
                out float textX, out float textY, out float textW, out float textH,
                out float btnX, out float btnY, out float btnW, out float btnH,
                out bool stacked);

            string bannerText = w < 120f
                ? ("虚(" + overrideCount + ")")
                : ("虚拟站位生效中 (" + overrideCount + "人)");
            int txtSize = (w < 120f || textH < 14f) ? Math.Max(8, Math.Min(10, (int)Math.Max(8f, textH - 1f))) : 12;

            var txt = new GTextField
            {
                touchable = false,
                autoSize = AutoSizeType.None,
                singleLine = true,
                align = stacked ? AlignType.Center : AlignType.Left,
                verticalAlign = VertAlignType.Middle,
                textFormat = new TextFormat { size = txtSize, bold = true, color = AccentViolet },
                text = bannerText
            };
            txt.SetXY(textX, textY);
            txt.SetSize(textW, textH);
            bar.AddChild(txt);

            int capturedMatchGen = _currentMatchGen;
            string btnLabel = btnW < 28f ? "复" : "还原";
            int btnFont = (btnW < 30f || btnH < 14f) ? Math.Max(8, Math.Min(10, (int)Math.Max(8f, btnH - 1f))) : 11;
            var resetBtn = CreateMiniButton(btnLabel, btnW, btnH, () => VirtualResetAllRequested?.Invoke(capturedMatchGen, layoutGen), fontSize: btnFont);
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
            bg.SetSize(rowW, rowHeight);
            DrawRowBg(bg, rowW, rowHeight, isSelected, hasActed, hovered: false);
            row.AddChild(bg);

            int nameFontSize = rowHeight < 30f ? Math.Max(8, Math.Min(13, (int)Math.Max(8f, rowHeight - 2f))) : 14;
            PopupPagination.ClampChildVertical(rowHeight, (rowHeight - 20f) * 0.5f, Math.Min(22f, rowHeight), out float nameY, out float nameH);
            PopupPagination.ComputeSplitRowGeometry(rowW, 6f, 0.54f, out float nameX, out float nameW, out float metaX, out float metaW);
            Color nameColor = isSelected ? AccentGold : (isActing ? AccentGreen : (hasActed ? TextMuted : TextPrimary));

            string rankTag = st.ActionOrderRank > 0 ? ("#" + st.ActionOrderRank + " ") : string.Empty;
            string prefix = isSelected ? "▶" : (isActing ? "★" : string.Empty);
            string displayName = !string.IsNullOrEmpty(st.DisplayName) ? st.DisplayName : st.Name;

            var nameTf = new GTextField
            {
                touchable = false,
                autoSize = AutoSizeType.None,
                singleLine = true,
                textFormat = new TextFormat { size = nameFontSize, bold = isSelected || isActing, color = nameColor },
                text = prefix + rankTag + displayName
            };
            nameTf.SetXY(nameX, nameY);
            nameTf.SetSize(nameW, nameH);
            row.AddChild(nameTf);

            int metaFontSize = rowHeight < 30f ? Math.Max(8, Math.Min(11, (int)Math.Max(8f, rowHeight - 2f))) : 12;
            PopupPagination.ClampChildVertical(rowHeight, (rowHeight - 18f) * 0.5f, Math.Min(20f, rowHeight), out float metaY, out float metaH);
            Color metaColor = isActing ? AccentGreen : (!st.IsNormalAggressive ? AccentCyan : TextMuted);

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
                    textFormat = new TextFormat { size = metaFontSize, bold = isActing, color = metaColor },
                    text = tilePart + (hpPart.Length > 0 ? " " + hpPart : string.Empty) + statusPart
                };
                metaTf.SetXY(metaX, metaY);
                metaTf.SetSize(metaW, metaH);
                row.AddChild(metaTf);
            }

            long capturedId = st.MonsterId;
            int capturedMatchGen = _currentMatchGen;
            row.onRollOver.Add(() => DrawRowBg(bg, rowW, rowHeight, isSelected, hasActed, hovered: true));
            row.onRollOut.Add(() => DrawRowBg(bg, rowW, rowHeight, isSelected, hasActed, hovered: false));
            row.onClick.Add(() =>
            {
                // Auto-close is owned by the controller after validating and committing the selection.
                MonsterSelected?.Invoke(capturedId, capturedMatchGen);
            });
            return row;
        }

        private static void DrawRowBg(GGraph bg, float w, float h, bool selected, bool acted, bool hovered)
        {
            if (bg == null || bg.isDisposed) return;
            if (selected)
                bg.DrawRect(w, h, 2, RowSelectedBorder, hovered ? RowHoverBg : RowSelectedBg);
            else
                bg.DrawRect(w, h, 1, new Color(0.24f, 0.30f, 0.40f, 0.65f), hovered ? RowHoverBg : (acted ? RowActedBg : RowNormalBg));
        }
    }
}
