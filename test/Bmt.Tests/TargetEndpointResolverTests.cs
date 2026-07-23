using System.Net;
using Bmt.LoadGen;
using Xunit;

namespace Bmt.Tests;

/// <summary>
/// Validates §4 target-endpoint resolution + matching — the basis for filtering TCP sockets to the
/// database destinations only (host-wide totals cannot prove DB-specific behavior). Uses IP-literal and
/// multi-mongos connection strings so no external DNS is required.
/// </summary>
public sealed class TargetEndpointResolverTests
{
    [Fact]
    public async Task Resolves_IpLiteral_HostPort_And_Matches_Only_ThatEndpoint()
    {
        var resolver = new TargetEndpointResolver("mongodb://10.0.0.5:10260/?ssl=true");
        await resolver.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, resolver.EndpointCount);
        Assert.Contains("10.0.0.5:10260", resolver.EndpointDescriptions);

        Assert.True(resolver.Matches(new IPEndPoint(IPAddress.Parse("10.0.0.5"), 10260)));
        Assert.False(resolver.Matches(new IPEndPoint(IPAddress.Parse("10.0.0.5"), 27017))); // wrong port
        Assert.False(resolver.Matches(new IPEndPoint(IPAddress.Parse("10.0.0.6"), 10260))); // wrong host
        Assert.False(resolver.Matches(null));
    }

    [Fact]
    public async Task Resolves_All_Mongos_Endpoints_In_A_Sharded_ConnectionString()
    {
        // Two mongos routers (sharded): both destinations must be tracked (§4).
        var resolver = new TargetEndpointResolver("mongodb://10.0.0.1:27017,10.0.0.2:27017/?replicaSet=rs0");
        await resolver.RefreshAsync(CancellationToken.None);

        Assert.Equal(2, resolver.EndpointCount);
        Assert.True(resolver.Matches(new IPEndPoint(IPAddress.Parse("10.0.0.1"), 27017)));
        Assert.True(resolver.Matches(new IPEndPoint(IPAddress.Parse("10.0.0.2"), 27017)));
    }

    [Fact]
    public async Task Matches_IPv4Mapped_IPv6_RemoteEndpoint()
    {
        var resolver = new TargetEndpointResolver("mongodb://10.0.0.5:10260/");
        await resolver.RefreshAsync(CancellationToken.None);

        // A TCP row reported as an IPv4-mapped IPv6 address (::ffff:10.0.0.5) must still match the
        // resolved IPv4 endpoint, otherwise target sockets would be silently missed.
        var mapped = IPAddress.Parse("10.0.0.5").MapToIPv6();
        Assert.True(mapped.IsIPv4MappedToIPv6);
        Assert.True(resolver.Matches(new IPEndPoint(mapped, 10260)));
    }
}
