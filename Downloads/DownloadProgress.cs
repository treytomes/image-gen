using ImageGen.Infrastructure;

namespace ImageGen.Downloads;

/// <summary>A point-in-time snapshot of a download's progress.</summary>
/// <param name="BytesDownloaded">Total bytes on disk so far (including any resumed prefix).</param>
/// <param name="TotalBytes">Total expected size, or null if the server didn't report it.</param>
public readonly record struct DownloadProgress(long BytesDownloaded, long? TotalBytes)
{
    /// <summary>Fraction complete in [0,1], or null when the total size is unknown.</summary>
    public double? Fraction => ProgressFraction.Of(BytesDownloaded, TotalBytes);
}
