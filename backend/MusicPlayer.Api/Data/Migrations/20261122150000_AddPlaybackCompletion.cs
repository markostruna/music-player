using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicPlayer.Api.Data.Migrations;

[DbContext(typeof(MusicDbContext))]
[Migration("20261122150000_AddPlaybackCompletion")]
public sealed class AddPlaybackCompletion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "CompletedAtUnixMilliseconds",
            table: "PlaybackHistory",
            type: "INTEGER",
            nullable: true);
        migrationBuilder.Sql("""
            UPDATE "PlaybackHistory"
            SET "CompletedAtUnixMilliseconds" = "UpdatedAtUnixMilliseconds"
            WHERE EXISTS (
                SELECT 1 FROM "Tracks"
                WHERE "Tracks"."Id" = "PlaybackHistory"."TrackId"
                    AND "Tracks"."DurationSeconds" > 0
                    AND "PlaybackHistory"."PositionSeconds" >= "Tracks"."DurationSeconds"
            )
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "CompletedAtUnixMilliseconds", table: "PlaybackHistory");
}
