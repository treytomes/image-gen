using System.Globalization;
using System.Text;

namespace ImageGen.Benchmark;

/// <summary>Renders a <see cref="BenchmarkReport"/> as a Markdown document.</summary>
public static class MarkdownReportWriter
{
    public static string Render(BenchmarkReport report)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# ImageGen Performance Report");
        sb.AppendLine();
        sb.AppendLine(Culture($"Generated: {report.StartedAt:yyyy-MM-dd HH:mm:ss zzz}"));
        sb.AppendLine();

        sb.AppendLine("## Environment");
        sb.AppendLine();
        sb.AppendLine("| Field | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Machine | {report.MachineName} |");
        sb.AppendLine($"| OS | {report.OSDescription} |");
        sb.AppendLine($"| Logical cores | {report.LogicalCores} |");
        sb.AppendLine($"| Physical cores | {(report.PhysicalCores > 0 ? report.PhysicalCores.ToString() : "n/a")} |");
        sb.AppendLine($"| Threads used | {report.ThreadsUsed} |");
        sb.AppendLine($"| Native info | {report.NativeSystemInfo} |");
        sb.AppendLine();

        sb.AppendLine("## Run settings");
        sb.AppendLine();
        sb.AppendLine("| Field | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Prompt | {report.Prompt} |");
        sb.AppendLine($"| Seed | {report.Seed} |");
        sb.AppendLine($"| Timed runs / preset | {report.Runs} |");
        sb.AppendLine($"| Warm-up runs (discarded) | {report.Warmup} |");
        sb.AppendLine();

        sb.AppendLine("## Results");
        sb.AppendLine();
        sb.AppendLine("Times in seconds. Load = model load; Gen = sampling; per-step = median gen ÷ steps.");
        sb.AppendLine("Total columns are min / median / max across the timed runs.");
        sb.AppendLine();
        sb.AppendLine("| Preset | Size | Steps | Load (med) | Gen (med) | Total min | Total med | Total max | Sec/step |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|");

        foreach (var r in report.Results)
        {
            if (r.Skipped)
            {
                sb.AppendLine($"| {r.Preset.Key} | {r.Width}×{r.Height} | {r.Steps} | — | — | — | — | — | _{r.SkipReason}_ |");
                continue;
            }

            var load = Stats.From(r.Runs, t => t.LoadSeconds)!.Value;
            var gen = Stats.From(r.Runs, t => t.GenerationSeconds)!.Value;
            var totalStats = Stats.From(r.Runs, t => t.TotalSeconds)!.Value;
            var secPerStep = r.Steps > 0 ? gen.Median / r.Steps : 0;

            sb.AppendLine(Culture(
                $"| {r.Preset.Key} | {r.Width}×{r.Height} | {r.Steps} | {load.Median:F1} | {gen.Median:F1} | {totalStats.Min:F1} | {totalStats.Median:F1} | {totalStats.Max:F1} | {secPerStep:F1} |"));
        }

        sb.AppendLine();
        AppendRelativeSpeed(sb, report.Results);

        return sb.ToString();
    }

    /// <summary>Adds a "×faster/slower than the slowest preset" comparison, which is
    /// what the CPU-only story actually turns on.</summary>
    private static void AppendRelativeSpeed(StringBuilder sb, IReadOnlyList<PresetBenchmark> results)
    {
        var timed = results
            .Where(r => !r.Skipped && r.Runs.Count > 0)
            .Select(r => (r.Preset.Key, Median: Stats.From(r.Runs, t => t.TotalSeconds)!.Value.Median))
            .OrderBy(x => x.Median)
            .ToArray();

        if (timed.Length < 2)
            return;

        var fastest = timed[0].Median;

        sb.AppendLine("## Relative speed");
        sb.AppendLine();
        sb.AppendLine("Median total time, fastest first. Multiplier is relative to the fastest preset.");
        sb.AppendLine();
        sb.AppendLine("| Preset | Median total (s) | vs. fastest |");
        sb.AppendLine("|---|---:|---:|");
        foreach (var (key, median) in timed)
        {
            var factor = fastest > 0 ? median / fastest : 1;
            var label = factor <= 1.0001 ? "1.0× (fastest)" : $"{factor:F1}× slower";
            sb.AppendLine(Culture($"| {key} | {median:F1} | {label} |"));
        }
        sb.AppendLine();
    }

    private static string Culture(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
