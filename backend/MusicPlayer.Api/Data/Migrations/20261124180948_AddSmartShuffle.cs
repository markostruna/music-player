using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicPlayer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSmartShuffle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PredictedGenre",
                table: "Tracks",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "SmartQueueAdvances",
                table: "PlaybackStates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "SmartShuffle",
                table: "PlaybackStates",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SuggestedTrackId",
                table: "PlaybackStates",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PredictedGenre",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "SmartQueueAdvances",
                table: "PlaybackStates");

            migrationBuilder.DropColumn(
                name: "SmartShuffle",
                table: "PlaybackStates");

            migrationBuilder.DropColumn(
                name: "SuggestedTrackId",
                table: "PlaybackStates");
        }
    }
}
