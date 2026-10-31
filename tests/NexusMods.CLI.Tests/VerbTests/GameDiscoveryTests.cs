using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.ProxyConsole;
using NexusMods.SingleProcess;
using NSubstitute;

namespace NexusMods.CLI.Tests.VerbTests;

public class GameDiscoveryTests
{
    [Fact]
    public async Task NoInstalledGamesIsASuccessfulDiscoveryCheck()
    {
        var locator = Substitute.For<IGameLocator>();
        locator.Locate().Returns(Array.Empty<GameLocatorResult>());
        using var provider = CreateServices().AddSingleton(locator).BuildServiceProvider();
        var renderer = new LoggingRenderer();

        var result = await provider.GetRequiredService<CommandLineConfigurator>()
            .RunAsync([GameDiscoveryVerbs.CommandName], renderer, CancellationToken.None);

        result.Should().Be(0);
        renderer.Logs.OfType<Text>().Should().Contain(text =>
            text.Template.Contains($"{GameDiscoveryVerbs.SuccessMarker} Locators: 1 Installations: 0"));
        locator.Received(1).Locate();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LocatorFailuresReturnANonzeroExitCodeWithoutASuccessMarker(bool failsDuringConstruction)
    {
        var services = CreateServices();
        if (failsDuringConstruction)
        {
            services.AddSingleton<IGameLocator>(_ => throw new InvalidOperationException("Missing locator dependency"));
        }
        else
        {
            var locator = Substitute.For<IGameLocator>();
            locator.Locate().Returns(_ => throw new IOException("Unable to enumerate installations"));
            services.AddSingleton(locator);
        }

        using var provider = services.BuildServiceProvider();
        var renderer = new LoggingRenderer();
        var result = await provider.GetRequiredService<CommandLineConfigurator>()
            .RunAsync([GameDiscoveryVerbs.CommandName], renderer, CancellationToken.None);

        result.Should().NotBe(0);
        renderer.Logs.OfType<Text>().Should().NotContain(text => text.Template.Contains(GameDiscoveryVerbs.SuccessMarker));
    }

    private static IServiceCollection CreateServices()
        => new ServiceCollection().AddLogging().AddGameDiscoveryVerbs().AddSingleton<CommandLineConfigurator>();
}
