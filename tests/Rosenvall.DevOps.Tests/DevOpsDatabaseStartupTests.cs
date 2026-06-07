using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Rosenvall.DevOps.Api;

namespace Rosenvall.DevOps.Tests;

public sealed class DevOpsDatabaseStartupTests
{
    [Fact]
    public void Api_startup_uses_ef_migrations_instead_of_ensure_created()
    {
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "src", "Rosenvall.DevOps.Api", "Program.cs"));
        var initializerPath = Path.Combine(root, "src", "Rosenvall.DevOps.Api", "Persistence", "DevOpsStateDatabaseInitializer.cs");
        var migrationsPath = Path.Combine(root, "src", "Rosenvall.DevOps.Api", "Migrations");

        Assert.DoesNotContain("EnsureCreated", program, StringComparison.Ordinal);
        Assert.Contains("DevOpsStateDatabaseInitializer.MigrateAsync", program, StringComparison.Ordinal);
        Assert.True(File.Exists(initializerPath), "Database startup should live in Persistence/DevOpsStateDatabaseInitializer.cs.");
        Assert.True(Directory.Exists(migrationsPath), "The API project should carry EF migrations instead of relying on EnsureCreated.");
        Assert.Contains(Directory.EnumerateFiles(migrationsPath, "*.cs"), path => Path.GetFileName(path).Contains("InitialDevOpsState", StringComparison.Ordinal));
    }

    [Fact]
    public void Devops_state_db_context_lives_in_persistence_module()
    {
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "src", "Rosenvall.DevOps.Api", "Program.cs"));
        var persistencePath = Path.Combine(root, "src", "Rosenvall.DevOps.Api", "Persistence", "DevOpsStatePersistence.cs");

        Assert.DoesNotContain("public sealed class DevOpsStateDbContext", program, StringComparison.Ordinal);
        Assert.DoesNotContain("public sealed class DevOpsStateDocument", program, StringComparison.Ordinal);
        Assert.True(File.Exists(persistencePath), "DevOps state EF types should live in Persistence/DevOpsStatePersistence.cs.");
        var persistence = File.ReadAllText(persistencePath);
        Assert.Contains("public sealed class DevOpsStateDbContext", persistence, StringComparison.Ordinal);
        Assert.Contains("public sealed class DevOpsStateDocument", persistence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Database_initializer_creates_sqlite_schema_with_migration_history()
    {
        var databasePath = TempDatabasePath();
        try
        {
            var factory = CreateSqliteFactory(databasePath);

            await DevOpsStateDatabaseInitializer.MigrateAsync(factory, NullLogger.Instance);

            await using var db = await factory.CreateDbContextAsync();
            Assert.True(await TableExistsAsync(db, "Documents"));
            Assert.True(await TableExistsAsync(db, "__EFMigrationsHistory"));
            Assert.True(await MigrationExistsAsync(db, DevOpsStateDatabaseInitializer.InitialMigrationId));
        }
        finally
        {
            DeleteSqliteFiles(databasePath);
        }
    }

    [Fact]
    public async Task Database_initializer_baselines_legacy_sqlite_ensure_created_schema()
    {
        var databasePath = TempDatabasePath();
        try
        {
            var factory = CreateSqliteFactory(databasePath);
            await using (var legacy = await factory.CreateDbContextAsync())
            {
                await legacy.Database.EnsureCreatedAsync();
                legacy.Documents.Add(new DevOpsStateDocument
                {
                    Id = "default",
                    Json = """{"schema":"legacy"}""",
                    UpdatedAt = DateTimeOffset.UtcNow
                });
                await legacy.SaveChangesAsync();
            }

            await DevOpsStateDatabaseInitializer.MigrateAsync(factory, NullLogger.Instance);

            await using var db = await factory.CreateDbContextAsync();
            Assert.Equal("""{"schema":"legacy"}""", await db.Documents.Where(document => document.Id == "default").Select(document => document.Json).SingleAsync());
            Assert.True(await MigrationExistsAsync(db, DevOpsStateDatabaseInitializer.InitialMigrationId));
        }
        finally
        {
            DeleteSqliteFiles(databasePath);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Rosenvall.DevOps.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }

    private static IDbContextFactory<DevOpsStateDbContext> CreateSqliteFactory(string databasePath)
    {
        var options = new DbContextOptionsBuilder<DevOpsStateDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        return new TestDbContextFactory(options);
    }

    private static async Task<bool> TableExistsAsync(DevOpsStateDbContext db, string name) =>
        await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = {0}",
            name)
            .SingleAsync() > 0;

    private static async Task<bool> MigrationExistsAsync(DevOpsStateDbContext db, string migrationId) =>
        await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = {0}",
            migrationId)
            .SingleAsync() > 0;

    private static string TempDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"devops-migrations-{Guid.NewGuid():N}.db");

    private static void DeleteSqliteFiles(string databasePath)
    {
        foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Best-effort cleanup for temp SQLite files used by tests.
            }
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<DevOpsStateDbContext> options) : IDbContextFactory<DevOpsStateDbContext>
    {
        public DevOpsStateDbContext CreateDbContext() => new(options);
    }
}
