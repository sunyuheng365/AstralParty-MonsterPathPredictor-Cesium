using System.Collections.Generic;
using MonsterPathPredictor.Core;
using Xunit;

namespace MonsterPathPredictor.Tests
{
    public class PopupPaginationTests
    {
        [Theory]
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
                if (slice.Count > 1)
                {
                    Assert.True(usedH <= maxContentH + 0.001f, $"Page {p} exceeded maxContentH ({usedH} > {maxContentH})");
                }
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
            Assert.Contains(PopupPagination.StyleRowKind.Presets, reached);
            Assert.Contains(PopupPagination.StyleRowKind.ResetDefaults, reached);
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
                if (slice.Count > 1)
                {
                    Assert.True(usedH <= maxContentH + 0.001f);
                }
            }

            Assert.Equal(playerCount, reachedPlayers.Count);
            Assert.Equal(neighborCount, reachedNeighbors.Count);
            Assert.True(sawToggle);
            Assert.True(sawActions);
        }

        [Theory]
        [InlineData(60f, 4f)]
        [InlineData(100f, 4f)]
        [InlineData(120f, 4f)]
        [InlineData(160f, 4f)]
        [InlineData(220f, 8f)]
        [InlineData(344f, 8f)]
        public void NarrowStageTabsAndFooterStayStrictlyInsidePanelBounds(float panelW, float padX)
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
        }
    }
}
