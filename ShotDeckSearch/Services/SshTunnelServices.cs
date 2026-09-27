using Npgsql;
using Renci.SshNet;
using ConnectionInfo = Renci.SshNet.ConnectionInfo;

public sealed record SshTunnelStatus(
    bool Connected,
    bool UsingExistingForward,
    string Endpoint,
    int Attempts,
    DateTimeOffset? ConnectedSince,
    DateTimeOffset? LastErrorAt,
    string? LastError);

/// <summary>
/// Keeps an SSH local port-forward to Postgres alive for the lifetime of the
/// process. Settings come from <c>SshTunnel:*</c> configuration and fall back
/// to the historical constants so no App Settings change is required.
/// <para>
/// Deployment slots share the worker's loopback. If something already answers
/// on the local forward (the other slot during a swap, or an overlapped
/// recycle), it is reused rather than fought over; when it stops answering the
/// service takes the port over and opens its own forward.
/// </para>
/// </summary>
public class SshTunnelService : IHostedService, IDisposable
{
    public const string DefaultLocalBindHost = "127.0.0.1";
    public const int DefaultLocalBindPort = 5433;

    private readonly IConfiguration _configuration;
    private readonly string? _sshPrivateKey;

    private SshClient? _sshClient;
    private ForwardedPortLocal? _portForward;

    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;

    private volatile bool _connected;
    private volatile bool _usingExistingForward;
    private int _attempts;
    private DateTimeOffset? _connectedSince;
    private DateTimeOffset? _lastErrorAt;
    private string? _lastError;
    private string _endpoint = $"{DefaultLocalBindHost}:{DefaultLocalBindPort}";

    public Task TunnelReady => _tunnelReady.Task;
    private readonly TaskCompletionSource _tunnelReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SshTunnelStatus Status => new(
        _connected, _usingExistingForward, _endpoint, _attempts, _connectedSince, _lastErrorAt, _lastError);

    public SshTunnelService(IConfiguration configuration)
    {
        _configuration = configuration;
        _sshPrivateKey = configuration["SSH_PRIVATE_KEY"]; // base64 PEM in App Settings
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        try { if (_loopTask is not null) await _loopTask; } catch { /* ignore */ }
        TearDown();
    }

    public void Dispose()
    {
        _cts.Cancel();
        TearDown();
        _cts.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var sshHost = _configuration["SshTunnel:SshHost"] ?? "35.89.51.60";
        var sshPort = _configuration.GetValue<int?>("SshTunnel:SshPort") ?? 22;
        var sshUser = _configuration["SshTunnel:SshUser"] ?? "ec2-user";
        var sshKeyPath = _configuration["SshTunnel:SshKeyPath"] ?? @"D:\shotdeck\pem\db\shotdeck.pem";
        var remoteDbHost = _configuration["SshTunnel:RemoteDbHost"] ?? "shotdeck-postgres-db.cnvhvhrwu7ln.us-west-2.rds.amazonaws.com";
        var remoteDbPort = _configuration.GetValue<uint?>("SshTunnel:RemoteDbPort") ?? 5432;
        var localBindHost = _configuration["SshTunnel:LocalBindHost"] ?? DefaultLocalBindHost;
        var localBindPort = _configuration.GetValue<uint?>("SshTunnel:LocalBindPort") ?? DefaultLocalBindPort;
        var backoffMs = _configuration.GetValue<int?>("SshTunnel:ReconnectDelayMs") ?? 2000;
        const int maxBackoffMs = 60_000;

        _endpoint = $"{localBindHost}:{localBindPort}";
        var delayMs = backoffMs;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (await LocalForwardReachesDatabaseAsync(ct))
                {
                    _usingExistingForward = true;
                    _connected = true;
                    _connectedSince ??= DateTimeOffset.UtcNow;
                    delayMs = backoffMs;
                    Console.WriteLine($"ℹ️ Using a forward already listening on {_endpoint} (another process on this worker owns it)");
                    if (!_tunnelReady.Task.IsCompleted)
                        _tunnelReady.SetResult();

                    while (!ct.IsCancellationRequested && await LocalForwardReachesDatabaseAsync(ct))
                        await Task.Delay(5000, ct);

                    _usingExistingForward = false;
                    _connected = false;
                    _connectedSince = null;
                    if (!ct.IsCancellationRequested)
                        Console.WriteLine($"⚠️ The existing forward on {_endpoint} stopped working; taking it over");

                    ClearPools();
                    continue;
                }

                Interlocked.Increment(ref _attempts);

                PrivateKeyFile privateKey;
                if (!string.IsNullOrWhiteSpace(_sshPrivateKey))
                {
                    byte[] pemBytes = Convert.FromBase64String(_sshPrivateKey);
                    using var keyStream = new MemoryStream(pemBytes);
                    privateKey = new PrivateKeyFile(keyStream);
                }
                else
                {
                    privateKey = new PrivateKeyFile(sshKeyPath);
                }

                var connectionInfo = new ConnectionInfo(
                    sshHost,
                    sshPort,
                    sshUser,
                    new PrivateKeyAuthenticationMethod(sshUser, privateKey))
                {
                    Timeout = TimeSpan.FromSeconds(20)
                };

                _sshClient = new SshClient(connectionInfo)
                {
                    // keep-alives so NAT/SNAT doesn't drop the idle session
                    KeepAliveInterval = TimeSpan.FromSeconds(30)
                };

                _sshClient.ErrorOccurred += (_, e) =>
                    Console.WriteLine("SSH error: " + e.Exception?.Message);

                _sshClient.Connect();

                _portForward = new ForwardedPortLocal(localBindHost, localBindPort, remoteDbHost, remoteDbPort);
                _portForward.Exception += (_, e) =>
                    Console.WriteLine("Port forward exception: " + e.Exception?.Message);

                _sshClient.AddForwardedPort(_portForward);
                _portForward.Start();

                // Pooled sockets may point at a previous (dead) forward.
                ClearPools();

                _connected = true;
                _connectedSince = DateTimeOffset.UtcNow;
                delayMs = backoffMs;
                Console.WriteLine($"✅ SSH tunnel established on {_endpoint} → {remoteDbHost}:{remoteDbPort}");
                if (!_tunnelReady.Task.IsCompleted)
                    _tunnelReady.SetResult();

                while (!ct.IsCancellationRequested && _sshClient.IsConnected && _portForward.IsStarted)
                    await Task.Delay(1000, ct);

                _connected = false;
                _connectedSince = null;
                Console.WriteLine("⚠️ Tunnel no longer active; will reconnect…");
                ClearPools();
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch (Exception ex)
            {
                _connected = false;
                _connectedSince = null;
                _lastErrorAt = DateTimeOffset.UtcNow;
                _lastError = $"{ex.GetType().Name}: {ex.Message}";
                Console.WriteLine($"❌ Tunnel loop error (attempt {_attempts}, retry in {delayMs}ms): " + ex.Message);
            }
            finally
            {
                TearDown();
            }

            if (!ct.IsCancellationRequested)
            {
                await Task.Delay(delayMs, ct);
                delayMs = Math.Min(delayMs * 2, maxBackoffMs);
            }
        }
    }

    private async Task<bool> LocalForwardReachesDatabaseAsync(CancellationToken ct)
    {
        var connStr = _configuration.GetConnectionString("DefaultConnection")
                      ?? _configuration["ConnectionStrings:Default"];
        if (string.IsNullOrWhiteSpace(connStr)) return false;

        try
        {
            var csb = new NpgsqlConnectionStringBuilder(connStr) { Timeout = 5, Pooling = false };
            await using var conn = new NpgsqlConnection(csb.ConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("SELECT 1;", conn);
            await cmd.ExecuteScalarAsync(ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static void ClearPools()
    {
        try
        {
            NpgsqlConnection.ClearAllPools();
            Console.WriteLine("🧹 Cleared Npgsql connection pools.");
        }
        catch (Exception poolEx)
        {
            Console.WriteLine("⚠️ Failed to clear Npgsql pools: " + poolEx.Message);
        }
    }

    private void TearDown()
    {
        try { _portForward?.Stop(); } catch { }
        try { _sshClient?.Disconnect(); } catch { }
        try { _portForward?.Dispose(); } catch { }
        try { _sshClient?.Dispose(); } catch { }
        _portForward = null;
        _sshClient = null;
    }
}
