using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VideoOrganizer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchTrigramIndexes : Migration
    {
        // Search index support for the /api/search endpoint.
        //
        // pg_trgm + GIN trigram indexes are what make `ILIKE '%foo%'` stay
        // subsecond as the videos table grows. Without these, a substring
        // scan against a 100k-row table is a sequential table scan (a few
        // hundred ms on warm cache, much worse on cold); with the GIN
        // trigram index Postgres rewrites the ILIKE into an index scan
        // that's a few ms even at that size.
        //
        // Indexes are scoped to the four free-text fields the search
        // endpoint queries:
        //   · FileName  — bare filename (255 chars max)
        //   · FilePath  — absolute path (4096 max)
        //   · Notes     — freeform user notes (4096 max)
        //   · Md5       — hex digest (32 chars). Pasted-hash lookups are
        //                 the main use case; trigram lets it match on
        //                 any substring, not just prefix.
        //
        // None of these are EF-managed (HasIndex doesn't know about
        // gin_trgm_ops), so they live as raw SQL in this migration and
        // do NOT appear in the model snapshot.
        //
        // Tag.Name / Tag.Aliases / VideoSet.Name / TagGroup.Name etc.
        // intentionally have NO trigram indexes yet — v1 of the search
        // endpoint only returns video results. Add a follow-up migration
        // when those other result kinds are wired up.
        //
        // The extension is left in place on Down: other features may
        // depend on it later, and dropping it would error if any other
        // index uses it. Dropping the indexes themselves is the
        // appropriate reverse step.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS ix_videos_filename_trgm " +
                "ON videos USING GIN (\"FileName\" gin_trgm_ops);");

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS ix_videos_filepath_trgm " +
                "ON videos USING GIN (\"FilePath\" gin_trgm_ops);");

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS ix_videos_notes_trgm " +
                "ON videos USING GIN (\"Notes\" gin_trgm_ops);");

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS ix_videos_md5_trgm " +
                "ON videos USING GIN (\"Md5\" gin_trgm_ops);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_videos_md5_trgm;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_videos_notes_trgm;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_videos_filepath_trgm;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_videos_filename_trgm;");
            // pg_trgm extension intentionally left in place — see Up().
        }
    }
}
