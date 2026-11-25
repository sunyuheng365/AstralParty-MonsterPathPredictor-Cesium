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
            if (hasVirtual && layout.MaxContentHeight >= 52f)
            {
                float bannerH = layout.RowH < 28f ? 22f : 26f;
                var banner = BuildVirtualActiveBanner(sandbox.OverrideCount, rowW, bannerH);
                banner.SetXY(0f, y);
                _contentHost.AddChild(banner);
                y += bannerH + layout.RowGap;
            }

            float availListH = Math.Max(0f, layout.MaxContentHeight - y);
            if (visible.Count == 0)
            {
                float emptyH = Math.Min(42f, availListH);
                _emptyText.visible = emptyH >= 18f;
                _emptyText.SetXY(padX, contentY + y + Math.Max(2f, (emptyH - 18f) * 0.5f));
                _emptyText.SetSize(rowW, 20f);
                return y + emptyH;
            }

            _emptyText.visible = false;
            int start = _pageIndex * rowsPerPage;
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

        private GComponent BuildVirtualActiveBanner(int overrideCount, float w, float h)
        {
            var bar = new GComponent { touchable = true, opaque = true };
            bar.SetSize(w, h);

            var bg = new GGraph { touchable = false };
            bg.SetSize(w, h);
            bg.DrawRect(w, h, 1, BtnVioletBorder, BtnVioletBg);
            bar.AddChild(bg);

            float resetW = Math.Min(54f, w * 0.3f);
            var txt = new GTextField
            {
                touchable = false,
                autoSize = AutoSizeType.None,
                singleLine = true,
                verticalAlign = VertAlignType.Middle,
                textFormat = new TextFormat { size = 12, bold = true, color = AccentViolet },
                text = "虚拟站位生效中 (" + overrideCount + "人)"
            };
            txt.SetXY(6f, 0f);
            txt.SetSize(Math.Max(30f, w - resetW - 14f), h);
            bar.AddChild(txt);

            var resetBtn = CreateMiniButton("还原", resetW, Math.Max(16f, h - 4f), () => VirtualResetAllRequested?.Invoke(), fontSize: 11);
            resetBtn.SetXY(w - resetW - 4f, (h - resetBtn.height) * 0.5f);
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

            int nameFontSize = rowHeight < 30f ? 13 : 14;
            float nameY = Math.Max(1f, (rowHeight - 20f) * 0.5f);
            float nameW = Math.Max(64f, (rowW - 14f) * 0.54f);
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
            nameTf.SetXY(6f, nameY);
            nameTf.SetSize(nameW, Math.Min(22f, rowHeight - 2f));
            row.AddChild(nameTf);

            int metaFontSize = rowHeight < 30f ? 11 : 12;
            float metaY = Math.Max(1f, (rowHeight - 18f) * 0.5f);
            float metaX = 6f + nameW + 4f;
            float metaW = Math.Max(28f, rowW - metaX - 6f);
            Color metaColor = isActing ? AccentGreen : (!st.IsNormalAggressive ? AccentCyan : TextMuted);

            string hpPart = st.MaxHp > 0 ? ("HP " + st.Hp + "/" + st.MaxHp) : string.Empty;
            string tilePart = "格" + st.CurrentLandId;
            string statusPart = isActing
                ? " 行动中"
                : (hasActed ? " 已动" : (!st.IsNormalAggressive ? " 非主动" : string.Empty));

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
            metaTf.SetSize(metaW, Math.Min(20f, rowHeight - 2f));
            row.AddChild(metaTf);

            long capturedId = st.MonsterId;
            row.onRollOver.Add(() => DrawRowBg(bg, rowW, rowHeight, isSelected, hasActed, hovered: true));
            row.onRollOut.Add(() => DrawRowBg(bg, rowW, rowHeight, isSelected, hasActed, hovered: false));
            row.onClick.Add(() =>
            {
                MonsterSelected?.Invoke(capturedId);
                if (_closeOnSelect) Hide();
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
