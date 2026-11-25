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
        private static readonly Color DeterministicColor = new Color(0.24f, 0.90f, 1.00f, 0.96f);      // Bright cyan
        private static readonly Color PostCombatCyan = new Color(0.58f, 0.78f, 0.95f, 0.56f);          // Softer post-combat continuation
        private static readonly Color MajorBranchColor = new Color(1.00f, 0.82f, 0.22f, 0.96f);        // Bright gold
        private static readonly Color MinorBranchColor = new Color(1.00f, 0.48f, 0.28f, 0.88f);        // Warm coral
        private static readonly Color GuideRedArrow = new Color(1.00f, 0.32f, 0.32f, 0.98f);           // Guide red arrow
        private static readonly Color GuideBlueArrow = new Color(0.26f, 0.68f, 1.00f, 0.98f);          // Guide blue arrow
        private static readonly Color CombatBadgeColor = new Color(1.00f, 0.40f, 0.34f, 0.98f);        // Encounter marker
        private static readonly Color RingColor = new Color(1.00f, 0.88f, 0.25f, 0.95f);               // Selection ring
        private static readonly Color BadgeBg = new Color(0.06f, 0.09f, 0.14f, 0.86f);                 // Dark pill
        private static readonly float[] PillCornerRadii = { 6f, 6f, 6f, 6f };

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
        private readonly HashSet<int> _combatLandSet = new HashSet<int>();
        private readonly List<DashQuad> _dashQuadBuf = new List<DashQuad>(32);
        private readonly List<Vector2> _vertBuf = new List<Vector2>(4);
        private int _usedLineGraphs;
        private int _usedPills;

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
                if (_lineGraphPool[i].visible) _lineGraphPool[i].visible = false;
            }
            for (int i = 0; i < _pillPool.Count; i++)
            {
                if (_pillPool[i].Root.visible) _pillPool[i].Root.visible = false;
            }
            _usedLineGraphs = 0;
            _usedPills = 0;
        }

        public void Render(
            Camera camera,
            BoardGraph board,
            LiveMonsterInfo selectedMonster,
            PathPlan plan,
            PredictorSettings settings)
        {
            var layer = EnsureLayer();
            if (layer == null || camera == null || board == null || selectedMonster == null || plan == null)
            {
                HideAll();
                return;
            }

            _usedLineGraphs = 0;
            _usedPills = 0;

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

            // 0. Selected monster header badge FIRST (always Pill #0 so its cached FairyGUI mesh never swaps)
            Vector3 headerWorld = GetStableHeaderAnchorWorld(selectedMonster);
            if (TryProjectWorld(layer, camera, camPos, camForward, headerWorld, out var headerPt, nearClipDist))
            {
                string headerText = !string.IsNullOrEmpty(plan.HeadlineNote)
                    ? plan.MonsterName + " (" + plan.HeadlineNote + ")"
                    : plan.MonsterName;
                DrawPillLabel(new Point2(headerPt.X, headerPt.Y - 12f), headerText, Math.Max(13, settings.FontSize - 2), RingColor);
            }

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

            // 1. Highlight dot on the selected monster's starting tile
            if (TryProjectTile(plan.StartLandId, out var startPt))
            {
                DrawDot(startPt, 8.5f, RingColor);
            }

            // 2. Animated forward-marching dashed lines for all predicted edges:
            //    first clip 3D segment against camera near plane in double precision, then project
            //    already-verified endpoints directly (no redundant float depth re-check), clip 2D
            //    via exact float-rational Liang-Barsky, and preserve exact dash phase in 106-bit double-double.
            double dashPeriod = (double)settings.DashLength + Math.Max(0.0, settings.GapLength);

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

                if (!DashGeometry.TryClipSegmentToRect(pFrom, pTo, -40f, -40f, layer.width + 40f, layer.height + 40f,
                        out var clipFrom, out var clipTo, out double clippedFromStartHi, out double clippedFromStartLo))
                    continue;

                Color color = PickEdgeColor(edge.Probability, edge.IsAfterCombat);
                float baseWidth = edge.IsAfterCombat ? Math.Max(2.2f, settings.LineWidth * 0.78f) : settings.LineWidth;
                float width = edge.Probability >= 0.999
                    ? baseWidth
                    : Math.Max(2.0f, baseWidth * (0.65f + 0.35f * (float)edge.Probability));

                double clippedFromStartPx = clippedFromStartHi + clippedFromStartLo;
                double origTrimStart = clippedNearStart ? 0.0 : (edge.EarliestStep == 0 ? 10.0 : 3.0);
                double origTrimEnd = clippedNearEnd ? 0.0 : 3.0;
                double trimStart = Math.Max(0.0, origTrimStart - clippedFromStartPx);
                double fullEdgeLen = Math.Sqrt(((double)pTo.X - pFrom.X) * ((double)pTo.X - pFrom.X) +
                                               ((double)pTo.Y - pFrom.Y) * ((double)pTo.Y - pFrom.Y));
                double clipLen = Math.Sqrt(((double)clipTo.X - clipFrom.X) * ((double)clipTo.X - clipFrom.X) +
                                           ((double)clipTo.Y - clipFrom.Y) * ((double)clipTo.Y - clipFrom.Y));
                double clippedEndPx = Math.Max(0.0, fullEdgeLen - clippedFromStartPx - clipLen);
                double trimEnd = Math.Max(0.0, origTrimEnd - clippedEndPx);
                float clippedPhase = DashGeometry.ComputeClippedDashPhase(phaseOffset, origTrimStart, clippedFromStartHi, clippedFromStartLo, trimStart, dashPeriod);

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

                Color forkAccent = PickForkAccentColor(fork);
                DrawDot(forkPt, 5.5f, forkAccent);

                _forkTagStack.TryGetValue(fork.ForkLandId, out int visitIndex);

                for (int e = 0; e < fork.Exits.Count; e++)
                {
                    var exit = fork.Exits[e];
                    if (!TryProjectTile(exit.NextLandId, out var exitPt)) continue;

                    Color exitColor = PickForkExitColor(fork, exit);
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

                Color badgeColor = combat.IsSteal ? MajorBranchColor : CombatBadgeColor;
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
                        : PickEdgeColor(ts.Probability, ts.IsAfterCombat);

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

                    Color epColor = PickEdgeColor(ep.Probability, ep.IsAfterCombat);
                    DrawDot(epPt, 6.5f, epColor);

                    string epLabel = ep.IsDeadEnd ? ("终点(" + ep.StepIndex + "格)") : (ep.StepIndex + "格");
                    float yOff = _combatLandSet.Contains(ep.LandId) ? -22f : 16f;
                    DrawPillLabel(new Point2(epPt.X, epPt.Y + yOff), epLabel, Math.Max(12, settings.FontSize - 4), epColor);
                }
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

        private static Color PickEdgeColor(double probability, bool isAfterCombat)
        {
            if (isAfterCombat)
            {
                if (probability >= 0.999) return PostCombatCyan;
                var c = probability >= 0.50 ? MajorBranchColor : MinorBranchColor;
                return new Color(c.r, c.g, c.b, 0.55f);
            }
            if (probability >= 0.999) return DeterministicColor;
            if (probability >= 0.50) return MajorBranchColor;
            return MinorBranchColor;
        }

        private static Color PickForkAccentColor(PredictedFork fork)
        {
            switch (fork.ReasonKind)
            {
                case ForkReasonKind.ForkStartRed:
                case ForkReasonKind.PostCombatRed:
                case ForkReasonKind.SkillTrashcanRed:
                    return GuideRedArrow;
                case ForkReasonKind.ForkStartBlue:
                case ForkReasonKind.PostCombatBlue:
                    return GuideBlueArrow;
                case ForkReasonKind.SkillLinglingRedBlue:
                    return (fork.Exits.Count > 0 && fork.Exits[0].IsGlobalRedArrow) ? GuideRedArrow : GuideBlueArrow;
                default:
                    if (fork.HasMultipleBranches)
                        return fork.IsAfterCombat ? new Color(MajorBranchColor.r, MajorBranchColor.g, MajorBranchColor.b, 0.72f) : MajorBranchColor;
                    return fork.IsAfterCombat ? PostCombatCyan : DeterministicColor;
            }
        }

        private static Color PickForkExitColor(PredictedFork fork, ForkExitPrediction exit)
        {
            if (!fork.HasMultipleBranches)
            {
                return PickForkAccentColor(fork);
            }
            return PickEdgeColor(exit.ConditionalProbability, fork.IsAfterCombat);
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
