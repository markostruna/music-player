using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicPlayer.Api.Data.Migrations;

[DbContext(typeof(MusicDbContext))]
[Migration("20261122140000_IndexPlaybackStatistics")]
public sealed class IndexPlaybackStatistics : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.CreateIndex(
            name: "IX_PlaybackHistory_UserId_TrackId_StartedAtUnixMilliseconds_Id",
            table: "PlaybackHistory",
            columns: new[] { "UserId", "TrackId", "StartedAtUnixMilliseconds", "Id" });

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropIndex(
            name: "IX_PlaybackHistory_UserId_TrackId_StartedAtUnixMilliseconds_Id",
            table: "PlaybackHistory");
}
