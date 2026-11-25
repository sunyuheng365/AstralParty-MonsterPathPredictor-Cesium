using System;
using System.Collections.Generic;
using FairyGUI;
using UnityEngine;
using MonsterPathPredictor.Core;

namespace MonsterPathPredictor.Runtime
{
    internal sealed partial class MonsterPickerPopup
    {
        private static void ComputeVirtualTabLayout(
            IReadOnlyList<TrackedPlayer> players,
            BoardGraph board,
            VirtualPlayerSandbox sandbox,
            float stageHeight,
            float contentW,
            out CompactLayout layout,
            out List<PopupPagination.VirtualRowItem> rows,
            out List<PageSlice> pages,
            out float rowGap,
            out int neighborCols)
        {
            int playerCount = players != null ? players.Count : 0;
            bool hasLivingSelected = false;
            int neighborCount = 0;
            if (players != null && sandbox != null)
            {
                for (int i = 0; i < players.Count; i++)
                {
                    var p = players[i];
                    if (p != null && p.PlayerId == sandbox.SelectedPlayerId)
                    {
                        hasLivingSelected = p.IsAlive;
                        if (hasLivingSelected && board != null)
                        {
                            int eff = sandbox.GetEffectiveLandId(p);
                            neighborCount = VirtualPlayerSandbox.GetReachableNeighbors(board, eff).Count;
                        }
                        break;
                    }
                }
            }

            if (playerCount == 0 || sandbox == null)
            {
                layout = ComputeCompactLayout(stageHeight, showPager: false);
                rows = new List<PopupPagination.VirtualRowItem>();
                pages = new List<PageSlice> { new PageSlice(0, 0) };
                rowGap = 3f;
                neighborCols = 1;
                return;
            }

            // First try fitting on a single page without pager chrome.
            var singleLayout = ComputeCompactLayout(stageHeight, showPager: false);
            var singleRows = PopupPagination.BuildVirtualRows(playerCount, hasLivingSelected, neighborCount, singleLayout.MaxContentHeight, contentW, out float singleGap, out int singleCols);
            var singlePages = PopupPagination.PaginateVirtualRows(singleRows, singleGap, singleLayout.MaxContentHeight);
            if (singlePages.Count <= 1)
            {
                layout = singleLayout;
                rows = singleRows;
                pages = singlePages;
                rowGap = singleGap;
                neighborCols = singleCols;
                return;
            }

            // Needs pagination: reserve pager chrome and re-paginate measured rows cleanly so zero rows are ever dropped.
            layout = ComputeCompactLayout(stageHeight, showPager: true);
            rows = PopupPagination.BuildVirtualRows(playerCount, hasLivingSelected, neighborCount, layout.MaxContentHeight, contentW, out rowGap, out neighborCols);
            pages = PopupPagination.PaginateVirtualRows(rows, rowGap, layout.MaxContentHeight);
        }

        private float BuildVirtualTab(
            IReadOnlyList<TrackedPlayer> players,
            BoardGraph board,
            VirtualPlayerSandbox sandbox,
            long localPlayerId,
            CompactLayout layout,
            float contentW,
            IReadOnlyList<PopupPagination.VirtualRowItem> rows,
            PageSlice slice,
            float rowGap,
            int neighborCols)
        {
            float y = 0f;
            float maxH = layout.MaxContentHeight;

            if (players == null || players.Count == 0 || sandbox == null || rows == null || slice.Count == 0)
            {
                var empty = new GTextField
                {
                    touchable = false,
                    autoSize = AutoSizeType.None,
                    singleLine = true,
                    textFormat = new TextFormat { size = 13, color = TextMuted },
                    text = "等待读取角色站位..."
                };
                empty.SetXY(0f, 8f);
                empty.SetSize(contentW, 20f);
                _contentHost.AddChild(empty);
                return Math.Min(maxH, 36f);
            }

            TrackedPlayer selectedPlayer = null;
            for (int i = 0; i < players.Count; i++)
            {
                if (players[i] != null && players[i].PlayerId == sandbox.SelectedPlayerId)
                {
                    selectedPlayer = players[i];
                    break;
                }
            }

            bool hasLivingSelected = selectedPlayer != null && selectedPlayer.IsAlive;
            int effLand = hasLivingSelected ? sandbox.GetEffectiveLandId(selectedPlayer) : -1;
            int layoutGen = sandbox.LayoutGeneration;
            IReadOnlyList<int> neighbors = (hasLivingSelected && board != null)
                ? (IReadOnlyList<int>)VirtualPlayerSandbox.GetReachableNeighbors(board, effLand)
                : Array.Empty<int>();

            int end = Math.Min(rows.Count, slice.StartIndex + slice.Count);
            for (int idx = slice.StartIndex; idx < end; idx++)
            {
                var item = rows[idx];
                if (idx > slice.StartIndex) y += rowGap;

                switch (item.Kind)
                {
                    case PopupPagination.VirtualRowKind.Hint:
                    {
                        var hint = new GTextField
                        {
                            touchable = false,
                            autoSize = AutoSizeType.None,
                            singleLine = true,
                            textFormat = new TextFormat { size = 12, color = TextMuted },
                            text = "虚拟移位不影响实战，点怪物即可看转向变化"
                        };
                        hint.SetXY(2f, y);
                        hint.SetSize(contentW - 4f, item.Height);
                        _contentHost.AddChild(hint);
                        break;
                    }

                    case PopupPagination.VirtualRowKind.PlayerRow:
                    {
                        if (item.StartIndex >= 0 && item.StartIndex < players.Count && players[item.StartIndex] != null)
                        {
                            var p = players[item.StartIndex];
                            bool isSel = p.PlayerId == sandbox.SelectedPlayerId;
                            var row = BuildVirtualPlayerRow(p, sandbox, localPlayerId, isSel, contentW, item.Height);
                            row.SetXY(0f, y);
                            _contentHost.AddChild(row);
                        }
                        break;
                    }

                    case PopupPagination.VirtualRowKind.BoardPickToggle:
                    {
                        bool armed = hasLivingSelected && sandbox.BoardPickArmed;
                        long capturedOwnerId = selectedPlayer != null ? selectedPlayer.PlayerId : 0L;
                        string toggleLabel = !hasLivingSelected
                            ? "○ 暂无存活角色可右键移位"
                            : (armed ? "● 右键点击棋盘任意格移位：已开启" : "○ 开启「右键点击棋盘格」快速移位");
                        var pickBtn = CreateStyledButton(
                            toggleLabel,
                            contentW,
                            item.Height,
                            armed ? BtnVioletBg : BtnBg,
                            armed ? BtnVioletBorder : (hasLivingSelected ? new Color(0.35f, 0.45f, 0.60f, 0.8f) : new Color(0.26f, 0.30f, 0.36f, 0.55f)),
                            armed ? AccentGold : (hasLivingSelected ? TextPrimary : TextMuted),
                            () => VirtualBoardPickToggled?.Invoke(capturedOwnerId, !armed, layoutGen),
                            fontSize: 12);
                        pickBtn.touchable = hasLivingSelected;
                        pickBtn.SetXY(0f, y);
                        _contentHost.AddChild(pickBtn);
                        break;
                    }

                    case PopupPagination.VirtualRowKind.NeighborHeader:
                    {
                        if (hasLivingSelected)
                        {
                            var nbTitle = new GTextField
                            {
                                touchable = false,
                                autoSize = AutoSizeType.None,
                                singleLine = true,
                                textFormat = new TextFormat { size = 12, bold = true, color = AccentCyan },
                                text = selectedPlayer.SeatTag + " 当前格#" + effLand + (neighbors.Count > 0 ? "，点相邻格移动：" : "（无相邻格）")
                            };
                            nbTitle.SetXY(2f, y);
                            nbTitle.SetSize(contentW - 4f, item.Height);
                            _contentHost.AddChild(nbTitle);
                        }
                        break;
                    }

                    case PopupPagination.VirtualRowKind.NeighborRow:
                    {
                        if (hasLivingSelected && neighbors.Count > 0)
                        {
                            int cols = Math.Max(1, neighborCols);
                            float nbGap = 4f;
                            float nbW = (contentW - nbGap * (cols - 1)) / cols;
                            long capturedPid = selectedPlayer.PlayerId;
                            int rowCount = Math.Min(item.Count, neighbors.Count - item.StartIndex);
                            for (int c = 0; c < rowCount; c++)
                            {
                                int targetTile = neighbors[item.StartIndex + c];
                                bool isRealTile = targetTile == selectedPlayer.LandId;
                                string label = isRealTile ? ("回格#" + targetTile) : ("走→#" + targetTile);
                                var stepBtn = CreateStyledButton(
                                    label,
                                    nbW,
                                    item.Height,
                                    isRealTile ? BtnActiveBg : BtnBg,
                                    isRealTile ? AccentCyan : BtnVioletBorder,
                                    isRealTile ? AccentCyan : TextPrimary,
                                    () => VirtualStepNeighborRequested?.Invoke(capturedPid, effLand, targetTile, layoutGen),
                                    fontSize: 12);
                                stepBtn.SetXY(c * (nbW + nbGap), y);
                                _contentHost.AddChild(stepBtn);
                            }
                        }
                        break;
                    }

                    case PopupPagination.VirtualRowKind.ActionsRow:
                    {
                        float aGap = 4f;
                        float aW = (contentW - aGap * 2f) / 3f;
                        long selId = sandbox.SelectedPlayerId;
                        bool canUndo = sandbox.CanUndo(selId);
                        bool selHasState = sandbox.HasPlayerState(selId);
                        bool anyState = sandbox.HasAnyState;

                        var undoBtn = CreateStyledButton("撤回一步", aW, item.Height, BtnBg, canUndo ? AccentCyan : new Color(0.28f, 0.34f, 0.42f, 0.6f), canUndo ? TextPrimary : TextMuted, () => VirtualUndoRequested?.Invoke(selId), fontSize: 12);
                        undoBtn.touchable = canUndo;
                        undoBtn.SetXY(0f, y);
                        _contentHost.AddChild(undoBtn);

                        var resetOneBtn = CreateStyledButton("重置角色", aW, item.Height, BtnBg, selHasState ? AccentGold : new Color(0.28f, 0.34f, 0.42f, 0.6f), selHasState ? AccentGold : TextMuted, () => VirtualResetPlayerRequested?.Invoke(selId), fontSize: 12);
                        resetOneBtn.touchable = selHasState;
                        resetOneBtn.SetXY(aW + aGap, y);
                        _contentHost.AddChild(resetOneBtn);

                        var resetAllBtn = CreateStyledButton("全部还原", aW, item.Height, anyState ? BtnVioletBg : BtnBg, anyState ? BtnVioletBorder : new Color(0.28f, 0.34f, 0.42f, 0.6f), anyState ? AccentViolet : TextMuted, () => VirtualResetAllRequested?.Invoke(), fontSize: 12);
                        resetAllBtn.touchable = anyState;
                        resetAllBtn.SetXY((aW + aGap) * 2f, y);
                        _contentHost.AddChild(resetAllBtn);
                        break;
                    }
                }

                y += item.Height;
            }

            return Math.Min(maxH, Math.Max(24f, y + 2f));
        }

        private GComponent BuildVirtualPlayerRow(
            TrackedPlayer player,
            VirtualPlayerSandbox sandbox,
            long localPlayerId,
            bool isSelected,
            float rowW,
            float rowH)
        {
            bool hasVirt = sandbox.TryGetVirtualLand(player.PlayerId, out int virtLand) && virtLand != player.LandId;
            bool isLocal = player.PlayerId == localPlayerId;

            var row = new GComponent { touchable = player.IsAlive, opaque = true };
            row.SetSize(rowW, rowH);

            var bg = new GGraph { touchable = false };
            bg.SetSize(rowW, rowH);
            Color fill = !player.IsAlive ? RowActedBg : (isSelected ? (hasVirt ? BtnVioletBg : RowSelectedBg) : RowNormalBg);
            Color border = isSelected ? RowSelectedBorder : (hasVirt ? BtnVioletBorder : new Color(0.24f, 0.30f, 0.40f, 0.65f));
            bg.DrawRect(rowW, rowH, isSelected ? 2 : 1, border, fill);
            row.AddChild(bg);

            float leftW = Math.Max(60f, (rowW - 14f) * 0.52f);
            string selfTag = isLocal ? "(我)" : string.Empty;
            string pName = string.IsNullOrEmpty(player.Name) ? player.SeatTag : (player.SeatTag + selfTag + " " + player.Name);
            var nameTf = new GTextField
            {
                touchable = false,
                autoSize = AutoSizeType.None,
                singleLine = true,
                verticalAlign = VertAlignType.Middle,
                textFormat = new TextFormat
                {
                    size = 13,
                    bold = isSelected || isLocal,
                    color = !player.IsAlive ? TextMuted : (isSelected ? AccentGold : TextPrimary)
                },
                text = (isSelected ? "▶ " : "  ") + pName
            };
            nameTf.SetXY(6f, 0f);
            nameTf.SetSize(leftW, rowH);
            row.AddChild(nameTf);

            float rightX = 6f + leftW + 4f;
            float rightW = Math.Max(28f, rowW - rightX - 6f);
            string posText = !player.IsAlive
                ? "倒地"
                : (hasVirt ? ("格" + player.LandId + " → 虚#" + virtLand) : ("格" + player.LandId));
            var posTf = new GTextField
            {
                touchable = false,
                autoSize = AutoSizeType.None,
                singleLine = true,
                align = AlignType.Right,
                verticalAlign = VertAlignType.Middle,
                textFormat = new TextFormat
                {
                    size = 12,
                    bold = hasVirt,
                    color = hasVirt ? AccentViolet : TextMuted
                },
                text = posText
            };
            posTf.SetXY(rightX, 0f);
            posTf.SetSize(rightW, rowH);
            row.AddChild(posTf);

            if (player.IsAlive)
            {
                long capturedId = player.PlayerId;
                row.onRollOver.Add(() => bg.DrawRect(rowW, rowH, isSelected ? 2 : 1, PanelBorder, RowHoverBg));
                row.onRollOut.Add(() => bg.DrawRect(rowW, rowH, isSelected ? 2 : 1, border, fill));
                row.onClick.Add(() => VirtualPlayerSelected?.Invoke(capturedId));
            }
            return row;
        }
    }
}
