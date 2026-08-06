using StableDiffusion.NET;

namespace ImageGen;

/// <summary>Generation defaults for one model family.</summary>
public sealed record ModelPreset(
    string Key,
    string Name,
    string FileName,
    int Width,
    int Height,
    int Steps,
    float Cfg,
    Sampler Sampler,
    string DownloadUrl,
    string? NegativePrompt = null,
    string? VaeFileName = null,
    string? VaeDownloadUrl = null);

/// <summary>The built-in model presets, keyed by CLI name.</summary>
public static class Presets
{
    // Turbo variants are distilled for few-step sampling and expect CFG ~1.0
    // (classifier-free guidance effectively off). Full SD 1.5 wants CFG ~7.
    public static readonly IReadOnlyList<ModelPreset> All =
    [
        new(
            Key: "sd15",
            Name: "Stable Diffusion 1.5",
            FileName: "sd-v1-5.safetensors",
            Width: 512, Height: 512,
            Steps: 20, Cfg: 7.0f,
            Sampler: Sampler.Euler_A,
            DownloadUrl: "https://huggingface.co/Comfy-Org/stable-diffusion-v1-5-archive/resolve/main/v1-5-pruned-emaonly-fp16.safetensors",
            NegativePrompt: "blurry, low quality, distorted"),

        new(
            Key: "sdturbo",
            Name: "SD Turbo",
            FileName: "sd-turbo.safetensors",
            Width: 512, Height: 512,
            Steps: 2, Cfg: 1.0f,
            Sampler: Sampler.Euler,
            DownloadUrl: "https://huggingface.co/stabilityai/sd-turbo/resolve/main/sd_turbo.safetensors"),

        new(
            Key: "sdxlturbo",
            Name: "SDXL Turbo",
            FileName: "sdxl-turbo.safetensors",
            Width: 1024, Height: 1024,
            Steps: 4, Cfg: 1.0f,
            Sampler: Sampler.Euler,
            DownloadUrl: "https://huggingface.co/stabilityai/sdxl-turbo/resolve/main/sd_xl_turbo_1.0_fp16.safetensors",
            // SDXL's original fp16 VAE is unstable; drop a fixed VAE here if
            // you see black/NaN output. Optional otherwise.
            VaeFileName: "sdxl-vae-fp16-fix.safetensors",
            VaeDownloadUrl: "https://huggingface.co/madebyollin/sdxl-vae-fp16-fix/resolve/main/sdxl_vae.safetensors"),
    ];

    public static readonly IReadOnlyList<string> Keys = All.Select(p => p.Key).ToArray();

    public static ModelPreset? Find(string key) =>
        All.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
}
