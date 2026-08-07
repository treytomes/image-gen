using System.Runtime.InteropServices;
using ImageGen.Generation;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using StableDiffusion.NET;

namespace ImageGen.Benchmark;

/// <summary>
/// Drives repeated generations through the generation queue and aggregates timings
/// into a <see cref="BenchmarkReport"/>. Warm-up runs are executed but excluded from
/// the reported statistics. Progress is shown via Spectre.Console.
/// </summary>
public sealed class BenchmarkRunner(GenerationQueue queue, ILogger<BenchmarkRunner> logger)
{
    /// <summary>Runs the benchmark for <paramref name="options"/> and returns the
    /// collected report.</summary>
    public async Task<BenchmarkReport> RunAsync(BenchmarkOptions options, CancellationToken cancellationToken)
    {
        var threadsUsed = options.Threads ?? Environment.ProcessorCount;
        var results = new List<PresetBenchmark>(options.Presets.Count);

        var outDir = Path.Combine(Path.GetTempPath(), "imagegen-bench");
        Directory.CreateDirectory(outDir);

        // Capture native system info up front. These native calls are unstable once
        // a model has been loaded and disposed, so we snapshot before any run.
        var physicalCores = SafePhysicalCores();
        var nativeInfo = SafeSystemInfo();

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                foreach (var preset in options.Presets)
                {
                    var total = options.Warmup + options.Runs;
                    var task = ctx.AddTask($"{preset.Name}", autoStart: true, maxValue: total);

                    var benchmark = await BenchmarkPresetAsync(preset, options, threadsUsed, outDir, task, cancellationToken);
                    results.Add(benchmark);
                }
            });

        logger.LogInformation("Benchmark runs complete; writing report.");

        return new BenchmarkReport
        {
            StartedAt = DateTimeOffset.Now,
            MachineName = Environment.MachineName,
            OSDescription = RuntimeInformation.OSDescription,
            LogicalCores = Environment.ProcessorCount,
            PhysicalCores = physicalCores,
            ThreadsUsed = threadsUsed,
            NativeSystemInfo = nativeInfo,
            Runs = options.Runs,
            Warmup = options.Warmup,
            Prompt = options.Prompt,
            Seed = options.Seed,
            Results = results,
        };
    }

    private async Task<PresetBenchmark> BenchmarkPresetAsync(
        ModelPreset preset,
        BenchmarkOptions options,
        int threadsUsed,
        string outDir,
        ProgressTask task,
        CancellationToken cancellationToken)
    {
        var timings = new List<RunTiming>(options.Runs);
        var total = options.Warmup + options.Runs;

        for (var i = 0; i < total; i++)
        {
            var isWarmup = i < options.Warmup;
            task.Description = isWarmup
                ? $"{preset.Name} [grey](warmup)[/]"
                : $"{preset.Name} [grey](run {i - options.Warmup + 1}/{options.Runs})[/]";

            var request = new GenerationRequest
            {
                Preset = preset,
                Prompt = options.Prompt,
                Seed = options.Seed,
                Threads = options.Threads,
                Width = options.Width,
                Height = options.Height,
                OutputPath = Path.Combine(outDir, $"{preset.Key}-{i}.png"),
            };

            try
            {
                var job = queue.Enqueue(request);
                var result = await job.Completion;
                if (!isWarmup)
                    timings.Add(new RunTiming(result.LoadSeconds, result.GenerationSeconds));
            }
            catch (FileNotFoundException ex)
            {
                logger.LogWarning("Skipping {Preset}: {Message}", preset.Key, ex.Message);
                task.Description = $"{preset.Name} [red](skipped)[/]";
                task.StopTask();
                return new PresetBenchmark
                {
                    Preset = preset,
                    Steps = request.EffectiveSteps,
                    Width = request.EffectiveWidth,
                    Height = request.EffectiveHeight,
                    SkipReason = "model file not found",
                };
            }

            task.Increment(1);
        }

        var sample = new GenerationRequest
        {
            Preset = preset,
            Prompt = options.Prompt,
            Width = options.Width,
            Height = options.Height,
        };
        return new PresetBenchmark
        {
            Preset = preset,
            Steps = sample.EffectiveSteps,
            Width = sample.EffectiveWidth,
            Height = sample.EffectiveHeight,
            Runs = timings,
        };
    }

    private static int SafePhysicalCores()
    {
        try { return StableDiffusionCpp.GetNumPhysicalCores(); }
        catch { return 0; }
    }

    // Note: StableDiffusionCpp.GetSystemInfo() hard-crashes the process in this
    // build (native abort, not a catchable exception), so we don't call it. The
    // .NET runtime's architecture string is a safe substitute.
    private static string SafeSystemInfo() => RuntimeInformation.ProcessArchitecture.ToString();
}
