using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using VideoOrganizer.API.Services;
using VideoOrganizer.Infrastructure.Data;
using Xunit;

namespace VideoOrganizer.Tests.Fixtures;

/// <summary>
/// Boots the real API (WebApplicationFactory&lt;Program&gt;) against a throwaway
/// Postgres container, so endpoint + EF behaviour is exercised against an actual
/// database — migrations applied fresh, real SQL, real trigram indexes.
///
/// Hermetic: the container is ephemeral and torn down after the run; nothing is
/// committed and no real data is touched. The startup ffmpeg download is
/// neutralized by seeding the lookup dir with the system binaries (Program.cs
/// skips the fetch when they're already present), and the backup directory is a
/// temp dir. If Docker isn't available the suite reports <see cref="Available"/>
/// = false and the tests skip rather than fail.
/// </summary>
public sealed class PostgresApiFixture : IAsyncLifetime
{
    public bool Available { get; private set; }
    public string? SkipReason { get; private set; }

    public WebApplicationFactory<Program> Factory { get; private set; } = default!;
    public HttpClient Client { get; private set; } = default!;
    public string BackupDir { get; private set; } = string.Empty;

    private PostgreSqlContainer? _pg;

    public async Task InitializeAsync()
    {
#pragma warning disable CS0618 // parameterless ctor is obsolete; we still set the image explicitly via WithImage
        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("videoorganizer_test")
            .WithUsername("testuser")
            .WithPassword("testpass")
            .Build();
#pragma warning restore CS0618

        try
        {
            await _pg.StartAsync();
        }
        catch (Exception ex)
        {
            // No Docker daemon (or it's unreachable) — skip rather than fail.
            Available = false;
            SkipReason = "Docker not available for Testcontainers: " + ex.Message;
            return;
        }

        // The API builds its connection string from POSTGRES_* env vars at
        // startup, and Required() throws if they're unset — so they must be in
        // place before the host boots. Point them at the container.
        Environment.SetEnvironmentVariable("POSTGRES_HOST", _pg.Hostname);
        Environment.SetEnvironmentVariable("POSTGRES_PORT", _pg.GetMappedPublicPort(5432).ToString());
        Environment.SetEnvironmentVariable("POSTGRES_DB", "videoorganizer_test");
        Environment.SetEnvironmentVariable("POSTGRES_USER", "testuser");
        Environment.SetEnvironmentVariable("POSTGRES_PASSWORD", "testpass");

        // Backups (and the pre-restore safety snapshot) write here.
        BackupDir = Path.Combine(Path.GetTempPath(), "sas-backup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(BackupDir);
        Environment.SetEnvironmentVariable("Backup__Directory", BackupDir);

        // Shared ffmpeg setup: seeds <BaseDir>/ffmpeg with symlinks so the app's
        // startup ffmpeg download is skipped, and everyone agrees on one location
        // (see TestFfmpeg / issue #106).
        TestFfmpeg.Ensure();

        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureServices(services =>
                {
                    // Drop the thumbnail + Md5 background workers (they'd add a
                    // ~10s startup delay and idle DB churn), but KEEP the import
                    // queue so the end-to-end import test can actually run — the
                    // import endpoint just enqueues; ImportQueueService consumes.
                    services.RemoveAll<IHostedService>();
                    services.AddHostedService(sp => sp.GetRequiredService<ImportQueueService>());
                });
            });

        // First client creation triggers the host build → migrations apply
        // against the container.
        Client = Factory.CreateClient();
        Available = true;
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Factory is not null) await Factory.DisposeAsync();
        if (_pg is not null) await _pg.DisposeAsync();
        try { if (BackupDir.Length > 0 && Directory.Exists(BackupDir)) Directory.Delete(BackupDir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    /// <summary>Run work against a fresh DbContext scope (seed or assert).</summary>
    public async Task WithDbAsync(Func<VideoOrganizerDbContext, Task> work)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VideoOrganizerDbContext>();
        await work(db);
    }

}

[CollectionDefinition("PostgresApi")]
public sealed class PostgresApiCollection : ICollectionFixture<PostgresApiFixture> { }
