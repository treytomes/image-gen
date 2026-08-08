# DONE

Completed work, moved out of [TODO.md](TODO.md) to keep it focused on remaining
tasks. Kept for the findings — several encode non-obvious facts about the native
library (StableDiffusion.NET v7 / stable-diffusion.cpp) worth not rediscovering.

---

## Pause-and-inspect via preview frames

`generate --preview <N>` writes intermediate frames to `<output>.previews/`;
`--preview-mode` selects `tae`/`vae`/`proj`. Lets you watch the image form from
noise. See README for usage.

Findings:
- The `Preview` event yields an already-decoded `HPPH.Image<ColorRGB>` (not a raw
  latent) — the exact type `Png.cs` encodes, so frame-writing reused `Png.Save`
  with zero new decode code.
- **`EnablePreview`'s interval is ignored in this build** — the native event fires
  every step regardless of the interval passed. We enforce the interval
  client-side (keep first, last, and every Nth step). Verified `--steps 8
  --preview 3` → frames at steps 1, 3, 6, 8.
- **Previews are costly on CPU**: the native side decodes *every* step even when we
  save only some, so a 12-step sd15 run with per-step TAE previews took ~404s vs.
  the ~273s/20-step baseline. The client-side throttle limits disk output, not the
  decode cost. Prefer `tae` over `vae` on CPU.
- Preview/Progress events are static/global and require
  `StableDiffusionCpp.InitializeEvents()` (called once in the engine); safe because
  the generation queue uses a single worker.

## Batch generation + early stop

`generate --count <N>` runs N images, incrementing the seed each time; each image
is saved as it completes. Ctrl-C stops the batch between images.

Finding — **a single in-flight `GenerateImage` cannot be interrupted**: a runtime
test throwing `OperationCanceledException` from the `Progress` callback showed the
exception is swallowed and generation runs to completion. So mid-image abort is
impossible from managed code. Ctrl-C handling (in `Program.cs`) is therefore
**two-stage**, and we own the signal directly rather than via System.CommandLine:
- We set `InvocationConfiguration.ProcessTerminationTimeout = null`, which (verified
  by reflection: the `ProcessTerminationHandler` is only constructed when the timeout
  has a value) disables SCL's own Ctrl-C handling so it can't race ours.
- A `Console.CancelKeyPress` handler drives a `CancellationTokenSource` passed to the
  command tree. **1st press:** cancel the token (a batch stops before its next image),
  print a notice, `e.Cancel = true` to stay alive so the current image finishes.
  **2nd press:** `Environment.Exit(130)` — instant, and works even while the main
  thread is blocked in the native call because the event fires on its own thread.
- Superseded an earlier attempt that raised `ProcessTerminationTimeout` to 20 min;
  that made a single-image Ctrl-C appear frozen (no feedback, ~unbounded wait) since
  nothing checks the token mid-image. The two-stage handler fixes that.

**Note:** the two-stage Ctrl-C flow could not be runtime-verified in the dev shell
(it can't deliver a real console SIGINT). Verified instead: `null` disables SCL's
handler, `CancelKeyPress` fires off-thread, and the no-Ctrl-C happy path is intact.

## Scheduler exposure (and CFG-scheduling research — parked)

**Shipped:** `--scheduler` on `generate` (any of the library's schedulers), and the
`sdxl` preset defaults to `AYS` so low step counts stay usable.

**Parked — true per-step CFG scheduling (high→low across the run) is not feasible**
without a custom sampler. Findings (verified by reflection):
- Guidance is scalar — `GuidanceParameter { TxtCfg, ImgCfg, DistilledGuidance, Slg }`,
  set once for the whole run. `GenerateImage` takes no step delegate. No native
  guidance-schedule hook exists.
- Adjacent levers do exist: `SampleParameter.CustomSigmas` (per-step noise
  schedule), the `Scheduler` enum (now exposed), and `Slg` skip-layer guidance whose
  `SkipLayerStart`/`SkipLayerEnd` are *fractions* — proof the native side can gate
  guidance by run-progress, and a plausible upstream feature ask.
- Verdict: not worth a custom sampling loop for a CPU-only tool. See TODO.md
  "Parked / revisit" for the residual follow-ups.

## Solution restructure (src/ + tests/, ImageGen.slnx)

Reorganized the flat project into a `.slnx` solution:
- `src/ImageGen` — the console app.
- `src/ImageGen.Imaging` — generic, deterministic image tools (the unit-testable
  part), referenced by the app.
- `tests/ImageGen.Imaging.Tests` — xUnit tests for the imaging library.
- `models/`, `out/`, `logs/`, `reports/` stay at the repo root; run from root with
  `dotnet run --project src/ImageGen --`.

Only the imaging library is unit-tested; the app (native calls, DI, Ctrl-C, output
quality) stays "run and see" by deliberate choice.

## IImageCodec abstraction + PNG decoder

Replaced the static `Png` with an instance `Png : IImageCodec` (Save/Load) so image
I/O is injectable and swappable; registered `IImageCodec -> Png` and injected into
`ImageGenerationService` in place of the static calls.

- **`Png.Load` / `Png.Decode`** decode 8-bit non-interlaced PNGs: color type 2 (RGB)
  and 6 (RGBA, alpha dropped), all five scanline filters (None/Sub/Up/Average/Paeth).
  Unsupported variants (grayscale/palette/16-bit/interlaced) throw
  `ImageFormatException`. `RawImage` carries packed 24-bit RGB.
- **Tests:** round-trip Theory over several sizes, plus **external fixtures** — one
  PNG per scanline filter and one RGBA — hand-authored in Python and **validated with
  Pillow** (independent decoder) so they're trustworthy ground truth, not
  self-referential. The committed `generate_fixtures.py` reproduces them byte-for-byte.
  Guards the Sub/Up/Average/Paeth and alpha-drop paths our own encoder never exercises.
- Verified beyond the suite: decoded a real 512×512 app-generated PNG and an external
  RGBA PNG.

## Image editing — img2img

`generate --init <png> [--strength <f>]` starts from an existing image instead of
noise; `--strength` (0–1, default 0.75) sets how far the result may stray. First of
the five editing modes (see TODO.md for the rest).

- Input is loaded via `IImageCodec.Load`, wrapped as `HPPH.Image<ColorRGB>` via the
  4-arg `Create(bytes, w, h, stride)` overload (raw RGB straight through; ColorRGB is
  3 bytes), and fed to `ImageGenerationParameter.ImageToImage`. Output size follows
  the init image, so `--size` is ignored.
- **Gotcha handled:** the model must load with `VaeDecodeOnly = false` for img2img —
  encoding the init image into a latent needs the VAE *encoder*, which the
  text-to-image default (`true`) omits. Set from `request.IsImageToImage`.
- Init image is loaded up front (fail fast before the multi-GB model load).
- Verified end-to-end: strength 0.3 → mean 3.8/px diff from the source (subtle),
  0.8 → 55.4/px (heavy divergence) — confirming `Strength` behaves as intended, not
  just that it runs.

## Interactive refinement REPL

`generate --repl` drops into a loop after the first image: each prompt refines the
previous output via img2img, chaining toward a result. Opt-in, single-image only
(ignored with `--count > 1`).

- Each round: a Spectre `TextPrompt<string>` for the prompt, then a
  `TextPrompt<float>` for strength that **defaults to the last value** (Enter reuses
  it) and validates 0–1 — the answer to "how to set strength in the REPL." Each
  refinement saves its own timestamped file; the next round uses it as the init image.
- Reuses the same `RunOneAsync` progress-bar path and the `IImageCodec`/img2img
  chain — no new generation logic.
- **Ctrl-C** exits the loop cleanly: the global handler cancels the token, which
  `PromptAsync(..., cancellationToken)` surfaces as `OperationCanceledException`
  (caught). Empty prompt or `exit`/`quit` also leaves.
- **Robustness:** Spectre's prompts throw `InvalidOperationException` on
  non-interactive stdin (piped/CI); caught and reported ("needs an interactive
  terminal; skipping") rather than crashing.
- **Not runtime-verified interactively** (the dev shell can't drive a live prompt or
  real Ctrl-C). Verified instead: base-generate → REPL entry/banner, the
  non-interactive graceful-exit path, and that a standalone `--init` refine at the
  loop's exact parameters produces a valid chained image.

### Strength resolution fix (configurable refinement steps)

Symptom reported: img2img strength felt like an on/off switch — <0.5 did nothing,
>0.5 changed everything. Cause: strength effectively scales how many steps run
(`effective ≈ strength × steps`), so a preset's few-step default (SD Turbo = 2)
gives strength only ~2 distinct outcomes. Confirmed empirically: at fixed strength
0.4, divergence from source differed between 4 steps (21.6/px) and 20 steps
(29.3/px), showing step count is a real lever on the result.

Fix: the REPL uses its own step count (`RefinementOptions.Steps`, default 20)
instead of the preset default, and warns when a round's effective steps drop below
`CoarseStrengthStepThreshold` (default 4). Both live in `appsettings.yaml` under
`ImageGen:Refinement`, documented inline and in the README. Verified the YAML binds
(Steps=20, threshold=4).

### Refinement parameter prompts (prompt-through tuning)

Grew the REPL so each round exposes **every** parameter, not just strength. After
the refinement prompt, it walks through strength → steps → cfg → scheduler →
negative prompt → seed, each a Spectre `TextPrompt<T>` pre-filled with the current
value as its default: press Enter to keep it, or type a new value. Entered values
carry forward as the next round's defaults.

- Starting values come from `ImageGen:Refinement` config (`Strength`, `Steps`,
  `Cfg`, `Scheduler`, `NegativePrompt`, `RandomSeedEachRound`); parameters resolve to
  concrete values up front (preset defaults folded in) so every prompt shows a real
  default.
- Enum scheduler prompt works because Spectre parses input via
  `TypeDescriptor.GetConverter` (`EnumConverter` handles names case-insensitively);
  `.WithConverter` only controls how the default is displayed.
- **Seed handling (the sleeper fix):** rounds previously always reused seed 42, so
  re-running a prompt was deterministic — no exploration. Now with
  `RandomSeedEachRound: true` the seed prompt pre-fills a fresh random value each
  round (Enter = a new variation); otherwise it keeps the last seed.
- `Cfg`/`NegativePrompt` only bite on non-distilled presets (sd15/sdxl); Turbo bakes
  CFG ≈ 1. Documented as such.
- (Superseded an intermediate slash-command design — `/strength` etc. — after it
  proved less natural than Enter-through defaults.)
- Verified: build 0/0, 17 tests pass, all new YAML keys bind, non-interactive path
  exits gracefully. Interactive rendering still not driveable from the dev shell.

## Inference-speed experiments (measured)

Two levers tested with the `benchmark`/`generate` timing output; both settled by
measurement rather than assumption:

- **Thread count.** Default is `Environment.ProcessorCount` (22 logical here).
  Tested `--threads 11` (physical cores) on the theory that hyperthreads contend
  for FP units — but 22 was **faster** (sdturbo median 32.9s @ 22 vs 35.8s @ 11).
  Hyperthreading helps on this CPU; the default stays, and `--threads` remains for
  per-machine tuning.
- **Quantization** (`DiffusionModelParameter.Quantization`, "quantizes on load").
  **Not a CPU speed lever** — built `--quantize`, measured, reverted. sd15 @ 20
  steps: fp16 gen 299.0s, Q8_0 289.3s (~3%), Q4_K 286.7s (~4%, load time rose to
  6.2s). Output near-identical (Q8_0 vs fp16 mean 5.3/255 per byte). Gen is
  dominated by compute that dequantizes regardless, so fewer bits saves RAM, not
  math — stable-diffusion.cpp's CPU path lacks the quantized-matmul kernels that
  make quantization a win for LLMs (llama.cpp). Keep in mind only as a *memory*
  lever (fitting the ~7 GB SDXL weights on a constrained machine), never for speed.
