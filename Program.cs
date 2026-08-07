using System.CommandLine;
using ImageGen;
using ImageGen.Downloads;
using ImageGen.Generation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Spectre.Console;

// ---------------------------------------------------------------------------
// Composition root: build the Generic Host (config + DI + Serilog), then parse
// the command line with System.CommandLine and dispatch into DI-resolved
// services.
// ---------------------------------------------------------------------------

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .AddYamlFile("appsettings.yaml", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables(prefix: "IMAGEGEN_");

builder.Services.Configure<ImageGenOptions>(
    builder.Configuration.GetSection(ImageGenOptions.SectionName));

// Serilog: console + rolling file, configured from appsettings.json.
builder.Services.AddSerilog((services, cfg) => cfg
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

// Generation stack: the engine, a queue, and the background worker that drains it.
builder.Services.AddSingleton<ImageGenerationService>();
builder.Services.AddSingleton<GenerationQueue>();
builder.Services.AddHostedService<GenerationBackgroundService>();
builder.Services.AddSingleton<ImageGen.Benchmark.BenchmarkRunner>();

// Download stack: a queue, the resumable downloader, and the background worker
// service that drains the queue.
builder.Services.AddSingleton<DownloadQueue>();
builder.Services.AddHttpClient<FileDownloader>(client =>
{
    // Model hosts (e.g. Hugging Face) can be slow to first-byte on large files.
    client.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddHostedService<DownloadBackgroundService>();

using var host = builder.Build();

// Start the host so hosted services (the download and generation workers) run,
// then dispatch the command. Stopping the host lets queues drain and flushes logs.
await host.StartAsync();
try
{
    // Ctrl-C handling. A single native GenerateImage call cannot be interrupted
    // mid-run (verified: exceptions thrown from its callbacks are swallowed). We own
    // the signal end-to-end rather than relying on System.CommandLine's handler, so
    // the behavior is unambiguous:
    //   1st Ctrl-C — cancel our token (a batch stops before its next image) and tell
    //                the user the current image must finish. e.Cancel = true keeps the
    //                process alive so that can happen.
    //   2nd Ctrl-C — quit immediately via Environment.Exit. The event fires on a
    //                runtime thread independent of the main thread, so this works even
    //                while the main thread is blocked inside the native call.
    using var cancellation = new CancellationTokenSource();
    var ctrlCPresses = 0;
    Console.CancelKeyPress += (_, e) =>
    {
        if (Interlocked.Increment(ref ctrlCPresses) == 1)
        {
            e.Cancel = true;
            cancellation.Cancel();
            Console.Error.WriteLine();
            Console.Error.WriteLine(
                "Stopping. The image currently rendering can't be interrupted and will " +
                "finish first (this may take a while). A batch will stop before its next " +
                "image. Press Ctrl-C again to quit immediately and discard it.");
        }
        else
        {
            Console.Error.WriteLine("Quitting immediately.");
            // Environment.Exit skips the finally block's Log.CloseAndFlushAsync, so
            // flush Serilog's buffered sinks here to preserve the run's log tail.
            // Guard it — a flush failure must not block the exit the user asked for.
            try { Log.CloseAndFlush(); } catch { /* exiting regardless */ }
            Environment.Exit(130); // 128 + SIGINT, conventional for Ctrl-C
        }
    };

    var rootCommand = CommandFactory.Build(host.Services);

    // Disable System.CommandLine's own Ctrl-C handling (ProcessTerminationTimeout:
    // null) so it doesn't race our handler; our CancellationTokenSource drives
    // cancellation instead.
    var invocation = new InvocationConfiguration { ProcessTerminationTimeout = null };
    return await rootCommand.Parse(args).InvokeAsync(invocation, cancellation.Token);
}
catch (Exception ex)
{
    Log.Fatal(ex, "Unhandled exception");
    AnsiConsole.WriteException(ex);
    return 1;
}
finally
{
    await host.StopAsync();
    await Log.CloseAndFlushAsync();
}
