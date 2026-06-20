using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VideoOrganizer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOcrTextLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "OcrScannedThroughSeconds",
                table: "videos",
                type: "double precision",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ocr_text_lines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VideoId = table.Column<Guid>(type: "uuid", nullable: false),
                    TimeSeconds = table.Column<double>(type: "double precision", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ocr_text_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ocr_text_lines_videos_VideoId",
                        column: x => x.VideoId,
                        principalTable: "videos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ocr_text_lines_VideoId_TimeSeconds",
                table: "ocr_text_lines",
                columns: new[] { "VideoId", "TimeSeconds" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ocr_text_lines");

            migrationBuilder.DropColumn(
                name: "OcrScannedThroughSeconds",
                table: "videos");
        }
    }
}
