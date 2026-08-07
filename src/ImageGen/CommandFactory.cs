using System.CommandLine;
using ImageGen.Benchmark;
using ImageGen.Downloads;
using ImageGen.Generation;
using ImageGen.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Spectre.Console;
using StableDiffusion.NET;

namespace ImageGen;

/// <summary>Builds the System.CommandLine command tree and wires actions into
/// DI-resolved services.</summary>
public static class CommandFactory
{
    /// <summary>Builds the root command with all subcommands, resolving services
    /// from <paramref name="services"/>.</summary>
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
        var sizeOpt = new Option<string?>("--size") { Description = "Dimensions as WxH, e.g. 512x512 (ignored with --init)." };
        var initOpt = new Option<string?>("--init", "-i")
        {
            Description = "Input PNG for image-to-image: start from this image instead of noise. " +
                          "Output size follows the input, so --size is ignored.",
        };
        var strengthOpt = new Option<float>("--strength")
        {
            Description = "Image-to-image denoising strength 0-1 (default 0.75): low restyles " +
                          "subtly, high keeps only loose structure. Only used with --init.",
            DefaultValueFactory = _ => 0.75f,
        };
        var schedulerOpt = new Option<string?>("--scheduler")
        {
            Description = $"Sampling scheduler ({string.Join(", ", Enum.GetNames<Scheduler>())}). " +
                          "Overrides the preset default; AYS suits low step counts.",
        };
        schedulerOpt.AcceptOnlyFromAmong(Enum.GetNames<Scheduler>());
        var countOpt = new Option<int>("--count", "-c")
        {
            Description = "Generate this many images, incrementing the seed each time. " +
                          "Ctrl-C stops after the current image finishes.",
            DefaultValueFactory = _ => 1,
        };
        var previewOpt = new Option<int>("--preview")
        {
            Description = "Save an intermediate preview image every N sampling steps (0 = off).",
            DefaultValueFactory = _ => 0,
        };
        var previewModeOpt = new Option<string>("--preview-mode")
        {
            Description = "Preview decoder: tae (fast, default), vae (accurate/slow), or proj (cheapest).",
            DefaultValueFactory = _ => "tae",
        };
        previewModeOpt.AcceptOnlyFromAmong("tae", "vae", "proj");
        var replOpt = new Option<bool>("--repl")
        {
            Description = "After generating, enter an interactive loop to refine the result with " +
                          "further prompts (each refines the previous output via img2img). Ignored with --count.",
        };

        var cmd = new Command("generate", "Generate an image from a text prompt.")
        {
            presetArg, promptArg, outOpt, seedOpt, stepsOpt, cfgOpt, negOpt, threadsOpt, sizeOpt,
            initOpt, strengthOpt, schedulerOpt, countOpt, previewOpt, previewModeOpt, replOpt,
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

            var previewMode = parseResult.GetValue(previewModeOpt) switch
            {
                "vae" => Preview.VAE,
                "proj" => Preview.Proj,
                _ => Preview.TAE,
            };

            Scheduler? scheduler = parseResult.GetValue(schedulerOpt) is { } schedRaw
                && Enum.TryParse<Scheduler>(schedRaw, out var s) ? s : null;

            var count = Math.Max(1, parseResult.GetValue(countOpt));
            var explicitOut = parseResult.GetValue(outOpt);
            var baseSeed = parseResult.GetValue(seedOpt);

            // For a batch, an explicit --out would collide across images; fall back to
            // timestamped per-seed names and note it.
            if (count > 1 && explicitOut is not null)
            {
                AnsiConsole.MarkupLine("[yellow]--out is ignored with --count > 1; using timestamped names.[/]");
                explicitOut = null;
            }

            GenerationRequest MakeRequest(long seed) => new()
            {
                Preset = preset,
                Prompt = parseResult.GetValue(promptArg)!,
                NegativePrompt = parseResult.GetValue(negOpt),
                Width = size?.Width,
                Height = size?.Height,
                Steps = parseResult.GetValue(stepsOpt),
                Cfg = parseResult.GetValue(cfgOpt),
                Scheduler = scheduler,
                Seed = seed,
                Threads = parseResult.GetValue(threadsOpt),
                OutputPath = explicitOut,
                InitImagePath = parseResult.GetValue(initOpt),
                Strength = parseResult.GetValue(strengthOpt),
                PreviewInterval = Math.Max(0, parseResult.GetValue(previewOpt)),
                PreviewMode = previewMode,
            };

            PrintRequestSummary(MakeRequest(baseSeed));
            if (count > 1)
                AnsiConsole.MarkupLineInterpolated(
                    $"[grey]Batch of {count}, seeds {baseSeed}–{baseSeed + count - 1}. Ctrl-C stops after the current image.[/]");

            var queue = services.GetRequiredService<GenerationQueue>();

            // Runs a single request under a live progress bar and returns its result,
            // or null on failure. The bar is driven by the engine's step-progress hook.
            //
            // Generation saturates every CPU core, so we can't rely on Spectre's
            // auto-refresh thread getting scheduled. AutoRefresh is disabled and we
            // redraw synchronously from the native progress callback (see
            // SynchronousProgress) — the one thread guaranteed to have the CPU.
            async Task<GenerationResult?> RunOneAsync(GenerationRequest request, string label)
            {
                GenerationResult? result = null;
                await AnsiConsole.Progress()
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
                        var task = ctx.AddTask($"Generating ({label})", autoStart: true, maxValue: request.EffectiveSteps);
                        var progress = new SynchronousProgress<GenerationProgress>(p =>
                        {
                            task.Description = p.Phase == GenerationPhase.LoadingModel
                                ? $"Loading model ({label})"
                                : $"Sampling ({label})";
                            task.MaxValue = p.TotalSteps;
                            task.Value = p.Step;
                            ctx.Refresh();
                        });

                        var job = queue.Enqueue(request, progress);
                        try
                        {
                            var res = await job.Completion;
                            task.Value = task.MaxValue;
                            ctx.Refresh();
                            result = res;
                        }
                        catch (Exception ex)
                        {
                            ctx.Refresh();
                            AnsiConsole.MarkupLineInterpolated($"[red]Generation failed (seed {request.Seed}):[/] {ex.Message}");
                        }
                    });

                if (result is { } r)
                {
                    AnsiConsole.MarkupLineInterpolated($"[green]Saved:[/] {r.OutputPath}");
                    if (r.PreviewDirectory is { } previewDir)
                        AnsiConsole.MarkupLineInterpolated($"[grey]Preview frames:[/] {previewDir}");
                    AnsiConsole.MarkupLineInterpolated(
                        $"[grey]load {r.LoadSeconds:F1}s + gen {r.GenerationSeconds:F1}s[/]");
                }
                return result;
            }

            var completed = 0;
            var canceled = false;
            GenerationResult? last = null;

            for (var i = 0; i < count; i++)
            {
                // Cancellation can't interrupt an in-flight native GenerateImage,
                // but it stops us from starting the next image in a batch.
                if (cancellationToken.IsCancellationRequested)
                {
                    canceled = true;
                    break;
                }

                var request = MakeRequest(baseSeed + i);
                var label = count > 1 ? $"{preset.Key} #{i + 1}/{count} (seed {request.Seed})" : preset.Key;
                if (await RunOneAsync(request, label) is { } res)
                {
                    completed++;
                    last = res;
                }
            }

            if (canceled)
                AnsiConsole.MarkupLineInterpolated(
                    $"[yellow]Canceled.[/] Completed {completed}/{count} before stopping.");
            else if (count > 1)
                AnsiConsole.MarkupLineInterpolated($"[green]Batch done:[/] {completed}/{count} image(s).");

            // Interactive refinement loop (single-image runs only). Each prompt refines
            // the previous output via img2img.
            if (parseResult.GetValue(replOpt))
            {
                if (count > 1)
                    AnsiConsole.MarkupLine("[yellow]--repl is ignored with --count > 1.[/]");
                else if (!canceled && last is { } seed)
                {
                    var refinement = services.GetRequiredService<IOptions<ImageGenOptions>>().Value.Refinement;
                    await RunRefinementLoopAsync(
                        preset, seed, baseSeed, refinement, RunOneAsync, cancellationToken);
                }
            }

            return completed == 0 ? 2 : 0;
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
        if (r.IsImageToImage)
            table.AddRow("Init / Strength", $"{Markup.Escape(r.InitImagePath!)} @ {r.Strength}");
        else
            table.AddRow("Size", $"{r.EffectiveWidth}x{r.EffectiveHeight}");
        table.AddRow("Steps / CFG", $"{r.EffectiveSteps} / {r.EffectiveCfg}");
        table.AddRow("Sampler / Scheduler", $"{r.Preset.Sampler} / {r.EffectiveScheduler}");
        table.AddRow("Seed / Threads", $"{r.Seed} / {r.EffectiveThreads}");
        table.AddRow("Prompt", Markup.Escape(r.Prompt));
        if (!string.IsNullOrEmpty(r.EffectiveNegativePrompt))
            table.AddRow("Negative", Markup.Escape(r.EffectiveNegativePrompt));
        AnsiConsole.Write(table);
    }

    /// <summary>Interactive refinement console. A bare line is a prompt that refines the
    /// previous output via img2img; a <c>/command</c> adjusts a setting without generating.
    /// Exits on an empty line, "exit"/"quit", or Ctrl-C.</summary>
    private static async Task RunRefinementLoopAsync(
        ModelPreset preset,
        GenerationResult seedImage,
        long startSeed,
        RefinementOptions options,
        Func<GenerationRequest, string, Task<GenerationResult?>> runOne,
        CancellationToken cancellationToken)
    {
        // Mutable per-session state, seeded from config; each is adjustable via a command.
        var state = new RefineState
        {
            Strength = Math.Clamp(options.Strength, 0f, 1f),
            Steps = options.Steps > 0 ? options.Steps : preset.Steps,
            Cfg = options.Cfg,
            NegativePrompt = options.NegativePrompt,
            Scheduler = ParseScheduler(options.Scheduler),
            Seed = startSeed,
            RandomSeedEachRound = options.RandomSeedEachRound,
        };
        var rng = new Random();
        var currentImage = seedImage.OutputPath;
        string? lastPrompt = null;

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Refinement console.[/] Type a prompt to refine the image; " +
            "[grey]/help[/] for commands. Empty line, [grey]exit[/], or [grey]Ctrl-C[/] to quit.");
        PrintRefineState(state, preset);

        while (true)
        {
            string line;
            try
            {
                line = await AnsiConsole.PromptAsync(
                    new TextPrompt<string>("[green]refine[/]>").AllowEmpty(), cancellationToken);
            }
            catch (OperationCanceledException) { break; } // Ctrl-C
            catch (InvalidOperationException)
            {
                AnsiConsole.MarkupLine("[yellow]Refinement mode needs an interactive terminal; skipping.[/]");
                break;
            }

            line = line.Trim();
            if (line.Length == 0 || line is "exit" or "quit")
                break;

            // Slash-command: adjust state, don't generate.
            if (line.StartsWith('/'))
            {
                if (HandleRefineCommand(line, state, preset, out var reroll))
                {
                    if (reroll && lastPrompt is { } lp)
                    {
                        state.Seed = rng.Next();
                        await RunRoundAsync(lp);
                    }
                    else if (reroll)
                    {
                        AnsiConsole.MarkupLine("[yellow]Nothing to reroll yet — run a prompt first.[/]");
                    }
                }
                continue;
            }

            lastPrompt = line;
            if (state.RandomSeedEachRound)
                state.Seed = rng.Next();
            await RunRoundAsync(line);
        }

        AnsiConsole.MarkupLine("[grey]Left refinement mode.[/]");
        return;

        // Runs one refinement round from the current state and chains the output.
        async Task RunRoundAsync(string prompt)
        {
            var effective = state.Strength * state.Steps;
            if (options.CoarseStrengthStepThreshold > 0 && effective < options.CoarseStrengthStepThreshold)
                AnsiConsole.MarkupLineInterpolated(
                    $"[yellow]  note:[/] [grey]~{effective:F1} effective steps — strength control is coarse here; raise steps with /steps.[/]");

            var request = new GenerationRequest
            {
                Preset = preset,
                Prompt = prompt,
                InitImagePath = currentImage,
                Strength = state.Strength,
                Steps = state.Steps,
                Cfg = state.Cfg,
                NegativePrompt = state.NegativePrompt,
                Scheduler = state.Scheduler,
                Seed = state.Seed,
            };

            var result = await runOne(request, $"{preset.Key} refine (seed {state.Seed})");
            if (result is { } r)
                currentImage = r.OutputPath; // chain: next round builds on this one
        }
    }

    /// <summary>Mutable state for a refinement session.</summary>
    private sealed class RefineState
    {
        public float Strength;
        public int Steps;
        public float? Cfg;
        public string? NegativePrompt;
        public Scheduler? Scheduler;
        public long Seed;
        public bool RandomSeedEachRound;
    }

    /// <summary>Handles a <c>/command</c> line, mutating <paramref name="state"/>. Returns
    /// true if the line was a recognized command; sets <paramref name="reroll"/> when the
    /// command was <c>/reroll</c>.</summary>
    private static bool HandleRefineCommand(string line, RefineState state, ModelPreset preset, out bool reroll)
    {
        reroll = false;
        var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
        var cmd = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1] : "";

        switch (cmd)
        {
            case "/help":
                AnsiConsole.MarkupLine(
                    "[grey]Commands: /strength <0-1>, /steps <n>, /cfg <f>, /neg <text> (blank clears), " +
                    "/scheduler <name>, /seed <n>, /random on|off, /reroll, /show, /help[/]");
                break;
            case "/show":
                PrintRefineState(state, preset);
                break;
            case "/strength" when float.TryParse(arg, out var s) && s is >= 0f and <= 1f:
                state.Strength = s; break;
            case "/steps" when int.TryParse(arg, out var n) && n > 0:
                state.Steps = n; break;
            case "/cfg" when float.TryParse(arg, out var c) && c >= 0f:
                state.Cfg = c; break;
            case "/neg":
                state.NegativePrompt = arg.Length == 0 ? null : arg; break;
            case "/scheduler" when Enum.TryParse<Scheduler>(arg, ignoreCase: true, out var sch):
                state.Scheduler = sch; break;
            case "/seed" when long.TryParse(arg, out var seed):
                state.Seed = seed; state.RandomSeedEachRound = false; break;
            case "/random" when arg is "on" or "off":
                state.RandomSeedEachRound = arg == "on"; break;
            case "/reroll":
                reroll = true; break;
            default:
                AnsiConsole.MarkupLineInterpolated($"[red]Unknown or invalid command:[/] {Markup.Escape(line)}  [grey](/help)[/]");
                return true;
        }
        return true;
    }

    private static void PrintRefineState(RefineState s, ModelPreset preset)
    {
        var cfg = s.Cfg?.ToString() ?? $"{preset.Cfg} (preset)";
        var sched = s.Scheduler?.ToString() ?? $"{preset.Scheduler} (preset)";
        var neg = string.IsNullOrEmpty(s.NegativePrompt) ? "(none)" : s.NegativePrompt;
        var seed = s.RandomSeedEachRound ? "random each round" : s.Seed.ToString();
        AnsiConsole.MarkupLineInterpolated(
            $"[grey]strength {s.Strength} · steps {s.Steps} · cfg {cfg} · scheduler {sched} · seed {seed} · neg {neg}[/]");
    }

    /// <summary>Parses a scheduler name (case-insensitive), or null if unset/invalid.</summary>
    private static Scheduler? ParseScheduler(string? name) =>
        !string.IsNullOrWhiteSpace(name) && Enum.TryParse<Scheduler>(name, ignoreCase: true, out var s) ? s : null;

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
