using Microsoft.Extensions.Configuration;
using System.Diagnostics;

namespace Rosenvall.DevOps.Api;

public static class ApiResourceDiagnosticsReader
{
    private const long DefaultMinHeadroomBytes = 128L * 1024 * 1024;

    public static ApiResourceDiagnosticsDto Read(IConfiguration configuration, SnapshotStoreDiagnostics? snapshot = null)
    {
        var processRssBytes = Process.GetCurrentProcess().WorkingSet64;
        var memoryCurrentBytes = ReadCgroupLong("/sys/fs/cgroup/memory.current") ??
            ReadCgroupLong("/sys/fs/cgroup/memory/memory.usage_in_bytes");
        var memoryLimitBytes = ReadCgroupLimit("/sys/fs/cgroup/memory.max") ??
            ReadCgroupLimit("/sys/fs/cgroup/memory/memory.limit_in_bytes");
        long? availableBytes = memoryCurrentBytes is { } current && memoryLimitBytes is { } limit && limit > current
            ? limit - current
            : null;
        var minHeadroomBytes = configuration.GetValue("RepositoryRuns:ApiMemoryMinHeadroomBytes", DefaultMinHeadroomBytes);
        var pressured = availableBytes is { } available && available < minHeadroomBytes;
        var message = pressured
            ? $"API memory headroom is below {minHeadroomBytes / 1024 / 1024} MiB."
            : null;

        return new ApiResourceDiagnosticsDto(
            processRssBytes,
            memoryCurrentBytes,
            memoryLimitBytes,
            availableBytes,
            pressured,
            pressured ? "Degraded" : "Healthy",
            message,
            snapshot?.JsonBytes,
            snapshot?.PersistWriteCount ?? 0,
            snapshot?.PersistSkipCount ?? 0,
            snapshot?.LastPersistedAt);
    }

    private static long? ReadCgroupLong(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var value = File.ReadAllText(path).Trim();
        return long.TryParse(value, out var parsed) ? parsed : null;
    }

    private static long? ReadCgroupLimit(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var value = File.ReadAllText(path).Trim();
        if (string.Equals(value, "max", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!long.TryParse(value, out var parsed))
        {
            return null;
        }

        return parsed > long.MaxValue / 4 ? null : parsed;
    }
}

public static class ImplementationCapacityPreflight
{
    public static ImplementationCapacityPreflightResult Evaluate(ApiResourceDiagnosticsDto diagnostics, long minHeadroomBytes)
    {
        if (diagnostics.IsMemoryPressured ||
            diagnostics.MemoryAvailableBytes is { } available && available < minHeadroomBytes)
        {
            return new ImplementationCapacityPreflightResult(
                false,
                "Implementation cannot start because Rosenvall DevOps API is memory pressured. Try again after cleanup or restart.",
                diagnostics);
        }

        return new ImplementationCapacityPreflightResult(true, "Implementation capacity is available.", diagnostics);
    }
}
