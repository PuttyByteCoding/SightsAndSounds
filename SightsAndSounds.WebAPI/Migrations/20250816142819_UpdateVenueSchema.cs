using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SightsAndSounds.WebAPI.Migrations
{
    /// <inheritdoc />
    public partial class UpdateVenueSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Concerts_Venues_VenueId",
                table: "Concerts");

            migrationBuilder.DropForeignKey(
                name: "FK_Tracks_Songs_SongId",
                table: "Tracks");

            migrationBuilder.DropIndex(
                name: "IX_Concerts_VenueId",
                table: "Concerts");

            migrationBuilder.AddColumn<int>(
                name: "DmbAlmanacId",
                table: "Venues",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DmbAlmanacUrl",
                table: "Venues",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<Guid>(
                name: "SongId",
                table: "Tracks",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AddColumn<string>(
                name: "TrackFileLocation",
                table: "Tracks",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<Guid>(
                name: "VenueId",
                table: "Concerts",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConcertDirPath",
                table: "Concerts",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddForeignKey(
                name: "FK_Tracks_Songs_SongId",
                table: "Tracks",
                column: "SongId",
                principalTable: "Songs",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tracks_Songs_SongId",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "DmbAlmanacId",
                table: "Venues");

            migrationBuilder.DropColumn(
                name: "DmbAlmanacUrl",
                table: "Venues");

            migrationBuilder.DropColumn(
                name: "TrackFileLocation",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "ConcertDirPath",
                table: "Concerts");

            migrationBuilder.AlterColumn<Guid>(
                name: "SongId",
                table: "Tracks",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "VenueId",
                table: "Concerts",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.CreateIndex(
                name: "IX_Concerts_VenueId",
                table: "Concerts",
                column: "VenueId");

            migrationBuilder.AddForeignKey(
                name: "FK_Concerts_Venues_VenueId",
                table: "Concerts",
                column: "VenueId",
                principalTable: "Venues",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Tracks_Songs_SongId",
                table: "Tracks",
                column: "SongId",
                principalTable: "Songs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
