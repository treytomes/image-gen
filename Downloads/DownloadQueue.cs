using ImageGen.Infrastructure;

namespace ImageGen.Downloads;

/// <summary>A download job: its completion yields the saved file path.</summary>
public sealed class DownloadQueue()
    : ChannelWorkQueue<DownloadRequest, DownloadProgress, string>(singleReader: false)
{
    // Multiple workers may drain this queue concurrently (singleReader: false).
}
