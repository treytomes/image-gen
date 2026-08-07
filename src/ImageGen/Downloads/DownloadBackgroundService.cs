using ImageGen.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ImageGen.Downloads;

/// <summary>
/// Drains the download queue with a configurable pool of workers so multiple
/// downloads can proceed concurrently while the rest wait.
/// </summary>
public sealed class DownloadBackgroundService(
    DownloadQueue queue,
    FileDownloader downloader,
    IOptions<ImageGenOptions> options,
    ILogger<DownloadBackgroundService> logger)
    : QueueWorkerService<DownloadRequest, DownloadProgress, string>(queue, logger)
{
    /// <inheritdoc/>
    protected override string ServiceName => "Download service";

    /// <inheritdoc/>
    protected override int WorkerCount => options.Value.DownloadWorkerCount;

    /// <inheritdoc/>
    protected override string Describe(WorkJob<DownloadRequest, DownloadProgress, string> job) =>
        job.Request.EffectiveDisplayName;

    /// <inheritdoc/>
    protected override Task<string> ExecuteJobAsync(
        WorkJob<DownloadRequest, DownloadProgress, string> job, CancellationToken cancellationToken) =>
        downloader.DownloadAsync(job.Request, job.Progress, cancellationToken);
}
