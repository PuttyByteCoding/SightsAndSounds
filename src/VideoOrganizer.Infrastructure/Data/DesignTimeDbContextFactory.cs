using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace VideoOrganizer.Infrastructure.Data;

// Lets `dotnet ef migrations add/...` construct the DbContext without going
// through the API's Program.cs (which tries to download FFmpeg and apply
// migrations eagerly — unsuitable for design-time tooling).
//
// Credentials resolution order — mirrors Program.cs so `dotnet ef` "just
// works" with no env-var dance:
//   1. `VIDEOORGANIZER_DESIGN_CS` env var — full connection string override.
//      Useful for pointing at a non-default db without editing files.
//   2. POSTGRES_HOST / POSTGRES_PORT / POSTGRES_DB / POSTGRES_USER /
//      POSTGRES_PASSWORD env vars — same source of truth the runtime uses.
//      `.env` (gitignored) is auto-loaded if present, so the developer
//      doesn't have to source it manually before running `dotnet ef …`.
//   3. Placeholder localhost connection — final fallback. Will fail auth
//      against a real db, which is intentional: surface the missing creds
//      loud rather than silently connecting somewhere unexpected.
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<VideoOrganizerDbContext>
{
    public VideoOrganizerDbContext CreateDbContext(string[] args)
    {
        // Pull .env into process env vars before we read anything else.
        // Same logic as the API's LoadDotEnv() — walks up from cwd until
        // it finds a .env, sets each KEY=VALUE that isn't already set in
        // the process environment.
        LoadDotEnv();

        var connectionString =
            Environment.GetEnvironmentVariable("VIDEOORGANIZER_DESIGN_CS")
            ?? BuildFromPostgresEnvVars()
            ?? "Host=localhost;Port=5432;Database=videoorganizer;Username=postgresuser;Password=placeholder";

        var builder = new DbContextOptionsBuilder<VideoOrganizerDbContext>()
            .UseNpgsql(connectionString);

        return new VideoOrganizerDbContext(builder.Options);
    }

    private static string? BuildFromPostgresEnvVars()
    {
        // Username + password are the meaningful inputs — without them
        // we'd just emit the placeholder fallback. Host / port / db
        // have safe defaults that mirror Program.cs.
        var user = Environment.GetEnvironmentVariable("POSTGRES_USER");
        var pw   = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD");
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pw)) return null;

        var host = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "localhost";
        var port = Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? "5432";
        var db   = Environment.GetEnvironmentVariable("POSTGRES_DB")   ?? "videoorganizer";

        return string.Join(';',
            $"Host={host}",
            $"Port={port}",
            $"Database={db}",
            $"Username={user}",
            $"Password={pw}");
    }

    private static void LoadDotEnv()
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
                    // Strip surrounding quotes — same shape as Program.cs.
                    if ((value.StartsWith('"') && value.EndsWith('"')) ||
                        (value.StartsWith('\'') && value.EndsWith('\'')))
                    {
                        value = value[1..^1];
                    }
                    // Already-set env vars win — explicit > .env.
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
}
