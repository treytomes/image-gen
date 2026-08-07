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
    var rootCommand = CommandFactory.Build(host.Services);

    // On Ctrl-C, System.CommandLine cancels the action's token, then force-terminates
    // after this timeout. A single native GenerateImage call can't be interrupted
    // mid-run, so we allow generous time for the in-flight image to finish and save
    // before the process is killed — the token stops *further* batch jobs from starting.
    var invocation = new InvocationConfiguration
    {
        ProcessTerminationTimeout = TimeSpan.FromMinutes(20),
    };
    return await rootCommand.Parse(args).InvokeAsync(invocation);
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
