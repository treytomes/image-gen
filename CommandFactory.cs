using System.CommandLine;
using ImageGen.Benchmark;
using ImageGen.Downloads;
using ImageGen.Generation;
using ImageGen.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Spectre.Console;

namespace ImageGen;

/// <summary>Builds the System.CommandLine command tree and wires actions into
/// DI-resolved services.</summary>
public static class CommandFactory
{
    public static RootCommand Build(IServiceProvider services)
    {
        var root = new RootCommand("Local text-to-image generator (Stable Diffusion, runs on your machine).");
        root.Add(BuildGenerateCommand(services));
        root.Add(BuildListCommand());
        root.Add(BuildDownloadCommand(services));
        root.Add(BuildBenchmarkCommand(services));
        return root;
    }

    private static Command BuildGenerateCommand(IServiceProvider services)
    {
        var presetArg = new Argument<string>("preset")
        {
            Description = $"Model preset: {string.Join(" | ", Presets.Keys)}",
        };
        presetArg.AcceptOnlyFromAmong(Presets.Keys.ToArray());

        var promptArg = new Argument<string>("prompt") { Description = "Text prompt to generate from." };

        var outOpt = new Option<string?>("--out", "-o") { Description = "Output PNG path (default: a timestamped file under out/)." };
        var seedOpt = new Option<long>("--seed") { Description = "RNG seed (default 42).", DefaultValueFactory = _ => 42 };
        var stepsOpt = new Option<int?>("--steps") { Description = "Override sampling steps." };
        var cfgOpt = new Option<float?>("--cfg") { Description = "Override guidance (CFG) scale." };
        var negOpt = new Option<string?>("--negative", "-n") { Description = "Negative prompt." };
        var threadsOpt = new Option<int?>("--threads") { Description = "CPU thread count (default: all cores)." };
        var sizeOpt = new Option<string?>("--size") { Description = "Dimensions as WxH, e.g. 512x512." };

        var cmd = new Command("generate", "Generate an image from a text prompt.")
        {
            presetArg, promptArg, outOpt, seedOpt, stepsOpt, cfgOpt, negOpt, threadsOpt, sizeOpt,
        };

        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var presetKey = parseResult.GetValue(presetArg)!;
            var preset = Presets.Find(presetKey)!; // validated by AcceptOnlyFromAmong

            (int Width, int Height)? size = null;
            var sizeRaw = parseResult.GetValue(sizeOpt);
            if (!string.IsNullOrWhiteSpace(sizeRaw))
            {
                var parts = sizeRaw.Split('x', 'X');
                if (parts.Length != 2 || !int.TryParse(parts[0], out var w) || !int.TryParse(parts[1], out var h))
                {
                    AnsiConsole.MarkupLine("[red]--size must be WxH, e.g. 512x512.[/]");
                    return 1;
                }
                size = (w, h);
            }

            var request = new GenerationRequest
            {
                Preset = preset,
                Prompt = parseResult.GetValue(promptArg)!,
                NegativePrompt = parseResult.GetValue(negOpt),
                Width = size?.Width,
                Height = size?.Height,
                Steps = parseResult.GetValue(stepsOpt),
                Cfg = parseResult.GetValue(cfgOpt),
                Seed = parseResult.GetValue(seedOpt),
                Threads = parseResult.GetValue(threadsOpt),
                OutputPath = parseResult.GetValue(outOpt),
            };

            PrintRequestSummary(request);

            var queue = services.GetRequiredService<GenerationQueue>();

            // A live progress bar driven by the engine's step-progress hook (via the
            // background worker). The bar covers the sampling phase; model loading
            // shows as an indeterminate spinner before the first step arrives.
            //
            // Generation saturates every CPU core, so we can't rely on Spectre's
            // auto-refresh thread getting scheduled. AutoRefresh is disabled and we
            // redraw synchronously from the native progress callback (see
            // SynchronousProgress) — the one thread guaranteed to have the CPU.
            var result = await AnsiConsole.Progress()
                .AutoClear(false)
                .AutoRefresh(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new RemainingTimeColumn(),
                    new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask($"Generating ({preset.Key})", autoStart: true, maxValue: request.EffectiveSteps);

                    var progress = new SynchronousProgress<GenerationProgress>(p =>
                    {
                        task.Description = p.Phase == GenerationPhase.LoadingModel
                            ? $"Loading model ({preset.Key})"
                            : $"Sampling ({preset.Key})";
                        task.MaxValue = p.TotalSteps;
                        task.Value = p.Step;
                        ctx.Refresh();
                    });

                    var job = queue.Enqueue(request, progress);
                    try
                    {
                        var r = await job.Completion;
                        task.Value = task.MaxValue;
                        ctx.Refresh();
                        return (GenerationResult?)r;
                    }
                    catch (Exception ex)
                    {
                        ctx.Refresh();
                        AnsiConsole.MarkupLineInterpolated($"[red]Generation failed:[/] {ex.Message}");
                        return null;
                    }
                });

            if (result is not { } res)
                return 2;

            AnsiConsole.MarkupLineInterpolated($"[green]Saved:[/] {res.OutputPath}");
            AnsiConsole.MarkupLineInterpolated(
                $"[grey]load {res.LoadSeconds:F1}s + gen {res.GenerationSeconds:F1}s[/]");
            return 0;
        });

        return cmd;
    }

    private static void PrintRequestSummary(GenerationRequest r)
    {
        var table = new Table().Border(TableBorder.Rounded).HideHeaders();
        table.AddColumn("k");
        table.AddColumn("v");
        table.AddRow("Preset", $"[bold]{Markup.Escape(r.Preset.Name)}[/]");
        table.AddRow("Model file", Markup.Escape(r.Preset.FileName));
        table.AddRow("Size", $"{r.EffectiveWidth}x{r.EffectiveHeight}");
        table.AddRow("Steps / CFG", $"{r.EffectiveSteps} / {r.EffectiveCfg}");
        table.AddRow("Seed / Threads", $"{r.Seed} / {r.EffectiveThreads}");
        table.AddRow("Prompt", Markup.Escape(r.Prompt));
        if (!string.IsNullOrEmpty(r.EffectiveNegativePrompt))
            table.AddRow("Negative", Markup.Escape(r.EffectiveNegativePrompt));
        AnsiConsole.Write(table);
    }

    private static Command BuildListCommand()
    {
        var cmd = new Command("list", "List available model presets and their defaults.");
        cmd.SetAction(_ =>
        {
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Preset");
            table.AddColumn("Model");
            table.AddColumn(new TableColumn("Size").Centered());
            table.AddColumn(new TableColumn("Steps").Centered());
            table.AddColumn(new TableColumn("CFG").Centered());
            table.AddColumn("File");

            foreach (var p in Presets.All)
                table.AddRow(
                    $"[bold]{p.Key}[/]",
                    p.Name,
                    $"{p.Width}x{p.Height}",
                    p.Steps.ToString(),
                    p.Cfg.ToString(),
                    Markup.Escape(p.FileName));

            AnsiConsole.Write(table);
        });
        return cmd;
    }

    private static Command BuildDownloadCommand(IServiceProvider services)
    {
        var urlArg = new Argument<string[]>("urls")
        {
            Description = "Raw URLs to download into the models directory.",
            Arity = ArgumentArity.ZeroOrMore,
        };

        var presetOpt = new Option<string[]>("--preset", "-p")
        {
            Description = $"Download a model preset by name ({string.Join(", ", Presets.Keys)}). Repeatable.",
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = true,
        };
        presetOpt.AcceptOnlyFromAmong(Presets.Keys.ToArray());

        var allOpt = new Option<bool>("--all", "-a")
        {
            Description = "Download all model presets.",
        };

        var dirOpt = new Option<string?>("--dir", "-d")
        {
            Description = "Destination directory (default: the configured models directory).",
        };

        var cmd = new Command("download", "Download model files into the models directory (resumable).")
        {
            urlArg, presetOpt, allOpt, dirOpt,
        };

        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var urls = parseResult.GetValue(urlArg) ?? [];
            var presetKeys = parseResult.GetValue(presetOpt) ?? [];
            var all = parseResult.GetValue(allOpt);

            var modelsDir = parseResult.GetValue(dirOpt)
                ?? Environment.GetEnvironmentVariable("MODELS_DIR")
                ?? services.GetRequiredService<IOptions<ImageGenOptions>>().Value.ModelsDir;

            // Assemble the download set from presets, --all, and raw URLs.
            var items = ResolveDownloadItems(urls, presetKeys, all, modelsDir, out var hadError);
            if (hadError)
                return 1;

            if (items.Count == 0)
            {
                AnsiConsole.MarkupLine(
                    "[yellow]Nothing to download.[/] Provide URLs, [green]--preset <name>[/], or [green]--all[/].");
                return 1;
            }

            var queue = services.GetRequiredService<DownloadQueue>();

            // Spectre's Progress owns the live display; each job's IProgress hook
            // updates its own task bar as the background worker reports bytes.
            var allOk = await AnsiConsole.Progress()
                .AutoClear(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new DownloadedColumn(),
                    new TransferSpeedColumn(),
                    new RemainingTimeColumn(),
                    new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var jobs = new List<WorkJob<DownloadRequest, DownloadProgress, string>>(items.Count);

                    foreach (var (url, destPath, label) in items)
                    {
                        var task = ctx.AddTask(Markup.Escape(label), autoStart: false);
                        var progress = new Progress<DownloadProgress>(p =>
                        {
                            if (p.TotalBytes is { } total)
                            {
                                task.MaxValue = total;
                                task.Value = p.BytesDownloaded;
                                if (!task.IsStarted) task.StartTask();
                            }
                        });

                        var request = new DownloadRequest
                        {
                            Url = url,
                            DestinationPath = destPath,
                            DisplayName = label,
                        };
                        jobs.Add(queue.Enqueue(request, progress));
                    }

                    // Await every enqueued download; the background service does the work.
                    var results = await Task.WhenAll(jobs.Select(async job =>
                    {
                        try { await job.Completion; return true; }
                        catch (Exception ex)
                        {
                            AnsiConsole.MarkupLineInterpolated(
                                $"[red]Failed {job.Request.EffectiveDisplayName}:[/] {ex.Message}");
                            return false;
                        }
                    }));

                    return results.All(ok => ok);
                });

            return allOk ? 0 : 2;
        });

        return cmd;
    }

    private static Command BuildBenchmarkCommand(IServiceProvider services)
    {
        var presetOpt = new Option<string[]>("--preset", "-p")
        {
            Description = $"Presets to benchmark ({string.Join(", ", Presets.Keys)}). Repeatable. Default: all.",
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = true,
        };
        presetOpt.AcceptOnlyFromAmong(Presets.Keys.ToArray());

        var outOpt = new Option<string?>("--out", "-o")
        {
            Description = "Report file path (default: a timestamped .md under reports/).",
        };
        var runsOpt = new Option<int>("--runs")
        {
            Description = "Timed runs per preset.", DefaultValueFactory = _ => 3,
        };
        var warmupOpt = new Option<int>("--warmup")
        {
            Description = "Warm-up runs per preset (discarded).", DefaultValueFactory = _ => 1,
        };
        var promptOpt = new Option<string>("--prompt")
        {
            Description = "Prompt to generate.", DefaultValueFactory = _ => "a red fox in a snowy forest",
        };
        var seedOpt = new Option<long>("--seed") { Description = "RNG seed.", DefaultValueFactory = _ => 42 };
        var threadsOpt = new Option<int?>("--threads") { Description = "CPU thread count (default: all cores)." };
        var sizeOpt = new Option<string?>("--size")
        {
            Description = "Force a common size (WxH, e.g. 768x768) so every preset does equal pixel work. " +
                          "Default: each preset's native size.",
        };

        var cmd = new Command("benchmark", "Measure generation performance per preset and write a report.")
        {
            presetOpt, outOpt, runsOpt, warmupOpt, promptOpt, seedOpt, threadsOpt, sizeOpt,
        };

        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var presetKeys = parseResult.GetValue(presetOpt) ?? [];
            var presets = presetKeys.Length == 0
                ? Presets.All
                : presetKeys.Select(Presets.Find).OfType<ModelPreset>().ToList();

            var runs = Math.Max(1, parseResult.GetValue(runsOpt));
            var warmup = Math.Max(0, parseResult.GetValue(warmupOpt));

            (int Width, int Height)? size = null;
            var sizeRaw = parseResult.GetValue(sizeOpt);
            if (!string.IsNullOrWhiteSpace(sizeRaw))
            {
                var parts = sizeRaw.Split('x', 'X');
                if (parts.Length != 2 || !int.TryParse(parts[0], out var w) || !int.TryParse(parts[1], out var h))
                {
                    AnsiConsole.MarkupLine("[red]--size must be WxH, e.g. 768x768.[/]");
                    return 1;
                }
                size = (w, h);
            }

            var options = new BenchmarkOptions
            {
                Presets = presets,
                Prompt = parseResult.GetValue(promptOpt)!,
                Seed = parseResult.GetValue(seedOpt),
                Runs = runs,
                Warmup = warmup,
                Threads = parseResult.GetValue(threadsOpt),
                Width = size?.Width,
                Height = size?.Height,
            };

            AnsiConsole.MarkupLineInterpolated(
                $"[grey]Benchmarking {presets.Count} preset(s): {warmup} warm-up + {runs} timed run(s) each.[/]");

            var runner = services.GetRequiredService<BenchmarkRunner>();
            var report = await runner.RunAsync(options, cancellationToken);

            try
            {
                var markdown = MarkdownReportWriter.Render(report);
                var outPath = parseResult.GetValue(outOpt)
                    ?? Path.Combine("reports", $"benchmark-{report.StartedAt:yyyyMMdd-HHmmss}.md");
                var fullPath = Path.GetFullPath(outPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                await File.WriteAllTextAsync(fullPath, markdown, cancellationToken);

                AnsiConsole.MarkupLineInterpolated($"[green]Report written:[/] {fullPath}");
                return 0;
            }
            catch (Exception ex)
            {
                AnsiConsole.WriteException(ex);
                return 3;
            }
        });

        return cmd;
    }

    /// <summary>Expands raw URLs, named presets, and <c>--all</c> into concrete
    /// (url, destination, label) download items, deduplicated by destination.
    /// Preset files are saved under the preset's expected local filename.</summary>
    private static List<(Uri Url, string DestPath, string Label)> ResolveDownloadItems(
        string[] urls, string[] presetKeys, bool all, string modelsDir, out bool hadError)
    {
        hadError = false;
        var items = new List<(Uri, string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(Uri url, string fileName, string label)
        {
            var dest = Path.GetFullPath(Path.Combine(modelsDir, fileName));
            if (seen.Add(dest))
                items.Add((url, dest, label));
        }

        var presets = all
            ? Presets.All
            : presetKeys.Select(Presets.Find).OfType<ModelPreset>().ToList();

        foreach (var preset in presets)
        {
            Add(new Uri(preset.DownloadUrl), preset.FileName, preset.Name);

            // Pull the fixed VAE alongside its model when the preset defines one.
            if (preset is { VaeFileName: { } vaeFile, VaeDownloadUrl: { } vaeUrl })
                Add(new Uri(vaeUrl), vaeFile, $"{preset.Name} VAE");
        }

        foreach (var raw in urls)
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var url))
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Invalid URL:[/] {raw}");
                hadError = true;
                continue;
            }

            var fileName = Path.GetFileName(url.LocalPath);
            Add(url, fileName, fileName);
        }

        return items;
    }
}
