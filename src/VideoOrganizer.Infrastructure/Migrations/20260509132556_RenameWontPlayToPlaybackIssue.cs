using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VideoOrganizer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RenameWontPlayToPlaybackIssue : Migration
    {
        // The C# property went from `WontPlay` to `PlaybackIssue` and the
        // EF model snapshot was edited in lockstep, so the EF tooling saw
        // a no-op delta and emitted an empty migration. The actual DB
        // column on the videos table still needs the rename — Postgres'
        // ALTER TABLE … RENAME COLUMN is metadata-only (no row rewrite,
        // no lock storm), so this is a safe backwards-compatible move.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "WontPlay",
                table: "videos",
                newName: "PlaybackIssue");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "PlaybackIssue",
                table: "videos",
                newName: "WontPlay");
        }
    }
}
