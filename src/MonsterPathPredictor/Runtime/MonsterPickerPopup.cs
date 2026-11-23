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
        private GTextField _stepsLabel;
        private GTextField _emptyText;

        private bool _isOpen;
        private bool _positioned;
        private string _lastSignature = string.Empty;

        public bool IsOpen => _isOpen;

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
            if (_root != null && !_root.isDisposed)
            {
                _root.visible = false;
            }
        }

        public bool ContainsStagePoint(Vector2 stagePos)
        {
            if (!_isOpen || _root == null || _root.isDisposed || !_root.visible) return false;
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
            if (!_isOpen) return;
            EnsureCreated();
            if (_root == null || _root.isDisposed) return;

            string sig = BuildSignature(monsters, selectedMonsterId, settings);
            if (sig == _lastSignature) return;
            _lastSignature = sig;

            _titleText.text = "怪物寻路选择 [" + settings.PickerHotkey + "]";
            _stepsLabel.text = "延伸 " + settings.PreviewSteps + " 格";

            _listHost.RemoveChildren(0, -1, true);

            var visible = new List<LiveMonsterInfo>();
            if (monsters != null)
            {
                for (int i = 0; i < monsters.Count && visible.Count < MaxVisibleRows; i++)
                {
                    var m = monsters[i];
                    if (m == null || m.State == null) continue;
                    if (!settings.AllowNonAggressiveMonsters && !m.State.IsNormalAggressive) continue;
                    visible.Add(m);
                }
            }

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
                listHeight = visible.Count * (RowHeight + 4f) + 4f;
                for (int i = 0; i < visible.Count; i++)
                {
                    var row = BuildMonsterRow(visible[i], selectedMonsterId, settings.ClosePickerOnSelect);
                    row.SetXY(0f, i * (RowHeight + 4f));
                    _listHost.AddChild(row);
                }
            }

            float totalHeight = HeaderHeight + Padding + listHeight + Padding + FooterHeight;
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

        private GComponent BuildMonsterRow(LiveMonsterInfo info, long selectedMonsterId, bool closeOnSelect)
        {
            var st = info.State;
            bool isSelected = st.MonsterId == selectedMonsterId;
            float rowW = PanelWidth - Padding * 2f;

            var row = new GComponent
            {
                touchable = true
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
                if (closeOnSelect) Hide();
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
                draggable = true,
                sortingOrder = 2200,
                visible = _isOpen
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

            _emptyText = new GTextField
            {
                touchable = false,
                autoSize = AutoSizeType.Both,
                textFormat = new TextFormat { size = 14, color = TextMuted },
                text = "当前棋盘上没有存活怪物"
            };
            _root.AddChild(_emptyText);

            _footerHost = new GComponent { touchable = true };
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
            var btn = new GComponent { touchable = true };
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

        private static string BuildSignature(IReadOnlyList<LiveMonsterInfo> monsters, long selectedId, PredictorSettings settings)
        {
            var sb = new System.Text.StringBuilder(128);
            sb.Append(selectedId).Append('|').Append(settings.PreviewSteps).Append('|').Append(settings.PickerHotkey).Append('|');
            if (monsters != null)
            {
                for (int i = 0; i < monsters.Count; i++)
                {
                    var st = monsters[i].State;
                    sb.Append(st.MonsterId).Append(':')
                      .Append(st.DisplayName).Append(':')
                      .Append(st.CurrentLandId).Append(':')
                      .Append(st.Hp).Append('/').Append(st.MaxHp).Append(';');
                }
            }
            return sb.ToString();
        }

        public void Dispose()
        {
            _lastSignature = string.Empty;
            _positioned = false;
            if (_root != null)
            {
                try { _root.Dispose(); } catch { }
                _root = null;
            }
            _panelBg = null;
            _headerBg = null;
            _titleText = null;
            _listHost = null;
            _footerHost = null;
            _stepsLabel = null;
            _emptyText = null;
        }
    }
}
