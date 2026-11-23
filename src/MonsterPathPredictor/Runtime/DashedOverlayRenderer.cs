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

        private GComponent _layer;
        private readonly List<GGraph> _graphPool = new List<GGraph>();
        private readonly List<GTextField> _textPool = new List<GTextField>();
        private int _usedGraphs;
        private int _usedTexts;

        public GComponent EnsureLayer()
        {
            if (_layer != null && !_layer.isDisposed && _layer.parent == GRoot.inst)
            {
                _layer.SetSize(GRoot.inst.width, GRoot.inst.height);
                return _layer;
            }

            Dispose();
            if (GRoot.inst == null) return null;

            _layer = new GComponent
            {
                name = "MonsterPathPredictor",
                touchable = false,
                sortingOrder = 55
            };
            GRoot.inst.AddChild(_layer);
            _layer.SetSize(GRoot.inst.width, GRoot.inst.height);
            return _layer;
        }

        public void HideAll()
        {
            for (int i = 0; i < _graphPool.Count; i++) _graphPool[i].visible = false;
            for (int i = 0; i < _textPool.Count; i++) _textPool[i].visible = false;
            _usedGraphs = 0;
            _usedTexts = 0;
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

            _usedGraphs = 0;
            _usedTexts = 0;

            var camTr = UnityObject.GetTransform(camera);
            var camPos = TransformService.GetPosition(camTr);
            var camForward = TransformService.GetForward(camTr);
            float hOffset = settings.HeightOffset;

            // Smooth forward-marching dash phase (px)
            float phaseOffset = settings.DashSpeed > 0.01f
                ? UnityTime.RealtimeSinceStartup * settings.DashSpeed
                : 0f;

            var screenCache = new Dictionary<int, Point2>();
            bool TryProjectTile(int landId, out Point2 pt)
            {
                if (screenCache.TryGetValue(landId, out pt)) return true;
                if (!board.TryGetNode(landId, out var node))
                {
                    pt = default;
                    return false;
                }
                var world = new Vector3(node.X, node.Y + hOffset, node.Z);
                if (!TryProjectWorld(layer, camera, camPos, camForward, world, out pt)) return false;
                screenCache[landId] = pt;
                return true;
            }

            // 1. Highlight dot on the selected monster's starting tile
            if (TryProjectTile(plan.StartLandId, out var startPt))
            {
                DrawDot(startPt, 8.5f, RingColor);
            }

            // 2. Animated forward-marching dashed lines for all predicted edges
            for (int i = 0; i < plan.Edges.Count; i++)
            {
                var edge = plan.Edges[i];
                if (!TryProjectTile(edge.FromLandId, out var pFrom)) continue;
                if (!TryProjectTile(edge.ToLandId, out var pTo)) continue;

                Color color = PickEdgeColor(edge.Probability, edge.IsAfterCombat);
                float baseWidth = edge.IsAfterCombat ? Math.Max(2.2f, settings.LineWidth * 0.78f) : settings.LineWidth;
                float width = edge.Probability >= 0.999
                    ? baseWidth
                    : Math.Max(2.0f, baseWidth * (0.65f + 0.35f * (float)edge.Probability));

                var quads = DashGeometry.BuildDashedSegments(
                    pFrom, pTo,
                    settings.DashLength,
                    settings.GapLength,
                    width,
                    trimStart: edge.EarliestStep == 0 ? 10f : 3f,
                    trimEnd: 3f,
                    phaseOffset: phaseOffset);

                for (int q = 0; q < quads.Count; q++)
                {
                    DrawPolygon(quads[q], color);
                }

                // Draw directional chevron on the very first step out of the monster
                if (edge.EarliestStep == 0)
                {
                    var head = DashGeometry.BuildArrowHead(pFrom, pTo, Math.Max(12f, width * 3.1f), Math.Max(6f, width * 1.6f), 0.60f);
                    if (head.Length >= 3) DrawPolygon(head, color);
                }
            }

            // 3. Fork directional arrows & probability percentage badges (guide-aligned colors)
            for (int i = 0; i < plan.Forks.Count; i++)
            {
                var fork = plan.Forks[i];
                if (!TryProjectTile(fork.ForkLandId, out var forkPt)) continue;

                Color forkAccent = PickForkAccentColor(fork);
                DrawDot(forkPt, 5.5f, forkAccent);

                for (int e = 0; e < fork.Exits.Count; e++)
                {
                    var exit = fork.Exits[e];
                    if (!TryProjectTile(exit.NextLandId, out var exitPt)) continue;

                    Color exitColor = PickForkExitColor(fork, exit);
                    float arrowLen = Math.Max(14f, settings.LineWidth * 3.5f);
                    float arrowHalfW = Math.Max(7f, settings.LineWidth * 1.8f);
                    var arrow = DashGeometry.BuildArrowHead(forkPt, exitPt, arrowLen, arrowHalfW, 0.56f);
                    if (arrow.Length >= 3) DrawPolygon(arrow, exitColor);

                    if (fork.HasMultipleBranches || settings.ShowDeterministicPercent)
                    {
                        string pctText = exit.FormatConditionalPercent();
                        var anchor = DashGeometry.ComputeEdgeLabelAnchor(forkPt, exitPt, alongRatio: 0.56f, lateralOffset: e == 0 ? -15f : 15f);
                        DrawPillLabel(anchor, pctText, settings.FontSize, exitColor);
                    }
                }

                if (settings.ShowReasonTag && !string.IsNullOrEmpty(fork.SummaryLabel))
                {
                    var tagPos = new Point2(forkPt.X, forkPt.Y - settings.FontSize - 14f);
                    DrawPillLabel(tagPos, fork.SummaryLabel, Math.Max(12, settings.FontSize - 3), forkAccent);
                }
            }

            // 4. Encounter / combat markers on tiles occupied by living players
            for (int i = 0; i < plan.Combats.Count; i++)
            {
                var combat = plan.Combats[i];
                if (!TryProjectTile(combat.LandId, out var cPt)) continue;

                DrawDot(cPt, 7.5f, CombatBadgeColor);
                string cLabel = "⚔遇" + combat.TargetLabel + "(" + combat.StepIndex + "格)";
                DrawPillLabel(new Point2(cPt.X, cPt.Y + 18f), cLabel, Math.Max(12, settings.FontSize - 3), CombatBadgeColor);
            }

            // 5. Endpoint markers at the end of X steps (skip if a combat marker already sits on that exact tile)
            var combatLands = new HashSet<int>();
            for (int i = 0; i < plan.Combats.Count; i++) combatLands.Add(plan.Combats[i].LandId);

            for (int i = 0; i < plan.Endpoints.Count; i++)
            {
                var ep = plan.Endpoints[i];
                if (ep.StoppedAtCombat && combatLands.Contains(ep.LandId)) continue;
                if (!TryProjectTile(ep.LandId, out var epPt)) continue;

                Color epColor = PickEdgeColor(ep.Probability, ep.IsAfterCombat);
                DrawDot(epPt, 6.5f, epColor);

                string epLabel = ep.IsDeadEnd ? "终点" : (ep.StepIndex + "格");
                float yOff = combatLands.Contains(ep.LandId) ? -22f : 16f;
                DrawPillLabel(new Point2(epPt.X, epPt.Y + yOff), epLabel, Math.Max(12, settings.FontSize - 4), epColor);
            }

            // 6. Selected monster header badge cleanly above the actual sprite top
            var bounds = selectedMonster.CharacterRef != null
                ? SpriteBoundsReader.Capture(selectedMonster.CharacterRef, selectedMonster.FeetWorldPos, camera)
                : selectedMonster.VisualBounds;
            Vector3 headerWorld = new Vector3(bounds.TopWorld.x, bounds.TopWorld.y + 0.18f, bounds.TopWorld.z);
            if (TryProjectWorld(layer, camera, camPos, camForward, headerWorld, out var headerPt))
            {
                string headerText = plan.MonsterName + " · " + plan.StepsRequested + "格";
                if (!string.IsNullOrEmpty(plan.HeadlineNote))
                    headerText += " (" + plan.HeadlineNote + ")";
                DrawPillLabel(new Point2(headerPt.X, headerPt.Y - 12f), headerText, Math.Max(13, settings.FontSize - 2), RingColor);
            }

            for (int i = _usedGraphs; i < _graphPool.Count; i++) _graphPool[i].visible = false;
            for (int i = _usedTexts; i < _textPool.Count; i++) _textPool[i].visible = false;
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

        private static bool TryProjectWorld(
            GComponent layer,
            Camera camera,
            Vector3 camPos,
            Vector3 camForward,
            Vector3 world,
            out Point2 pt)
        {
            pt = default;
            if (Vector3.Dot(world - camPos, camForward) <= 0.1f) return false;
            Vector2 local = layer.WorldToLocal(world, camera);
            if (float.IsNaN(local.x) || float.IsInfinity(local.x) || float.IsNaN(local.y) || float.IsInfinity(local.y))
                return false;
            if (local.x < -300f || local.x > layer.width + 300f || local.y < -300f || local.y > layer.height + 300f)
                return false;
            pt = new Point2(local.x, local.y);
            return true;
        }

        private void DrawPolygon(Point2[] points, Color color)
        {
            if (points == null || points.Length < 3) return;

            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < points.Length; i++)
            {
                if (points[i].X < minX) minX = points[i].X;
                if (points[i].Y < minY) minY = points[i].Y;
                if (points[i].X > maxX) maxX = points[i].X;
                if (points[i].Y > maxY) maxY = points[i].Y;
            }

            float w = Math.Max(1f, maxX - minX);
            float h = Math.Max(1f, maxY - minY);
            var verts = new List<Vector2>(points.Length);
            for (int i = 0; i < points.Length; i++)
            {
                verts.Add(new Vector2(points[i].X - minX, points[i].Y - minY));
            }

            var g = AcquireGraph();
            g.SetXY(minX, minY);
            g.SetSize(w, h);
            g.DrawPolygon(w, h, verts, color);
            g.visible = true;
        }

        private void DrawDot(Point2 center, float radius, Color color)
        {
            float d = radius * 2f;
            var g = AcquireGraph();
            g.SetXY(center.X - radius, center.Y - radius);
            g.SetSize(d, d);
            g.DrawEllipse(d, d, color);
            g.visible = true;
        }

        private void DrawPillLabel(Point2 center, string text, int fontSize, Color textColor)
        {
            var tf = AcquireText();
            tf.textFormat = new TextFormat
            {
                size = fontSize,
                bold = true,
                color = textColor,
                outline = 1.5f,
                outlineColor = Color.black
            };
            tf.text = text;
            float padX = 7f;
            float padY = 3f;
            float w = Math.Max(18f, tf.width + padX * 2f);
            float h = Math.Max(16f, tf.height + padY * 2f);
            float left = center.X - w * 0.5f;
            float top = center.Y - h * 0.5f;

            var bg = AcquireGraph();
            bg.SetXY(left, top);
            bg.SetSize(w, h);
            bg.DrawRoundRect(w, h, BadgeBg, new[] { 6f, 6f, 6f, 6f });
            bg.visible = true;

            _layer.SetChildIndex(tf, _layer.numChildren - 1);
            tf.SetXY(left + padX, top + padY);
            tf.visible = true;
        }

        private GGraph AcquireGraph()
        {
            if (_usedGraphs < _graphPool.Count)
            {
                return _graphPool[_usedGraphs++];
            }

            var g = new GGraph { touchable = false };
            _layer.AddChild(g);
            _graphPool.Add(g);
            _usedGraphs++;
            return g;
        }

        private GTextField AcquireText()
        {
            if (_usedTexts < _textPool.Count)
            {
                return _textPool[_usedTexts++];
            }

            var tf = new GTextField
            {
                touchable = false,
                autoSize = AutoSizeType.Both
            };
            _layer.AddChild(tf);
            _textPool.Add(tf);
            _usedTexts++;
            return tf;
        }

        public void Dispose()
        {
            for (int i = 0; i < _graphPool.Count; i++)
            {
                try { _graphPool[i].Dispose(); } catch { }
            }
            for (int i = 0; i < _textPool.Count; i++)
            {
                try { _textPool[i].Dispose(); } catch { }
            }
            _graphPool.Clear();
            _textPool.Clear();
            _usedGraphs = 0;
            _usedTexts = 0;

            if (_layer != null)
            {
                try { _layer.Dispose(); } catch { }
                _layer = null;
            }
        }
    }
}
