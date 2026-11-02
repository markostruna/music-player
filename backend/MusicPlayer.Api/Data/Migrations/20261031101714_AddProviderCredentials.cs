using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicPlayer.Api.Data.Migrations;

[DbContext(typeof(MusicDbContext))]
[Migration("20261031101714_AddProviderCredentials")]
public sealed class AddProviderCredentials : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.CreateTable(
            name: "ProviderCredentials",
            columns: table => new
            {
                Key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                ProtectedValue = table.Column<string>(type: "TEXT", maxLength: 8192, nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_ProviderCredentials", credential => credential.Key));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "ProviderCredentials");
}
