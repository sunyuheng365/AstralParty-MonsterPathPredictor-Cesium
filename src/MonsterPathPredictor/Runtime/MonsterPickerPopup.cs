using System;
using System.Collections.Generic;
using System.Text;
using CesiumLoader.SDK.Inputs;
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
        private bool _styleSaveFailed;
        private readonly HashSet<int> _activePopupTouchIds = new HashSet<int>();
        private readonly HashSet<int> _mouseOriginatedTouchIds = new HashSet<int>();
        private Stage _hookedStage;
        private bool _hasRenderedContent;
        private int _currentMatchGen;
        private int _popupLocalRevision;
        private int _lastRefreshDataRev = -1;
        private int _lastRefreshLocalRev = -1;
        private int _lastRefreshStageW = -1;
        private int _lastRefreshStageH = -1;
        private int _lastRefreshMatchGen = -1;
        private long _lastRefreshSelectedId = long.MinValue;
        private PickerPopupTab _activeTab = PickerPopupTab.Monsters;
        private RouteColorRole _selectedColorRole = RouteColorRole.Deterministic;
        private int _monsterPageIndex;
        private int _virtualPageIndex;
        private int _stylePageIndex;
        private string _lastSignature = string.Empty;
        private readonly List<LiveMonsterInfo> _visibleMonstersBuf = new List<LiveMonsterInfo>(16);
        private readonly StringBuilder _sigBuilder = new StringBuilder(320);

        public bool IsOpen => _isOpen && !_temporarilyHidden;

        public void SetStyleSaveFailed(bool failed)
        {
            if (_styleSaveFailed == failed) return;
            _styleSaveFailed = failed;
            _popupLocalRevision++;
            _lastSignature = string.Empty;
        }

        public void CancelPendingPress()
        {
            _activePopupTouchIds.Clear();
            _mouseOriginatedTouchIds.Clear();
            _popupLocalRevision++;
            _lastSignature = string.Empty;
        }

        public event Action<long, int> MonsterSelected;
        public event Action ClearRequested;
        public event Action<int> StepsDeltaRequested;

        public event Action<long, int, int> VirtualPlayerSelected;
        public event Action<long, int, int, int, int> VirtualStepNeighborRequested;
        public event Action<long, int, int> VirtualUndoRequested;
        public event Action<long, int, int> VirtualResetPlayerRequested;
        public event Action<int, int> VirtualResetAllRequested;
        public event Action<long, bool, int, int> VirtualBoardPickToggled;

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
            _popupLocalRevision++;
            _lastSignature = string.Empty;
            EnsureCreated();
            if (_root != null && !_root.isDisposed)
            {
                _root.visible = true;
                // Re-insert within our sortingOrder band so reopening brings the popup to the front
                // of any equal-sortingOrder sibling (SetChildIndex is ignored by FairyGUI for sorted children).
                if (GRoot.inst != null && _root.parent == GRoot.inst)
                {
                    GRoot.inst.RemoveChild(_root, false);
                    GRoot.inst.AddChild(_root);
                }
            }
        }

        public void Hide()
        {
            _isOpen = false;
            _temporarilyHidden = false;
            _activePopupTouchIds.Clear();
            _mouseOriginatedTouchIds.Clear();
            if (_root != null && !_root.isDisposed)
            {
                _root.visible = false;
            }
        }

        public void Invalidate()
        {
            _popupLocalRevision++;
            _lastSignature = string.Empty;
        }

        public void SetTemporarilyHidden(bool hidden)
        {
            if (_temporarilyHidden == hidden) return;
            _temporarilyHidden = hidden;
            if (hidden)
            {
                _activePopupTouchIds.Clear();
                _mouseOriginatedTouchIds.Clear();
            }
            else
            {
                _popupLocalRevision++;
            }
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
            PredictorSettings settings,
            int matchGeneration = 0,
            int dataRevision = 0)
        {
            if (!IsOpen) return;
            EnsureCreated();
            if (_root == null || _root.isDisposed) return;

            _currentMatchGen = matchGeneration;

            float stageWidth = GRoot.inst != null && GRoot.inst.width > 0f ? GRoot.inst.width : PanelWidth;
            float stageHeight = GRoot.inst != null && GRoot.inst.height > 0f ? GRoot.inst.height : 720f;
            int stageWInt = (int)Math.Round(stageWidth);
            int stageHInt = (int)Math.Round(stageHeight);

            // Defer destructive content rebuild while any pointer/touch press that began inside our popup
            // is still active — independent of _lastSignature so Invalidate() or briefly dragging
            // outside the popup never disposes a pressed row/button before MouseUp fires onClick.
            if (_hasRenderedContent && IsPopupPressActive())
            {
                if (_positioned) ClampToStage();
                return;
            }

            // Fast-path cache hit: skip row layout, pagination, neighbor enumeration, and signature building
            // whenever neither external data/sandbox/settings nor local tab/page/stage dimensions changed.
            if (dataRevision > 0 &&
                _hasRenderedContent &&
                !string.IsNullOrEmpty(_lastSignature) &&
                _lastRefreshDataRev == dataRevision &&
                _lastRefreshLocalRev == _popupLocalRevision &&
                _lastRefreshStageW == stageWInt &&
                _lastRefreshStageH == stageHInt &&
                _lastRefreshMatchGen == matchGeneration &&
                _lastRefreshSelectedId == selectedMonsterId)
            {
                if (_positioned) ClampToStage();
                return;
            }

            _visibleMonstersBuf.Clear();
            if (monsters != null)
            {
                for (int i = 0; i < monsters.Count; i++)
                {
                    var m = monsters[i];
                    if (m == null || m.State == null) continue;
                    if (!settings.AllowNonAggressiveMonsters && !m.State.IsNormalAggressive) continue;
                    _visibleMonstersBuf.Add(m);
                }
            }

            float panelW = Math.Min(PanelWidth, Math.Max(48f, stageWidth));
            float padX = panelW < 220f ? 4f : Padding;
            float contentW = Math.Max(40f, panelW - padX * 2f);

            bool hasVirtualOverrides = sandbox != null && sandbox.HasOverrides;
            int rowsPerPage = ComputeRowsPerPage(stageHeight, hasVirtualOverrides);
            int totalPages;
            int activePageIndex;
            CompactLayout layout;
            List<PopupPagination.VirtualRowItem> virtualRows = null;
            List<PageSlice> virtualPages = null;
            IReadOnlyList<int> virtualNeighbors = Array.Empty<int>();
            float virtualRowGap = 3f;
            int virtualNeighborCols = 1;
            List<PopupPagination.StyleRowItem> styleRows = null;
            List<PageSlice> stylePages = null;
            float styleRowGap = 3f;

            if (_activeTab == PickerPopupTab.Monsters)
            {
                totalPages = Math.Max(1, (_visibleMonstersBuf.Count + rowsPerPage - 1) / rowsPerPage);
                _monsterPageIndex = ClampPage(_monsterPageIndex, totalPages);
                activePageIndex = _monsterPageIndex;
                layout = ComputeCompactLayout(stageHeight, totalPages > 1);
            }
            else if (_activeTab == PickerPopupTab.VirtualMove)
            {
                ComputeVirtualTabLayout(
                    players, board, sandbox, stageHeight, contentW,
                    out layout, out virtualRows, out virtualPages, out virtualRowGap, out virtualNeighborCols, out virtualNeighbors);
                totalPages = Math.Max(1, virtualPages.Count);
                _virtualPageIndex = ClampPage(_virtualPageIndex, totalPages);
                activePageIndex = _virtualPageIndex;
            }
            else
            {
                ComputeStyleTabLayout(
                    stageHeight, contentW, _styleSaveFailed,
                    out layout, out styleRows, out stylePages, out styleRowGap);
                totalPages = Math.Max(1, stylePages.Count);
                _stylePageIndex = ClampPage(_stylePageIndex, totalPages);
                activePageIndex = _stylePageIndex;
            }

            if (_positioned) ClampToStage();

            string sig = BuildSignature(_visibleMonstersBuf, players, virtualNeighbors, sandbox, localPlayerId, selectedMonsterId, settings, activePageIndex, totalPages, rowsPerPage, stageWidth, stageHeight);
            if (sig == _lastSignature)
            {
                _lastRefreshDataRev = dataRevision;
                _lastRefreshLocalRev = _popupLocalRevision;
                _lastRefreshStageW = stageWInt;
                _lastRefreshStageH = stageHInt;
                _lastRefreshMatchGen = matchGeneration;
                _lastRefreshSelectedId = selectedMonsterId;
                return;
            }

            bool showPager = totalPages > 1;

            _titleText.text = "怪物寻路 [" + settings.PickerHotkey + "]";
            _stepsLabel.text = "延伸 " + settings.PreviewSteps + " 格";
            ApplyChromeLayout(layout, panelW, padX, sandbox != null ? sandbox.OverrideCount : 0);

            _contentHost.RemoveChildren(0, -1, true);
            _contentHost.visible = true;
            _emptyText.visible = false;

            float contentY = layout.HeaderH + layout.TabH + layout.Pad;
            float contentHeight;

            if (_activeTab == PickerPopupTab.Monsters)
            {
                contentHeight = BuildMonstersTab(_visibleMonstersBuf, sandbox, selectedMonsterId, layout, contentW, padX, contentY, rowsPerPage);
            }
            else if (_activeTab == PickerPopupTab.VirtualMove)
            {
                contentHeight = BuildVirtualTab(
                    players, virtualNeighbors, sandbox, localPlayerId, layout, contentW,
                    virtualRows, virtualPages[activePageIndex], virtualRowGap, virtualNeighborCols);
            }
            else
            {
                contentHeight = BuildStyleTab(
                    settings, layout, contentW,
                    styleRows, stylePages[activePageIndex], styleRowGap);
            }

            if (_pagerHost != null)
            {
                _pagerHost.visible = showPager && layout.PagerH > 0f;
                if (_pagerHost.visible)
                {
                    _pageLabel.text = _activeTab == PickerPopupTab.Monsters
                        ? ((activePageIndex + 1) + " / " + totalPages + " (" + _visibleMonstersBuf.Count + "只)")
                        : ("第 " + (activePageIndex + 1) + " / " + totalPages + " 页");
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

            // Commit signature only AFTER full tab construction and layout succeed.
            _hasRenderedContent = true;
            _lastSignature = sig;
            _lastRefreshDataRev = dataRevision;
            _lastRefreshLocalRev = _popupLocalRevision;
            _lastRefreshStageW = stageWInt;
            _lastRefreshStageH = stageHInt;
            _lastRefreshMatchGen = matchGeneration;
            _lastRefreshSelectedId = selectedMonsterId;
        }

        private static int ClampPage(int index, int totalPages)
        {
            if (totalPages <= 1) return 0;
            if (index < 0) return 0;
            if (index >= totalPages) return totalPages - 1;
            return index;
        }

        private void StepActivePage(int delta)
        {
            if (_activeTab == PickerPopupTab.Monsters)
                _monsterPageIndex = Math.Max(0, _monsterPageIndex + delta);
            else if (_activeTab == PickerPopupTab.VirtualMove)
                _virtualPageIndex = Math.Max(0, _virtualPageIndex + delta);
            else
                _stylePageIndex = Math.Max(0, _stylePageIndex + delta);
            _popupLocalRevision++;
            _lastSignature = string.Empty;
        }

        private void OnPopupTouchBegin(EventContext ctx)
        {
            int touchId = (ctx != null && ctx.inputEvent != null) ? ctx.inputEvent.touchId : -1;
            // In FairyGUI, mouse pointers carry negative touchId (-1) while touch fingers carry >= 0.
            // Track every concurrent touchId independently so a second finger never loses rebuild deferral
            // and a touch is never misclassified as mouse merely because a mouse button is held simultaneously.
            _activePopupTouchIds.Add(touchId);
            if (touchId < 0)
            {
                _mouseOriginatedTouchIds.Add(touchId);
            }
        }

        private void OnStageTouchEnd(EventContext ctx)
        {
            if (_activePopupTouchIds.Count == 0) return;
            if (ctx == null || ctx.inputEvent == null)
            {
                _activePopupTouchIds.Clear();
                _mouseOriginatedTouchIds.Clear();
                return;
            }
            int tid = ctx.inputEvent.touchId;
            _activePopupTouchIds.Remove(tid);
            _mouseOriginatedTouchIds.Remove(tid);
        }

        private bool IsPopupPressActive()
        {
            if (_root == null || _root.isDisposed || !_root.visible ||
                (_hookedStage != null && Stage.inst != _hookedStage))
            {
                _activePopupTouchIds.Clear();
                _mouseOriginatedTouchIds.Clear();
                return false;
            }

            if (_mouseOriginatedTouchIds.Count > 0)
            {
                // Auto-recover mouse-originated presses if focus loss dropped Stage.onTouchEnd,
                // without ever clearing concurrent non-mouse finger touches.
                try
                {
                    if (!InputService.IsMouseButtonHeld(0) && !InputService.IsMouseButtonPressed(0))
                    {
                        foreach (int mid in _mouseOriginatedTouchIds)
                        {
                            _activePopupTouchIds.Remove(mid);
                        }
                        _mouseOriginatedTouchIds.Clear();
                    }
                }
                catch { }
            }

            if (_activePopupTouchIds.Count > 0) return true;

            try
            {
                if ((InputService.IsMouseButtonHeld(0) || InputService.IsMouseButtonPressed(0)) &&
                    Stage.inst != null && ContainsStagePoint(Stage.inst.touchPosition))
                {
                    _activePopupTouchIds.Add(-1);
                    _mouseOriginatedTouchIds.Add(-1);
                    return true;
                }
            }
            catch { }
            return false;
        }

        private void SwitchTab(PickerPopupTab tab)
        {
            if (_activeTab == tab) return;
            _activeTab = tab;
            _popupLocalRevision++;
            _lastSignature = string.Empty;
        }
    }
}
