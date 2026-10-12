using ShotDeck.Keywords;

namespace ShotDeckSearch.Tests
{
    public class ReadinessTests
    {
        [Fact]
        public void CsvWarmOnlyIsNotReady()
        {
            var status = new KeywordCacheStatus { FlatCount = 97000, LastCsvWarmAt = DateTimeOffset.UtcNow };
            Assert.False(status.IsReady(1000));
        }

        [Fact]
        public void DatabaseRefreshBelowMinimumIsNotReady()
        {
            var status = new KeywordCacheStatus { FlatCount = 10, LastSuccessfulRefreshAt = DateTimeOffset.UtcNow };
            Assert.False(status.IsReady(1000));
        }

        [Fact]
        public void DatabaseRefreshAboveMinimumIsReady()
        {
            var status = new KeywordCacheStatus { FlatCount = 97611, LastSuccessfulRefreshAt = DateTimeOffset.UtcNow };
            Assert.True(status.IsReady(1000));
        }
    }
}
