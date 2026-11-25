using System;
using System.Collections.Generic;
using System.Text;
using FairyGUI;
using UnityEngine;
using MonsterPathPredictor.Core;

namespace MonsterPathPredictor.Runtime
{
    internal enum PickerPopupTab
    {
        Monsters = 0,
        VirtualMove = 1,
        RouteStyle = 2
    }

    /// <summary>
    /// Draggable 3-tab FairyGUI popup toggled by hotkey (default F10):
    ///   - Tab 0: Action-order sorted monster picker
    ///   - Tab 1: Virtual player position sandbox (what-if fork planning)
    ///   - Tab 2: Live in-game route style customizer
    /// </summary>
    internal sealed partial class MonsterPickerPopup : IDisposable
    {
        private const float PanelWidth = 344f;
        private const float HeaderHeight = 34f;
        private const float TabBarHeight = 28f;
        private const float FooterHeight = 36f;
        private const float RowHeight = 34f;
        private const float Padding = 8f;
        private const int MaxVisibleRows = 12;

        private static readonly Color PanelBg = new Color(0.07f, 0.10f, 0.15f, 0.93f);
        private static readonly Color PanelBorder = new Color(0.28f, 0.72f, 0.92f, 0.78f);
        private static readonly Color HeaderBg = new Color(0.11f, 0.16f, 0.24f, 0.96f);
        private static readonly Color RowNormalBg = new Color(0.12f, 0.16f, 0.22f, 0.86f);
        private static readonly Color RowHoverBg = new Color(0.18f, 0.26f, 0.36f, 0.95f);
        private static readonly Color RowSelectedBg = new Color(0.24f, 0.36f, 0.20f, 0.95f);
        private static readonly Color RowSelectedBorder = new Color(1.00f, 0.84f, 0.24f, 0.95f);
        private static readonly Color RowActedBg = new Color(0.10f, 0.13f, 0.18f, 0.72f);
        private static readonly Color BtnBg = new Color(0.18f, 0.24f, 0.34f, 0.95f);
        private static readonly Color BtnHoverBg = new Color(0.26f, 0.36f, 0.50f, 1.00f);
        private static readonly Color BtnActiveBg = new Color(0.24f, 0.42f, 0.54f, 0.98f);
        private static readonly Color BtnVioletBg = new Color(0.38f, 0.20f, 0.54f, 0.96f);
        private static readonly Color BtnVioletBorder = new Color(0.84f, 0.46f, 1.00f, 0.96f);
        private static readonly Color AccentGold = new Color(1.00f, 0.86f, 0.30f, 1f);
        private static readonly Color AccentCyan = new Color(0.42f, 0.90f, 1.00f, 1f);
        private static readonly Color AccentViolet = new Color(0.86f, 0.52f, 1f, 1f);
        private static readonly Color AccentGreen = new Color(0.36f, 0.92f, 0.58f, 1f);
        private static readonly Color TextPrimary = new Color(0.95f, 0.97f, 1.00f, 1f);
        private static readonly Color TextMuted = new Color(0.68f, 0.76f, 0.86f, 1f);

        private GComponent _root;
        private GGraph _panelBg;
        private GGraph _headerBg;
        private GTextField _titleText;
        private GComponent _closeBtn;
        private GComponent _tabHost;
        private GComponent _tabMonstersBtn;
        private GComponent _tabVirtualBtn;
        private GComponent _tabStyleBtn;
        private GComponent _contentHost;
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
        private PickerPopupTab _activeTab = PickerPopupTab.Monsters;
        private RouteColorRole _selectedColorRole = RouteColorRole.Deterministic;
        private int _pageIndex;
        private string _lastSignature = string.Empty;

        public bool IsOpen => _isOpen && !_temporarilyHidden;

        public event Action<long> MonsterSelected;
        public event Action ClearRequested;
        public event Action<int> StepsDeltaRequested;

        public event Action<long> VirtualPlayerSelected;
        public event Action<long, int> VirtualMoveRequested;
        public event Action<long> VirtualUndoRequested;
        public event Action<long> VirtualResetPlayerRequested;
        public event Action VirtualResetAllRequested;
        public event Action<bool> VirtualBoardPickToggled;

        public event Action<Action<PredictorSettings>> StyleMutationRequested;
        public event Action StyleResetRequested;

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

        public void Invalidate()
        {
            _lastSignature = string.Empty;
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
            IReadOnlyList<TrackedPlayer> players,
            BoardGraph board,
            VirtualPlayerSandbox sandbox,
            long localPlayerId,
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

            float stageWidth = GRoot.inst != null && GRoot.inst.width > 0f ? GRoot.inst.width : PanelWidth;
            float stageHeight = GRoot.inst != null && GRoot.inst.height > 0f ? GRoot.inst.height : 720f;
            float panelW = Math.Min(PanelWidth, Math.Max(140f, stageWidth));
            float padX = panelW < 220f ? 4f : Padding;

            int rowsPerPage = ComputeRowsPerPage(stageHeight);
            int totalPages = _activeTab == PickerPopupTab.Monsters
                ? Math.Max(1, (visible.Count + rowsPerPage - 1) / rowsPerPage)
                : 1;
            if (_pageIndex >= totalPages) _pageIndex = totalPages - 1;
            if (_pageIndex < 0) _pageIndex = 0;

            if (_positioned) ClampToStage();

            string sig = BuildSignature(visible, players, board, sandbox, localPlayerId, selectedMonsterId, settings, _pageIndex, totalPages, rowsPerPage, stageWidth, stageHeight);
            if (sig == _lastSignature) return;
            _lastSignature = sig;

            bool showPager = _activeTab == PickerPopupTab.Monsters && totalPages > 1;
            var layout = ComputeCompactLayout(stageHeight, showPager);

            _titleText.text = "怪物寻路 [" + settings.PickerHotkey + "]";
            _stepsLabel.text = "延伸 " + settings.PreviewSteps + " 格";
            ApplyChromeLayout(layout, panelW, padX, sandbox != null ? sandbox.OverrideCount : 0);

            _contentHost.RemoveChildren(0, -1, true);
            _contentHost.visible = true;
            _emptyText.visible = false;

            float contentW = Math.Max(40f, panelW - padX * 2f);
            float contentY = layout.HeaderH + layout.TabH + layout.Pad;
            float contentHeight;

            if (_activeTab == PickerPopupTab.Monsters)
            {
                contentHeight = BuildMonstersTab(visible, sandbox, selectedMonsterId, layout, contentW, padX, contentY, rowsPerPage);
            }
            else if (_activeTab == PickerPopupTab.VirtualMove)
            {
                contentHeight = BuildVirtualTab(players, board, sandbox, localPlayerId, layout, contentW);
            }
            else
            {
                contentHeight = BuildStyleTab(settings, layout, contentW);
            }

            if (_pagerHost != null)
            {
                _pagerHost.visible = showPager && layout.PagerH > 0f;
                if (_pagerHost.visible)
                {
                    _pageLabel.text = (_pageIndex + 1) + " / " + totalPages + " (" + visible.Count + "只)";
                    _pagerHost.SetXY(0f, contentY + contentHeight);
                }
            }

            float totalHeight = contentY + contentHeight + (showPager ? layout.PagerH : 0f) + layout.Pad + layout.FooterH;
            if (stageHeight > 0f && totalHeight > stageHeight)
            {
                totalHeight = stageHeight;
            }
            _root.SetSize(panelW, totalHeight);
            _panelBg.SetSize(panelW, totalHeight);
            _panelBg.DrawRect(panelW, totalHeight, 2, PanelBorder, PanelBg);

            _contentHost.SetXY(padX, contentY);
            _contentHost.SetSize(contentW, Math.Max(0f, contentHeight));
            _footerHost.SetXY(0f, Math.Max(layout.HeaderH, totalHeight - layout.FooterH));

            if (!_positioned && GRoot.inst != null)
            {
                _positioned = true;
                float x = Math.Max(0f, Math.Min(GRoot.inst.width - panelW, Math.Max(20f, GRoot.inst.width - panelW - 28f)));
                float y = Math.Max(0f, Math.Min(GRoot.inst.height - totalHeight, Math.Max(60f, (GRoot.inst.height - totalHeight) * 0.25f)));
                _root.SetXY(x, y);
            }
            ClampToStage();
        }

        private void SwitchTab(PickerPopupTab tab)
        {
            if (_activeTab == tab) return;
            _activeTab = tab;
            _lastSignature = string.Empty;
        }
    }
}
