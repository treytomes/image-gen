using ImageGen.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ImageGen.Generation;

/// <summary>
/// Drains the generation queue. A single worker (the default) processes jobs
/// sequentially: generation is CPU/GPU-bound and holds the model in memory, so
/// there's nothing to gain from running requests concurrently.
/// </summary>
public sealed class GenerationBackgroundService(
    GenerationQueue queue,
    ImageGenerationService engine,
    ILogger<GenerationBackgroundService> logger)
    : QueueWorkerService<GenerationRequest, GenerationProgress, GenerationResult>(queue, logger)
{
    /// <inheritdoc/>
    protected override string ServiceName => "Generation service";

    /// <inheritdoc/>
    protected override string Describe(WorkJob<GenerationRequest, GenerationProgress, GenerationResult> job) =>
        job.Request.Preset.Key;

    /// <inheritdoc/>
    protected override Task<GenerationResult> ExecuteJobAsync(
        WorkJob<GenerationRequest, GenerationProgress, GenerationResult> job, CancellationToken cancellationToken)
    {
        // The engine is synchronous (native, CPU/GPU-bound); run it inline on this
        // worker so only one generation happens at a time.
        var result = engine.Generate(job.Request, job.Progress, cancellationToken);
        return Task.FromResult(result);
    }
}
