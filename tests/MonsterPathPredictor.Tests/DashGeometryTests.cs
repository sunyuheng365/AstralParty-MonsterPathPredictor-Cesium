using System.Collections.Generic;
using MonsterPathPredictor.Core;
using Xunit;

namespace MonsterPathPredictor.Tests
{
    public class DashGeometryTests
    {
        [Theory]
        [InlineData(0f, 0f, 120f, 0f, 0f)]
        [InlineData(0f, 0f, 120f, 0f, 5f)]
        [InlineData(0f, 0f, 120f, 0f, 14.5f)]
        [InlineData(0f, 0f, 0f, 120f, 7f)]
        [InlineData(120f, 80f, -40f, -60f, 19.9f)]
        [InlineData(-50f, 20f, 90f, -110f, -6f)]
        public void AnimatedDashedSegmentsHavePositiveWindingAndFiniteCoordinates(
            float x0, float y0, float x1, float y1, float phaseOffset)
        {
            var quads = DashGeometry.BuildDashedSegments(
                new Point2(x0, y0),
                new Point2(x1, y1),
                dashLength: 12f,
                gapLength: 8f,
                thickness: 4f,
                trimStart: 4f,
                trimEnd: 4f,
                phaseOffset: phaseOffset);

            Assert.NotEmpty(quads);
            foreach (var quad in quads)
            {
                Assert.Equal(4, quad.Length);
                Assert.True(DashGeometry.SignedArea(quad) > 0f, "FairyGUI PolygonMesh requires positive signed area.");
                foreach (var p in quad)
                {
                    Assert.True(float.IsFinite(p.X) && float.IsFinite(p.Y));
                }
            }
        }

        [Fact]
        public void IncreasingPhaseOffsetShiftsDashesForwardAlongDirection()
        {
            var start = new Point2(0f, 0f);
            var end = new Point2(100f, 0f);
            var q0 = DashGeometry.BuildDashedSegments(start, end, 12f, 8f, 4f, 0f, 0f, phaseOffset: 0f);
            var q4 = DashGeometry.BuildDashedSegments(start, end, 12f, 8f, 4f, 0f, 0f, phaseOffset: 4f);
            var q12 = DashGeometry.BuildDashedSegments(start, end, 12f, 8f, 4f, 0f, 0f, phaseOffset: 12f);

            // At phase 0, first dash sits at [0, 12]
            Assert.Equal(0f, System.Math.Min(q0[0][0].X, q0[0][2].X), 2);
            Assert.Equal(12f, System.Math.Max(q0[0][0].X, q0[0][2].X), 2);

            // At phase 4 (within 8px gap), first dash shifts forward by +4px to [4, 16]
            Assert.Equal(4f, System.Math.Min(q4[0][0].X, q4[0][2].X), 2);
            Assert.Equal(16f, System.Math.Max(q4[0][0].X, q4[0][2].X), 2);

            // At phase 12 (past 8px gap), a new dash smoothly emerges at [0, 4] and next sits at [12, 24]
            Assert.Equal(0f, System.Math.Min(q12[0][0].X, q12[0][2].X), 2);
            Assert.Equal(4f, System.Math.Max(q12[0][0].X, q12[0][2].X), 2);
            Assert.Equal(12f, System.Math.Min(q12[1][0].X, q12[1][2].X), 2);
            Assert.Equal(24f, System.Math.Max(q12[1][0].X, q12[1][2].X), 2);
        }

        [Fact]
        public void DegenerateSegmentProducesNoQuads()
        {
            var quads = DashGeometry.BuildDashedSegments(new Point2(10f, 10f), new Point2(10f, 10f), 12f, 8f, 4f);
            Assert.Empty(quads);
        }

        [Theory]
        [InlineData(0f, 0f, 100f, 0f)]
        [InlineData(100f, 100f, 0f, 20f)]
        public void ArrowHeadHasThreeVerticesAndPositiveWinding(float x0, float y0, float x1, float y1)
        {
            var head = DashGeometry.BuildArrowHead(new Point2(x0, y0), new Point2(x1, y1), 16f, 8f);
            Assert.Equal(3, head.Length);
            Assert.True(DashGeometry.SignedArea(head) > 0f);
            foreach (var p in head)
            {
                Assert.True(float.IsFinite(p.X) && float.IsFinite(p.Y));
            }
        }

        [Fact]
        public void HitTesterMatchesClickAnywhereOnMonsterModelIncludingHead()
        {
            // Tall sprite standing from y=100 (head) to y=260 (feet)
            var boxes = new List<MonsterHitBox>
            {
                new MonsterHitBox
                {
                    MonsterId = 42,
                    Rect = new HitRect2D(200f, 100f, 280f, 260f),
                    CameraDepth = 10f
                }
            };

            // Clicking the head (top of sprite, far from feet!) must hit monster 42
            Assert.Equal(42, MonsterHitTester.PickMonster(new Point2(240f, 110f), boxes, nearMissRadius: 20f));
            // Clicking the torso/wings hits monster 42
            Assert.Equal(42, MonsterHitTester.PickMonster(new Point2(210f, 175f), boxes, nearMissRadius: 20f));
            // Clicking far away misses
            Assert.Equal(0, MonsterHitTester.PickMonster(new Point2(50f, 50f), boxes, nearMissRadius: 20f));
        }

        [Fact]
        public void HitTesterDisambiguatesStackedSideBySideMonstersOnSameTile()
        {
            // Two monsters stacked side-by-side on the same tile (nearly identical camera depth, overlapping middle edge)
            var boxes = new List<MonsterHitBox>
            {
                new MonsterHitBox
                {
                    MonsterId = 101,
                    Rect = new HitRect2D(180f, 100f, 250f, 240f), // Left monster (center x=215)
                    CameraDepth = 12.0f
                },
                new MonsterHitBox
                {
                    MonsterId = 102,
                    Rect = new HitRect2D(230f, 100f, 300f, 240f), // Right monster (center x=265)
                    CameraDepth = 12.05f
                }
            };

            // Click clearly on left monster
            Assert.Equal(101, MonsterHitTester.PickMonster(new Point2(200f, 150f), boxes, 20f));
            // Click clearly on right monster
            Assert.Equal(102, MonsterHitTester.PickMonster(new Point2(280f, 150f), boxes, 20f));
            // Click inside overlap zone closer to left monster's center (x=235 vs 215/265) -> left wins
            Assert.Equal(101, MonsterHitTester.PickMonster(new Point2(235f, 150f), boxes, 20f));
            // Click inside overlap zone closer to right monster's center (x=245 vs 215/265) -> right wins
            Assert.Equal(102, MonsterHitTester.PickMonster(new Point2(245f, 150f), boxes, 20f));
        }

        [Fact]
        public void HitTesterPrefersCloserToCameraMonsterWhenOverlappingInDepth()
        {
            var boxes = new List<MonsterHitBox>
            {
                new MonsterHitBox
                {
                    MonsterId = 201,
                    Rect = new HitRect2D(200f, 100f, 280f, 240f),
                    CameraDepth = 15.0f // Background monster
                },
                new MonsterHitBox
                {
                    MonsterId = 202,
                    Rect = new HitRect2D(210f, 110f, 290f, 250f),
                    CameraDepth = 10.0f // Foreground monster (closer to camera)
                }
            };

            Assert.Equal(202, MonsterHitTester.PickMonster(new Point2(245f, 175f), boxes, 20f));
        }
    }
}
