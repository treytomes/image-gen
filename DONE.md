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
impossible from managed code. Consequences, as shipped:
- `ProcessTerminationTimeout` is raised to 20 min in `Program.cs` so the current
  image finishes and saves before the process is force-terminated.
- Cancellation only stops the *next* image in a batch from starting — ideal for
  "queue a pile, stop once a preview looks good."

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
