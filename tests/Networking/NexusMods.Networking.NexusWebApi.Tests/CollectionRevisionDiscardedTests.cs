using FluentAssertions;
using NexusMods.Networking.NexusWebApi.Errors;
using NSubstitute;
using StrawberryShake;

namespace NexusMods.Networking.NexusWebApi.Tests;

public class CollectionRevisionDiscardedTests
{
    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, 7)]
    [InlineData(true, 7L)]
    [InlineData(true, "7")]
    [InlineData(true, "unrecognized metadata")]
    public void DiscardedRevisionRemainsATypedErrorWithOptionalMetadata(bool hasExtensions, object? revisionMetadata)
    {
        const string message = "This collection revision has been discarded.";
        var clientError = Substitute.For<IClientError>();
        clientError.Code.Returns(CollectionRevisionDiscarded.Code.Value);
        clientError.Message.Returns(message);
        IReadOnlyDictionary<string, object?>? extensions = hasExtensions
            ? new Dictionary<string, object?> { ["revision_number"] = revisionMetadata }
            : null;
        clientError.Extensions.Returns(extensions);

        var operation = Substitute.For<IOperationResult<object>>();
        operation.Errors.Returns(new[] { clientError });

        operation.TryExtractErrors<object, string, CollectionRevisionDiscarded>(out var result, out var data)
            .Should().BeTrue();

        data.Should().BeNull();
        result.Should().NotBeNull();
        result!.Errors.Should().ContainSingle().Which.Key.Should().Be(CollectionRevisionDiscarded.Code);
        result.TryGetError<CollectionRevisionDiscarded>(out var error).Should().BeTrue();
        error!.Message.Should().Be(message);
    }
}
