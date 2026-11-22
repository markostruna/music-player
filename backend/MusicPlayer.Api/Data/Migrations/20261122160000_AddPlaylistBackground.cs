using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicPlayer.Api.Data.Migrations;

[DbContext(typeof(MusicDbContext))]
[Migration("20261122160000_AddPlaylistBackground")]
public sealed class AddPlaylistBackground : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<int>(
            name: "BackgroundId",
            table: "Playlists",
            type: "INTEGER",
            nullable: false,
            defaultValue: 1);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "BackgroundId", table: "Playlists");
}
