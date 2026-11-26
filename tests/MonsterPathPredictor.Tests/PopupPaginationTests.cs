using System.Collections.Generic;
using MonsterPathPredictor.Core;
using Xunit;

namespace MonsterPathPredictor.Tests
{
    public class PopupPaginationTests
    {
        [Theory]
        [InlineData(4f, 40f, true)]
        [InlineData(6f, 40f, false)]
        [InlineData(16f, 40f, true)]
        [InlineData(16f, 52f, true)]
        [InlineData(42f, 40f, true)]
        [InlineData(42f, 140f, false)]
        [InlineData(42f, 140f, true)]
        [InlineData(78f, 140f, false)]
        [InlineData(78f, 140f, true)]
        [InlineData(78f, 328f, false)]
        [InlineData(78f, 328f, true)]
        [InlineData(160f, 328f, true)]
        [InlineData(560f, 328f, true)]
        public void StyleTabPaginationExposesEveryControlWithoutSilentDrops(float maxContentH, float contentW, bool hasSaveWarn)
        {
            var rows = PopupPagination.BuildStyleRows(maxContentH, contentW, hasSaveWarn, out float gap);
            var pages = PopupPagination.PaginateStyleRows(rows, gap, maxContentH);

            Assert.NotEmpty(pages);
            var reached = new HashSet<PopupPagination.StyleRowKind>();
            int totalSeen = 0;

            for (int p = 0; p < pages.Count; p++)
            {
                var slice = pages[p];
                Assert.True(slice.Count > 0);
                float usedH = 0f;
                for (int i = 0; i < slice.Count; i++)
                {
                    var item = rows[slice.StartIndex + i];
                    if (i > 0) usedH += gap;
                    usedH += item.Height;
                    reached.Add(item.Kind);
                    totalSeen++;
                }
                // Even singleton pages at extreme heights (e.g. 16px) MUST stay strictly inside maxContentH!
                Assert.True(usedH <= maxContentH + 0.001f, $"Page {p} exceeded maxContentH ({usedH} > {maxContentH})");
            }

            Assert.Equal(rows.Count, totalSeen);
            Assert.Contains(PopupPagination.StyleRowKind.LineWidth, reached);
            Assert.Contains(PopupPagination.StyleRowKind.DashSpeed, reached);
            Assert.Contains(PopupPagination.StyleRowKind.DashLength, reached);
            Assert.Contains(PopupPagination.StyleRowKind.GapLength, reached);
            Assert.Contains(PopupPagination.StyleRowKind.FontSize, reached);
            Assert.Contains(PopupPagination.StyleRowKind.HeightOffset, reached);
            Assert.Contains(PopupPagination.StyleRowKind.Toggles, reached);
            Assert.Contains(PopupPagination.StyleRowKind.ColorHeader, reached);
            Assert.Contains(PopupPagination.StyleRowKind.ColorRolesRow1, reached);
            Assert.Contains(PopupPagination.StyleRowKind.ColorRolesRow2, reached);
            Assert.Contains(PopupPagination.StyleRowKind.ResetDefaults, reached);
            if (contentW >= 160f)
            {
                Assert.Contains(PopupPagination.StyleRowKind.Presets, reached);
            }
            else
            {
                Assert.Contains(PopupPagination.StyleRowKind.PresetsRow1, reached);
                Assert.Contains(PopupPagination.StyleRowKind.PresetsRow2, reached);
            }
            if (hasSaveWarn) Assert.Contains(PopupPagination.StyleRowKind.SaveWarning, reached);
            if (contentW >= 210f)
            {
                Assert.Contains(PopupPagination.StyleRowKind.RgbaPairRG, reached);
                Assert.Contains(PopupPagination.StyleRowKind.RgbaPairBA, reached);
            }
            else
            {
                Assert.Contains(PopupPagination.StyleRowKind.RgbaSingleR, reached);
                Assert.Contains(PopupPagination.StyleRowKind.RgbaSingleG, reached);
                Assert.Contains(PopupPagination.StyleRowKind.RgbaSingleB, reached);
                Assert.Contains(PopupPagination.StyleRowKind.RgbaSingleA, reached);
            }
        }

        [Theory]
        [InlineData(16f, 52f, 4, 4)]
        [InlineData(42f, 140f, 4, 4)]
        [InlineData(42f, 328f, 4, 6)]
        [InlineData(78f, 328f, 4, 4)]
        [InlineData(160f, 140f, 4, 5)]
        [InlineData(460f, 328f, 4, 4)]
        public void VirtualTabPaginationExposesEveryPlayerAndEveryReachableNeighbor(
            float maxContentH,
            float contentW,
            int playerCount,
            int neighborCount)
        {
            var rows = PopupPagination.BuildVirtualRows(playerCount, hasLivingSelected: true, neighborCount, maxContentH, contentW, out float gap, out int cols);
            var pages = PopupPagination.PaginateVirtualRows(rows, gap, maxContentH);

            Assert.True(cols >= 1);
            Assert.NotEmpty(pages);

            var reachedPlayers = new HashSet<int>();
            var reachedNeighbors = new HashSet<int>();
            bool sawToggle = false;
            bool sawActions = false;

            for (int p = 0; p < pages.Count; p++)
            {
                var slice = pages[p];
                float usedH = 0f;
                for (int i = 0; i < slice.Count; i++)
                {
                    var item = rows[slice.StartIndex + i];
                    if (i > 0) usedH += gap;
                    usedH += item.Height;

                    if (item.Kind == PopupPagination.VirtualRowKind.PlayerRow)
                    {
                        reachedPlayers.Add(item.StartIndex);
                    }
                    else if (item.Kind == PopupPagination.VirtualRowKind.NeighborRow)
                    {
                        for (int k = 0; k < item.Count; k++) reachedNeighbors.Add(item.StartIndex + k);
                    }
                    else if (item.Kind == PopupPagination.VirtualRowKind.BoardPickToggle) sawToggle = true;
                    else if (item.Kind == PopupPagination.VirtualRowKind.ActionsRow) sawActions = true;
                }
                Assert.True(usedH <= maxContentH + 0.001f);
            }

            Assert.Equal(playerCount, reachedPlayers.Count);
            Assert.Equal(neighborCount, reachedNeighbors.Count);
            Assert.True(sawToggle);
            Assert.True(sawActions);
        }

        [Theory]
        [InlineData(48f, 4f)]
        [InlineData(60f, 4f)]
        [InlineData(100f, 4f)]
        [InlineData(120f, 4f)]
        [InlineData(160f, 4f)]
        [InlineData(220f, 8f)]
        [InlineData(344f, 8f)]
        public void NarrowStageTabsFooterAndInnerRowsStayNonOverlappingAndInsideBounds(float panelW, float padX)
        {
            PopupPagination.ComputeThreeTabGeometry(panelW, padX, out float tabW, out float tabGap);
            float rightEdge = padX + (tabW + tabGap) * 2f + tabW;
            Assert.True(tabW > 0f);
            Assert.True(rightEdge <= panelW - padX + 0.01f, $"Tabs exceeded bounds at width {panelW}: {rightEdge} > {panelW - padX}");

            PopupPagination.ComputeFooterGeometry(
                panelW, padX,
                out float minusX, out float stepBtnW,
                out float stepsX, out float stepsW,
                out float plusX,
                out float clearX, out float clearW);

            Assert.True(minusX >= padX - 0.01f);
            Assert.True(minusX + stepBtnW <= stepsX + 0.01f);
            Assert.True(stepsX + stepsW <= plusX + 0.01f);
            Assert.True(plusX + stepBtnW <= clearX + 0.01f);
            Assert.True(clearX + clearW <= panelW - padX + 0.01f);

            float contentW = panelW - padX * 2f;
            PopupPagination.ComputeChannelGeometry(contentW, out float tagX, out float tagW, out float cMinusX, out float cBtnW, out float cValX, out float cValW, out float cPlusX);
            Assert.True(tagX >= 0f);
            Assert.True(tagX + tagW <= cMinusX + 0.01f);
            Assert.True(cMinusX + cBtnW <= cValX + 0.01f);
            Assert.True(cValX + cValW <= cPlusX + 0.01f);
            Assert.True(cPlusX + cBtnW <= contentW + 0.01f);

            PopupPagination.ComputeSplitRowGeometry(contentW, 6f, 0.54f, out float lX, out float lW, out float rX, out float rW);
            Assert.True(lX >= 0f);
            Assert.True(lX + lW <= rX + 0.01f);
            Assert.True(rX + rW <= contentW + 0.01f);

            // Actual numeric stepper child rects MUST stay non-overlapping and strictly inside [0, contentW] x [0, rowH]
            // even at the 48px panel (40px content) breakpoint AND tiny 6px/24px/30px row heights!
            foreach (float rowH in new[] { 6f, 16f, 24f, 30f })
            {
                var st = PopupPagination.ComputeStepperGeometry(contentW, rowH);
                Assert.True(st.MinusX >= 0f);
                Assert.True(st.MinusX + st.BtnW <= st.ValX + 0.01f);
                Assert.True(st.ValX + st.ValW <= st.PlusX + 0.01f);
                Assert.True(st.PlusX + st.BtnW <= contentW + 0.01f);
                Assert.True(st.MinusY >= 0f && st.MinusY + st.BtnH <= rowH + 0.01f);
                Assert.True(st.ValY >= 0f && st.ValY + st.ValH <= rowH + 0.01f);
                if (st.ShowLabel)
                {
                    Assert.True(st.LabelX >= 0f && st.LabelX + st.LabelW <= contentW + 0.01f);
                    Assert.True(st.LabelY >= 0f && st.LabelY + st.LabelH <= rowH + 0.01f);
                    if (st.Stacked)
                        Assert.True(st.LabelY + st.LabelH <= st.MinusY + 0.01f);
                    else
                        Assert.True(st.LabelX + st.LabelW <= st.MinusX + 0.01f);
                }
                if (contentW >= 40f)
                {
                    Assert.True(st.ValW >= 12f, $"Stepper value width too narrow at contentW={contentW}: {st.ValW}");
                }

                // Virtual banner text and reset button MUST NOT overlap at any width or height!
                PopupPagination.ComputeVirtualBannerGeometry(
                    contentW, rowH,
                    out float bx, out float by, out float bw, out float bh,
                    out float rx, out float ry, out float rw, out float rh,
                    out bool stacked);
                Assert.True(bx >= 0f && bx + bw <= contentW + 0.01f);
                Assert.True(by >= 0f && by + bh <= rowH + 0.01f);
                Assert.True(rx >= 0f && rx + rw <= contentW + 0.01f);
                Assert.True(ry >= 0f && ry + rh <= rowH + 0.01f);
                if (stacked)
                    Assert.True(by + bh <= ry + 0.01f);
                else
                    Assert.True(bx + bw <= rx + 0.01f);
            }
        }

        [Fact]
        public void Narrow48PxPanelSplitsMultiButtonRowsAndClampsExtremeCompactChromeHeights()
        {
            // At 48px panel width (40px contentW), Style toggles MUST split to 1 button per row (full 40px width!)
            // and Virtual actions MUST split to 1 button per row so no button is crushed to 7.75px!
            var styleRows = PopupPagination.BuildStyleRows(maxContentH: 300f, contentW: 40f, hasSaveWarn: false, out _);
            int toggleRows = 0;
            int toggleCovered = 0;
            int roleRows = 0;
            int roleCovered = 0;
            foreach (var r in styleRows)
            {
                if (r.Kind == PopupPagination.StyleRowKind.Toggles)
                {
                    toggleRows++;
                    Assert.Equal(1, r.Count);
                    toggleCovered += r.Count;
                }
                if (r.Kind == PopupPagination.StyleRowKind.ColorRolesRow1 || r.Kind == PopupPagination.StyleRowKind.ColorRolesRow2)
                {
                    roleRows++;
                    Assert.Equal(1, r.Count);
                    roleCovered += r.Count;
                }
            }
            Assert.Equal(4, toggleRows);
            Assert.Equal(4, toggleCovered);
            Assert.Equal(6, roleRows);
            Assert.Equal(6, roleCovered);

            var virtRows = PopupPagination.BuildVirtualRows(4, hasLivingSelected: true, neighborCount: 3, maxContentH: 300f, contentW: 40f, out _, out int nbCols);
            Assert.Equal(1, nbCols);
            int actionRows = 0;
            foreach (var vr in virtRows)
            {
                if (vr.Kind == PopupPagination.VirtualRowKind.ActionsRow)
                {
                    actionRows++;
                    Assert.Equal(1, vr.Count);
                }
            }
            Assert.Equal(3, actionRows);

            // Extreme 48px-high stage chrome clamping: child heights MUST stay <= parent budget!
            foreach (float tabH in new[] { 4f, 6f, 7.5f, 12f, 24f })
            {
                float btnH = PopupPagination.ComputeTabStripButtonHeight(tabH);
                Assert.True(btnH >= 1f && btnH <= tabH + 0.001f);
                PopupPagination.ClampChildVertical(tabH, 2f, 16f, out float cy, out float ch);
                Assert.True(cy >= 0f && cy + ch <= tabH + 0.001f);
            }
        }

        [Fact]
        public void StyleRowsAddSectionHeadersOnlyWhenTall()
        {
            var tallRows = PopupPagination.BuildStyleRows(maxContentH: 400f, contentW: 328f, hasSaveWarn: false, out _);
            int secLineIdx = tallRows.FindIndex(r => r.Kind == PopupPagination.StyleRowKind.SectionLine);
            int lineWidthIdx = tallRows.FindIndex(r => r.Kind == PopupPagination.StyleRowKind.LineWidth);
            int secDispIdx = tallRows.FindIndex(r => r.Kind == PopupPagination.StyleRowKind.SectionDisplay);
            int firstToggleIdx = tallRows.FindIndex(r => r.Kind == PopupPagination.StyleRowKind.Toggles);

            Assert.True(secLineIdx >= 0);
            Assert.True(secDispIdx >= 0);
            Assert.True(secLineIdx < lineWidthIdx);
            Assert.True(secDispIdx < firstToggleIdx);

            var shortRows = PopupPagination.BuildStyleRows(maxContentH: 200f, contentW: 328f, hasSaveWarn: false, out _);
            Assert.DoesNotContain(shortRows, r => r.Kind == PopupPagination.StyleRowKind.SectionLine);
            Assert.DoesNotContain(shortRows, r => r.Kind == PopupPagination.StyleRowKind.SectionDisplay);
        }
    }
}
