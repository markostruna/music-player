using Microsoft.EntityFrameworkCore;

namespace MusicPlayer.Api.Data;

public static class DatabaseMigrationInitializer
{
    private const string InitialMigrationId = "20261024170421_InitialCreate";
    private const string EfProductVersion = "10.0.12";

    public static async Task MigrateAsync(MusicDbContext database, CancellationToken cancellationToken = default)
    {
        await BaselineLegacyDatabaseAsync(database, cancellationToken);
        await database.Database.MigrateAsync(cancellationToken);
    }

    private static async Task BaselineLegacyDatabaseAsync(
        MusicDbContext database,
        CancellationToken cancellationToken)
    {
        var connection = database.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var inspect = connection.CreateCommand();
            inspect.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'Users')";
            if (Convert.ToInt32(await inspect.ExecuteScalarAsync(cancellationToken)) == 0)
            {
                return;
            }

            await using var baseline = connection.CreateCommand();
            baseline.CommandText = """
                CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                    "ProductVersion" TEXT NOT NULL
                );
                INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                VALUES ($migrationId, $productVersion);
                """;
            var migrationId = baseline.CreateParameter();
            migrationId.ParameterName = "$migrationId";
            migrationId.Value = InitialMigrationId;
            baseline.Parameters.Add(migrationId);
            var productVersion = baseline.CreateParameter();
            productVersion.ParameterName = "$productVersion";
            productVersion.Value = EfProductVersion;
            baseline.Parameters.Add(productVersion);
            await baseline.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }
}
