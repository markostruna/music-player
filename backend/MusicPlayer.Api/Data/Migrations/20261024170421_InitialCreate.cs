using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicPlayer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AlbumMetadata",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Artist = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Album = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 20000, nullable: false),
                    DescriptionEdited = table.Column<bool>(type: "INTEGER", nullable: false),
                    MusicBrainzId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ImageFileName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ImageSourceRootId = table.Column<int>(type: "INTEGER", nullable: true),
                    ImageRelativePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    ImageMissing = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlbumMetadata", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "ArtistMetadata",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Artist = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 20000, nullable: false),
                    DescriptionEdited = table.Column<bool>(type: "INTEGER", nullable: false),
                    MusicBrainzId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ImageFileName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ImageSourceRootId = table.Column<int>(type: "INTEGER", nullable: true),
                    ImageRelativePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    ImageMissing = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtistMetadata", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "SourceRoots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceRoots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    SecurityStamp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    Theme = table.Column<string>(type: "TEXT", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Folders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceRootId = table.Column<int>(type: "INTEGER", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Folders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Folders_SourceRoots_SourceRootId",
                        column: x => x.SourceRootId,
                        principalTable: "SourceRoots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Tracks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceRootId = table.Column<int>(type: "INTEGER", nullable: false),
                    FolderId = table.Column<int>(type: "INTEGER", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Artist = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Album = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    AlbumArtist = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Genre = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    TrackNumber = table.Column<uint>(type: "INTEGER", nullable: false),
                    DiscNumber = table.Column<uint>(type: "INTEGER", nullable: false),
                    Year = table.Column<uint>(type: "INTEGER", nullable: false),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: false),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: false),
                    LastWriteTime = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ScannedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    IsAvailable = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tracks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tracks_Folders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "Folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tracks_SourceRoots_SourceRootId",
                        column: x => x.SourceRootId,
                        principalTable: "SourceRoots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Folders_SourceRootId_RelativePath",
                table: "Folders",
                columns: new[] { "SourceRootId", "RelativePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceRoots_Path",
                table: "SourceRoots",
                column: "Path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_FolderId",
                table: "Tracks",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_SourceRootId_RelativePath",
                table: "Tracks",
                columns: new[] { "SourceRootId", "RelativePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_NormalizedEmail",
                table: "Users",
                column: "NormalizedEmail",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlbumMetadata");

            migrationBuilder.DropTable(
                name: "ArtistMetadata");

            migrationBuilder.DropTable(
                name: "Tracks");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "Folders");

            migrationBuilder.DropTable(
                name: "SourceRoots");
        }
    }
}
