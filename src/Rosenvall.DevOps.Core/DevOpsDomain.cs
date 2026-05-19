using System.Text;
using System.Text.RegularExpressions;

namespace Rosenvall.DevOps.Core;

public sealed record StatusTransition(string From, string To);

public sealed class WorkItemType
{
    private readonly HashSet<string> _statuses;
    private readonly HashSet<StatusTransition> _transitions;

    private WorkItemType(string name, IEnumerable<string> statuses, IEnumerable<StatusTransition> transitions)
    {
        Name = name;
        _statuses = new HashSet<string>(statuses, StringComparer.OrdinalIgnoreCase);
        _transitions = new HashSet<StatusTransition>(transitions);
    }

    public string Name { get; }

    public static WorkItemType Create(string name, IEnumerable<string> statuses, IEnumerable<StatusTransition> transitions)
    {
        var statusList = statuses.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Work item type name is required.", nameof(name));
        }

        if (statusList.Length == 0)
        {
            throw new ArgumentException("At least one status is required.", nameof(statuses));
        }

        var transitionList = transitions.ToArray();
        foreach (var transition in transitionList)
        {
            if (!statusList.Contains(transition.From, StringComparer.OrdinalIgnoreCase) ||
                !statusList.Contains(transition.To, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Transitions must reference configured statuses.", nameof(transitions));
            }
        }

        return new WorkItemType(name.Trim(), statusList, transitionList);
    }

    public bool CanMove(string from, string to) =>
        _statuses.Contains(from) &&
        _statuses.Contains(to) &&
        _transitions.Contains(new StatusTransition(from, to));
}

public enum AiRunStatus
{
    Planning,
    PlanReady,
    Approved,
    ImplementationRunning,
    Completed,
    Failed,
    Discarded
}

public sealed class AiRun
{
    private AiRun(Guid id, Guid workItemId, string provider, string model, AiRunStatus status, string? plan, string? approvedBy)
    {
        Id = id;
        WorkItemId = workItemId;
        Provider = provider;
        Model = model;
        Status = status;
        Plan = plan;
        ApprovedBy = approvedBy;
    }

    public Guid Id { get; }
    public Guid WorkItemId { get; }
    public string Provider { get; }
    public string Model { get; }
    public AiRunStatus Status { get; private set; }
    public string? Plan { get; private set; }
    public string? ApprovedBy { get; private set; }

    public static AiRun Start(Guid workItemId, string provider, string model)
    {
        if (workItemId == Guid.Empty)
        {
            throw new ArgumentException("Work item id is required.", nameof(workItemId));
        }

        if (string.IsNullOrWhiteSpace(provider))
        {
            throw new ArgumentException("Provider is required.", nameof(provider));
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("Model is required.", nameof(model));
        }

        return new AiRun(Guid.NewGuid(), workItemId, provider.Trim(), model.Trim(), AiRunStatus.Planning, null, null);
    }

    public static AiRun Restore(Guid id, Guid workItemId, string provider, string model, AiRunStatus status, string? plan, string? approvedBy)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("AI run id is required.", nameof(id));
        }

        if (workItemId == Guid.Empty)
        {
            throw new ArgumentException("Work item id is required.", nameof(workItemId));
        }

        return new AiRun(id, workItemId, provider, model, status, plan, approvedBy);
    }

    public void PostPlan(string plan)
    {
        if (string.IsNullOrWhiteSpace(plan))
        {
            throw new ArgumentException("Plan is required.", nameof(plan));
        }

        Plan = plan.Trim();
        Status = AiRunStatus.PlanReady;
    }

    public void Approve(string approvedBy)
    {
        if (Status != AiRunStatus.PlanReady)
        {
            throw new InvalidOperationException("An AI run can only be approved after a plan is ready.");
        }

        if (string.IsNullOrWhiteSpace(approvedBy))
        {
            throw new ArgumentException("Approver is required.", nameof(approvedBy));
        }

        ApprovedBy = approvedBy.Trim();
        Status = AiRunStatus.Approved;
    }

    public void Discard(string discardedBy)
    {
        if (Status != AiRunStatus.PlanReady)
        {
            throw new InvalidOperationException("Only a ready plan can be discarded.");
        }

        if (string.IsNullOrWhiteSpace(discardedBy))
        {
            throw new ArgumentException("Discarding user is required.", nameof(discardedBy));
        }

        ApprovedBy = discardedBy.Trim();
        Status = AiRunStatus.Discarded;
    }
}

public sealed class PreviewEnvironment
{
    private PreviewEnvironment(Guid workItemId, string image, DateTimeOffset createdAt)
    {
        Id = Guid.NewGuid();
        WorkItemId = workItemId;
        Image = image;
        CreatedAt = createdAt;
        ExpiresAt = createdAt.AddDays(7);
    }

    public Guid Id { get; }
    public Guid WorkItemId { get; }
    public string Image { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset ExpiresAt { get; }

    public static PreviewEnvironment Create(Guid workItemId, string image, DateTimeOffset createdAt)
    {
        if (workItemId == Guid.Empty)
        {
            throw new ArgumentException("Work item id is required.", nameof(workItemId));
        }

        if (string.IsNullOrWhiteSpace(image))
        {
            throw new ArgumentException("Image is required.", nameof(image));
        }

        return new PreviewEnvironment(workItemId, image.Trim(), createdAt);
    }
}

public sealed record PreviewResourceSet(
    string Namespace,
    string Name,
    string Hostname,
    string Image,
    string ParentGateway,
    string? StaticHtml,
    bool IncludeNamespace)
{
    public static PreviewResourceSet Create(string key, string title, string image, string? staticHtml = null, string? namespaceOverride = null, bool includeNamespace = true)
    {
        if (string.IsNullOrWhiteSpace(image))
        {
            throw new ArgumentException("Image is required.", nameof(image));
        }

        var hostname = PreviewHostnames.ForWorkItem(key, title);
        var name = hostname.Split('.')[0];
        var @namespace = string.IsNullOrWhiteSpace(namespaceOverride) ? $"devops-preview-{name}" : namespaceOverride.Trim();
        return new PreviewResourceSet(@namespace, name, hostname, image.Trim(), "gateway/external", string.IsNullOrWhiteSpace(staticHtml) ? null : staticHtml.Trim(), includeNamespace);
    }
}

public static class PreviewManifestRenderer
{
    public static string Render(PreviewResourceSet resources)
    {
        var builder = new StringBuilder();
        if (resources.IncludeNamespace)
        {
            builder.AppendLine("apiVersion: v1");
            builder.AppendLine("kind: Namespace");
            builder.AppendLine("metadata:");
            builder.AppendLine($"  name: {resources.Namespace}");
            builder.AppendLine("  labels:");
            builder.AppendLine("    app.kubernetes.io/part-of: rosenvall-devops-preview");
            builder.AppendLine("---");
        }
        if (resources.StaticHtml is not null)
        {
            builder.AppendLine("apiVersion: v1");
            builder.AppendLine("kind: ConfigMap");
            builder.AppendLine("metadata:");
            builder.AppendLine($"  name: {resources.Name}-content");
            builder.AppendLine($"  namespace: {resources.Namespace}");
            builder.AppendLine("data:");
            builder.AppendLine("  index.html: |");
            builder.AppendLine(IndentBlock(resources.StaticHtml, 4));
            builder.AppendLine("---");
        }

        builder.AppendLine("apiVersion: apps/v1");
        builder.AppendLine("kind: Deployment");
        builder.AppendLine("metadata:");
        builder.AppendLine($"  name: {resources.Name}");
        builder.AppendLine($"  namespace: {resources.Namespace}");
        builder.AppendLine("  labels:");
        builder.AppendLine($"    app.kubernetes.io/name: {resources.Name}");
        builder.AppendLine("    app.kubernetes.io/part-of: rosenvall-devops-preview");
        builder.AppendLine("spec:");
        builder.AppendLine("  replicas: 1");
        builder.AppendLine("  selector:");
        builder.AppendLine("    matchLabels:");
        builder.AppendLine($"      app.kubernetes.io/name: {resources.Name}");
        builder.AppendLine("  template:");
        builder.AppendLine("    metadata:");
        builder.AppendLine("      labels:");
        builder.AppendLine($"        app.kubernetes.io/name: {resources.Name}");
        builder.AppendLine("        app.kubernetes.io/part-of: rosenvall-devops-preview");
        builder.AppendLine("    spec:");
        builder.AppendLine("      securityContext:");
        builder.AppendLine("        runAsNonRoot: true");
        builder.AppendLine("        runAsUser: 101");
        builder.AppendLine("        runAsGroup: 101");
        builder.AppendLine("        seccompProfile:");
        builder.AppendLine("          type: RuntimeDefault");
        builder.AppendLine("      containers:");
        builder.AppendLine("        - name: app");
        builder.AppendLine($"          image: {resources.Image}");
        builder.AppendLine("          securityContext:");
        builder.AppendLine("            allowPrivilegeEscalation: false");
        builder.AppendLine("            capabilities:");
        builder.AppendLine("              drop:");
        builder.AppendLine("                - ALL");
        builder.AppendLine("          ports:");
        builder.AppendLine("            - name: http");
        builder.AppendLine("              containerPort: 8080");
        if (resources.StaticHtml is not null)
        {
            builder.AppendLine("          volumeMounts:");
            builder.AppendLine("            - name: static-content");
            builder.AppendLine("              mountPath: /usr/share/nginx/html/index.html");
            builder.AppendLine("              subPath: index.html");
            builder.AppendLine("      volumes:");
            builder.AppendLine("        - name: static-content");
            builder.AppendLine("          configMap:");
            builder.AppendLine($"            name: {resources.Name}-content");
        }

        builder.AppendLine("---");
        builder.AppendLine("apiVersion: v1");
        builder.AppendLine("kind: Service");
        builder.AppendLine("metadata:");
        builder.AppendLine($"  name: {resources.Name}");
        builder.AppendLine($"  namespace: {resources.Namespace}");
        builder.AppendLine("spec:");
        builder.AppendLine("  selector:");
        builder.AppendLine($"    app.kubernetes.io/name: {resources.Name}");
        builder.AppendLine("  ports:");
        builder.AppendLine("    - name: http");
        builder.AppendLine("      port: 8080");
        builder.AppendLine("      targetPort: http");
        builder.AppendLine("---");
        builder.AppendLine("apiVersion: gateway.networking.k8s.io/v1");
        builder.AppendLine("kind: HTTPRoute");
        builder.AppendLine("metadata:");
        builder.AppendLine($"  name: {resources.Name}");
        builder.AppendLine($"  namespace: {resources.Namespace}");
        builder.AppendLine("spec:");
        builder.AppendLine("  parentRefs:");
        builder.AppendLine("    - name: external");
        builder.AppendLine("      namespace: gateway");
        builder.AppendLine("      sectionName: https");
        builder.AppendLine("  hostnames:");
        builder.AppendLine($"    - {resources.Hostname}");
        builder.AppendLine("  rules:");
        builder.AppendLine("    - matches:");
        builder.AppendLine("        - path:");
        builder.AppendLine("            type: PathPrefix");
        builder.AppendLine("            value: /");
        builder.AppendLine("      backendRefs:");
        builder.AppendLine($"        - name: {resources.Name}");
        builder.AppendLine("          port: 8080");
        return builder.ToString();
    }

    private static string IndentBlock(string value, int spaces)
    {
        var indent = new string(' ', spaces);
        var lines = value.Replace("\r\n", "\n").Split('\n');
        return string.Join('\n', lines.Select(line => $"{indent}{line}"));
    }
}

public static partial class PreviewHostnames
{
    private const string PreviewDomain = "rosenvall.se";
    private const int PreviewSlugBudget = 48;
    private static readonly Regex UnsafeCharacters = UnsafeLabelCharactersRegex();
    private static readonly Regex DuplicateDashes = DuplicateDashesRegex();

    public static string ForWorkItem(string key, string title)
    {
        var label = Slugify($"{key}-{title}");
        if (label.Length > PreviewSlugBudget)
        {
            label = label[..PreviewSlugBudget].Trim('-');
        }

        return $"{label}.{PreviewDomain}";
    }

    private static string Slugify(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD).ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);

        foreach (var character in normalized)
        {
            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character);
            if (category != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        var safe = UnsafeCharacters.Replace(builder.ToString(), "-");
        return DuplicateDashes.Replace(safe, "-").Trim('-');
    }

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.Compiled)]
    private static partial Regex UnsafeLabelCharactersRegex();

    [GeneratedRegex("-{2,}", RegexOptions.Compiled)]
    private static partial Regex DuplicateDashesRegex();
}
