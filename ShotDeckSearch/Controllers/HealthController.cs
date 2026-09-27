using Microsoft.AspNetCore.Mvc;
using Npgsql;

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

        public HealthController(SshTunnelService tunnel, IConfiguration configuration)
        {
            _tunnel = tunnel;
            _configuration = configuration;
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
