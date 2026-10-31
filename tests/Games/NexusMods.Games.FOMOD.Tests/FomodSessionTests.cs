using System.IO.Compression;
using System.Text;
using FluentAssertions;
using FomodInstaller.Interface;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.GuidedInstallers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.FOMOD.CoreDelegates;
using NexusMods.Games.Generic;
using NexusMods.Games.RedEngine;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;
using NexusMods.StandardGameLocators.TestHelpers;
using Xunit.Abstractions;

namespace NexusMods.Games.FOMOD.Tests;

/// <summary>Executes the real FOMOD engine and production delegates using offline archives.</summary>
public class FomodSessionTests(ITestOutputHelper output)
    : ALibraryArchiveInstallerTests<FomodSessionTests, Cyberpunk2077Game>(output)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        ConfigOptions.RegisterNullGuidedInstaller = false;
        return base.AddServices(services)
            .AddRedEngineGames()
            .AddUniversalGameLocator<Cyberpunk2077Game>(new Version("1.6.1"))
            .AddTransient<ICoreDelegates, InstallerDelegates>()
            .AddSingleton<ChoiceController>()
            .AddSingleton<IFomodDependencyResolver>(provider => new ResolverWithTestVersion(provider.GetRequiredService<ChoiceController>()))
            .AddTransient<IGuidedInstaller, DefaultChoices>();
    }

    [Fact]
    public async Task ARecordedPresetDoesNotLeakIntoTheNextInteractiveInstallation()
    {
        var archive = await CreateArchive();
        var loadout = await CreateLoadout();
        var installer = FomodXmlInstaller.Create(ServiceProvider, new GamePath(LocationId.Game, "Data"));

        var preset = await InstallSession(installer, loadout, archive, Choices("Alpha"));
        var interactive = await InstallSession(installer, loadout, archive, null);

        Paths(preset).Should().BeEquivalentTo("Data/base.txt", "Data/alpha.txt");
        Paths(interactive).Should().BeEquivalentTo("Data/base.txt", "Data/beta.txt");
        ServiceProvider.GetRequiredService<ChoiceController>().Requests.Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentInstallationsKeepTheirOwnPresets()
    {
        var archive = await CreateArchive();
        var loadout = await CreateLoadout();
        var installer = FomodXmlInstaller.Create(ServiceProvider, new GamePath(LocationId.Game, "Data"));
        var installs = await Task.WhenAll(
            InstallSession(installer, loadout, archive, Choices("Alpha")),
            InstallSession(installer, loadout, archive, Choices("Beta")));

        Paths(installs[0]).Should().BeEquivalentTo("Data/base.txt", "Data/alpha.txt");
        Paths(installs[1]).Should().BeEquivalentTo("Data/base.txt", "Data/beta.txt");
    }

    [Fact]
    public async Task AnEmptyRecordedSelectionClearsRecommendedDefaults()
    {
        var archive = await CreateArchive();
        var loadout = await CreateLoadout();
        var installer = FomodXmlInstaller.Create(ServiceProvider, new GamePath(LocationId.Game, "Data"));
        var installed = await InstallSession(installer, loadout, archive, Choices());
        Paths(installed).Should().Equal("Data/base.txt");
    }

    [Fact]
    public async Task HiddenRecordedStepsDoNotShiftTheVisibleStepsChoices()
    {
        var archive = await CreateArchive();
        var loadout = await CreateLoadout();
        var installer = FomodXmlInstaller.Create(ServiceProvider, new GamePath(LocationId.Game, "Data"));
        var hiddenStep = new FomodOption { name = "Conditionally hidden", groups = Choices("Beta")[0].groups };
        var installed = await InstallSession(installer, loadout, archive, [hiddenStep, Choices("Alpha")[0]]);
        Paths(installed).Should().BeEquivalentTo("Data/base.txt", "Data/alpha.txt");
    }

    [Theory]
    [InlineData("SelectExactlyOne", "Optional", false)]
    [InlineData("SelectAtMostOne", "NotUsable", true)]
    public async Task PresetsRejectInvalidGroupCountsAndUnavailableOptions(string groupType, string alphaType, bool chooseAlpha)
    {
        var archive = await CreateArchive(groupType: groupType, alphaType: alphaType);
        var loadout = await CreateLoadout();
        var installer = FomodXmlInstaller.Create(ServiceProvider, new GamePath(LocationId.Game, "Data"));
        var action = () => InstallSession(installer, loadout, archive, chooseAlpha ? Choices("Alpha") : Choices())
            .WaitAsync(TimeSpan.FromSeconds(10));
        await action.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData("Missing choice", "Options")]
    [InlineData("Alpha", "Renamed step")]
    public async Task InvalidPresetsFailWithoutHangingOrCommittingFiles(string choice, string step)
    {
        var archive = await CreateArchive();
        var loadout = await CreateLoadout();
        var installer = FomodXmlInstaller.Create(ServiceProvider, new GamePath(LocationId.Game, "Data"));
        var options = Choices(choice);
        options[0] = new FomodOption { name = step, groups = options[0].groups };

        var action = () => InstallSession(installer, loadout, archive, options).WaitAsync(TimeSpan.FromSeconds(10));
        await action.Should().ThrowAsync<InvalidDataException>().WithMessage("*collection*");
        LoadoutItem.FindByLoadout(Connection.Db, loadout).Any(item => item.Name == "Offline FOMOD").Should().BeFalse();
    }

    [Fact]
    public async Task CancellationClosesAWaitingInstallerWithoutCommittingTheGroup()
    {
        var archive = await CreateArchive();
        var loadout = await CreateLoadout();
        var controller = ServiceProvider.GetRequiredService<ChoiceController>();
        controller.WaitForCancellation = true;
        var installer = FomodXmlInstaller.Create(ServiceProvider, new GamePath(LocationId.Game, "Data"));
        using var cancellation = new CancellationTokenSource();
        var installation = InstallSession(installer, loadout, archive, null, cancellation.Token);
        await controller.ChoiceRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        var action = () => installation.WaitAsync(TimeSpan.FromSeconds(10));
        await action.Should().ThrowAsync<OperationCanceledException>();
        controller.Cleanups.Should().Be(1);
        LoadoutItem.FindByLoadout(Connection.Db, loadout).Any(item => item.Name == "Offline FOMOD").Should().BeFalse();
    }

    [Theory]
    [InlineData(false, "Active", true)]
    [InlineData(true, "Active", false)]
    [InlineData(true, "Inactive", true)]
    [InlineData(false, "Missing", false)]
    public async Task FileDependenciesUseTheTargetProfileAndDisabledAncestors(bool disabled, string state, bool succeeds)
    {
        var archive = await CreateArchive(state);
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            var group = new LoadoutItemGroup.New(tx, out var groupId)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, groupId) { Name = "Dependency mod", LoadoutId = loadout, IsDisabled = disabled },
            };
            _ = new LoadoutFile.New(tx, out var fileId)
            {
                Hash = "plugin"u8.xxHash3(), Size = Size.FromLong(6),
                LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, fileId)
                {
                    TargetPath = (loadout.Id, LocationId.Game, "Data/Dependency.esp"),
                    LoadoutItem = new LoadoutItem.New(tx, fileId) { Name = "Dependency.esp", LoadoutId = loadout, ParentId = group },
                },
            };
            await tx.Commit();
        }

        var installer = FomodXmlInstaller.Create(ServiceProvider, new GamePath(LocationId.Game, "Data"));
        var action = () => InstallSession(installer, loadout, archive, Choices());
        if (succeeds) Paths(await action()).Should().Equal("Data/base.txt");
        else await action.Should().ThrowAsync<InvalidDataException>();

        // The same installation's other profile has no dependency, regardless of disk state.
        if (state == "Active")
        {
            var otherProfile = await CreateLoadout();
            var otherAction = () => InstallSession(installer, otherProfile, archive, Choices());
            await otherAction.Should().ThrowAsync<InvalidDataException>();
        }
    }

    [Theory]
    [InlineData("1.6.1170", true)]
    [InlineData("1.6.640", false)]
    [InlineData(null, false)]
    public async Task GameVersionRequirementsUseTheReportedExecutableVersionAndRejectUnknownVersions(string? version, bool succeeds)
    {
        var controller = ServiceProvider.GetRequiredService<ChoiceController>();
        controller.OverrideVersion = true;
        controller.GameVersion = version is null ? null : Version.Parse(version);
        var archive = await CreateArchive(minimumGameVersion: "1.6.1130");
        var loadout = await CreateLoadout();
        var installer = FomodXmlInstaller.Create(ServiceProvider, new GamePath(LocationId.Game, "Data"));
        var action = () => InstallSession(installer, loadout, archive, Choices());
        if (succeeds) Paths(await action()).Should().Equal("Data/base.txt");
        else if (version is null)
            await action.Should().ThrowAsync<NotSupportedException>().WithMessage("*primary executable's version*");
        else await action.Should().ThrowAsync<InvalidDataException>();
    }

    private static string[] Paths(LoadoutItemGroup.ReadOnly group) => group.Children
        .OfTypeLoadoutItemWithTargetPath().Select(file => file.TargetPath.Item3.ToString()).Order().ToArray();

    private static FomodOption[] Choices(params string[] choices) =>
    [
        new FomodOption { name = "Options", groups = [new FomodGroup
        {
            name = "Features", choices = choices.Select(name => new FomodChoice { name = name }).ToArray(),
        }] },
    ];

    private async Task<LoadoutItemGroup.ReadOnly> InstallSession(FomodXmlInstaller installer, Loadout.ReadOnly loadout,
        LibraryArchive.ReadOnly archive, FomodOption[]? choices, CancellationToken cancellationToken = default)
    {
        using var tx = Connection.BeginTransaction();
        var group = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id) { Name = "Offline FOMOD", LoadoutId = loadout },
        };
        var result = await installer.ExecuteAsync(archive, group, tx, Loadout.Load(Connection.Db, loadout.Id), choices, cancellationToken);
        result.IsSuccess.Should().BeTrue();
        return (await tx.Commit()).Remap(group);
    }

    private async Task<LibraryArchive.ReadOnly> CreateArchive(string? dependencyState = null, string groupType = "SelectAtMostOne", string alphaType = "Optional", string? minimumGameVersion = null)
    {
        var dependencies = dependencyState is null ? "" : $"<moduleDependencies><fileDependency file=\"Dependency.esp\" state=\"{dependencyState}\" /></moduleDependencies>";
        if (minimumGameVersion is not null)
            dependencies = $"<moduleDependencies><gameDependency version=\"{minimumGameVersion}\" /></moduleDependencies>";
        var config = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <config xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:noNamespaceSchemaLocation="http://qconsulting.ca/fo3/ModConfig5.0.xsd">
              <moduleName>Offline FOMOD</moduleName>
              {{dependencies}}
              <requiredInstallFiles><file source="base.txt" destination="base.txt" /></requiredInstallFiles>
              <installSteps order="Explicit"><installStep name="Options"><optionalFileGroups order="Explicit">
                <group name="Features" type="{{groupType}}"><plugins order="Explicit">
                  <plugin name="Alpha"><description>Alpha</description><files><file source="alpha.txt" destination="alpha.txt" /></files><typeDescriptor><type name="{{alphaType}}" /></typeDescriptor></plugin>
                  <plugin name="Beta"><description>Beta</description><files><file source="beta.txt" destination="beta.txt" /></files><typeDescriptor><type name="Recommended" /></typeDescriptor></plugin>
                </plugins></group>
              </optionalFileGroups></installStep></installSteps>
            </config>
            """;
        await using var file = TemporaryFileManager.CreateFile();
        await using (var stream = file.Path.Create())
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var (path, text) in new[] { ("fomod/ModuleConfig.xml", config), ("base.txt", "base"), ("alpha.txt", "alpha"), ("beta.txt", "beta") })
            {
                await using var entry = zip.CreateEntry(path).Open();
                await entry.WriteAsync(Encoding.UTF8.GetBytes(text));
            }
        }
        return await RegisterLocalArchive(file.Path);
    }

    public sealed class ChoiceController
    {
        public bool WaitForCancellation;
        public bool OverrideVersion;
        public Version? GameVersion;
        public int Requests;
        public int Cleanups;
        public TaskCompletionSource ChoiceRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ResolverWithTestVersion(ChoiceController controller) : IFomodDependencyResolver
    {
        public FomodDependencySnapshot Capture(Loadout.ReadOnly loadout, LoadoutItemGroupId? installingCollection = null)
        {
            var snapshot = new FomodDependencyResolver().Capture(loadout, installingCollection);
            return controller.OverrideVersion
                ? new FomodDependencySnapshot(snapshot.PresentFiles, snapshot.ActiveFiles, controller.GameVersion)
                : snapshot;
        }
    }

    public sealed class DefaultChoices(ChoiceController controller) : IGuidedInstaller
    {
        public void SetupInstaller(string name) { }
        public void CleanupInstaller() => Interlocked.Increment(ref controller.Cleanups);
        public void Dispose() { }
        public Task<UserChoice> RequestUserChoice(GuidedInstallationStep step, Percent progress, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref controller.Requests);
            controller.ChoiceRequested.TrySetResult();
            if (controller.WaitForCancellation)
                return new TaskCompletionSource<UserChoice>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            var options = step.Groups.SelectMany(group => group.Options
                .Where(option => option.Type is OptionType.PreSelected or OptionType.Required)
                .Select(option => new SelectedOption(group.Id, option.Id))).ToArray();
            return Task.FromResult(new UserChoice(new UserChoice.GoToNextStep(options)));
        }
    }
}
