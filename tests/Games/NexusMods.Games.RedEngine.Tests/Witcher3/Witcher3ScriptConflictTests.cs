using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.RedEngine.Witcher3;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.StandardGameLocators.TestHelpers;
using Xunit.Abstractions;

namespace NexusMods.Games.RedEngine.Tests.Witcher3;

public class Witcher3ScriptConflictTests(ITestOutputHelper output)
    : AIsolatedGameTest<Witcher3ScriptConflictTests, Witcher3Game>(output)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
        => base.AddServices(services).AddRedEngineGames().AddUniversalGameLocator<Witcher3Game>(new Version("4.04"));

    [Fact]
    public async Task PendingConflictsRespectDisabledCollectionsAndHistoricalSnapshots()
    {
        var loadout = await CreateLoadout();
        var collection = await CreateCollection(loadout, "Optional scripts");
        using (var tx = Connection.BeginTransaction())
        {
            var first = AddEmptyGroup(tx, loadout, "First scripts");
            var second = AddEmptyGroup(tx, loadout, "Second scripts", collection.AsLoadoutItemGroup().LoadoutItemGroupId);
            AddFile(tx, loadout, first, new GamePath(LocationId.Game, "Mods/modFirst/content/scripts/game/player.ws"), "first script", out _, out _);
            AddFile(tx, loadout, second, new GamePath(LocationId.Game, "Mods/modSecond/content/scripts/game/player.ws"), "second script", out _, out _);
            await tx.Commit();
        }

        var beforeDisable = loadout.Rebase();
        var emitter = new Witcher3ScriptConflictEmitter();
        var pending = await emitter.Diagnose(beforeDisable, CancellationToken.None).ToListAsync();
        pending.Should().ContainSingle();
        GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "Mods/modFirst/content/scripts/game/player.ws")).FileExists.Should().BeFalse();

        await DisableItem(collection.Id);
        (await emitter.Diagnose(loadout.Rebase(), CancellationToken.None).ToListAsync()).Should().BeEmpty();
        (await emitter.Diagnose(beforeDisable, CancellationToken.None).ToListAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task ExistingManualScriptsParticipateInOtherProfilesAndRespectExplicitDeletion()
    {
        var manualPath = new GamePath(LocationId.Game, "Mods/modManual/content/scripts/game/player.ws");
        var onDisk = GameInstallation.Locations.ToAbsolutePath(manualPath);
        onDisk.Parent.CreateDirectory();
        await onDisk.WriteAllTextAsync("Original manual script");
        await CreateLoadout();
        var target = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            var pendingMod = AddEmptyGroup(tx, target, "New script package");
            AddFile(tx, target, pendingMod, new GamePath(LocationId.Game, "Mods/modPending/content/scripts/game/player.ws"), "Different script");
            await tx.Commit();
        }

        var emitter = new Witcher3ScriptConflictEmitter();
        (await emitter.Diagnose(target.Rebase(), CancellationToken.None).ToListAsync()).Should().ContainSingle();

        using (var tx = Connection.BeginTransaction())
        {
            var removal = AddEmptyGroup(tx, target, "Remove manual script in this profile");
            _ = new DeletedFile.New(tx, out var id)
            {
                Reason = "Explicit profile removal",
                LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, id)
                {
                    TargetPath = manualPath.ToGamePathParentTuple(target.Id),
                    LoadoutItem = new LoadoutItem.New(tx, id)
                    {
                        Name = "Remove manual script",
                        ParentId = removal,
                        LoadoutId = target,
                    },
                },
            };
            await tx.Commit();
        }

        (await emitter.Diagnose(target.Rebase(), CancellationToken.None).ToListAsync()).Should().BeEmpty();
        (await onDisk.ReadAllTextAsync()).Should().Be("Original manual script", "diagnosis must not change disk files");
    }

    [Fact]
    public void IdenticalScriptsAndUnrelatedFilesDoNotCreateConflictWarnings()
    {
        (RelativePath Path, Hash Hash)[] files =
        [
            ("Mods/modFirst/content/scripts/game/identical.ws", Hash.From(1)),
            ("Mods/modSecond/content/scripts/game/identical.ws", Hash.From(1)),
            ("Mods/modFirst/content/scripts/game/first.ws", Hash.From(2)),
            ("Mods/modSecond/content/scripts/game/second.ws", Hash.From(3)),
            ("Mods/modFirst/content/bundles/same.bundle", Hash.From(4)),
            ("Mods/modSecond/content/bundles/same.bundle", Hash.From(5)),
            ("other/modThird/content/scripts/game/first.ws", Hash.From(6)),
        ];

        Witcher3ScriptConflictEmitter.FindConflicts(files).Should().BeEmpty();
    }

    [Fact]
    public void LooseScriptOverlapIsComparedCaseInsensitivelyAcrossModFolders()
    {
        (RelativePath Path, Hash Hash)[] files =
        [
            ("MODS/modFirst/CONTENT/SCRIPTS/game/Player.WS", Hash.From(1)),
            ("Mods/modSecond/content/scripts/game/player.ws", Hash.From(2)),
        ];

        var conflict = Witcher3ScriptConflictEmitter.FindConflicts(files).Should().ContainSingle().Which;
        conflict.ModFolders.Should().BeEquivalentTo(new[] { "modFirst", "modSecond" });
        conflict.ScriptPath.Should().BeEquivalentTo("scripts/game/player.ws");
    }
}
