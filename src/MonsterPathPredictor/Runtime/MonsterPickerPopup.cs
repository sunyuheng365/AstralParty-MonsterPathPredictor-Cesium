using System;
using System.Collections.Generic;
using FairyGUI;
using UnityEngine;
using MonsterPathPredictor.Core;

namespace MonsterPathPredictor.Runtime
{
    /// <summary>
    /// Draggable FairyGUI popup toggled by hotkey (default F8) to quickly lock onto any live monster
    /// on the PVE board, especially helpful when monsters stack or move quickly.
    /// Also includes live - / + buttons to adjust forecast steps X and quick clear/close controls.
    /// </summary>
    internal sealed class MonsterPickerPopup : IDisposable
    {
        private const float PanelWidth = 310f;
        private const float HeaderHeight = 36f;
        private const float FooterHeight = 36f;
        private const float RowHeight = 34f;
        private const float Padding = 8f;
        private const int MaxVisibleRows = 12;

        private static readonly Color PanelBg = new Color(0.07f, 0.10f, 0.15f, 0.92f);
        private static readonly Color PanelBorder = new Color(0.28f, 0.72f, 0.92f, 0.75f);
        private static readonly Color HeaderBg = new Color(0.11f, 0.16f, 0.24f, 0.96f);
        private static readonly Color RowNormalBg = new Color(0.12f, 0.16f, 0.22f, 0.85f);
        private static readonly Color RowHoverBg = new Color(0.18f, 0.26f, 0.36f, 0.95f);
        private static readonly Color RowSelectedBg = new Color(0.24f, 0.36f, 0.20f, 0.95f);
        private static readonly Color RowSelectedBorder = new Color(1.00f, 0.84f, 0.24f, 0.95f);
        private static readonly Color BtnBg = new Color(0.18f, 0.24f, 0.34f, 0.95f);
        private static readonly Color BtnHoverBg = new Color(0.26f, 0.36f, 0.50f, 1.00f);
        private static readonly Color AccentGold = new Color(1.00f, 0.86f, 0.30f, 1f);
        private static readonly Color AccentCyan = new Color(0.42f, 0.90f, 1.00f, 1f);
        private static readonly Color TextPrimary = new Color(0.95f, 0.97f, 1.00f, 1f);
        private static readonly Color TextMuted = new Color(0.68f, 0.76f, 0.86f, 1f);

        private GComponent _root;
        private GGraph _panelBg;
        private GGraph _headerBg;
        private GTextField _titleText;
        private GComponent _closeBtn;
        private GComponent _listHost;
        private GComponent _footerHost;
        private GGraph _footerBg;
        private GComponent _minusBtn;
        private GComponent _plusBtn;
        private GComponent _clearBtn;
        private GComponent _pagerHost;
        private GComponent _prevBtn;
        private GComponent _nextBtn;
        private GTextField _pageLabel;
        private GTextField _stepsLabel;
        private GTextField _emptyText;

        private bool _isOpen;
        private bool _temporarilyHidden;
        private bool _positioned;
        private bool _closeOnSelect;
        private int _pageIndex;
        private string _lastSignature = string.Empty;

        public bool IsOpen => _isOpen && !_temporarilyHidden;

        public event Action<long> MonsterSelected;
        public event Action ClearRequested;
        public event Action<int> StepsDeltaRequested;

        public void Toggle()
        {
            if (_isOpen) Hide();
            else Show();
        }

        public void Show()
        {
            _isOpen = true;
            _temporarilyHidden = false;
            _lastSignature = string.Empty;
            EnsureCreated();
            if (_root != null)
            {
                _root.visible = true;
                GRoot.inst.SetChildIndex(_root, GRoot.inst.numChildren - 1);
            }
        }

        public void Hide()
        {
            _isOpen = false;
            _temporarilyHidden = false;
            if (_root != null && !_root.isDisposed)
            {
                _root.visible = false;
            }
        }

        public void SetTemporarilyHidden(bool hidden)
        {
            if (_temporarilyHidden == hidden) return;
            _temporarilyHidden = hidden;
            if (_root != null && !_root.isDisposed)
            {
                _root.visible = _isOpen && !_temporarilyHidden;
            }
        }

        public bool ContainsStagePoint(Vector2 stagePos)
        {
            if (!IsOpen || _root == null || _root.isDisposed || !_root.visible) return false;
            try
            {
                Vector2 local = _root.GlobalToLocal(stagePos);
                return local.x >= 0f && local.x <= _root.width && local.y >= 0f && local.y <= _root.height;
            }
            catch
            {
                return false;
            }
        }

        public void Refresh(
            IReadOnlyList<LiveMonsterInfo> monsters,
            long selectedMonsterId,
            PredictorSettings settings)
        {
            if (!IsOpen) return;
            EnsureCreated();
            if (_root == null || _root.isDisposed) return;

            _closeOnSelect = settings.ClosePickerOnSelect;

            var visible = new List<LiveMonsterInfo>();
            if (monsters != null)
            {
                for (int i = 0; i < monsters.Count; i++)
                {
                    var m = monsters[i];
                    if (m == null || m.State == null) continue;
                    if (!settings.AllowNonAggressiveMonsters && !m.State.IsNormalAggressive) continue;
                    visible.Add(m);
                }
            }

            // Dynamically fit panel width, rows per page, and compact chrome to current GRoot viewport
            // so header, rows, pager (including ▶), and footer controls remain reachable even on 240x79 viewports.
            float stageWidth = GRoot.inst != null && GRoot.inst.width > 0f ? GRoot.inst.width : PanelWidth;
            float stageHeight = GRoot.inst != null && GRoot.inst.height > 0f ? GRoot.inst.height : 720f;
            float panelW = Math.Min(PanelWidth, Math.Max(140f, stageWidth));
            float padX = panelW < 220f ? 4f : Padding;
            int rowsPerPage = ComputeRowsPerPage(stageHeight);
            int totalPages = Math.Max(1, (visible.Count + rowsPerPage - 1) / rowsPerPage);
            if (_pageIndex >= totalPages) _pageIndex = totalPages - 1;
            if (_pageIndex < 0) _pageIndex = 0;

            // Always re-clamp position to current GRoot bounds BEFORE signature early-return so shrinking the
            // game window immediately brings the popup back inside the visible viewport.
            if (_positioned) ClampToStage();

            string sig = BuildSignature(visible, selectedMonsterId, settings, _pageIndex, totalPages, rowsPerPage, stageWidth, stageHeight);
            if (sig == _lastSignature) return;
            _lastSignature = sig;

            bool showPager = totalPages > 1;
            var layout = ComputeCompactLayout(stageHeight, showPager);

            _titleText.text = "怪物寻路选择 [" + settings.PickerHotkey + "]";
            _stepsLabel.text = "延伸 " + settings.PreviewSteps + " 格";
            ApplyChromeLayout(layout, panelW, padX);

            _listHost.RemoveChildren(0, -1, true);

            float rowW = Math.Max(40f, panelW - padX * 2f);
            float listHeight;
            if (visible.Count == 0)
            {
                listHeight = Math.Min(42f, layout.MaxListHeight);
                _emptyText.visible = listHeight >= 18f;
                _emptyText.SetXY(padX, layout.HeaderH + Math.Max(2f, (listHeight - 18f) * 0.5f));
                _emptyText.SetSize(rowW, 20f);
            }
            else
            {
                _emptyText.visible = false;
                int start = _pageIndex * rowsPerPage;
                int count = Math.Min(rowsPerPage, visible.Count - start);
                float rowStep = layout.RowH + layout.RowGap;
                listHeight = Math.Min(layout.MaxListHeight, count * rowStep + layout.RowGap);
                bool showRows = layout.RowH >= 10f && listHeight >= layout.RowH;
                _listHost.visible = showRows;
                if (showRows)
                {
                    int fitRows = Math.Max(1, Math.Min(count, (int)((listHeight + layout.RowGap) / rowStep)));
                    for (int i = 0; i < fitRows; i++)
                    {
                        var row = BuildMonsterRow(visible[start + i], selectedMonsterId, layout.RowH, rowW);
                        row.SetXY(0f, i * rowStep);
                        _listHost.AddChild(row);
                    }
                    listHeight = Math.Min(layout.MaxListHeight, fitRows * rowStep + layout.RowGap);
                }
            }

            if (_pagerHost != null)
            {
                _pagerHost.visible = showPager && layout.PagerH > 0f;
                if (_pagerHost.visible)
                {
                    _pageLabel.text = (_pageIndex + 1) + " / " + totalPages + " (" + visible.Count + "只)";
                    _pagerHost.SetXY(0f, layout.HeaderH + layout.Pad + listHeight);
                }
            }

            float totalHeight = layout.HeaderH + layout.Pad + listHeight + (showPager ? layout.PagerH : 0f) + layout.Pad + layout.FooterH;
            if (stageHeight > 0f && totalHeight > stageHeight)
            {
                totalHeight = stageHeight;
            }
            _root.SetSize(panelW, totalHeight);
            _panelBg.SetSize(panelW, totalHeight);
            _panelBg.DrawRect(panelW, totalHeight, 2, PanelBorder, PanelBg);

            _listHost.SetXY(padX, layout.HeaderH + layout.Pad);
            _listHost.SetSize(rowW, Math.Max(0f, listHeight));
            _footerHost.SetXY(0f, Math.Max(layout.HeaderH, totalHeight - layout.FooterH));

            if (!_positioned && GRoot.inst != null)
            {
                _positioned = true;
                float x = Math.Max(0f, Math.Min(GRoot.inst.width - panelW, Math.Max(20f, GRoot.inst.width - panelW - 28f)));
                float y = Math.Max(0f, Math.Min(GRoot.inst.height - totalHeight, Math.Max(80f, (GRoot.inst.height - totalHeight) * 0.35f)));
                _root.SetXY(x, y);
            }
            ClampToStage();
        }

        private GComponent BuildMonsterRow(LiveMonsterInfo info, long selectedMonsterId, float rowHeight, float rowW)
        {
            var st = info.State;
            bool isSelected = st.MonsterId == selectedMonsterId;

            var row = new GComponent
            {
                touchable = true,
                opaque = true
            };
            row.SetSize(rowW, rowHeight);

            var bg = new GGraph { touchable = false };
            bg.SetSize(rowW, rowHeight);
            DrawRowBg(bg, rowW, rowHeight, isSelected, hovered: false);
            row.AddChild(bg);

            int nameFontSize = rowHeight < 30f ? 13 : 15;
            float nameY = Math.Max(1f, (rowHeight - 20f) * 0.5f);
            float nameW = Math.Max(56f, (rowW - 16f) * 0.52f);
            var nameTf = new GTextField
            {
                touchable = false,
                autoSize = AutoSizeType.None,
                singleLine = true,
                textFormat = new TextFormat
                {
                    size = nameFontSize,
                    bold = isSelected,
                    color = isSelected ? AccentGold : TextPrimary
                }
            };
            nameTf.SetXY(6f, nameY);
            nameTf.SetSize(nameW, Math.Min(22f, rowHeight - 2f));
            string prefix = isSelected ? "▶ " : "• ";
            nameTf.text = prefix + (!string.IsNullOrEmpty(st.DisplayName) ? st.DisplayName : st.Name);
            row.AddChild(nameTf);

            int metaFontSize = rowHeight < 30f ? 12 : 13;
            float metaY = Math.Max(1f, (rowHeight - 18f) * 0.5f);
            float metaX = 6f + nameW + 4f;
            float metaW = Math.Max(28f, rowW - metaX - 6f);
            var metaTf = new GTextField
            {
                touchable = false,
                autoSize = AutoSizeType.None,
                singleLine = true,
                align = AlignType.Right,
                textFormat = new TextFormat
                {
                    size = metaFontSize,
                    color = st.IsNormalAggressive ? TextMuted : AccentCyan
                }
            };
            metaTf.SetXY(metaX, metaY);
            metaTf.SetSize(metaW, Math.Min(20f, rowHeight - 2f));
            string hpPart = st.MaxHp > 0 ? ("HP " + st.Hp + "/" + st.MaxHp) : "";
            string tilePart = "#" + st.CurrentLandId;
            string notePart = !st.IsNormalAggressive ? " 非主动" : (st.ActionWeight > 0 ? (" 序" + st.ActionWeight) : "");
            metaTf.text = tilePart + (hpPart.Length > 0 ? "  " + hpPart : "") + notePart;
            row.AddChild(metaTf);

            long capturedId = st.MonsterId;
            row.onRollOver.Add(() => DrawRowBg(bg, rowW, rowHeight, isSelected, hovered: true));
            row.onRollOut.Add(() => DrawRowBg(bg, rowW, rowHeight, isSelected, hovered: false));
            row.onClick.Add(() =>
            {
                MonsterSelected?.Invoke(capturedId);
                if (_closeOnSelect) Hide();
            });

            return row;
        }

        private static void DrawRowBg(GGraph bg, float w, float h, bool selected, bool hovered)
        {
            if (bg == null || bg.isDisposed) return;
            if (selected)
                bg.DrawRect(w, h, 2, RowSelectedBorder, hovered ? RowHoverBg : RowSelectedBg);
            else
                bg.DrawRect(w, h, 1, new Color(0.24f, 0.30f, 0.40f, 0.65f), hovered ? RowHoverBg : RowNormalBg);
        }

        private void EnsureCreated()
        {
            if (_root != null && !_root.isDisposed && _root.parent == GRoot.inst) return;
            Dispose();
            if (GRoot.inst == null) return;

            _root = new GComponent
            {
                name = "MonsterPathPredictor.PickerPopup",
                touchable = true,
                opaque = true,
                draggable = true,
                sortingOrder = 2200,
                visible = IsOpen
            };
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
            _titleText.SetXY(10f, 8f);
            _titleText.SetSize(PanelWidth - 52f, 20f);
            _root.AddChild(_titleText);

            _closeBtn = CreateMiniButton("×", 28f, 24f, () => Hide());
            _closeBtn.SetXY(PanelWidth - 36f, 6f);
            _root.AddChild(_closeBtn);

            _listHost = new GComponent { touchable = true };
            _root.AddChild(_listHost);

            _pagerHost = new GComponent { touchable = true, visible = false };
            _pagerHost.SetSize(PanelWidth, 28f);
            _prevBtn = CreateMiniButton("◀", 28f, 22f, () => { _pageIndex = Math.Max(0, _pageIndex - 1); _lastSignature = string.Empty; });
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
            _nextBtn = CreateMiniButton("▶", 28f, 22f, () => { _pageIndex++; _lastSignature = string.Empty; });
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
            _minusBtn.SetXY(10f, 6f);
            _footerHost.AddChild(_minusBtn);

            _stepsLabel = new GTextField
            {
                touchable = false,
                autoSize = AutoSizeType.None,
                align = AlignType.Center,
                textFormat = new TextFormat { size = 14, bold = true, color = TextPrimary }
            };
            _stepsLabel.SetXY(40f, 8f);
            _stepsLabel.SetSize(82f, 22f);
            _footerHost.AddChild(_stepsLabel);

            _plusBtn = CreateMiniButton("+", 26f, 24f, () => StepsDeltaRequested?.Invoke(1));
            _plusBtn.SetXY(126f, 6f);
            _footerHost.AddChild(_plusBtn);

            _clearBtn = CreateMiniButton("清除预览", 76f, 24f, () => ClearRequested?.Invoke());
            _clearBtn.SetXY(PanelWidth - 86f, 6f);
            _footerHost.AddChild(_clearBtn);

            _root.AddChild(_footerHost);
        }

        private static GComponent CreateMiniButton(string text, float w, float h, Action onClick)
        {
            var btn = new GComponent { touchable = true, opaque = true };
            btn.SetSize(w, h);

            var bg = new GGraph { touchable = false };
            bg.SetSize(w, h);
            bg.DrawRect(w, h, 1, new Color(0.35f, 0.45f, 0.60f, 0.8f), BtnBg);
            btn.AddChild(bg);

            var tf = new GTextField
            {
                touchable = false,
                autoSize = AutoSizeType.None,
                align = AlignType.Center,
                verticalAlign = VertAlignType.Middle,
                textFormat = new TextFormat { size = 13, bold = true, color = TextPrimary },
                text = text
            };
            tf.SetSize(w, h);
            btn.AddChild(tf);

            btn.onRollOver.Add(() => bg.DrawRect(btn.width, btn.height, 1, PanelBorder, BtnHoverBg));
            btn.onRollOut.Add(() => bg.DrawRect(btn.width, btn.height, 1, new Color(0.35f, 0.45f, 0.60f, 0.8f), BtnBg));
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
            public readonly float FooterH;
            public readonly float PagerH;
            public readonly float Pad;
            public readonly float RowH;
            public readonly float RowGap;
            public readonly float MaxListHeight;

            public CompactLayout(float headerH, float footerH, float pagerH, float pad, float rowH, float rowGap, float maxListHeight)
            {
                HeaderH = headerH;
                FooterH = footerH;
                PagerH = pagerH;
                Pad = pad;
                RowH = rowH;
                RowGap = rowGap;
                MaxListHeight = maxListHeight;
            }
        }

        private static CompactLayout ComputeCompactLayout(float stageHeight, bool showPager)
        {
            const float NormalPagerH = 28f;
            float normalPager = showPager ? NormalPagerH : 0f;
            float normalMinTotal = HeaderHeight + Padding + (RowHeight + 4f + 4f) + normalPager + Padding + FooterHeight;
            if (stageHeight >= normalMinTotal)
            {
                float maxList = Math.Max(RowHeight + 8f, stageHeight - (HeaderHeight + Padding * 2f + normalPager + FooterHeight));
                return new CompactLayout(HeaderHeight, FooterHeight, normalPager, Padding, RowHeight, 4f, maxList);
            }

            // Ultra-short viewport compact layout (e.g. GRoot.height = 120 or 79):
            // ALWAYS reserve at least one clickable monster row FIRST, then fit header/pager/footer
            // cleanly around it so every monster remains selectable via pager AND footer stays in bounds.
            float headerH = 24f;
            float footerH = 26f;
            float pagerH = showPager ? 22f : 0f;
            float pad = 2f;
            float rowGap = 2f;
            float avail = stageHeight - (headerH + footerH + pagerH + pad * 2f);

            if (avail >= 24f)
            {
                float rowH = Math.Min(28f, Math.Max(20f, avail - rowGap));
                return new CompactLayout(headerH, footerH, pagerH, pad, rowH, rowGap, avail);
            }

            // Tier 2 ultra-short budget (e.g. stageHeight = 79 with pager):
            // guarantee 1 clickable row (~22 px at height=79), compacting chrome proportionally around it.
            pad = 1f;
            rowGap = 1f;
            float desiredRowH = Math.Min(22f, Math.Max(14f, stageHeight * 0.28f));
            float listBudget = desiredRowH + rowGap;
            float chromeBudget = Math.Max(0f, stageHeight - listBudget - pad * 2f);
            float baseChrome = 17f + 20f + (showPager ? 16f : 0f);
            float chromeScale = baseChrome > 0f ? Math.Min(1f, chromeBudget / baseChrome) : 1f;
            headerH = 17f * chromeScale;
            footerH = 20f * chromeScale;
            pagerH = showPager ? 16f * chromeScale : 0f;
            float maxListHeight = Math.Max(0f, stageHeight - headerH - footerH - pagerH - pad * 2f);
            float finalRowH = Math.Min(24f, Math.Max(10f, maxListHeight - rowGap));
            return new CompactLayout(headerH, footerH, pagerH, pad, finalRowH, rowGap, maxListHeight);
        }

        private void ApplyChromeLayout(CompactLayout layout, float panelW, float padX)
        {
            _headerBg.SetSize(panelW, layout.HeaderH);
            _headerBg.DrawRect(panelW, layout.HeaderH, 0, Color.clear, HeaderBg);
            float closeW = panelW < 200f ? 22f : 28f;
            float closeH = Math.Min(24f, Math.Max(16f, layout.HeaderH - 4f));
            float closeX = Math.Max(padX, panelW - padX - closeW);
            ResizeMiniButton(_closeBtn, closeW, closeH);
            _closeBtn.SetXY(closeX, Math.Max(1f, (layout.HeaderH - closeH) * 0.5f));
            _titleText.SetXY(padX, Math.Max(1f, (layout.HeaderH - 20f) * 0.5f));
            _titleText.SetSize(Math.Max(40f, closeX - padX - 4f), Math.Min(20f, layout.HeaderH - 2f));

            if (layout.PagerH > 0f)
            {
                _pagerHost.SetSize(panelW, layout.PagerH);
                float pBtnW = panelW < 200f ? 24f : 28f;
                float pBtnH = Math.Min(22f, Math.Max(14f, layout.PagerH - 4f));
                float pBtnY = Math.Max(1f, (layout.PagerH - pBtnH) * 0.5f);
                ResizeMiniButton(_prevBtn, pBtnW, pBtnH);
                _prevBtn.SetXY(padX, pBtnY);
                float nextX = Math.Max(padX + pBtnW + 4f, panelW - padX - pBtnW);
                ResizeMiniButton(_nextBtn, pBtnW, pBtnH);
                _nextBtn.SetXY(nextX, pBtnY);
                float labelX = padX + pBtnW + 4f;
                _pageLabel.SetXY(labelX, Math.Max(1f, (layout.PagerH - 18f) * 0.5f));
                _pageLabel.SetSize(Math.Max(24f, nextX - labelX - 4f), Math.Min(18f, layout.PagerH - 2f));
            }

            _footerHost.SetSize(panelW, layout.FooterH);
            _footerBg.SetSize(panelW, layout.FooterH);
            _footerBg.DrawRect(panelW, layout.FooterH, 0, Color.clear, HeaderBg);
            float fBtnH = Math.Min(24f, Math.Max(16f, layout.FooterH - 4f));
            float fBtnY = Math.Max(1f, (layout.FooterH - fBtnH) * 0.5f);
            float gap = panelW < 220f ? 3f : 4f;
            float stepBtnW = panelW < 200f ? 20f : 26f;
            float clearW = panelW < 200f ? 52f : (panelW < 260f ? 68f : 76f);
            float clearX = Math.Max(padX, panelW - padX - clearW);
            float minusX = padX;
            float stepsX = minusX + stepBtnW + gap;
            float maxStepsW = Math.Max(28f, clearX - gap - stepBtnW - gap - stepsX);
            float stepsW = Math.Min(82f, maxStepsW);
            float plusX = stepsX + stepsW + gap;

            ResizeMiniButton(_minusBtn, stepBtnW, fBtnH);
            _minusBtn.SetXY(minusX, fBtnY);
            _stepsLabel.SetXY(stepsX, Math.Max(1f, (layout.FooterH - 20f) * 0.5f));
            _stepsLabel.SetSize(stepsW, Math.Min(22f, layout.FooterH - 2f));
            ResizeMiniButton(_plusBtn, stepBtnW, fBtnH);
            _plusBtn.SetXY(plusX, fBtnY);
            ResizeMiniButton(_clearBtn, clearW, fBtnH);
            _clearBtn.SetXY(clearX, fBtnY);
        }

        private static int ComputeRowsPerPage(float stageHeight)
        {
            const float PagerReserve = 28f;
            const float MarginReserve = 24f;
            float chrome = HeaderHeight + Padding + PagerReserve + Padding + FooterHeight + MarginReserve;
            float availList = stageHeight - chrome;
            if (availList <= RowHeight + 4f) return 1;
            int rows = (int)((availList - 4f) / (RowHeight + 4f));
            return Math.Max(1, Math.Min(MaxVisibleRows, rows));
        }

        private static string BuildSignature(
            IReadOnlyList<LiveMonsterInfo> visibleMonsters,
            long selectedId,
            PredictorSettings settings,
            int pageIndex,
            int totalPages,
            int rowsPerPage,
            float stageWidth,
            float stageHeight)
        {
            var sb = new System.Text.StringBuilder(160);
            sb.Append(selectedId).Append('|')
              .Append(settings.PreviewSteps).Append('|')
              .Append(settings.PickerHotkey).Append('|')
              .Append(settings.AllowNonAggressiveMonsters ? '1' : '0')
              .Append(settings.ClosePickerOnSelect ? '1' : '0').Append('|')
              .Append(pageIndex).Append('/').Append(totalPages).Append(':').Append(rowsPerPage)
              .Append('@').Append((int)Math.Round(stageWidth)).Append('x').Append((int)Math.Round(stageHeight)).Append('|');
            if (visibleMonsters != null)
            {
                for (int i = 0; i < visibleMonsters.Count; i++)
                {
                    var st = visibleMonsters[i].State;
                    sb.Append(st.MonsterId).Append(':')
                      .Append(st.DisplayName).Append(':')
                      .Append(st.CurrentLandId).Append(':')
                      .Append(st.Hp).Append('/').Append(st.MaxHp).Append(':')
                      .Append(st.ActionWeight).Append(':')
                      .Append(st.IsNormalAggressive ? 'A' : 'N').Append(';');
                }
            }
            return sb.ToString();
        }

        public void Dispose()
        {
            _lastSignature = string.Empty;
            _positioned = false;
            _temporarilyHidden = false;
            _pageIndex = 0;
            if (_root != null)
            {
                try { _root.Dispose(); } catch { }
                _root = null;
            }
            _panelBg = null;
            _headerBg = null;
            _titleText = null;
            _closeBtn = null;
            _listHost = null;
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
