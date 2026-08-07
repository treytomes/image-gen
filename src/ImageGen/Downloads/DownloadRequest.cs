namespace ImageGen.Downloads;

/// <summary>A queued download: where to fetch from and where it lands on disk.</summary>
public sealed record DownloadRequest
{
    public required Uri Url { get; init; }

    /// <summary>Absolute destination path for the completed file.</summary>
    public required string DestinationPath { get; init; }

    /// <summary>Human-friendly label for progress display (defaults to the file name).</summary>
    public string DisplayName { get; init; } = "";

    public string EffectiveDisplayName =>
        string.IsNullOrWhiteSpace(DisplayName) ? Path.GetFileName(DestinationPath) : DisplayName;
}
