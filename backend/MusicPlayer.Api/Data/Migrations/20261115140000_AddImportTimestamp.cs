using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicPlayer.Api.Data.Migrations;

[DbContext(typeof(MusicDbContext))]
[Migration("20261115140000_AddImportTimestamp")]
public sealed class AddImportTimestamp : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "ImportedAtUnixMilliseconds",
            table: "Tracks",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.Sql(
            """
            UPDATE "Tracks"
            SET "ImportedAtUnixMilliseconds" = CAST(strftime('%s', "ScannedAt") AS INTEGER) * 1000;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ImportedAtUnixMilliseconds", table: "Tracks");
    }
}
