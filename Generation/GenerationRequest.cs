namespace ImageGen.Generation;

/// <summary>A single text-to-image request: a preset plus optional overrides.</summary>
public sealed record GenerationRequest
{
    public required ModelPreset Preset { get; init; }
    public required string Prompt { get; init; }
    public string? NegativePrompt { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? Steps { get; init; }
    public float? Cfg { get; init; }
    public long Seed { get; init; } = 42;
    public int? Threads { get; init; }
    public string? OutputPath { get; init; }

    // Effective values after folding overrides onto the preset defaults.
    public int EffectiveWidth => Width ?? Preset.Width;
    public int EffectiveHeight => Height ?? Preset.Height;
    public int EffectiveSteps => Steps ?? Preset.Steps;
    public float EffectiveCfg => Cfg ?? Preset.Cfg;
    public int EffectiveThreads => Threads ?? Environment.ProcessorCount;
    public string EffectiveNegativePrompt => NegativePrompt ?? Preset.NegativePrompt ?? "";

    /// <summary>Explicit <see cref="OutputPath"/> if given, otherwise a timestamped
    /// default under "out/" — e.g. <c>out/sd15-20260806-144337-seed42.png</c> — so
    /// successive generations don't overwrite each other.</summary>
    public string ResolveOutputPath()
    {
        if (OutputPath is { Length: > 0 } path)
            return path;

        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        return Path.Combine("out", $"{Preset.Key}-{timestamp}-seed{Seed}.png");
    }
}
