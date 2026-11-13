---
name: cpu-only-target
description: ImageGen targets CPU-only systems with no CUDA/GPU support
metadata: 
  node_type: memory
  type: project
  originSessionId: 33c61fd2-faca-471d-9171-124439171904
  modified: 2026-08-06T20:00:51.271Z
---

The ImageGen app targets systems with **no CUDA/GPU support** — CPU-only inference is the deployment reality, not just a fallback.

**Why:** The user confirmed (2026-08-06) they are targeting systems with no CUDA support.

**How to apply:**
- Keep the `StableDiffusion.NET.Backend.Cpu` package; do not switch the default to CUDA.
- Favor the few-step distilled models (SD Turbo / SDXL Turbo, 1–4 steps) since full SD 1.5 at 20 steps is slow on CPU (~15s/step observed, ~300s total).
- Frame perf expectations around CPU timings; the README's GPU note stays a brief one-liner and is not a priority.
