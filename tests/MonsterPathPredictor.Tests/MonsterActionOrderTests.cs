using System.Collections.Generic;
using MonsterPathPredictor.Core;
using Xunit;

namespace MonsterPathPredictor.Tests
{
    public class MonsterActionOrderTests
    {
        [Fact]
        public void SortsByActingNowThenUpcomingWeightAndSpawnSequenceThenActed()
        {
            var list = new List<MonsterState>
            {
                new MonsterState { MonsterId = 200, Name = "Boss", ActionWeight = 60, SpawnSequence = 1, HasActedThisRound = false },
                new MonsterState { MonsterId = 201, Name = "小怪", ActionWeight = 20, SpawnSequence = 2, HasActedThisRound = true },
                new MonsterState { MonsterId = 202, Name = "精英", ActionWeight = 40, SpawnSequence = 3, IsCurrentlyActing = true },
                new MonsterState { MonsterId = 203, Name = "小怪", ActionWeight = 20, SpawnSequence = 4, HasActedThisRound = false },
                new MonsterState { MonsterId = 204, Name = "未知权", ActionWeight = 0, SpawnSequence = 5, HasActedThisRound = false },
            };

            MonsterActionOrder.SortAndAssign(list, m => m);

            Assert.Equal(new long[] { 202, 203, 200, 204, 201 }, list.ConvertAll(m => m.MonsterId));
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, list.ConvertAll(m => m.ActionOrderRank));
        }

        [Fact]
        public void HandlesMonsterId100kWraparoundViaSpawnSequence()
        {
            // Real replay pattern (match 1788437771627793 R9):
            // Equal ActionWeight monsters with wrapped MonsterId act in spawn order (94124, 94126, 3995, 3996)
            var list = new List<MonsterState>
            {
                new MonsterState { MonsterId = 3996, Name = "迎宾燕", ActionWeight = 20, SpawnSequence = 4 },
                new MonsterState { MonsterId = 3995, Name = "迎宾燕", ActionWeight = 20, SpawnSequence = 3 },
                new MonsterState { MonsterId = 94126, Name = "迎宾燕", ActionWeight = 20, SpawnSequence = 2 },
                new MonsterState { MonsterId = 94124, Name = "迎宾燕", ActionWeight = 20, SpawnSequence = 1 },
            };

            MonsterActionOrder.SortAndAssign(list, m => m);

            Assert.Equal(new long[] { 94124, 94126, 3995, 3996 }, list.ConvertAll(m => m.MonsterId));
            Assert.Equal(new[] { "迎宾燕 1", "迎宾燕 2", "迎宾燕 3", "迎宾燕 4" }, list.ConvertAll(m => m.DisplayName));
        }

        [Fact]
        public void DuplicateNameSuffixesStayStableWhenEarlierMonsterFinishesActing()
        {
            var first = new MonsterState { MonsterId = 10, Name = "玩具恐龙", ActionWeight = 30, SpawnSequence = 1, HasActedThisRound = true };
            var second = new MonsterState { MonsterId = 11, Name = "玩具恐龙", ActionWeight = 30, SpawnSequence = 2, IsCurrentlyActing = true };
            var list = new List<MonsterState> { first, second };

            MonsterActionOrder.SortAndAssign(list, m => m);

            // Second acts first in the sorted display list, yet keeps its stable "玩具恐龙 2" suffix
            Assert.Equal(11, list[0].MonsterId);
            Assert.Equal("玩具恐龙 2", list[0].DisplayName);
            Assert.Equal(1, list[0].ActionOrderRank);

            Assert.Equal(10, list[1].MonsterId);
            Assert.Equal("玩具恐龙 1", list[1].DisplayName);
            Assert.Equal(2, list[1].ActionOrderRank);
        }
    }
}
