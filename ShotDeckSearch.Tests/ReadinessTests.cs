using ShotDeck.Keywords;

namespace ShotDeckSearch.Tests
{
    public class ReadinessTests
    {
        private static readonly TimeSpan FreshCsv = TimeSpan.FromMinutes(15);

        [Fact]
        public void CsvWarmWithoutWriteTimeIsNotReady()
        {
            var status = new KeywordCacheStatus { FlatCount = 97000, LastCsvWarmAt = DateTimeOffset.UtcNow };
            Assert.False(status.IsReady(1000, FreshCsv));
        }

        [Fact]
        public void StaleCsvIsNotReady()
        {
            var now = DateTimeOffset.UtcNow;
            var status = new KeywordCacheStatus { FlatCount = 97000, LastCsvWarmAt = now, CsvSnapshotWrittenAt = now.AddHours(-3) };
            Assert.False(status.IsReady(1000, FreshCsv));
        }

        [Fact]
        public void FreshCsvIsReady()
        {
            var now = DateTimeOffset.UtcNow;
            var status = new KeywordCacheStatus { FlatCount = 97000, LastCsvWarmAt = now, CsvSnapshotWrittenAt = now.AddSeconds(-40) };
            Assert.True(status.IsReady(1000, FreshCsv));
        }

        [Fact]
        public void FreshCsvBelowMinimumIsNotReady()
        {
            var now = DateTimeOffset.UtcNow;
            var status = new KeywordCacheStatus { FlatCount = 10, LastCsvWarmAt = now, CsvSnapshotWrittenAt = now.AddSeconds(-40) };
            Assert.False(status.IsReady(1000, FreshCsv));
        }

        [Fact]
        public void DatabaseRefreshBelowMinimumIsNotReady()
        {
            var status = new KeywordCacheStatus { FlatCount = 10, LastSuccessfulRefreshAt = DateTimeOffset.UtcNow };
            Assert.False(status.IsReady(1000, FreshCsv));
        }

        [Fact]
        public void DatabaseRefreshAboveMinimumIsReady()
        {
            var status = new KeywordCacheStatus { FlatCount = 97611, LastSuccessfulRefreshAt = DateTimeOffset.UtcNow };
            Assert.True(status.IsReady(1000, FreshCsv));
        }
    }
}
