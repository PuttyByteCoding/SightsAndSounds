using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VideoOrganizer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTagHiddenByDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HiddenByDefault",
                table: "tags",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HiddenByDefault",
                table: "tags");
        }
    }
}
