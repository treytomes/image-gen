# AGENTS.md - ImageGen

## Quickstart
- .NET 10 SDK required. Run commands from repo root.
- Build: `dotnet build ImageGen.slnx`
- Test: `dotnet test ImageGen.slnx` (only imaging tests exist)
- Run: `dotnet run --project src/ImageGen -- <command>` (abbreviated as `dotnet run -- ...` in docs)
- Models live in `./models/` (or `MODELS_DIR` env). Downloads: `dotnet run -- download --all`.

## Project structure
- `src/ImageGen/` - Console app (Generic Host + System.CommandLine + DI + Serilog + Spectre.Console). Uses Channel<T> + BackgroundService workers.
- `src/ImageGen.Imaging/` - Pure image codec (PNG Load/Save). Dependency-free; unit-tested. `IImageCodec` injected.
- `tests/ImageGen.Imaging.Tests/` - xUnit tests for imaging (includes filter fixtures for PNG unfilter paths).
- `src/ImageGen/Infrastructure/QueueWorkerService.cs` - Shared queue worker base (downloads use configurable pool; generation uses single worker).
- `out/`, `logs/`, `models/`, `reports/` at repo root; app uses working-dir-relative paths.

## Commands (exact, non-obvious)
- List presets: `dotnet run -- list`
- Generate with preset: `dotnet run -- generate sdturbo "prompt"`
- Batch: `--count N` (increments seed; timestamped names). Ctrl-C: first press finishes current image then stops before next; second quits immediately.
- Img2img: `--init <png> --strength 0.75` (size follows input; `--size` ignored).
- Refinement loop: `--repl` (single image; steps through params; defaults from `appsettings.yaml` Refinement; Turbo presets bake CFG~1 so neg/cfg less effective).
- Previews: `--preview N [--preview-mode tae|vae|proj]`. Native decodes every step even if we throttle writes - costs time on CPU; `tae` fastest.
- Downloads: `--download [--preset name]+ [--all] [--dir path]` (resumable `.part`, skips existing). Worker count from `ImageGen:DownloadWorkerCount` (default 2).
- Config override: `IMAGEGEN_ImageGen__*` env vars (e.g. `IMAGEGEN_ImageGen__DownloadWorkerCount=4`); also `MODELS_DIR`.

## Model + scheduler facts
- Presets in `src/ImageGen/ModelPreset.cs` (filenames/sources). VAE fp16 for SDXL auto-picked if present.
- Turbo (sdturbo/sdxlturbo): distilled, few steps (1-4), CFG~1; negative prompt has little effect; don't expect quality gain from many steps.
- Full models (sd15/sdxl): sd15 ~20 steps cfg~7; sdxl base uses **AYS** scheduler by default, often 25-40 steps for quality (slower on CPU).
- Dimensions must be multiple of 64; size close to native matters. Native: sd15 512x512, sdturbo 512x512, sdxlturbo 1024x1024.
- Ctrl-C cannot interrupt mid-image (native call); two-stage behavior implemented in `Program.cs`. We set `ProcessTerminationTimeout=null` to disable System.CommandLine's Ctrl-C handling.

## Architecture notes
- Background workers: `DownloadBackgroundService` (configurable pool via `WorkerCount`), `GenerationBackgroundService` (single worker - model held in memory, CPU/GPU-bound). Both inherit `QueueWorkerService<TRequest,TProgress,TResult>`.
- Engine: `ImageGenerationService` reports progress via `StableDiffusion.NET` events (`StableDiffusionCpp.Progress/Preview`). `StableDiffusionCpp.InitializeEvents()` called once. Previews use client-side interval filtering (native may fire every step).
- Hosting: Generic Host; Serilog configured from `appsettings.yaml` (also env). `appsettings.yaml` copied to output.
- Imaging: custom `Png.cs` (no ImageSharp license dependency). Fixtures test filter types 0-4.

## Conventions
- Treat warnings as errors; nullable enabled; XML docs generated.
- Logging goes to console + rolling file (`logs/imagegen-<date>.log`, 14 retained).
- Output files timestamped to avoid overwrite; preview dir `<output>.previews/`.
- When adding features that affect model loading or native params, check `ImageGenerationParameter`/`DiffusionModelParameter` in StableDiffusion.NET (v7) usage patterns in codebase first.

## Gotchas
- `dotnet run --` requires `--project src/ImageGen` in some shells; docs show abbreviated form - use the full project path as above.
- Previews are slow because decode happens every step (not just saved steps).
- Turbo presets ignore/are brittle with high CFG and extra steps.
- SDXL is full base (not distilled) - real CFG/neg but expensive on CPU.
