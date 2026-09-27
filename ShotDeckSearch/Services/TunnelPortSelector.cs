using System.Net;
using System.Net.Sockets;
using Npgsql;

/// <summary>
/// Picks the loopback port the SSH tunnel forwards from.
/// <para>
/// Deployment slots share the worker's loopback, so two slots forwarding from
/// the same local port contend for it: whichever starts second cannot bind
/// ("Only one usage of each socket address is normally permitted") and has no
/// route to Postgres. Keep the configured port when it is free and fall back
/// to any free port otherwise.
/// </para>
/// <para>
/// A loopback connection string is then pointed at whichever port the tunnel
/// forwards from, so the two cannot disagree.
/// </para>
/// </summary>
public static class TunnelPortSelector
{
    /// <summary>
    /// Resolves the local bind port and writes it, plus the matching
    /// connection string, back into configuration. Call before the host runs:
    /// consumers read the connection string in their constructors.
    /// </summary>
    public static void Apply(IConfiguration configuration)
    {
        var bindHost = configuration["SshTunnel:LocalBindHost"] ?? SshTunnelService.DefaultLocalBindHost;
        var configuredPort = configuration.GetValue<int?>("SshTunnel:LocalBindPort") ?? SshTunnelService.DefaultLocalBindPort;

        var connKey = configuration.GetConnectionString("DefaultConnection") is not null
            ? "ConnectionStrings:DefaultConnection"
            : "ConnectionStrings:Default";
        var connStr = configuration[connKey];

        if (configuredPort <= 0 || string.IsNullOrWhiteSpace(connStr))
            return;

        if (!IPAddress.TryParse(bindHost, out var bindAddress))
            return;

        var port = configuredPort;

        if (!IsFree(bindAddress, configuredPort))
        {
            var free = FindFreePort(bindAddress);
            if (free is null)
                return;

            port = free.Value;
            Console.WriteLine($"[TunnelPortSelector] {bindHost}:{configuredPort} is taken (another slot?); tunnel will bind {bindHost}:{port}");
        }

        configuration["SshTunnel:LocalBindPort"] = port.ToString();

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connStr);
        }
        catch (ArgumentException)
        {
            return;
        }

        if (!IsLoopback(builder.Host) || builder.Port == port)
            return;

        builder.Port = port;
        configuration[connKey] = builder.ConnectionString;
    }

    private static bool IsLoopback(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }

    private static bool IsFree(IPAddress address, int port)
    {
        try
        {
            using var listener = new TcpListener(address, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static int? FindFreePort(IPAddress address)
    {
        try
        {
            using var listener = new TcpListener(address, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
        catch (SocketException)
        {
            return null;
        }
    }
}
