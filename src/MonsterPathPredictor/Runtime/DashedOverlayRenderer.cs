using System;
using System.Collections.Generic;
using CesiumLoader.SDK.Engine;
using FairyGUI;
using UnityEngine;
using MonsterPathPredictor.Core;

namespace MonsterPathPredictor.Runtime
{
    /// <summary>
    /// Renders animated forward-marching dashed path segments, guide-colored fork arrows (Red/Blue for
    /// fork-start/post-combat, Cyan/Gold/Coral for mid-walk chases & probability splits), encounter markers
    /// ("⚔遇P1"), distinct softer post-combat continuation styling, and a sprite-top monster header badge.
    /// Pools GGraph / GTextField widgets across frames to avoid GC pressure during LateUpdate.
    /// </summary>
    internal sealed class DashedOverlayRenderer : IDisposable
    {
        private static readonly Color CombatBadgeColor = new Color(1.00f, 0.40f, 0.34f, 0.98f);        // Encounter marker
        private static readonly Color RingColor = new Color(1.00f, 0.88f, 0.25f, 0.95f);               // Selection ring
        private static readonly Color VirtualGhostColor = new Color(0.82f, 0.42f, 1.00f, 0.98f);       // Virtual player ghost marker
        private static readonly Color VirtualTetherColor = new Color(0.82f, 0.42f, 1.00f, 0.72f);      // Real -> Virtual dashed tether
        private static readonly Color VirtualInnerDotColor = new Color(0.98f, 0.92f, 1.00f, 0.98f);    // Inner ghost core
        private static readonly Color BadgeBg = new Color(0.06f, 0.09f, 0.14f, 0.86f);                 // Dark pill
        private static readonly float[] PillCornerRadii = { 6f, 6f, 6f, 6f };

        private readonly struct RoutePalette
        {
            public readonly Color Deterministic;
            public readonly Color MajorBranch;
            public readonly Color MinorBranch;
            public readonly Color PostCombat;
            public readonly Color GuideRed;
            public readonly Color GuideBlue;

            public RoutePalette(PredictorSettings settings)
            {
                Deterministic = ToUnityColor(StyleColor.GetRoleColor(settings, RouteColorRole.Deterministic));
                MajorBranch = ToUnityColor(StyleColor.GetRoleColor(settings, RouteColorRole.MajorBranch));
                MinorBranch = ToUnityColor(StyleColor.GetRoleColor(settings, RouteColorRole.MinorBranch));
                PostCombat = ToUnityColor(StyleColor.GetRoleColor(settings, RouteColorRole.PostCombat));
                GuideRed = ToUnityColor(StyleColor.GetRoleColor(settings, RouteColorRole.GuideRed));
                GuideBlue = ToUnityColor(StyleColor.GetRoleColor(settings, RouteColorRole.GuideBlue));
            }

            private static Color ToUnityColor(RgbaColor c) => new Color(c.Rf, c.Gf, c.Bf, c.Af);
        }

        private sealed class PooledPill
        {
            public GComponent Root;
            public GGraph Bg;
            public GTextField Text;
            public string LastText;
            public int LastFontSize;
            public Color LastColor;
            public bool LastCompact;
            public float Width;
            public float Height;
            public float LastX;
            public float LastY;
            public bool HasContent;
            public bool HasPos;
        }

        private GComponent _layer;
        private GComponent _lineLayer;
        private GComponent _pillLayer;
        private readonly List<GGraph> _lineGraphPool = new List<GGraph>();
        private readonly List<PooledPill> _pillPool = new List<PooledPill>();
        private long _stableHeaderMonsterId;
        private float _stableHeaderHeight;
        private readonly Dictionary<int, Point2> _screenCache = new Dictionary<int, Point2>();
        private readonly Dictionary<int, int> _forkTagStack = new Dictionary<int, int>();
        private readonly Dictionary<int, float> _forkTagNextTopY = new Dictionary<int, float>();
        private readonly Dictionary<long, int> _forkExitLabelStack = new Dictionary<long, int>();
        private readonly Dictionary<long, float> _forkExitNextTopY = new Dictionary<long, float>();
        private readonly Dictionary<int, int> _ghostTileStack = new Dictionary<int, int>();
        private readonly HashSet<int> _drawnGhostRingTiles = new HashSet<int>();
        private readonly HashSet<long> _multiBranchEdgeKeys = new HashSet<long>();
        private readonly HashSet<int> _combatLandSet = new HashSet<int>();
        private readonly List<DashQuad> _dashQuadBuf = new List<DashQuad>(32);
        private readonly List<Vector2> _vertBuf = new List<Vector2>(4);
        private int _usedLineGraphs;
        private int _usedPills;

        private RoutePalette _cachedPalette;
        private bool _hasCachedPalette;
        private string _palDet, _palMaj, _palMin, _palPost, _palRed, _palBlue;

        private RoutePalette GetPalette(PredictorSettings settings)
        {
            if (!_hasCachedPalette ||
                !string.Equals(_palDet, settings.DeterministicColorHex, StringComparison.Ordinal) ||
                !string.Equals(_palMaj, settings.MajorBranchColorHex, StringComparison.Ordinal) ||
                !string.Equals(_palMin, settings.MinorBranchColorHex, StringComparison.Ordinal) ||
                !string.Equals(_palPost, settings.PostCombatColorHex, StringComparison.Ordinal) ||
                !string.Equals(_palRed, settings.GuideRedColorHex, StringComparison.Ordinal) ||
                !string.Equals(_palBlue, settings.GuideBlueColorHex, StringComparison.Ordinal))
            {
                _palDet = settings.DeterministicColorHex;
                _palMaj = settings.MajorBranchColorHex;
                _palMin = settings.MinorBranchColorHex;
                _palPost = settings.PostCombatColorHex;
                _palRed = settings.GuideRedColorHex;
                _palBlue = settings.GuideBlueColorHex;
                _cachedPalette = new RoutePalette(settings);
                _hasCachedPalette = true;
            }
            return _cachedPalette;
        }

        public GComponent EnsureLayer()
        {
            if (_layer != null && !_layer.isDisposed && _layer.parent == GRoot.inst &&
                _lineLayer != null && !_lineLayer.isDisposed &&
                _pillLayer != null && !_pillLayer.isDisposed)
            {
                float w = GRoot.inst.width;
                float h = GRoot.inst.height;
                if (Math.Abs(_layer.width - w) > 0.5f || Math.Abs(_layer.height - h) > 0.5f)
                {
                    _layer.SetSize(w, h);
                    _lineLayer.SetSize(w, h);
                    _pillLayer.SetSize(w, h);
                }
                return _layer;
            }

            Dispose();
            if (GRoot.inst == null) return null;

            float rootW = GRoot.inst.width;
            float rootH = GRoot.inst.height;

            _layer = new GComponent
            {
                name = "MonsterPathPredictor",
                touchable = false,
                sortingOrder = 55
            };
            _lineLayer = new GComponent { name = "Lines", touchable = false };
            _pillLayer = new GComponent { name = "Pills", touchable = false };

            _layer.AddChild(_lineLayer);
            _layer.AddChild(_pillLayer);
            GRoot.inst.AddChild(_layer);

            _layer.SetSize(rootW, rootH);
            _lineLayer.SetSize(rootW, rootH);
            _pillLayer.SetSize(rootW, rootH);
            return _layer;
        }

        public void HideAll()
        {
            for (int i = 0; i < _lineGraphPool.Count; i++)
            {
                try
                {
                    var g = _lineGraphPool[i];
                    if (g != null && !g.isDisposed && g.visible) g.visible = false;
                }
                catch { }
            }
            for (int i = 0; i < _pillPool.Count; i++)
            {
                try
                {
                    var root = _pillPool[i]?.Root;
                    if (root != null && !root.isDisposed && root.visible) root.visible = false;
                }
                catch { }
            }
            _usedLineGraphs = 0;
            _usedPills = 0;
        }

        public void Render(
            Camera camera,
            BoardGraph board,
            LiveMonsterInfo selectedMonster,
            PathPlan plan,
            PredictorSettings settings,
            IReadOnlyList<VirtualPlayerGhost> virtualGhosts = null)
        {
            var layer = EnsureLayer();
            bool hasMonsterPlan = selectedMonster != null && plan != null;
            bool hasVirtualGhosts = virtualGhosts != null && virtualGhosts.Count > 0;
            if (layer == null || camera == null || board == null || settings == null || (!hasMonsterPlan && !hasVirtualGhosts))
            {
                HideAll();
                return;
            }

            _usedLineGraphs = 0;
            _usedPills = 0;
            _forkTagNextTopY.Clear();

            var palette = GetPalette(settings);
            var camTr = UnityObject.GetTransform(camera);
            var camPos = TransformService.GetPosition(camTr);
            var camForward = TransformService.GetForward(camTr);
            float hOffset = settings.HeightOffset;

            // Use the camera's actual near clip plane consistently in double precision across both
            // 3D near-plane clipping and unclipped projections so passed endpoints are never rejected
            // by lower-precision float dot-product drift.
            float nearClipDist = 0.01f;
            try { if (camera.nearClipPlane > 0f) nearClipDist = Math.Max(0.01f, camera.nearClipPlane); } catch { }

            // Smooth forward-marching dash phase (px)
            float phaseOffset = settings.DashSpeed > 0.01f
                ? UnityTime.RealtimeSinceStartup * settings.DashSpeed
                : 0f;

            _screenCache.Clear();
            bool TryProjectTileRaw(int landId, out Point2 pt)
            {
                if (_screenCache.TryGetValue(landId, out pt)) return true;
                if (!board.TryGetNode(landId, out var node))
                {
                    pt = default;
                    return false;
                }
                var world = new Vector3(node.X, node.Y + hOffset, node.Z);
                if (!TryProjectWorldUnclipped(layer, camera, camPos, camForward, world, out pt, nearClipDist)) return false;
                _screenCache[landId] = pt;
                return true;
            }

            bool TryProjectTile(int landId, out Point2 pt)
            {
                if (!TryProjectTileRaw(landId, out pt)) return false;
                return pt.X >= -300f && pt.X <= layer.width + 300f && pt.Y >= -300f && pt.Y <= layer.height + 300f;
            }

            if (hasMonsterPlan)
            {
                // 0. Selected monster header badge FIRST (always Pill #0 so its cached FairyGUI mesh never swaps)
                Vector3 headerWorld = GetStableHeaderAnchorWorld(selectedMonster);
                if (TryProjectWorld(layer, camera, camPos, camForward, headerWorld, out var headerPt, nearClipDist))
                {
                    string note = plan.HeadlineNote;
                    if (hasVirtualGhosts)
                    {
                        note = string.IsNullOrEmpty(note) ? "虚拟站位" : (note + "·虚拟站位");
                    }
                    string headerText = !string.IsNullOrEmpty(note)
                        ? plan.MonsterName + " (" + note + ")"
                        : plan.MonsterName;
                    DrawPillLabel(new Point2(headerPt.X, headerPt.Y - 12f), headerText, Math.Max(13, settings.FontSize - 2), hasVirtualGhosts ? VirtualGhostColor : RingColor);
                }

                // 1. Highlight dot on the selected monster's starting tile
                if (TryProjectTile(plan.StartLandId, out var startPt))
                {
                    DrawDot(startPt, 8.5f, RingColor);
                }
            }

            // 2. Animated forward-marching dashed lines for all predicted edges:
            //    first clip 3D segment against camera near plane in double precision, then project
            //    already-verified endpoints directly (no redundant float depth re-check), clip 2D
            //    via exact float-rational Liang-Barsky, and preserve exact dash phase in 106-bit double-double.
            double dashPeriod = (double)settings.DashLength + Math.Max(0.0, settings.GapLength);

            if (hasMonsterPlan)
            {
                _multiBranchEdgeKeys.Clear();
                for (int fi = 0; fi < plan.Forks.Count; fi++)
                {
                    var fk = plan.Forks[fi];
                    if (!fk.HasMultipleBranches) continue;
                    for (int ei = 0; ei < fk.Exits.Count; ei++)
                    {
                        _multiBranchEdgeKeys.Add(((long)fk.ForkLandId << 32) ^ (uint)fk.Exits[ei].NextLandId);
                    }
                }

                for (int i = 0; i < plan.Edges.Count; i++)
            {
                var edge = plan.Edges[i];
                if (!board.TryGetNode(edge.FromLandId, out var fromNode)) continue;
                if (!board.TryGetNode(edge.ToLandId, out var toNode)) continue;

                float ax = fromNode.X, ay = fromNode.Y + hOffset, az = fromNode.Z;
                float bx = toNode.X, by = toNode.Y + hOffset, bz = toNode.Z;
                if (!DashGeometry.TryClipSegmentToNearPlane(
                        ax, ay, az, bx, by, bz,
                        camPos.x, camPos.y, camPos.z,
                        camForward.x, camForward.y, camForward.z,
                        nearClipDist,
                        out float cax, out float cay, out float caz,
                        out float cbx, out float cby, out float cbz,
                        out bool clippedNearStart, out bool clippedNearEnd))
                    continue;

                Point2 pFrom, pTo;
                if (!clippedNearStart && _screenCache.TryGetValue(edge.FromLandId, out pFrom))
                {
                    // Reuse cached projection
                }
                else
                {
                    if (!TryProjectWorldUnclipped(layer, camera, camPos, camForward, new Vector3(cax, cay, caz), out pFrom, nearClipDist, skipDepthCheck: true)) continue;
                    if (!clippedNearStart) _screenCache[edge.FromLandId] = pFrom;
                }

                if (!clippedNearEnd && _screenCache.TryGetValue(edge.ToLandId, out pTo))
                {
                    // Reuse cached projection
                }
                else
                {
                    if (!TryProjectWorldUnclipped(layer, camera, camPos, camForward, new Vector3(cbx, cby, cbz), out pTo, nearClipDist, skipDepthCheck: true)) continue;
                    if (!clippedNearEnd) _screenCache[edge.ToLandId] = pTo;
                }

                if (!DashGeometry.TryClipSegmentToRect(
                        pFrom, pTo, -40f, -40f, layer.width + 40f, layer.height + 40f,
                        dashPeriod,
                        out var clipFrom, out var clipTo,
                        out double clippedFromStartPx, out double clippedStartModPeriod, out double clippedEndPx))
                    continue;

                long edgeKey = ((long)edge.FromLandId << 32) ^ (uint)edge.ToLandId;
                bool edgeIsSplit = _multiBranchEdgeKeys.Contains(edgeKey);
                Color color = PickEdgeColor(palette, edge.Probability, edge.IsAfterCombat, edgeIsSplit);
                float baseWidth = edge.IsAfterCombat ? Math.Max(2.2f, settings.LineWidth * 0.78f) : settings.LineWidth;
                float width = edge.Probability >= 0.999
                    ? baseWidth
                    : Math.Max(2.0f, baseWidth * (0.65f + 0.35f * (float)edge.Probability));

                double origTrimStart = clippedNearStart ? 0.0 : (edge.EarliestStep == 0 ? 10.0 : 3.0);
                double origTrimEnd = clippedNearEnd ? 0.0 : 3.0;
                double trimStart = Math.Max(0.0, origTrimStart - clippedFromStartPx);
                double trimEnd = Math.Max(0.0, origTrimEnd - clippedEndPx);
                float clippedPhase = DashGeometry.ComputeClippedDashPhase(phaseOffset, origTrimStart, clippedStartModPeriod, trimStart, dashPeriod);

                DashGeometry.BuildDashedSegmentsInto(
                    _dashQuadBuf,
                    clipFrom, clipTo,
                    settings.DashLength,
                    settings.GapLength,
                    width,
                    trimStart: (float)trimStart,
                    trimEnd: (float)trimEnd,
                    phaseOffset: clippedPhase);

                for (int q = 0; q < _dashQuadBuf.Count; q++)
                {
                    DrawQuad(_dashQuadBuf[q], color);
                }

                // Draw directional chevron on the very first step out of the monster (only when visible)
                if (edge.EarliestStep == 0 && !clippedNearStart && !clippedNearEnd &&
                    DashGeometry.TryBuildArrowHead(pFrom, pTo, Math.Max(12f, width * 3.1f), Math.Max(6f, width * 1.6f), out var head, 0.60f) &&
                    head.P0.X >= -60f && head.P0.X <= layer.width + 60f && head.P0.Y >= -60f && head.P0.Y <= layer.height + 60f)
                {
                    DrawTriangle(head, color);
                }
            }

            // 3. Fork directional arrows & probability percentage badges (de-overlapped across multi-visit forks)
            _forkTagStack.Clear();
            _forkTagNextTopY.Clear();
            _forkExitLabelStack.Clear();
            _forkExitNextTopY.Clear();
            const float StackGapPx = 4f;
            for (int i = 0; i < plan.Forks.Count; i++)
            {
                var fork = plan.Forks[i];
                if (!TryProjectTile(fork.ForkLandId, out var forkPt)) continue;

                Color forkAccent = PickForkAccentColor(palette, fork);
                DrawDot(forkPt, 5.5f, forkAccent);

                _forkTagStack.TryGetValue(fork.ForkLandId, out int visitIndex);

                for (int e = 0; e < fork.Exits.Count; e++)
                {
                    var exit = fork.Exits[e];
                    if (!TryProjectTile(exit.NextLandId, out var exitPt)) continue;

                    Color exitColor = PickForkExitColor(palette, fork, exit);
                    float arrowLen = Math.Max(14f, settings.LineWidth * 3.5f);
                    float arrowHalfW = Math.Max(7f, settings.LineWidth * 1.8f);
                    if (DashGeometry.TryBuildArrowHead(forkPt, exitPt, arrowLen, arrowHalfW, out var arrow, 0.56f))
                    {
                        DrawTriangle(arrow, exitColor);
                    }

                    if (fork.HasMultipleBranches || settings.ShowDeterministicPercent)
                    {
                        long exitKey = ((long)fork.ForkLandId << 32) ^ (uint)exit.NextLandId;
                        _forkExitLabelStack.TryGetValue(exitKey, out int exitVisit);
                        _forkExitLabelStack[exitKey] = exitVisit + 1;

                        string pctText = (visitIndex > 0 || exitVisit > 0)
                            ? ("第" + fork.StepIndex + "步 " + exit.FormatConditionalPercent())
                            : exit.FormatConditionalPercent();

                        // Fixed edge base anchor per (ForkLandId, NextLandId), independent of visit-local exit index,
                        // stacked strictly upward by measured pill height so repeated visits can never collide.
                        var baseAnchor = DashGeometry.ComputeEdgeLabelAnchor(forkPt, exitPt, alongRatio: 0.56f, lateralOffset: 0f);
                        bool hasPriorTop = _forkExitNextTopY.TryGetValue(exitKey, out float prevTopY);
                        float nextTopY = DrawStackedPillLabel(baseAnchor, hasPriorTop, prevTopY, StackGapPx, pctText, settings.FontSize, exitColor);
                        _forkExitNextTopY[exitKey] = nextTopY;
                    }
                }

                if (settings.ShowReasonTag && !string.IsNullOrEmpty(fork.SummaryLabel))
                {
                    string reasonText = visitIndex > 0
                        ? ("第" + fork.StepIndex + "步·" + fork.SummaryLabel)
                        : fork.SummaryLabel;
                    var baseTagPos = new Point2(forkPt.X, forkPt.Y - settings.FontSize - 14f);
                    bool hasPriorTagTop = _forkTagNextTopY.TryGetValue(fork.ForkLandId, out float prevTagTopY);
                    float nextTagTopY = DrawStackedPillLabel(baseTagPos, hasPriorTagTop, prevTagTopY, StackGapPx, reasonText, Math.Max(12, settings.FontSize - 3), forkAccent);
                    _forkTagNextTopY[fork.ForkLandId] = nextTagTopY;
                }

                _forkTagStack[fork.ForkLandId] = visitIndex + 1;
            }

            // 4. Encounter / combat / steal markers on tiles occupied by living players
            for (int i = 0; i < plan.Combats.Count; i++)
            {
                var combat = plan.Combats[i];
                if (!TryProjectTile(combat.LandId, out var cPt)) continue;

                Color badgeColor = combat.IsSteal ? palette.MajorBranch : CombatBadgeColor;
                DrawDot(cPt, 7.5f, badgeColor);
                string prefix = combat.IsSteal ? "偷" : "⚔遇";
                string cLabel = prefix + combat.TargetLabel + "(第" + combat.StepIndex + "格)";
                DrawPillLabel(new Point2(cPt.X, cPt.Y + 20f), cLabel, Math.Max(12, settings.FontSize - 3), badgeColor);
            }

            // 5. Forward step count badges along the predicted path (0 at monster -> 1, 2, ... -> X格 at endpoint)
            if (settings.ShowStepNumbers && plan.TileSteps.Count > 0)
            {
                int stepFont = Math.Max(11, settings.FontSize - 5);
                int endFont = Math.Max(12, settings.FontSize - 3);

                for (int i = 0; i < plan.TileSteps.Count; i++)
                {
                    var ts = plan.TileSteps[i];
                    if (!TryProjectTile(ts.LandId, out var tilePt)) continue;

                    Color stepColor = ts.StepIndex == 0
                        ? RingColor
                        : PickEdgeColor(palette, ts.Probability, ts.IsAfterCombat);

                    if (ts.IsEndpoint)
                    {
                        DrawDot(tilePt, 6.5f, stepColor);
                        DrawPillLabel(tilePt, ts.FormatStepBadge(), endFont, stepColor, compact: false);
                    }
                    else
                    {
                        DrawPillLabel(tilePt, ts.FormatStepBadge(), stepFont, stepColor, compact: true);
                    }
                }
            }
            else
            {
                // Fallback endpoint-only markers when per-tile step numbers are disabled
                _combatLandSet.Clear();
                for (int i = 0; i < plan.Combats.Count; i++) _combatLandSet.Add(plan.Combats[i].LandId);

                for (int i = 0; i < plan.Endpoints.Count; i++)
                {
                    var ep = plan.Endpoints[i];
                    if (ep.StoppedAtCombat && _combatLandSet.Contains(ep.LandId)) continue;
                    if (!TryProjectTile(ep.LandId, out var epPt)) continue;

                    Color epColor = PickEdgeColor(palette, ep.Probability, ep.IsAfterCombat);
                    DrawDot(epPt, 6.5f, epColor);

                    string epLabel = ep.IsDeadEnd ? ("终点(" + ep.StepIndex + "格)") : (ep.StepIndex + "格");
                    float yOff = _combatLandSet.Contains(ep.LandId) ? -22f : 16f;
                    DrawPillLabel(new Point2(epPt.X, epPt.Y + yOff), epLabel, Math.Max(12, settings.FontSize - 4), epColor);
                }
            }
            }

            // 6. Virtual player ghost markers & real -> virtual tethers (visible even when no monster is selected)
            if (hasVirtualGhosts)
            {
                RenderVirtualGhosts(layer, camera, camPos, camForward, board, virtualGhosts, settings, hOffset, nearClipDist, phaseOffset);
            }

            for (int i = _usedLineGraphs; i < _lineGraphPool.Count; i++)
            {
                if (_lineGraphPool[i].visible) _lineGraphPool[i].visible = false;
            }
            for (int i = _usedPills; i < _pillPool.Count; i++)
            {
                if (_pillPool[i].Root.visible) _pillPool[i].Root.visible = false;
            }
        }

        private Vector3 GetStableHeaderAnchorWorld(LiveMonsterInfo selectedMonster)
        {
            Vector3 basePos = selectedMonster.FeetWorldPos;
            var vb = selectedMonster.VisualBounds;

            // Keep horizontal anchor synced to sprite center (handles stacked lateral offsets on the same tile),
            // while locking vertical height against idle animation bobbing so the header pill stays rock-steady.
            float x = vb.FromSprite ? vb.TopWorld.x : basePos.x;
            float z = vb.FromSprite ? vb.TopWorld.z : basePos.z;
            float measuredHeight = vb.FromSprite ? Math.Max(1.0f, vb.TopWorld.y - basePos.y) : 1.95f;

            long mid = selectedMonster.State != null ? selectedMonster.State.MonsterId : 0;
            if (mid != 0)
            {
                if (_stableHeaderMonsterId == mid)
                {
                    if (measuredHeight > _stableHeaderHeight + 0.08f || _stableHeaderHeight - measuredHeight > 0.60f)
                    {
                        _stableHeaderHeight = measuredHeight;
                    }
                    measuredHeight = _stableHeaderHeight;
                }
                else
                {
                    _stableHeaderMonsterId = mid;
                    _stableHeaderHeight = measuredHeight;
                }
            }

            return new Vector3(x, basePos.y + measuredHeight + 0.18f, z);
        }

        private void RenderVirtualGhosts(
            GComponent layer,
            Camera camera,
            Vector3 camPos,
            Vector3 camForward,
            BoardGraph board,
            IReadOnlyList<VirtualPlayerGhost> ghosts,
            PredictorSettings settings,
            float hOffset,
            float nearClipDist,
            float phaseOffset)
        {
            const float TetherDashLen = 8f;
            const float TetherGapLen = 6f;
            const double TetherPeriod = TetherDashLen + TetherGapLen;
            const float StackGapPx = 4f;
            int labelFont = Math.Max(12, settings.FontSize - 3);
            _ghostTileStack.Clear();
            _drawnGhostRingTiles.Clear();

            // First determine which virtual tiles have the currently edited player so their gold ring wins on shared tiles.
            for (int i = 0; i < ghosts.Count; i++)
            {
                if (!ghosts[i].IsSelectedForEdit) continue;
                int vid = ghosts[i].VirtualLandId;
                if (_drawnGhostRingTiles.Add(vid) &&
                    board.TryGetNode(vid, out var vn) &&
                    TryProjectWorld(layer, camera, camPos, camForward, new Vector3(vn.X, vn.Y + hOffset, vn.Z), out var selPt, nearClipDist))
                {
                    DrawDot(selPt, 11.5f, RingColor);
                    DrawDot(selPt, 5.5f, VirtualInnerDotColor);
                }
            }

            for (int i = 0; i < ghosts.Count; i++)
            {
                var ghost = ghosts[i];
                if (!board.TryGetNode(ghost.VirtualLandId, out var virtNode)) continue;

                // Dashed tether from real tile -> virtual tile
                if (ghost.RealLandId >= 0 && ghost.RealLandId != ghost.VirtualLandId &&
                    board.TryGetNode(ghost.RealLandId, out var realNode))
                {
                    if (DashGeometry.TryClipSegmentToNearPlane(
                            realNode.X, realNode.Y + hOffset, realNode.Z,
                            virtNode.X, virtNode.Y + hOffset, virtNode.Z,
                            camPos.x, camPos.y, camPos.z,
                            camForward.x, camForward.y, camForward.z,
                            nearClipDist,
                            out float cax, out float cay, out float caz,
                            out float cbx, out float cby, out float cbz,
                            out bool clippedNearStart, out bool clippedNearEnd) &&
                        TryProjectWorldUnclipped(layer, camera, camPos, camForward, new Vector3(cax, cay, caz), out var pReal, nearClipDist, skipDepthCheck: true) &&
                        TryProjectWorldUnclipped(layer, camera, camPos, camForward, new Vector3(cbx, cby, cbz), out var pVirtClip, nearClipDist, skipDepthCheck: true) &&
                        DashGeometry.TryClipSegmentToRect(
                            pReal, pVirtClip, -40f, -40f, layer.width + 40f, layer.height + 40f,
                            TetherPeriod,
                            out var clipFrom, out var clipTo,
                            out double clippedFromStartPx, out double clippedStartModPeriod, out double clippedEndPx))
                    {
                        double origTrim = clippedNearStart ? 0.0 : 8.0;
                        double origTrimEnd = clippedNearEnd ? 0.0 : 10.0;
                        double trimStart = Math.Max(0.0, origTrim - clippedFromStartPx);
                        double trimEnd = Math.Max(0.0, origTrimEnd - clippedEndPx);
                        float clippedPhase = DashGeometry.ComputeClippedDashPhase(phaseOffset * 0.65f, origTrim, clippedStartModPeriod, trimStart, TetherPeriod);

                        DashGeometry.BuildDashedSegmentsInto(
                            _dashQuadBuf,
                            clipFrom, clipTo,
                            TetherDashLen,
                            TetherGapLen,
                            Math.Max(2.5f, Math.Min(4.5f, settings.LineWidth * 0.72f)),
                            trimStart: (float)trimStart,
                            trimEnd: (float)trimEnd,
                            phaseOffset: clippedPhase);

                        for (int q = 0; q < _dashQuadBuf.Count; q++)
                        {
                            DrawQuad(_dashQuadBuf[q], VirtualTetherColor);
                        }
                    }
                }

                // Virtual position ring & label pill (stacked cleanly above any existing fork/ghost pills on this tile)
                var virtWorld = new Vector3(virtNode.X, virtNode.Y + hOffset, virtNode.Z);
                if (!TryProjectWorld(layer, camera, camPos, camForward, virtWorld, out var vPt, nearClipDist))
                    continue;

                Color accent = ghost.IsSelectedForEdit ? RingColor : VirtualGhostColor;
                if (_drawnGhostRingTiles.Add(ghost.VirtualLandId))
                {
                    DrawDot(vPt, 11.5f, accent);
                    DrawDot(vPt, 5.5f, VirtualInnerDotColor);
                }

                string selfTag = ghost.IsLocalPlayer ? "(我)" : string.Empty;
                string badge = "虚·" + ghost.SeatTag + selfTag + " #" + ghost.VirtualLandId;
                var basePos = new Point2(vPt.X, vPt.Y - 24f);
                bool hasPriorTop = _forkTagNextTopY.TryGetValue(ghost.VirtualLandId, out float priorTopY);
                float nextTopY = DrawStackedPillLabel(basePos, hasPriorTop, priorTopY, StackGapPx, badge, labelFont, accent);
                _forkTagNextTopY[ghost.VirtualLandId] = nextTopY;
            }
        }

        private static Color PickEdgeColor(RoutePalette palette, double probability, bool isAfterCombat, bool forceSplit = false)
        {
            bool isDeterministic = !forceSplit && probability >= 0.999999;
            if (isAfterCombat)
            {
                if (isDeterministic) return palette.PostCombat;
                var c = probability >= 0.50 ? palette.MajorBranch : palette.MinorBranch;
                // Apply configured PostCombat alpha consistently to post-combat split edges as well.
                return new Color(c.r, c.g, c.b, Math.Min(c.a, palette.PostCombat.a));
            }
            if (isDeterministic) return palette.Deterministic;
            if (probability >= 0.50) return palette.MajorBranch;
            return palette.MinorBranch;
        }

        private static Color PickForkAccentColor(RoutePalette palette, PredictedFork fork)
        {
            switch (fork.ReasonKind)
            {
                case ForkReasonKind.ForkStartRed:
                case ForkReasonKind.PostCombatRed:
                case ForkReasonKind.SkillTrashcanRed:
                    return palette.GuideRed;
                case ForkReasonKind.ForkStartBlue:
                case ForkReasonKind.PostCombatBlue:
                    return palette.GuideBlue;
                case ForkReasonKind.SkillLinglingRedBlue:
                    return (fork.Exits.Count > 0 && fork.Exits[0].IsGlobalRedArrow) ? palette.GuideRed : palette.GuideBlue;
                default:
                    if (fork.HasMultipleBranches)
                        return fork.IsAfterCombat
                            ? new Color(palette.MajorBranch.r, palette.MajorBranch.g, palette.MajorBranch.b, Math.Min(palette.MajorBranch.a, palette.PostCombat.a))
                            : palette.MajorBranch;
                    return fork.IsAfterCombat ? palette.PostCombat : palette.Deterministic;
            }
        }

        private static Color PickForkExitColor(RoutePalette palette, PredictedFork fork, ForkExitPrediction exit)
        {
            if (!fork.HasMultipleBranches && !exit.HasOtherPositiveExits)
            {
                return PickForkAccentColor(palette, fork);
            }
            return PickEdgeColor(palette, exit.ConditionalProbability, fork.IsAfterCombat, forceSplit: true);
        }

        private static bool TryProjectWorldUnclipped(
            GComponent layer,
            Camera camera,
            Vector3 camPos,
            Vector3 camForward,
            Vector3 world,
            out Point2 pt,
            float minDepth = 0.01f,
            bool skipDepthCheck = false)
        {
            pt = default;
            if (!skipDepthCheck &&
                DashGeometry.ComputeCameraDepth(world.x, world.y, world.z, camPos.x, camPos.y, camPos.z, camForward.x, camForward.y, camForward.z) < minDepth)
            {
                return false;
            }
            Vector2 local = layer.WorldToLocal(world, camera);
            if (float.IsNaN(local.x) || float.IsInfinity(local.x) || float.IsNaN(local.y) || float.IsInfinity(local.y))
                return false;
            pt = new Point2(local.x, local.y);
            return true;
        }

        private static bool TryProjectWorld(
            GComponent layer,
            Camera camera,
            Vector3 camPos,
            Vector3 camForward,
            Vector3 world,
            out Point2 pt,
            float minDepth = 0.01f)
        {
            if (!TryProjectWorldUnclipped(layer, camera, camPos, camForward, world, out pt, minDepth)) return false;
            if (pt.X < -300f || pt.X > layer.width + 300f || pt.Y < -300f || pt.Y > layer.height + 300f)
                return false;
            return true;
        }

        private void DrawQuad(DashQuad q, Color color)
        {
            float minX = Math.Min(Math.Min(q.P0.X, q.P1.X), Math.Min(q.P2.X, q.P3.X));
            float minY = Math.Min(Math.Min(q.P0.Y, q.P1.Y), Math.Min(q.P2.Y, q.P3.Y));
            float maxX = Math.Max(Math.Max(q.P0.X, q.P1.X), Math.Max(q.P2.X, q.P3.X));
            float maxY = Math.Max(Math.Max(q.P0.Y, q.P1.Y), Math.Max(q.P2.Y, q.P3.Y));

            float w = Math.Max(1f, maxX - minX);
            float h = Math.Max(1f, maxY - minY);
            _vertBuf.Clear();
            _vertBuf.Add(new Vector2(q.P0.X - minX, q.P0.Y - minY));
            _vertBuf.Add(new Vector2(q.P1.X - minX, q.P1.Y - minY));
            _vertBuf.Add(new Vector2(q.P2.X - minX, q.P2.Y - minY));
            _vertBuf.Add(new Vector2(q.P3.X - minX, q.P3.Y - minY));

            var g = AcquireLineGraph();
            g.SetXY(minX, minY);
            g.SetSize(w, h);
            g.DrawPolygon(w, h, _vertBuf, color);
            if (!g.visible) g.visible = true;
        }

        private void DrawTriangle(ArrowTriangle t, Color color)
        {
            float minX = Math.Min(t.P0.X, Math.Min(t.P1.X, t.P2.X));
            float minY = Math.Min(t.P0.Y, Math.Min(t.P1.Y, t.P2.Y));
            float maxX = Math.Max(t.P0.X, Math.Max(t.P1.X, t.P2.X));
            float maxY = Math.Max(t.P0.Y, Math.Max(t.P1.Y, t.P2.Y));

            float w = Math.Max(1f, maxX - minX);
            float h = Math.Max(1f, maxY - minY);
            _vertBuf.Clear();
            _vertBuf.Add(new Vector2(t.P0.X - minX, t.P0.Y - minY));
            _vertBuf.Add(new Vector2(t.P1.X - minX, t.P1.Y - minY));
            _vertBuf.Add(new Vector2(t.P2.X - minX, t.P2.Y - minY));

            var g = AcquireLineGraph();
            g.SetXY(minX, minY);
            g.SetSize(w, h);
            g.DrawPolygon(w, h, _vertBuf, color);
            if (!g.visible) g.visible = true;
        }

        private void DrawDot(Point2 center, float radius, Color color)
        {
            float d = radius * 2f;
            var g = AcquireLineGraph();
            g.SetXY(center.X - radius, center.Y - radius);
            g.SetSize(d, d);
            g.DrawEllipse(d, d, color);
            if (!g.visible) g.visible = true;
        }

        private PooledPill PreparePill(string text, int fontSize, Color textColor, bool compact)
        {
            var pill = AcquirePill();
            text = text ?? string.Empty;

            bool contentChanged = !pill.HasContent ||
                pill.LastFontSize != fontSize ||
                pill.LastCompact != compact ||
                !string.Equals(pill.LastText, text, StringComparison.Ordinal) ||
                !ColorsEqual(pill.LastColor, textColor);

            if (contentChanged)
            {
                float padX = compact ? 5f : 7f;
                float padY = compact ? 1.5f : 3f;

                pill.Text.textFormat = new TextFormat
                {
                    size = fontSize,
                    bold = true,
                    color = textColor,
                    outline = 1.5f,
                    outlineColor = Color.black
                };
                pill.Text.text = text;

                float h = (float)Math.Ceiling(Math.Max(compact ? 18f : 16f, pill.Text.height + padY * 2f));
                float minW = compact ? h : 18f;
                float w = (float)Math.Ceiling(Math.Max(minW, pill.Text.width + padX * 2f));

                pill.Root.SetSize(w, h);
                pill.Bg.SetSize(w, h);
                pill.Bg.DrawRoundRect(w, h, BadgeBg, PillCornerRadii);
                pill.Text.SetXY(
                    (float)Math.Round((w - pill.Text.width) * 0.5f),
                    (float)Math.Round((h - pill.Text.height) * 0.5f));

                pill.Width = w;
                pill.Height = h;
                pill.LastText = text;
                pill.LastFontSize = fontSize;
                pill.LastColor = textColor;
                pill.LastCompact = compact;
                pill.HasContent = true;
            }

            return pill;
        }

        private static void PlacePill(PooledPill pill, Point2 center)
        {
            float left = (float)Math.Round(center.X - pill.Width * 0.5f);
            float top = (float)Math.Round(center.Y - pill.Height * 0.5f);
            if (!pill.HasPos || Math.Abs(left - pill.LastX) >= 0.5f || Math.Abs(top - pill.LastY) >= 0.5f)
            {
                pill.Root.SetXY(left, top);
                pill.LastX = left;
                pill.LastY = top;
                pill.HasPos = true;
            }

            if (!pill.Root.visible) pill.Root.visible = true;
        }

        private void DrawPillLabel(Point2 center, string text, int fontSize, Color textColor, bool compact = false)
        {
            var pill = PreparePill(text, fontSize, textColor, compact);
            PlacePill(pill, center);
        }

        private float DrawStackedPillLabel(Point2 baseCenter, bool hasPriorTop, float priorTopY, float gapPx, string text, int fontSize, Color textColor)
        {
            var pill = PreparePill(text, fontSize, textColor, compact: false);
            float centerY = hasPriorTop
                ? (priorTopY - gapPx - pill.Height * 0.5f)
                : baseCenter.Y;
            PlacePill(pill, new Point2(baseCenter.X, centerY));
            return centerY - pill.Height * 0.5f;
        }

        private static bool ColorsEqual(Color a, Color b)
        {
            return Math.Abs(a.r - b.r) < 0.002f &&
                   Math.Abs(a.g - b.g) < 0.002f &&
                   Math.Abs(a.b - b.b) < 0.002f &&
                   Math.Abs(a.a - b.a) < 0.002f;
        }

        private GGraph AcquireLineGraph()
        {
            if (_usedLineGraphs < _lineGraphPool.Count)
            {
                return _lineGraphPool[_usedLineGraphs++];
            }

            var g = new GGraph { touchable = false };
            _lineLayer.AddChild(g);
            _lineGraphPool.Add(g);
            _usedLineGraphs++;
            return g;
        }

        private PooledPill AcquirePill()
        {
            if (_usedPills < _pillPool.Count)
            {
                return _pillPool[_usedPills++];
            }

            var root = new GComponent { touchable = false };
            var bg = new GGraph { touchable = false };
            var tf = new GTextField
            {
                touchable = false,
                autoSize = AutoSizeType.Both
            };

            // Fixed child order inside each pill container: bg (0) always beneath text (1); never mutates.
            root.AddChild(bg);
            root.AddChild(tf);
            _pillLayer.AddChild(root);

            var pill = new PooledPill
            {
                Root = root,
                Bg = bg,
                Text = tf
            };
            _pillPool.Add(pill);
            _usedPills++;
            return pill;
        }

        public void ClearStableHeaderCache()
        {
            _stableHeaderMonsterId = 0;
            _stableHeaderHeight = 0f;
        }

        public void Dispose()
        {
            for (int i = 0; i < _lineGraphPool.Count; i++)
            {
                try { _lineGraphPool[i].Dispose(); } catch { }
            }
            for (int i = 0; i < _pillPool.Count; i++)
            {
                try { _pillPool[i].Root.Dispose(); } catch { }
            }
            _lineGraphPool.Clear();
            _pillPool.Clear();
            ClearStableHeaderCache();
            _usedLineGraphs = 0;
            _usedPills = 0;

            _lineLayer = null;
            _pillLayer = null;

            if (_layer != null)
            {
                try { _layer.Dispose(); } catch { }
                _layer = null;
            }
        }
    }
}
