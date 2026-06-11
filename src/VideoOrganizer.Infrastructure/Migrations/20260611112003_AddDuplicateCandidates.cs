using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VideoOrganizer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDuplicateCandidates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "duplicate_candidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VideoAId = table.Column<Guid>(type: "uuid", nullable: false),
                    VideoBId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_duplicate_candidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_duplicate_candidates_videos_VideoAId",
                        column: x => x.VideoAId,
                        principalTable: "videos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_duplicate_candidates_videos_VideoBId",
                        column: x => x.VideoBId,
                        principalTable: "videos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_duplicate_candidates_Status",
                table: "duplicate_candidates",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_duplicate_candidates_VideoAId_VideoBId",
                table: "duplicate_candidates",
                columns: new[] { "VideoAId", "VideoBId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_duplicate_candidates_VideoBId",
                table: "duplicate_candidates",
                column: "VideoBId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "duplicate_candidates");
        }
    }
}
