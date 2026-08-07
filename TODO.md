# TODO

Remaining work. Each section records what's confirmed (by reflection against
StableDiffusion.NET v7), what's still uncertain, and the smallest useful first
step. Completed features live in [DONE.md](DONE.md).

---

## Image editing (img2img, inpaint, ControlNet, reference, PhotoMaker)

**Goal:** Edit existing images, not just generate from scratch. Five related
capabilities the wrapper exposes, in rough order of CPU practicality.

**Verdict after research (StableDiffusion.NET 7.0.0, verified by reflection):
img2img / inpaint / ControlNet are practical on CPU with the SD 1.5 / SDXL weights
we already have; reference-image and PhotoMaker need extra weights and specific
model support. ALL five share one hard prerequisite (see below).**

### Shared prerequisite: a PNG *decoder*

Every editing mode takes an input image as `HPPH.IImage`. Confirmed by reflection:
- The library has **no image loader** — no `LoadImage` / `FromFile` / `Decode`
  anywhere. `DiffusionModel` exposes only `GenerateImage` / `GenerateVideo`.
- The only way to build an `IImage` is `HPPH.Image<ColorRGB>.Create(buffer, w, h)`
  (or `Wrap(...)`) from **raw RGB pixels**.
- `Png` (now in `ImageGen.Imaging`) only **encodes**. So we must first write a
  **PNG decoder** (inflate + unfilter, the reverse of `Png.Save`) to load a user's
  input image into raw RGB.

The imaging library and its test project already exist (`src/ImageGen.Imaging`,
`tests/ImageGen.Imaging.Tests`). `PngTests.cs` has a **skipped round-trip test**
(`Load_RoundTripsSavedImage`) as the red-green anchor: implement `Png.Load`, unskip
it, and it asserts encode→decode returns the original pixels. Add a few real-file
fixtures decoded from an external tool to prove independence from our own encoder.

This is the gating task for the whole feature. Scope: a `Png.Load(path) ->
(byte[] rgb, int w, int h)` mirroring `Png.Save`, using `System.IO.Compression`
for inflate. Only need to support the 8-bit truecolor PNGs we ourselves write plus
common editor output; document the supported subset. (Alternative: take input as
raw/BMP to defer the decoder — but PNG in/PNG out is the sane UX.)

### The API, verified

All on `ImageGenerationParameter` (with fluent `With*` extension helpers):
`InitImage`, `Strength`, `MaskImage`, `RefImages` + `AutoResizeRefImage` +
`IncreaseRefIndex`, `ControlNet` (`ControlNetParameter { Image, Strength }`),
`PhotoMaker` (`PhotoMakerParameter { IdImages, IdEmbedPath, StyleStrength }`).
There is a static `ImageGenerationParameter.ImageToImage(prompt, image)` factory
paralleling `TextToImage`. Some modes also need model-load-time paths on
`DiffusionModelParameter`: `ControlNetPath`, `StackedIdEmbeddingsDirectory`.

### 3a. img2img — `InitImage` + `Strength`  [CPU: easy, recommended first]

- Feed an init image; `Strength` (0–1) sets how far from it to wander (low = subtle
  restyle, high = loosely inspired). Fewer effective steps than txt2img, so it's
  *cheaper* than generation.
- Needs: PNG decoder. No extra weights — uses current sd15/sdxl checkpoints.
- Impl: `GenerationRequest.InitImagePath` + `Strength`; if set, call
  `ImageToImage(prompt, img)` instead of `TextToImage`. New `edit`/`img2img` command
  or an `--init <png> --strength <f>` pair on `generate`.
- Testing: round-trip a generated PNG back in at strength 0.3 and 0.8; confirm low
  strength barely changes it, high strength diverges.

### 3b. inpaint — `MaskImage`  [CPU: easy]

- Supply image + mask; only the masked region is regenerated.
- Needs: PNG decoder (image **and** mask). **Open question (runtime):** does base
  sd15/sdxl inpaint acceptably, or is a dedicated inpainting checkpoint required for
  good seams? Reflection can't answer; test with base weights first.
- Impl: `--mask <png>` alongside `--init`; wire `WithMaskImage`.
- Testing: mask a rectangle over a generated image, prompt a replacement, check the
  unmasked region is preserved and the seam is plausible.

### 3c. ControlNet + Canny  [CPU: moderate; needs a ControlNet model]

- Condition generation on structure (Canny edge map) so output follows composition
  while the prompt supplies content.
- Needs: PNG decoder; a **ControlNet checkpoint** via `DiffusionModelParameter.
  ControlNetPath` (extra multi-hundred-MB download); `KeepControlNetOnCPU` exists.
  `StableDiffusionCpp.PreprocessCanny(CannyParameter)` generates the edge map for us
  (static, returns an `Image`), so we don't hand-roll edge detection.
- Impl: model-load path wiring + a preset/option for the ControlNet file;
  `ControlNetParameter { Image, Strength }`. Bigger than 3a/3b because it touches
  model loading, not just the request.
- Testing: Canny-preprocess a source image, generate with a new prompt, confirm the
  output traces the source edges.

### 3d. RefImages — reference-image conditioning  [CPU: uncertain; model-dependent]

- `RefImages` (+ `AutoResizeRefImage`, `IncreaseRefIndex`) conditions on reference
  images. **Open question:** which model families this is wired for in our build
  (IP-Adapter-style vs. Flux-Kontext-style editing). Sampling cost ~= generation, so
  the blocker is *support + weights*, not raw CPU speed. NEEDS INVESTIGATION before
  committing — verify against the pinned upstream commit which models honor RefImages.

### 3e. PhotoMaker — identity/subject editing  [CPU: heaviest of the five]

- `PhotoMakerParameter { IdImages, IdEmbedPath, StyleStrength }` + model-load
  `StackedIdEmbeddingsDirectory`. Takes ID photos, preserves a subject's identity.
- Needs: PNG decoder; a **PhotoMaker checkpoint** (extra download); it is
  **SDXL-based**, so per-image cost is SDXL-tier (~4–5 min/image on our CPU per the
  baseline). Not CPU-*hostile*, but the slowest and most setup-heavy. Lowest priority.

### Suggested order

1. **PNG decoder** (`Png.Load`) — unblocks everything.
2. **img2img** (3a) — smallest, most useful, no extra weights.
3. **inpaint** (3b) — small delta over img2img; answer the base-vs-dedicated-model question.
4. **ControlNet** (3c) — first one needing model-load changes + an extra download.
5. **Investigate RefImages** (3d) before deciding; **PhotoMaker** (3e) last.

### Cross-cutting testing notes

- All editing modes reuse the existing generation queue / progress / preview
  plumbing unchanged — only the request shape and (for 3c/3e) model loading differ.
- Reuse `--count` for edit variations (seed sweep of an img2img at fixed strength).
- The preview feature applies to editing too — watch an edit converge.

---

## Parked / revisit

Not planned work, but worth keeping in view:

- **Upstream-version check.** `GenerateImage` is one blocking native call with no
  cancellation token and no step delegate — this gated both the (shipped)
  monitor-and-abort scope and the (parked) CFG scheduling. Before any deeper work
  on stepwise control, check whether a newer StableDiffusion.NET / stable-diffusion.cpp
  release adds a stepwise or cancellable generation API. The wrapper pins a specific
  upstream commit (`StableDiffusionCpp.ExpectedSDCommit`) — compare against upstream
  `master` when evaluating an upgrade. An upgrade could unlock true pause/resume and
  per-step CFG far more cheaply than working around the current design.
- **Per-step CFG scheduling** stays parked (see [DONE.md](DONE.md) for why). If
  desired, the realistic path is an upstream feature ask, citing that `Slg` already
  gates guidance by start/end fraction.
- **`CustomSigmas` experiment.** What does the `float[]` expect (length = steps?
  sigma ordering high→low?) and does it meaningfully change output? Needs an upstream
  read + a runtime trial. Only worth it if someone wants to experiment with the
  denoise trajectory beyond the `Scheduler` options already exposed.
