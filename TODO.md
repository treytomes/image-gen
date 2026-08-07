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

### Shared prerequisite: a PNG *decoder* — DONE

Every editing mode takes an input image as `HPPH.IImage`, built via
`HPPH.Image<ColorRGB>.Create(buffer, w, h)` from raw RGB — the library has no image
loader of its own. That decoder now exists: **`Png.Load` / `Png.Decode`** in
`ImageGen.Imaging` (behind `IImageCodec`), decoding 8-bit non-interlaced RGB/RGBA
with all five scanline filters. Covered by fixture tests (one per filter + RGBA,
Pillow-validated). See [DONE.md](DONE.md). So editing work can now start directly at
img2img; each mode below just needs to load its input image(s) via the codec and
hand the raw pixels to `HPPH.Image<ColorRGB>.Create`.

### The API, verified

All on `ImageGenerationParameter` (with fluent `With*` extension helpers):
`InitImage`, `Strength`, `MaskImage`, `RefImages` + `AutoResizeRefImage` +
`IncreaseRefIndex`, `ControlNet` (`ControlNetParameter { Image, Strength }`),
`PhotoMaker` (`PhotoMakerParameter { IdImages, IdEmbedPath, StyleStrength }`).
There is a static `ImageGenerationParameter.ImageToImage(prompt, image)` factory
paralleling `TextToImage`. Some modes also need model-load-time paths on
`DiffusionModelParameter`: `ControlNetPath`, `StackedIdEmbeddingsDirectory`.

### 3a. img2img — `InitImage` + `Strength`  ✅ SHIPPED

Done — `generate --init <png> --strength <f>`. See [DONE.md](DONE.md).

### 3b. inpaint — `MaskImage`  [CPU: easy — do next]

- Supply image + mask; only the masked region is regenerated.
- Needs: load image **and** mask via `IImageCodec` (done). **Open question
  (runtime):** does base sd15/sdxl inpaint acceptably, or is a dedicated inpainting
  checkpoint required for good seams? Reflection can't answer; test with base weights
  first.
- Impl: `--mask <png>` alongside `--init`; wire `WithMaskImage`.
- Testing: mask a rectangle over a generated image, prompt a replacement, check the
  unmasked region is preserved and the seam is plausible.

### 3c. ControlNet + Canny  [CPU: moderate; needs a ControlNet model]

- Condition generation on structure (Canny edge map) so output follows composition
  while the prompt supplies content.
- Needs: input loaded via `IImageCodec` (done); a **ControlNet checkpoint** via
  `DiffusionModelParameter.ControlNetPath` (extra multi-hundred-MB download);
  `KeepControlNetOnCPU` exists.
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
- Needs: ID images loaded via `IImageCodec` (done); a **PhotoMaker checkpoint**
  (extra download); it is **SDXL-based**, so per-image cost is SDXL-tier (~4–5
  min/image on our CPU per the baseline). Not CPU-*hostile*, but the slowest and most
  setup-heavy. Lowest priority.

### Suggested order

0. ~~**PNG decoder** (`Png.Load`) — unblocks everything.~~ **DONE.**
1. ~~**img2img** (3a) — smallest, most useful, no extra weights.~~ **DONE.**
2. **inpaint** (3b) — small delta over img2img; answer the base-vs-dedicated-model question. **Start here.**
3. **ControlNet** (3c) — first one needing model-load changes + an extra download.
4. **Investigate RefImages** (3d) before deciding; **PhotoMaker** (3e) last.

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
