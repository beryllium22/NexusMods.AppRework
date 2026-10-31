using CliWrap;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Collections;
using NexusMods.Abstractions.NexusModsLibrary.Models;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Games.TestFramework;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Networking.NexusWebApi;
using NexusMods.Networking.NexusWebApi.Errors;
using NexusMods.Sdk;
using NexusMods.Sdk.NexusModsApi;
using NSubstitute;
using Xunit.Abstractions;
using RevisionQueryResult = NexusMods.Networking.NexusWebApi.Errors.GraphQlResult<(NexusMods.Abstractions.NexusWebApi.Types.RevisionNumber Number, NexusMods.Abstractions.NexusModsLibrary.Models.RevisionStatus Status)[], NexusMods.Networking.NexusWebApi.Errors.NotFound>;

namespace NexusMods.Collections.Tests;

public class CollectionRefreshTests : ACyberpunkIsolatedGameTest<CollectionRefreshTests>
{
    private readonly IGraphQlClient _client;

    public CollectionRefreshTests(ITestOutputHelper helper) : base(helper)
    {
        _client = ServiceProvider.GetRequiredService<IGraphQlClient>();
    }

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        var mapping = Substitute.For<IGameDomainToGameIdMappingCache>();
        mapping[Arg.Any<NexusModsGameId>()].Returns(GameDomain.From("cyberpunk2077"));

        return base.AddServices(services)
            .AddSingleton<IProcessRunner, NoExternalProcesses>()
            .AddSingleton(_ => Substitute.For<IGraphQlClient>())
            .AddSingleton(mapping);
    }

    [Fact]
    public async Task RefreshPersistsPublicationSoReloadedCollectionIsPublished()
    {
        var group = await CreateManagedCollection();
        SetResponse(Published(2));

        await CollectionCreator.RefreshPublishedRevision(ServiceProvider, group.Id);

        var reloaded = ManagedCollectionLoadoutGroup.Load(Connection.Db, group.Id);
        reloaded.LastPublishedRevisionNumber.Value.Should().Be(RevisionNumber.From(2));
        reloaded.ToStatus().Should().Be(RevisionStatus.Published);

        var previousTransaction = Connection.Db.BasisTxId;
        await CollectionCreator.RefreshPublishedRevision(ServiceProvider, group.Id);
        Connection.Db.BasisTxId.Should().Be(previousTransaction, "refreshing unchanged metadata must not create another revision");
    }

    [Fact]
    public async Task SuccessfulResponseWithoutPublishedRevisionsClearsCachedPublication()
    {
        var group = await CreateManagedCollection();
        SetResponse(new RevisionQueryResult([(RevisionNumber.From(2), RevisionStatus.Draft)]));

        await CollectionCreator.RefreshPublishedRevision(ServiceProvider, group.Id);

        var reloaded = ManagedCollectionLoadoutGroup.Load(Connection.Db, group.Id);
        reloaded.LastPublishedRevisionNumber.HasValue.Should().BeFalse();
        reloaded.ToStatus().Should().Be(RevisionStatus.Draft);
    }

    [Fact]
    public async Task ApiErrorKeepsCachedPublication()
    {
        var group = await CreateManagedCollection();
        var previousTransaction = Connection.Db.BasisTxId;
        SetResponse(new RevisionQueryResult(new NotFound { Message = "The collection is unavailable." }));

        await CollectionCreator.RefreshPublishedRevision(ServiceProvider, group.Id);

        ManagedCollectionLoadoutGroup.Load(Connection.Db, group.Id).LastPublishedRevisionNumber.Value
            .Should().Be(RevisionNumber.From(1));
        Connection.Db.BasisTxId.Should().Be(previousTransaction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledRefreshDoesNotCommitEvenIfApiReturnsData(bool cancelBeforeRequest)
    {
        var group = await CreateManagedCollection();
        var previousTransaction = Connection.Db.BasisTxId;
        using var cancellation = new CancellationTokenSource();
        if (cancelBeforeRequest) cancellation.Cancel();

        _client.QueryCollectionRevisionNumbers(CollectionSlug.From("test-collection"), GameDomain.From("cyberpunk2077"), cancellation.Token)
            .Returns(_ =>
            {
                cancellation.Cancel();
                return ValueTask.FromResult(Published(2));
            });

        var refresh = async () => await CollectionCreator.RefreshPublishedRevision(ServiceProvider, group.Id, cancellation.Token);
        await refresh.Should().ThrowAsync<OperationCanceledException>();

        ManagedCollectionLoadoutGroup.Load(Connection.Db, group.Id).LastPublishedRevisionNumber.Value
            .Should().Be(RevisionNumber.From(1));
        Connection.Db.BasisTxId.Should().Be(previousTransaction);
        if (cancelBeforeRequest)
            await _client.DidNotReceiveWithAnyArgs().QueryCollectionRevisionNumbers(CollectionSlug.From("test-collection"), GameDomain.From("cyberpunk2077"), cancellation.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InFlightRefreshDoesNotOverwriteDeletionOrNewerPublication(bool removeCollection)
    {
        var group = await CreateManagedCollection();
        var response = new TaskCompletionSource<RevisionQueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.QueryCollectionRevisionNumbers(CollectionSlug.From("test-collection"), GameDomain.From("cyberpunk2077"), CancellationToken.None)
            .Returns(_ => new ValueTask<RevisionQueryResult>(response.Task));

        var refresh = CollectionCreator.RefreshPublishedRevision(ServiceProvider, group.Id).AsTask();
        using (var tx = Connection.BeginTransaction())
        {
            if (removeCollection)
                tx.Delete(group.Id, recursive: true);
            else
                tx.Add(group.Id, ManagedCollectionLoadoutGroup.LastPublishedRevisionNumber, RevisionNumber.From(3));
            await tx.Commit();
        }

        var previousTransaction = Connection.Db.BasisTxId;
        response.SetResult(Published(2));
        await refresh;

        var reloaded = ManagedCollectionLoadoutGroup.Load(Connection.Db, group.Id);
        if (removeCollection)
            reloaded.IsValid().Should().BeFalse();
        else
            reloaded.LastPublishedRevisionNumber.Value.Should().Be(RevisionNumber.From(3));
        Connection.Db.BasisTxId.Should().Be(previousTransaction);
    }

    private void SetResponse(RevisionQueryResult result)
    {
        _client.QueryCollectionRevisionNumbers(CollectionSlug.From("test-collection"), GameDomain.From("cyberpunk2077"), CancellationToken.None)
            .Returns(ValueTask.FromResult(result));
    }

    private static RevisionQueryResult Published(ulong number) => new([(RevisionNumber.From(number), RevisionStatus.Published)]);

    private async Task<ManagedCollectionLoadoutGroup.ReadOnly> CreateManagedCollection()
    {
        var loadout = await CreateLoadout();
        var group = await CreateCollection(loadout.LoadoutId, "My collection");

        using var tx = Connection.BeginTransaction();
        var author = new User.New(tx)
        {
            Name = "Test curator",
            NexusId = 1,
        };
        var collection = new CollectionMetadata.New(tx)
        {
            CollectionId = CollectionId.From(1),
            Slug = CollectionSlug.From("test-collection"),
            Name = "My collection",
            GameId = NexusModsGameId.From(3333),
            AuthorId = author.Id,
        };

        tx.Add(group.Id, ManagedCollectionLoadoutGroup.Collection, collection.Id);
        tx.Add(group.Id, ManagedCollectionLoadoutGroup.CurrentRevisionNumber, RevisionNumber.From(2));
        tx.Add(group.Id, ManagedCollectionLoadoutGroup.LastUploadDate, DateTimeOffset.UtcNow);
        tx.Add(group.Id, ManagedCollectionLoadoutGroup.LastPublishedRevisionNumber, RevisionNumber.From(1));
        await tx.Commit();

        return ManagedCollectionLoadoutGroup.Load(Connection.Db, group.Id);
    }

    private sealed class NoExternalProcesses : IProcessRunner
    {
        public void Run(Command command, bool logOutput = false)
            => throw new NotSupportedException("This test must not start external processes.");

        public Task<CommandResult> RunAsync(Command command, bool logOutput = false, CancellationToken cancellationToken = default)
            => throw new FileNotFoundException($"{command.TargetFilePath} is unavailable in this test.");

        public Task RunAsync(System.Diagnostics.Process process, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("This test must not start external processes.");
    }
}
