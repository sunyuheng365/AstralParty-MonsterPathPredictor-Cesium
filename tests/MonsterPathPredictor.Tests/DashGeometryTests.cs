using MonsterPathPredictor.Core;
using Xunit;

namespace MonsterPathPredictor.Tests
{
    public class DashGeometryTests
    {
        [Theory]
        [InlineData(0f, 0f, 120f, 0f)]
        [InlineData(0f, 0f, 0f, 120f)]
        [InlineData(120f, 80f, -40f, -60f)]
        [InlineData(-50f, 20f, 90f, -110f)]
        public void DashedSegmentsHavePositiveWindingAndFiniteCoordinates(float x0, float y0, float x1, float y1)
        {
            var quads = DashGeometry.BuildDashedSegments(
                new Point2(x0, y0),
                new Point2(x1, y1),
                dashLength: 12f,
                gapLength: 8f,
                thickness: 4f,
                trimStart: 4f,
                trimEnd: 4f);

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
    }
}
