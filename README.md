# ImageGen

Minimal local text-to-image console app. Runs Stable Diffusion entirely on your
machine (CPU or GPU) via [StableDiffusion.NET](https://github.com/DarthAffe/StableDiffusion.NET),
a .NET wrapper over [stable-diffusion.cpp](https://github.com/leejet/stable-diffusion.cpp).
No Python runtime required.

Switches between three model presets: **SD 1.5**, **SD Turbo**, and **SDXL Turbo**.

Built on the .NET Generic Host: `Microsoft.Extensions.*` for DI and configuration,
[Serilog](https://serilog.net/) for console + rolling-file logging,
[Spectre.Console](https://spectreconsole.net/) for console output, and
[System.CommandLine](https://learn.microsoft.com/dotnet/standard/commandline/) for
argument parsing.

Both downloads and image generation run through hosted `BackgroundService`
workers fed by `Channel<T>` queues. Commands enqueue a job and await its
completion while a live Spectre.Console progress bar tracks it — downloads report
bytes, generation reports denoising steps (via stable-diffusion.cpp's progress
event). Downloads use a configurable worker pool; generation uses a single worker
since it is CPU/GPU-bound and holds the model in memory.

## Requirements

- .NET 10 SDK
- A native backend package (already referenced): `StableDiffusion.NET.Backend.Cpu`.
  For NVIDIA GPUs, swap it for `StableDiffusion.NET.Backend.Cuda` in the `.csproj`.
- Model weight files (see below) — not committed; they're large.

## Models

The quickest way to get the weights is `dotnet run -- download --all` (see
[Downloading models](#downloading-models)), which fetches every preset into the
models directory under the filenames below. To do it manually, place the weight
files in `./models` (or set `MODELS_DIR` to another folder); single-file
`.safetensors` checkpoints work directly.

| Preset | Expected filename | Source |
|---|---|---|
| `sd15` | `sd-v1-5.safetensors` | [Hugging Face: stable-diffusion-v1-5-archive](https://huggingface.co/Comfy-Org/stable-diffusion-v1-5-archive) |
| `sdturbo` | `sd-turbo.safetensors` | [Hugging Face: stabilityai/sd-turbo](https://huggingface.co/stabilityai/sd-turbo) |
| `sdxlturbo` | `sdxl-turbo.safetensors` | [Hugging Face: stabilityai/sdxl-turbo](https://huggingface.co/stabilityai/sdxl-turbo) |

For SDXL Turbo, a fixed fp16 VAE (`sdxl-vae-fp16-fix.safetensors`, from
[madebyollin/sdxl-vae-fp16-fix](https://huggingface.co/madebyollin/sdxl-vae-fp16-fix))
is picked up automatically and avoids occasional black/NaN output. The
`download` command pulls it alongside the `sdxlturbo` model.

Filenames and source URLs are defined in `Presets.All` in
[ModelPreset.cs](ModelPreset.cs) — adjust there if yours differ (e.g. a
quantized `.gguf`).

## Usage

```bash
# List presets and their defaults
dotnet run -- list

# Generate with a preset's defaults
dotnet run -- generate sdturbo "a red fox in a snowy forest"

# Override defaults
dotnet run -- generate sd15 "a lighthouse at dusk" --seed 7 --steps 25 --cfg 7.5 --size 512x768
dotnet run -- generate sdxlturbo "studio portrait of a robot" --out robot.png

# Help for any command
dotnet run -- generate --help
```

When `--out` is omitted, output PNGs are written to a timestamped file under
`out/` — e.g. `out/sd15-20260806-145000-seed42.png` — so successive runs don't
overwrite each other.

### Watching an image form (preview frames)

`--preview <N>` saves an intermediate image every N sampling steps so you can
watch the picture emerge from noise (flip through the frames like a flipbook):

```bash
dotnet run -- generate sd15 "a red fox in a snowy forest" --preview 4
```

Frames land in a `<output>.previews/` directory next to the final image, named
`step-0001.png`, `step-0004.png`, … (the first and last steps are always kept).
`--preview-mode` picks the decoder: `tae` (fast, default), `vae` (accurate but
slow), or `proj` (cheapest). Previews add real cost on CPU — a frame is decoded
at every step internally — so use them for exploration, not timed runs.

### Downloading models

The `download` command fetches files into the models directory via a background
download queue, with a live Spectre.Console progress bar per file. Downloads are
**resumable**: an interrupted transfer leaves a `.part` file and continues from
where it left off on the next run (via HTTP range requests). Files already
present are skipped.

The easiest way is to let a preset supply the URL — no need to look one up:

```bash
# Download every model preset (sd15 + sdturbo + sdxlturbo, plus the SDXL VAE)
dotnet run -- download --all

# Download specific presets by name (repeatable; -p is the short form)
dotnet run -- download --preset sdturbo
dotnet run -- download -p sd15 -p sdxlturbo
```

Preset files are saved under the local filenames `generate` expects (see the
[Models](#models) table), regardless of the source URL's name. You can still pass
raw URLs, and mix them with presets:

```bash
# Raw URLs (space-separated)
dotnet run -- download "https://example.com/some-model.safetensors"

# Override the destination directory
dotnet run -- download --all --dir /data/models
```

Downloads run concurrently up to `ImageGen:DownloadWorkerCount` (default 2); the
rest wait in the queue.

### Options

| Flag | Meaning |
|---|---|
| `--out <file>` | Output PNG path |
| `--seed <n>` | RNG seed (default 42; `-1` = random) |
| `--steps <n>` | Sampling steps |
| `--cfg <f>` | Guidance scale |
| `--size <w>x<h>` | Dimensions, e.g. `512x512` |
| `--negative "<p>"` | Negative prompt |
| `--threads <n>` | CPU thread count (default: all cores) |

### Parameters explained

**Steps** (`--steps`) — the number of denoising iterations the sampler runs to
turn random noise into the final image. More steps generally means more detail
and coherence, but linearly more compute (and time). There are diminishing
returns: quality plateaus and can even degrade past a model's sweet spot.

- **SD 1.5**: ~20–30 is the usual range; 20 is a good default.
- **SD Turbo / SDXL Turbo**: these are *distilled* for few-step sampling — 1–4
  steps. Adding more steps does **not** improve them and often makes output worse.

**CFG** (`--cfg`, Classifier-Free Guidance scale) — how strongly the image is
pushed to match your prompt. Low values give the model more creative freedom but
may drift from the prompt; high values follow the prompt more literally but can
oversaturate, harden edges, or produce artifacts.

- **SD 1.5**: ~7–8 is typical (default 7).
- **SD Turbo / SDXL Turbo**: the distillation process assumes guidance is
  effectively **off**, so use **CFG ≈ 1.0**. Higher values break these models.

**Size** (`--size <W>x<H>`) — output dimensions in pixels. Each model was trained
at a specific native resolution and works best at (or near) it. Straying far from
native — especially *below* it — commonly causes repetition, cropping, duplicated
subjects, or mushy detail.

| Preset | Native size | Recommended range | Constraints |
|---|---|---|---|
| `sd15` | **512×512** | 512×512 to ~768×768 | Each dimension must be a multiple of **64**. Best on square or mildly rectangular sizes; going much past 768 in both axes invites duplicated subjects. |
| `sdturbo` | **512×512** | Stay at/near 512×512 | Multiple of **64**. Distilled from SD 1.5; the few-step sampling is least forgiving of off-native sizes. |
| `sdxlturbo` | **1024×1024** | ~768×768 to 1024×1024 | Multiple of **64**. Inherits SDXL's higher native resolution; avoid dropping to 512 (SDXL degrades badly at small sizes). |

The multiple-of-64 rule comes from the VAE's 8× downsampling combined with the
UNet's internal factor-of-8 structure; non-multiples are rejected or silently
rounded. When in doubt, leave `--size` off and use the preset's native default.

**Negative prompt** (`--negative` / `-n`) — a description of what you *don't* want
in the image. The model steers away from these concepts, the mirror image of how
the main prompt steers toward them. It's useful for suppressing common defects
(`blurry, low quality, watermark, extra fingers`) or unwanted content/styles. The
strength of this steering is governed by CFG — at **CFG ≈ 1.0** (the Turbo presets)
guidance is effectively off, so **the negative prompt has little to no effect**;
it only meaningfully applies to SD 1.5. `sd15` supplies a default negative prompt
(`blurry, low quality, distorted`); passing `--negative` replaces it.

## Configuration & logging

- `appsettings.yaml` holds the models directory, download worker count, and the
  Serilog config (console template, rolling-file path/retention). Copied to the
  output directory on build. YAML support comes from the
  [NetEscapades.Configuration.Yaml](https://github.com/andrewlock/NetEscapades.Configuration)
  configuration provider.
  - `ImageGen:ModelsDir` — where model files live (default `models`).
  - `ImageGen:DownloadWorkerCount` — concurrent downloads (default `2`, min `1`).
- Config can be overridden by environment variables prefixed `IMAGEGEN_`
  (e.g. `IMAGEGEN_ImageGen__ModelsDir=/data/models`,
  `IMAGEGEN_ImageGen__DownloadWorkerCount=4`). `MODELS_DIR` is also honored.
- Logs are written to `logs/imagegen-<date>.log`, rolling daily, 14 files retained.

## Notes

- **Turbo models** are distilled for few-step sampling: 1–4 steps at CFG ≈ 1.0.
  Full SD 1.5 wants ~20 steps at CFG ≈ 7. The presets encode this.
- PNG encoding is done by a small dependency-free encoder ([Png.cs](Png.cs)) to
  avoid ImageSharp 4.x's commercial license.
- First CPU run is slow (model load + generation). SD Turbo at 512×512 / 2 steps
  is the fastest to iterate with.
