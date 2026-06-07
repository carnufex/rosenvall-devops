using Microsoft.EntityFrameworkCore;

namespace Rosenvall.DevOps.Api;

public sealed class DevOpsStateDbContext(DbContextOptions<DevOpsStateDbContext> options) : DbContext(options)
{
    public DbSet<DevOpsStateDocument> Documents => Set<DevOpsStateDocument>();
}

public sealed class DevOpsStateDocument
{
    public string Id { get; set; } = "default";
    public string Json { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
