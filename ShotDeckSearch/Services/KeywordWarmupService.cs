using Microsoft.Extensions.Hosting;
using ShotDeck.Keywords;

// Loads the keyword cache when the process starts instead of on the first request,
// so a restart (e.g. Azure's restart after a slot swap) never serves an empty cache.
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
        _ = Task.Run(WarmAsync);
        return Task.CompletedTask;
    }

    private async Task WarmAsync()
    {
        try
        {
            // The DB refresh that follows the CSV warm needs the tunnel up, or it fails and stays CSV-only.
            await _tunnel.TunnelReady.WaitAsync(TunnelWait, _stopping.Token);
        }
        catch (TimeoutException)
        {
            Console.WriteLine($"⚠️ SSH tunnel not ready after {TunnelWait.TotalSeconds:0}s; warming keyword cache anyway.");
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            Console.WriteLine("🔁 Warming up keyword cache...");
            _keywordCache.GetFlatKeywordSet();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Failed to warm keyword cache: {ex.Message}");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        return Task.CompletedTask;
    }
}
