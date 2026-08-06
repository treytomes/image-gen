using ImageGen.Infrastructure;

namespace ImageGen.Generation;

/// <summary>A generation job: its completion yields the <see cref="GenerationResult"/>.</summary>
public sealed class GenerationQueue()
    : ChannelWorkQueue<GenerationRequest, GenerationProgress, GenerationResult>(singleReader: true)
{
    // A single worker drains this queue: generation is CPU/GPU-bound, so there's
    // no benefit to running requests concurrently (singleReader: true).
}
