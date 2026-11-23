using System.Linq;
using MonsterPathPredictor.Core;
using Xunit;

namespace MonsterPathPredictor.Tests
{
    public class BoardGraphTests
    {
        [Fact]
        public void PreservesRawNeighborOrderAndHandlesZeroId()
        {
            var graph = new BoardGraph(new[]
            {
                new LandNodeSnapshot(11, new[] { 0, 52, 12 }),
                new LandNodeSnapshot(0, new[] { 11 }),
                new LandNodeSnapshot(52, new[] { 11 }),
                new LandNodeSnapshot(12, new[] { 11 })
            });

            Assert.True(graph.IsFork(11));
            Assert.Equal(new[] { 0, 52, 12 }, graph.GetValidNeighbors(11));

            // Approaching from 12 -> Red is 0 (index 0)
            Assert.True(graph.TryGetPriorityForwardNeighbor(11, 12, out int nextFrom12, out bool isRedFrom12));
            Assert.Equal(0, nextFrom12);
            Assert.True(isRedFrom12);

            // Approaching from 0 (Red occupies incoming) -> Blue is 52 (index 1)
            Assert.True(graph.TryGetPriorityForwardNeighbor(11, 0, out int nextFrom0, out bool isRedFrom0));
            Assert.Equal(52, nextFrom0);
            Assert.False(isRedFrom0);
        }

        [Fact]
        public void FiltersDisabledMissingSelfAndDuplicateNeighbors()
        {
            var graph = new BoardGraph(new[]
            {
                new LandNodeSnapshot(10, new[] { 10, 11, 11, 12, 999, 13 }),
                new LandNodeSnapshot(11, new[] { 10 }),
                new LandNodeSnapshot(12, new[] { 10 }, disabled: true),
                new LandNodeSnapshot(13, new[] { 10 })
            });

            Assert.False(graph.IsFork(10));
            Assert.Equal(new[] { 11, 13 }, graph.GetValidNeighbors(10));
        }

        [Fact]
        public void ShortestPathCountsMultipleEqualPathsAccurately()
        {
            // Diamond: 1 -> {2, 3} -> 4 -> {5, 6} -> 7  (2 * 2 = 4 shortest paths of length 4 from 1 to 7)
            var graph = new BoardGraph(new[]
            {
                new LandNodeSnapshot(1, new[] { 2, 3 }),
                new LandNodeSnapshot(2, new[] { 1, 4 }),
                new LandNodeSnapshot(3, new[] { 1, 4 }),
                new LandNodeSnapshot(4, new[] { 2, 3, 5, 6 }),
                new LandNodeSnapshot(5, new[] { 4, 7 }),
                new LandNodeSnapshot(6, new[] { 4, 7 }),
                new LandNodeSnapshot(7, new[] { 5, 6 })
            });

            var field = graph.ComputeShortestPathsTo(7);
            Assert.Equal(4, field.GetDistance(1));
            Assert.Equal(4.0, field.GetPathCount(1));
            Assert.Equal(2.0, field.GetPathCount(2));
            Assert.Equal(2.0, field.GetPathCount(3));
        }
    }
}
