using System.Runtime.InteropServices;
using FluentAssertions;
using GameFinder.RegistryUtils;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Backend.Games.Locators;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Backend.Tests.Games;

public class GameLocatorRegistrationTests
{
    [Test]
    public void WindowsLocatorsResolveWithTheDefaultRegistry()
    {
        var services = CreateServices(OSInformation.FakeWindows);
        services.AddGameLocators(os: OSInformation.FakeWindows);
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<IRegistry>();
        if (OperatingSystem.IsWindows()) registry.Should().BeSameAs(WindowsRegistry.Shared);
        else registry.Should().BeOfType<InMemoryRegistry>();

        // Resolving IGameRegistry alone misses the original failure: its locator list is lazy.
        provider.GetServices<IGameLocator>().Select(locator => locator.GetType())
            .Should().BeEquivalentTo(new[] { typeof(SteamLocator), typeof(GOGLocator), typeof(EGSLocator) });
    }

    [Test]
    public void WindowsDiscoveryPreservesAndUsesACallerSuppliedRegistry()
    {
        var registry = new InMemoryRegistry();
        var services = CreateServices(OSInformation.FakeWindows);
        services.AddSingleton<IRegistry>(registry);
        services.AddGameLocators(os: OSInformation.FakeWindows);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IRegistry>().Should().BeSameAs(registry);
        provider.GetServices<IRegistry>().Should().ContainSingle();
        var locators = provider.GetServices<IGameLocator>().ToArray();
        locators.Should().HaveCount(3);
        foreach (var locator in locators)
            locator.Locate().Should().BeEmpty("the isolated filesystem and registry contain no installed games");
    }

    [Test]
    [Arguments("LINUX")]
    [Arguments("OSX")]
    public void NonWindowsSteamDiscoveryDoesNotRequireARegistry(string platform)
    {
        var os = new OSInformation(OSPlatform.Create(platform));
        var services = CreateServices(os);
        services.AddGameLocators(os: os);
        using var provider = services.BuildServiceProvider();

        provider.GetService<IRegistry>().Should().BeNull();
        var steam = provider.GetServices<IGameLocator>().OfType<SteamLocator>().Single();
        steam.Locate().Should().BeEmpty();
    }

    private static IServiceCollection CreateServices(IOSInformation os)
    {
        var fileSystem = new InMemoryFileSystem(os);
        var root = fileSystem.FromUnsanitizedFullPath(os.IsWindows ? "C:/" : "/");
        // Known folders normally come from the host OS. Supply valid paths for the
        // simulated OS as well, including Windows program folders on a Linux runner.
        var isolatedFileSystem = fileSystem.CreateOverlayFileSystem(pathMappings: [], knownPathMappings: new()
        {
            [KnownPath.ProgramFilesDirectory] = root.Combine("Program Files"),
            [KnownPath.ProgramFilesX86Directory] = root.Combine("Program Files (x86)"),
            [KnownPath.CommonApplicationDataDirectory] = root.Combine("ProgramData"),
            [KnownPath.HomeDirectory] = root.Combine("Users/test"),
            [KnownPath.LocalApplicationDataDirectory] = root.Combine("Users/test/AppData/Local"),
        });
        return new ServiceCollection()
            .AddLogging()
            .AddSingleton<IFileSystem>(isolatedFileSystem);
    }
}
