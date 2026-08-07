namespace ImageGen;

/// <summary>Application configuration bound from the "ImageGen" config section.</summary>
public sealed class ImageGenOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "ImageGen";

    /// <summary>Directory holding the model weight files. Relative paths resolve
    /// against the current working directory. Overridden by the MODELS_DIR env var.</summary>
    public string ModelsDir { get; set; } = "models";

    /// <summary>Number of downloads the background service processes concurrently.
    /// Clamped to at least 1.</summary>
    public int DownloadWorkerCount { get; set; } = 2;
}
