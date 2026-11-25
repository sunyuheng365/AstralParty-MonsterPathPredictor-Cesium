using System;
using System.Collections.Generic;

namespace MonsterPathPredictor.Core
{
    public readonly struct PageSlice
    {
        public int StartIndex { get; }
        public int Count { get; }

        public PageSlice(int startIndex, int count)
        {
            StartIndex = startIndex;
            Count = count;
        }
    }

    /// <summary>
    /// Pure-C# measured-height paginator & narrow-width horizontal fitter for FairyGUI popup tabs.
    /// Guarantees every discrete UI item/row appears on a concrete page with zero silent drops.
    /// </summary>
    public static class PopupPagination
    {
        public static List<PageSlice> Paginate(IReadOnlyList<float> itemHeights, float gap, float maxPageHeight)
        {
            var pages = new List<PageSlice>();
            if (itemHeights == null || itemHeights.Count == 0)
            {
                pages.Add(new PageSlice(0, 0));
                return pages;
            }

            float budget = Math.Max(1f, maxPageHeight);
            int pageStart = 0;
            float used = 0f;
            int count = 0;

            for (int i = 0; i < itemHeights.Count; i++)
            {
                float h = Math.Max(1f, itemHeights[i]);
                float needed = count == 0 ? h : (used + gap + h);
                if (count > 0 && needed > budget)
                {
                    pages.Add(new PageSlice(pageStart, count));
                    pageStart = i;
                    used = h;
                    count = 1;
                }
                else
                {
                    used = needed;
                    count++;
                }
            }

            if (count > 0)
            {
                pages.Add(new PageSlice(pageStart, count));
            }
            return pages;
        }

        /// <summary>
        /// Computes strictly in-bounds tab button width and gap for 3 tabs inside [padX, panelW - padX].
        /// </summary>
        public static void ComputeThreeTabGeometry(float panelW, float padX, out float tabW, out float tabGap)
        {
            float usable = Math.Max(12f, panelW - padX * 2f);
            tabGap = usable < 120f ? 1f : (usable < 200f ? 2f : 4f);
            tabW = Math.Max(4f, (usable - tabGap * 2f) / 3f);
        }

        /// <summary>
        /// Computes strictly in-bounds, non-overlapping footer button geometry inside [padX, panelW - padX].
        /// </summary>
        public static void ComputeFooterGeometry(
            float panelW,
            float padX,
            out float minusX,
            out float stepBtnW,
            out float stepsX,
            out float stepsW,
            out float plusX,
            out float clearX,
            out float clearW)
        {
            float avail = Math.Max(24f, panelW - padX * 2f);
            float gap = panelW < 140f ? 1f : (panelW < 220f ? 2f : 4f);

            stepBtnW = panelW < 120f ? 14f : (panelW < 160f ? 16f : (panelW < 200f ? 20f : 26f));
            clearW = panelW < 120f
                ? Math.Max(18f, avail * 0.30f)
                : (panelW < 160f ? Math.Max(26f, avail * 0.34f) : (panelW < 200f ? 48f : (panelW < 260f ? 68f : 76f)));

            // Guarantee all 4 controls + 3 gaps fit strictly inside avail even on extreme narrow stages.
            float minStepsW = panelW < 120f ? 10f : 16f;
            float needed = stepBtnW * 2f + minStepsW + clearW + gap * 3f;
            if (needed > avail)
            {
                float scale = avail / needed;
                stepBtnW = Math.Max(4f, stepBtnW * scale);
                clearW = Math.Max(6f, clearW * scale);
                gap = Math.Max(0.5f, gap * scale);
            }

            clearX = Math.Max(padX, panelW - padX - clearW);
            minusX = padX;
            stepsX = minusX + stepBtnW + gap;
            float maxRightForPlus = Math.Max(stepsX + stepBtnW, clearX - gap);
            float plusLeftLimit = Math.Max(stepsX, maxRightForPlus - stepBtnW);
            stepsW = Math.Max(4f, Math.Min(88f, plusLeftLimit - gap - stepsX));
            plusX = Math.Min(plusLeftLimit, stepsX + stepsW + gap);
        }

        /// <summary>
        /// Computes strictly in-bounds, non-overlapping channel-stepper geometry (tag, '-', value, '+')
        /// inside [0, w] even at extreme narrow widths (48-100px).
        /// </summary>
        public static void ComputeChannelGeometry(
            float w,
            out float tagX,
            out float tagW,
            out float minusX,
            out float btnW,
            out float valX,
            out float valW,
            out float plusX)
        {
            float pad = w < 100f ? 2f : 4f;
            float gap = w < 100f ? 1f : 2f;
            float avail = Math.Max(20f, w - pad * 2f);

            tagW = w < 90f ? 10f : 18f;
            btnW = w < 90f ? 12f : Math.Min(24f, Math.Max(16f, w * 0.22f));
            float minValW = w < 90f ? 10f : 16f;
            float needed = tagW + btnW * 2f + minValW + gap * 3f;
            if (needed > avail)
            {
                float scale = avail / needed;
                tagW = Math.Max(4f, tagW * scale);
                btnW = Math.Max(6f, btnW * scale);
                gap = Math.Max(0.5f, gap * scale);
            }

            tagX = pad;
            minusX = tagX + tagW + gap;
            plusX = Math.Max(minusX + btnW + gap, w - pad - btnW);
            valX = minusX + btnW + gap;
            valW = Math.Max(0f, plusX - valX - gap);
        }

        /// <summary>
        /// Computes strictly in-bounds, non-overlapping left/right text boxes for list rows.
        /// </summary>
        public static void ComputeSplitRowGeometry(
            float rowW,
            float padX,
            float leftRatio,
            out float leftX,
            out float leftW,
            out float rightX,
            out float rightW)
        {
            float pad = Math.Min(padX, Math.Max(1f, rowW * 0.08f));
            float gap = rowW < 100f ? 2f : 4f;
            float usable = Math.Max(0f, rowW - pad * 2f);
            leftX = pad;
            if (usable <= gap + 8f)
            {
                leftW = usable;
                rightX = pad + usable;
                rightW = 0f;
                return;
            }
            float avail = usable - gap;
            leftW = Math.Max(4f, avail * leftRatio);
            rightX = leftX + leftW + gap;
            rightW = Math.Max(0f, avail - leftW);
        }

        public static void ClampChildVertical(float parentH, float desiredY, float desiredH, out float y, out float h)
        {
            float safeParent = Math.Max(1f, parentH);
            h = Math.Max(1f, Math.Min(safeParent, desiredH));
            y = Math.Max(0f, Math.Min(safeParent - h, desiredY));
        }

        public static float ComputeTabStripButtonHeight(float tabStripH)
        {
            float safeH = Math.Max(1f, tabStripH);
            return safeH <= 4f ? safeH : Math.Max(1f, safeH - 1f);
        }

        public readonly struct StepperLayout
        {
            public bool Stacked { get; }
            public bool ShowLabel { get; }
            public float LabelX { get; }
            public float LabelY { get; }
            public float LabelW { get; }
            public float LabelH { get; }
            public float MinusX { get; }
            public float MinusY { get; }
            public float BtnW { get; }
            public float BtnH { get; }
            public float ValX { get; }
            public float ValY { get; }
            public float ValW { get; }
            public float ValH { get; }
            public float PlusX { get; }

            public StepperLayout(
                bool stacked, bool showLabel,
                float labelX, float labelY, float labelW, float labelH,
                float minusX, float minusY, float btnW, float btnH,
                float valX, float valY, float valW, float valH,
                float plusX)
            {
                Stacked = stacked; ShowLabel = showLabel;
                LabelX = labelX; LabelY = labelY; LabelW = labelW; LabelH = labelH;
                MinusX = minusX; MinusY = minusY; BtnW = btnW; BtnH = btnH;
                ValX = valX; ValY = valY; ValW = valW; ValH = valH;
                PlusX = plusX;
            }
        }

        /// <summary>
        /// Computes strictly in-bounds, non-overlapping numeric stepper child rects across all widths
        /// (including 40px narrow panels) and extreme compact heights.
        /// </summary>
        public static StepperLayout ComputeStepperGeometry(float rowW, float rowH)
        {
            float safeW = Math.Max(12f, rowW);
            float safeH = Math.Max(1f, rowH);
            float pad = safeW < 80f ? 1f : 4f;
            float gap = safeW < 80f ? 1f : 2f;
            float usable = Math.Max(6f, safeW - pad * 2f);

            if (safeW >= 150f)
            {
                float btnW = Math.Min(24f, Math.Max(16f, safeW * 0.09f));
                float valW = Math.Min(48f, Math.Max(30f, safeW * 0.16f));
                float rightBlock = btnW * 2f + valW + gap * 2f;
                float labelW = Math.Max(20f, usable - rightBlock - gap);
                float minusX = pad + labelW + gap;
                float valX = minusX + btnW + gap;
                float plusX = valX + valW + gap;
                ClampChildVertical(safeH, 1f, Math.Max(1f, safeH - 2f), out float cy, out float ch);
                return new StepperLayout(false, true, pad, cy, labelW, ch, minusX, cy, btnW, ch, valX, cy, valW, ch, plusX);
            }

            // Narrow width (< 150px): stack label above [- val +] when height allows; otherwise compact single-band.
            bool stacked = safeH >= 18f;
            float topH = stacked ? Math.Max(7f, (float)Math.Floor(safeH * 0.42f)) : 0f;
            float botY = stacked ? topH : 0f;
            float botH = Math.Max(1f, safeH - botY);
            ClampChildVertical(botH, 0.5f, Math.Max(1f, botH - 1f), out float relBy, out float btnH);
            float cyBot = botY + relBy;

            float nBtnW = Math.Max(4f, Math.Min(14f, (usable - gap * 2f) * 0.28f));
            float nValW = Math.Max(4f, usable - nBtnW * 2f - gap * 2f);
            float nMinusX = pad;
            float nValX = nMinusX + nBtnW + gap;
            float nPlusX = nValX + nValW + gap;

            return new StepperLayout(
                stacked: stacked,
                showLabel: stacked,
                labelX: pad, labelY: 0f, labelW: usable, labelH: topH,
                minusX: nMinusX, minusY: cyBot, btnW: nBtnW, btnH: btnH,
                valX: nValX, valY: cyBot, valW: nValW, valH: btnH,
                plusX: nPlusX);
        }

        public static void ComputeVirtualBannerGeometry(
            float rowW,
            float rowH,
            out float textX,
            out float textY,
            out float textW,
            out float textH,
            out float btnX,
            out float btnY,
            out float btnW,
            out float btnH,
            out bool stacked)
        {
            float safeW = Math.Max(12f, rowW);
            float safeH = Math.Max(1f, rowH);
            float pad = safeW < 80f ? 1f : 6f;
            float gap = safeW < 80f ? 1f : 4f;
            float usable = Math.Max(6f, safeW - pad * 2f);

            if (safeW >= 120f)
            {
                stacked = false;
                btnW = Math.Min(56f, Math.Max(28f, usable * 0.26f));
                textW = Math.Max(8f, usable - btnW - gap);
                textX = pad;
                btnX = pad + textW + gap;
                ClampChildVertical(safeH, 1f, Math.Max(1f, safeH - 2f), out textY, out textH);
                ClampChildVertical(safeH, 2f, Math.Max(1f, safeH - 4f), out btnY, out btnH);
                return;
            }

            if (safeH >= 20f)
            {
                stacked = true;
                textX = pad;
                textY = 0f;
                textW = usable;
                textH = Math.Max(8f, (float)Math.Floor(safeH * 0.48f));
                btnX = pad;
                btnY = textH;
                btnW = usable;
                btnH = Math.Max(1f, safeH - textH);
                return;
            }

            stacked = false;
            textW = Math.Max(4f, (usable - gap) * 0.56f);
            btnW = Math.Max(4f, usable - gap - textW);
            textX = pad;
            btnX = pad + textW + gap;
            ClampChildVertical(safeH, 0f, safeH, out textY, out textH);
            ClampChildVertical(safeH, 0f, safeH, out btnY, out btnH);
        }

        public enum StyleRowKind
        {
            SaveWarning,
            LineWidth,
            DashSpeed,
            DashLength,
            GapLength,
            FontSize,
            HeightOffset,
            Toggles,
            ColorHeader,
            ColorRolesRow1,
            ColorRolesRow2,
            Presets,
            PresetsRow1,
            PresetsRow2,
            RgbaPairRG,
            RgbaPairBA,
            RgbaSingleR,
            RgbaSingleG,
            RgbaSingleB,
            RgbaSingleA,
            ResetDefaults
        }

        public readonly struct StyleRowItem
        {
            public StyleRowKind Kind { get; }
            public float Height { get; }
            public int StartIndex { get; }
            public int Count { get; }
            public StyleRowItem(StyleRowKind kind, float height, int startIndex = 0, int count = 0)
            {
                Kind = kind;
                Height = height;
                StartIndex = startIndex;
                Count = count;
            }
        }

        public static List<StyleRowItem> BuildStyleRows(float maxContentH, float contentW, bool hasSaveWarn, out float rowGap)
        {
            float capH = Math.Max(1f, maxContentH);
            float rowH = Math.Min(capH, maxContentH < 220f ? 21f : (maxContentH < 360f ? 23f : 25f));
            float stepperH = (contentW < 150f && capH >= 24f) ? Math.Min(capH, 30f) : rowH;
            float chipH = Math.Min(capH, Math.Min(rowH, 23f));
            float swatchH = Math.Min(capH, Math.Min(rowH, 22f));
            float headerH = Math.Min(capH, 20f);
            float warnH = Math.Min(capH, 18f);
            rowGap = capH < 24f ? 1f : 3f;

            var items = new List<StyleRowItem>(24);
            if (hasSaveWarn) items.Add(new StyleRowItem(StyleRowKind.SaveWarning, warnH));
            items.Add(new StyleRowItem(StyleRowKind.LineWidth, stepperH));
            items.Add(new StyleRowItem(StyleRowKind.DashSpeed, stepperH));
            items.Add(new StyleRowItem(StyleRowKind.DashLength, stepperH));
            items.Add(new StyleRowItem(StyleRowKind.GapLength, stepperH));
            items.Add(new StyleRowItem(StyleRowKind.FontSize, stepperH));
            items.Add(new StyleRowItem(StyleRowKind.HeightOffset, stepperH));

            int toggleCols = contentW < 96f ? 1 : (contentW < 190f ? 2 : 4);
            for (int start = 0; start < 4; start += toggleCols)
            {
                items.Add(new StyleRowItem(StyleRowKind.Toggles, rowH, start, Math.Min(toggleCols, 4 - start)));
            }

            items.Add(new StyleRowItem(StyleRowKind.ColorHeader, headerH));

            int roleCols = contentW < 96f ? 1 : (contentW < 160f ? 2 : 3);
            for (int start = 0; start < 6; start += roleCols)
            {
                var rk = start < 3 ? StyleRowKind.ColorRolesRow1 : StyleRowKind.ColorRolesRow2;
                items.Add(new StyleRowItem(rk, chipH, start, Math.Min(roleCols, 6 - start)));
            }

            if (contentW >= 160f)
            {
                items.Add(new StyleRowItem(StyleRowKind.Presets, swatchH, 0, 8));
            }
            else if (contentW >= 80f)
            {
                items.Add(new StyleRowItem(StyleRowKind.PresetsRow1, swatchH, 0, 4));
                items.Add(new StyleRowItem(StyleRowKind.PresetsRow2, swatchH, 4, 4));
            }
            else
            {
                for (int start = 0; start < 8; start += 2)
                {
                    var pk = start < 4 ? StyleRowKind.PresetsRow1 : StyleRowKind.PresetsRow2;
                    items.Add(new StyleRowItem(pk, swatchH, start, 2));
                }
            }

            if (contentW >= 210f)
            {
                items.Add(new StyleRowItem(StyleRowKind.RgbaPairRG, chipH));
                items.Add(new StyleRowItem(StyleRowKind.RgbaPairBA, chipH));
            }
            else
            {
                items.Add(new StyleRowItem(StyleRowKind.RgbaSingleR, chipH));
                items.Add(new StyleRowItem(StyleRowKind.RgbaSingleG, chipH));
                items.Add(new StyleRowItem(StyleRowKind.RgbaSingleB, chipH));
                items.Add(new StyleRowItem(StyleRowKind.RgbaSingleA, chipH));
            }

            items.Add(new StyleRowItem(StyleRowKind.ResetDefaults, rowH));
            return items;
        }

        public enum VirtualRowKind
        {
            Hint,
            PlayerRow,
            BoardPickToggle,
            NeighborHeader,
            NeighborRow,
            ActionsRow
        }

        public readonly struct VirtualRowItem
        {
            public VirtualRowKind Kind { get; }
            public float Height { get; }
            public int StartIndex { get; }
            public int Count { get; }
            public VirtualRowItem(VirtualRowKind kind, float height, int startIndex = 0, int count = 0)
            {
                Kind = kind;
                Height = height;
                StartIndex = startIndex;
                Count = count;
            }
        }

        public static List<VirtualRowItem> BuildVirtualRows(
            int playerCount,
            bool hasLivingSelected,
            int neighborCount,
            float maxContentH,
            float contentW,
            out float rowGap,
            out int neighborCols)
        {
            float capH = Math.Max(1f, maxContentH);
            float pRowH = Math.Min(capH, maxContentH < 160f ? 22f : 28f);
            float btnH = Math.Min(capH, maxContentH < 180f ? 21f : (maxContentH < 240f ? 23f : 26f));
            float hdrH = Math.Min(capH, 18f);
            rowGap = capH < 24f ? 1f : 3f;
            neighborCols = neighborCount <= 0 ? 1 : (contentW < 110f ? 1 : (contentW < 180f ? 2 : Math.Min(3, neighborCount)));

            var items = new List<VirtualRowItem>(14);
            if (maxContentH >= 170f)
            {
                items.Add(new VirtualRowItem(VirtualRowKind.Hint, hdrH));
            }
            for (int i = 0; i < playerCount; i++)
            {
                items.Add(new VirtualRowItem(VirtualRowKind.PlayerRow, pRowH, i, 1));
            }
            items.Add(new VirtualRowItem(VirtualRowKind.BoardPickToggle, btnH));
            if (hasLivingSelected)
            {
                items.Add(new VirtualRowItem(VirtualRowKind.NeighborHeader, hdrH));
                for (int start = 0; start < neighborCount; start += neighborCols)
                {
                    int cnt = Math.Min(neighborCols, neighborCount - start);
                    items.Add(new VirtualRowItem(VirtualRowKind.NeighborRow, btnH, start, cnt));
                }
            }
            int actionCols = contentW < 110f ? 1 : 3;
            for (int start = 0; start < 3; start += actionCols)
            {
                items.Add(new VirtualRowItem(VirtualRowKind.ActionsRow, btnH, start, Math.Min(actionCols, 3 - start)));
            }
            return items;
        }

        public static List<PageSlice> PaginateStyleRows(IReadOnlyList<StyleRowItem> items, float gap, float maxPageHeight)
        {
            var heights = new float[items.Count];
            for (int i = 0; i < items.Count; i++) heights[i] = items[i].Height;
            return Paginate(heights, gap, maxPageHeight);
        }

        public static List<PageSlice> PaginateVirtualRows(IReadOnlyList<VirtualRowItem> items, float gap, float maxPageHeight)
        {
            var heights = new float[items.Count];
            for (int i = 0; i < items.Count; i++) heights[i] = items[i].Height;
            return Paginate(heights, gap, maxPageHeight);
        }
    }
}
