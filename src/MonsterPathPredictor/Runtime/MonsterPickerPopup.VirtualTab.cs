using System;
using System.Collections.Generic;
using FairyGUI;
using UnityEngine;
using MonsterPathPredictor.Core;

namespace MonsterPathPredictor.Runtime
{
    internal sealed partial class MonsterPickerPopup
    {
        private static int ComputeVirtualTabPageCount(float stageHeight)
        {
            // Full single-page virtual tab needs ~320 px content + ~114 px chrome = ~434 px.
            return stageHeight >= 435f ? 1 : 2;
        }

        private float BuildVirtualTab(
            IReadOnlyList<TrackedPlayer> players,
            BoardGraph board,
            VirtualPlayerSandbox sandbox,
            long localPlayerId,
            CompactLayout layout,
            float contentW,
            int pageIndex,
            int totalPages)
        {
            float y = 0f;
            float maxH = layout.MaxContentHeight;

            if (players == null || players.Count == 0 || sandbox == null)
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

            bool showPlayersSection = totalPages == 1 || pageIndex == 0;
            bool showMoveSection = totalPages == 1 || pageIndex == 1;
            float btnH = maxH < 240f ? 22f : 26f;

            if (showPlayersSection)
            {
                if (maxH >= 150f)
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
                    hint.SetSize(contentW - 4f, 18f);
                    _contentHost.AddChild(hint);
                    y += 20f;
                }

                float pRowH = maxH < 200f ? 22f : 28f;
                float pGap = 3f;
                for (int i = 0; i < players.Count; i++)
                {
                    var p = players[i];
                    if (p == null) continue;
                    bool isSel = p.PlayerId == sandbox.SelectedPlayerId;
                    if (y + pRowH > maxH) break;

                    var row = BuildVirtualPlayerRow(p, sandbox, localPlayerId, isSel, contentW, pRowH);
                    row.SetXY(0f, y);
                    _contentHost.AddChild(row);
                    y += pRowH + pGap;
                }

                if (y + btnH <= maxH)
                {
                    y += 3f;
                    bool armed = sandbox.BoardPickArmed;
                    string toggleLabel = armed
                        ? "● 右键点击棋盘任意格移位：已开启"
                        : "○ 开启「右键点击棋盘格」快速移位";
                    var pickBtn = CreateStyledButton(
                        toggleLabel,
                        contentW,
                        btnH,
                        armed ? BtnVioletBg : BtnBg,
                        armed ? BtnVioletBorder : new Color(0.35f, 0.45f, 0.60f, 0.8f),
                        armed ? AccentGold : TextPrimary,
                        () => VirtualBoardPickToggled?.Invoke(!armed),
                        fontSize: 12);
                    pickBtn.SetXY(0f, y);
                    _contentHost.AddChild(pickBtn);
                    y += btnH + 6f;
                }
            }

            if (showMoveSection)
            {
                // Reserve space for the bottom action bar first so complete neighbor rows never push Undo/Reset off-panel.
                float actionReserve = btnH + 6f;
                float nbLimitY = Math.Max(y, maxH - actionReserve);

                if (selectedPlayer != null && selectedPlayer.IsAlive && board != null && y + btnH <= nbLimitY)
                {
                    int effLand = sandbox.GetEffectiveLandId(selectedPlayer);
                    var neighbors = VirtualPlayerSandbox.GetReachableNeighbors(board, effLand);

                    if (y + 19f + btnH <= nbLimitY)
                    {
                        var nbTitle = new GTextField
                        {
                            touchable = false,
                            autoSize = AutoSizeType.None,
                            singleLine = true,
                            textFormat = new TextFormat { size = 12, bold = true, color = AccentCyan },
                            text = selectedPlayer.SeatTag + " 当前格#" + effLand + "，点相邻格移动："
                        };
                        nbTitle.SetXY(2f, y);
                        nbTitle.SetSize(contentW - 4f, 18f);
                        _contentHost.AddChild(nbTitle);
                        y += 19f;
                    }

                    if (neighbors.Count > 0)
                    {
                        int cols = contentW < 180f ? 2 : Math.Min(3, Math.Max(1, neighbors.Count));
                        float nbGap = 4f;
                        float nbW = (contentW - nbGap * (cols - 1)) / cols;
                        long capturedPid = selectedPlayer.PlayerId;
                        int totalRows = (neighbors.Count + cols - 1) / cols;

                        for (int r = 0; r < totalRows; r++)
                        {
                            if (y + btnH > nbLimitY) break;
                            int rowStart = r * cols;
                            int rowCount = Math.Min(cols, neighbors.Count - rowStart);
                            for (int c = 0; c < rowCount; c++)
                            {
                                int targetTile = neighbors[rowStart + c];
                                bool isRealTile = targetTile == selectedPlayer.LandId;
                                string label = isRealTile ? ("回格#" + targetTile) : ("走→#" + targetTile);
                                var stepBtn = CreateStyledButton(
                                    label,
                                    nbW,
                                    btnH,
                                    isRealTile ? BtnActiveBg : BtnBg,
                                    isRealTile ? AccentCyan : BtnVioletBorder,
                                    isRealTile ? AccentCyan : TextPrimary,
                                    () => VirtualMoveRequested?.Invoke(capturedPid, targetTile),
                                    fontSize: 12);
                                stepBtn.SetXY(c * (nbW + nbGap), y);
                                _contentHost.AddChild(stepBtn);
                            }
                            y += btnH + 4f;
                        }
                        y += 2f;
                    }
                }

                // Action bar: Undo | Reset Selected | Reset All (touchable & visual state strictly synced to HasPlayerState / HasAnyState)
                if (y + btnH <= maxH)
                {
                    float aGap = 4f;
                    float aW = (contentW - aGap * 2f) / 3f;
                    long selId = sandbox.SelectedPlayerId;
                    bool canUndo = sandbox.CanUndo(selId);
                    bool selHasState = sandbox.HasPlayerState(selId);
                    bool anyState = sandbox.HasAnyState;

                    var undoBtn = CreateStyledButton("撤回一步", aW, btnH, BtnBg, canUndo ? AccentCyan : new Color(0.28f, 0.34f, 0.42f, 0.6f), canUndo ? TextPrimary : TextMuted, () => VirtualUndoRequested?.Invoke(selId), fontSize: 12);
                    undoBtn.touchable = canUndo;
                    undoBtn.SetXY(0f, y);
                    _contentHost.AddChild(undoBtn);

                    var resetOneBtn = CreateStyledButton("重置角色", aW, btnH, BtnBg, selHasState ? AccentGold : new Color(0.28f, 0.34f, 0.42f, 0.6f), selHasState ? AccentGold : TextMuted, () => VirtualResetPlayerRequested?.Invoke(selId), fontSize: 12);
                    resetOneBtn.touchable = selHasState;
                    resetOneBtn.SetXY(aW + aGap, y);
                    _contentHost.AddChild(resetOneBtn);

                    var resetAllBtn = CreateStyledButton("全部还原", aW, btnH, anyState ? BtnVioletBg : BtnBg, anyState ? BtnVioletBorder : new Color(0.28f, 0.34f, 0.42f, 0.6f), anyState ? AccentViolet : TextMuted, () => VirtualResetAllRequested?.Invoke(), fontSize: 12);
                    resetAllBtn.touchable = anyState;
                    resetAllBtn.SetXY((aW + aGap) * 2f, y);
                    _contentHost.AddChild(resetAllBtn);

                    y += btnH + 4f;
                }
            }

            return Math.Min(maxH, Math.Max(32f, y));
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
