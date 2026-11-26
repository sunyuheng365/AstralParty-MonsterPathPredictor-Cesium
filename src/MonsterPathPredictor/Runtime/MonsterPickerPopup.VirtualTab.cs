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
            out int neighborCols,
            out IReadOnlyList<int> neighbors)
        {
            int playerCount = players != null ? players.Count : 0;
            bool hasLivingSelected = false;
            neighbors = Array.Empty<int>();
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
                            neighbors = VirtualPlayerSandbox.GetReachableNeighbors(board, eff);
                        }
                        break;
                    }
                }
            }
            int neighborCount = neighbors.Count;

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
            IReadOnlyList<int> precomputedNeighbors,
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
                if (maxH < 4f) return 0f;
                PopupPagination.ClampChildVertical(maxH, Math.Min(8f, maxH * 0.15f), 20f, out float ey, out float eh);
                var empty = new GTextField
                {
                    touchable = false,
                    autoSize = AutoSizeType.None,
                    singleLine = true,
                    verticalAlign = VertAlignType.Middle,
                    textFormat = new TextFormat { size = eh < 14f ? Math.Max(8, (int)eh) : 13, color = TextMuted },
                    text = contentW < 80f ? "等待..." : "等待读取角色站位..."
                };
                empty.SetXY(0f, ey);
                empty.SetSize(contentW, eh);
                _contentHost.AddChild(empty);
                return Math.Min(maxH, ey + eh);
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
            int matchGen = _currentMatchGen;
            int layoutGen = sandbox.LayoutGeneration;
            IReadOnlyList<int> neighbors = (hasLivingSelected && precomputedNeighbors != null)
                ? precomputedNeighbors
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
                            verticalAlign = VertAlignType.Middle,
                            textFormat = new TextFormat { size = 11, color = TextSecondary },
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
                        if (contentW >= 180f && item.Height >= 18f)
                        {
                            string swLabel = !hasLivingSelected
                                ? "暂无存活角色可移位"
                                : (armed ? "右键棋盘任意格移位 · 已开启" : "右键点棋盘格快速移位");
                            var swRow = BuildSwitchRow(
                                swLabel,
                                armed,
                                hasLivingSelected,
                                contentW,
                                item.Height,
                                () => VirtualBoardPickToggled?.Invoke(capturedOwnerId, !armed, matchGen, layoutGen));
                            swRow.SetXY(0f, y);
                            _contentHost.AddChild(swRow);
                        }
                        else
                        {
                            string toggleLabel = contentW < 100f
                                ? (!hasLivingSelected ? "无活人" : (armed ? "●右选" : "○右选"))
                                : (!hasLivingSelected
                                    ? "○ 暂无存活角色可右键移位"
                                    : (armed ? "● 右键点击棋盘任意格移位：已开启" : "○ 开启「右键点击棋盘格」快速移位"));
                            int btnFont = (contentW < 100f || item.Height < 15f) ? Math.Max(8, Math.Min(10, (int)Math.Max(8f, item.Height - 2f))) : 12;
                            var pickBtn = CreateStyledButton(
                                toggleLabel,
                                contentW,
                                item.Height,
                                armed ? VioletSoft : BtnBg,
                                armed ? WithAlpha(Violet, 0.55f) : HairlineStrong,
                                armed ? Violet : TextPrimary,
                                () => VirtualBoardPickToggled?.Invoke(capturedOwnerId, !armed, matchGen, layoutGen),
                                fontSize: btnFont,
                                radius: RadiusCard);
                            SetButtonEnabled(pickBtn, hasLivingSelected);
                            pickBtn.SetXY(0f, y);
                            _contentHost.AddChild(pickBtn);
                        }
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
                                verticalAlign = VertAlignType.Middle,
                                textFormat = new TextFormat { size = item.Height < 14f ? Math.Max(8, (int)item.Height) : 12, bold = true, color = TextSecondary },
                                text = contentW < 100f
                                    ? (selectedPlayer.SeatTag + "#" + effLand)
                                    : (selectedPlayer.SeatTag + " 当前格 #" + effLand + (neighbors.Count > 0 ? " · 相邻格" : "（无相邻格）"))
                            };
                            nbTitle.SetXY(2f, y);
                            nbTitle.SetSize(Math.Max(0f, contentW - 4f), item.Height);
                            _contentHost.AddChild(nbTitle);
                        }
                        break;
                    }

                    case PopupPagination.VirtualRowKind.NeighborRow:
                    {
                        if (hasLivingSelected && neighbors.Count > 0)
                        {
                            int cols = Math.Max(1, neighborCols);
                            float nbGap = contentW < 100f ? 2f : 4f;
                            float nbW = (contentW - nbGap * (cols - 1)) / cols;
                            long capturedPid = selectedPlayer.PlayerId;
                            int rowCount = Math.Min(item.Count, neighbors.Count - item.StartIndex);
                            int nbFont = (nbW < 54f || item.Height < 15f) ? Math.Max(8, Math.Min(10, (int)Math.Max(8f, item.Height - 2f))) : 12;
                            for (int c = 0; c < rowCount; c++)
                            {
                                int targetTile = neighbors[item.StartIndex + c];
                                bool isRealTile = targetTile == selectedPlayer.LandId;
                                string label = nbW < 54f
                                    ? ((isRealTile ? "回" : "→") + targetTile)
                                    : (isRealTile ? ("回 #" + targetTile) : ("→ #" + targetTile));
                                var stepBtn = CreateStyledButton(
                                    label,
                                    nbW,
                                    item.Height,
                                    isRealTile ? AccentSoft : new Color(1f, 1f, 1f, 0.04f),
                                    isRealTile ? WithAlpha(Accent, 0.60f) : WithAlpha(Violet, 0.45f),
                                    isRealTile ? Accent : TextPrimary,
                                    () => VirtualStepNeighborRequested?.Invoke(capturedPid, effLand, targetTile, matchGen, layoutGen),
                                    fontSize: nbFont,
                                    radius: RadiusButton);
                                stepBtn.SetXY(c * (nbW + nbGap), y);
                                _contentHost.AddChild(stepBtn);
                            }
                        }
                        break;
                    }

                    case PopupPagination.VirtualRowKind.ActionsRow:
                    {
                        int count = item.Count > 0 ? item.Count : 3;
                        int start = item.Count > 0 ? item.StartIndex : 0;
                        float aGap = contentW < 100f ? 2f : 4f;
                        float aW = (contentW - aGap * (count - 1)) / count;
                        long selId = sandbox.SelectedPlayerId;
                        bool canUndo = sandbox.CanUndo(selId);
                        bool selHasState = sandbox.HasPlayerState(selId);
                        bool anyState = sandbox.HasAnyState;
                        int aFont = (aW < 54f || item.Height < 15f) ? Math.Max(8, Math.Min(10, (int)Math.Max(8f, item.Height - 2f))) : 12;

                        for (int c = 0; c < count; c++)
                        {
                            int actionIdx = start + c;
                            GComponent btn;
                            if (actionIdx == 0)
                            {
                                btn = CreateGhostButton(
                                    aW < 50f ? "撤回" : "撤回一步",
                                    aW, item.Height,
                                    () => VirtualUndoRequested?.Invoke(selId, matchGen, layoutGen),
                                    fontSize: aFont,
                                    textColor: Accent);
                                SetButtonEnabled(btn, canUndo);
                            }
                            else if (actionIdx == 1)
                            {
                                btn = CreateGhostButton(
                                    aW < 50f ? "重置" : "重置角色",
                                    aW, item.Height,
                                    () => VirtualResetPlayerRequested?.Invoke(selId, matchGen, layoutGen),
                                    fontSize: aFont,
                                    textColor: Gold);
                                SetButtonEnabled(btn, selHasState);
                            }
                            else
                            {
                                btn = CreateStyledButton(
                                    aW < 50f ? "全复" : "全部还原",
                                    aW, item.Height,
                                    VioletSoft,
                                    WithAlpha(Violet, 0.55f),
                                    Violet,
                                    () => VirtualResetAllRequested?.Invoke(matchGen, layoutGen),
                                    fontSize: aFont,
                                    radius: RadiusButton);
                                SetButtonEnabled(btn, anyState);
                            }
                            btn.SetXY(c * (aW + aGap), y);
                            _contentHost.AddChild(btn);
                        }
                        break;
                    }
                }

                y += item.Height;
            }

            return Math.Min(maxH, Math.Max(0f, y));
        }

        private static GComponent BuildSwitchRow(string label, bool on, bool enabled, float w, float h, Action onClick)
        {
            var row = new GComponent { touchable = enabled, opaque = true };
            row.SetSize(w, h);
            if (!enabled) row.alpha = 0.45f;

            Color fill = on ? VioletSoft : Surface2;
            Color border = on ? WithAlpha(Violet, 0.50f) : Hairline;
            Color hoverFill = on ? Lighten(VioletSoft, 0.05f) : Surface2Hover;
            Color hoverBorder = on ? WithAlpha(Violet, 0.75f) : HairlineStrong;

            var bg = new GGraph { touchable = false };
            DrawRound(bg, w, h, RadiusCard, fill, border);
            row.AddChild(bg);

            var lbl = new GTextField
            {
                touchable = false,
                autoSize = AutoSizeType.None,
                singleLine = true,
                verticalAlign = VertAlignType.Middle,
                textFormat = new TextFormat
                {
                    size = 12,
                    bold = on,
                    color = enabled ? (on ? Violet : TextPrimary) : TextDim
                },
                text = label ?? string.Empty
            };
            lbl.SetXY(10f, 0f);
            lbl.SetSize(Math.Max(10f, w - 52f), h);
            row.AddChild(lbl);

            float trackW = 30f;
            float trackH = 16f;
            float trackX = w - 40f;
            float trackY = (h - trackH) * 0.5f;
            var track = new GGraph { touchable = false };
            track.SetXY(trackX, trackY);
            DrawRound(track, trackW, trackH, 8f, on ? WithAlpha(Violet, 0.90f) : new Color(1f, 1f, 1f, 0.14f), Color.clear);
            row.AddChild(track);

            var knob = new GGraph { touchable = false };
            knob.SetXY(trackX + (on ? 16f : 2f), trackY + 2f);
            DrawCircle(knob, 12f, on ? Color.white : TextSecondary);
            row.AddChild(knob);

            if (enabled)
            {
                row.onRollOver.Add(() => DrawRound(bg, w, h, RadiusCard, hoverFill, hoverBorder));
                row.onRollOut.Add(() => DrawRound(bg, w, h, RadiusCard, fill, border));
                row.onClick.Add(() => onClick?.Invoke());
            }
            return row;
        }

        private static void DrawPlayerRowBg(GGraph bg, float w, float h, bool isSelected, bool hasVirt, bool isAlive, bool hovered)
        {
            if (bg == null || bg.isDisposed) return;
            Color fill;
            Color border;
            if (isSelected)
            {
                fill = hovered ? WithAlpha(Gold, 0.20f) : GoldSoft;
                border = WithAlpha(Gold, 0.75f);
            }
            else if (hasVirt)
            {
                fill = hovered ? Surface2Hover : Surface2;
                border = WithAlpha(Violet, 0.45f);
            }
            else if (!isAlive)
            {
                fill = RowActedBg;
                border = Hairline;
            }
            else
            {
                fill = hovered ? Surface2Hover : Surface2;
                border = Hairline;
            }
            DrawRound(bg, w, h, RadiusCard, fill, border);
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
            DrawPlayerRowBg(bg, rowW, rowH, isSelected, hasVirt, player.IsAlive, hovered: false);
            row.AddChild(bg);

            if (rowW >= 200f && rowH >= 22f)
            {
                float chipW = 26f;
                float chipH = 18f;
                float chipY = (rowH - chipH) * 0.5f;
                Color chipFill = isSelected ? GoldSoft : (hasVirt ? VioletSoft : new Color(1f, 1f, 1f, 0.06f));
                Color chipTextCol = isSelected ? Gold : (hasVirt ? Violet : TextSecondary);

                var seatBg = new GGraph { touchable = false };
                seatBg.SetXY(8f, chipY);
                DrawRound(seatBg, chipW, chipH, RadiusChip, chipFill, Color.clear);
                row.AddChild(seatBg);

                var seatTf = new GTextField
                {
                    touchable = false,
                    autoSize = AutoSizeType.None,
                    singleLine = true,
                    align = AlignType.Center,
                    verticalAlign = VertAlignType.Middle,
                    textFormat = new TextFormat { size = 11, bold = true, color = chipTextCol },
                    text = player.SeatTag ?? string.Empty
                };
                seatTf.SetXY(8f, chipY);
                seatTf.SetSize(chipW, chipH);
                row.AddChild(seatTf);

                string baseName = string.IsNullOrEmpty(player.Name) ? player.SeatTag : player.Name;
                string pName = isLocal ? (baseName + " · 我") : baseName;
                float rightW = Math.Max(64f, rowW * 0.36f);
                float rightX = rowW - 8f - rightW;
                float nameX = 42f;
                float nameW = Math.Max(20f, rightX - nameX - 4f);

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
                        color = !player.IsAlive ? TextDim : TextPrimary
                    },
                    text = pName
                };
                nameTf.SetXY(nameX, 0f);
                nameTf.SetSize(nameW, rowH);
                row.AddChild(nameTf);

                string posText = !player.IsAlive
                    ? "倒地"
                    : (hasVirt ? ("格 " + player.LandId + " → 虚 " + virtLand) : ("格 " + player.LandId));
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
                        color = !player.IsAlive ? Danger : (hasVirt ? Violet : TextSecondary)
                    },
                    text = posText
                };
                posTf.SetXY(rightX, 0f);
                posTf.SetSize(rightW, rowH);
                row.AddChild(posTf);
            }
            else
            {
                PopupPagination.ComputeSplitRowGeometry(rowW, 6f, 0.52f, out float leftX, out float leftW, out float rightX, out float rightW);
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
                        size = rowH < 18f ? Math.Max(8, (int)Math.Max(8f, rowH - 2f)) : 12,
                        bold = isSelected || isLocal,
                        color = !player.IsAlive ? TextDim : (isSelected ? Gold : TextPrimary)
                    },
                    text = (isSelected ? "▶ " : "  ") + pName
                };
                nameTf.SetXY(leftX, 0f);
                nameTf.SetSize(leftW, rowH);
                row.AddChild(nameTf);

                if (rightW >= 14f)
                {
                    string posText = !player.IsAlive
                        ? "倒地"
                        : (hasVirt ? ("格" + player.LandId + "→虚#" + virtLand) : ("格" + player.LandId));
                    var posTf = new GTextField
                    {
                        touchable = false,
                        autoSize = AutoSizeType.None,
                        singleLine = true,
                        align = AlignType.Right,
                        verticalAlign = VertAlignType.Middle,
                        textFormat = new TextFormat
                        {
                            size = rowH < 18f ? Math.Max(8, (int)Math.Max(8f, rowH - 2f)) : 11,
                            bold = hasVirt,
                            color = !player.IsAlive ? Danger : (hasVirt ? Violet : TextSecondary)
                        },
                        text = posText
                    };
                    posTf.SetXY(rightX, 0f);
                    posTf.SetSize(rightW, rowH);
                    row.AddChild(posTf);
                }
            }

            if (player.IsAlive)
            {
                long capturedId = player.PlayerId;
                int capturedMatchGen = _currentMatchGen;
                int capturedLayoutGen = sandbox.LayoutGeneration;
                row.onRollOver.Add(() => DrawPlayerRowBg(bg, rowW, rowH, isSelected, hasVirt, player.IsAlive, hovered: true));
                row.onRollOut.Add(() => DrawPlayerRowBg(bg, rowW, rowH, isSelected, hasVirt, player.IsAlive, hovered: false));
                row.onClick.Add(() => VirtualPlayerSelected?.Invoke(capturedId, capturedMatchGen, capturedLayoutGen));
            }
            return row;
        }
    }
}
