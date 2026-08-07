using ImageGen.Generation;

namespace ImageGen.Benchmark;

/// <summary>Parameters controlling a benchmark run.</summary>
public sealed record BenchmarkOptions
{
    /// <summary>Model presets to benchmark.</summary>
    public required IReadOnlyList<ModelPreset> Presets { get; init; }

    /// <summary>Prompt used for every generation.</summary>
    public required string Prompt { get; init; }

    /// <summary>Random seed used for every run.</summary>
    public long Seed { get; init; } = 42;

    /// <summary>Number of timed runs per preset.</summary>
    public int Runs { get; init; } = 3;

    /// <summary>Number of warm-up runs (excluded from timings).</summary>
    public int Warmup { get; init; } = 1;

    /// <summary>Optional thread count override for generation.</summary>
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
    /// <summary>Total seconds spent loading and sampling.</summary>
    public double TotalSeconds => LoadSeconds + GenerationSeconds;
}

/// <summary>Benchmark results for one preset (or the reason it was skipped).</summary>
public sealed record PresetBenchmark
{
    /// <summary>The preset that was benchmarked.</summary>
    public required ModelPreset Preset { get; init; }

    /// <summary>Number of sampling steps used.</summary>
    public required int Steps { get; init; }

    /// <summary>Image width used.</summary>
    public required int Width { get; init; }

    /// <summary>Image height used.</summary>
    public required int Height { get; init; }

    /// <summary>Timed runs (warm-up excluded). Empty when <see cref="SkipReason"/> is set.</summary>
    public IReadOnlyList<RunTiming> Runs { get; init; } = [];

    /// <summary>Non-null when the preset was skipped (e.g. model file missing).</summary>
    public string? SkipReason { get; init; }

    /// <summary>True when the preset was skipped.</summary>
    public bool Skipped => SkipReason is not null;
}

/// <summary>A complete benchmark report: environment metadata plus per-preset results.</summary>
public sealed record BenchmarkReport
{
    /// <summary>When the benchmark run started.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Name of the machine that ran the benchmark.</summary>
    public required string MachineName { get; init; }

    /// <summary>Operating system description.</summary>
    public required string OSDescription { get; init; }

    /// <summary>Number of logical CPU cores.</summary>
    public required int LogicalCores { get; init; }

    /// <summary>Number of physical CPU cores.</summary>
    public required int PhysicalCores { get; init; }

    /// <summary>Number of threads used for generation.</summary>
    public required int ThreadsUsed { get; init; }

    /// <summary>Native system information string.</summary>
    public required string NativeSystemInfo { get; init; }

    /// <summary>Number of timed runs per preset.</summary>
    public required int Runs { get; init; }

    /// <summary>Number of warm-up runs excluded from timings.</summary>
    public required int Warmup { get; init; }

    /// <summary>Prompt used for every generation.</summary>
    public required string Prompt { get; init; }

    /// <summary>Random seed used for every run.</summary>
    public required long Seed { get; init; }

    /// <summary>Per-preset benchmark results.</summary>
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
