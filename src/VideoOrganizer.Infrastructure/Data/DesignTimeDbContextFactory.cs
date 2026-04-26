using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace VideoOrganizer.Infrastructure.Data;

// Lets `dotnet ef migrations add/...` construct the DbContext without going
// through the API's Program.cs (which tries to download FFmpeg and apply
// migrations eagerly — unsuitable for design-time tooling).
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<VideoOrganizerDbContext>
{
    public VideoOrganizerDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("VIDEOORGANIZER_DESIGN_CS")
            ?? "Host=localhost;Port=5432;Database=videoorganizer;Username=postgresuser;Password=placeholder";

        var builder = new DbContextOptionsBuilder<VideoOrganizerDbContext>()
            .UseNpgsql(connectionString);

        return new VideoOrganizerDbContext(builder.Options);
    }
}
