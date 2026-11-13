# ImageGen Performance Report

> **Reference baseline**, checked in for comparison. Captured on an 11-core / 22-thread
> CPU-only machine (see Environment below). Regenerate with `dotnet run -- benchmark`;
> fresh reports land in the gitignored `reports/` directory.

Generated: 2026-08-06 16:46:59 -05:00

## Environment

| Field | Value |
|---|---|
| Machine | (redacted) |
| OS | Microsoft Windows 10.0.26200 |
| Logical cores | 22 |
| Physical cores | 11 |
| Threads used | 22 |
| Native info | X64 |

## Run settings

| Field | Value |
|---|---|
| Prompt | a red fox in a snowy forest |
| Seed | 42 |
| Timed runs / preset | 3 |
| Warm-up runs (discarded) | 1 |

## Results

Times in seconds. Load = model load; Gen = sampling; per-step = median gen ÷ steps.
Total columns are min / median / max across the timed runs.

| Preset | Size | Steps | Load (med) | Gen (med) | Total min | Total med | Total max | Sec/step |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| sd15 | 512×512 | 20 | 1.0 | 273.4 | 269.0 | 274.4 | 274.9 | 13.7 |
| sdturbo | 512×512 | 2 | 2.3 | 29.5 | 31.6 | 31.8 | 32.1 | 14.7 |
| sdxlturbo | 1024×1024 | 4 | 3.5 | 261.3 | 262.6 | 264.6 | 265.2 | 65.3 |

## Relative speed

Median total time, fastest first. Multiplier is relative to the fastest preset.

| Preset | Median total (s) | vs. fastest |
|---|---:|---:|
| sdturbo | 31.8 | 1.0× (fastest) |
| sdxlturbo | 264.6 | 8.3× slower |
| sd15 | 274.4 | 8.6× slower |

