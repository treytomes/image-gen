# TODO

Planned features. Both are larger than they first appear because they bump
against what the underlying native library (stable-diffusion.cpp, via
StableDiffusion.NET v7) actually exposes. Each section records what's confirmed,
what's uncertain, and the smallest useful first step.

---

## 1. Pause-and-inspect generation

**Goal:** Let the user watch the image form step-by-step and stop early once the
result is good enough (or abandon a bad run without waiting for all steps).

### What the library gives us

- `StableDiffusionCpp.Preview` — a static event, and `EnablePreview(mode, interval,
  denoised, noisy)` to turn it on. This emits the **partially-denoised latent** at a
  configurable step interval. Confirmed present in v7 (seen during the progress-bar
  work); the exact `StableDiffusionPreviewEventArgs` shape still needs to be read.
- `StableDiffusionCpp.Progress` — already wired up; fires per sampling step. Requires
  `StableDiffusionCpp.InitializeEvents()` first (already called in the engine).

### The hard constraint

`DiffusionModel.GenerateImage(...)` is a **single blocking native call**. There is no
exposed "run to step N, hand control back, resume from N" entry point. So the honest
scope is **monitor-and-abort**, not true pause/resume:

- We *can*: show live preview thumbnails, and cancel the run early via the
  `CancellationToken` the engine already threads through (need to confirm the native
  side honors cancellation mid-sample; if not, cancellation only takes effect between
  jobs).
- We *cannot* (without a bigger change): freeze at step N, inspect, then continue the
  *same* latent to completion. That would require either a library feature that
  surfaces the sampling loop, or reimplementing the sampler loop against lower-level
  bindings.

### Proposed first step (spike)

1. Read `StableDiffusionPreviewEventArgs` to learn what the preview payload is
   (latent buffer? decoded image? dimensions?).
2. Call `EnablePreview(...)` in the engine and subscribe to `Preview`, converting each
   preview to a PNG in a temp/preview directory (reuse `Png.cs`).
3. Add a `generate --preview <interval>` option that writes those intermediate frames
   so the user can eyeball progress.
4. Verify whether cancelling the `CancellationToken` actually interrupts an in-flight
   `GenerateImage` on CPU. Document the answer either way.

### Open questions

- Does the native call honor cancellation mid-sample, or only between jobs?
- Preview payload format and cost — decoding a preview every N steps adds VAE work;
  measure the overhead (especially relevant on CPU-only targets).
- UX: write frames to disk vs. attempt in-terminal display. Disk is the safe default.

---

## 2. CFG scheduling (guidance that changes across the run)

**Goal:** Start with strong prompt adherence, then relax guidance toward the end for
more creative freedom — a per-step CFG schedule instead of one fixed value.

**Terminology note:** CFG (Classifier-Free Guidance scale) is **not** temperature.
Temperature is an LLM sampling concept; CFG controls how hard diffusion is pushed
toward the prompt. The high→low-over-time intuition is a real and useful technique,
just under the CFG name.

### The hard constraint

`SampleParameter.Guidance.TxtCfg` is a **single scalar** applied for the whole run.
The v7 API surface reviewed so far exposes no per-step CFG schedule / callback. So
this is blocked on one of:

- **(a)** A library feature — some hook to vary guidance per step. Needs investigation;
  may not exist.
- **(b)** Custom sampling loop — drive the denoise steps ourselves against lower-level
  bindings, applying our own CFG curve. Significantly more code and ties us to
  internals; likely out of scope for this app.
- **(c)** Approximation by segmentation — run a few steps at high CFG, then (if the
  library ever exposes resume) continue at lower CFG. Currently blocked by the same
  "no resume" limitation as feature #1.

### Proposed first step (investigation, no code yet)

1. Audit the full v7 `SampleParameter` / guidance surface and any sampler options for
   anything schedule-shaped (curves, arrays, callbacks). Reflection dump + changelog.
2. Check upstream stable-diffusion.cpp for CFG-schedule support that the wrapper may
   not surface yet — if it exists natively, the ask becomes a wrapper feature request.
3. If neither (a) nor a cheap (c) is available, park this feature and record why.

### Open questions

- Does stable-diffusion.cpp support any built-in guidance schedule?
- Is the payoff worth a custom sampling loop? Probably not for a CPU-only tool unless
  quality gains are clear.

---

## Shared prerequisite

Both features would benefit from the generation path exposing richer control than the
current single blocking `GenerateImage` call. Before committing to either, confirm
whether StableDiffusion.NET v7 (or a newer version) offers a stepwise/streaming
generation API. If a later version does, upgrading may unlock both features far more
cheaply than working around the current one.
