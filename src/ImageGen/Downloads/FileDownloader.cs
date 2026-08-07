using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace ImageGen.Downloads;

/// <summary>
/// Downloads a file over HTTP with resume support. Data is streamed to a
/// "<c>&lt;dest&gt;.part</c>" file and atomically moved into place on completion;
/// an interrupted download resumes from the partial file via an HTTP Range request.
/// </summary>
public sealed class FileDownloader(HttpClient httpClient, ILogger<FileDownloader> logger)
{
    private const int BufferSize = 1 << 20; // 1 MiB

    /// <summary>Downloads <paramref name="request"/>, reporting to <paramref name="progress"/>.
    /// Returns the final destination path.</summary>
    public async Task<string> DownloadAsync(
        DownloadRequest request,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var destination = request.DestinationPath;
        var partPath = destination + ".part";

        var dir = Path.GetDirectoryName(Path.GetFullPath(destination));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (File.Exists(destination))
        {
            logger.LogInformation("{Name} already present, skipping download.", request.EffectiveDisplayName);
            progress?.Report(new DownloadProgress(new FileInfo(destination).Length, new FileInfo(destination).Length));
            return destination;
        }

        var existingBytes = File.Exists(partPath) ? new FileInfo(partPath).Length : 0L;

        using var httpReq = new HttpRequestMessage(HttpMethod.Get, request.Url);
        if (existingBytes > 0)
        {
            httpReq.Headers.Range = new RangeHeaderValue(existingBytes, null);
            logger.LogInformation(
                "Resuming {Name} from {Bytes:N0} bytes.", request.EffectiveDisplayName, existingBytes);
        }

        using var response = await httpClient
            .SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        // If we asked for a range but the server sent 200 (full body), it ignored
        // the request — discard the partial file and start over.
        var resuming = existingBytes > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (existingBytes > 0 && !resuming)
        {
            logger.LogWarning(
                "Server did not honor resume for {Name}; restarting from scratch.", request.EffectiveDisplayName);
            existingBytes = 0;
        }

        response.EnsureSuccessStatusCode();

        // Total size = bytes already on disk (when resuming) + remaining content length.
        var remaining = response.Content.Headers.ContentLength;
        long? totalBytes = remaining is { } r ? existingBytes + r : null;

        var fileMode = resuming ? FileMode.Append : FileMode.Create;
        await using (var fileStream = new FileStream(
                         partPath, fileMode, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
        await using (var httpStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            var buffer = new byte[BufferSize];
            var totalRead = existingBytes;
            progress?.Report(new DownloadProgress(totalRead, totalBytes));

            int read;
            while ((read = await httpStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                totalRead += read;
                progress?.Report(new DownloadProgress(totalRead, totalBytes));
            }
        }

        // Atomic-ish publish: move the completed .part file onto the destination.
        File.Move(partPath, destination, overwrite: true);
        logger.LogInformation("Completed {Name} -> {Path}", request.EffectiveDisplayName, destination);
        return destination;
    }
}
