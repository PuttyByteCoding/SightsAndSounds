using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VideoOrganizer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tag_groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AllowMultiple = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    DisplayAsCheckboxes = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    Notes = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tag_groups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "video_sets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_video_sets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "videos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    FilePath = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    Md5 = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Md5Failed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    Md5FailedError = table.Column<string>(type: "text", nullable: true),
                    ThumbnailsFailed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    ThumbnailsFailedError = table.Column<string>(type: "text", nullable: true),
                    ThumbnailsGenerated = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    ImportJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    FileSize = table.Column<long>(type: "bigint", nullable: false),
                    Duration = table.Column<TimeSpan>(type: "interval", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    VideoDimensionFormat = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    VideoCodec = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Bitrate = table.Column<long>(type: "bigint", nullable: false),
                    FrameRate = table.Column<double>(type: "double precision", nullable: false),
                    PixelFormat = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Ratio = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    CreationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VideoStreamCount = table.Column<int>(type: "integer", nullable: false),
                    AudioStreamCount = table.Column<int>(type: "integer", nullable: false),
                    IngestDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    CameraType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    VideoQuality = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    WatchCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    Notes = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    NeedsReview = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    WontPlay = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    MarkedForDeletion = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    IsFavorite = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    ParentVideoId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClipStartSeconds = table.Column<double>(type: "double precision", nullable: true),
                    ClipEndSeconds = table.Column<double>(type: "double precision", nullable: true),
                    ChapterMarkers = table.Column<string>(type: "jsonb", nullable: false),
                    VideoBlocks = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_videos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_videos_videos_ParentVideoId",
                        column: x => x.ParentVideoId,
                        principalTable: "videos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "property_definitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DataType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TagGroupId = table.Column<Guid>(type: "uuid", nullable: true),
                    Required = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    Notes = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_property_definitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_property_definitions_tag_groups_TagGroupId",
                        column: x => x.TagGroupId,
                        principalTable: "tag_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TagGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Aliases = table.Column<string>(type: "jsonb", nullable: false),
                    IsFavorite = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    Notes = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tags_tag_groups_TagGroupId",
                        column: x => x.TagGroupId,
                        principalTable: "tag_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "video_property_values",
                columns: table => new
                {
                    VideoId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Value = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_video_property_values", x => new { x.VideoId, x.PropertyDefinitionId });
                    table.ForeignKey(
                        name: "FK_video_property_values_property_definitions_PropertyDefiniti~",
                        column: x => x.PropertyDefinitionId,
                        principalTable: "property_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_video_property_values_videos_VideoId",
                        column: x => x.VideoId,
                        principalTable: "videos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tag_property_values",
                columns: table => new
                {
                    TagId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Value = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tag_property_values", x => new { x.TagId, x.PropertyDefinitionId });
                    table.ForeignKey(
                        name: "FK_tag_property_values_property_definitions_PropertyDefinition~",
                        column: x => x.PropertyDefinitionId,
                        principalTable: "property_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_tag_property_values_tags_TagId",
                        column: x => x.TagId,
                        principalTable: "tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "video_tags",
                columns: table => new
                {
                    VideoId = table.Column<Guid>(type: "uuid", nullable: false),
                    TagId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_video_tags", x => new { x.VideoId, x.TagId });
                    table.ForeignKey(
                        name: "FK_video_tags_tags_TagId",
                        column: x => x.TagId,
                        principalTable: "tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_video_tags_videos_VideoId",
                        column: x => x.VideoId,
                        principalTable: "videos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_property_definitions_Scope_TagGroupId_Name",
                table: "property_definitions",
                columns: new[] { "Scope", "TagGroupId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_property_definitions_TagGroupId",
                table: "property_definitions",
                column: "TagGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_tag_groups_Name",
                table: "tag_groups",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tag_property_values_PropertyDefinitionId",
                table: "tag_property_values",
                column: "PropertyDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_tags_TagGroupId_Name",
                table: "tags",
                columns: new[] { "TagGroupId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_video_property_values_PropertyDefinitionId",
                table: "video_property_values",
                column: "PropertyDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_video_sets_Name",
                table: "video_sets",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_video_tags_TagId",
                table: "video_tags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_videos_FilePath_FileName",
                table: "videos",
                columns: new[] { "FilePath", "FileName" },
                unique: true,
                filter: "\"ParentVideoId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_videos_ImportJobId",
                table: "videos",
                column: "ImportJobId");

            migrationBuilder.CreateIndex(
                name: "IX_videos_IngestDate",
                table: "videos",
                column: "IngestDate");

            migrationBuilder.CreateIndex(
                name: "IX_videos_Md5",
                table: "videos",
                column: "Md5");

            migrationBuilder.CreateIndex(
                name: "IX_videos_ParentVideoId",
                table: "videos",
                column: "ParentVideoId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tag_property_values");

            migrationBuilder.DropTable(
                name: "video_property_values");

            migrationBuilder.DropTable(
                name: "video_sets");

            migrationBuilder.DropTable(
                name: "video_tags");

            migrationBuilder.DropTable(
                name: "property_definitions");

            migrationBuilder.DropTable(
                name: "tags");

            migrationBuilder.DropTable(
                name: "videos");

            migrationBuilder.DropTable(
                name: "tag_groups");
        }
    }
}
