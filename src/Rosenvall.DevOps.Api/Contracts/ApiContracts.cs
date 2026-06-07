using Microsoft.AspNetCore.Http;
using Rosenvall.DevOps.Core;
using System.Net;

namespace Rosenvall.DevOps.Api;

public sealed record WorkspaceDto(Guid Id, string Name, string EnvironmentName, string Region, int ActiveProjects, int OpenPullRequests, int SuccessfulAiImplementations, int ComputeUsagePercent);
public sealed record UserDto(Guid Id, string DisplayName, string Email, string Subject, string? AvatarUrl = null);
public sealed record TeamMemberDto(Guid UserId, string Role, string? DisplayName = null, string? Email = null, string? Status = null);
public sealed record TeamDto(Guid Id, string Name, IReadOnlyList<TeamMemberDto> Members, DateTimeOffset CreatedAt);
public sealed record RepositoryDto(Guid Id, string Provider, string Name, string RemoteUrl, string? WebUrl, string DefaultBranch, DateTimeOffset CreatedAt, string? Owner = null, string ImplementationProfile = "react-preview", string ImplementationWorkflow = "preview-then-pr");
public sealed record RepositorySourceRepositoryDto(Guid RepositoryId, string Provider, string Name, string? Owner, string DefaultBranch, bool IsPrimary, string? WebUrl, bool SourceReadable = true, string? SourceUnavailableReason = null, string SyncState = "Ready");
public sealed record RepositorySourceEntryDto(string Name, string Path, string Type, long? Size = null, string? Sha = null);
public sealed record RepositorySourceTreeDto(Guid RepositoryId, string Provider, string Ref, string Path, IReadOnlyList<RepositorySourceEntryDto> Entries, string? Message = null);
public sealed record RepositorySourceFileDto(Guid RepositoryId, string Provider, string Ref, string Path, string? Content, string Encoding, long? Size, bool IsBinary, bool Truncated, string? Message = null);
public sealed record RepositoryCloneInfoDto(
    Guid RepositoryId,
    string Provider,
    string RemoteUrl,
    string CloneCommand,
    bool InternalOnly,
    string Message,
    string? HumanCloneUrl,
    string RunnerCloneUrl,
    string? WebUrl,
    string RecommendedMode,
    string Explanation);
public sealed record BoardRepositoryDto(Guid BoardId, Guid RepositoryId, bool IsPrimary, string ImplementationProfile, RepositoryDto Repository, RepositoryProfileDto? Profile = null, string ImplementationWorkflow = "direct-pr", string SyncState = "Ready");
public sealed record BoardTeamAccessDto(Guid BoardId, Guid TeamId, string TeamName, string Role);
public sealed record BoardGitOpsSettingsDto(Guid BoardId, IReadOnlyList<string> AllowedPaths, string ArgoNamespace, string ArgoApplicationSelector);
public sealed record BoardAiContextDto(Guid BoardId, string Instructions, IReadOnlyList<string> EnabledSkills, bool AskWhenUncertain, string AgentInstructions = "");
public sealed record BoardPlanningContextDto(Guid BoardId, string RepositoryProfile, BoardGitOpsSettingsDto? GitOpsSettings = null, BoardAiContextDto? AiContext = null, RepositoryProfileDto? RepositoryProfileDraft = null, string ImplementationWorkflow = "preview-only", string? PublicHostname = null);
public sealed record BoardDto(Guid Id, Guid WorkspaceId, string Name, IReadOnlyList<BoardColumnDto> Columns, RepositoryDto? Repository = null, IReadOnlyList<BoardRepositoryDto>? Repositories = null, IReadOnlyList<BoardTeamAccessDto>? TeamAccess = null, BoardGitOpsSettingsDto? GitOpsSettings = null, BoardAiContextDto? AiContext = null, string RepositorySyncState = "Preview only", IReadOnlyList<string>? ProviderCapabilities = null, string ImplementationWorkflow = "preview-only", string? PublicHostname = null, BoardPublicAppDto? PublicApp = null);
public sealed record BoardColumnDto(string Name, IReadOnlyList<WorkItemSummaryDto> Items);
public sealed record WorkItemSummaryDto(Guid Id, string Key, string Type, string Title, string Status, string? Assignee, string Priority, int CommentCount, string? AiStatus, string? PullRequestUrl, int SortOrder, string? PreviewUrl, Guid? ParentWorkItemId = null, string? ParentKey = null, Guid? RootWorkItemId = null, string? RootKey = null, string HierarchyPath = "", bool IsBug = false, int ChildCount = 0, int DoneChildCount = 0, int BlockedChildCount = 0, int OpenPullRequestChildCount = 0);
public sealed record WorkItemDetailDto(WorkItemSummaryDto Item, string Description, IReadOnlyList<CommentDto> Comments, PreviewDto? Preview, DevelopmentDto? Development, IReadOnlyList<ImplementationRunDto>? ImplementationRuns = null, AiSessionDto? AiSession = null, IReadOnlyList<PreviewEventDto>? PreviewEvents = null, IReadOnlyList<AiRun>? PreviewImplementationRunsAwaitingRecovery = null, BoardPlanningContextDto? BoardContext = null, IReadOnlyList<RepositoryCleanupRunDto>? RepositoryCleanupRuns = null, IReadOnlyList<AiPlanReviewCommentDto>? AiPlanReviewComments = null, WorkItemSummaryDto? Parent = null, IReadOnlyList<WorkItemSummaryDto>? Children = null, IReadOnlyList<WorkItemSummaryDto>? Ancestors = null, IReadOnlyList<WorkItemSummaryDto>? Descendants = null, IReadOnlyList<EpicRunDto>? EpicRuns = null, IReadOnlyList<EpicGoalRunDto>? EpicGoalRuns = null);
public sealed record CommentDto(Guid Id, Guid WorkItemId, string Author, string Kind, string Body, DateTimeOffset CreatedAt, string? AuthorSubject = null);
public sealed record AiPlanReviewCommentDto(Guid Id, Guid WorkItemId, Guid AiRunId, string AnchorKey, string QuotedText, string Author, string Body, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? ResolvedBy = null, DateTimeOffset? ResolvedAt = null, int AiRunSequenceNumber = 0);
public sealed record PreviewDto(Guid Id, Guid WorkItemId, string Url, string Image, string Status, DateTimeOffset ExpiresAt, string? StaticHtml, string? Namespace = null, string? ResourceName = null, string? Phase = null, string? Message = null, DateTimeOffset? LastCheckedAt = null, string? PodName = null, string? FailureReason = null, string? FailureLog = null, IReadOnlyList<PreviewSourceFile>? SourceFiles = null, IReadOnlyList<PreviewTerminalLineDto>? TerminalLines = null, IReadOnlyList<PreviewStepLogDto>? StepLogs = null);
public sealed record PreviewStepLogDto(string Key, string Title, string Description, string State, DateTimeOffset? StartedAt = null, DateTimeOffset? CompletedAt = null, IReadOnlyList<PreviewTerminalLineDto>? TerminalLines = null);
public sealed record BoardPublicAppDto(Guid BoardId, string Hostname, string Url, string Namespace, string ResourceName, string Status, Guid? SourceWorkItemId, Guid? SourcePreviewId, Guid? SourceImplementationRunId, string? SourcePullRequestUrl, string? SourceBranch, string? CommitSha, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? LastDeployedAt = null, string? FailureReason = null, string? Message = null, IReadOnlyList<PreviewSourceFile>? SourceFiles = null);
public sealed record PreviewEnvironmentDto(Guid Id, Guid? WorkItemId, string WorkItemKey, string WorkItemTitle, string Url, string Namespace, string ResourceName, string Image, string Status, DateTimeOffset ExpiresAt, string? Phase = null, string? Message = null, DateTimeOffset? LastCheckedAt = null, string? PodName = null, string? FailureReason = null, string? FailureLog = null);
public sealed record PreviewEventDto(Guid Id, Guid? WorkItemId, string WorkItemKey, string WorkItemTitle, string EventType, string? Namespace, string? Url, string Actor, string Message, DateTimeOffset CreatedAt);
public sealed record PreviewTerminalLineDto(DateTimeOffset CreatedAt, string Stream, string Message);
public sealed record PipelineStatusDto(Guid Id, Guid? WorkItemId, string WorkItemKey, string WorkItemTitle, string Stage, string Status, string Message, DateTimeOffset UpdatedAt);
public sealed record PipelineRunDto(Guid Id, Guid RepositoryId, Guid? BoardId, Guid? WorkItemId, string Stage, string Status, string Message, string? Url, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt = null, int TokensUsed = 0, int CodeAdded = 0, int CodeDeleted = 0, Guid? TargetRepositoryId = null);
public sealed record ActionLedgerDto(Guid Id, string ActorSubject, Guid? BoardId, Guid? WorkItemId, string OperationKind, string IdempotencyKey, string Status, Guid? RunId, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt = null, DateTimeOffset? CompletedAt = null, string? Failure = null);
public sealed record ActionStartResultDto(bool Started, ActionLedgerDto? Action, string? BlockReason = null, string? BlockMessage = null, int? RetryAfterSeconds = null);
public sealed record ActionQuotaStatusDto(bool Allowed, string ActorSubject, IReadOnlyList<string> OperationKinds, int Count, int MaxStartedPerActor, int WindowSeconds, int? RetryAfterSeconds = null);
public sealed record ExpensiveActionQuotaOptions(bool Enabled, int MaxStartedPerActor, TimeSpan Window);
public sealed record ImplementationRunDto(Guid Id, Guid RepositoryId, Guid WorkItemId, Guid AiRunId, string WorkItemKey, string WorkItemTitle, string Status, string Branch, string? PullRequestUrl, string? CommitSha, string? FailureReason, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<PreviewTerminalLineDto>? TerminalLines = null, string? JobName = null, string? PodName = null, string? LastCondition = null, string? LastEventSummary = null, string RunKind = "codex", Guid? SourcePreviewId = null, string? PullRequestProvider = null, int? PullRequestNumber = null, string? PullRequestState = null, DateTimeOffset? PullRequestMergedAt = null);
public sealed record EpicRunChildDto(Guid WorkItemId, string WorkItemKey, string WorkItemTitle, string AgentRole, string Status, Guid? AiRunId = null, Guid? ImplementationRunId = null, string? Summary = null, DateTimeOffset? UpdatedAt = null);
public sealed record EpicRunDto(Guid Id, Guid RootWorkItemId, string RootWorkItemKey, string RootWorkItemTitle, string Status, string Actor, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<EpicRunChildDto> Children, string? Summary = null, string? FailureReason = null);
public sealed record EpicGoalRunDto(Guid Id, Guid RootWorkItemId, Guid? EpicRunId, string Status, string Actor, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? Summary = null, string? FailureReason = null);
public sealed record RepositoryCleanupRunDto(Guid Id, Guid RepositoryId, Guid WorkItemId, Guid SourceImplementationRunId, string WorkItemKey, string WorkItemTitle, string Status, string Branch, string SourcePullRequestUrl, string? CleanupPullRequestUrl, string? CommitSha, string? FailureReason, string? SourcePullRequestState, string? SourcePullRequestDiff, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<PreviewTerminalLineDto>? TerminalLines = null, string? JobName = null, string? PodName = null, string? LastCondition = null, string? LastEventSummary = null, bool Adopted = false, DateTimeOffset? MergedAt = null, DateTimeOffset? VerifiedAt = null, string? VerificationFailure = null);
public sealed record ApiStatusDto(string AuthMode, ApiResourceDiagnosticsDto Resources, LocalGitReadinessDto? LocalGit = null, DemoSandboxPolicyDto? DemoSandboxPolicy = null);
public sealed record ApiResourceDiagnosticsDto(long? ProcessRssBytes, long? MemoryCurrentBytes, long? MemoryLimitBytes, long? MemoryAvailableBytes, bool IsMemoryPressured, string Status, string? Message, long? SnapshotJsonBytes = null, long SnapshotPersistWriteCount = 0, long SnapshotPersistSkipCount = 0, DateTimeOffset? LastSnapshotPersistedAt = null);
public sealed record DemoSandboxPolicyDto(bool Enabled, bool DemoUserPresent, bool Isolated, string DemoEmail, string WorkspaceName, string Message);
public sealed record ImplementationCapacityPreflightResult(bool Succeeded, string Message, ApiResourceDiagnosticsDto Diagnostics);
public sealed record GitHubPullRequestDto(string Owner, string Repository, int Number, string State, bool Merged, string HtmlUrl, string? DiffUrl = null, string? HeadRef = null, string? BaseRef = null);
public sealed record PullRequestDiffFileDto(string Path, string? Status = null, int? Additions = null, int? Deletions = null, string? PreviousPath = null);
public sealed record PullRequestReviewCommentDto(Guid Id, Guid WorkItemId, string PullRequestProvider, int PullRequestNumber, string FilePath, string Side, int LineNumber, string DiffLine, string Author, string Body, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? ResolvedBy = null, DateTimeOffset? ResolvedAt = null);
public sealed record PullRequestApprovalStateDto(bool CanApprove, string Status, string Message, string? PullRequestApprovedBy = null, DateTimeOffset? PullRequestApprovedAt = null, DateTimeOffset? PullRequestMergedAt = null, string? PullRequestFailure = null);
public sealed record PullRequestDiffDto(string Provider, string Repository, int Number, string State, string? BaseBranch, string? HeadBranch, string? PullRequestUrl, int ChangedFiles, int Additions, int Deletions, bool Truncated, string? Message, IReadOnlyList<PullRequestDiffFileDto> Files, string Diff, IReadOnlyList<PullRequestReviewCommentDto>? ReviewComments = null, string? PullRequestApprovedBy = null, DateTimeOffset? PullRequestApprovedAt = null, DateTimeOffset? PullRequestMergedAt = null, string? PullRequestFailure = null, bool CanApprove = false, string ApprovalStatus = "blocked", string? ApprovalMessage = null);
public sealed record GitOpsApplicationStatusDto(string Name, string Namespace, string SyncStatus, string HealthStatus, string? Revision, string Message, string? Url, DateTimeOffset? UpdatedAt, IReadOnlyList<string>? ApplicationUrls = null);
public sealed record GitOpsApplicationsResponseDto(IReadOnlyList<GitOpsApplicationStatusDto> Applications, string? Message = null);
public sealed record GitHubIntegrationDto(Guid Id, long InstallationId, string AccountLogin, string AccountType, string Status, int RepositoriesCount, string InstalledBy, DateTimeOffset CreatedAt, bool CanCreateRepositories = false, IReadOnlyList<Guid>? RepositoryCreatorTeamIds = null, bool CanManageRepositoryCreationPolicy = false, bool RequiresUserAuthorizationForRepositoryCreation = false, bool HasUserAuthorization = false, string? AuthorizedGitHubLogin = null, string? RepositoryCreationMessage = null);
public sealed record GitHubManifestAppDto(long Id, string Slug, string Name, string Pem, string? ClientId = null, string? ClientSecret = null, string? WebhookSecret = null);
public sealed record BoardSecretDto(Guid Id, Guid BoardId, Guid? RepositoryId, string Key, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? LastUsedAt = null);
public sealed record AiSessionDto(Guid Id, Guid WorkItemId, string Provider, string Model, string? ProviderSessionId, string Status, DateTimeOffset LastPromptAt, Guid? RepositoryId = null, Guid? LastRunId = null, string? ContextSummary = null, string? ReasoningEffort = null);
public sealed record TimelineEventDto(Guid Id, Guid? BoardId, Guid? RepositoryId, Guid? WorkItemId, string Kind, string Title, string Message, string Actor, string? Url, DateTimeOffset CreatedAt);
public sealed record DevelopmentDto(string Repository, string Branch, string? PullRequestUrl, string ChecksStatus, string? PullRequestApprovedBy = null, DateTimeOffset? PullRequestApprovedAt = null, Guid? RepositoryId = null, string? PullRequestProvider = null, int? PullRequestNumber = null, string? PullRequestState = null, DateTimeOffset? PullRequestMergedAt = null, string? PullRequestFailure = null);
public sealed record SettingsDto(GitHubSettingsDto GitHub, AiSettingsDto Ai, PreviewSettingsDto Preview, RepositoryHostingSettingsDto Repositories, AuthentikSettingsDto Authentik);
public sealed record GitHubSettingsDto(string Account, string TargetRepository, string BranchWatchPatterns, bool Connected, bool AppConfigured, string? InstallUrl, bool SyncAvailable);
public sealed record AiSettingsDto(string Provider, string Endpoint, string ActiveModel, IReadOnlyList<string> AvailableModels, bool AutoReviewPullRequests, IReadOnlyList<AiProviderSettingsDto> AvailableProviders);
public sealed record AiProviderSettingsDto(string Provider, string DisplayName, string Status, string Endpoint, string ActiveModel, IReadOnlyList<string> AvailableModels, IReadOnlyList<string>? AvailableReasoningEfforts = null, string? DefaultReasoningEffort = null);
public sealed record PreviewSettingsDto(string Domain, int DefaultTtlDays, string Namespace);
public sealed record RepositoryHostingSettingsDto(string Provider, string Mode, string ApiBaseUrl, bool CanCreateRepositories, bool LocalGitEnabled = false, bool LocalGitAvailable = false, string? LocalGitMessage = null);
public sealed record LocalGitReadinessDto(bool Enabled, bool Configured, bool Available, string ApiBaseUrl, string? Message = null, int? StatusCode = null);
public sealed record AuthentikSettingsDto(bool Enabled, string Authority, string UsersEndpoint);
public sealed record MetricsDto(Guid? BoardId, int TokensUsed, int CodeAdded, int CodeDeleted, int PipelineRuns);
public sealed record AssigneeDto(string Id, string DisplayName, string Email, string Source);
public sealed record GitHubInstallUrlDto(string Url);
public sealed record GitHubRepositoryPickerDto(string Status, string? Message, IReadOnlyList<RepositoryDto> Repositories, long? ActiveInstallationId = null);
public sealed record RepositorySkillDraftDto(string Name, string Description, string Content, bool Enabled = true);
public sealed record RepositoryProfileDto(
    string ImplementationProfile,
    string DisplayName,
    double Confidence,
    IReadOnlyList<string> EnabledSkills,
    string Instructions,
    IReadOnlyList<string> Signals,
    string Source = "scanner",
    IReadOnlyList<string>? CapabilityTags = null,
    IReadOnlyList<RepositorySkillDraftDto>? SkillDrafts = null,
    string? AnalyzerModel = null,
    DateTimeOffset? AnalyzedAt = null);

public static class ActionLedgerBlockReasons
{
    public const string QuotaExceeded = "QuotaExceeded";
    public static readonly IReadOnlyList<string> AiPlanningActionKinds = ["ai-plan", "ai-plan-revise"];
    public static readonly IReadOnlyList<string> RepositoryImplementationActionKinds = ["repository-implementation"];
    public static readonly IReadOnlyList<string> PullRequestReviewFixActionKinds = ["pr-review-fix"];
    public static readonly IReadOnlyList<string> PreviewBuildActionKinds = ["preview-build"];
    public static readonly IReadOnlyList<string> CleanupActionKinds = ["board-cleanup", "work-item-cleanup", "repository-cleanup"];
    public static readonly IReadOnlyList<string> RepositoryCreationActionKinds = ["repository-creation"];
}

public sealed record UserIdentityRequest(string Subject, string DisplayName, string Email, string? AvatarUrl = null);
public sealed record CreateTeamRequest(string Name);
public sealed record UpsertTeamMemberRequest(Guid UserId, string Role);
public sealed record InviteTeamMemberRequest(string Email, string Role);
public sealed record CreateWorkspaceRequest(string Name, string EnvironmentName, string Region);
public sealed record CreateRepositoryRequest(string Provider, string Name, string RemoteUrl, string DefaultBranch, string? WebUrl = null, string? Owner = null, string? ImplementationProfile = null, string? ImplementationWorkflow = null);
public sealed record SyncRepositoryToProviderRequest(Guid SourceRepositoryId, string TargetProvider, string TargetName, bool Private = true);
public sealed record SyncRepositoryToProviderResponse(RepositoryDto Repository, PipelineRunDto Run, string Message);
public sealed record GitHubRepositoryOnboardingFileDto(string Path, string Content);
public sealed record GitHubRepositoryOnboardingDraftRequest(string Name, string? Description = null, string? Prompt = null, string? ImplementationProfile = null);
public sealed record GitHubRepositoryOnboardingDraftDto(string Name, string Description, string Prompt, RepositoryProfileDto RepositoryProfile, BoardAiContextRequest AiContext, IReadOnlyList<GitHubRepositoryOnboardingFileDto> Files, string Source = "fallback", string? Model = null);
public sealed record CreateGitHubRepositoryRequest(long? InstallationId, string Name, bool Private = true, string? Description = null, string? Owner = null, string? ImplementationProfile = null, string? OnboardingPrompt = null, IReadOnlyList<GitHubRepositoryOnboardingFileDto>? Files = null, RepositoryProfileDto? RepositoryProfile = null, BoardAiContextRequest? AiContext = null, string? ImplementationWorkflow = null);
public sealed record CreateLocalGitRepositoryRequest(string Name, bool Private = true, string? Description = null, string? ImplementationProfile = null, string? OnboardingPrompt = null, IReadOnlyList<GitHubRepositoryOnboardingFileDto>? Files = null, RepositoryProfileDto? RepositoryProfile = null, BoardAiContextRequest? AiContext = null, string? ImplementationWorkflow = null);
public sealed record GitHubRepositoryCreateResponse(RepositoryDto Repository, RepositoryProfileDto? RepositoryProfile = null, BoardAiContextRequest? AiContext = null);
public sealed record GitHubRepositoryCreationResult(bool Succeeded, RepositoryDto? Repository, string Message, HttpStatusCode? StatusCode = null);
public sealed record GitHubRepositoryCreationPolicyDto(long InstallationId, IReadOnlyList<Guid> AllowedTeamIds);
public sealed record UpdateGitHubRepositoryCreationPolicyRequest(IReadOnlyList<Guid>? AllowedTeamIds);
public sealed record GitHubUserAuthorizationState(string ActorSubject, long InstallationId, DateTimeOffset CreatedAt);
public sealed record GitHubUserAuthorizationDto(Guid Id, string ActorSubject, long InstallationId, string AccountLogin, string GitHubLogin, string Status, string SecretName, DateTimeOffset AuthorizedAt, DateTimeOffset? ExpiresAt = null);
public sealed record GitHubUserAuthorizationStatusDto(long InstallationId, bool RequiredForRepositoryCreation, bool Connected, string? GitHubLogin, DateTimeOffset? ExpiresAt, string Message);
public sealed record GitHubUserAuthorizationStartDto(string AuthorizationUrl);
public sealed record GitHubUserAuthorizationTokenDto(string AccessToken, string? RefreshToken = null, DateTimeOffset? ExpiresAt = null);
public sealed record GitHubUserDto(string Login);
public sealed record RepositoryCreationTokenResult(bool Succeeded, string Token, string Message, int StatusCode)
{
    public static RepositoryCreationTokenResult Ok(string token) => new(true, token, "", StatusCodes.Status200OK);
    public static RepositoryCreationTokenResult Fail(string message, int statusCode) => new(false, "", message, statusCode);
}
public sealed record CreateBoardRequest(string Name, Guid? RepositoryId, string? RepositoryProvider, string? RepositoryName, string? RepositoryRemoteUrl, string? RepositoryWebUrl, string? RepositoryDefaultBranch, string? RepositoryOwner = null, string? ImplementationProfile = null, string? ProviderMode = null, string? GitHubRepositoryId = null, string? CustomRepositoryUrl = null, IReadOnlyList<Guid>? TeamIds = null, BoardGitOpsSettingsRequest? GitOpsSettings = null, BoardAiContextRequest? AiContext = null, RepositoryProfileDto? RepositoryProfile = null, string? ImplementationWorkflow = null, string? PublicHostname = null);
public sealed record BoardHostingSettingsRequest(string? PublicHostname, string? ImplementationWorkflow = null);
public sealed record BoardGitOpsSettingsRequest(IReadOnlyList<string>? AllowedPaths, string? ArgoNamespace, string? ArgoApplicationSelector);
public sealed record BoardAiContextRequest(string? Instructions, IReadOnlyList<string>? EnabledSkills, bool? AskWhenUncertain, string? AgentInstructions = null);
public sealed record LinkBoardRepositoryRequest(Guid RepositoryId, bool IsPrimary, string? ImplementationProfile = null, string SyncState = "Ready");
public sealed record SyncGitHubRepositoryRequest(Guid? RepositoryId = null, string? Owner = null, string? Name = null, bool Private = true, string? Description = null, long? InstallationId = null, string? ImplementationProfile = null, bool CreateNew = false, string? RemoteUrl = null, string? WebUrl = null, string? DefaultBranch = null, string? ImplementationWorkflow = null);
public sealed record UpsertBoardTeamAccessRequest(string Role);
public sealed record CreateBoardSecretRequest(string Key, string Value, Guid? RepositoryId = null);
public sealed record CreateWorkItemRequest(Guid BoardId, string Type, string Title, string Description, string Status, string Priority, string? Assignee, Guid? ParentWorkItemId = null, bool IsBug = false);
public sealed record UpdateWorkItemRequest(string Title, string Description, string Type, string Status, string Priority, string? Assignee, Guid? ParentWorkItemId = null, bool IsBug = false);
public sealed record CreateChildWorkItemRequest(string Type, string Title, string Description, string Status, string Priority, string? Assignee, bool IsBug = false);
public sealed record UpdateWorkItemHierarchyRequest(Guid? ParentWorkItemId);
public sealed record WorkItemHierarchyNodeDto(WorkItemSummaryDto Item, IReadOnlyList<WorkItemHierarchyNodeDto> Children);
public sealed record MoveWorkItemRequest(string Status, int SortOrder);
public sealed record AddCommentRequest(string Body);
public sealed record UpdateCommentRequest(string Body);
public sealed record CreateAiPlanReviewCommentRequest(string AnchorKey, string QuotedText, string Body);
public sealed record UpdateAiPlanReviewCommentRequest(string? Body = null, string? Status = null);
public sealed record CreatePullRequestReviewCommentRequest(string FilePath, string Side, int LineNumber, string DiffLine, string Body);
public sealed record UpdatePullRequestReviewCommentRequest(string? Body = null, string? Status = null);
public sealed record StartPullRequestReviewFixRequest(string? ReasoningEffort = null)
{
    internal string? ActorForStore { get; init; }

    public StartPullRequestReviewFixRequest(string? actor, string? reasoningEffort)
        : this(reasoningEffort)
    {
        ActorForStore = actor;
    }
}

public sealed record StartAiPlanRequest(string Provider, string Model, string? ReasoningEffort = null);
public sealed record ReviseAiPlanRequest(string Message, string Provider, string Model, string? ReasoningEffort = null, Guid? AiRunId = null);
public sealed record ApproveAiRunRequest(string? ReasoningEffort = null)
{
    public ApproveAiRunRequest(string? approvedBy, string? reasoningEffort)
        : this(reasoningEffort)
    {
    }
}

public sealed record DiscardAiRunRequest();
public sealed record ApprovePullRequestRequest();
public sealed record PreviewActionRequest();
public sealed record DeleteAndCleanupRequest();
public sealed record AdoptCleanupPullRequestRequest(string? PullRequestUrl = null, Guid? SourceImplementationRunId = null)
{
    public AdoptCleanupPullRequestRequest(string? actor, string? pullRequestUrl, Guid? sourceImplementationRunId)
        : this(pullRequestUrl, sourceImplementationRunId)
    {
    }
}

public sealed record RecordPipelineRunRequest(Guid RepositoryId, Guid? BoardId, Guid? WorkItemId, string Stage, string Status, string Message, string? Url = null, int TokensUsed = 0, int CodeAdded = 0, int CodeDeleted = 0, Guid? TargetRepositoryId = null);
public sealed record ExecutePipelineRunRequest();
public sealed record StartImplementationRunRequest(Guid AiRunId, Guid? RepositoryId = null, string? ReasoningEffort = null)
{
    internal string? ActorForStore { get; init; }

    public StartImplementationRunRequest(Guid aiRunId, string? actor, Guid? repositoryId = null, string? reasoningEffort = null)
        : this(aiRunId, repositoryId, reasoningEffort)
    {
        ActorForStore = actor;
    }
}

public sealed record GitHubIntegrationCallbackRequest(long InstallationId, string AccountLogin, string AccountType, string InstalledBy, int RepositoriesCount = 0, string Status = "Installed");
public sealed record SnapshotStoreDiagnostics(long? JsonBytes, long PersistWriteCount, long PersistSkipCount, DateTimeOffset? LastPersistedAt);
public sealed record UpdateAiSessionProviderRequest(string ProviderSessionId);
public sealed record GitHubCallbackRequest(Guid WorkItemId, string Repository, string Branch, string? PullRequestUrl, string Image, string ChecksStatus, string? StaticHtml = null);
