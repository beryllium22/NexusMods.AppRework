using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using DynamicData.Kernel;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.HttpDownloads;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using Polly;
using Polly.Retry;
using System.ComponentModel;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Library;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NexusMods.Networking.HttpDownloader;

/// <summary>
/// Download job.
/// </summary>
[PublicAPI]
public record HttpDownloadJob : IJobDefinitionWithStart<HttpDownloadJob, AbsolutePath>, IHttpDownloadJob
{
    private static readonly ResiliencePipeline<AbsolutePath> ResiliencePipeline = BuildResiliencePipeline();

    /// <summary>
    /// Client.
    /// </summary>
    public required HttpClient Client { get; init; }

    /// <summary>
    /// Logger.
    /// </summary>
    public required ILogger Logger { get; init; }

    /// <summary>
    /// The uri of the download page.
    /// </summary>
    public required Uri Uri { get; init; }
    
    /// <summary>
    /// The uri of the download page.
    /// </summary>
    public required Uri DownloadPageUri { get; init; }
    
    /// <summary>
    /// The destination of the download.
    /// </summary>
    public required AbsolutePath Destination { get; init; }
    
    /// <summary>
    /// Only exists for extension by derived classes.
    /// </summary>
    public virtual ValueTask AddMetadata(ITransaction transaction, LibraryFile.New libraryFile)
    {
        return ValueTask.CompletedTask;
    }

    private readonly HttpDownloadState _state = new();
    
    public bool SupportsPausing => true;
    /// <summary>
    /// Implementation for external job state data access
    /// </summary>
    public IPublicJobStateData? GetJobStateData() => _state;
    
    /// <summary>
    /// Constructor for the job
    /// </summary>
    public static IJobTask<HttpDownloadJob, AbsolutePath> Create(
        IServiceProvider provider,
        Uri uri,
        Uri downloadPage,
        AbsolutePath destination)
    {
        var monitor = provider.GetRequiredService<IJobMonitor>();
        var job = new HttpDownloadJob
        {
            Uri = uri,
            DownloadPageUri = downloadPage,
            Destination = destination,
            Logger = provider.GetRequiredService<ILogger<HttpDownloadJob>>(),
            Client = provider.GetRequiredService<HttpClient>(),
        };

        return monitor.Begin<HttpDownloadJob, AbsolutePath>(job);
    }

    /// <inheritdoc/>
    public async ValueTask<AbsolutePath> StartAsync(IJobContext<HttpDownloadJob> context)
    {
        var result = await ResiliencePipeline.ExecuteAsync(
            callback: static (tuple, _) =>
            {
                var (self, context) = tuple;
                return self.StartAsyncImpl(context);
            },
            state: (this, context),
            cancellationToken: context.CancellationToken
        );

        return result;
    }

    private async ValueTask<AbsolutePath> StartAsyncImpl(IJobContext<HttpDownloadJob> context)
    {
        await context.YieldAsync();
        if (_state.TotalBytesDownloaded > Size.Zero)
            Logger.LogInformation("Resuming download from {Bytes} bytes", _state.TotalBytesDownloaded);
        
        await FetchMetadata(context);

        await context.YieldAsync();
        await using var fileStream = Destination.Open(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

        fileStream.Position = (long)_state.TotalBytesDownloaded.Value;

        await context.YieldAsync();
        using var request = PrepareRequest(out var isRangeRequest);
        using var response = await Client.SendAsync(
            request: request,
            completionOption: HttpCompletionOption.ResponseHeadersRead,
            cancellationToken: context.CancellationToken
        );

        // Some servers advertise ranges but reject a particular resume request. Repeating
        // that offset cannot recover; let the retry pipeline request the complete file.
        if (isRangeRequest && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            throw ResetAfterInvalidResponse("The server rejected the resume range; retrying the complete download");

        response.EnsureSuccessStatusCode();

        if (response.StatusCode == HttpStatusCode.OK)
        {
            // A 200 always contains a full representation, including a retry on a server that
            // never advertised ranges. Replace the partial file instead of appending to it.
            if (_state.TotalBytesDownloaded > Size.Zero)
                Logger.LogInformation("Server returned the full download from `{PageUri}`; restarting from scratch", DownloadPageUri);

            _state.TotalBytesDownloaded = Size.Zero;
            fileStream.Position = 0;
            fileStream.SetLength(0);

            // GET describes the bytes we are about to write; HEAD can be stale after If-Range
            // fails. In particular, do not retain the old size for a new chunked response.
            var length = response.Content.Headers.ContentLength;
            _state.ContentLength = length is not null ? Size.FromLong(length.Value) : Optional<Size>.None;
            _state.ETag = response.Headers.ETag;
            if (isRangeRequest) _state.AcceptRanges = false;
        }
        else if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            var totalLength = range?.Length ?? (_state.ContentLength.HasValue ? (long)_state.ContentLength.Value.Value : (long?)null);
            var bodyLength = response.Content.Headers.ContentLength;

            // We requested all remaining bytes. A partial response is usable only when it
            // starts exactly at our offset and reaches the end of the same representation.
            // Content-Length on a 206 is the body length, not the total file size.
            if (!isRangeRequest || range is null || !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
                || range.From != fileStream.Position || totalLength is null || range.To != totalLength - 1
                || (bodyLength is not null && bodyLength != range.To - range.From + 1)
                || (_state.ContentLength.HasValue && (long)_state.ContentLength.Value.Value != totalLength)
                || (response.Headers.ETag is { } etag && (!_state.ETag.HasValue || !etag.Equals(_state.ETag.Value))))
            {
                throw ResetAfterInvalidResponse("The partial download does not match the requested range or representation");
            }

            _state.ContentLength = Size.FromLong(totalLength.Value);
        }
        else
        {
            throw ResetAfterInvalidResponse($"Unexpected download response status {(int)response.StatusCode}");
        }

        if (_state.ContentLength.HasValue)
            fileStream.SetLength((long)_state.ContentLength.Value.Value);

        // Start reporting only after the response has established the offset and length.
        // Otherwise the timer can restore stale progress while a full response resets it.
        var finalPosition = fileStream.Position;
        try
        {
            await using var outputStream = new StreamProgressWrapper<IJobContext<HttpDownloadJob>>(
                fileStream,
                context,
                (state, tuple) =>
                {
                    var (bytesWritten, speed) = tuple;
                    _state.TotalBytesDownloaded = bytesWritten;
                    state.SetPercent(bytesWritten, _state.ContentLength.ValueOr(static () => Size.One));
                    state.SetRateOfProgress(speed);
                });

            try
            {
                await response.Content.CopyToAsync(outputStream, context.CancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Logger.LogWarning(e, "Exception while downloading from `{PageUri}`, downloaded `{DownloadedBytes}` bytes", DownloadPageUri, outputStream.Position);
                throw;
            }
            finally
            {
                finalPosition = outputStream.Position;
            }
        }
        finally
        {
            // DisposeAsync waits for outstanding timer callbacks before publishing the final
            // offset, so a late tick cannot overwrite the value used by the next attempt.
            _state.TotalBytesDownloaded = Size.FromLong(finalPosition);
        }

        // Preallocation makes Length look correct even when the body stopped early. Check
        // actual written bytes before declaring success and reset before the retry pipeline.
        if (_state.ContentLength.HasValue && finalPosition != (long)_state.ContentLength.Value.Value)
            throw ResetAfterInvalidResponse($"Download wrote {finalPosition} bytes but expected {_state.ContentLength.Value.Value}");

        // Completion also applies to an empty representation: 0 / 0 would report 0%.
        context.SetPercent(Size.One, Size.One);

        return Destination;
    }

    private HttpIOException ResetAfterInvalidResponse(string message)
    {
        _state.TotalBytesDownloaded = Size.Zero;
        return new HttpIOException(HttpRequestError.InvalidResponse, message);
    }
    
    private HttpRequestMessage PrepareRequest(out bool isRangeRequest)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Uri);

        // NOTE(erri120): use a normal GET request for the entire file
        if (!_state.AcceptRanges.Value || _state.TotalBytesDownloaded == Size.Zero)
        {
            // NOTE(erri120): Using If-Match to ensure that what we're downloading didn't suddenly change
            if (_state.ETag.HasValue && !_state.ETag.Value.IsWeak)
            {
                // https://developer.mozilla.org/en-US/docs/Web/HTTP/Headers/If-Match
                request.Headers.IfMatch.Add(_state.ETag.Value);
            }

            isRangeRequest = false;
        }
        else
        {
            // NOTE(erri120): Using If-Range for range requests instead of If-Match
            if (_state.ETag.HasValue)
            {
                // https://developer.mozilla.org/en-US/docs/Web/HTTP/Headers/If-Range
                request.Headers.IfRange = new RangeConditionHeaderValue(_state.ETag.Value);
            }

            // A server MAY send a Content-Length header field in a response to a HEAD request
            // https://httpwg.org/specs/rfc9110.html#rfc.section.8.6

            // NOTE(erri120): As such, we might not know the content length, but since we download
            // in serial, we can omit the end position to request all remaining bytes
            var range = new RangeHeaderValue(from: (long)_state.TotalBytesDownloaded.Value, to: null);

            // https://developer.mozilla.org/en-US/docs/Web/HTTP/Headers/Range
            request.Headers.Range = range;
            isRangeRequest = true;
        }

        return request;
    }

    private async ValueTask FetchMetadata(IJobContext context)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, Uri);
        using var response = await Client.SendAsync(
            request: request,
            completionOption: HttpCompletionOption.ResponseHeadersRead,
            cancellationToken: context.CancellationToken
        ).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
        {
            // HEAD is an optional metadata probe for our downloader. A method-specific
            // rejection must not prevent a valid GET, but its error-page headers cannot
            // validate an existing prefix. Restart with no conditional or range headers.
            _state.TotalBytesDownloaded = Size.Zero;
            _state.AcceptRanges = false;
            _state.ETag = Optional<EntityTagHeaderValue>.None;
            _state.ContentLength = Optional<Size>.None;
            return;
        }

        response.EnsureSuccessStatusCode();

        // https://datatracker.ietf.org/doc/html/rfc7233#section-5.1.2
        // "bytes" and "none" are the only range units we care about,
        // "none" meaning the server doesn't support ranges
        var etag = response.Headers.ETag;
        var contentLength = response.Content.Headers.ContentLength;

        // Never attach a new validator to bytes downloaded under an old one. Without a
        // matching strong validator we cannot safely combine separate responses (RFC 9110,
        // sections 13.1.5 and 15.3.7.3), so resume falls back to a complete download.
        if (_state.TotalBytesDownloaded > Size.Zero
            && (!_state.ETag.HasValue || _state.ETag.Value.IsWeak || etag is null || etag.IsWeak
                || !_state.ETag.Value.Equals(etag)
                || (contentLength is not null && (_state.TotalBytesDownloaded.Value >= (ulong)contentLength.Value
                    || (_state.ContentLength.HasValue && _state.ContentLength.Value.Value != (ulong)contentLength.Value)))))
        {
            Logger.LogInformation("Download metadata changed or has no strong validator for `{PageUri}`; restarting from scratch", DownloadPageUri);
            _state.TotalBytesDownloaded = Size.Zero;
        }

        _state.AcceptRanges = response.Headers.AcceptRanges.Contains("bytes");
        _state.ETag = etag;
        _state.ContentLength = contentLength is not null ? Size.FromLong(contentLength.Value) : Optional<Size>.None;
    }

    private static ResiliencePipeline<AbsolutePath> BuildResiliencePipeline()
    {
        ImmutableArray<Type> networkExceptions =
        [
            typeof(HttpIOException),
            typeof(HttpRequestException),
            typeof(SocketException),
        ];

        var pipeline = new ResiliencePipelineBuilder<AbsolutePath>()
            .AddRetry(new RetryStrategyOptions<AbsolutePath>
            {
                ShouldHandle = args => ValueTask.FromResult(args.Outcome.Exception is not null && networkExceptions.Contains(args.Outcome.Exception.GetType())),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(3),
            })
            .Build();

        return pipeline;
    }
}

/// <summary>
/// Public interface for external access to download information
/// </summary>
[PublicAPI]
public interface IHttpDownloadState : IPublicJobStateData, INotifyPropertyChanged
{
    /// <summary>
    /// Content length from HTTP headers
    /// </summary>
    Optional<Size> ContentLength { get; }
    
    /// <summary>
    /// Total bytes downloaded so far
    /// </summary>
    Size TotalBytesDownloaded { get; }
}

/// <summary>
/// Internal mutable state that persists across pause/resume cycles
/// </summary>
internal sealed class HttpDownloadState : ReactiveObject, IHttpDownloadState
{
    [Reactive] public Optional<Size> ContentLength { get; set; }
    [Reactive] public Size TotalBytesDownloaded { get; set; }
    public Optional<EntityTagHeaderValue> ETag { get; set; }
    public Optional<bool> AcceptRanges { get; set; }
}
