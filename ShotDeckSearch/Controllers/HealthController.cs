using Microsoft.AspNetCore.Mvc;
using Npgsql;
using ShotDeck.Keywords;

namespace ShotDeckSearch.Controllers
{
    /// <summary>
    /// Operational diagnostics for the release pipeline: tunnel state plus a
    /// real <c>SELECT 1</c> through it. Never returns secrets.
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    public class HealthController : ControllerBase
    {
        private readonly SshTunnelService _tunnel;
        private readonly IConfiguration _configuration;
        private readonly IKeywordCacheService _keywordCache;

        public HealthController(SshTunnelService tunnel, IConfiguration configuration, IKeywordCacheService keywordCache)
        {
            _tunnel = tunnel;
            _configuration = configuration;
            _keywordCache = keywordCache;
        }

        /// <summary>
        /// 200 once the keyword cache has loaded from the database, 503 until then.
        /// Azure's slot swap polls this (WEBSITE_SWAP_WARMUP_PING_PATH) so a
        /// restarted process never takes production traffic on the CSV snapshot.
        /// </summary>
        [HttpGet("ready")]
        public IActionResult GetReadiness()
        {
            // Touching the cache starts the CSV warm + background DB refresh in a fresh process.
            _keywordCache.GetFlatKeywordSet();
            var status = _keywordCache.GetKeywordCacheStatus();
            var minFlatCount = _configuration.GetValue("Readiness:MinFlatCount", 1000);
            var ready = status.IsReady(minFlatCount);

            var body = new
            {
                ready,
                flatCount = status.FlatCount,
                minFlatCount,
                lastSuccessfulRefreshAt = status.LastSuccessfulRefreshAt,
                isRefreshRunning = status.IsRefreshRunning,
                lastRefreshError = status.LastRefreshError is null ? null : status.LastRefreshError.Split('\n')[0]
            };
            return ready ? Ok(body) : StatusCode(StatusCodes.Status503ServiceUnavailable, body);
        }

        [HttpGet("db")]
        public async Task<IActionResult> GetDatabaseHealth(CancellationToken ct = default)
        {
            var status = _tunnel.Status;

            string target = "(unparsed)";
            string? queryError = null;
            var reachable = false;

            var connStr = _configuration.GetConnectionString("DefaultConnection")
                          ?? _configuration["ConnectionStrings:Default"];
            if (string.IsNullOrWhiteSpace(connStr))
            {
                queryError = "Connection string is not configured.";
            }
            else
            {
                try
                {
                    var csb = new NpgsqlConnectionStringBuilder(connStr);
                    target = $"{csb.Host}:{csb.Port}/{csb.Database}";
                }
                catch (Exception ex)
                {
                    target = $"(invalid connection string: {ex.GetType().Name})";
                }

                try
                {
                    await using var conn = new NpgsqlConnection(connStr);
                    await conn.OpenAsync(ct);
                    await using var cmd = new NpgsqlCommand("SELECT 1;", conn);
                    await cmd.ExecuteScalarAsync(ct);
                    reachable = true;
                }
                catch (Exception ex)
                {
                    queryError = $"{ex.GetType().Name}: {ex.Message}";
                }
            }

            var body = new
            {
                databaseReachable = reachable,
                databaseTarget = target,
                databaseError = queryError,
                tunnel = new
                {
                    connected = status.Connected,
                    usingExistingForward = status.UsingExistingForward,
                    endpoint = status.Endpoint,
                    attempts = status.Attempts,
                    connectedSince = status.ConnectedSince,
                    lastErrorAt = status.LastErrorAt,
                    lastError = status.LastError
                }
            };

            return reachable ? Ok(body) : StatusCode(StatusCodes.Status503ServiceUnavailable, body);
        }
    }
}
