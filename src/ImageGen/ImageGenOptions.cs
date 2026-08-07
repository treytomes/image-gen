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

    /// <summary>Settings for the interactive refinement loop (<c>generate --repl</c>).</summary>
    public RefinementOptions Refinement { get; set; } = new();
}

/// <summary>
/// Settings for the interactive refinement REPL. Refinement is image-to-image, where
/// strength effectively scales the number of sampling steps that run
/// (<c>effective ≈ strength × steps</c>). With a preset's few-step default (e.g. SD
/// Turbo's 2), strength has almost no resolution and feels like an on/off switch, so
/// the REPL uses its own higher step count instead of the preset default.
/// </summary>
public sealed class RefinementOptions
{
    /// <summary>Sampling steps used for each refinement round, overriding the preset's
    /// default so strength has room to resolve. Higher = finer strength control but
    /// slower per round. Values ≤ 0 fall back to the preset's step count.</summary>
    public int Steps { get; set; } = 20;

    /// <summary>When a round's effective steps (<c>strength × steps</c>) fall below this
    /// value, the REPL warns that strength control is coarse. Set to 0 to disable the
    /// hint.</summary>
    public int CoarseStrengthStepThreshold { get; set; } = 4;
}
