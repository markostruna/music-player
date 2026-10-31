using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicPlayer.Api.Data.Migrations;

[DbContext(typeof(MusicDbContext))]
[Migration("20261031191400_RemoveAlbumDescription")]
public sealed class RemoveAlbumDescription : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE "AlbumMetadata_new" (
                "Key" TEXT NOT NULL CONSTRAINT "PK_AlbumMetadata" PRIMARY KEY,
                "Artist" TEXT NOT NULL,
                "Album" TEXT NOT NULL,
                "MusicBrainzId" TEXT NOT NULL,
                "ImageFileName" TEXT NULL,
                "ImageSourceRootId" INTEGER NULL,
                "ImageRelativePath" TEXT NULL,
                "ImageMissing" INTEGER NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            INSERT INTO "AlbumMetadata_new" (
                "Key", "Artist", "Album", "MusicBrainzId", "ImageFileName",
                "ImageSourceRootId", "ImageRelativePath", "ImageMissing", "UpdatedAt")
            SELECT
                "Key", "Artist", "Album", "MusicBrainzId", "ImageFileName",
                "ImageSourceRootId", "ImageRelativePath", "ImageMissing", "UpdatedAt"
            FROM "AlbumMetadata";
            DROP TABLE "AlbumMetadata";
            ALTER TABLE "AlbumMetadata_new" RENAME TO "AlbumMetadata";
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE "AlbumMetadata_new" (
                "Key" TEXT NOT NULL CONSTRAINT "PK_AlbumMetadata" PRIMARY KEY,
                "Artist" TEXT NOT NULL,
                "Album" TEXT NOT NULL,
                "Description" TEXT NOT NULL,
                "DescriptionEdited" INTEGER NOT NULL,
                "MusicBrainzId" TEXT NOT NULL,
                "ImageFileName" TEXT NULL,
                "ImageSourceRootId" INTEGER NULL,
                "ImageRelativePath" TEXT NULL,
                "ImageMissing" INTEGER NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            INSERT INTO "AlbumMetadata_new" (
                "Key", "Artist", "Album", "Description", "DescriptionEdited",
                "MusicBrainzId", "ImageFileName", "ImageSourceRootId",
                "ImageRelativePath", "ImageMissing", "UpdatedAt")
            SELECT
                "Key", "Artist", "Album", '', 0, "MusicBrainzId", "ImageFileName",
                "ImageSourceRootId", "ImageRelativePath", "ImageMissing", "UpdatedAt"
            FROM "AlbumMetadata";
            DROP TABLE "AlbumMetadata";
            ALTER TABLE "AlbumMetadata_new" RENAME TO "AlbumMetadata";
            """);
    }
}
