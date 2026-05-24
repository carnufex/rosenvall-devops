# Homelab GitOps AI Workflow Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the first production-grade repo workflow for `rosenvalls-homelab`: a board linked to the GitHub repo, board-specific AI skills/instructions, AI plans that can stop and ask questions, implementation through Git branches/PRs only, persistent AI session context per card, live implementation feedback, and ArgoCD status read from Kubernetes CRDs.

**Architecture:** Extend the existing board/repository model with GitOps and AI-context settings, then feed those settings into the existing Codex planning and repository implementation paths. Keep cluster mutation PR-first: Codex edits the GitOps repo in a Kubernetes job, pushes a branch, opens a PR, and ArgoCD feedback is displayed separately from `applications.argoproj.io`.

**Tech Stack:** ASP.NET Core minimal API, EF-backed `DevOpsStore` snapshot state, React/TypeScript frontend, GitHub App installation tokens, Codex CLI, Kubernetes Jobs, `kubectl`, ArgoCD Application CRDs.

---

## Current Baseline

- `ai-plan.md` in the repo root contains the product plan that this implementation plan expands.
- Existing repo support already includes GitHub App repository discovery, board repository links, board secrets, `ImplementationRunDto`, Kubernetes implementation jobs, Codex CLI execution, PR creation, and per-card `AiSessionDto`.
- Existing preview work is separate from repo implementation. Keep React preview behavior intact.
- Existing untracked `.playwright-mcp/` is unrelated and must not be removed or staged unless explicitly requested.

---

## Requirements

- Add `gitops-homelab` as an implementation profile for boards/repositories.
- Add board-level GitOps settings:
  - allowed repo paths
  - ArgoCD namespace
  - ArgoCD Application label selector
- Add board-level AI context:
  - free-form board instructions
  - ordered skill references/names
  - ask-when-uncertain guidance
- Do not inline all skill documents into every prompt. Store skill references and inject them as references so Codex can resolve relevant skills through its server-side skill/CODEX_HOME environment.
- Add AI `NeedsInput` behavior:
  - planning can produce blocking questions instead of an implementable plan
  - implementation is disabled until the user answers and a revised plan exists
- Keep v1 GitOps workflow PR-first:
  - no direct `kubectl apply`
  - no auto-merge
  - all cluster changes go through branch + pull request
- Make repository implementation safer:
  - prompt identifies GitOps mode, allowed paths, board instructions, enabled skill references, and PR-first rules
  - runner fails clearly if generated changes touch paths outside board scope
  - runner emits live `RDO_STEP` markers for clone, inspect, implement, validate, push, PR ready, and failure
- Add ArgoCD feedback:
  - read `applications.argoproj.io` via `kubectl` with service account/RBAC
  - show sync/health/status for board-matched applications
  - explain missing CRD, missing RBAC, and no matching applications

---

## Task 1: Persist Board GitOps And AI Context Settings

**Files:**
- Modify: `src/Rosenvall.DevOps.Api/Program.cs`
- Modify: `tests/Rosenvall.DevOps.Tests/DevOpsStoreTests.cs`
- Modify: `frontend/src/App.tsx`

- [ ] Add DTOs:
  - `BoardGitOpsSettingsDto(Guid BoardId, IReadOnlyList<string> AllowedPaths, string ArgoNamespace, string ArgoApplicationSelector)`
  - `BoardAiContextDto(Guid BoardId, string Instructions, IReadOnlyList<string> EnabledSkills, bool AskWhenUncertain)`
- [ ] Extend `BoardDto` with nullable `GitOpsSettings` and `AiContext`.
- [ ] Extend `CreateBoardRequest` with nullable GitOps and AI-context fields.
- [ ] Extend persisted snapshot state with board GitOps settings and board AI context records.
- [ ] Add store methods:
  - `GetBoardGitOpsSettings(Guid boardId)`
  - `UpsertBoardGitOpsSettings(Guid boardId, BoardGitOpsSettingsRequest request)`
  - `GetBoardAiContext(Guid boardId)`
  - `UpsertBoardAiContext(Guid boardId, BoardAiContextRequest request)`
- [ ] Add endpoints:
  - `PUT /api/boards/{boardId}/gitops-settings`
  - `PUT /api/boards/{boardId}/ai-context`
- [ ] Default behavior:
  - no settings means normal existing behavior
  - `gitops-homelab` boards default to `AllowedPaths = ["apps/", "clusters/", "infrastructure/"]`, `ArgoNamespace = "argocd"`, empty selector, `AskWhenUncertain = true`
- [ ] Add backend tests proving create/reopen persistence for GitOps settings and AI context.

---

## Task 2: Add `gitops-homelab` Board/Profile UI

**Files:**
- Modify: `frontend/src/App.tsx`

- [ ] Add `gitops-homelab` to implementation profile choices.
- [ ] In board creation, show GitOps fields when `gitops-homelab` is selected:
  - allowed paths textarea/input
  - ArgoCD namespace
  - ArgoCD Application selector
  - board AI instructions textarea
  - enabled skill references textarea/list
  - ask-when-uncertain toggle
- [ ] In Settings view, add editable sections for:
  - GitOps settings
  - Board AI context
- [ ] Keep forms compact and operational; avoid explanatory marketing text.
- [ ] Ensure GitHub App boards can select `gitops-homelab`.
- [ ] Run `cd frontend && npm run build`.

---

## Task 3: Add `NeedsInput` Planning Semantics

**Files:**
- Modify: `src/Rosenvall.DevOps.Core/DevOpsDomain.cs`
- Modify: `src/Rosenvall.DevOps.Api/Program.cs`
- Modify: `frontend/src/App.tsx`
- Modify: `tests/Rosenvall.DevOps.Tests/DevOpsStoreTests.cs`

- [ ] Add `NeedsInput` to `AiRunStatus`.
- [ ] Add an `AiRun` representation for questions. Preferred minimal approach:
  - keep `Plan` as the rendered body
  - mark status `NeedsInput`
  - body begins with a clear `Questions` section
- [ ] Update plan providers’ prompts:
  - for repo/GitOps boards, instruct AI to return explicit blocking questions when required facts are missing
  - do not guess namespaces, app names, permanent-vs-temporary intent, deletion scope, or allowed paths
  - include board instructions and enabled skill references
- [ ] Add server-side plan classifier:
  - if generated plan contains a clear questions marker such as `Questions:` or `Needs input:`, store as `NeedsInput`
  - otherwise store as `PlanReady`
- [ ] Update approval/implementation guard:
  - `NeedsInput` plans cannot be approved or implemented
  - return a 409 with an actionable message
- [ ] UI behavior:
  - show `NeedsInput` plan as questions needing answers
  - hide/disable Implement for `NeedsInput`
  - keep “Generate revised AI plan” visible after user comments with answers
- [ ] Tests:
  - `NeedsInput` cannot be implemented
  - revised plan after comments can become `PlanReady`

---

## Task 4: Inject Board AI Context Into Planning And Implementation

**Files:**
- Modify: `src/Rosenvall.DevOps.Api/Program.cs`
- Modify: `tests/Rosenvall.DevOps.Tests/DevOpsStoreTests.cs`

- [ ] Include board AI context in `WorkItemDetailDto` or a nested planning context used by prompt builders.
- [ ] Planning prompt must include:
  - board instructions
  - enabled skill names/references
  - ask-when-uncertain policy
  - repository profile
  - GitOps settings when present
- [ ] Repository implementation prompt must include:
  - board instructions
  - enabled skill names/references
  - allowed paths
  - PR-first rule
  - ArgoCD/GitOps context
- [ ] Do not paste skill file contents into prompts. Use references like:
  - `Enabled board skills: kubernetes, argocd, gitops-homelab`
  - `Use these skills when relevant; do not assume unrelated skills are active.`
- [ ] Tests:
  - planning prompt contains skill references and instructions
  - implementation manifest prompt contains skill references and instructions
  - generated prompt does not contain full fake skill body when only a skill name is configured

---

## Task 5: Enforce Allowed Paths In Repository Implementation Jobs

**Files:**
- Modify: `src/Rosenvall.DevOps.Api/Program.cs`
- Modify: `tests/Rosenvall.DevOps.Tests/DevOpsStoreTests.cs`

- [ ] Add allowed paths to implementation job environment, encoded safely.
- [ ] After Codex edits and before commit, run a shell validation in the job:
  - collect changed files with `git status --porcelain`
  - fail with `RDO_FAILURE=Changed files outside allowed GitOps paths` if any path is outside the board allowed paths
- [ ] Add `RDO_STEP=Inspecting` before prompt execution.
- [ ] Rename/align validation marker to `RDO_STEP=Validating`.
- [ ] Keep `RDO_STEP=Testing` only for actual test/build attempts, if retained.
- [ ] Ensure failures are visible in `ImplementationRunPanel`.
- [ ] Tests:
  - manifest contains allowed-path validation script
  - manifest emits expected `RDO_STEP` markers
  - manifest contains failure marker for out-of-scope changes

---

## Task 6: Add ArgoCD Application Status Reader

**Files:**
- Modify: `src/Rosenvall.DevOps.Api/Program.cs`
- Modify: `tests/Rosenvall.DevOps.Tests/DevOpsStoreTests.cs`
- Modify: `frontend/src/App.tsx`

- [ ] Add `GitOpsApplicationStatusDto`:
  - `Name`
  - `Namespace`
  - `SyncStatus`
  - `HealthStatus`
  - `Revision`
  - `Message`
  - `Url`
  - `UpdatedAt`
- [ ] Add `GitOpsStatusReader` or equivalent service using existing `PipelineJobOrchestrator`/kubectl pattern.
- [ ] Endpoint:
  - `GET /api/boards/{boardId}/gitops/applications`
- [ ] Query:
  - `kubectl get applications.argoproj.io -n {argoNamespace} -l {selector} -o json`
  - if selector is empty, query all apps in namespace and return a clear message if none match board context
- [ ] Failure handling:
  - missing CRD => return status payload/message explaining ArgoCD Application CRD was not found
  - forbidden/RBAC => return message explaining service account lacks access
  - namespace not found => return message explaining namespace is missing
- [ ] UI:
  - add compact GitOps/ArgoCD panel on board or settings
  - show sync and health badges
  - show actionable explanation for missing CRD/RBAC/no apps
- [ ] Tests:
  - parser handles healthy/synced
  - parser handles degraded/progressing
  - failures map to actionable messages

---

## Task 7: Wire Frontend Data Refresh And Live Feedback

**Files:**
- Modify: `frontend/src/App.tsx`

- [ ] Load board GitOps/AI context with board DTOs.
- [ ] Poll or refresh ArgoCD status when board view/settings opens.
- [ ] Keep existing SignalR-driven implementation run updates.
- [ ] Ensure implementation panel never appears idle while a run is pending:
  - show spinner/active state for `Queued`, `Cloning`, `Inspecting`, `Implementing`, `Validating`, `Testing`, `Pushing`
  - show terminal output as it updates
- [ ] Display `NeedsInput` questions in AI plans panel.
- [ ] Disable implementation buttons when:
  - selected plan is `NeedsInput`
  - no GitHub-backed repository is linked
  - repo profile requires GitOps settings but they are missing
- [ ] Run `cd frontend && npm run build`.

---

## Task 8: Verification And Commit

**Commands:**
- `dotnet test .\tests\Rosenvall.DevOps.Tests\Rosenvall.DevOps.Tests.csproj -c Release`
- `dotnet test .\Rosenvall.DevOps.slnx -c Release`
- `cd frontend && npm run build`

- [ ] Verify no unrelated `.playwright-mcp/` files are staged.
- [ ] Commit the plan file first if not already committed.
- [ ] Commit implementation slices in coherent commits.
- [ ] Do not mark the goal complete until every requirement in `ai-plan.md` has direct evidence:
  - persisted GitOps settings
  - persisted board AI context
  - skill references injected without full skill inlining
  - `NeedsInput` behavior and UI guard
  - PR-first repo implementation
  - allowed-path validation
  - live implementation statuses/logs
  - ArgoCD status endpoint/UI
  - passing backend tests
  - passing frontend build

