using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using VideoOrganizer.Infrastructure.Data;
using Npgsql.EntityFrameworkCore.PostgreSQL;
using VideoOrganizer.API;
using VideoOrganizer.API.Services;
using VideoOrganizer.Shared.Configuration;
using VideoOrganizer.Domain.Models;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;

var builder = WebApplication.CreateBuilder(args);

// 48-hour in-memory log buffer feeding the /logs page. Registered before
// any other services log so we don't miss startup events.
var logBuffer = new VideoOrganizer.API.Services.LogBuffer { Retention = TimeSpan.FromHours(48) };
builder.Services.AddSingleton(logBuffer);
builder.Logging.AddProvider(new VideoOrganizer.API.Services.BufferedLoggerProvider(logBuffer));

// Forward structured logs to Seq in Development when Logging:Seq:ServerUrl is
// configured. The URL lives in `appsettings.Development.json` (gitignored —
// see `appsettings.Development.example.json` for the template). Hard-gated on
// IsDevelopment() so a stray ServerUrl in production config can never ship
// logs off-host. We capture the decision into a local so the actual sink
// status gets logged once the logger pipeline is built (see migration scope
// below) — without that, an operator with a misconfigured URL has no signal
// that logs aren't reaching Seq.
string? seqSinkUrl = null;
string? seqSinkSkipReason = null;
if (builder.Environment.IsDevelopment())
{
    seqSinkUrl = builder.Configuration["Logging:Seq:ServerUrl"];
    if (!string.IsNullOrWhiteSpace(seqSinkUrl))
    {
        builder.Logging.AddSeq(builder.Configuration.GetSection("Logging:Seq"));
    }
    else
    {
        seqSinkSkipReason = "Logging:Seq:ServerUrl not configured in appsettings.Development.json";
    }
}
else
{
    seqSinkSkipReason = $"environment is {builder.Environment.EnvironmentName} (Seq is dev-only)";
}

// Add services to the container.

// Configure JSON options for minimal APIs
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    // Use lenient converter factory for all enum types to handle case-insensitive parsing and numeric values
    options.SerializerOptions.Converters.Add(new LenientEnumConverterFactory());
    options.SerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
});

// Also configure JSON options globally for consistency
builder.Services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(options =>
{
    options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    // Use lenient converter factory for all enum types
    options.JsonSerializerOptions.Converters.Add(new LenientEnumConverterFactory());
    options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
});

// Auto-load `.env` so `dotnet run` works in PowerShell / cmd / Visual Studio
// without anyone having to source the file manually first. Walks up from the
// current directory until it finds a .env (so running from anywhere in the
// tree works). Already-set env vars take precedence — explicit env wins.
LoadDotEnv();
static void LoadDotEnv()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null)
    {
        var path = Path.Combine(dir.FullName, ".env");
        if (File.Exists(path))
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq].Trim();
                var value = line[(eq + 1)..].Trim();
                if ((value.StartsWith('"') && value.EndsWith('"')) ||
                    (value.StartsWith('\'') && value.EndsWith('\'')))
                {
                    value = value[1..^1];
                }
                if (Environment.GetEnvironmentVariable(key) is null)
                {
                    Environment.SetEnvironmentVariable(key, value);
                }
            }
            return;
        }
        dir = dir.Parent;
    }
}

// Build the postgres connection string from environment variables. .env
// (gitignored) is the single source of truth for credentials. The auto-loader
// above pulls them in for native `dotnet run`; docker compose loads its own
// copy directly. Failing fast on a missing user/password is better than a
// confusing connection refused later.
static string Required(string name) =>
    Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException(
            $"{name} is not set. Add it to .env (see .env.example) or set it in your environment before running the API.");

var connectionString = string.Join(';',
    $"Host={Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "localhost"}",
    $"Port={Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? "5432"}",
    $"Database={Required("POSTGRES_DB")}",
    $"Username={Required("POSTGRES_USER")}",
    $"Password={Required("POSTGRES_PASSWORD")}");

builder.Services.AddDbContext<VideoOrganizerDbContext>(options => options
    .UseNpgsql(connectionString)
    // On first startup against a fresh DB, EF queries the __EFMigrationsHistory
    // table before it exists, logs a scary Error-level line, then recovers and
    // creates everything. Demote this specific recoverable event to Warning so
    // the startup log is less alarming.
    .ConfigureWarnings(w => w.Log((Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.CommandError, LogLevel.Warning)))
);

// Register video storage configuration
builder.Services.Configure<VideoStorageOptions>(
    builder.Configuration.GetSection("VideoStorage"));

builder.Services.AddSingleton<VideoStorageOptions>(sp =>
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<VideoStorageOptions>>().Value);

// Tunables for the Thumbnail + Md5 background workers. Bound from the
// "BackgroundWorkers" section; defaults match the original constants when
// the section is absent.
builder.Services.Configure<BackgroundWorkerOptions>(
    builder.Configuration.GetSection("BackgroundWorkers"));
builder.Services.AddSingleton<BackgroundWorkerOptions>(sp =>
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BackgroundWorkerOptions>>().Value);

builder.Services.AddSingleton<VideoOrganizer.Import.Services.IVideoMetadataService, VideoOrganizer.Import.Services.FfprobeVideoMetadataService>();
builder.Services.AddScoped<VideoOrganizer.Import.Services.DirectoryImportService>();

// Register thumbnail generator service
builder.Services.AddSingleton<IThumbnailGenerator, ThumbnailGenerator>();

// Background worker that pre-generates scrub sprites for videos so the first
// hover in the Video Browser doesn't have to wait on ffmpeg. The signal lets
// the import endpoint wake the service immediately when a batch lands
// (instead of waiting up to RescanInterval for the next scan).
builder.Services.AddSingleton<ThumbnailWarmingSignal>();
builder.Services.AddSingleton<ThumbnailWarmingProgressTracker>();
builder.Services.AddHostedService<ThumbnailWarmingService>();

// Background worker that computes Md5 hashes for videos imported without one,
// and flags cross-video duplicates for user review. Signal wakes the worker
// from its idle sleep when an import finishes; progress tracker exposes the
// currently-hashing file to the UI.
builder.Services.AddSingleton<Md5BackfillProgressTracker>();
builder.Services.AddSingleton<Md5BackfillSignal>();
builder.Services.AddHostedService<Md5BackfillService>();

// Register directory import service
builder.Services.AddScoped<IDirectoryImportService, DirectoryImportService>();

// Track import progress across requests
builder.Services.AddSingleton<ImportProgressTracker>();

// Reads config/tags.seed.json on first run; no-op once tag_groups has rows.
builder.Services.AddScoped<TagSeedService>();
// Centralized pause flags for the three workers. Toggled by
// /api/{worker}/pause + /resume endpoints; checked by each worker before
// processing the next item.
builder.Services.AddSingleton<WorkerPauseStatus>();
// Single-consumer queue that serializes the Add-to-database phase across
// import requests (Import1 finishes saving its Videos before Import2 starts).
builder.Services.AddSingleton<ImportQueueService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ImportQueueService>());

// Add CORS policy
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowUI", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// OpenAPI document generation (first-party, replaces Swashbuckle).
// Scalar wraps it for the interactive UI (mapped below at /swagger).
builder.Services.AddOpenApi(options =>
{
    // Domain.Models and Shared.Dto each define same-named enums
    // (CameraTypes, VideoQuality, VideoCodec, VideoDimensionFormat).
    // The default schema-id strategy uses Type.Name, so the two
    // collide and produce a broken document with $refs pointing at
    // the wrong schema. Disambiguate on FullName instead — '.' isn't
    // a legal char inside a $ref path so we replace it with '_'.
    options.CreateSchemaReferenceId = jsonTypeInfo =>
        jsonTypeInfo.Type.FullName?.Replace('.', '_') ?? jsonTypeInfo.Type.Name;

    // The default Info.Title is the assembly name, which renders as the
    // unhelpful bare "API" header in Scalar. Set something meaningful so
    // the UI and any spec consumer (codegen, Postman import, etc.) get a
    // sensible identifier.
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "VideoOrganizer API";
        document.Info.Version = "v1";
        document.Info.Description =
            "Self-hosted media library manager — videos, tags, custom properties, " +
            "background workers (thumbnail sprites + Md5 dedup), and bulk import.";
        return Task.CompletedTask;
    });
});

var app = builder.Build();

// Download FFmpeg if not already present (needed for thumbnail generation).
// On native Windows the Xabe.FFmpeg downloader doesn't auto-register its install
// path with the runtime, so we pin both the download dir and the lookup dir.
var ffmpegDir = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
Directory.CreateDirectory(ffmpegDir);
FFmpeg.SetExecutablesPath(ffmpegDir);
await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, ffmpegDir);

// Apply any pending migrations at startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<VideoOrganizerDbContext>();
    var startupLog = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    // Heads-up for the "table does not exist" warn line that EF emits when
    // the __EFMigrationsHistory table isn't created yet (always the case on a
    // fresh DB). We can't suppress it outright without losing real SQL-error
    // visibility, so we just describe it.
    if (!string.IsNullOrWhiteSpace(seqSinkUrl))
    {
        startupLog.LogInformation("Seq log sink wired to {SeqUrl}", seqSinkUrl);
    }
    else
    {
        startupLog.LogInformation("Seq log sink disabled — {Reason}", seqSinkSkipReason);
    }

    startupLog.LogInformation(
        "Applying EF migrations. On a fresh database you'll see a one-time warning about " +
        "__EFMigrationsHistory not existing — that's expected and self-heals.");

    db.Database.Migrate();

    // Seed default tag groups + flag tags from config/tags.seed.json on a
    // fresh DB. No-op once any TagGroup row exists.
    var seedSvc = scope.ServiceProvider.GetRequiredService<TagSeedService>();
    await seedSvc.SeedIfEmptyAsync();

    // Seed a "Default" VideoSet from config if the table is empty, so fresh
    // installs aren't missing a root to browse.
    if (!db.VideoSets.Any())
    {
        var storage = scope.ServiceProvider.GetRequiredService<VideoStorageOptions>();
        if (!string.IsNullOrWhiteSpace(storage.Root))
        {
            db.VideoSets.Add(new VideoOrganizer.Domain.Models.VideoSet
            {
                Id = Guid.NewGuid(),
                Name = "Videos",
                Path = VideoOrganizer.Shared.PathNormalizer.Normalize(storage.Root),
                Enabled = true,
                SortOrder = 0
            });
            db.SaveChanges();
        }
    }
}

// Serve the OpenAPI document at /openapi/v1.json (default) and the
// Scalar interactive UI at /swagger so the SvelteKit /api-docs iframe
// and the Vite /swagger proxy keep working without changes.
app.MapOpenApi();
app.MapScalarApiReference("/swagger", options =>
{
    options
        .WithTitle("VideoOrganizer API")
        // Mars is Scalar's dark default; matches the surrounding SvelteKit
        // app's theme. Was previously ~70 lines of inline Swagger UI CSS.
        .WithTheme(ScalarTheme.Mars)
        // Without this, Scalar derives the spec URL from its own mount
        // path (`/swagger/openapi/v1.json`) instead of the actual route
        // MapOpenApi() registered (`/openapi/v1.json`), so the UI loads
        // but renders an empty document.
        .WithOpenApiRoutePattern("/openapi/{documentName}.json");

    // Hide the "Open API Client" button. By default it opens the spec in
    // Scalar's hosted client at client.scalar.com — undesirable for a
    // self-hosted dev tool (sends our local URL to a third-party site).
    // The in-page "Try It Out" panel still works for hands-on testing.
    options.HideClientButton = true;
});

// Disable HTTPS redirection in Development to avoid container redirect issues
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseCors("AllowUI");

app.UseDefaultFiles();
app.UseStaticFiles();

// Structured per-request log for /api/* — gives every endpoint entry/exit
// visibility (method, path, query, status, elapsed) without touching the 70+
// handlers in ApiEndpoints.cs. Scoped to /api so static files and Swagger
// don't drown the log feed. 4xx logs at Information, 5xx at Warning so the
// signal is easy to filter on in Seq.
{
    var requestLog = app.Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("VideoOrganizer.Api.Request");
    app.Use(async (ctx, next) =>
    {
        if (!ctx.Request.Path.StartsWithSegments("/api"))
        {
            await next();
            return;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await next();
        }
        finally
        {
            sw.Stop();
            var status = ctx.Response.StatusCode;
            var level = status >= 500 ? LogLevel.Warning : LogLevel.Information;
            requestLog.Log(level,
                "HTTP {Method} {Path}{Query} -> {StatusCode} in {ElapsedMs}ms",
                ctx.Request.Method,
                ctx.Request.Path.Value,
                ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value : string.Empty,
                status,
                sw.ElapsedMilliseconds);
        }
    });
}

app.MapApiEndpoints();

// SPA fallback: any non-API, non-Swagger, non-file route returns index.html
// so the SvelteKit client-side router can handle it.
app.MapFallbackToFile("index.html");

app.Run();

