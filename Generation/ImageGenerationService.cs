using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StableDiffusion.NET;

namespace ImageGen.Generation;

/// <summary>
/// The generation engine: loads a diffusion model and produces an image for a
/// request, reporting step progress through an <see cref="IProgress{T}"/> hook.
/// This type does no console I/O — presentation is the caller's concern — so it
/// can run inside the background service.
/// </summary>
public sealed class ImageGenerationService(
    IOptions<ImageGenOptions> options,
    ILogger<ImageGenerationService> logger)
{
    private readonly ImageGenOptions _options = options.Value;

    /// <summary>Resolves the models directory: MODELS_DIR env var wins, else config.</summary>
    private string ModelsDir =>
        Environment.GetEnvironmentVariable("MODELS_DIR") is { Length: > 0 } dir
            ? dir
            : _options.ModelsDir;

    /// <summary>Runs one generation to completion, saving a PNG.</summary>
    /// <exception cref="FileNotFoundException">The preset's model file is missing.</exception>
    /// <exception cref="InvalidOperationException">The model returned no image.</exception>
    public GenerationResult Generate(
        GenerationRequest request,
        IProgress<GenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var modelPath = Path.Combine(ModelsDir, request.Preset.FileName);
        if (!File.Exists(modelPath))
        {
            logger.LogError("Model file not found for preset {Preset}: {Path}", request.Preset.Key, modelPath);
            throw new FileNotFoundException(
                $"Model file not found for preset '{request.Preset.Key}'. " +
                "Run 'download --preset <name>' or set MODELS_DIR.", modelPath);
        }

        var modelParam = DiffusionModelParameter.Create();
        modelParam.ModelPath = modelPath;
        modelParam.ThreadCount = request.EffectiveThreads;
        modelParam.VaeDecodeOnly = true; // text-to-image only; saves memory

        var vaePath = request.Preset.VaeFileName is { } vae ? Path.Combine(ModelsDir, vae) : null;
        if (vaePath is not null && File.Exists(vaePath))
            modelParam.VaePath = vaePath;

        logger.LogInformation(
            "Generating with {Preset} ({Width}x{Height}, {Steps} steps, cfg {Cfg}, seed {Seed})",
            request.Preset.Key, request.EffectiveWidth, request.EffectiveHeight,
            request.EffectiveSteps, request.EffectiveCfg, request.Seed);

        var totalSteps = request.EffectiveSteps;
        progress?.Report(GenerationProgress.Loading(totalSteps));

        // The native library also emits progress events while loading the model
        // (its own internal iterations). Ignore those so the bar shows "Loading"
        // until we start sampling; this flips true once the model is constructed.
        var sampling = false;

        // stable-diffusion.cpp reports sampling progress via a static event. Its
        // Step/Steps count the model's internal iterations, not our SampleSteps,
        // so report the fraction against the event's own total.
        void OnProgress(object? _, StableDiffusionProgressEventArgs e)
        {
            if (sampling)
                progress?.Report(GenerationProgress.Sampling(e.Step, e.Steps > 0 ? e.Steps : totalSteps));
        }

        // The managed Log/Progress/Preview events are dormant until the native
        // callbacks are registered. This is a one-time, idempotent global setup.
        StableDiffusionCpp.InitializeEvents();

        var loadStart = Stopwatch.GetTimestamp();
        StableDiffusionCpp.Progress += OnProgress;
        try
        {
            using var model = new DiffusionModel(modelParam);
            var loadSeconds = Stopwatch.GetElapsedTime(loadStart).TotalSeconds;
            logger.LogInformation("Model loaded in {Seconds:F1}s", loadSeconds);
            sampling = true;

            cancellationToken.ThrowIfCancellationRequested();

            var genStart = Stopwatch.GetTimestamp();
            var image = model.GenerateImage(BuildParameter(request));
            var genSeconds = Stopwatch.GetElapsedTime(genStart).TotalSeconds;

            if (image is null)
            {
                logger.LogError("Generation returned no image for preset {Preset}", request.Preset.Key);
                throw new InvalidOperationException("Generation returned no image.");
            }

            // Ensure the bar reads 100% even if the last step event lagged.
            progress?.Report(GenerationProgress.Sampling(totalSteps, totalSteps));

            var outPath = Path.GetFullPath(request.ResolveOutputPath());
            Png.Save(outPath, image.ToRawArray(), image.Width, image.Height);

            logger.LogInformation(
                "Saved {Path} (load {Load:F1}s, gen {Gen:F1}s)", outPath, loadSeconds, genSeconds);
            return new GenerationResult(outPath, loadSeconds, genSeconds);
        }
        finally
        {
            StableDiffusionCpp.Progress -= OnProgress;
        }
    }

    private static ImageGenerationParameter BuildParameter(GenerationRequest request)
    {
        var p = ImageGenerationParameter.TextToImage(request.Prompt);
        p.NegativePrompt = request.EffectiveNegativePrompt;
        p.Width = request.EffectiveWidth;
        p.Height = request.EffectiveHeight;
        p.Seed = request.Seed;
        p.SampleParameter.SampleSteps = request.EffectiveSteps;
        p.SampleParameter.SampleMethod = request.Preset.Sampler;
        p.SampleParameter.Guidance.TxtCfg = request.EffectiveCfg;
        return p;
    }
}
