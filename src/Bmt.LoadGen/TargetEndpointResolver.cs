using System.Net;
using DnsClient;
using MongoDB.Driver;
using MongoDB.Driver.Core.Configuration;

namespace Bmt.LoadGen;

/// <summary>
/// Resolves the set of database destination endpoints (IP + port) for a target so the TCP sampler can
/// filter to TARGET-specific sockets rather than counting every unrelated connection on the VM
/// (test_instruction.md §4). Host-wide socket totals cannot prove database-specific connection behavior;
/// this resolver produces the authoritative destination IP/port set to correlate against.
///
/// <list type="bullet">
///   <item>Parses the connection string via <see cref="MongoClientSettings"/> to get the seed host:port list.</item>
///   <item>For <c>mongodb+srv</c> schemes, resolves the SRV records (<c>_mongodb._tcp.&lt;host&gt;</c>) to the
///     real host:port targets.</item>
///   <item>Resolves every target hostname to its A/AAAA addresses (IP literals pass through).</item>
///   <item>Refreshes periodically so a managed service that re-homes behind a changed IP is still tracked.</item>
/// </list>
/// For sharded/mongos configurations the connection string lists all routers, so all of their endpoints
/// are included. The resolved set is a snapshot swapped atomically, so sampling never blocks on DNS.
/// </summary>
public sealed class TargetEndpointResolver
{
    private readonly string _connectionString;
    private readonly LookupClient _dns = new();

    private volatile HashSet<IPEndPoint> _endpoints = new();
    private volatile string[] _endpointDescriptions = Array.Empty<string>();
    private volatile string _resolvedAtUtc = string.Empty;

    public TargetEndpointResolver(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    /// <summary>Endpoint descriptions ("ip:port") from the most recent resolution (for logging/reporting).</summary>
    public IReadOnlyList<string> EndpointDescriptions => _endpointDescriptions;

    /// <summary>ISO-8601 UTC instant of the most recent successful resolution.</summary>
    public string ResolvedAtUtc => _resolvedAtUtc;

    /// <summary>Number of resolved destination IP/port pairs.</summary>
    public int EndpointCount => _endpoints.Count;

    /// <summary>True when <paramref name="remote"/> is one of the resolved target destinations.</summary>
    public bool Matches(IPEndPoint? remote) => remote is not null && _endpoints.Contains(Normalize(remote));

    /// <summary>Re-resolve the target endpoint set and atomically swap the snapshot. Never throws.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var seeds = ParseSeeds(out var isSrv);
            var targets = isSrv
                ? await ResolveSrvAsync(seeds, ct).ConfigureAwait(false)
                : seeds;

            var endpoints = new HashSet<IPEndPoint>();
            var allResolved = true;
            foreach (var (host, port) in targets)
            {
                if (IPAddress.TryParse(host, out var literal))
                {
                    endpoints.Add(Normalize(new IPEndPoint(literal, port)));
                    continue;
                }

                try
                {
                    var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
                    if (addresses.Length == 0)
                    {
                        allResolved = false;
                    }

                    foreach (var ip in addresses)
                    {
                        endpoints.Add(Normalize(new IPEndPoint(ip, port)));
                    }
                }
                catch (Exception ex)
                {
                    allResolved = false;
                    ConsoleLog.Warn($"endpoint resolve: DNS for '{host}:{port}' failed: {ex.Message}");
                }
            }

            // A partial failure must NOT drop a previously-known endpoint (§4: include ALL endpoints).
            // On an incomplete refresh, union with the prior snapshot so transiently-failed targets persist.
            if (!allResolved)
            {
                foreach (var prev in _endpoints)
                {
                    endpoints.Add(prev);
                }
            }

            if (endpoints.Count > 0)
            {
                _endpoints = endpoints;
                _endpointDescriptions = endpoints
                    .Select(e => $"{e.Address}:{e.Port}")
                    .OrderBy(s => s, StringComparer.Ordinal)
                    .ToArray();
                _resolvedAtUtc = DateTime.UtcNow.ToString("O");
            }
        }
        catch (Exception ex)
        {
            ConsoleLog.Warn($"endpoint resolve failed (keeping previous set of {_endpoints.Count}): {ex.Message}");
        }
    }

    /// <summary>Normalize IPv4-mapped-IPv6 addresses to IPv4 so a resolved IPv4 matches an <c>::ffff:x</c> socket row.</summary>
    private static IPEndPoint Normalize(IPEndPoint ep) =>
        ep.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(ep.Address.MapToIPv4(), ep.Port) : ep;

    private List<(string Host, int Port)> ParseSeeds(out bool isSrv)
    {
        var settings = MongoClientSettings.FromConnectionString(_connectionString);
        isSrv = settings.Scheme == ConnectionStringScheme.MongoDBPlusSrv;
        var seeds = new List<(string, int)>();
        foreach (var s in settings.Servers)
        {
            // For SRV the "port" on the seed host is the DNS port placeholder; the SRV record carries the
            // real port, so default to 27017 here and let ResolveSrvAsync override it.
            seeds.Add((s.Host, s.Port));
        }

        return seeds;
    }

    private async Task<List<(string Host, int Port)>> ResolveSrvAsync(
        List<(string Host, int Port)> seeds, CancellationToken ct)
    {
        var targets = new List<(string, int)>();
        foreach (var (host, _) in seeds)
        {
            try
            {
                var query = $"_mongodb._tcp.{host}";
                var result = await _dns.QueryAsync(query, QueryType.SRV, cancellationToken: ct).ConfigureAwait(false);
                foreach (var srv in result.Answers.SrvRecords())
                {
                    targets.Add((srv.Target.Value.TrimEnd('.'), srv.Port));
                }
            }
            catch (Exception ex)
            {
                ConsoleLog.Warn($"endpoint resolve: SRV lookup for '{host}' failed: {ex.Message}");
            }
        }

        // If SRV resolution produced nothing (e.g. offline), fall back to the seed hosts with the MongoDB
        // default port (the SRV seed's own port is the DNS port 53, which is NOT the database port).
        return targets.Count > 0
            ? targets
            : seeds.Select(s => (s.Host, 27017)).ToList();
    }
}
