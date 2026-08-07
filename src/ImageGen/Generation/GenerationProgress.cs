using ImageGen.Infrastructure;

namespace ImageGen.Generation;

/// <summary>Progress for a generation job, expressed in denoising steps.</summary>
/// <param name="Phase">Current phase (loading the model, or sampling).</param>
/// <param name="Step">Completed sampling steps (0 while loading).</param>
/// <param name="TotalSteps">Total sampling steps for this request.</param>
public readonly record struct GenerationProgress(GenerationPhase Phase, int Step, int TotalSteps)
{
    /// <summary>Fraction of sampling complete in [0,1], or null when total is unknown.</summary>
    public double? Fraction => ProgressFraction.Of(Step, TotalSteps);

    /// <summary>Progress for the model-loading phase (0 of <paramref name="totalSteps"/>).</summary>
    public static GenerationProgress Loading(int totalSteps) =>
        new(GenerationPhase.LoadingModel, 0, totalSteps);

    /// <summary>Progress for the sampling phase at the given step.</summary>
    public static GenerationProgress Sampling(int step, int totalSteps) =>
        new(GenerationPhase.Sampling, step, totalSteps);
}

/// <summary>The phase a generation job is in.</summary>
public enum GenerationPhase
{
    /// <summary>The model is loading; sampling has not started.</summary>
    LoadingModel,

    /// <summary>The sampler is denoising steps.</summary>
    Sampling,
}
