using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Rosenvall.DevOps.Api;

public sealed class GitHubCallbackStateCleanupService(DevOpsStore store, ILogger<GitHubCallbackStateCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ManifestStateLifetime = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan UserAuthorizationStateLifetime = TimeSpan.FromMinutes(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var removed = store.CleanupExpiredGitHubCallbackStates(ManifestStateLifetime, UserAuthorizationStateLifetime);
                if (removed > 0)
                {
                    logger.LogInformation("Removed {Count} expired GitHub OAuth callback state entries.", removed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "GitHub OAuth callback state cleanup failed.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }
}
