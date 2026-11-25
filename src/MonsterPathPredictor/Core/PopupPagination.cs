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
            public StyleRowItem(StyleRowKind kind, float height) { Kind = kind; Height = height; }
        }

        public static List<StyleRowItem> BuildStyleRows(float maxContentH, float contentW, bool hasSaveWarn, out float rowGap)
        {
            float rowH = maxContentH < 220f ? 21f : (maxContentH < 360f ? 23f : 25f);
            float chipH = Math.Min(rowH, 23f);
            float swatchH = Math.Min(rowH, 22f);
            rowGap = 3f;

            var items = new List<StyleRowItem>(16);
            if (hasSaveWarn) items.Add(new StyleRowItem(StyleRowKind.SaveWarning, 18f));
            items.Add(new StyleRowItem(StyleRowKind.LineWidth, rowH));
            items.Add(new StyleRowItem(StyleRowKind.DashSpeed, rowH));
            items.Add(new StyleRowItem(StyleRowKind.DashLength, rowH));
            items.Add(new StyleRowItem(StyleRowKind.GapLength, rowH));
            items.Add(new StyleRowItem(StyleRowKind.FontSize, rowH));
            items.Add(new StyleRowItem(StyleRowKind.HeightOffset, rowH));
            items.Add(new StyleRowItem(StyleRowKind.Toggles, rowH));
            items.Add(new StyleRowItem(StyleRowKind.ColorHeader, 20f));
            items.Add(new StyleRowItem(StyleRowKind.ColorRolesRow1, chipH));
            items.Add(new StyleRowItem(StyleRowKind.ColorRolesRow2, chipH));
            items.Add(new StyleRowItem(StyleRowKind.Presets, swatchH));

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
            float pRowH = maxContentH < 160f ? 22f : 28f;
            float btnH = maxContentH < 180f ? 21f : (maxContentH < 240f ? 23f : 26f);
            rowGap = 3f;
            neighborCols = neighborCount <= 0 ? 1 : (contentW < 180f ? 2 : Math.Min(3, neighborCount));

            var items = new List<VirtualRowItem>(12);
            if (maxContentH >= 170f)
            {
                items.Add(new VirtualRowItem(VirtualRowKind.Hint, 18f));
            }
            for (int i = 0; i < playerCount; i++)
            {
                items.Add(new VirtualRowItem(VirtualRowKind.PlayerRow, pRowH, i, 1));
            }
            items.Add(new VirtualRowItem(VirtualRowKind.BoardPickToggle, btnH));
            if (hasLivingSelected)
            {
                items.Add(new VirtualRowItem(VirtualRowKind.NeighborHeader, 18f));
                for (int start = 0; start < neighborCount; start += neighborCols)
                {
                    int cnt = Math.Min(neighborCols, neighborCount - start);
                    items.Add(new VirtualRowItem(VirtualRowKind.NeighborRow, btnH, start, cnt));
                }
            }
            items.Add(new VirtualRowItem(VirtualRowKind.ActionsRow, btnH));
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
