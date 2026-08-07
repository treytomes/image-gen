# TODO

Planned features. Both are larger than they first appear because they bump
against what the underlying native library (stable-diffusion.cpp, via
StableDiffusion.NET v7) actually exposes. Each section records what's confirmed,
what's uncertain, and the smallest useful first step.

---

## 1. Pause-and-inspect generation

**Goal:** Let the user watch the image form step-by-step and stop early once the
result is good enough (or abandon a bad run without waiting for all steps).

**STATUS: SHIPPED (preview frames).** `generate --preview <N>` writes intermediate
frames to `<output>.previews/`; `--preview-mode` selects tae/vae/proj. See README.
The "watch it form" half is done. The "abort early" half is still open (see below).

### What the runtime testing taught us (beyond the reflection findings)

- **`EnablePreview`'s interval is ignored in this build** — the native `Preview`
  event fires **every step** regardless of the interval passed. We enforce the
  interval **client-side** in the handler instead (keep first, last, and every Nth
  step). Verified: `--steps 8 --preview 3` → frames at steps 1, 3, 6, 8.
- **Preview cost is significant on CPU**, exactly as feared. A 12-step sd15 run with
  per-step TAE previews took ~404s vs. the ~273s/20-step baseline — because the
  native side decodes a preview *every* step even though we only save some. Our
  client-side throttle limits **disk output**, not the wasted decode time. Documented
  in README as "exploration, not timed runs."
- Preview payload confirmed as decoded `HPPH.Image<ColorRGB>`; `Png.Save` reuse worked
  with zero new decode code, as predicted.

### Still open: early abort

`GenerateImage` takes no `CancellationToken`, so cancelling mid-run isn't wired yet.
The runtime test for "can an in-flight generation be interrupted at all" (original
step 4) was **not** performed — the preview half was the priority. Next step if we
want true monitor-**and-abort**: attempt to interrupt the native call and document
whether it's possible or whether cancellation only takes effect between jobs.

### Original research verdict (kept for reference): FEASIBLE as monitor-and-abort.

### Research findings (StableDiffusion.NET 7.0.0, verified by reflection)

- `StableDiffusionCpp.Preview` is a **static event**; `StableDiffusionCpp.EnablePreview(
  Preview mode, int interval, bool denoised, bool noisy)` turns it on. Both are static.
- `Preview` enum modes: `None, Proj, TAE, VAE`. `TAE` = tiny autoencoder preview (fast,
  approximate); `VAE` = full decode (accurate, slower); `Proj` = cheap latent projection.
- **`StableDiffusionPreviewEventArgs` gives us `{ int Step, bool IsNoisy, Image<ColorRGB> Image }`.**
  The payload is an **already-decoded image**, not a raw latent — and it is
  `HPPH.Image<ColorRGB>`, the *exact type* `Png.cs` already encodes. So writing preview
  frames is essentially free plumbing: reuse `Png.Save(image.ToRawArray(), ...)`.
- `StableDiffusionCpp.Progress` (already wired) also fires per step; both events require
  `StableDiffusionCpp.InitializeEvents()`, already called in the engine.
- Events are **static/global**, but our generation queue uses a **single worker**
  (`GenerationQueue`, `singleReader: true`), so only one generation runs at a time —
  no cross-run event interleaving to guard against.

### The hard constraint (confirmed)

`DiffusionModel.GenerateImage(ImageGenerationParameter)` is the only entry point — a
**single blocking native call with no `CancellationToken` parameter** and no step
delegate. So:

- We **can**: emit live preview frames at an interval and let the user watch progress.
- We **cannot** (without a bigger change): truly pause at step N, inspect, then resume
  the *same* latent. No resume entry point exists.
- Early cancellation **confirmed impossible mid-run**: a runtime test throwing
  `OperationCanceledException` from the `Progress` callback showed the exception is
  **swallowed** — generation continued to completion and the process only stopped at a
  timeout. So a single in-flight `GenerateImage` cannot be interrupted from managed code.

So the honest, shippable scope is **"watch it form + stop between images"**, not
pause/resume or mid-image abort. **SHIPPED:** `generate --count <N>` runs a batch;
Ctrl-C (via System.CommandLine's token, with `ProcessTerminationTimeout` raised to 20
min in Program.cs so the current image finishes) stops the batch before the next image.

### Proposed first step (spike) — low risk, most plumbing already exists

1. ~~Read `StableDiffusionPreviewEventArgs`~~ **DONE** — `{ Step, IsNoisy, Image<ColorRGB> }`.
2. In the engine, call `EnablePreview(Preview.TAE, interval, denoised: true, noisy: false)`
   and subscribe to `Preview`; on each event, `Png.Save` the frame to a preview dir
   (e.g. `out/preview/<preset>-<timestamp>/step-NNN.png`). Unsubscribe in the `finally`
   alongside the existing `Progress` handler.
3. Add `generate --preview <interval>` (0 = off, the default) to write intermediate frames.
4. **Runtime test:** can an in-flight `GenerateImage` be interrupted at all? Try aborting
   the worker's token mid-run and observe. Document the answer — it sets whether "abort"
   is real or just "let it finish, then discard."

### Open questions

- Cancellation mid-sample: possible, or only between jobs? (step 4)
- Preview cost on CPU: `VAE` mode decodes every N steps and could dominate runtime on
  CPU-only targets; `TAE` is the cheap default. Measure overhead at a couple intervals.
- UX: write frames to disk (safe default) vs. in-terminal display (Spectre can't show
  raster images; would need sixel/kitty support — out of scope).

### Implementation notes (concrete plan for the spike)

Touch points, smallest-diff-first. This mirrors how the `Progress` event was wired, so
it slots into the existing engine cleanly.

1. **`GenerationRequest`** ([Generation/GenerationRequest.cs](Generation/GenerationRequest.cs)) —
   add `int PreviewInterval { get; init; }` (0 = off) and optionally
   `Preview PreviewMode { get; init; } = Preview.TAE`. Default off so nothing changes
   for normal runs.

2. **`ImageGenerationService.Generate`** ([Generation/ImageGenerationService.cs](Generation/ImageGenerationService.cs)) —
   where `Progress` is already subscribed:
   - If `request.PreviewInterval > 0`: call
     `StableDiffusionCpp.EnablePreview(request.PreviewMode, request.PreviewInterval, denoised: true, noisy: false)`
     and subscribe a local `OnPreview(object?, StableDiffusionPreviewEventArgs e)` handler.
   - In the handler: `Png.Save(previewDir/$"step-{e.Step:D3}.png", e.Image.ToRawArray(), e.Image.Width, e.Image.Height)`.
     Reuse the exact `ToRawArray()` → `Png.Save` path already used for the final image.
   - Compute `previewDir` once (e.g. `out/preview/<preset>-<timestamp>/`); create it lazily.
   - Unsubscribe `Preview` in the same `finally` that unsubscribes `Progress`; also call
     `EnablePreview(Preview.None, 0, ...)` to disable, so preview state doesn't leak to
     the next job (events are static/global).
   - Consider returning the preview dir on `GenerationResult` so the CLI can print it.

3. **CLI** ([CommandFactory.cs](CommandFactory.cs), `BuildGenerateCommand`) — add
   `--preview <interval>` (int, default 0) and optionally `--preview-mode <tae|vae|proj>`.
   Thread into the `GenerationRequest`. On success, print the preview dir if frames were
   written.

4. **Thread-safety note:** the `Preview` handler fires on the native worker thread (same
   as `Progress`). `Png.Save` is a plain file write with no shared state, so no extra
   synchronization is needed — but do NOT touch the Spectre `ProgressContext` from the
   preview handler except via the same synchronous pattern already used (see
   `SynchronousProgress`). Simplest: the preview handler only writes files, touches no UI.

5. **Cancellation experiment (step 4 above):** wire the worker's `CancellationToken` so a
   Ctrl-C / cancel attempt is observable, then test whether an in-flight `GenerateImage`
   actually stops. If it does not (likely, since the native call takes no token), leave
   the token checked only between jobs and document "abort = discard after current run."

### Estimated size

Small — one new option, ~1 new field, ~15 lines in the engine, reusing `Png.cs`
wholesale. The runtime cancellation test is the only real unknown.

---

## 2. CFG scheduling (guidance that changes across the run)

**Goal:** Start with strong prompt adherence, then relax guidance toward the end for
more creative freedom — a per-step CFG schedule instead of one fixed value.

**Terminology note:** CFG (Classifier-Free Guidance scale) is **not** temperature.
Temperature is an LLM sampling concept; CFG controls how hard diffusion is pushed
toward the prompt. The high→low-over-time intuition is a real and useful technique,
just under the CFG name.

**Verdict after research: NOT feasible for true per-step CFG scheduling without a
custom sampler. PARK the scheduling ambition; a couple of adjacent knobs exist.**

### Research findings (StableDiffusion.NET 7.0.0, verified by reflection)

- `SampleParameter.Guidance` is a `GuidanceParameter` with scalar fields:
  `TxtCfg`, `ImgCfg`, `DistilledGuidance`, and an `Slg` sub-parameter. **All scalars —
  set once, applied to the whole run.** No per-step CFG array or callback anywhere.
- `GenerateImage` takes only the parameter object — **no step delegate** to vary
  guidance mid-run. Same single-blocking-call constraint as feature #1.
- So true "high CFG early, low CFG late" is **blocked**: it needs either a native
  guidance-schedule feature (none exposed) or a custom sampling loop (out of scope —
  significant code, couples us to internals, questionable payoff on CPU-only).

### Adjacent levers that *do* exist (worth knowing, not the same feature)

- **`SampleParameter.CustomSigmas` (`float[]`)** — a custom **noise schedule** (the
  per-step sigma curve the sampler follows). This is not CFG, but it's the closest
  per-step control the API exposes: it shapes how aggressively denoising proceeds at
  each step. Could enable experiments like front-loading denoising. Semantics are
  defined by upstream stable-diffusion.cpp, not documented in the wrapper's XML.
- **`SampleParameter.Scheduler`** enum (Karras, Exponential, AYS, GITS, …) — swappable
  built-in schedules; a coarse, whole-run alternative to hand-rolled sigmas.
- **`Slg` (Skip-Layer Guidance)** — `{ int[] Layers, float SkipLayerStart, float
  SkipLayerEnd, float Scale }`. Notably, `SkipLayerStart`/`SkipLayerEnd` are
  **start/end fractions**, i.e. this *one* guidance mechanism already varies across the
  run. It's a different knob than CFG, but it's proof the native side can gate guidance
  by progress — a hint that a CFG schedule could be a reasonable upstream feature ask.

### Recommendation

1. **Park true CFG scheduling** — not worth a custom sampler for a CPU-only tool.
2. If there's appetite to experiment, the cheap path is exposing **`Scheduler`** (and
   possibly `CustomSigmas`) as `generate` options — that gives real per-run control over
   the denoise trajectory without touching guidance internals.
3. Optionally file/track an upstream request for a CFG schedule, citing that `Slg`
   already gates guidance by start/end fraction.

### Open questions

- What exactly does `CustomSigmas` expect (length = steps? sigma ordering high→low)?
  Needs an upstream stable-diffusion.cpp read + a runtime trial.
- Would exposing `Scheduler` alone satisfy the underlying goal (varying the denoise
  trajectory), making the CFG-schedule ask unnecessary?

---

## Shared finding

Both features are ultimately gated by the same constraint: **`GenerateImage` is one
blocking native call** with no cancellation token and no step delegate. Research
confirmed v7 exposes no stepwise/streaming generation API and no resume entry point.

- Feature #1 works *around* this via the `Preview` event (observe-only), so it's
  shippable as monitor-and-abort.
- Feature #2's core ask (per-step CFG) cannot be worked around without a custom sampler.

**Next-version check:** before investing further in either, look at whether a newer
StableDiffusion.NET / stable-diffusion.cpp release adds a stepwise or cancellable
generation API. If so, upgrading likely unlocks true pause/resume *and* CFG scheduling
far more cheaply than working around the current single-call design. The wrapper pins a
specific upstream commit (`StableDiffusionCpp.ExpectedSDCommit`) — compare against
upstream `master` when evaluating an upgrade.

## Research status

- **Done:** v7 API surface reflected and documented above (preview event args, guidance
  params, sampler options, event staticness, no-cancellation-token fact).
- **Still needs a runtime trial (not just reflection):**
  1. Whether an in-flight `GenerateImage` can be cancelled at all (feature #1, step 4).
  2. Preview decode overhead on CPU at a couple of intervals / modes (`TAE` vs `VAE`).
  3. What `CustomSigmas` expects and whether it meaningfully changes output.
