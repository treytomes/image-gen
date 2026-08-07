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

## Project layout

The solution is [ImageGen.slnx](ImageGen.slnx):

```
src/
  ImageGen/            the console app (host, commands, generation, downloads)
  ImageGen.Imaging/    generic image tools (PNG encode/decode) — pure, unit-tested
tests/
  ImageGen.Imaging.Tests/   xUnit tests for the deterministic imaging code
```

`models/`, `out/`, `logs/`, and `reports/` live at the **repository root**; the app
uses paths relative to the working directory, so run commands from the repo root.

```bash
dotnet build ImageGen.slnx          # build everything
dotnet test ImageGen.slnx           # run the imaging tests
dotnet run --project src/ImageGen -- list   # run the app
```

The usage examples below abbreviate `dotnet run --project src/ImageGen --` to
`dotnet run --` for brevity — run them from the repo root with the full
`--project src/ImageGen` form.

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
| `sdxl` | `sdxl-base.safetensors` | [Hugging Face: stable-diffusion-xl-base-1.0](https://huggingface.co/stabilityai/stable-diffusion-xl-base-1.0) |

`sdxl` is the **full SDXL base model** (not distilled): real classifier-free
guidance and working negative prompts, at the cost of more steps. On a CPU it's a
batch/overnight model (~13+ min/image at its 15-step default). The preset uses the
**AYS** scheduler so lower step counts stay usable; raise `--steps` toward 25–40
for full quality if you have the time.

Both SDXL presets use a fixed fp16 VAE (`sdxl-vae-fp16-fix.safetensors`, from
[madebyollin/sdxl-vae-fp16-fix](https://huggingface.co/madebyollin/sdxl-vae-fp16-fix))
that is picked up automatically and avoids occasional black/NaN output. The
`download` command pulls it alongside the SDXL models.

Filenames and source URLs are defined in `Presets.All` in
[ModelPreset.cs](src/ImageGen/ModelPreset.cs) — adjust there if yours differ (e.g. a
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

# Full SDXL base with a scheduler override and more steps
dotnet run -- generate sdxl "an ornate steampunk airship" --steps 30 --scheduler Karras

# Batch: generate 10 images overnight, incrementing the seed each time
dotnet run -- generate sdxl "a red fox in a snowy forest" --count 10

# Help for any command
dotnet run -- generate --help
```

When `--out` is omitted, output PNGs are written to a timestamped file under
`out/` — e.g. `out/sd15-20260806-145000-seed42.png` — so successive runs don't
overwrite each other.

### Image-to-image (editing an existing image)

`--init <png>` starts generation from an existing image instead of pure noise, so
the prompt reworks *that* picture rather than inventing one from scratch.
`--strength` (0–1, default 0.75) controls how far the result may stray:

```bash
# Subtle restyle — output stays close to the input
dotnet run -- generate sd15 "a red fox in an autumn forest" --init photo.png --strength 0.3

# Loose reinterpretation — keeps only rough structure
dotnet run -- generate sd15 "an oil painting of a fox" --init photo.png --strength 0.8
```

- **Low strength (~0.2–0.4)** — small changes; good for restyling or cleanup.
- **High strength (~0.7–0.9)** — heavy changes; the input only guides composition.
- The input may be any PNG our decoder reads (8-bit RGB/RGBA, non-interlaced).
  **Output size follows the input**, so `--size` is ignored with `--init`.
- Works with any preset. Combine with `--count` for a strength/seed sweep, or
  `--preview` to watch the edit converge.

### Interactive refinement (`--repl`)

`--repl` drops you into a refinement loop after the first image is generated. Each
prompt you enter reworks the *previous* output via img2img, so you can iterate
toward a result conversationally:

```bash
dotnet run -- generate sdturbo "a red fox in a snowy forest" --repl
# ... first image generates, then:
# refine> make the sky more dramatic
#   strength [0.75]: 0.5
# ... refined image generates from the previous one ...
# refine> add falling snow
#   strength [0.5]:            (Enter reuses 0.5)
# refine>                      (empty, "exit", or Ctrl-C to quit)
```

Each round prompts for the text and then a **strength** (0–1); the strength
defaults to the last value used, so pressing Enter reuses it. Every refinement is
saved as its own timestamped file, and the next round builds on the most recent
one. Leave with an empty prompt, `exit`/`quit`, or **Ctrl-C**. Requires an
interactive terminal and a single image (ignored with `--count > 1`).

**Strength resolution and step count.** In image-to-image, strength effectively
scales how many sampling steps run (`effective ≈ strength × steps`). With a
preset's few-step default (SD Turbo uses 2), strength collapses to only ~2
outcomes and feels like an on/off switch. The REPL therefore uses its own higher
step count — configurable via `ImageGen:Refinement:Steps` (default 20) in
[appsettings.yaml](src/ImageGen/appsettings.yaml) — so strength blends smoothly.
If a round's effective steps fall below `ImageGen:Refinement:CoarseStrengthStepThreshold`
(default 4), the REPL notes that strength control is coarse. Raising `Steps` gives
finer control at the cost of longer rounds.

### Batch generation and stopping early

`--count <N>` generates N images in one run, incrementing the seed each time
(`--seed` sets the starting seed). Each image is saved as it completes, so a batch
left running overnight yields results incrementally. `--out` is ignored for
batches (names would collide); timestamped per-seed names are used instead.

**Stopping early — Ctrl-C.** A single image is one uninterruptible native call, so
cancellation can't abort the image currently rendering. Ctrl-C is therefore
two-stage:

- **First Ctrl-C** — the image currently rendering finishes and is saved, but no
  further images in a batch are started. Ideal for "queue a pile, stop once I see
  one I like in the preview frames."
- **Second Ctrl-C** — quit immediately, discarding the in-flight image.

So for a single-image run, one Ctrl-C prints a notice and waits for that image
(press again to quit now); for a batch, one Ctrl-C ends it cleanly after the
current image while keeping everything already saved.

### Watching an image form (preview frames)

`--preview <N>` saves an intermediate image every N sampling steps so you can
watch the picture emerge from noise (flip through the frames like a flipbook):

```bash
dotnet run -- generate sd15 "a red fox in a snowy forest" --preview 4
```

Frames land in a `<output>.previews/` directory next to the final image, named
`step-0001.png`, `step-0004.png`, … (the first and last steps are always kept).
`--preview-mode` picks the decoder: `tae` (fast, default), `vae` (accurate but
slow), or `proj` (cheapest).

**Why previews are slow.** A diffusion model works in a compressed *latent*
space, not pixels; turning a latent into a viewable image requires a decoder
(neural-network) pass. Normally that decode runs **once**, at the end. With
previews on, the underlying library decodes on **every** step to hand us an
image — that extra per-step decode is the real cost (a 12-step run roughly
doubled in our CPU tests). The `--preview <N>` interval is enforced on our side
to limit how many frames are *written* to disk, but it does not reduce the
per-step decode the library performs, so the time cost stays. Saving each PNG is
cheap by comparison (a few ms). Bottom line: use previews for exploration, not
timed runs, and prefer `tae` over `vae` on CPU.

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
| `--out`, `-o` `<file>` | Output PNG path |
| `--seed <n>` | RNG seed (default 42; `-1` = random) |
| `--steps <n>` | Sampling steps |
| `--cfg <f>` | Guidance scale |
| `--size <w>x<h>` | Dimensions, e.g. `512x512` (ignored with `--init`) |
| `--negative`, `-n` `"<p>"` | Negative prompt |
| `--init`, `-i` `<png>` | Input image for image-to-image (start from it, not noise) |
| `--strength <f>` | Image-to-image strength 0–1 (default 0.75); only with `--init` |
| `--scheduler <name>` | Sampling scheduler (overrides preset; e.g. `AYS`, `Karras`) |
| `--count`, `-c` `<n>` | Generate N images, incrementing the seed |
| `--preview <n>` | Save a preview frame every N steps (0 = off) |
| `--preview-mode <m>` | Preview decoder: `tae` (default), `vae`, `proj` |
| `--repl` | After generating, refine the result interactively (single image only) |
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

## How diffusion works (and how to read the preview frames)

A quick mental model — enough to make the parameters and the artifacts you'll see
make sense.

### The one-paragraph version

A diffusion model is trained by taking real images, progressively adding random
noise until they're pure static, and learning to **reverse** that process one small
step at a time. To generate, it starts from a field of pure noise and, over a
number of **steps**, repeatedly predicts "what did this look like with slightly less
noise?" — nudging the image toward something coherent. Your **prompt** conditions
every prediction; **CFG** controls how hard each prediction is pulled toward the
prompt (see [Parameters explained](#parameters-explained)). It doesn't work on
pixels directly but on a compressed *latent*, which a decoder turns into the final
image — that decode is what makes preview frames costly (see
[preview frames](#watching-an-image-form-preview-frames)).

### Structure is decided early, detail late

The crucial thing to internalize: **global structure is committed in the first
few steps, and later steps only refine it.** From the initial noise the model
quickly roughs out "a mass here, a limb there, horizon about here" — and every
subsequent step treats that layout as the thing to *sharpen*, not to *reconsider*.
This is exactly what the preview frames show: flip through them and you'll see the
composition lock in almost immediately, then sharpen over the remaining steps.

That's why the practical advice is what it is:

- **If the early frames show bad structure, the run is usually doomed.** Extra
  limbs, duplicated subjects, or a wrong composition that appears in the first few
  frames rarely gets fixed later — the model refines the mistake rather than
  removing it. Kill the batch (Ctrl-C) and reseed. A different `--seed` rolls a
  different initial noise field and therefore a different early layout; this is the
  single most effective fix, and it's what `--count` is built for — batch many seeds
  and keep the good ones.
- **More steps ≠ more quality.** Past a model's sweet spot, extra steps do nothing
  useful and can actively degrade the image (colors oversaturating, detail smearing)
  as it drifts into inputs it was never trained on. This is especially sharp for the
  **Turbo** models: they are *distilled* to converge in 1–4 giant steps, so a
  50- or 100-step Turbo run will peak early and then get *worse*. Match steps to the
  model (Turbo: 1–4; SD 1.5: ~20; SDXL base: 25–40).

### Distilled (Turbo) vs. full models, in these terms

- **Turbo** models trade deliberation for speed: they commit to a structure and do
  minimal refinement, so they're fast but **can't course-correct** a bad early
  layout within their tiny step budget (the "extra arms that merge but never
  resolve" effect). CFG is baked to ≈1, so negative prompts barely bite.
- **Full models** (`sd15`, `sdxl`) take many steps with real guidance, giving them
  more chances to renegotiate structure and honor a negative prompt like
  `extra limbs, deformed`. When you need the model to *fix its own mistakes* rather
  than just render fast, reach for `sdxl` at 25–40 steps — not a Turbo preset.

## Configuration & logging

- `appsettings.yaml` holds the models directory, download worker count, and the
  Serilog config (console template, rolling-file path/retention). Copied to the
  output directory on build. YAML support comes from the
  [NetEscapades.Configuration.Yaml](https://github.com/andrewlock/NetEscapades.Configuration)
  configuration provider.
  - `ImageGen:ModelsDir` — where model files live (default `models`).
  - `ImageGen:DownloadWorkerCount` — concurrent downloads (default `2`, min `1`).
  - `ImageGen:Refinement:Steps` — sampling steps per `--repl` refinement round
    (default `20`); higher gives finer strength control, slower rounds.
  - `ImageGen:Refinement:CoarseStrengthStepThreshold` — warn when a round's
    effective steps fall below this (default `4`; `0` disables the hint).
- Config can be overridden by environment variables prefixed `IMAGEGEN_`
  (e.g. `IMAGEGEN_ImageGen__ModelsDir=/data/models`,
  `IMAGEGEN_ImageGen__DownloadWorkerCount=4`). `MODELS_DIR` is also honored.
- Logs are written to `logs/imagegen-<date>.log`, rolling daily, 14 files retained.

## Notes

- **Turbo models** are distilled for few-step sampling: 1–4 steps at CFG ≈ 1.0.
  Full SD 1.5 wants ~20 steps at CFG ≈ 7. The presets encode this.
- PNG encoding and decoding use a small dependency-free codec
  ([Png.cs](src/ImageGen.Imaging/Png.cs), behind `IImageCodec`) to avoid
  ImageSharp 4.x's commercial license.
- First CPU run is slow (model load + generation). SD Turbo at 512×512 / 2 steps
  is the fastest to iterate with.
