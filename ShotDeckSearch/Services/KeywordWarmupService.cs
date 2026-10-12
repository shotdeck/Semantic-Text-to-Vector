using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using ShotDeck.Keywords;

// Hosted services start before the server takes requests, so loading the CSV snapshot here
// means a restarted process (e.g. Azure's restart after a slot swap) serves a full cache from
// its first request. The DB refresh then runs in the background once the tunnel is up.
public class KeywordWarmupService : IHostedService
{
    private static readonly TimeSpan TunnelWait = TimeSpan.FromMinutes(2);

    private readonly IKeywordCacheService _keywordCache;
    private readonly SshTunnelService _tunnel;
    private readonly CancellationTokenSource _stopping = new();

    public KeywordWarmupService(IKeywordCacheService keywordCache, SshTunnelService tunnel)
    {
        _keywordCache = keywordCache;
        _tunnel = tunnel;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (_keywordCache.WarmFromCsvIfEmpty())
                Console.WriteLine($"✅ Keyword cache loaded from CSV snapshot in {sw.ElapsedMilliseconds}ms.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Failed to load keyword CSV snapshot: {ex.Message}");
        }

        _ = Task.Run(RefreshWhenTunnelReadyAsync);
        return Task.CompletedTask;
    }

    private async Task RefreshWhenTunnelReadyAsync()
    {
        try
        {
            // A refresh started before the forward exists fails and leaves the cache CSV-only.
            await _tunnel.TunnelReady.WaitAsync(TunnelWait, _stopping.Token);
        }
        catch (TimeoutException)
        {
            Console.WriteLine($"⚠️ SSH tunnel not ready after {TunnelWait.TotalSeconds:0}s; refreshing keyword cache anyway.");
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            Console.WriteLine("🔁 Refreshing keyword cache from database...");
            if (!_keywordCache.TryStartRefreshInBackground())
                Console.WriteLine("ℹ️ Keyword refresh already running.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Failed to start keyword refresh: {ex.Message}");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        return Task.CompletedTask;
    }
}
