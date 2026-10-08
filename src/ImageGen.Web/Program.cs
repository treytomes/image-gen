using ImageGen;
using ImageGen.Downloads;
using ImageGen.Generation;
using ImageGen.Imaging;
using Microsoft.Extensions.Options;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddYamlFile("appsettings.yaml", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables(prefix: "IMAGEGEN_");

builder.Services.Configure<ImageGenOptions>(
    builder.Configuration.GetSection(ImageGenOptions.SectionName));

builder.Services.AddSerilog((services, cfg) => cfg
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

// Imaging: PNG encode/decode
builder.Services.AddSingleton<IImageCodec, Png>();

// Generation stack
builder.Services.AddSingleton<ImageGenerationService>();
builder.Services.AddSingleton<GenerationQueue>();
builder.Services.AddHostedService<GenerationBackgroundService>();

// Download stack
builder.Services.AddSingleton<DownloadQueue>();
builder.Services.AddHttpClient<FileDownloader>(client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddHostedService<DownloadBackgroundService>();

// Blazor Server
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

await app.RunAsync();
