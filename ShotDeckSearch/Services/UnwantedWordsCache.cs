using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ShotDeck.Keywords
{
    public interface IUnwantedWordsCacheService
    {
        IReadOnlySet<string> GetUnwantedWords();
        IReadOnlySet<string> GetSuperBlacklistWords();
        bool IsUnwantedWord(string word);
        bool IsSuperBlacklistMatch(string text);
        UnwantedWordsCacheStatus GetStatus();
        Task RefreshAsync();
    }

    public sealed class UnwantedWordsCacheStatus
    {
        public DateTimeOffset? LastCsvWarmAt { get; init; }
        public DateTimeOffset? LastRefreshStartAt { get; init; }
        public DateTimeOffset? LastRefreshEndAt { get; init; }
        public bool IsRefreshRunning { get; init; }
        public bool LastRefreshSucceeded { get; init; }
        public string? LastRefreshError { get; init; }
        public int TotalCount { get; init; }
        public int SuperBlacklistCount { get; init; }
    }

    internal sealed class UnwantedWordsSnapshot
    {
        public readonly HashSet<string> AllWords;
        public readonly HashSet<string> SuperBlacklistWords;

        internal static readonly string CsvFile = "unwanted_words.csv";

        public static readonly UnwantedWordsSnapshot Empty = new(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        public UnwantedWordsSnapshot(HashSet<string> allWords, HashSet<string> superBlacklistWords)
        {
            AllWords = allWords;
            SuperBlacklistWords = superBlacklistWords;
        }
    }

    public class UnwantedWordsCacheService : IUnwantedWordsCacheService
    {
        private readonly IServiceProvider _serviceProvider;
        private UnwantedWordsSnapshot _snapshot = UnwantedWordsSnapshot.Empty;
        private readonly SemaphoreSlim _refreshGate = new(1, 1);

        private DateTimeOffset? _lastCsvWarmAt;
        private DateTimeOffset? _lastRefreshStartAt;
        private DateTimeOffset? _lastRefreshEndAt;
        private bool _lastRefreshSucceeded;
        private string? _lastRefreshError;

        public event Action<UnwantedWordsCacheStatus>? StatusChanged;

        private void PublishStatus()
        {
            try
            {
                StatusChanged?.Invoke(GetStatus());
            }
            catch { }
        }

        private readonly string _csvDir;

        public UnwantedWordsCacheService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
            var appDir = AppContext.BaseDirectory;
            _csvDir = Path.Combine(appDir, "keyword_cache");
            Directory.CreateDirectory(_csvDir);
            Console.WriteLine($"[UnwantedWordsCacheService] CSV dir: {_csvDir}");
        }

        public IReadOnlySet<string> GetUnwantedWords()
        {
            EnsureWarmOrCsvThenBackgroundRefresh();
            return _snapshot.AllWords;
        }

        public IReadOnlySet<string> GetSuperBlacklistWords()
        {
            EnsureWarmOrCsvThenBackgroundRefresh();
            return _snapshot.SuperBlacklistWords;
        }

        public bool IsUnwantedWord(string word)
        {
            EnsureWarmOrCsvThenBackgroundRefresh();
            if (string.IsNullOrWhiteSpace(word)) return false;
            return _snapshot.AllWords.Contains(word.Trim());
        }

        public bool IsSuperBlacklistMatch(string text)
        {
            EnsureWarmOrCsvThenBackgroundRefresh();
            if (string.IsNullOrWhiteSpace(text)) return false;

            var normalizedText = text.ToLowerInvariant();
            foreach (var blacklistWord in _snapshot.SuperBlacklistWords)
            {
                if (normalizedText.Contains(blacklistWord.ToLowerInvariant()))
                    return true;
            }
            return false;
        }

        public UnwantedWordsCacheStatus GetStatus()
        {
            var snap = _snapshot;
            return new UnwantedWordsCacheStatus
            {
                LastCsvWarmAt = _lastCsvWarmAt,
                LastRefreshStartAt = _lastRefreshStartAt,
                LastRefreshEndAt = _lastRefreshEndAt,
                IsRefreshRunning = (_refreshGate.CurrentCount == 0),
                LastRefreshSucceeded = _lastRefreshSucceeded,
                LastRefreshError = _lastRefreshError,
                TotalCount = snap.AllWords.Count,
                SuperBlacklistCount = snap.SuperBlacklistWords.Count
            };
        }

        private void EnsureWarmOrCsvThenBackgroundRefresh()
        {
            if (!ReferenceEquals(_snapshot, UnwantedWordsSnapshot.Empty)) return;

            if (TryWarmFromCsv(out var warmed))
            {
                Interlocked.Exchange(ref _snapshot, warmed);
                _lastCsvWarmAt = DateTimeOffset.UtcNow;
                PublishStatus();
                Console.WriteLine($"[UnwantedWordsCacheService] CSV re-warm complete. Total={warmed.AllWords.Count}, SuperBlacklist={warmed.SuperBlacklistWords.Count}");
            }
            else
            {
                Console.WriteLine("[UnwantedWordsCacheService] CSV re-warm unavailable; still Empty until refresh.");
            }

            _ = TriggerRefreshInBackground();
        }

        public async Task RefreshAsync()
        {
            await _refreshGate.WaitAsync().ConfigureAwait(false);
            try
            {
                _lastRefreshStartAt = DateTimeOffset.UtcNow;
                _lastRefreshError = null;
                _lastRefreshSucceeded = false;
                PublishStatus();

                Console.WriteLine("[UnwantedWordsCache] Refresh (public) started...");
                await RefreshCoreAsync().ConfigureAwait(false);
                _lastRefreshSucceeded = true;
                Console.WriteLine("[UnwantedWordsCache] Refresh (public) completed.");
            }
            catch (Exception ex)
            {
                _lastRefreshError = ex.ToString();
                _lastRefreshSucceeded = false;
                Console.Error.WriteLine($"[UnwantedWordsCache] Refresh (public) FAILED: {ex}");
                throw;
            }
            finally
            {
                _lastRefreshEndAt = DateTimeOffset.UtcNow;
                PublishStatus();
                _refreshGate.Release();
            }
        }

        private async Task TriggerRefreshInBackground()
        {
            if (!_refreshGate.Wait(0))
            {
                Console.WriteLine("[UnwantedWordsCache] Refresh already running; skip trigger.");
                return;
            }

            try
            {
                _lastRefreshStartAt = DateTimeOffset.UtcNow;
                _lastRefreshError = null;
                _lastRefreshSucceeded = false;
                PublishStatus();

                Console.WriteLine("[UnwantedWordsCache] Refresh (bg) started...");
                await RefreshCoreAsync().ConfigureAwait(false);
                _lastRefreshSucceeded = true;
                Console.WriteLine("[UnwantedWordsCache] Refresh (bg) completed.");
            }
            catch (Exception ex)
            {
                _lastRefreshError = ex.ToString();
                _lastRefreshSucceeded = false;
                Console.Error.WriteLine($"[UnwantedWordsCache] Refresh (bg) FAILED: {ex}");
            }
            finally
            {
                _lastRefreshEndAt = DateTimeOffset.UtcNow;
                PublishStatus();
                _refreshGate.Release();
            }
        }

        private async Task RefreshCoreAsync()
        {
            using var scope = _serviceProvider.CreateScope();
            var conn = scope.ServiceProvider.GetRequiredService<NpgsqlConnection>();

            var allWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var superBlacklistWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            const string sql = @"SELECT phrase, is_super_blacklist FROM frl.frl_keywords_unwanted_words;";

            using var cmd = new NpgsqlCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                if (reader.IsDBNull(0)) continue;

                var phrase = reader.GetString(0)?.Trim();
                if (string.IsNullOrWhiteSpace(phrase)) continue;

                allWords.Add(phrase);

                var isSuperBlacklist = !reader.IsDBNull(1) && reader.GetBoolean(1);
                if (isSuperBlacklist)
                    superBlacklistWords.Add(phrase);
            }

            Console.WriteLine($"[UnwantedWordsCache] Loaded: Total={allWords.Count}, SuperBlacklist={superBlacklistWords.Count}");

            var newSnapshot = new UnwantedWordsSnapshot(allWords, superBlacklistWords);
            SaveSnapshotToCsv(newSnapshot);
            Interlocked.Exchange(ref _snapshot, newSnapshot);
            PublishStatus();

            Console.WriteLine($"[UnwantedWordsCache] Snapshot swap: total={_snapshot.AllWords.Count}, superBlacklist={_snapshot.SuperBlacklistWords.Count}");
        }

        private bool TryWarmFromCsv(out UnwantedWordsSnapshot snapshot)
        {
            snapshot = UnwantedWordsSnapshot.Empty;
            try
            {
                var csvPath = Path.Combine(_csvDir, UnwantedWordsSnapshot.CsvFile);
                if (!File.Exists(csvPath)) return false;

                var (allWords, superBlacklistWords) = LoadFromCsv(csvPath);
                if (allWords.Count == 0) return false;

                snapshot = new UnwantedWordsSnapshot(allWords, superBlacklistWords);
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[UnwantedWordsCacheService] CSV warm failed: {ex}");
                return false;
            }
        }

        private static (HashSet<string> AllWords, HashSet<string> SuperBlacklistWords) LoadFromCsv(string path)
        {
            var allWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var superBlacklistWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.StartsWith("phrase,is_super_blacklist", StringComparison.OrdinalIgnoreCase)) continue;

                var (phrase, isSuperBlacklistStr) = SplitCsv2(line);
                if (string.IsNullOrWhiteSpace(phrase)) continue;

                phrase = Uncsv(phrase);
                allWords.Add(phrase);

                var isSuperBlacklist = string.Equals(Uncsv(isSuperBlacklistStr), "true", StringComparison.OrdinalIgnoreCase);
                if (isSuperBlacklist)
                    superBlacklistWords.Add(phrase);
            }

            return (allWords, superBlacklistWords);
        }

        private static (string A, string B) SplitCsv2(string line)
        {
            string a, b;
            int i = 0;

            static string ParseField(string s, ref int i)
            {
                var sb = new StringBuilder();
                if (i < s.Length && s[i] == '"')
                {
                    i++;
                    while (i < s.Length)
                    {
                        if (s[i] == '"' && i + 1 < s.Length && s[i + 1] == '"')
                        {
                            sb.Append('"');
                            i += 2;
                        }
                        else if (s[i] == '"')
                        {
                            i++;
                            break;
                        }
                        else
                        {
                            sb.Append(s[i++]);
                        }
                    }
                }
                else
                {
                    while (i < s.Length && s[i] != ',') sb.Append(s[i++]);
                }
                if (i < s.Length && s[i] == ',') i++;
                return sb.ToString();
            }

            a = ParseField(line, ref i);
            b = ParseField(line, ref i);
            return (a, b);
        }

        private static string Csv(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        private static string Uncsv(string s)
        {
            s = s.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
                return s.Substring(1, s.Length - 2).Replace("\"\"", "\"");
            return s;
        }

        private void SaveSnapshotToCsv(UnwantedWordsSnapshot snap)
        {
            try
            {
                Directory.CreateDirectory(_csvDir);

                var lines = new List<string>(snap.AllWords.Count + 1) { "phrase,is_super_blacklist" };

                foreach (var word in snap.AllWords.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var isSuperBlacklist = snap.SuperBlacklistWords.Contains(word);
                    lines.Add($"{Csv(word)},{(isSuperBlacklist ? "true" : "false")}");
                }

                File.WriteAllLines(Path.Combine(_csvDir, UnwantedWordsSnapshot.CsvFile), lines, Encoding.UTF8);
                Console.WriteLine($"[UnwantedWordsCacheService] Snapshot CSV written to {_csvDir}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[UnwantedWordsCacheService] Failed to write CSV: {ex}");
            }
        }
    }
}
