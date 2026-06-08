namespace Rosenvall.DevOps.Api;

public static class DemoSandboxAccessPolicy
{
    public const string DemoUserEmail = "demo@rosenvall.local";
    public const string DemoWorkspaceName = "Demo Sandbox";
    public const string DemoBoardName = "Demo Sandbox";
    public const string DemoTeamName = "Demo";

    private static readonly string[] DemoRepositoryProviders = ["NoRepository", "LocalGit"];

    public static bool IsDemoEmail(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Trim().Equals(DemoUserEmail, StringComparison.OrdinalIgnoreCase);

    public static UserAccessProfileDto BuildDemoProfile(string actorSubject, IReadOnlyList<Guid> workspaceIds) =>
        new(
            actorSubject,
            true,
            workspaceIds,
            DemoRepositoryProviders.ToArray(),
            CanCreateTeams: false,
            CanCreateWorkspaces: false,
            CanLinkExternalRepositories: false,
            CanUseGitHubIntegrations: false,
            CanSyncToGitHub: false);
}
