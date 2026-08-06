using ImageGen.Generation;

namespace ImageGen.Benchmark;

/// <summary>Parameters controlling a benchmark run.</summary>
public sealed record BenchmarkOptions
{
    public required IReadOnlyList<ModelPreset> Presets { get; init; }
    public required string Prompt { get; init; }
    public long Seed { get; init; } = 42;
    public int Runs { get; init; } = 3;
    public int Warmup { get; init; } = 1;
    public int? Threads { get; init; }

    /// <summary>Optional common width forced on every preset for a fair comparison.</summary>
    public int? Width { get; init; }

    /// <summary>Optional common height forced on every preset for a fair comparison.</summary>
    public int? Height { get; init; }
}

/// <summary>Timings for a single generation run.</summary>
/// <param name="LoadSeconds">Seconds spent loading the model.</param>
/// <param name="GenerationSeconds">Seconds spent sampling.</param>
public readonly record struct RunTiming(double LoadSeconds, double GenerationSeconds)
{
    public double TotalSeconds => LoadSeconds + GenerationSeconds;
}

/// <summary>Benchmark results for one preset (or the reason it was skipped).</summary>
public sealed record PresetBenchmark
{
    public required ModelPreset Preset { get; init; }
    public required int Steps { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>Timed runs (warm-up excluded). Empty when <see cref="SkipReason"/> is set.</summary>
    public IReadOnlyList<RunTiming> Runs { get; init; } = [];

    /// <summary>Non-null when the preset was skipped (e.g. model file missing).</summary>
    public string? SkipReason { get; init; }

    public bool Skipped => SkipReason is not null;
}

/// <summary>A complete benchmark report: environment metadata plus per-preset results.</summary>
public sealed record BenchmarkReport
{
    public required DateTimeOffset StartedAt { get; init; }
    public required string MachineName { get; init; }
    public required string OSDescription { get; init; }
    public required int LogicalCores { get; init; }
    public required int PhysicalCores { get; init; }
    public required int ThreadsUsed { get; init; }
    public required string NativeSystemInfo { get; init; }
    public required int Runs { get; init; }
    public required int Warmup { get; init; }
    public required string Prompt { get; init; }
    public required long Seed { get; init; }
    public required IReadOnlyList<PresetBenchmark> Results { get; init; }
}

/// <summary>Aggregate statistics over a set of run timings.</summary>
public readonly record struct Stats(double Min, double Median, double Max)
{
    /// <summary>Computes min/median/max of the selected metric across the runs.
    /// Returns null when there are no runs.</summary>
    public static Stats? From(IReadOnlyList<RunTiming> runs, Func<RunTiming, double> selector)
    {
        if (runs.Count == 0)
            return null;

        var values = runs.Select(selector).OrderBy(v => v).ToArray();
        return new Stats(values[0], MedianOf(values), values[^1]);
    }

    private static double MedianOf(double[] sorted)
    {
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
