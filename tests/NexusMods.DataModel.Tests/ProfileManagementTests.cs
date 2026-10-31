using CliWrap;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Extensions;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Abstractions.Loadouts.Synchronizers.Conflicts;
using NexusMods.Games.RedEngine.Cyberpunk2077.Models;
using NexusMods.Games.RedEngine.Cyberpunk2077.SortOrder;
using NexusMods.Games.TestFramework;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.ElementComparers;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;
using OneOf;
using Xunit.Abstractions;

namespace NexusMods.DataModel.Tests;

/// <summary>Exercises profile operations against real database models, without game processes.</summary>
public class ProfileManagementTests(ITestOutputHelper output) : ACyberpunkIsolatedGameTest<ProfileManagementTests>(output)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
        => base.AddServices(services).AddSingleton<IProcessRunner, NoExternalProcesses>();

    [Fact]
    public async Task CopyPreservesTheCompleteGraphWithIndependentInternalReferences()
    {
        var source = await CreateSourceGraph();
        var sourceSnapshot = SnapshotGraph(source);
        var originalIds = OwnedIds(source);
        var copy = await LoadoutManager.CopyLoadout(source, suggestedName: "  Texture experiments  ");

        copy.Name.Should().Be("Texture experiments");
        copy.ShortName.Should().NotBe(source.ShortName);
        copy.InstallationId.Should().Be(source.InstallationId);
        copy.GameVersion.Should().Be(source.GameVersion);
        copy.LocatorIds.Should().BeEquivalentTo(source.LocatorIds);
        copy.Revision.Should().Be(0);
        copy.LoadoutKind.Should().Be(LoadoutKind.Default);
        copy.Contains(Loadout.LastAppliedDateTime).Should().BeFalse();
        copy.Contains(RedModDeploymentState.Pending).Should().BeFalse();
        copy.Contains(RedModDeploymentState.InputFingerprint).Should().BeFalse();
        LoadoutManager.GetCurrentlyActiveLoadout(GameInstallation).Value.Should().Be(source.LoadoutId);

        var sourceItems = LoadoutItem.FindByLoadout(source.Db, source).ToDictionary(StructuralKey);
        var copiedItems = LoadoutItem.FindByLoadout(copy.Db, copy).ToDictionary(StructuralKey);
        copiedItems.Keys.Should().BeEquivalentTo(sourceItems.Keys);
        OwnedIds(copy).Should().NotIntersectWith(originalIds);

        foreach (var (key, sourceItem) in sourceItems)
        {
            var item = copiedItems[key];
            item.LoadoutId.Should().Be(copy.LoadoutId);
            item.Name.Should().Be(sourceItem.Name);
            item.IsDisabled.Should().Be(sourceItem.IsDisabled);
            item.IsEnabled().Should().Be(sourceItem.IsEnabled());
            item.HasParent().Should().Be(sourceItem.HasParent());
            if (item.HasParent())
                item.ParentId.Should().Be(LoadoutItemGroupId.From(copiedItems[StructuralKey(sourceItem.Parent.AsLoadoutItem())].Id));

            if (CollectionGroup.IsReadOnly.TryGetValue(sourceItem, out var isReadOnly))
                CollectionGroup.IsReadOnly.Get(item).Should().Be(isReadOnly);

            if (LibraryLinkedLoadoutItem.LibraryItem.TryGetValue(sourceItem, out var libraryId))
                LibraryLinkedLoadoutItem.LibraryItem.Get(item).Should().Be(libraryId, "immutable library data is shared rather than duplicated");

            if (LoadoutItemWithTargetPath.TargetPath.TryGetValue(sourceItem, out var sourcePath))
            {
                var target = LoadoutItemWithTargetPath.TargetPath.Get(item);
                target.Item1.Should().Be(copy.Id, "the loadout reference inside the target-path tuple must be remapped");
                target.Item2.Should().Be(sourcePath.Item2);
                target.Item3.Should().Be(sourcePath.Item3);
                LoadoutFile.Hash.Get(item).Should().Be(LoadoutFile.Hash.Get(sourceItem));
                LoadoutFile.Size.Get(item).Should().Be(LoadoutFile.Size.Get(sourceItem));
            }
        }

        var sourcePriorities = LoadoutItemGroupPriority.FindByLoadout(source.Db, source).ToArray();
        var copyPriorities = LoadoutItemGroupPriority.FindByLoadout(copy.Db, copy).ToArray();
        copyPriorities.Should().HaveCount(sourcePriorities.Length);
        foreach (var priority in sourcePriorities)
        {
            var target = copiedItems[StructuralKey(priority.Target.AsLoadoutItem())];
            var copiedPriority = copyPriorities.Single(row => row.TargetId.Value == target.Id);
            copiedPriority.LoadoutId.Should().Be(copy.LoadoutId);
            copiedPriority.Priority.Should().Be(priority.Priority);
        }

        var sourceOrders = SortOrder.FindByLoadout(source.Db, source).ToArray();
        var copyOrders = SortOrder.FindByLoadout(copy.Db, copy).ToArray();
        copyOrders.Should().HaveCount(2);
        foreach (var order in sourceOrders)
        {
            var expectedParent = order.ParentEntity.Match(
                _ => copy.Id,
                collectionId => copiedItems[StructuralKey(LoadoutItem.Load(source.Db, collectionId.Value))].Id);
            var copiedOrder = copyOrders.Single(row => row.ParentEntity.Match(id => id.Value, id => id.Value) == expectedParent);
            copiedOrder.ParentEntity.Index.Should().Be(order.ParentEntity.Index, "packed parent references retain their loadout/collection discriminator");
            copiedOrder.LoadoutId.Should().Be(copy.LoadoutId);
            copiedOrder.SortOrderTypeId.Should().Be(order.SortOrderTypeId);
            copiedOrder.Contains(RedModSortOrder.Marker).Should().BeTrue();

            var expectedEntries = SortOrderItem.FindByParentSortOrder(source.Db, order)
                .Select(item => (item.SortIndex, Folder: RedModSortOrderItem.RedModFolderName.Get(item))).ToArray();
            var actualEntries = SortOrderItem.FindByParentSortOrder(copy.Db, copiedOrder)
                .Select(item => (item.SortIndex, Folder: RedModSortOrderItem.RedModFolderName.Get(item))).ToArray();
            actualEntries.Should().BeEquivalentTo(expectedEntries);
        }

        ProfileCopyReservation.All(Connection.Db).Should().BeEmpty();
        SnapshotGraph(source.Rebase()).Should().BeEquivalentTo(sourceSnapshot);
    }

    [Fact]
    public async Task EditingAndDeletingTheCopyLeavesTheSourceAndSharedLibraryIntact()
    {
        var source = await CreateSourceGraph();
        var before = SnapshotGraph(source);
        var copy = await LoadoutManager.CopyLoadout(source);
        var copiedIds = OwnedIds(copy);
        var copiedItems = LoadoutItem.FindByLoadout(copy.Db, copy).ToArray();
        var linkedMod = copiedItems.Single(item => item.Name == "Linked mod");
        var libraryId = LibraryLinkedLoadoutItem.LibraryItem.Get(linkedMod);
        var collection = copiedItems.Single(item => item.Name == "Curated collection");
        var copiedOrder = SortOrder.FindByLoadout(copy.Db, copy).First();
        var copiedSortItem = SortOrderItem.FindByParentSortOrder(copy.Db, copiedOrder).First();

        await LoadoutManager.RenameLoadout(copy, "  Copy only  ");
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(linkedMod.Id, LoadoutItem.Name, "Changed only in copy");
            tx.Retract(collection.Id, LoadoutItem.Disabled, Null.Instance);
            tx.Add(copiedSortItem.Id, SortOrderItem.SortIndex, 99);
            await tx.Commit();
        }

        SnapshotGraph(source.Rebase()).Should().BeEquivalentTo(before);
        await LoadoutManager.DeleteLoadout(copy, deactivateIfActive: false);

        Loadout.Load(Connection.Db, copy.Id).IsValid().Should().BeFalse();
        foreach (var id in copiedIds)
            Connection.Db.Get(id).ToArray().Should().BeEmpty("all copied items, priorities and order entries belong only to the copy");

        LibraryItem.Load(Connection.Db, libraryId).IsValid().Should().BeTrue();
        source.Rebase().IsVisible().Should().BeTrue();
        SnapshotGraph(source.Rebase()).Should().BeEquivalentTo(before);
        ProfileCopyReservation.All(Connection.Db).Should().BeEmpty();
    }

    [Fact]
    public async Task DeletingAnActiveProfileRemovesFilesIngestedDuringDeactivation()
    {
        var source = await CreateLoadout();
        var external = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "archive/pc/mod/added-after-profile.archive"));
        external.Parent.CreateDirectory();
        await external.WriteAllTextAsync("Created outside the app after the last sync");

        await LoadoutManager.DeleteLoadout(source);

        Loadout.Load(Connection.Db, source.Id).IsValid().Should().BeFalse();
        LoadoutItem.FindByLoadout(Connection.Db, source.LoadoutId).Should().BeEmpty("deactivation ingests external files into a new Overrides group before deleting the profile");
        LoadoutItemGroupPriority.FindByLoadout(Connection.Db, source.LoadoutId).Should().BeEmpty();
        SortOrder.FindByLoadout(Connection.Db, source.LoadoutId).Should().BeEmpty();
        external.FileExists.Should().BeFalse();
        LoadoutManager.GetCurrentlyActiveLoadout(GameInstallation).HasValue.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlreadyCancelledDeletionLeavesActiveAndInactiveProfilesUnchanged(bool inactive)
    {
        var source = await CreateSourceGraph();
        var target = inactive ? await LoadoutManager.CopyLoadout(source) : source;
        var before = SnapshotGraph(target);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var delete = () => LoadoutManager.DeleteLoadout(target, cancellationToken: cancellation.Token).AsTask();

        await delete.Should().ThrowAsync<OperationCanceledException>();
        target.Rebase().IsVisible().Should().BeTrue();
        SnapshotGraph(target.Rebase()).Should().BeEquivalentTo(before);
        LoadoutManager.GetCurrentlyActiveLoadout(GameInstallation).Value.Should().Be(source.LoadoutId);
    }

    [Fact]
    public async Task ConcurrentRepeatedDeletesDoNotRecreateAnEmptyProfileOrAffectTheSource()
    {
        var source = await CreateSourceGraph();
        var before = SnapshotGraph(source);
        var copy = await LoadoutManager.CopyLoadout(source);
        var copiedIds = OwnedIds(copy);

        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => LoadoutManager.DeleteLoadout(copy).AsTask()));
        await LoadoutManager.DeleteLoadout(copy);

        foreach (var id in copiedIds)
            Connection.Db.Get(id).ToArray().Should().BeEmpty();
        SnapshotGraph(source.Rebase()).Should().BeEquivalentTo(before);
        LoadoutManager.GetCurrentlyActiveLoadout(GameInstallation).Value.Should().Be(source.LoadoutId);
    }

    [Fact]
    public async Task NamedCreateCopyAndRenameTrimNamesWithoutChangingDeploymentState()
    {
        var source = await LoadoutManager.CreateLoadout(GameInstallation, "  Main profile  ");
        source.Name.Should().Be("Main profile");
        var copy = await LoadoutManager.CopyLoadout(source, suggestedName: "  Safe experiments  ");
        copy.Name.Should().Be("Safe experiments");

        var applied = source.LastAppliedDateTime;
        var shortName = source.ShortName;
        var sourceItems = LoadoutItem.FindByLoadout(source.Db, source).Select(item => item.Id).ToArray();
        await LoadoutManager.RenameLoadout(source, "  Main profile revised  ");
        var renamed = source.Rebase();
        renamed.Name.Should().Be("Main profile revised");
        renamed.ShortName.Should().Be(shortName);
        renamed.LastAppliedDateTime.Should().Be(applied);
        LoadoutItem.FindByLoadout(renamed.Db, renamed).Select(item => item.Id).Should().BeEquivalentTo(sourceItems);
        copy.Rebase().Name.Should().Be("Safe experiments");
    }

    [Fact]
    public async Task InvalidNamesRejectAllThreeOperationsWithoutPersistingChanges()
    {
        var source = await CreateSourceGraph();
        var before = SnapshotGraph(source);
        var profileIds = Loadout.All(Connection.Db).Select(profile => profile.Id).ToArray();
        string[] invalidNames = ["", "   ", "line\nbreak", "control\0character", new string('x', LoadoutName.MaximumLength + 1)];

        foreach (var name in invalidNames)
        {
            Func<Task> create = async () => { await LoadoutManager.CreateLoadout(GameInstallation, name); };
            Func<Task> copy = async () => { await LoadoutManager.CopyLoadout(source, suggestedName: name); };
            Func<Task> rename = () => LoadoutManager.RenameLoadout(source, name).AsTask();
            await create.Should().ThrowAsync<ArgumentException>();
            await copy.Should().ThrowAsync<ArgumentException>();
            await rename.Should().ThrowAsync<ArgumentException>();
        }

        Loadout.All(Connection.Db).Select(profile => profile.Id).Should().BeEquivalentTo(profileIds);
        SnapshotGraph(source.Rebase()).Should().BeEquivalentTo(before);
        ProfileCopyReservation.All(Connection.Db).Should().BeEmpty();
    }

    [Fact]
    public async Task AlreadyCancelledCopyDoesNotReserveOrPublishAnything()
    {
        var source = await CreateSourceGraph();
        var before = SnapshotGraph(source);
        var profiles = Loadout.All(Connection.Db).Select(profile => profile.Id).ToArray();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var copy = () => LoadoutManager.CopyLoadout(source, cancellation.Token).AsTask();

        await copy.Should().ThrowAsync<OperationCanceledException>();
        Loadout.All(Connection.Db).Select(profile => profile.Id).Should().BeEquivalentTo(profiles);
        ProfileCopyReservation.All(Connection.Db).Should().BeEmpty();
        SnapshotGraph(source.Rebase()).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task ConcurrentCopiesReceiveDistinctShortNamesAndIndependentGraphs()
    {
        var source = await CreateSourceGraph();
        var copies = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(number => LoadoutManager.CopyLoadout(source, suggestedName: $"Copy {number}").AsTask()));

        copies.Select(copy => copy.ShortName).Append(source.ShortName).Should().OnlyHaveUniqueItems();
        copies.Select(copy => copy.Id).Should().OnlyHaveUniqueItems();
        var allIds = new HashSet<EntityId>(OwnedIds(source));
        foreach (var copy in copies)
        {
            var ids = OwnedIds(copy.Rebase());
            ids.Should().NotIntersectWith(allIds);
            ids.Should().HaveCount(OwnedIds(source).Count);
            allIds.UnionWith(ids);
        }
        ProfileCopyReservation.All(Connection.Db).Should().BeEmpty();
    }

    [Fact]
    public async Task ConcurrentFirstProfilesShareOneInstallationAndIngestOnlyOnce()
    {
        var original = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "archive/pc/mod/preexisting.archive"));
        original.Parent.CreateDirectory();
        await original.WriteAllTextAsync("Existing manual mod");

        var profiles = await Task.WhenAll(Enumerable.Range(0, 4).Select(async number =>
            await LoadoutManager.CreateLoadout(GameInstallation, $"First profile {number}")));
        var metadata = GameInstallMetadata.All(Connection.Db).Should().ContainSingle().Which;
        profiles.Select(profile => profile.InstallationId.Value).Should().OnlyContain(id => id == metadata.Id);
        profiles.Select(profile => profile.ShortName).Should().OnlyHaveUniqueItems();
        metadata.Contains(GameInstallMetadata.InitialDiskStateTransaction).Should().BeTrue();

        var ingested = profiles.Select(profile => profile.Rebase())
            .Where(profile => profile.Contains(Loadout.LastAppliedDateTime)).Should().ContainSingle().Which;
        LoadoutManager.GetCurrentlyActiveLoadout(GameInstallation).Value.Should().Be(ingested.LoadoutId);
        var files = LoadoutItem.FindByLoadout(Connection.Db, ingested).OfTypeLoadoutItemWithTargetPath().ToArray();
        files.Should().ContainSingle(file => file.TargetPath.Item3 == (RelativePath)"archive/pc/mod/preexisting.archive");
        foreach (var unused in profiles.Where(profile => profile.Id != ingested.Id))
            LoadoutItem.FindByLoadout(Connection.Db, unused).OfTypeLoadoutItemWithTargetPath().Should().BeEmpty();

        (await original.ReadAllTextAsync()).Should().Be("Existing manual mod");
        ProfileCopyReservation.All(Connection.Db).Should().BeEmpty();
    }

    [Fact]
    public async Task RecoveryRemovesOnlyReservationsAndPreservesProfilesWithStaleMarkers()
    {
        var source = await CreateSourceGraph();
        var before = SnapshotGraph(source);
        EntityId orphan;
        using (var tx = Connection.BeginTransaction())
        {
            orphan = tx.TempId();
            tx.Add(orphan, ProfileCopyReservation.Reserved, Null.Instance);
            tx.Add(source.Id, ProfileCopyReservation.Reserved, Null.Instance);
            var result = await tx.Commit();
            orphan = result[orphan];
        }

        await new ProfileCopyRecoveryService(Connection).StartAsync(CancellationToken.None);

        Connection.Db.Get(orphan).ToArray().Should().BeEmpty();
        ProfileCopyReservation.All(Connection.Db).Should().BeEmpty();
        source.Rebase().IsValid().Should().BeTrue();
        SnapshotGraph(source.Rebase()).Should().BeEquivalentTo(before);
    }

    [Theory]
    [InlineData("item parent")]
    [InlineData("priority target")]
    [InlineData("sort-order parent")]
    [InlineData("non-collection sort-order parent")]
    [InlineData("self-cycle")]
    public async Task InvalidInternalReferencesAreRejectedBeforeCopying(string invalidLink)
    {
        var source = await CreateLoadout();
        var other = await CreateLoadout();
        var foreignCollection = await CreateCollection(other, "Another profile's collection");
        using (var tx = Connection.BeginTransaction())
        {
            if (invalidLink == "item parent")
                AddEmptyGroup(tx, source, "Invalid parent", foreignCollection.AsLoadoutItemGroup().LoadoutItemGroupId);
            else if (invalidLink == "priority target")
                _ = new LoadoutItemGroupPriority.New(tx)
                {
                    LoadoutId = source,
                    TargetId = foreignCollection.AsLoadoutItemGroup().LoadoutItemGroupId,
                    Priority = ConflictPriority.From(123),
                };
            else if (invalidLink == "non-collection sort-order parent")
            {
                var ordinaryGroup = AddEmptyGroup(tx, source, "Not a collection");
                AddOrder(tx, source, OneOf<LoadoutId, CollectionGroupId>.FromT1(CollectionGroupId.From(ordinaryGroup.Value)), ["Invalid"]);
            }
            else if (invalidLink == "self-cycle")
            {
                var cyclicGroup = AddEmptyGroup(tx, source, "Cyclic group");
                tx.Add(cyclicGroup.Value, LoadoutItem.Parent, cyclicGroup.Value);
            }
            else
                AddOrder(tx, source, OneOf<LoadoutId, CollectionGroupId>.FromT1(foreignCollection.CollectionGroupId), ["Foreign"]);
            await tx.Commit();
        }

        var before = SnapshotGraph(source.Rebase());
        var profiles = Loadout.All(Connection.Db).Select(profile => profile.Id).ToArray();
        var copy = () => LoadoutManager.CopyLoadout(source).AsTask();
        await copy.Should().ThrowAsync<InvalidOperationException>();
        ProfileCopyReservation.All(Connection.Db).Should().BeEmpty();
        Loadout.All(Connection.Db).Select(profile => profile.Id).Should().BeEquivalentTo(profiles);
        SnapshotGraph(source.Rebase()).Should().BeEquivalentTo(before);
        foreignCollection.Rebase().IsValid().Should().BeTrue();
    }

    private async Task<Loadout.ReadOnly> CreateSourceGraph()
    {
        var source = await CreateLoadout();
        var library = await CreateLibraryArchive(Connection, "shared-library.zip");
        var collection = await CreateCollection(source, "Curated collection");
        using var tx = Connection.BeginTransaction();
        tx.Add(source.Id, Loadout.Revision, 42UL);
        tx.Add(source.Id, Loadout.LastAppliedDateTime, DateTimeOffset.Parse("2026-01-02T03:04:05Z"));
        tx.Add(source.Id, Loadout.LocatorIds, LocatorId.From("secondary-source-locator"));
        tx.Add(source.Id, RedModDeploymentState.Pending, Null.Instance);
        tx.Add(source.Id, RedModDeploymentState.InputFingerprint, "source-deployment-receipt");
        tx.Add(collection.Id, CollectionGroup.IsReadOnly, true);
        tx.Add(collection.Id, LoadoutItem.Disabled, Null.Instance);

        var folder = AddEmptyGroup(tx, source, "Nested folder", collection.AsLoadoutItemGroup().LoadoutItemGroupId);
        var mod = AddEmptyGroup(tx, source, "Linked mod", folder);
        tx.Add(mod.Value, LibraryLinkedLoadoutItem.LibraryItem, library.Id);
        AddFile(tx, source, mod, new GamePath(LocationId.Game, "mods/Zeta/info.json"), "{\"name\":\"Zeta\"}", out _, out _);
        AddFile(tx, source, mod, new GamePath(LocationId.Game, "archive/pc/mod/shared.archive"), "preferred content", out _, out _);
        var loser = AddEmptyGroup(tx, source, "Conflict loser");
        tx.Add(loser.Value, LoadoutItem.Disabled, Null.Instance);
        AddFile(tx, source, loser, new GamePath(LocationId.Game, "archive/pc/mod/shared.archive"), "other content", out _, out _);
        _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = source, TargetId = mod, Priority = ConflictPriority.From(9000) };
        _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = source, TargetId = loser, Priority = ConflictPriority.From(1000) };

        AddOrder(tx, source, OneOf<LoadoutId, CollectionGroupId>.FromT0(source.LoadoutId), ["Zeta", "Alpha"]);
        AddOrder(tx, source, OneOf<LoadoutId, CollectionGroupId>.FromT1(collection.CollectionGroupId), ["Alpha", "Zeta"]);
        await tx.Commit();
        return source.Rebase();
    }

    private void AddOrder(ITransaction tx, LoadoutId loadout, OneOf<LoadoutId, CollectionGroupId> parent, string[] folders)
    {
        var order = tx.TempId();
        tx.Add(order, SortOrder.Loadout, loadout.Value);
        tx.Add(order, SortOrder.ParentEntity, parent);
        tx.Add(order, SortOrder.SortOrderTypeId, ServiceProvider.GetRequiredService<RedModSortOrderVariety>().SortOrderVarietyId.Value);
        tx.Add(order, RedModSortOrder.Marker, Null.Instance);
        for (var index = 0; index < folders.Length; index++)
        {
            var item = tx.TempId();
            tx.Add(item, SortOrderItem.ParentSortOrder, order);
            tx.Add(item, SortOrderItem.SortIndex, index);
            tx.Add(item, RedModSortOrderItem.RedModFolderName, (RelativePath)folders[index]);
        }
    }

    private static string StructuralKey(LoadoutItem.ReadOnly item)
        => string.Join('/', item.GetThisAndParents().Reverse().Select(parent => parent.Name));

    private static HashSet<EntityId> OwnedIds(Loadout.ReadOnly source)
    {
        var ids = new HashSet<EntityId> { source.Id };
        ids.UnionWith(LoadoutItem.FindByLoadout(source.Db, source).Select(item => item.Id));
        ids.UnionWith(LoadoutItemGroupPriority.FindByLoadout(source.Db, source).Select(priority => priority.Id));
        foreach (var order in SortOrder.FindByLoadout(source.Db, source))
        {
            ids.Add(order.Id);
            ids.UnionWith(SortOrderItem.FindByParentSortOrder(source.Db, order).Select(item => item.Id));
        }
        return ids;
    }

    private static Dictionary<EntityId, string[]> SnapshotGraph(Loadout.ReadOnly source)
        => OwnedIds(source).ToDictionary(id => id, id => source.Db.Get(id)
            .Select(datom => $"{datom.A}:{datom.Prefix.ValueTag}:{Convert.ToHexString(datom.ValueSpan)}")
            .Order(StringComparer.Ordinal).ToArray());

    private sealed class NoExternalProcesses : IProcessRunner
    {
        public void Run(Command command, bool logOutput = false) => throw new NotSupportedException("Profile tests must not start game processes.");
        public Task<CommandResult> RunAsync(Command command, bool logOutput = false, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Profile tests must not start game processes.");
        public Task RunAsync(System.Diagnostics.Process process, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Profile tests must not start game processes.");
    }
}
