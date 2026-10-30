using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Jobs;
using NSubstitute;

namespace NexusMods.Networking.HttpDownloader.Tests;

/// <summary>
/// Exercises the real download job with scripted HTTP responses and an in-memory filesystem.
/// No sockets, Nexus API key, installed game, or external service is involved.
/// </summary>
public class HttpDownloadJobIntegrityTests
{
    private static readonly byte[] Payload = "0123456789"u8.ToArray();
    private const string OriginalETag = "\"original\"";
    private const int ResumeOffset = 3;

    [Fact]
    public async Task RestartingWithoutRangeSupportReplacesThePartialFile()
    {
        var getCount = 0;
        using var fixture = new DownloadFixture(request =>
        {
            if (request.Method == HttpMethod.Head) return Metadata(acceptRanges: false);
            getCount++;
            request.Headers.Range.Should().BeNull();
            return getCount == 1 ? InterruptedBody() : FullBody(Payload);
        });

        await fixture.InterruptAsync();
        await fixture.RunAsync();

        (await fixture.Destination.ReadAllBytesAsync()).Should().Equal(Payload);
        getCount.Should().Be(2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnIgnoredRangeReplacesTheFileEvenWhenTheNewBodyHasNoLength(bool hasLength)
    {
        var replacement = "NEW!!"u8.ToArray();
        var getCount = 0;
        using var fixture = new DownloadFixture(request =>
        {
            // The representation changes between HEAD and the resumed GET.
            if (request.Method == HttpMethod.Head) return Metadata();
            if (++getCount == 1) return InterruptedBody();

            request.Headers.Range!.ToString().Should().Be("bytes=3-");
            request.Headers.IfRange!.EntityTag!.Tag.Should().Be(OriginalETag);
            return FullBody(replacement, hasLength, "\"replacement\"");
        });

        await fixture.InterruptAsync();
        await fixture.RunAsync();

        (await fixture.Destination.ReadAllBytesAsync()).Should().Equal(replacement);
        fixture.State.TotalBytesDownloaded.Should().Be(Size.FromLong(replacement.Length));
        fixture.State.ContentLength.HasValue.Should().Be(hasLength);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AValidRangeResumesAtTheExactOffsetAndUsesTheTotalFileLength(bool headHasLength)
    {
        var getCount = 0;
        using var fixture = new DownloadFixture(request =>
        {
            if (request.Method == HttpMethod.Head) return Metadata(hasLength: headHasLength);
            if (++getCount == 1) return InterruptedBody();

            request.Headers.Range!.ToString().Should().Be("bytes=3-");
            var response = FullBody(Payload[ResumeOffset..]);
            response.StatusCode = HttpStatusCode.PartialContent;
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(ResumeOffset, Payload.Length - 1, Payload.Length);
            return response;
        });

        await fixture.InterruptAsync();
        await fixture.RunAsync();

        (await fixture.Destination.ReadAllBytesAsync()).Should().Equal(Payload);
        fixture.State.ContentLength.Value.Should().Be(Size.FromLong(Payload.Length));
    }

    [Theory]
    [InlineData("changed-etag")]
    [InlineData("missing-etag")]
    [InlineData("weak-etag")]
    [InlineData("changed-length")]
    public async Task ChangedMetadataCannotRelabelAnOldPartialFile(string change)
    {
        var replacement = change == "changed-length" ? "ABCDEFGH"u8.ToArray() : "ABCDEFGHIJ"u8.ToArray();
        var replacementETag = change switch
        {
            "changed-etag" => "\"replacement\"",
            "missing-etag" => null,
            "weak-etag" => "W/\"original\"",
            _ => OriginalETag,
        };
        var getCount = 0;
        using var fixture = new DownloadFixture(request =>
        {
            if (request.Method == HttpMethod.Head)
                return getCount == 0 ? Metadata() : Metadata(etag: replacementETag, length: replacement.Length);
            if (++getCount == 1) return InterruptedBody();

            request.Headers.Range.Should().BeNull("the existing prefix was not validated for the new response");
            if (change == "weak-etag") request.Headers.IfMatch.Should().BeEmpty();
            return FullBody(replacement, etag: replacementETag);
        });

        await fixture.InterruptAsync();
        await fixture.RunAsync();

        (await fixture.Destination.ReadAllBytesAsync()).Should().Equal(replacement);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("W/\"original\"")]
    public async Task APartialDownloadWithoutAStrongValidatorRestartsSafely(string? etag)
    {
        var getCount = 0;
        using var fixture = new DownloadFixture(request =>
        {
            if (request.Method == HttpMethod.Head) return Metadata(etag: etag);
            request.Headers.Range.Should().BeNull();
            request.Headers.IfMatch.Should().BeEmpty();
            return ++getCount == 1 ? InterruptedBody(etag) : FullBody(Payload, etag: etag);
        });

        await fixture.InterruptAsync();
        await fixture.RunAsync();

        (await fixture.Destination.ReadAllBytesAsync()).Should().Equal(Payload);
    }

    [Theory]
    [InlineData("missing-range")]
    [InlineData("wrong-start")]
    [InlineData("wrong-total")]
    [InlineData("incomplete-range")]
    [InlineData("wrong-body-length")]
    [InlineData("changed-etag")]
    public async Task InvalidPartialResponsesAreDiscardedBeforeTheirBodiesAreWritten(string problem)
    {
        var getCount = 0;
        using var fixture = new DownloadFixture(request =>
        {
            if (request.Method == HttpMethod.Head) return Metadata();
            if (++getCount == 1) return InterruptedBody();
            if (getCount > 2)
            {
                request.Headers.Range.Should().BeNull("an invalid range must discard the untrusted resume state");
                return FullBody(Payload);
            }

            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ScriptedContent([], Payload.Length - ResumeOffset, refuseCopy: true),
            };
            response.Headers.ETag = EntityTagHeaderValue.Parse(problem == "changed-etag" ? "\"other\"" : OriginalETag);
            response.Content.Headers.ContentRange = problem switch
            {
                "missing-range" => null,
                "wrong-start" => new ContentRangeHeaderValue(ResumeOffset - 1, Payload.Length - 1, Payload.Length),
                "wrong-total" => new ContentRangeHeaderValue(ResumeOffset, Payload.Length, Payload.Length + 1),
                "incomplete-range" => new ContentRangeHeaderValue(ResumeOffset, Payload.Length - 2, Payload.Length),
                _ => new ContentRangeHeaderValue(ResumeOffset, Payload.Length - 1, Payload.Length),
            };
            // Keep the body length consistent with each declared range so these cases
            // independently exercise the start, total, and end checks.
            response.Content.Headers.ContentLength = problem switch
            {
                "wrong-start" or "wrong-total" => Payload.Length - ResumeOffset + 1,
                "incomplete-range" => Payload.Length - ResumeOffset - 1,
                "wrong-body-length" => Payload.Length,
                _ => Payload.Length - ResumeOffset,
            };
            return response;
        });

        await fixture.InterruptAsync();
        await fixture.RunAsync();

        (await fixture.Destination.ReadAllBytesAsync()).Should().Equal(Payload);
        getCount.Should().Be(3);
    }

    [Fact]
    public async Task AnUnrequestedPartialResponseCannotBeReportedAsACompleteFile()
    {
        var getCount = 0;
        using var fixture = new DownloadFixture(request =>
        {
            if (request.Method == HttpMethod.Head) return Metadata();
            request.Headers.Range.Should().BeNull();
            if (++getCount > 1) return FullBody(Payload);

            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ScriptedContent([], Payload.Length, refuseCopy: true),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, Payload.Length - 1, Payload.Length);
            return response;
        });

        await fixture.RunAsync();

        (await fixture.Destination.ReadAllBytesAsync()).Should().Equal(Payload);
        getCount.Should().Be(2);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(11)]
    public async Task ABodyThatDisagreesWithItsAdvertisedLengthRetriesFromTheBeginning(int bodyLength)
    {
        var getCount = 0;
        using var fixture = new DownloadFixture(request =>
        {
            if (request.Method == HttpMethod.Head) return Metadata();
            request.Headers.Range.Should().BeNull();
            if (++getCount > 1) return FullBody(Payload);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ScriptedContent(Enumerable.Repeat((byte)'x', bodyLength).ToArray(), Payload.Length),
                Headers = { ETag = EntityTagHeaderValue.Parse(OriginalETag) },
            };
        });

        await fixture.RunAsync();

        (await fixture.Destination.ReadAllBytesAsync()).Should().Equal(Payload);
        getCount.Should().Be(2, "preallocated length must not hide missing bytes or an overlong body");
    }

    private static HttpResponseMessage Metadata(bool acceptRanges = true, bool hasLength = true, string? etag = OriginalETag, int? length = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ScriptedContent([], hasLength ? length ?? Payload.Length : null),
        };
        if (acceptRanges) response.Headers.AcceptRanges.Add("bytes");
        if (etag is not null) response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        return response;
    }

    private static HttpResponseMessage FullBody(byte[] payload, bool hasLength = true, string? etag = OriginalETag)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ScriptedContent(payload, hasLength ? payload.Length : null),
        };
        if (etag is not null) response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        return response;
    }

    private static HttpResponseMessage InterruptedBody(string? etag = OriginalETag)
    {
        var response = FullBody(Payload, etag: etag);
        response.Content = new ScriptedContent(Payload[..ResumeOffset], Payload.Length, interrupt: true);
        return response;
    }

    private sealed class DownloadFixture : IDisposable
    {
        private readonly HttpClient _client;
        private readonly HttpDownloadJob _job;
        private readonly IJobContext<HttpDownloadJob> _context;

        public DownloadFixture(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            var fileSystem = new InMemoryFileSystem();
            Destination = fileSystem.FromUnsanitizedFullPath(Path.GetFullPath("download-integrity-test.zip"));
            Destination.Parent.CreateDirectory();
            _client = new HttpClient(new ScriptedHandler(respond));
            _job = new HttpDownloadJob
            {
                Client = _client,
                Logger = NullLogger<HttpDownloadJob>.Instance,
                Uri = new Uri("https://downloads.example.invalid/archive.zip"),
                DownloadPageUri = new Uri("https://downloads.example.invalid/mod"),
                Destination = Destination,
            };
            _context = Substitute.For<IJobContext<HttpDownloadJob>>();
            _context.YieldAsync().Returns(Task.CompletedTask);
            _context.CancellationToken.Returns(CancellationToken.None);
        }

        public AbsolutePath Destination { get; }
        public IHttpDownloadState State => (IHttpDownloadState)_job.GetJobStateData()!;
        public Task<AbsolutePath> RunAsync() => _job.StartAsync(_context).AsTask();

        public async Task InterruptAsync()
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(RunAsync);
            State.TotalBytesDownloaded.Should().Be(Size.FromLong(ResumeOffset));
        }

        public void Dispose() => _client.Dispose();
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private sealed class ScriptedContent(byte[] payload, long? advertisedLength, bool interrupt = false, bool refuseCopy = false) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = advertisedLength ?? 0;
            return advertisedLength.HasValue;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            if (refuseCopy) throw new InvalidOperationException("An invalid response body must not be copied");
            await stream.WriteAsync(payload);
            if (interrupt) throw new OperationCanceledException("Simulated pause after a partial body");
        }
    }
}
