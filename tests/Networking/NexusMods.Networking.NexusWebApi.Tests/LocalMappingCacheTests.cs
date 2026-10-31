using FluentAssertions;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Networking.NexusWebApi.V1Interop;
using NexusMods.Sdk.NexusModsApi;

namespace NexusMods.Networking.NexusWebApi.Tests;

public class LocalMappingCacheTests
{
    [Fact]
    public void Test_Parse()
    {
        LocalMappingCache.TryParseJsonFile(NullLogger.Instance, out _, out _).Should().BeTrue();
    }

    [Fact]
    public void MultipleIdsForOneDomainRemainUsable()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""
            [{"id":180,"domain_name":"neverwinter"},{"id":8703,"domain_name":"neverwinter"}]
            """));

        LocalMappingCache.TryParseJson(stream, NullLogger.Instance, out var ids, out var domains).Should().BeTrue();
        ids![NexusModsGameId.From(180)].Should().Be(GameDomain.From("neverwinter"));
        ids[NexusModsGameId.From(8703)].Should().Be(GameDomain.From("neverwinter"));
        domains![GameDomain.From("neverwinter")].Should().Be(NexusModsGameId.From(180));
    }

    [Fact]
    public void DuplicateIdsUseTheFirstEntry()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""
            [{"id":1,"domain_name":"first"},{"id":1,"domain_name":"second"},{"id":1,"domain_name":"first"}]
            """));

        LocalMappingCache.TryParseJson(stream, NullLogger.Instance, out var ids, out var domains).Should().BeTrue();
        ids!.Should().ContainSingle();
        ids[NexusModsGameId.From(1)].Should().Be(GameDomain.From("first"));
        domains!.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("not json")]
    public void UnusableCatalogAllowsTheFallbackCache(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        LocalMappingCache.TryParseJson(stream, NullLogger.Instance, out var ids, out var domains).Should().BeFalse();
        ids.Should().BeNull();
        domains.Should().BeNull();
    }
}
