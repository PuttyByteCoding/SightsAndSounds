using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VideoOrganizer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTagGroupTextFormat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TextFormat",
                table: "tag_groups",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "NoFormatting");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TextFormat",
                table: "tag_groups");
        }
    }
}
