using Microsoft.EntityFrameworkCore;

namespace Rosenvall.DevOps.Api;

public static class DevOpsStateDatabaseInitializer
{
    public const string InitialMigrationId = "20260607000000_InitialDevOpsState";
    private const string ProductVersion = "10.0.8";

    public static async Task MigrateAsync(
        IDbContextFactory<DevOpsStateDbContext> dbFactory,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await BaselineLegacyEnsureCreatedDatabaseAsync(db, logger, cancellationToken);
        await db.Database.MigrateAsync(cancellationToken);
    }

    private static async Task BaselineLegacyEnsureCreatedDatabaseAsync(
        DevOpsStateDbContext db,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!await db.Database.CanConnectAsync(cancellationToken))
        {
            return;
        }

        var provider = db.Database.ProviderName ?? string.Empty;
        if (provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            await BaselineSqliteAsync(db, logger, cancellationToken);
            return;
        }

        if (provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
        {
            await BaselinePostgresAsync(db, logger, cancellationToken);
        }
    }

    private static async Task BaselineSqliteAsync(
        DevOpsStateDbContext db,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var hasDocuments = await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = 'Documents'")
            .SingleAsync(cancellationToken) > 0;
        if (!hasDocuments)
        {
            return;
        }

        var hasHistory = await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsHistory'")
            .SingleAsync(cancellationToken) > 0;
        if (!hasHistory)
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE "__EFMigrationsHistory" (
                    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                    "ProductVersion" TEXT NOT NULL
                )
                """,
                cancellationToken);
        }

        await InsertBaselineMigrationIfMissingAsync(
            db,
            """
            INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            SELECT {0}, {1}
            WHERE NOT EXISTS (
                SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = {0}
            )
            """,
            cancellationToken);
        logger.LogInformation("Baselined legacy SQLite DevOps state database for EF migration {MigrationId}.", InitialMigrationId);
    }

    private static async Task BaselinePostgresAsync(
        DevOpsStateDbContext db,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var hasDocuments = await db.Database.SqlQueryRaw<bool>(
            """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.tables
                WHERE table_schema = 'public' AND table_name = 'Documents'
            ) AS "Value"
            """)
            .SingleAsync(cancellationToken);
        if (!hasDocuments)
        {
            return;
        }

        var hasHistory = await db.Database.SqlQueryRaw<bool>(
            """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.tables
                WHERE table_schema = 'public' AND table_name = '__EFMigrationsHistory'
            ) AS "Value"
            """)
            .SingleAsync(cancellationToken);
        if (!hasHistory)
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE "__EFMigrationsHistory" (
                    "MigrationId" character varying(150) NOT NULL,
                    "ProductVersion" character varying(32) NOT NULL,
                    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
                )
                """,
                cancellationToken);
        }

        await InsertBaselineMigrationIfMissingAsync(
            db,
            """
            INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            SELECT {0}, {1}
            WHERE NOT EXISTS (
                SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = {0}
            )
            """,
            cancellationToken);
        logger.LogInformation("Baselined legacy PostgreSQL DevOps state database for EF migration {MigrationId}.", InitialMigrationId);
    }

    private static Task InsertBaselineMigrationIfMissingAsync(
        DevOpsStateDbContext db,
        string sql,
        CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync(sql, [InitialMigrationId, ProductVersion], cancellationToken);
}
