using System;
using System.Collections.Generic;
using System.Text;
using FairyGUI;
using UnityEngine;
using MonsterPathPredictor.Core;

namespace MonsterPathPredictor.Runtime
{
    internal sealed partial class MonsterPickerPopup
    {
        private void EnsureCreated()
        {
            if (_root != null && !_root.isDisposed && _root.parent == GRoot.inst && _footerHost != null && !_footerHost.isDisposed)
                return;
            Dispose();
            if (GRoot.inst == null) return;

            try
            {
            _root = new GComponent
            {
                name = "MonsterPathPredictor.PickerPopup",
                touchable = true,
                opaque = true,
                draggable = true,
                sortingOrder = 2200,
                visible = IsOpen
            };
            _root.onTouchBegin.Add(OnPopupTouchBegin);
            if (Stage.inst != null)
            {
                _hookedStage = Stage.inst;
                _hookedStage.onTouchEnd.Add(OnStageTouchEnd);
            }
            GRoot.inst.AddChild(_root);

            _panelBg = new GGraph { touchable = false };
            _root.AddChild(_panelBg);

            _headerBg = new GGraph { touchable = false };
            _headerBg.SetSize(PanelWidth, HeaderHeight);
            _headerBg.DrawRect(PanelWidth, HeaderHeight, 0, Color.clear, HeaderBg);
            _root.AddChild(_headerBg);

            _titleText = new GTextField
            {
                touchable = false,
                autoSize = AutoSizeType.None,
                singleLine = true,
                textFormat = new TextFormat { size = 15, bold = true, color = AccentCyan }
            };
            _titleText.SetXY(10f, 7f);
            _titleText.SetSize(PanelWidth - 52f, 20f);
            _root.AddChild(_titleText);

            _closeBtn = CreateMiniButton("×", 28f, 24f, () => Hide());
            _closeBtn.SetXY(PanelWidth - 36f, 5f);
            _root.AddChild(_closeBtn);

            _tabHost = new GComponent { touchable = true };
            _tabHost.SetSize(PanelWidth, TabBarHeight);
            _tabMonstersBtn = CreateMiniButton("怪物列表", 96f, 24f, () => SwitchTab(PickerPopupTab.Monsters));
            _tabVirtualBtn = CreateMiniButton("虚拟走位", 96f, 24f, () => SwitchTab(PickerPopupTab.VirtualMove));
            _tabStyleBtn = CreateMiniButton("路线样式", 96f, 24f, () => SwitchTab(PickerPopupTab.RouteStyle));
            _tabHost.AddChild(_tabMonstersBtn);
            _tabHost.AddChild(_tabVirtualBtn);
            _tabHost.AddChild(_tabStyleBtn);
            _root.AddChild(_tabHost);

            _contentHost = new GComponent { touchable = true };
            _root.AddChild(_contentHost);

            _pagerHost = new GComponent { touchable = true, visible = false };
            _pagerHost.SetSize(PanelWidth, 28f);
            _prevBtn = CreateMiniButton("◀", 28f, 22f, () => StepActivePage(-1));
            _prevBtn.SetXY(Padding, 3f);
            _pagerHost.AddChild(_prevBtn);
            _pageLabel = new GTextField
            {
                touchable = false,
                autoSize = AutoSizeType.None,
                align = AlignType.Center,
                textFormat = new TextFormat { size = 13, color = TextMuted }
            };
            _pageLabel.SetXY(44f, 5f);
            _pageLabel.SetSize(PanelWidth - 88f, 18f);
            _pagerHost.AddChild(_pageLabel);
            _nextBtn = CreateMiniButton("▶", 28f, 22f, () => StepActivePage(1));
            _nextBtn.SetXY(PanelWidth - Padding - 28f, 3f);
            _pagerHost.AddChild(_nextBtn);
            _root.AddChild(_pagerHost);

            _emptyText = new GTextField
            {
                touchable = false,
                autoSize = AutoSizeType.None,
                singleLine = true,
                textFormat = new TextFormat { size = 14, color = TextMuted },
                text = "当前棋盘上没有存活怪物"
            };
            _root.AddChild(_emptyText);

            _footerHost = new GComponent { touchable = true, opaque = true };
            _footerHost.SetSize(PanelWidth, FooterHeight);
            _footerBg = new GGraph { touchable = false };
            _footerBg.SetSize(PanelWidth, FooterHeight);
            _footerBg.DrawRect(PanelWidth, FooterHeight, 0, Color.clear, HeaderBg);
            _footerHost.AddChild(_footerBg);

            _minusBtn = CreateMiniButton("-", 26f, 24f, () => StepsDeltaRequested?.Invoke(-1));
            _footerHost.AddChild(_minusBtn);

            _stepsLabel = new GTextField
            {
                touchable = false,
                autoSize = AutoSizeType.None,
                align = AlignType.Center,
                textFormat = new TextFormat { size = 14, bold = true, color = TextPrimary }
            };
            _footerHost.AddChild(_stepsLabel);

            _plusBtn = CreateMiniButton("+", 26f, 24f, () => StepsDeltaRequested?.Invoke(1));
            _footerHost.AddChild(_plusBtn);

            _clearBtn = CreateMiniButton("清除预览", 76f, 24f, () => ClearRequested?.Invoke());
            _footerHost.AddChild(_clearBtn);

            _root.AddChild(_footerHost);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private static GComponent CreateMiniButton(string text, float w, float h, Action onClick, int fontSize = 13)
        {
            return CreateStyledButton(text, w, h, BtnBg, new Color(0.35f, 0.45f, 0.60f, 0.8f), TextPrimary, onClick, fontSize);
        }

        private static GComponent CreateStyledButton(
            string text,
            float w,
            float h,
            Color fill,
            Color border,
            Color textColor,
            Action onClick,
            int fontSize = 13)
        {
            var btn = new GComponent { touchable = true, opaque = true };
            btn.SetSize(w, h);

            var bg = new GGraph { touchable = false };
            bg.SetSize(w, h);
            bg.DrawRect(w, h, 1, border, fill);
            btn.AddChild(bg);

            var tf = new GTextField
            {
                touchable = false,
                autoSize = AutoSizeType.None,
                singleLine = true,
                align = AlignType.Center,
                verticalAlign = VertAlignType.Middle,
                textFormat = new TextFormat { size = fontSize, bold = true, color = textColor },
                text = text ?? string.Empty
            };
            tf.SetSize(w, h);
            btn.AddChild(tf);

            btn.onRollOver.Add(() => bg.DrawRect(btn.width, btn.height, 1, PanelBorder, BtnHoverBg));
            btn.onRollOut.Add(() => bg.DrawRect(btn.width, btn.height, 1, border, fill));
            btn.onClick.Add(() => onClick?.Invoke());
            return btn;
        }

        private static void ResizeMiniButton(GComponent btn, float w, float h)
        {
            if (btn == null || btn.isDisposed) return;
            btn.SetSize(w, h);
            if (btn.numChildren >= 1 && btn.GetChildAt(0) is GGraph bg)
            {
                bg.SetSize(w, h);
                bg.DrawRect(w, h, 1, new Color(0.35f, 0.45f, 0.60f, 0.8f), BtnBg);
            }
            if (btn.numChildren >= 2 && btn.GetChildAt(1) is GTextField tf)
            {
                tf.SetSize(w, h);
            }
        }

        private static void StyleTabButton(GComponent btn, string label, float w, float h, bool active, bool accentViolet)
        {
            if (btn == null || btn.isDisposed) return;
            btn.SetSize(w, h);
            Color fill = active ? (accentViolet ? BtnVioletBg : BtnActiveBg) : BtnBg;
            Color border = active ? (accentViolet ? BtnVioletBorder : AccentCyan) : (accentViolet ? BtnVioletBorder : new Color(0.35f, 0.45f, 0.60f, 0.8f));
            Color txtCol = active ? AccentGold : (accentViolet ? AccentViolet : TextPrimary);

            if (btn.numChildren >= 1 && btn.GetChildAt(0) is GGraph bg)
            {
                bg.SetSize(w, h);
                bg.DrawRect(w, h, active ? 2 : 1, border, fill);
                btn.onRollOver.Clear();
                btn.onRollOut.Clear();
                btn.onRollOver.Add(() => bg.DrawRect(btn.width, btn.height, active ? 2 : 1, PanelBorder, BtnHoverBg));
                btn.onRollOut.Add(() => bg.DrawRect(btn.width, btn.height, active ? 2 : 1, border, fill));
            }
            if (btn.numChildren >= 2 && btn.GetChildAt(1) is GTextField tf)
            {
                tf.SetSize(w, h);
                var fmt = tf.textFormat;
                fmt.size = w < 64f ? 11 : 12;
                fmt.color = txtCol;
                tf.textFormat = fmt;
                tf.text = label;
            }
        }

        private void ClampToStage()
        {
            if (_root == null || GRoot.inst == null) return;
            float maxX = Math.Max(0f, GRoot.inst.width - _root.width);
            float maxY = Math.Max(0f, GRoot.inst.height - _root.height);
            float x = Math.Max(0f, Math.Min(maxX, _root.x));
            float y = Math.Max(0f, Math.Min(maxY, _root.y));
            _root.SetXY(x, y);
        }

        private readonly struct CompactLayout
        {
            public readonly float HeaderH;
            public readonly float TabH;
            public readonly float FooterH;
            public readonly float PagerH;
            public readonly float Pad;
            public readonly float RowH;
            public readonly float RowGap;
            public readonly float MaxContentHeight;

            public CompactLayout(float headerH, float tabH, float footerH, float pagerH, float pad, float rowH, float rowGap, float maxContentHeight)
            {
                HeaderH = headerH;
                TabH = tabH;
                FooterH = footerH;
                PagerH = pagerH;
                Pad = pad;
                RowH = rowH;
                RowGap = rowGap;
                MaxContentHeight = maxContentHeight;
            }
        }

        private static CompactLayout ComputeCompactLayout(float stageHeight, bool showPager)
        {
            const float NormalPagerH = 28f;
            float normalPager = showPager ? NormalPagerH : 0f;
            float normalChrome = HeaderHeight + TabBarHeight + Padding * 2f + normalPager + FooterHeight;
            if (stageHeight >= normalChrome + RowHeight + 8f)
            {
                float maxContent = Math.Max(RowHeight + 8f, stageHeight - normalChrome);
                return new CompactLayout(HeaderHeight, TabBarHeight, FooterHeight, normalPager, Padding, RowHeight, 4f, maxContent);
            }

            float headerH = 22f;
            float tabH = 20f;
            float footerH = 24f;
            float pagerH = showPager ? 20f : 0f;
            float pad = 2f;
            float rowGap = 2f;
            float avail = stageHeight - (headerH + tabH + footerH + pagerH + pad * 2f);
            if (avail >= 22f)
            {
                float rowH = Math.Min(28f, Math.Max(18f, avail - rowGap));
                return new CompactLayout(headerH, tabH, footerH, pagerH, pad, rowH, rowGap, avail);
            }

            pad = 1f;
            rowGap = 1f;
            float desiredRowH = Math.Min(20f, Math.Max(12f, stageHeight * 0.25f));
            float listBudget = desiredRowH + rowGap;
            float chromeBudget = Math.Max(0f, stageHeight - listBudget - pad * 2f);
            float baseChrome = 15f + 15f + 18f + (showPager ? 14f : 0f);
            float chromeScale = baseChrome > 0f ? Math.Min(1f, chromeBudget / baseChrome) : 1f;
            headerH = 15f * chromeScale;
            tabH = 15f * chromeScale;
            footerH = 18f * chromeScale;
            pagerH = showPager ? 14f * chromeScale : 0f;
            float maxContentH = Math.Max(0f, stageHeight - headerH - tabH - footerH - pagerH - pad * 2f);
            float finalRowH = Math.Min(22f, Math.Max(10f, maxContentH - rowGap));
            return new CompactLayout(headerH, tabH, footerH, pagerH, pad, finalRowH, rowGap, maxContentH);
        }

        private void ApplyChromeLayout(CompactLayout layout, float panelW, float padX, int virtualOverrideCount)
        {
            _headerBg.SetSize(panelW, layout.HeaderH);
            _headerBg.DrawRect(panelW, layout.HeaderH, 0, Color.clear, HeaderBg);
            float closeW = panelW < 100f ? 14f : (panelW < 200f ? 22f : 28f);
            PopupPagination.ClampChildVertical(layout.HeaderH, 2f, Math.Min(24f, Math.Max(1f, layout.HeaderH - 4f)), out float closeY, out float closeH);
            float closeX = Math.Max(padX, panelW - padX - closeW);
            ResizeMiniButton(_closeBtn, closeW, closeH);
            _closeBtn.SetXY(closeX, closeY);
            float titleW = Math.Max(0f, closeX - padX - 4f);
            PopupPagination.ClampChildVertical(layout.HeaderH, (layout.HeaderH - 20f) * 0.5f, Math.Min(20f, layout.HeaderH), out float titleY, out float titleH);
            _titleText.SetXY(padX, titleY);
            _titleText.SetSize(titleW, titleH);
            _titleText.visible = titleW >= 12f && titleH >= 6f;

            // 3-tab bar directly under header — guaranteed strictly inside [padX, panelW - padX] and [0, layout.TabH].
            _tabHost.SetXY(0f, layout.HeaderH);
            _tabHost.SetSize(panelW, layout.TabH);
            PopupPagination.ComputeThreeTabGeometry(panelW, padX, out float tabW, out float tabGap);
            float desiredTabH = Math.Min(24f, PopupPagination.ComputeTabStripButtonHeight(layout.TabH));
            PopupPagination.ClampChildVertical(layout.TabH, (layout.TabH - desiredTabH) * 0.5f, desiredTabH, out float tabBtnY, out float tabBtnH);

            string monstersLabel = tabW < 42f ? "怪" : (panelW < 210f ? "怪物" : "怪物列表");
            string virtualLabel = tabW < 42f
                ? (virtualOverrideCount > 0 ? ("虚" + virtualOverrideCount) : "虚")
                : (panelW < 210f
                    ? (virtualOverrideCount > 0 ? "虚(" + virtualOverrideCount + ")" : "虚拟")
                    : (virtualOverrideCount > 0 ? ("虚拟(" + virtualOverrideCount + ")") : "虚拟走位"));
            string styleLabel = tabW < 42f ? "样" : (panelW < 210f ? "样式" : "路线样式");

            StyleTabButton(_tabMonstersBtn, monstersLabel, tabW, tabBtnH, _activeTab == PickerPopupTab.Monsters, accentViolet: false);
            StyleTabButton(_tabVirtualBtn, virtualLabel, tabW, tabBtnH, _activeTab == PickerPopupTab.VirtualMove, accentViolet: virtualOverrideCount > 0);
            StyleTabButton(_tabStyleBtn, styleLabel, tabW, tabBtnH, _activeTab == PickerPopupTab.RouteStyle, accentViolet: false);

            _tabMonstersBtn.SetXY(padX, tabBtnY);
            _tabVirtualBtn.SetXY(padX + tabW + tabGap, tabBtnY);
            _tabStyleBtn.SetXY(padX + (tabW + tabGap) * 2f, tabBtnY);

            if (layout.PagerH > 0f)
            {
                _pagerHost.SetSize(panelW, layout.PagerH);
                float usablePager = Math.Max(24f, panelW - padX * 2f);
                float pBtnW = Math.Min(panelW < 200f ? 24f : 28f, Math.Max(8f, usablePager * 0.28f));
                PopupPagination.ClampChildVertical(layout.PagerH, 1f, Math.Min(22f, Math.Max(1f, layout.PagerH - 2f)), out float pBtnY, out float pBtnH);
                ResizeMiniButton(_prevBtn, pBtnW, pBtnH);
                _prevBtn.SetXY(padX, pBtnY);
                float nextX = Math.Max(padX + pBtnW + 2f, panelW - padX - pBtnW);
                ResizeMiniButton(_nextBtn, pBtnW, pBtnH);
                _nextBtn.SetXY(nextX, pBtnY);
                float labelX = padX + pBtnW + 2f;
                PopupPagination.ClampChildVertical(layout.PagerH, (layout.PagerH - 18f) * 0.5f, Math.Min(18f, layout.PagerH), out float pLblY, out float pLblH);
                _pageLabel.SetXY(labelX, pLblY);
                _pageLabel.SetSize(Math.Max(0f, nextX - labelX - 2f), pLblH);
            }

            _footerHost.SetSize(panelW, layout.FooterH);
            _footerBg.SetSize(panelW, layout.FooterH);
            _footerBg.DrawRect(panelW, layout.FooterH, 0, Color.clear, HeaderBg);
            PopupPagination.ClampChildVertical(layout.FooterH, 1f, Math.Min(24f, Math.Max(1f, layout.FooterH - 2f)), out float fBtnY, out float fBtnH);
            PopupPagination.ComputeFooterGeometry(
                panelW, padX,
                out float minusX, out float stepBtnW,
                out float stepsX, out float stepsW,
                out float plusX,
                out float clearX, out float clearW);

            ResizeMiniButton(_minusBtn, stepBtnW, fBtnH);
            _minusBtn.SetXY(minusX, fBtnY);
            PopupPagination.ClampChildVertical(layout.FooterH, (layout.FooterH - 20f) * 0.5f, Math.Min(22f, layout.FooterH), out float sLblY, out float sLblH);
            _stepsLabel.SetXY(stepsX, sLblY);
            _stepsLabel.SetSize(stepsW, sLblH);
            ResizeMiniButton(_plusBtn, stepBtnW, fBtnH);
            _plusBtn.SetXY(plusX, fBtnY);
            ResizeMiniButton(_clearBtn, clearW, fBtnH);
            _clearBtn.SetXY(clearX, fBtnY);
        }

        private static int ComputeRowsPerPage(float stageHeight, bool hasVirtualBanner)
        {
            const float PagerReserve = 28f;
            const float MarginReserve = 28f;
            float bannerReserve = hasVirtualBanner ? 30f : 0f;
            float chrome = HeaderHeight + TabBarHeight + Padding + PagerReserve + Padding + FooterHeight + MarginReserve;
            float availList = stageHeight - chrome;
            if (availList >= RowHeight + 4f + bannerReserve)
            {
                availList -= bannerReserve;
            }
            if (availList <= RowHeight + 4f) return 1;
            int rows = (int)((availList - 4f) / (RowHeight + 4f));
            return Math.Max(1, Math.Min(MaxVisibleRows, rows));
        }

        private string BuildSignature(
            IReadOnlyList<LiveMonsterInfo> visibleMonsters,
            IReadOnlyList<TrackedPlayer> players,
            IReadOnlyList<int> virtualNeighbors,
            VirtualPlayerSandbox sandbox,
            long localPlayerId,
            long selectedId,
            PredictorSettings settings,
            int pageIndex,
            int totalPages,
            int rowsPerPage,
            float stageWidth,
            float stageHeight)
        {
            var sb = _sigBuilder;
            sb.Clear();
            sb.Append((int)_activeTab).Append('|')
              .Append(_currentMatchGen).Append('|')
              .Append((int)_selectedColorRole).Append('|')
              .Append(_styleSaveFailed ? 'E' : 'O').Append('|')
              .Append(selectedId).Append('|')
              .Append(settings.PreviewSteps).Append('|')
              .Append(settings.PickerHotkey).Append('|')
              .Append(settings.AllowNonAggressiveMonsters ? '1' : '0')
              .Append(settings.ClosePickerOnSelect ? '1' : '0').Append('|')
              .Append(pageIndex).Append('/').Append(totalPages).Append(':').Append(rowsPerPage)
              .Append('@').Append((int)Math.Round(stageWidth)).Append('x').Append((int)Math.Round(stageHeight)).Append('|');

            if (sandbox != null)
            {
                sb.Append("VS:").Append(sandbox.SelectedPlayerId).Append(':')
                  .Append(sandbox.BoardPickArmed ? '1' : '0').Append(':')
                  .Append(sandbox.OverrideCount).Append(':')
                  .Append(sandbox.HasAnyState ? '1' : '0').Append(':')
                  .Append(sandbox.CanUndo(sandbox.SelectedPlayerId) ? '1' : '0').Append('|');
            }

            if (_activeTab == PickerPopupTab.Monsters && visibleMonsters != null)
            {
                for (int i = 0; i < visibleMonsters.Count; i++)
                {
                    var st = visibleMonsters[i].State;
                    sb.Append(st.MonsterId).Append(':')
                      .Append(st.ActionOrderRank).Append(':')
                      .Append(st.IsCurrentlyActing ? 'C' : (st.HasActedThisRound ? 'D' : 'W')).Append(':')
                      .Append(st.DisplayName).Append(':')
                      .Append(st.CurrentLandId).Append(':')
                      .Append(st.HasHp ? st.Hp.ToString() : "?").Append('/').Append(st.MaxHp).Append(':')
                      .Append(st.ActionWeight).Append(':')
                      .Append(st.IsNormalAggressive ? 'A' : 'N').Append(';');
                }
            }
            else if (_activeTab == PickerPopupTab.VirtualMove && players != null)
            {
                sb.Append("L:").Append(localPlayerId).Append('|');
                for (int i = 0; i < players.Count; i++)
                {
                    var p = players[i];
                    if (p == null) continue;
                    int eff = sandbox != null ? sandbox.GetEffectiveLandId(p) : p.LandId;
                    sb.Append(p.PlayerId).Append(':')
                      .Append(p.Slot).Append(':')
                      .Append(p.Name).Append(':')
                      .Append(p.IsAlive ? '1' : '0').Append(':')
                      .Append(p.LandId).Append('>').Append(eff).Append(';');
                }
                if (virtualNeighbors != null && virtualNeighbors.Count > 0)
                {
                    sb.Append("NB:");
                    for (int i = 0; i < virtualNeighbors.Count; i++) sb.Append(virtualNeighbors[i]).Append(',');
                }
            }
            else if (_activeTab == PickerPopupTab.RouteStyle)
            {
                sb.Append("ST:")
                  .Append((int)Math.Round(settings.LineWidth * 10f)).Append(':')
                  .Append((int)Math.Round(settings.DashSpeed)).Append(':')
                  .Append((int)Math.Round(settings.DashLength)).Append(':')
                  .Append((int)Math.Round(settings.GapLength)).Append(':')
                  .Append((int)Math.Round(settings.HeightOffset * 100f)).Append(':')
                  .Append(settings.FontSize).Append(':')
                  .Append((int)settings.PostCombatMode).Append(':')
                  .Append(settings.ShowStepNumbers ? '1' : '0')
                  .Append(settings.ShowReasonTag ? '1' : '0')
                  .Append(settings.ShowDeterministicPercent ? '1' : '0').Append(':')
                  .Append(settings.DeterministicColorHex).Append(':')
                  .Append(settings.MajorBranchColorHex).Append(':')
                  .Append(settings.MinorBranchColorHex).Append(':')
                  .Append(settings.PostCombatColorHex).Append(':')
                  .Append(settings.GuideRedColorHex).Append(':')
                  .Append(settings.GuideBlueColorHex);
            }

            return sb.ToString();
        }

        public void Dispose()
        {
            _lastSignature = string.Empty;
            _positioned = false;
            _temporarilyHidden = false;
            _activePopupTouchIds.Clear();
            _mouseOriginatedTouchIds.Clear();
            _hasRenderedContent = false;
            _lastRefreshDataRev = -1;
            _lastRefreshLocalRev = -1;
            _monsterPageIndex = 0;
            _virtualPageIndex = 0;
            _stylePageIndex = 0;
            try
            {
                if (_hookedStage != null) _hookedStage.onTouchEnd.Remove(OnStageTouchEnd);
                if (Stage.inst != null && Stage.inst != _hookedStage) Stage.inst.onTouchEnd.Remove(OnStageTouchEnd);
            }
            catch { }
            _hookedStage = null;
            if (_root != null)
            {
                try { _root.onTouchBegin.Remove(OnPopupTouchBegin); } catch { }
                try { _root.Dispose(); } catch { }
                _root = null;
            }
            _panelBg = null;
            _headerBg = null;
            _titleText = null;
            _closeBtn = null;
            _tabHost = null;
            _tabMonstersBtn = null;
            _tabVirtualBtn = null;
            _tabStyleBtn = null;
            _contentHost = null;
            _pagerHost = null;
            _prevBtn = null;
            _nextBtn = null;
            _pageLabel = null;
            _footerHost = null;
            _footerBg = null;
            _minusBtn = null;
            _plusBtn = null;
            _clearBtn = null;
            _stepsLabel = null;
            _emptyText = null;
        }
    }
}
