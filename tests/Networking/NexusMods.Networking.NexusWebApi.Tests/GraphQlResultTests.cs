using FluentAssertions;
using NexusMods.Networking.NexusWebApi.Errors;

namespace NexusMods.Networking.NexusWebApi.Tests;

public class GraphQlResultTests
{
    [Fact]
    public void ReturnsTheMatchingTypedErrorFromSeveralErrors()
    {
        var unrelated = new NotFound { Message = "Another item was not found." };
        var expected = new CollectionRevisionDiscarded { Message = "This revision was discarded." };
        var result = new GraphQlResult<NoData>(
        [
            new(NotFound.Code, unrelated),
            new(CollectionRevisionDiscarded.Code, expected),
        ]);

        result.TryGetError<CollectionRevisionDiscarded>(out var error).Should().BeTrue();

        error.Should().BeSameAs(expected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingErrorCodeReturnsFalseAndNoError(bool hasOtherError)
    {
        var result = hasOtherError
            ? new GraphQlResult<NoData>([new(NotFound.Code, new NotFound { Message = "No matching item." })])
            : new GraphQlResult<NoData>(new NoData());

        result.TryGetError<CollectionRevisionDiscarded>(out var error).Should().BeFalse();

        error.Should().BeNull();
    }

    [Fact]
    public void AMatchingCodeWithTheWrongPayloadTypeReportsTheActualType()
    {
        var result = new GraphQlResult<NoData>(
        [
            new(CollectionRevisionDiscarded.Code, new NotFound { Message = "Inconsistent typed error." }),
        ]);

        Action retrieveError = () => result.TryGetError<CollectionRevisionDiscarded>(out _);

        retrieveError.Should().Throw<NotSupportedException>()
            .WithMessage("*NotFound*expected*CollectionRevisionDiscarded*");
    }
}
