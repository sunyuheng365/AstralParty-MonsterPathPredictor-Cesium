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
        private GComponent _listHost;
        private GComponent _footerHost;
        private GComponent _pagerHost;
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

            // Dynamically fit rows per page to current GRoot viewport height so pager and footer controls
            // remain completely reachable even on short windows (e.g. height = 480).
            float stageHeight = GRoot.inst != null && GRoot.inst.height > 0f ? GRoot.inst.height : 720f;
            int rowsPerPage = ComputeRowsPerPage(stageHeight);
            int totalPages = Math.Max(1, (visible.Count + rowsPerPage - 1) / rowsPerPage);
            if (_pageIndex >= totalPages) _pageIndex = totalPages - 1;
            if (_pageIndex < 0) _pageIndex = 0;

            // Always re-clamp position to current GRoot bounds BEFORE signature early-return so shrinking the
            // game window immediately brings the popup back inside the visible viewport.
            if (_positioned) ClampToStage();

            string sig = BuildSignature(visible, selectedMonsterId, settings, _pageIndex, totalPages, rowsPerPage);
            if (sig == _lastSignature) return;
            _lastSignature = sig;

            _titleText.text = "怪物寻路选择 [" + settings.PickerHotkey + "]";
            _stepsLabel.text = "延伸 " + settings.PreviewSteps + " 格";

            _listHost.RemoveChildren(0, -1, true);

            float listHeight;
            if (visible.Count == 0)
            {
                listHeight = 42f;
                _emptyText.visible = true;
                _emptyText.SetXY(Padding, HeaderHeight + 12f);
            }
            else
            {
                _emptyText.visible = false;
                int start = _pageIndex * rowsPerPage;
                int count = Math.Min(rowsPerPage, visible.Count - start);
                listHeight = count * (RowHeight + 4f) + 4f;
                for (int i = 0; i < count; i++)
                {
                    var row = BuildMonsterRow(visible[start + i], selectedMonsterId);
                    row.SetXY(0f, i * (RowHeight + 4f));
                    _listHost.AddChild(row);
                }
            }

            bool showPager = totalPages > 1;
            float pagerHeight = showPager ? 28f : 0f;
            if (_pagerHost != null)
            {
                _pagerHost.visible = showPager;
                if (showPager)
                {
                    _pageLabel.text = (_pageIndex + 1) + " / " + totalPages + " (" + visible.Count + "只)";
                    _pagerHost.SetXY(0f, HeaderHeight + Padding + listHeight);
                }
            }

            float totalHeight = HeaderHeight + Padding + listHeight + pagerHeight + Padding + FooterHeight;
            _root.SetSize(PanelWidth, totalHeight);
            _panelBg.SetSize(PanelWidth, totalHeight);
            _panelBg.DrawRect(PanelWidth, totalHeight, 2, PanelBorder, PanelBg);

            _listHost.SetXY(Padding, HeaderHeight + Padding);
            _listHost.SetSize(PanelWidth - Padding * 2f, listHeight);
            _footerHost.SetXY(0f, totalHeight - FooterHeight);

            if (!_positioned && GRoot.inst != null)
            {
                _positioned = true;
                float x = Math.Max(20f, GRoot.inst.width - PanelWidth - 28f);
                float y = Math.Max(80f, (GRoot.inst.height - totalHeight) * 0.35f);
                _root.SetXY(x, y);
            }
            ClampToStage();
        }

        private GComponent BuildMonsterRow(LiveMonsterInfo info, long selectedMonsterId)
        {
            var st = info.State;
            bool isSelected = st.MonsterId == selectedMonsterId;
            float rowW = PanelWidth - Padding * 2f;

            var row = new GComponent
            {
                touchable = true,
                opaque = true
            };
            row.SetSize(rowW, RowHeight);

            var bg = new GGraph { touchable = false };
            bg.SetSize(rowW, RowHeight);
            DrawRowBg(bg, rowW, isSelected, hovered: false);
            row.AddChild(bg);

            var nameTf = new GTextField
            {
                touchable = false,
                autoSize = AutoSizeType.None,
                singleLine = true,
                textFormat = new TextFormat
                {
                    size = 15,
                    bold = isSelected,
                    color = isSelected ? AccentGold : TextPrimary
                }
            };
            nameTf.SetXY(8f, 7f);
            nameTf.SetSize(145f, 22f);
            string prefix = isSelected ? "▶ " : "• ";
            nameTf.text = prefix + (!string.IsNullOrEmpty(st.DisplayName) ? st.DisplayName : st.Name);
            row.AddChild(nameTf);

            var metaTf = new GTextField
            {
                touchable = false,
                autoSize = AutoSizeType.None,
                singleLine = true,
                align = AlignType.Right,
                textFormat = new TextFormat
                {
                    size = 13,
                    color = st.IsNormalAggressive ? TextMuted : AccentCyan
                }
            };
            metaTf.SetXY(152f, 8f);
            metaTf.SetSize(rowW - 160f, 20f);
            string hpPart = st.MaxHp > 0 ? ("HP " + st.Hp + "/" + st.MaxHp) : "";
            string tilePart = "#" + st.CurrentLandId;
            string notePart = !st.IsNormalAggressive ? " 非主动" : (st.ActionWeight > 0 ? (" 序" + st.ActionWeight) : "");
            metaTf.text = tilePart + (hpPart.Length > 0 ? "  " + hpPart : "") + notePart;
            row.AddChild(metaTf);

            long capturedId = st.MonsterId;
            row.onRollOver.Add(() => DrawRowBg(bg, rowW, isSelected, hovered: true));
            row.onRollOut.Add(() => DrawRowBg(bg, rowW, isSelected, hovered: false));
            row.onClick.Add(() =>
            {
                MonsterSelected?.Invoke(capturedId);
                if (_closeOnSelect) Hide();
            });

            return row;
        }

        private static void DrawRowBg(GGraph bg, float w, bool selected, bool hovered)
        {
            if (bg == null || bg.isDisposed) return;
            if (selected)
                bg.DrawRect(w, RowHeight, 2, RowSelectedBorder, hovered ? RowHoverBg : RowSelectedBg);
            else
                bg.DrawRect(w, RowHeight, 1, new Color(0.24f, 0.30f, 0.40f, 0.65f), hovered ? RowHoverBg : RowNormalBg);
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
                autoSize = AutoSizeType.Both,
                textFormat = new TextFormat { size = 15, bold = true, color = AccentCyan }
            };
            _titleText.SetXY(10f, 8f);
            _root.AddChild(_titleText);

            var closeBtn = CreateMiniButton("×", 28f, 24f, () => Hide());
            closeBtn.SetXY(PanelWidth - 36f, 6f);
            _root.AddChild(closeBtn);

            _listHost = new GComponent { touchable = true };
            _root.AddChild(_listHost);

            _pagerHost = new GComponent { touchable = true, visible = false };
            _pagerHost.SetSize(PanelWidth, 28f);
            var prevBtn = CreateMiniButton("◀", 28f, 22f, () => { _pageIndex = Math.Max(0, _pageIndex - 1); _lastSignature = string.Empty; });
            prevBtn.SetXY(Padding, 3f);
            _pagerHost.AddChild(prevBtn);
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
            var nextBtn = CreateMiniButton("▶", 28f, 22f, () => { _pageIndex++; _lastSignature = string.Empty; });
            nextBtn.SetXY(PanelWidth - Padding - 28f, 3f);
            _pagerHost.AddChild(nextBtn);
            _root.AddChild(_pagerHost);

            _emptyText = new GTextField
            {
                touchable = false,
                autoSize = AutoSizeType.Both,
                textFormat = new TextFormat { size = 14, color = TextMuted },
                text = "当前棋盘上没有存活怪物"
            };
            _root.AddChild(_emptyText);

            _footerHost = new GComponent { touchable = true, opaque = true };
            _footerHost.SetSize(PanelWidth, FooterHeight);
            var footerBg = new GGraph { touchable = false };
            footerBg.SetSize(PanelWidth, FooterHeight);
            footerBg.DrawRect(PanelWidth, FooterHeight, 0, Color.clear, HeaderBg);
            _footerHost.AddChild(footerBg);

            var minusBtn = CreateMiniButton("-", 26f, 24f, () => StepsDeltaRequested?.Invoke(-1));
            minusBtn.SetXY(10f, 6f);
            _footerHost.AddChild(minusBtn);

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

            var plusBtn = CreateMiniButton("+", 26f, 24f, () => StepsDeltaRequested?.Invoke(1));
            plusBtn.SetXY(126f, 6f);
            _footerHost.AddChild(plusBtn);

            var clearBtn = CreateMiniButton("清除预览", 76f, 24f, () => ClearRequested?.Invoke());
            clearBtn.SetXY(PanelWidth - 86f, 6f);
            _footerHost.AddChild(clearBtn);

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

            btn.onRollOver.Add(() => bg.DrawRect(w, h, 1, PanelBorder, BtnHoverBg));
            btn.onRollOut.Add(() => bg.DrawRect(w, h, 1, new Color(0.35f, 0.45f, 0.60f, 0.8f), BtnBg));
            btn.onClick.Add(() => onClick?.Invoke());
            return btn;
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
            int rowsPerPage)
        {
            var sb = new System.Text.StringBuilder(160);
            sb.Append(selectedId).Append('|')
              .Append(settings.PreviewSteps).Append('|')
              .Append(settings.PickerHotkey).Append('|')
              .Append(settings.AllowNonAggressiveMonsters ? '1' : '0')
              .Append(settings.ClosePickerOnSelect ? '1' : '0').Append('|')
              .Append(pageIndex).Append('/').Append(totalPages).Append(':').Append(rowsPerPage).Append('|');
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
            _listHost = null;
            _pagerHost = null;
            _pageLabel = null;
            _footerHost = null;
            _stepsLabel = null;
            _emptyText = null;
        }
    }
}
