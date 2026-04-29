using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
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

// Swagger/OpenAPI UI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    // Domain.Models and Shared.Dto each define same-named enums (CameraTypes,
    // VideoQuality, VideoBlockTypes). Default schemaId uses just the bare type
    // name, so the two collide and break /swagger/v1/swagger.json. Use full
    // names so each enum lives under its own schemaId.
    c.CustomSchemaIds(t => t.FullName);
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

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "VideoOrganizer API v1");
    c.RoutePrefix = "swagger";
    // Dark theme to match the Svelte app the iframe sits in. Overrides
    // Swagger UI's default light palette via a <style> block injected into
    // the head; keeping it inline avoids shipping a second static file.
    c.HeadContent = """
        <style>
        :root { color-scheme: dark; }
        body, .swagger-ui { background: #1f2937; color: #e5e7eb; }
        .swagger-ui, .swagger-ui .info, .swagger-ui .info .title, .swagger-ui .info p,
        .swagger-ui .opblock-tag, .swagger-ui .opblock-tag small,
        .swagger-ui .opblock .opblock-summary-path, .swagger-ui .opblock .opblock-summary-description,
        .swagger-ui .opblock-description-wrapper p, .swagger-ui .opblock-external-docs-wrapper p,
        .swagger-ui .opblock-title_normal p, .swagger-ui .responses-inner h4, .swagger-ui .responses-inner h5,
        .swagger-ui .parameter__name, .swagger-ui .parameter__type, .swagger-ui .parameter__in,
        .swagger-ui .parameter__deprecated, .swagger-ui .response-col_status, .swagger-ui .response-col_description,
        .swagger-ui table thead tr th, .swagger-ui table thead tr td, .swagger-ui .tab li,
        .swagger-ui .model, .swagger-ui .model-title, .swagger-ui section.models h4,
        .swagger-ui label, .swagger-ui .markdown p, .swagger-ui .renderedMarkdown p,
        .swagger-ui h1, .swagger-ui h2, .swagger-ui h3, .swagger-ui h4, .swagger-ui h5 { color: #e5e7eb; }
        .swagger-ui .topbar { background: #111827; border-bottom: 1px solid #374151; }
        .swagger-ui .scheme-container { background: #1f2937; box-shadow: none; border-bottom: 1px solid #374151; }
        .swagger-ui .opblock { background: #374151; border-color: #4b5563; box-shadow: none; }
        .swagger-ui .opblock .opblock-summary { border-color: #4b5563; }
        .swagger-ui section.models { background: #374151; border-color: #4b5563; }
        .swagger-ui section.models.is-open h4 { border-bottom-color: #4b5563; }
        .swagger-ui .model-box { background: rgba(0,0,0,0.2); }
        .swagger-ui table tbody tr td { border-bottom-color: #4b5563; color: #e5e7eb; }
        .swagger-ui input[type=text], .swagger-ui input[type=email], .swagger-ui input[type=password],
        .swagger-ui input[type=search], .swagger-ui input[type=number], .swagger-ui textarea,
        .swagger-ui select { background: #1f2937; color: #e5e7eb; border-color: #4b5563; }
        .swagger-ui .btn { background: #374151; color: #e5e7eb; border-color: #4b5563; box-shadow: none; }
        .swagger-ui .btn.cancel { background: #4b5563; color: #e5e7eb; border-color: #6b7280; }
        .swagger-ui .btn.execute { background: #6366f1; color: #fff; border-color: #6366f1; }
        .swagger-ui .btn.authorize { background: #374151; color: #34d399; border-color: #34d399; }
        .swagger-ui .markdown code, .swagger-ui .renderedMarkdown code { background: rgba(255,255,255,0.08); color: #f9a8d4; }
        .swagger-ui .highlight-code, .swagger-ui pre, .swagger-ui .microlight { background: #111827 !important; color: #e5e7eb !important; }
        .swagger-ui .response-col_links { color: #9ca3af; }
        .swagger-ui svg:not(:root) { fill: #e5e7eb; }
        .swagger-ui .arrow { fill: #e5e7eb; }
        .swagger-ui .dialog-ux .modal-ux { background: #1f2937; border-color: #4b5563; }
        .swagger-ui .dialog-ux .modal-ux-header h3, .swagger-ui .dialog-ux .modal-ux-content { color: #e5e7eb; }
        .swagger-ui .auth-container { background: #374151; border-color: #4b5563; }
        .swagger-ui .auth-container h4 { color: #e5e7eb; }
        .swagger-ui .errors-wrapper { background: #422; border-color: #d33; }

        /* Verb badges — soften the default loud Swagger colours to match the
           site's dim soft fill + brighter border pattern. Each verb gets a
           tinted bg (/20), a brighter border (/45), and a light text shade.
           The opblock container border picks up the same hue. */
        .swagger-ui .opblock-summary-method { background: transparent !important; min-width: 80px; text-align: center; padding: 4px 0; border-radius: 4px; border: 1px solid; font-weight: 700; }
        .swagger-ui .opblock { border-left-width: 3px; }

        .swagger-ui .opblock.opblock-get { background: rgba(59,130,246,0.05); border-color: rgba(59,130,246,0.45); }
        .swagger-ui .opblock.opblock-get .opblock-summary { border-color: rgba(59,130,246,0.30); }
        .swagger-ui .opblock.opblock-get .opblock-summary-method { background: rgba(59,130,246,0.20) !important; border-color: rgba(59,130,246,0.55); color: rgb(147,197,253); }

        .swagger-ui .opblock.opblock-post { background: rgba(34,197,94,0.05); border-color: rgba(34,197,94,0.45); }
        .swagger-ui .opblock.opblock-post .opblock-summary { border-color: rgba(34,197,94,0.30); }
        .swagger-ui .opblock.opblock-post .opblock-summary-method { background: rgba(34,197,94,0.20) !important; border-color: rgba(34,197,94,0.55); color: rgb(134,239,172); }

        .swagger-ui .opblock.opblock-put { background: rgba(139,92,246,0.05); border-color: rgba(139,92,246,0.45); }
        .swagger-ui .opblock.opblock-put .opblock-summary { border-color: rgba(139,92,246,0.30); }
        .swagger-ui .opblock.opblock-put .opblock-summary-method { background: rgba(139,92,246,0.20) !important; border-color: rgba(139,92,246,0.55); color: rgb(196,181,253); }

        .swagger-ui .opblock.opblock-delete { background: rgba(239,68,68,0.05); border-color: rgba(239,68,68,0.45); }
        .swagger-ui .opblock.opblock-delete .opblock-summary { border-color: rgba(239,68,68,0.30); }
        .swagger-ui .opblock.opblock-delete .opblock-summary-method { background: rgba(239,68,68,0.20) !important; border-color: rgba(239,68,68,0.55); color: rgb(252,165,165); }

        .swagger-ui .opblock.opblock-patch { background: rgba(245,158,11,0.05); border-color: rgba(245,158,11,0.45); }
        .swagger-ui .opblock.opblock-patch .opblock-summary { border-color: rgba(245,158,11,0.30); }
        .swagger-ui .opblock.opblock-patch .opblock-summary-method { background: rgba(245,158,11,0.20) !important; border-color: rgba(245,158,11,0.55); color: rgb(252,211,77); }

        .swagger-ui .opblock.opblock-head, .swagger-ui .opblock.opblock-options { background: rgba(168,162,158,0.05); border-color: rgba(168,162,158,0.45); }
        .swagger-ui .opblock.opblock-head .opblock-summary, .swagger-ui .opblock.opblock-options .opblock-summary { border-color: rgba(168,162,158,0.30); }
        .swagger-ui .opblock.opblock-head .opblock-summary-method, .swagger-ui .opblock.opblock-options .opblock-summary-method { background: rgba(168,162,158,0.20) !important; border-color: rgba(168,162,158,0.55); color: rgb(214,211,209); }
        </style>
        """;
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

