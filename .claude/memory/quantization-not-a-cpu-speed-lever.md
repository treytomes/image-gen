---
name: quantization-not-a-cpu-speed-lever
description: Weight quantization does NOT speed up CPU inference in the image-gen stack (measured)
metadata: 
  node_type: memory
  type: project
  originSessionId: 33c61fd2-faca-471d-9171-124439171904
  modified: 2026-08-08T00:16:08.669Z
---

For the ImageGen app (stable-diffusion.cpp via StableDiffusion.NET, CPU-only — see [[cpu-only-target]]), **on-load weight quantization is not a speed lever.**

Measured 2026-08-07, sd15 @ 20 steps, same prompt/seed:
- fp16 (full precision): gen **299.0s**
- Q8_0: **289.3s** (~3% faster)
- Q4_K: **286.7s** (~4% faster, and load time rose to 6.2s from the on-load quantize)
- Output near-identical (Q8_0 vs fp16 mean abs diff 5.3/255 per byte).

**Why:** `DiffusionModelParameter.Quantization` "quantizes on load"; gen time is dominated by compute that dequantizes regardless. stable-diffusion.cpp's CPU path lacks the optimized quantized-matmul kernels that make quantization a real win for LLMs (llama.cpp). Fewer bits saves RAM, not math.

**How to apply:** Don't propose quantization for speed. The `--quantize` feature was built and reverted. Quantization is only worth mentioning as a *memory* lever (e.g. fitting the ~7 GB SDXL weights on a RAM-constrained machine). Full detail also lives in the repo's DONE.md.

Related measured finding: thread count — 22 logical threads beat 11 physical (32.9s vs 35.8s on sdturbo), so hyperthreading helps here; the `Environment.ProcessorCount` default stays.
