namespace ImageGen.Downloads;

/// <summary>A queued download: where to fetch from and where it lands on disk.</summary>
public sealed record DownloadRequest
{
    /// <summary>The URL to download from.</summary>
    public required Uri Url { get; init; }

    /// <summary>Absolute destination path for the completed file.</summary>
    public required string DestinationPath { get; init; }

    /// <summary>Human-friendly label for progress display (defaults to the file name).</summary>
    public string DisplayName { get; init; } = "";

    /// <summary><see cref="DisplayName"/> if set, otherwise the destination file name.</summary>
    public string EffectiveDisplayName =>
        string.IsNullOrWhiteSpace(DisplayName) ? Path.GetFileName(DestinationPath) : DisplayName;
}
