# RDO Architecture And Quality Review - 2026-05-30

## Scope

This review looked for code-quality risks, implementation improvements, and pragmatic next slices in the current `rosenvall-devops` workspace.

Evidence gathered locally:

- `Program.cs` currently has about 15,750 lines, 102 `api.Map*` endpoint mappings, and 109 total `api/app.Map*` mappings.
- `frontend/src/App.tsx` currently has about 6,505 lines.
- `tests/Rosenvall.DevOps.Tests/DevOpsStoreTests.cs` currently has about 4,931 lines.
- `src/Rosenvall.DevOps.Core/DevOpsDomain.cs` currently has about 494 lines.
- `frontend/src/styles.css` currently has about 648 lines.
- Current working diff before this report was already large across backend, frontend, tests, and frontend dependencies.
- Verification run during review:
  - `dotnet test .\Rosenvall.DevOps.slnx -c Release` passed: 211 tests.
  - `cd frontend && npm test -- --runInBand` passed: 54 tests.
  - `cd frontend && npm run build` passed.

This is intentionally focused on the active architecture and delivery surface: LocalGit/Forgejo, Source, provider sync, preview/PR runners, state persistence, and the large frontend shell.

The later passes intentionally revisit some issues from different angles. For example, runner credential exposure appears both as a product/security boundary problem and as a manifest-rendering problem. Treat repeated findings as reinforcement, not as separate unrelated defects.

Finding count in this report:

- P0: 7
- P1: 69
- P2: 54

## Executive Summary

RDO is moving quickly and the product workflow is now much richer: LocalGit, preview-first delivery, PR review, source browsing, app hosting, hierarchy, goals, and syntax highlighting all exist in some form. The largest risk is that most of that behavior is concentrated in a few large files and one snapshot-backed in-memory store. That is workable for a prototype, but every new flow now increases the chance of hidden lifecycle bugs, stale UI states, and cleanup gaps.

The highest-priority security issues found in this review are the runner credential boundary and the GitHub webhook deploy path. Codex implementation/review-fix runs still receive `ROSENVALL_GIT_TOKEN` and board secret environment variables, and Kubernetes Codex jobs also expose reusable `CODEX_HOME` auth files in the same prompt-driven container. Prompt-injected repo or review content can therefore ask Codex to read or print runtime credentials. GitHub merge webhooks are also not signature-verified before RDO queues production deployment and preview cleanup. The highest-priority product issue is the production app source contract. Preview source is used to render production apps, but Local PRs can receive additional AI review-fix commits after preview promotion. That means "Approve PR" can merge one diff while production deploys older preview source. The LocalGit approval endpoint also merges the PR before production deployment succeeds, so a deployment failure leaves an irreversible merged PR with an uncompleted card. The highest-priority cleanup issue is provider-sync cleanup: provider-sync jobs and token secrets use their own renderer names, board cleanup currently deletes generic pipeline job names and does not delete provider-sync token secrets, and provider-sync runs do not have a durable completion observer.

The best next implementation direction is not a broad rewrite. Split by vertical runtime domains, starting with the newest/highest-risk flows: Source/provider sync, LocalGit PR review, and runner manifests. Keep the UI moving, but move new code into feature modules and typed service boundaries instead of expanding `Program.cs` and `App.tsx` further.

## Recommended First Tickets

Use these as the first backlog slice set if this report is converted into RDO cards:

1. Remove repository tokens and board secrets from the `codex exec` process environment.
2. Verify GitHub webhook signatures before triggering production deploy or preview cleanup.
3. Deploy production apps from the merged PR commit, not from stale stored preview source.
4. Make LocalGit approval deploy first or use a compensating state that does not mark approval complete until deploy/readiness succeeds.
5. Add provider-sync cleanup for `provider-sync-*` Jobs and `provider-sync-token-*` Secrets.
6. Add a provider-sync observer that marks sync runs succeeded/failed after Kubernetes Job completion.
7. Split Source/provider-sync into a feature module with provider-aware error results.
8. Replace raw YAML/string manifest tests with typed YAML parse assertions for runtime jobs and Secrets.
9. Add per-actor quotas and idempotency keys for expensive actions, especially demo-triggered actions.
10. Move LocalGit PR review and AI plan review onto shared review primitives before adding Source comments.

## Implementation Progress

- 2026-05-30: Started ticket 1 by removing repository tokens and board secret values from the `codex exec` process environment in implementation and PR review-fix manifests. Remaining hardening from the same finding: isolate Codex auth material instead of exposing reusable `CODEX_HOME` files in the prompt-driven container.
- 2026-06-07: Mitigated the remaining Codex auth-file exposure window for Kubernetes Codex jobs. Implementation, PR review-fix, cleanup and preview-source manifests now run `codex exec` as a tracked child process and remove `CODEX_HOME/auth.json` plus `installation_id` while Codex is running, with manifest tests covering all four job families. A full launcher/broker boundary that never places reusable Codex auth material in the prompt-driven execution environment remains the stronger follow-up for this P0 finding.
- 2026-06-07: Fixed Kubernetes runner image drift for repository implementation, preview-promotion, PR review-fix and repository cleanup Jobs. Those manifests now use `Ai:Codex:KubernetesRunnerImage` when configured, matching preview-source behavior and keeping runner pods on the digest-pinned image selected by deployment config instead of falling back to mutable `:main`.
- 2026-05-30: Completed ticket 5 cleanup rendering for provider-sync runtime artifacts. Board and work-item cleanup manifests now delete `provider-sync-*` Jobs and `provider-sync-token-*` Secrets while preserving generic pipeline cleanup.
- 2026-05-30: Completed the syntax-highlighting test gap from the source/diff review. Source and LocalGit PR diff code surfaces use lazy Shiki token rendering with plaintext fallback and line-count preservation tests.
- 2026-05-30: Completed ticket 2 application-side webhook verification. GitHub merge webhooks now fail closed unless `GitHub:WebhookSecret` is configured and `X-Hub-Signature-256` matches the raw payload. Manifest conversion persists GitHub's returned `webhook_secret`, local startup reads it from the app Secret, and Homelab API deployment reads `GitHub__WebhookSecret` from `rosenvall-devops-github-app`.
- 2026-05-30: Completed ticket 3 production source handoff. Public app deployments now persist the exact deployable source snapshot read from the merged PR branch, including LocalGit review-fix commits, and manifest rendering prefers that snapshot over legacy preview source. GitHub webhook/reconcile and LocalGit approval avoid deploying stale preview source when merged repository source cannot be read.
- 2026-05-30: Completed ticket 4 LocalGit approval ordering. Open LocalGit PR approval now reads deployable source from the PR branch, applies the production app first, and merges/marks the PR merged only after production apply succeeds; source/render/apply failures leave the PR unmerged and retryable.
- 2026-05-30: Completed ticket 6 provider-sync observation. A dedicated provider-sync monitor now adopts `provider-sync-*` Kubernetes Jobs after API restarts and marks `ProviderSync` pipeline runs succeeded or failed from Job status instead of leaving them indefinitely running.
- 2026-05-30: Started ticket 7 Source/provider-sync module split. Source path normalization, clone command rendering, target provider normalization, and provider-aware Source error helpers now live in `RepositorySourceFeature` instead of top-level `Program.cs` helpers; the larger endpoint/auth-token extraction remains as the next step for this ticket.
- 2026-05-30: Started ticket 8 typed manifest assertions. Backend tests now parse selected Kubernetes runtime YAML with `YamlDotNet` for preview-promotion Jobs and provider-sync Jobs/Secrets, which caught and fixed provider-sync token Secret labels being rendered outside `metadata.labels`.
- 2026-05-30: Started ticket 9 expensive-action idempotency. Preview approval PR starts now write a persistent action-ledger entry keyed by actor, work item, repository and source preview, and duplicate starts return the existing non-failed preview-promotion run instead of creating another PR-creation job. Provider sync now reserves an actor/board/source/target idempotency key before creating a target repo and returns a `409` with the existing operation/run for duplicate requests. Broader per-actor quotas and idempotency coverage for the other expensive actions remain follow-up work in this finding.
- 2026-05-30: Started ticket 10 shared review primitives. Frontend review helpers now route PR file counts and AI plan run counts through shared unresolved/comment-count primitives keyed by arbitrary anchors, giving Source comments a common base instead of a third bespoke implementation.
- 2026-05-30: Completed the repository-only pipeline authorization split from the fourth pass. Repository-only pipeline creation and updates now use mutating repository access instead of read visibility, so Viewer users can still browse Source but cannot trigger runner-side repository mutations.
- 2026-05-30: Completed the LocalGit runner API error handling slice from the twenty-fourth pass. LocalGit implementation and preview-promotion runners now check Forgejo PR creation HTTP status, parse response JSON with `jq`, emit sanitized `RDO_FAILURE` messages for non-2xx responses, and the API runner image installs `jq`.
- 2026-05-30: Completed a Source provider capability slice. Board Source repository lists now include `sourceReadable` and `sourceUnavailableReason`, and the frontend keeps unsupported providers visible for clone/sync context while disabling tree/file loading with a clear message.
- 2026-05-30: Completed the first CI coverage slice. The frontend GitHub Actions job now runs `npm test -- --runInBand` before `npm run build`, with a static regression test to keep that gate in place.
- 2026-05-30: Completed a Source ref-input stability slice. The Source page now keeps a draft ref while typing and only reloads repository trees after Enter/blur commits the normalized ref, reducing avoidable provider requests.
- 2026-05-30: Completed the image-publish gating slice. The image publishing workflow now runs after successful `CI` via `workflow_run` (manual dispatch still allowed), checks out the tested commit SHA, and tags API/frontend/preview-base images with that tested SHA.
- 2026-05-30: Completed the browser security headers slice. The API applies CSP, frame, MIME-sniffing, referrer, permissions and HSTS headers, and the frontend nginx config emits matching headers for the RDO UI without allowing inline scripts.
- 2026-05-30: Completed the local stop-script cleanup slice. Local startup records API, frontend and Forgejo port-forward PIDs in `.codex/devops-logs/local-demo.pids.json`, and the stop script now stops recorded processes first, falls back to command-line matches for API/Vite/API port-forward/Forgejo port-forward, removes the PID file and reports any remaining local demo processes.
- 2026-05-30: Completed the repository-scoped GitHub source credential slice. Source reads, deployable source snapshots and provider-sync source credentials now resolve GitHub installation tokens from the repository owner context instead of the actor's default installation; configured fallback tokens are only used when no repository-owner installation is known.
- 2026-05-30: Completed the provider-sync pending-link slice. Synced target repositories are linked to boards as `PendingSync`, switch to `Ready` only after the provider-sync monitor observes Job success, switch to `Failed` on submission/runtime failure, and Source disables browsing for non-ready target links with an explicit sync-state message.
- 2026-05-30: Completed the provider-sync credentialed-remote hardening slice. The provider-sync runner no longer constructs token-bearing Git remote URLs with shell string substitution; clone and push use a temporary `GIT_ASKPASS` helper with provider-specific usernames so secrets stay out of command arguments and remote URLs.
- 2026-05-30: Completed the Source ref validation slice. Source tree/file endpoints now normalize refs through `RepositorySourceFeature.NormalizeSourceRef`, allow branch/tag-like refs and full commit SHAs, reject control/whitespace/path traversal/lock-file forms, and return field-specific validation errors for invalid `ref` values.
- 2026-05-30: Completed the structured frontend API error slice. The frontend API client now throws `ApiError` with status, title, detail, type, validation errors, raw body, request path and retry-after while preserving concise `message` text for existing toasts; API-unavailable banners can branch on structured status instead of brittle message text.
- 2026-05-30: Completed the authentication fail-closed slice. Startup now resolves an explicit `Authentication:Mode`, disallows `DisabledForLocalDevelopment` outside Development, requires authority/audience when auth is required, exposes the resolved mode on `/api/status`, and local startup scripts set `Required` only when auth is enabled.
- 2026-05-30: Completed the normal comment identity slice. Public work-item comment create/update/delete endpoints now derive author/actor from claims instead of accepting client-supplied `author`, `kind` or `actor`, and new human comments persist `AuthorSubject` so edit/delete ownership checks are subject-based while legacy display-name comments still load.
- 2026-05-30: Completed the delivery audit actor slice. Public delivery mutation endpoints now derive audit actors from `ClaimsPrincipal`, frontend delivery actions no longer submit `actor`/`approvedBy`/`discardedBy`, and compatibility request DTOs accept empty payloads while endpoint boundaries ignore spoofable identity fields.
- 2026-05-30: Completed a Source provider-error slice. Source tree/file endpoints now wrap LocalGit/GitHub reads in provider-aware error mapping so unavailable providers, upstream HTTP failures and unreadable JSON become targeted 503/502 responses instead of generic 500s; source paths are escaped segment-by-segment so nested files are addressed correctly.
- 2026-05-30: Completed the LocalGit readiness slice from the Forgejo bootstrap finding. `/api/status` and `/api/settings` now run an authenticated Forgejo `/user` readiness probe, so LocalGit availability and create-repository capability require the service credential to authenticate instead of only checking config presence; tests cover successful Basic auth and rejected credentials.
- 2026-05-30: Completed the LocalGit terminal-redaction slice. Runner and preview terminal sanitization now redacts `Authorization: Basic ...`, literal `Basic $forgejo_auth`, generic credentialed URLs such as `http://user:password@host`, and Forgejo/Gitea-style token prefixes while preserving the existing GitHub/Bearer/env-var redaction behavior.
- 2026-05-30: Started the runtime-secret-store consolidation slice. Board secret create/update/delete endpoints now use `IRuntimeSecretStore` instead of `kubectl apply/delete` manifests, and the board-secret payload helper renders Kubernetes API JSON with `data` rather than `stringData`; implementation/cleanup/provider-sync per-run token secrets still need the same treatment.
- 2026-05-30: Continued the runtime-secret-store consolidation slice for provider sync. `POST /boards/{boardId}/repositories/sync-to-provider` now stores `source-token` and `target-token` through `IRuntimeSecretStore` before queuing the provider-sync Job, and the provider-sync token payload helper renders Kubernetes API JSON with `data` and no last-applied annotation; implementation and cleanup per-run token secrets remain.
- 2026-05-30: Completed the per-run implementation/cleanup token portion of runtime-secret-store consolidation. Repository implementation, preview-promotion, PR review-fix and repository cleanup starts now write their per-run GitHub/LocalGit token Secrets through `IRuntimeSecretStore`; token payload helpers render API JSON with base64 `data` and no `stringData`/last-applied annotation. GitHub App bootstrap Secret rendering remains a separate platform-credential follow-up.
- 2026-05-30: Completed the GitHub App bootstrap portion of runtime-secret-store consolidation. GitHub App manifest conversion now writes `rosenvall-devops-github-app` through `IRuntimeSecretStore`, and `GitHubAppSecretRenderer` emits Kubernetes API JSON with base64 `data` and no `stringData`/last-applied annotation.
- 2026-05-30: Completed the board-secret metadata ordering slice. Board secret create/update endpoints now prepare metadata without persisting, write the Kubernetes Secret through `IRuntimeSecretStore`, and only commit snapshot metadata after the runtime Secret write succeeds, preserving existing metadata on write failures.
- 2026-05-30: Completed the preview-cleaner namespace guard slice. Preview manifests now label namespaces with `rosenvall.devops/managed-by`, board id and work-item id, and the Homelab preview cleaner requires the `devops-preview-` prefix plus those ownership labels before deleting an aged namespace.
- 2026-05-30: Completed the Forgejo bootstrap visibility slice. Homelab now uses a dedicated `rosenvall-devops-forgejo-bootstrap` Job to create or update the RDO Forgejo service user and removed the deployment `postStart || true` bootstrap path, so credential establishment failures are visible in Job status/logs.
- 2026-05-30: Mitigated the board-deletion LocalGit partial-state issue. Board cleanup now removes each board-owned LocalGit repository from RDO metadata immediately after that repository is successfully deleted in Forgejo, so a later transient delete failure leaves retryable board state without stale links to already-deleted repos. A full persisted board-cleanup run with resumable phases remains future hardening.
- 2026-05-30: Completed the dependency vulnerability CI gate slice. CI now runs `dotnet list Rosenvall.DevOps.slnx package --vulnerable --include-transitive` after .NET restore and `npm audit --omit=dev --audit-level=high` after frontend dependency install.
- 2026-05-30: Completed the first-use syntax-highlighting cost slice. The frontend Shiki integration now initializes theme/engine once, loads only the requested language grammar on demand, caches loaded languages, and keeps the existing safe React-token rendering and plaintext fallback behavior.
- 2026-05-30: Completed the Source request cancellation slice. The frontend API client now accepts caller abort signals, Source tree requests abort when ref/path/repository changes, and Source file requests abort when superseded by another file selection or unmount.
- 2026-05-30: Completed the repository-management copy cleanup slice. The legacy board action now says `Link existing repository`, the Source action says `Copy repository to provider`, and no-repository Add Board copy no longer describes the old GitHub-link flow as a generic sync.
- 2026-05-30: Completed the clone-info contract slice. `/api/repositories/{id}/clone-info` now returns separate human and runner clone URLs plus mode/explanation metadata, and the Source clone drawer avoids presenting internal-only LocalGit runner URLs as normal workstation clone commands.
- 2026-05-30: Completed a Source/provider-copy authorization guard slice. Demo sandbox tests now assert demo users can browse only their LocalGit sandbox repository, cannot see real GitHub repositories, and cannot copy repositories to GitHub; the provider-copy endpoint checks `CanSyncRepositoryToProvider` before resolving credentials, creating repositories or submitting Kubernetes Jobs.
- 2026-05-30: Completed a demo-sandbox production-guardrail status slice. `/api/status` now includes demo sandbox policy diagnostics showing whether the demo user exists and whether demo users are isolated to `Demo Sandbox`, with a backend regression test covering the restricted visibility contract.
- 2026-05-30: Completed a personal GitHub integration visibility slice. Personal GitHub App installations are now visible/usable only to the actor who installed them or to an actor with matching GitHub user authorization for that account; demo users still see no GitHub installations.
- 2026-05-30: Completed a SignalR fail-closed guardrail slice. `Realtime:Enabled` now fails startup when authentication is enabled unless `Realtime:AllowUnsafeBroadcasts=true` explicitly acknowledges the current `Clients.All` broadcast risk; scoped board groups remain the full follow-up fix.
- 2026-05-30: Completed the API non-root Homelab hardening slice. The API image now sets `USER 1000:1000`, `HOME=/tmp`, and writable runtime directories, while the Homelab API Deployment runs as non-root UID/GID 1000 with `fsGroup`, a read-only root filesystem, and writable `/tmp` plus state/Codex PVC mounts; a static manifest/image regression test covers the contract.
- 2026-05-30: Started the runtime service-account split. Homelab now has separate `rosenvall-devops-api`, `rosenvall-devops-runtime`, and `rosenvall-devops-frontend` service accounts; preview manager and runtime secret cleanup bindings moved to the API identity, while the runtime identity is limited to a namespaced ConfigMap writer role for preview-source result publishing. Further identity split work remains for narrower API-side orchestration.
- 2026-05-30: Completed the frontend container hardening slice. The frontend Dockerfile now uses `nginxinc/nginx-unprivileged`, and the Homelab frontend Deployment runs as UID/GID 101 with no added capabilities, read-only root filesystem, writable `/tmp` and nginx cache mounts, and its own non-RBAC service account.
- 2026-05-30: Completed the first appsettings drift slice. Base API settings now default preview/pipeline kubeconfig paths to in-cluster auth and use LocalGit/InternalForgejo repository intent without the stale external `git.rosenvall.se` defaults; `appsettings.Development.json` carries the local `tofu/output/kubeconfig` discovery contract.

## Priority Findings

### P0: Codex Runs Can Read Runtime Credentials

Evidence:

- Implementation and PR review-fix manifests expose `ROSENVALL_GIT_TOKEN` as an environment variable.
- The shell unsets `GITHUB_TOKEN` before `codex exec`, but leaves `ROSENVALL_GIT_TOKEN` available to the Codex process.
- When `RepositoryRuns:ExposeBoardSecretsToCodex` is enabled, implementation manifests also render board/environment Secrets as normal environment variables through `RenderSecretEnvironment(...)`.
- Kubernetes Codex jobs copy `auth.json`, `config.toml`, `installation_id` and `models_cache.json` from the shared `rosenvall-devops-codex-home` PVC into `/app/codex-home`.
- `CODEX_HOME=/app/codex-home` is mounted in the same container that runs prompt-driven `codex exec --sandbox danger-full-access`.
- Runner output is copied back into RDO logs with `cat "$codex_log"`.

Impact:

- Repo content, work-item comments and review comments are all untrusted prompt inputs.
- A malicious or accidental instruction can make Codex reveal repository tokens, board secrets, Codex auth material, or other runtime credentials in logs or generated source.
- Even after `ROSENVALL_GIT_TOKEN` is removed from the Codex phase, `CODEX_HOME/auth.json` remains a sensitive prompt-accessible file unless it is isolated.

Recommended fix:

- Ensure Codex runs with no repository token env vars and no writable/readable credential material beyond what the CLI strictly needs.
- Split clone/push/PR operations into token-bearing phases and keep Codex in a tokenless phase.
- Prefer a small per-run credential broker or launcher that supplies Codex authentication without exposing `auth.json` as a normal workspace-readable file.
- Add manifest tests around every `codex exec` command:
  - no `ROSENVALL_GIT_TOKEN`,
  - no `GITHUB_TOKEN`,
  - no board secret env vars unless explicitly scoped to a post-Codex test/deploy phase,
  - no repository token secret mount,
  - no broad `CODEX_HOME` copy containing reusable auth files unless the threat model explicitly accepts it.

### P0: Provider-Sync Cleanup Misses Jobs And Token Secrets

Evidence:

- Provider sync creates `RepositoryProviderSyncJobManifestRenderer.JobName(run)` and `RepositoryProviderSyncJobManifestRenderer.TokenSecretName(run)` in `src/Rosenvall.DevOps.Api/Program.cs`.
- Board cleanup iterates board-level `_pipelineRuns`, but renders delete stubs with `PipelineJobManifestRenderer.JobName(run, repository)` and does not include `RepositoryProviderSyncJobManifestRenderer.TokenSecretName(run)`.

Impact:

- `Sync to new provider` can leave `provider-sync-*` Jobs and `provider-sync-token-*` Secrets in Kubernetes after board deletion.
- Those Secrets contain source/target provider credentials.
- The cleanup UI can report success while runtime artifacts remain.

Recommended fix:

- Add provider-specific pipeline cleanup rendering:
  - If `run.Stage == "ProviderSync"`, delete `RepositoryProviderSyncJobManifestRenderer.JobName(run)` and `RepositoryProviderSyncJobManifestRenderer.TokenSecretName(run)`.
  - Keep existing generic pipeline cleanup for older/manual pipeline runs.
- Add tests:
  - Board cleanup includes provider-sync job and token secret.
  - Work-item cleanup still handles normal pipeline jobs.
  - Cleanup excludes shared platform credentials.

### P1: Provider Sync Links Target Repository Before Sync Succeeds

Evidence:

- `POST /api/boards/{boardId}/repositories/sync-to-provider` creates the target provider repo, persists it in RDO, links it to the board, then creates the Kubernetes sync Secret and Job.

Impact:

- If token Secret creation or Job creation fails, the board can show a linked target repo that has not received source refs.
- If the Job starts but push fails, RDO has no clear completed/failed source-of-truth beyond the pipeline run status.

Recommended fix:

- Introduce a `ProviderSyncRun` state model with phases: `TargetCreated`, `RunnerQueued`, `Pushing`, `Succeeded`, `Failed`.
- Link the target repository as `PendingSync` until the runner reports success.
- Only show it as a normal secondary board repo once sync succeeds.
- Add a repair action for failed runs: retry push to existing target or delete target if RDO owns it.

### P1: Credential-Bearing Git Remotes Are Built In Shell

Evidence:

- Provider-sync runner builds authenticated remotes by injecting tokens into URLs via `sed`.
- Similar git flows rely on token-bearing remotes inside shell scripts.

Impact:

- Git generally redacts credentials in many contexts, but this is not a guarantee for every failure mode or future image.
- Shell string handling around URLs/tokens is fragile and hard to audit.

Recommended fix:

- Move runner credential handling to a safer helper:
  - `GIT_ASKPASS` script mounted from an in-memory file.
  - Or `.netrc` with `chmod 600`, removed before exit.
  - Or `git -c credential.helper=...` where the token is never embedded in the remote URL.
- Add regression tests that rendered manifests do not construct `https://user:token@...` style strings.

### P1: `Program.cs` Is Now Multiple Applications In One File

Evidence:

- `Program.cs` holds endpoint mappings, DTOs, store implementation, Kubernetes renderers, GitHub client, Forgejo client, Codex providers, background services, and snapshot serialization.
- It is about 15,750 lines.

Impact:

- Small features require editing a high-conflict file.
- Runtime domains bleed into each other: Source, PR review, Kubernetes runners, Auth, GitOps and AI planning are not isolated.
- Testing tends to become broad static assertions rather than focused service tests.

Recommended split, in this order:

1. `Features/Source`: source endpoints, DTOs, source service, provider source readers.
2. `Features/ProviderSync`: sync endpoints, run model, manifest renderer, monitor/reconciler.
3. `Features/LocalPullRequests`: diff, comments, approval/merge/deploy service.
4. `Runtime/Kubernetes`: manifest renderers, kubectl orchestration, runtime-secret store.
5. `State`: `DevOpsStore` interfaces grouped by use case, then gradually move off one snapshot aggregate.

Do not start by moving every DTO. Move one vertical slice with tests and leave forwarding wrappers if needed.

### P1: The Store Still Serializes The Whole Snapshot On Every Real Mutation

Evidence:

- `Persist()` builds a full `DevOpsSnapshot`, serializes it to JSON, hashes it, and writes/skips based on hash.
- This is much better than always writing, but it still pays the full serialization and hash cost for every mutation that reaches `Persist()`.

Impact:

- High-churn flows such as preview logs, terminal tails, health checks, comments and PR review can still create CPU and memory churn.
- The earlier OOM class of bugs is reduced, not structurally removed.

Recommended fix:

- Keep the snapshot model short-term, but introduce append-only tables for high-churn entities:
  - terminal lines,
  - preview events,
  - implementation run events,
  - PR review comments.
- Persist low-churn board/work-item metadata separately from terminal/log data.
- Add a budget test around snapshot size and persist frequency for a simulated long Codex run.

### P1: Frontend `App.tsx` Is Past The Point Where New UX Is Cheap

Evidence:

- `frontend/src/App.tsx` is about 6,505 lines.
- It contains top-level app state, auth, routing, board, source browser, work-item modal, AI review, PR diff review, comments, settings, teams, and helpers.

Impact:

- UI fixes are easy to make but hard to keep consistent.
- The same state is normalized in several places: development status, preview state, PR approval state, logs, diff state.
- Component tests are hard because most components are local to `App.tsx`.

Recommended split:

1. `features/source/SourceView.tsx`, `CloneDrawer.tsx`, `SyncToProviderModal.tsx`.
2. `features/pullRequests/PullRequestDiffReview.tsx`, `DiffSection.tsx`, review-comment hooks.
3. `features/workItems/WorkItemModal.tsx`, tab panels, hierarchy panel.
4. `features/ai/AiPlanPanel.tsx`, markdown review components.
5. `shared/code/CodeViewer.tsx`, `useHighlightedCode.ts`, `HighlightedTokens.tsx`.

The goal is not more abstraction. The goal is making each feature independently readable and testable.

### P2: Frontend Tests Are Useful But Too Many Are Source-String Assertions

Evidence:

- `frontend/src/boardChrome.test.ts` contains checks that read `App.tsx` and assert regexes such as rendered class names or absence of old component names.
- There is no React component test harness for the large views.

Impact:

- Tests can pass while behavior is broken, or fail because an implementation was renamed without behavior changing.
- Important UI flows such as Source navigation, clone drawer, PR comments, AI plan comments, and autosave are not directly exercised as rendered components.

Recommended fix:

- Add a small React test stack:
  - `vitest` or Node test + `@testing-library/react` + `jsdom`.
- Start with only three tests:
  - Source view opens tree and file content.
  - Local PR diff can click a line and create a comment.
  - AI plan markdown can expand and create an anchored comment.
- Keep pure helper tests in `boardChrome.test.ts`.

### P2: Source API Has Size And Binary Guards But No Ref Normalization Contract

Evidence:

- Source paths are normalized defensively.
- Refs are trimmed and passed through provider APIs.
- File content is capped at 1 MB and binary detection is null-byte based.
- File decoding uses `Encoding.UTF8.GetString(...)`; invalid UTF-8 bytes can be rendered with replacement characters if the file does not contain null bytes.

Impact:

- Behavior for tags, branch names with slashes, commit SHAs and invalid refs is provider-dependent.
- UI has a free-text ref field, so transient provider errors will be common.
- Some binary or non-UTF-8 text files can still show as unreadable text instead of a clean binary/unsupported state.

Recommended fix:

- Add branch/ref listing endpoint and make the default UI a selector with optional advanced free-text input.
- Add provider-neutral error DTOs for missing ref vs missing path vs provider unavailable.
- Use strict UTF-8 decoding or content-type/provider metadata when deciding whether a file is text-renderable.
- Add tests for branch names with slashes and invalid refs.

### P2: Shiki Integration Is Correct But Loads All Supported Languages At First Code View

Evidence:

- `codeHighlight.ts` lazy-loads Shiki on first code view.
- The first highlight loads all configured language modules.
- Build output shows separate lazy chunks for TypeScript, TSX, JavaScript, JSX, C#, shell, CSS, HTML, Markdown, YAML and the Shiki core.

Impact:

- Main bundle stays reasonable, but first Source/PR diff open can load more syntax machinery than needed.

Recommended fix:

- Keep current implementation for v1.
- Later, load only the detected language plus plaintext fallback:
  - cache highlighter state per language,
  - add language dynamically when first needed,
  - keep `github-dark` theme shared.

## New Implementation Proposals

### 1. Provider Abstraction As A Real Port

Create a narrow provider interface instead of expanding client-specific endpoint code:

```csharp
public interface IRepositoryProvider
{
    string Provider { get; }
    Task<RepositorySourceTreeDto?> GetTreeAsync(RepositoryDto repository, string reference, string path, CancellationToken ct);
    Task<RepositorySourceFileDto?> GetFileAsync(RepositoryDto repository, string reference, string path, CancellationToken ct);
    Task<RepositoryCreateResult> CreateRepositoryAsync(CreateRepositoryCommand command, UserContext actor, CancellationToken ct);
    Task<PullRequestDto?> GetPullRequestAsync(RepositoryDto repository, int number, CancellationToken ct);
    Task<PullRequestDiffDto> GetPullRequestDiffAsync(RepositoryDto repository, int number, CancellationToken ct);
}
```

Then keep GitHub and Forgejo differences behind provider classes. The endpoints become authorization + command dispatch rather than provider branching.

### 2. Runner Manifests As Typed Documents

Current manifest renderers are string templates. That has already caused YAML indentation bugs in prior slices.

Suggested direction:

- Create typed render helpers for common Kubernetes shapes:
  - Job metadata/spec,
  - security context,
  - projected secrets,
  - pod affinity,
  - token Secret.
- Serialize to YAML with a real serializer or at least centralize indentation helpers.
- Keep snapshot tests, but parse rendered YAML in unit tests before asserting content.

### 3. Delivery State Machine

RDO currently has several overlapping concepts: preview, implementation run, preview-promotion run, development, public app, pipeline run, cleanup run.

Introduce a normalized delivery timeline:

- `Plan`
- `PreviewSource`
- `PreviewDeploy`
- `PrCreation`
- `PrReview`
- `MergeAndDeploy`
- `Cleanup`

Each stage can point at its underlying concrete run. The UI can render tabs/logs from this one model instead of repeatedly reconciling local state.

### 4. Source And Diff Code Viewer Component

Source and PR diff now both need:

- line numbers,
- horizontal scroll,
- syntax tokens,
- path-based language detection,
- loading fallback,
- large-file handling.

Create one shared code-rendering layer:

- `CodeLine`
- `CodeToken`
- `CodeViewport`
- `useHighlightedCode`
- `DiffCodeLine` wrapper for add/delete/context metadata.

This keeps future line comments, search, copy line link and file-level comments from diverging.

### 5. Snapshot Store Exit Strategy

Do not replace the store in one step. Start with tables where write volume and correctness matter:

- `WorkItemEvents`
- `RunTerminalLines`
- `ReviewComments`
- `RuntimeArtifacts`
- `ProviderSyncRuns`

Keep snapshot for board/workspace shape until the domain stabilizes.

## Quick Wins

- Add provider-sync cleanup coverage and fix the cleanup renderer.
- Add `ProviderSyncRun` status to the UI so sync failures are visible instead of only showing a linked repo.
- Move `SourceView` and `PullRequestDiffReview` out of `App.tsx`.
- Parse rendered Kubernetes YAML in tests for every new manifest renderer.
- Add a bundle budget note for first code-view syntax highlighting payload.
- Add component tests for the three highest-value UI flows: Source open file, PR comment, AI plan comment.

## Second Pass: Security, Operations And Test Gaps

### P0: Runtime Secrets Still Go Through `kubectl apply`

Evidence:

- Board secrets are persisted as metadata only, but `POST /api/boards/{boardId}/secrets` and `PUT /api/boards/{boardId}/secrets/{secretId}` render a Kubernetes `Secret` manifest and call `PipelineJobOrchestrator.ApplyAsync`.
- Provider sync creates `provider-sync-token-*` with `RepositoryProviderSyncJobManifestRenderer.RenderTokenSecret(...)` and also applies it through `PipelineJobOrchestrator.ApplyAsync`.
- Both renderers use `data`, not `stringData`, which is good. The remaining problem is the apply mechanism itself.

Impact:

- `kubectl apply` can store the full applied object in `kubectl.kubernetes.io/last-applied-configuration`. For a Secret, that annotation can include base64 token material.
- This is the exact class of issue already fixed for GitHub user authorization tokens by adding a dedicated Kubernetes runtime-secret store.
- Provider-sync secrets are especially sensitive because they can carry both source and target provider credentials.

Recommended fix:

- Reuse or generalize `IRuntimeSecretStore` for all runtime Secrets:
  - GitHub user authorization tokens,
  - board environment secrets,
  - implementation/promotion repository token secrets,
  - cleanup token secrets,
  - provider-sync token secrets.
- Implement create-or-replace through the Kubernetes API, never `kubectl apply`.
- Keep labels and `data`, explicitly clear `kubectl.kubernetes.io/last-applied-configuration` if an older Secret already has it.
- Add regression tests that parse generated/stored Secret payloads and assert:
  - no `stringData`,
  - no last-applied annotation,
  - no raw token values,
  - cleanup still deletes the expected Secret names.

### P1: Provider-Sync Token Lifetime Has No Robust Cleanup Or Ownership

Evidence:

- Provider-sync token Secret names are generated by `RepositoryProviderSyncJobManifestRenderer.TokenSecretName(run)`.
- Board cleanup currently does not delete those names, and there is no owner reference or TTL-style cleanup visible in the rendered Secret.

Impact:

- If sync succeeds, fails, or the board is deleted, token Secrets can remain.
- Even after the cleanup-renderer bug is fixed, orphan cleanup still depends on RDO being available and the cleanup action being executed.

Recommended fix:

- Add provider-sync runtime artifacts to a first-class `RuntimeArtifact` record:
  - kind,
  - namespace,
  - name,
  - owning board/work item/run,
  - created timestamp,
  - sensitive flag.
- Cleanup should delete from this registry rather than rediscover names from several renderers.
- Add a periodic reconciler that removes sensitive runtime artifacts whose owning run is terminal or whose board no longer exists.

### P1: LocalGit Service Credential Is A Platform-Admin Capability

Evidence:

- LocalGit/Forgejo calls use a configured RDO service credential for create, read, merge, close, delete and source browsing.
- Demo isolation is handled in RDO authorization, not by per-user Forgejo credentials.

Impact:

- This is acceptable for an internal-only v1, but every authorization miss in RDO becomes high impact because the backend credential can operate across RDO-owned LocalGit repositories.
- Delete/sync/merge actions need stronger auditability than normal board mutations.

Recommended fix:

- Keep Forgejo internal-only, but add explicit audit events for:
  - LocalGit repo create/delete,
  - LocalGit PR merge/close,
  - provider sync target create/push,
  - board deletion that deletes LocalGit repos.
- If Forgejo supports narrower scoped tokens for the configured version, use separate service tokens per capability:
  - source read,
  - repo create,
  - PR merge,
  - repo delete.
- Add authorization tests for Source and clone-info endpoints against demo and non-demo users, not only board creation.

### P1: Deployment Drift Is Still A Product Risk

Evidence:

- Several recent failures were not caused by code logic alone; they were caused by older API/frontend images still running while the local UI or Homelab config expected newer behavior.
- Homelab pins image digests, while local development can run source directly or port-forward deployed API/frontend.
- The local startup script has grown to coordinate Authentik, kubeconfig, preview-source mode, Forgejo port-forwarding, cluster secrets and Vite.

Impact:

- The user can see a new frontend calling endpoints that the running API does not have.
- Debugging becomes slower because "is the fix deployed?" is not visible in the product.

Recommended fix:

- Add build identity to both API and frontend:
  - git SHA,
  - image digest if present,
  - build timestamp,
  - configuration mode: local API, port-forward API, deployed API.
- Expose this through `/api/status` and show a small diagnostics panel in Settings.
- Make the frontend warn when its build SHA is newer than the API SHA or when API reports an older schema version.
- Add `scripts/doctor-local-demo.ps1` that checks:
  - API health,
  - frontend health,
  - Authentik mode,
  - Forgejo port-forward,
  - kubeconfig,
  - runner image digest,
  - Source/diff endpoint availability.

### P2: Test Coverage Is Broad But Still Misses Lifecycle Edges

Evidence:

- Backend has useful tests for LocalGit clients, source reads, provider-sync manifest content, preview recovery and Homelab config.
- The strongest cleanup bug found in this review still slipped through because tests cover normal implementation/cleanup Secrets but not provider-sync cleanup.
- Frontend tests still include many source-string checks, which are useful as cheap regressions but not enough for complex UI flows.

Recommended tests to add next:

- Backend:
  - provider-sync cleanup includes job and token Secret;
  - board-secret and provider-sync Secret writes use runtime-secret store, not kubectl apply;
  - Source tree/file/clone-info reject users without board access;
  - demo user can read only demo LocalGit source;
  - sync target remains pending until push success;
  - failed sync target is visible and repairable.
- Frontend component/e2e:
  - Source page opens a repo, navigates folders and shows highlighted code;
  - Clone drawer displays internal-only LocalGit copy;
  - Sync modal blocks GitHub without matching personal authorization;
  - Local PR diff comment and approve flow;
  - AI plan comment and revise flow.

### Recommended Next Three Slices

1. Secret safety and provider-sync cleanup:
   - Replace provider-sync and board-secret `kubectl apply` with runtime-secret store operations.
   - Add provider-sync cleanup documents and tests.
   - Add a one-time cleanup for old `provider-sync-token-*` annotations.

2. Deployment/version diagnostics:
   - Add API/frontend build identity.
   - Add Settings diagnostics and local doctor script.
   - Include runner image digest and Homelab pinned digest in status.

3. Source/provider sync hardening:
   - Add `ProviderSyncRun` state.
   - Keep target repo in `PendingSync` until refs are pushed.
   - Add repair/retry/delete-target actions for failed syncs.

## Third Pass: Frontend State And Realtime Delivery

### P1: Frontend Uses Full Refreshes Where It Needs Targeted State Updates

Evidence:

- `loadShell()` fetches workspaces, boards, repositories, settings, preview environments, preview events, pipelines, timeline, metrics, assignees, `/api/me`, teams, GitHub integrations, board secrets and GitOps applications.
- Most actions call `refreshAfterChange()`, which calls `loadShell()` and then often reloads the selected work item as well.
- Autosave also calls `refreshAfterChange(id)` after a successful field patch.

Impact:

- Simple edits such as title/status/assignee can trigger a broad API refresh.
- This increases perceived latency and raises the chance of overwriting local UI state during active editing.
- It makes action-specific loading states harder because one global refresh drives many unrelated panels.

Recommended fix:

- Introduce a small client-side board cache with targeted patch operations:
  - update one work item after `PATCH /work-items/{id}`;
  - update one work item detail after comment/AI/PR changes;
  - refresh board-level collections only when a board, repository, team, or secret actually changed.
- Split `refreshAfterChange()` into explicit methods:
  - `refreshBoardSummary()`,
  - `refreshWorkItem(id)`,
  - `refreshRepositories()`,
  - `refreshBoardSecrets()`,
  - `refreshTimeline(boardId)`.
- Keep broad reload as a recovery fallback, not the default success path.

### P1: SignalR Is Published By The API But Not Used By The Frontend

Evidence:

- The API maps `/hubs/devops` and sends many events: `workItemChanged`, `previewChanged`, `implementationRunChanged`, `repositoryCleanupRunChanged`, `aiRunChanged`, comment events, board deletion and epic events.
- The frontend has no visible `@microsoft/signalr` dependency or hub connection in `frontend/src/App.tsx`.
- The frontend instead polls preview status every 2 seconds and GitOps status every 15 seconds, then does full refreshes.

Impact:

- The server already pays the complexity cost of event publishing, but the UI does not get the main benefit.
- Users see stale or confusing statuses until polling catches up or a full refresh completes.
- Polling plus full snapshot persistence increases load on exactly the paths that previously caused memory pressure.

Recommended fix:

- Add a frontend SignalR client that subscribes to board/work-item scoped events.
- Use events to invalidate or patch specific entities:
  - `previewChanged` patches the selected work item preview and board card preview status;
  - `implementationRunChanged` patches the run in the selected work item;
  - `workItemChanged` patches board columns and selected detail;
  - comment events patch only the comment list.
- Keep polling as fallback when the hub disconnects.
- Add a Settings diagnostics indicator for hub connected/reconnecting/disconnected.

Important caveat:

- Do not simply connect the frontend to the existing hub event stream as-is.
- The API currently broadcasts with `hub.Clients.All.SendAsync(...)` in many endpoint and monitor paths.
- When auth is enabled the hub can require an authenticated user, but broadcast-to-all still ignores board/workspace authorization.
- Before using the hub in the UI, add board/workspace-scoped SignalR groups and send events only to authorized clients for that scope.
- Add tests around hub target selection or move event publishing behind an `IRealtimeNotifier` that accepts a board/work-item id and resolves authorized recipients.

### P1: `BoardActions` Has Become A Command Bus Hidden Inside `App.tsx`

Evidence:

- `BoardActions` now exposes more than 40 async actions: board creation, card mutation, hierarchy, AI, preview, implementation, GitHub, board secrets, settings, PR review comments, AI plan comments, teams and drag/drop.
- The implementation lives inside the root `App` component and closes over shell state, selected state, actor, provider settings and toast state.

Impact:

- It is hard to reason about which action refreshes which data and which pending label is shown.
- Feature panels are coupled to global app concerns.
- Moving one view out of `App.tsx` requires carrying a very wide action object with it.

Recommended fix:

- Replace `BoardActions` with feature-specific command hooks:
  - `useWorkItemCommands`,
  - `useAiPlanCommands`,
  - `usePreviewCommands`,
  - `usePullRequestCommands`,
  - `useBoardSettingsCommands`,
  - `useSourceCommands`.
- Each hook owns:
  - action-specific pending state,
  - optimistic patch/update,
  - toast wording,
  - refresh scope.
- This pairs naturally with moving tab panels out of `App.tsx`.

### P2: Preview And Run Monitoring Still Has Multiple Timeout Models

Evidence:

- Preview health uses a hard-coded 3 minute timeout in `PreviewHealthMonitor`.
- Preview-source Kubernetes jobs use `Ai:Codex:PreviewSourceJobTimeoutSeconds`, now 600 seconds.
- Implementation and cleanup monitors use a 10 minute "no logs" stuck timeout.

Impact:

- These values may all be correct, but they are encoded in different places and exposed to the UI differently.
- Earlier incidents already came from timeout drift between local API, deployed API and Kubernetes job deadline.

Recommended fix:

- Centralize runtime timeout settings in one typed options object:
  - preview-source job timeout,
  - preview health timeout,
  - implementation stuck timeout,
  - cleanup stuck timeout,
  - reconcile interval.
- Include effective timeout values in `/api/status`.
- Render timeout values in diagnostics and in the relevant log panel when a run starts.

### P2: Source And Diff Highlighting Should Share A Rendering Contract

Evidence:

- Syntax highlighting was added for Source and Local PR diff.
- Source uses line-numbered code; diff uses continuous sections with old/new line anchors and review comments.

Impact:

- Highlighting can drift between the two code surfaces if token rendering and whitespace handling remain view-specific.
- Future additions like "copy line", "link to line", search, or code comments on Source would otherwise duplicate behavior.

Recommended fix:

- Extract a `CodeLineRenderer` contract:
  - input: path, raw line text, line number metadata, kind (`source`, `diff-add`, `diff-delete`, `diff-context`);
  - output: rendered line with stable token spans and preserved whitespace.
- Keep diff-specific coloring and comment anchors around that shared renderer.
- Add tests that Source and diff rendering preserve the same whitespace and token count for a representative TSX/JSON/CSS sample.

### Recommended Frontend Refactor Order

1. Add SignalR client and targeted state patching while keeping polling fallback.
2. Split command hooks from root `App`.
3. Move `SourceView` into `features/source`.
4. Move `PullRequestDiffReview` into `features/pullRequests`.
5. Move `AiPlanPanel` and markdown review into `features/ai`.

This order improves user feedback first, then makes the codebase cheaper to continue changing.

## Fourth Pass: Authorization And Provider Boundaries

### P1: Repository-Only Pipeline Runs Use View Permission For Mutation

Evidence:

- `CanRecordPipelineRun` and `CanMutatePipelineRun` resolve a target board when `BoardId` or `WorkItemId` is present.
- If no board/work item target is present, they call `CanMutateRepositoryOnlyPipelineWithoutLock`.
- `CanMutateRepositoryOnlyPipelineWithoutLock` currently allows the action when the repository is in `VisibleRepositoryIdsWithoutLock(actorSubject)`.

Impact:

- A user with read/view access to a board-linked repository can potentially record and execute a repository-only pipeline run.
- The pipeline runner is currently simple (`git clone --depth 1 ... log --oneline -5`), but this is still a mutation/action path and should not be governed by view permission.
- Future pipeline runner expansion would inherit the weaker permission boundary.

Recommended fix:

- Split repository visibility from repository action permission:
  - `CanViewRepository(repositoryId, actor)`,
  - `CanActOnRepository(repositoryId, actor)`.
- For board-linked repositories, `CanActOnRepository` should require mutating access to at least one board that links that repository.
- Reject repository-only pipeline creation/execution for viewer-only users.
- Add tests:
  - viewer can read Source for a repo;
  - viewer cannot record or execute repository-only pipeline runs;
  - member/admin/owner can execute when a board grants mutating access.

### P1: GitHub Source And Sync Token Resolution Uses Actor Default Installation

Evidence:

- Source tree/file endpoints call `ResolveGitHubRepositoryReadTokenAsync(store, github, actorSubject, ...)`.
- That helper chooses `store.GetDefaultGitHubInstallationId(actorSubject)` and creates a token for that installation.
- Provider sync uses the same helper when the source provider is GitHub.

Impact:

- A board can link a GitHub repository owned by one installation, while the current actor's default installation is another account.
- Source browsing or provider sync can then fail with confusing `404`/unavailable responses even though the board repository is valid.
- If multiple personal GitHub installations are present, default selection becomes a hidden dependency.

Recommended fix:

- Resolve GitHub read tokens by repository owner/installation, not actor default:
  - find an installed integration whose `AccountLogin` matches `repository.Owner`;
  - verify the actor can use that integration or can view/mutate the board containing the repository;
  - then mint that installation token.
- Add explicit error messages:
  - "No GitHub installation is linked for owner X",
  - "This user cannot use installation X",
  - "GitHub installation token could not be created".
- Use the same repository-specific token resolution for Source, profile scanning and provider sync.

### P2: Clone Info Should Separate Human Clone From Runner Clone

Evidence:

- `GET /api/repositories/{repositoryId}/clone-info` returns the stored repository remote and a `git clone ...` command.
- For LocalGit without `webUrl`, the API marks it as internal-only and explains that clone commands work from RDO runners or inside the cluster network.

Impact:

- This is technically accurate, but a user-facing "Clone" drawer that returns a cluster-internal URL can feel broken from a normal workstation.
- Once external LocalGit access is added, the same endpoint will need to distinguish unauthenticated public clone, per-user HTTPS clone, SSH clone and runner-only clone.

Recommended fix:

- Return a structured clone info DTO:
  - `humanCloneUrl`,
  - `runnerCloneUrl`,
  - `webUrl`,
  - `isInternalOnly`,
  - `recommendedMode`,
  - `explanation`.
- In v1, LocalGit should show "Clone is available only inside RDO runners" unless an external URL is configured.
- Avoid showing a command as primary if it cannot work from the user's machine.

## Fifth Pass: ASP.NET Core Structure And Operations

### P1: Minimal API Surface Needs Feature Route Groups

Evidence:

- The API targets `net10.0` and uses Minimal APIs.
- `Program.cs` registers services, middleware, all route handlers, DTOs, clients, renderers, background services and store logic in one file.
- The `/api` route group exists, but feature-level route groups do not.

Impact:

- Authorization, validation, ProblemDetails wording and dependencies are repeated inside individual handlers.
- A single file now has to carry product routing, infrastructure setup and domain behavior.
- It is hard to introduce endpoint filters or policies consistently for Source, LocalGit PR review, board secrets, teams or AI planning.

Recommended fix:

- Keep Minimal APIs, but split endpoint mapping by feature:
  - `MapWorkspaceEndpoints`,
  - `MapBoardEndpoints`,
  - `MapSourceEndpoints`,
  - `MapPullRequestReviewEndpoints`,
  - `MapPreviewEndpoints`,
  - `MapAiPlanEndpoints`,
  - `MapRuntimeSecretEndpoints`.
- Each feature group should own:
  - its request/response DTOs,
  - validation helpers or endpoint filters,
  - typed authorization checks,
  - service dependencies.
- Leave only host setup, middleware and top-level feature registration in `Program.cs`.

### P1: Background Work Is In-Process And Assumes One API Replica

Evidence:

- The API registers four hosted services: preview recovery, preview health monitor, implementation run monitor and public app deployment reconciler.
- Homelab currently deploys the API with `replicas: 1` and `strategy: Recreate`.

Impact:

- The current deployment intentionally avoids duplicate reconcilers by running one API pod.
- If the API is ever scaled above one replica or the deployment strategy changes, multiple pods can monitor/apply/cleanup the same runtime objects.
- Long-running business workflows depend on the web host being alive and healthy.

Recommended fix:

- Make the single-replica assumption explicit in code and status:
  - `/api/status` should report `RuntimeMode: single-api-reconciler`.
  - Homelab static tests should assert `replicas: 1` while in-process reconcilers exist.
- Longer term, move durable orchestration to one of:
  - Kubernetes Jobs/controllers with idempotent ownership labels,
  - a database-backed queue with leases,
  - a separate worker Deployment with leader election.
- Add idempotency keys to deployment/reconcile operations so duplicate workers become safe before scaling.

### P2: Configuration Should Move To Typed Options With Validation

Evidence:

- Runtime code reads many string keys directly from `IConfiguration`, for example `LocalGit:*`, `Ai:Codex:*`, `RepositoryRuns:*`, `Preview:*`, `Pipelines:*`, `GitHub:*`.
- Several incidents came from local/deployed configuration drift or missing values.

Impact:

- Missing or inconsistent configuration often fails late, inside a long-running flow.
- Local startup scripts and Homelab manifests can drift from app expectations without a single validation point.

Recommended fix:

- Introduce typed options:
  - `LocalGitOptions`,
  - `GitHubOptions`,
  - `PreviewOptions`,
  - `CodexOptions`,
  - `RuntimeSecretOptions`,
  - `RepositoryRunOptions`.
- Register them with `BindConfiguration(...).Validate(...)`.
- Surface the effective options in sanitized `/api/status` diagnostics.
- Add tests that local script defaults and Homelab ConfigMap satisfy the same options validation.

### P2: Request Validation Is Mostly Manual And Scattered

Evidence:

- The solution targets .NET 10, where Minimal APIs can use built-in validation support.
- Current handlers perform many inline checks and return `Results.Problem(...)` with locally chosen messages/status codes.

Impact:

- Validation behavior is inconsistent across endpoints.
- Adding new fields such as hierarchy, Source sync, review comments and AI plan comments requires more ad hoc checks.

Recommended fix:

- Start with high-value request DTOs:
  - create/update work item,
  - create board,
  - sync repository to provider,
  - create review comment,
  - create board secret.
- Add declarative validation attributes or endpoint filters.
- Keep domain validation in services where it depends on state, but use request validation for shape, required fields and length limits.
- Standardize errors with ProblemDetails types/codes so the frontend can render better field-specific messages.

## Sixth Pass: Build, Images And Supply Chain

### P1: API Runtime Image Mixes Web API And Runner Toolchain

Evidence:

- `src/Rosenvall.DevOps.Api/Dockerfile` builds the API and then installs `curl`, `git`, `nodejs`, `npm`, `kubectl` and global `@openai/codex` into the runtime API image.
- The same image is also used as the Kubernetes Codex runner image in deployed configuration.

Impact:

- The public API pod has a much larger runtime attack surface than it needs for serving HTTP requests.
- Updating Codex or runner dependencies requires rolling the API image.
- API restarts and runner behavior are coupled, which has already shown up in preview-source recovery and image-drift incidents.

Recommended fix:

- Split images:
  - `rosenvall-devops-api`: ASP.NET runtime only, no Node/npm/Codex/kubectl unless strictly needed for in-process local fallback.
  - `rosenvall-devops-runner`: Codex, git, kubectl and runner tools.
  - `rosenvall-devops-preview-base`: Vite preview runtime.
- Keep runner image digest in `Ai:Codex:KubernetesRunnerImage`.
- Add a test/static check that the API Dockerfile does not install Codex once runner image split is complete.

### P1: Codex And Preview Dependencies Are Not Fully Reproducible

Evidence:

- API Dockerfile runs `npm install -g @openai/codex` without a version pin.
- `preview-base/Dockerfile` runs `npm install` from `preview-base/package.json`, but there is no lockfile in `preview-base`.
- The frontend Dockerfile correctly uses `npm ci` with `package-lock.json`.

Impact:

- Rebuilding the same commit can produce a different runner or preview dependency set.
- A transient npm release can change preview generation behavior or break the runner image.
- Debugging "worked yesterday" build/runtime differences becomes harder.

Recommended fix:

- Pin Codex explicitly, for example `@openai/codex@<known-version>`, or install from a lockfile in a dedicated runner package directory.
- Add `preview-base/package-lock.json` and switch preview-base Dockerfile to `npm ci`.
- Add Renovate/Dependabot rules so dependency updates are explicit PRs, not implicit image rebuild changes.

### P2: External Binaries Should Be Checksum-Verified Or Image-Pinned

Evidence:

- The API Dockerfile downloads `kubectl` with `curl` from a versioned URL and makes it executable.
- Container base images use mutable tags such as `mcr.microsoft.com/dotnet/aspnet:10.0`, `mcr.microsoft.com/dotnet/sdk:10.0`, `node:24-alpine` and `nginx:1.29-alpine`.

Impact:

- Version tags are better than `latest`, but rebuilds can still pick up changed base image digests.
- The direct `kubectl` download is not checksum-verified.

Recommended fix:

- Pin production images by digest in Dockerfiles or in the build pipeline.
- Verify `kubectl` checksums or use a pinned package/image that already contains kubectl.
- Emit image SBOMs and provenance attestations in CI once the image build workflow is formalized.

### P2: CI And Image Publishing Are Separate Contracts

Evidence:

- `.github/workflows/ci.yml` runs backend tests and `npm run build`.
- The same CI workflow does not currently run the frontend unit test command that was used locally during this review: `npm test -- --runInBand`.
- `.github/workflows/publish-images.yml` publishes API, frontend and preview-base images on every push to `main`.
- The publish workflow is not gated by the CI workflow result; it repeats image builds and pushes mutable `:main` tags.

Impact:

- A commit can publish images even if the CI workflow for the same commit fails or if frontend unit tests would have failed.
- Operators then have to infer which image digest corresponds to a tested commit.
- This contributes to the recurring "merged, but not actually live / wrong digest" incidents.

Recommended fix:

- Add `npm test -- --runInBand` to CI.
- Gate image publishing on CI success, either by combining workflows with `needs` or by using `workflow_run`.
- Publish immutable SHA/digest metadata as an artifact for Homelab promotion.
- Keep `:main` as a convenience tag only, not as the deployment contract.

## Seventh Pass: Domain Boundaries And Core Project

### P1: `Rosenvall.DevOps.Core` Is Underused While Domain Policy Lives In The API

Evidence:

- `src/Rosenvall.DevOps.Core` contains one source file, `DevOpsDomain.cs`.
- That file owns early domain pieces such as `WorkItemType`, `AiRun`, `PreviewResourceSet`, `PreviewManifestRenderer`, `PreviewHostnames` and `PreviewSourceFile`.
- Newer domain-heavy logic lives in `src/Rosenvall.DevOps.Api/Program.cs` instead:
  - work item hierarchy normalization and validation,
  - bug-type migration,
  - implementation workflow classification,
  - preview source policy and generated-source validation,
  - repository profile classification,
  - LocalGit PR approval state,
  - epic run and goal state.

Impact:

- The API project has become the domain model, orchestration layer, transport layer and infrastructure layer at the same time.
- Pure rules are harder to test in isolation; tests are pulled toward `DevOpsStoreTests`, which is already large.
- Future API refactors are risky because a route-handler move can accidentally drag business rules with it.

Recommended fix:

- Make `Core` the home for pure policy and state transitions only. It should not reference ASP.NET, Kubernetes clients, `IConfiguration`, HTTP clients or shell rendering.
- Move in small slices:
  1. `WorkItemHierarchyPolicy`: allowed parent/child combinations, cycle checks, bug-type normalization.
  2. `ImplementationWorkflowPolicy`: `direct-pr`, `preview-then-pr`, `preview-only` classification from profile, repo and board hint.
  3. `PreviewSourcePolicy` and `PreviewSourceResultValidator`.
  4. `RepositoryProfileClassifier`.
  5. PR approval state derivation for LocalGit.
- Keep DTO mapping in API initially; move only deterministic rules first.

### P1: Kubernetes Manifest Rendering Is Split Between Core And API Without A Clear Rule

Evidence:

- `PreviewManifestRenderer` is in `Core` and renders preview `Namespace`, `ConfigMap`, `Deployment`, `Service`, `HTTPRoute` and `NetworkPolicy`.
- Many other Kubernetes renderers are in `Program.cs`, including implementation jobs, cleanup jobs, preview-source jobs, provider-sync jobs and board secret manifests.
- Several recent production issues were renderer-specific: malformed preview-promotion YAML, missing NetworkPolicy RBAC, retry-cleanup Secret deletion RBAC, provider-sync cleanup gaps and Secret apply concerns.

Impact:

- There is no obvious boundary for where a new renderer belongs.
- Text-rendered YAML defects are easy to introduce and often appear only when Kubernetes parses the manifest.
- RBAC and cleanup requirements drift because runtime resources are defined in several places without a common ownership inventory.

Recommended fix:

- Create a dedicated `Rosenvall.DevOps.Kubernetes` or `Infrastructure.Kubernetes` layer for manifest builders and runtime resource inventory.
- Prefer typed manifest objects plus serialization where practical; where string templates remain, add parse/dry-render tests for every manifest.
- Every renderer should expose:
  - resource inventory for cleanup,
  - required RBAC verbs/resources,
  - labels/selectors used for ownership,
  - whether it handles Secrets and therefore must avoid `kubectl apply`.
- Keep `Core` free from Kubernetes YAML unless the product intentionally treats preview manifests as domain output. My recommendation is to move `PreviewManifestRenderer` out of `Core` too and leave only `PreviewResourceSet`/naming policy there.

### P2: Epic/Goal Features Need A State Machine Before More Runner Automation

Evidence:

- Store tests already cover hierarchy and epic goal basics, for example `Epic_run_and_goal_track_child_feature_agents`.
- The current store records `EpicRunDto` and `EpicGoalRunDto`, but the orchestration remains inside `DevOpsStore`/API state rather than a dedicated state machine.
- The product direction is larger multi-card work where an epic coordinates feature/task runs.

Impact:

- Without a small explicit state model, epic goals risk becoming another set of one-off status strings and recovery paths.
- Parent cards will be hard to reason about when child preview, PR, cleanup and failure states all need aggregation.

Recommended fix:

- Before adding more goal automation, define a pure `EpicGoalStateMachine` in `Core`.
- Inputs should be child summaries, run events and user commands (`start`, `cancel`, `retry failed children`).
- Outputs should be next status, child actions to enqueue and visible progress copy.
- Add table-driven tests for all terminal and retry cases, then let hosted services/orchestrators call the state machine.

## Eighth Pass: Persistence Model And Recovery Semantics

### P1: Snapshot Persistence Still Has A Single-Document Ceiling

Evidence:

- `DevOpsStateDbContext` has a single `Documents` DbSet and `DevOpsStateDocument` stores `Id`, `Json` and `UpdatedAt`.
- `DevOpsStore.Persist()` constructs a full `DevOpsSnapshot` from all in-memory collections, serializes it and writes the whole JSON document.
- A hash skip now prevents unchanged writes, and `/api/status` exposes snapshot bytes and persist counts. That is a useful mitigation, not a long-term storage model.

Impact:

- Every real mutation still pays the cost of serializing all boards, work items, comments, logs, previews, runs, integrations and settings.
- High-churn data such as runner logs and health checks competes with low-churn data such as board config in the same document.
- Partial recovery is hard: a corrupt or incompatible snapshot can affect the entire product, not just one feature area.
- PostgreSQL support is configured, but the application is not using relational shape yet.

Recommended fix:

- Keep snapshot storage temporarily, but introduce typed tables by churn level:
  - low churn: workspaces, boards, repositories, board settings,
  - medium churn: work items, comments, AI plans,
  - high churn: run logs, preview step logs, timeline/events,
  - secret metadata only: board secrets and user authorization metadata.
- Start with append-heavy logs/events because they are the biggest memory and write-amplification risk.
- Add a compatibility loader that can read the old snapshot and write normalized rows once.
- After that, make snapshot export an admin/backup feature rather than the primary persistence mechanism.

### P1: There Is No Explicit Concurrency Contract For State Mutation

Evidence:

- `DevOpsStore` is a singleton with an in-process lock and many `Persist()` calls.
- The database row has no visible concurrency token or compare-and-swap update in `Persist()`.
- Homelab currently keeps the API at one replica with `Recreate`, which makes the in-process lock viable only because deployment topology enforces it.

Impact:

- Scaling the API to more than one replica would risk lost updates unless all mutations are serialized elsewhere.
- Hosted services and user-triggered endpoints share the same process lock today; externalizing workers later will need a real persistence concurrency model.
- This limits future HA and makes restarts more disruptive than they need to be.

Recommended fix:

- Decide and document the contract:
  - either intentionally single-writer API with leader election, or
  - optimistic concurrency in the database.
- If staying single-writer short term, add a startup/status warning when replicas are configured above one.
- If moving to multi-writer, add row-version/concurrency tokens to normalized tables and make command handlers retry safe.

### P2: Snapshot Upgrade Code Is Becoming Hidden Migration Logic

Evidence:

- Snapshot load performs many behavioral upgrades/backfills:
  - bug type migration,
  - hierarchy normalization,
  - AI result comment dedupe,
  - preview step-log backfill,
  - board hosting/workflow backfill,
  - public app backfill,
  - demo seed cleanup.
- The code is executed as part of `TryLoad()` and may call `Persist()` when it changes state.

Impact:

- Load-time behavior is doing both data migration and business reconciliation.
- It is hard to know which upgrades are one-time migrations and which are ongoing invariants.
- Future failures during load may be harder to diagnose because a read path can mutate persisted state.

Recommended fix:

- Introduce versioned migrations with names and audit logging.
- Separate:
  - pure compatibility mapping,
  - one-time data migrations,
  - ongoing reconcilers.
- Record the last applied migration version in storage.
- Add tests that open old fixtures and assert exactly which migrations run.

## Ninth Pass: Frontend Module Boundaries

### P1: `App.tsx` Is Still The Frontend Monolith

Evidence:

- `frontend/src/App.tsx` is about 6,505 lines.
- It contains:
  - top-level shell loading,
  - all board actions,
  - Add Board,
  - Source page,
  - work item modal tabs,
  - AI plan review,
  - preview UI,
  - LocalGit PR diff/review,
  - settings, teams, markdown rendering and utility functions.
- Some pure helpers have already moved to `boardChrome.ts`, `codeHighlight.ts`, `apiClient.ts`, `implementationRetry.ts` and `planQuestions.ts`, which is the right direction.

Impact:

- UI changes frequently touch the same file, increasing merge conflicts and regression risk.
- Component-local state and API calls are hard to reason about because everything shares one lexical scope.
- Tests drift toward pure helper tests because rendering behavior is expensive to isolate from the full app.

Recommended fix:

- Split by feature route/view, not by generic component type:
  - `features/board/BoardPage.tsx`
  - `features/source/SourceView.tsx`
  - `features/workItem/WorkItemModal.tsx`
  - `features/workItem/tabs/OverviewTab.tsx`
  - `features/workItem/tabs/AiTab.tsx`
  - `features/workItem/tabs/PreviewTab.tsx`
  - `features/workItem/tabs/PullRequestTab.tsx`
  - `features/workItem/tabs/LogsTab.tsx`
  - `features/settings/*`
- Keep `App.tsx` as composition only: auth, shell load, current view and provider objects.
- Move shared primitives (`ModalFrame`, `TerminalLog`, `MarkdownText`, `LineNumberedCode`) into `components/`.
- Move all API calls into `apiClient.ts` before splitting components, so components receive typed action functions rather than raw `fetch` logic.

### P1: `BoardActions` Is Doing Too Much

Evidence:

- `BoardActions` is passed through many views and modals.
- It includes unrelated commands: board save/delete, work item mutation, comments, AI planning, preview, PR approval, settings, secrets, GitOps, teams and repository sync.
- Most action methods call broad refresh logic after mutation.

Impact:

- Every child component receives more authority than it needs.
- It is hard to add action-specific pending/error state because the command surface is centralized but not grouped.
- Provider-specific operations leak into generic UI surfaces.

Recommended fix:

- Split into feature-scoped action objects:
  - `workItemActions`,
  - `aiPlanActions`,
  - `previewActions`,
  - `pullRequestActions`,
  - `sourceActions`,
  - `settingsActions`.
- Each action should return the updated entity when possible and update local state narrowly.
- Reserve full shell refresh for topology changes such as board creation/deletion, provider sync completion or auth changes.

### P2: Markdown, Plan Review And PR Diff Should Share Review Primitives

Evidence:

- LocalGit PR review has line-level comment anchoring and continuous diff scroll.
- AI plan review has markdown block comments using `CommentBody`/`MarkdownText`.
- Both need markers, inline composers, resolved/unresolved state and "block action while unresolved comments exist".

Impact:

- Two review UIs can drift in behavior and styling.
- The recent back-and-forth around plan markdown comments shows that the review surface is product-critical and should be a reusable primitive, not ad hoc per view.

Recommended fix:

- Extract a small `ReviewSurface` primitive:
  - anchors,
  - markers,
  - composer placement,
  - thread rendering,
  - resolved state.
- Implement adapters:
  - `MarkdownReviewSurface` for AI plans,
  - `DiffReviewSurface` for LocalGit PRs.
- Keep anchoring models different internally, but make the interaction contract consistent.

## Tenth Pass: Local Development And CI Feedback Loops

### P1: Local Stop Script Leaves Forgejo Port-Forward Processes Behind

Evidence:

- `scripts/start-local-demo.ps1` may start a `kubectl port-forward svc/rosenvall-devops-forgejo ...` process.
- The start script proactively kills old Forgejo port-forward processes before starting.
- `scripts/stop-local-demo.ps1` only stops `dotnet.exe` API and `node.exe` Vite processes. It does not stop `kubectl.exe` port-forward processes.

Impact:

- A stale Forgejo port-forward can keep a local port busy or point at an older/failed session.
- The next local run may behave differently depending on whether the user used `stop-local-demo.ps1` or restarted through `start-local-demo.ps1`.
- This contributes to "localhost says 500/404 but deployed state differs" style confusion.

Recommended fix:

- Make stop script symmetric with start script:
  - stop API port-forward,
  - stop Forgejo port-forward,
  - stop Vite,
  - stop local API.
- Store PIDs in `.codex/devops-logs/local-demo.pids.json` and stop by PID first, with command-line fallback.
- Print remaining matching processes after stop if any remain.

### P1: CI Does Not Run Frontend Tests

Evidence:

- `.github/workflows/ci.yml` runs backend restore/test.
- The frontend job runs `npm ci` and `npm run build`.
- The frontend test command exists and was run manually in this review, but CI does not run `npm test`.

Impact:

- Helper regressions in `boardChrome.ts`, `apiClient.ts`, `codeHighlight.ts`, plan questions and retry logic can merge if the build still passes.
- The current test suite is fast and does not require a browser, so skipping it saves little but removes useful coverage.

Recommended fix:

- Add `npm test` before `npm run build` in the frontend CI job.
- Keep `npm run build` after tests so TypeScript and Vite production bundling remain covered.
- Consider adding a small Playwright smoke job later, but do not block on that before adding the existing test command.

### P2: Image Publishing And Homelab Digest Rollout Are Separate Manual Steps

Evidence:

- `.github/workflows/publish-images.yml` publishes `:main` and `:${{ github.sha }}` tags for API, frontend and preview base.
- Homelab deployment pins image digests separately.
- Several recent incidents were caused by code merged/published but not live because Homelab still referenced older digests.

Impact:

- Users can see behavior from mixed versions: new frontend with old API, or old API with new runner expectations.
- Manual digest updates are easy to forget, especially for changes that only affect local smoke tests at first.

Recommended fix:

- Keep digest pinning, but automate a GitOps PR or commit that updates Homelab image digests after successful publish.
- Expose build SHA/digest in `/api/status` and frontend footer/user menu so drift is visible immediately.
- Add a deployment checklist step in release notes until the automation is in place.

## Eleventh Pass: Source View And Provider Sync Contracts

### P1: Source Reads And Provider Sync Need Repository-Scoped Credentials

Evidence:

- Source tree/file endpoints authorize by repository visibility, which is good.
- For GitHub source reads, the API calls `ResolveGitHubRepositoryReadTokenAsync(...)`.
- That helper uses `store.GetDefaultGitHubInstallationId(actorSubject)` and falls back to the configured token.
- Provider sync uses the same helper for GitHub source credentials.

Impact:

- A user can have visibility to a board repository while their default GitHub installation is not the installation that owns that repository.
- Reads and sync can fail for the wrong reason: "GitHub source access unavailable" even though the board is correctly linked.
- If a configured fallback token exists, source reads may use a broader credential than the repository link implies.

Recommended fix:

- Store or derive the GitHub installation id per GitHub repository link.
- Resolve read/sync tokens from the repository's owning installation first.
- Only use configured fallback tokens for explicit legacy/custom-url paths, not for GitHub App linked repos.
- Add tests with two GitHub installations where the actor default differs from the board repository installation.

### P2: Source Ref Validation Should Be Explicit

Evidence:

- Source APIs accept a `ref` query parameter and default it to `repository.DefaultBranch`.
- Paths are normalized to reject `..` and absolute paths.
- Refs are trimmed and passed to provider APIs.

Impact:

- Provider APIs escape the ref, so this is not a direct shell-injection issue.
- Still, an unbounded arbitrary ref string can produce confusing provider errors and makes it harder to distinguish branch names, tags and SHAs in the UI.

Recommended fix:

- Add a `GitRefNamePolicy` with length limits and clear allowed forms:
  - branch/tag names,
  - full commit SHA,
  - no control characters,
  - no `..`, no leading/trailing slash, no `.lock`.
- Return field-specific ProblemDetails for invalid refs.
- Add a future branch/tag selector backed by provider refs instead of free text only.

### P2: Clone UX Needs Separate Human And Runner URLs

Evidence:

- `clone-info` returns `repository.RemoteUrl` and a clone command.
- For LocalGit internal-only repositories, the response marks `internalOnly=true` and explains that clone commands work from RDO runners or inside the cluster network.

Impact:

- This is honest, but a user-facing "Clone" button that mostly returns a cluster-only URL can feel broken.
- The product now has three URL concepts:
  - provider API URL,
  - runner clone URL,
  - human clone/web URL.

Recommended fix:

- Extend repository metadata with `humanCloneUrl`, `runnerCloneUrl` and `webUrl`.
- For internal-only LocalGit, show "Clone unavailable outside the cluster" as the primary state and put the runner command behind a details disclosure.
- If external Forgejo exposure is added later, populate `humanCloneUrl` without changing runner behavior.

## Twelfth Pass: Frontend API Errors And Pending States

### P1: API Client Loses Structured Error Information

Evidence:

- `frontend/src/apiClient.ts` parses failed responses and throws `new Error(message)`.
- It chooses `payload.detail || payload.title || status text`.
- Status code, response body, ProblemDetails type, field errors and retry hints are not preserved.

Impact:

- UI code cannot reliably distinguish:
  - 401/403 authorization problems,
  - 404 stale local API route,
  - 409 conflict/pending-run state,
  - 422 validation/field errors,
  - 503 capacity or provider unavailable.
- This explains several user-facing states that looked generic: "API unavailable", "404 Not Found", or buttons not reflecting the true backend state.

Recommended fix:

- Introduce an `ApiError` class:
  - `status`,
  - `title`,
  - `detail`,
  - `type`,
  - `errors`,
  - `rawBody`,
  - `requestPath`.
- Update `apiUnavailableBannerMessage` and action handlers to branch on `ApiError.status`.
- Keep a concise `message` for toasts, but keep structured fields available to components.
- Add tests for JSON ProblemDetails, non-JSON errors, 204 responses, timeout and unauthorized refresh.

### P2: Action Pending State Is Improving But Still Global In Many Paths

Evidence:

- `App.tsx` has a global `busyAction` toast and many actions use `withBusy(...)`.
- Some newer flows have local pending state, for example source sync and Local PR approval.
- The same modal often disables all actions when `busyAction !== null`.

Impact:

- A long-running unrelated action can make a focused modal feel frozen.
- Quick actions can complete before the user notices the global toast.
- Duplicate-click prevention varies by feature.

Recommended fix:

- Replace global string state with a small action registry:
  - key: `workItemId/actionName` or `boardId/actionName`,
  - label,
  - startedAt,
  - optional job id.
- Components should disable only their own action keys.
- Show delayed inline progress after 2-3 seconds for Kubernetes-backed jobs.
- Keep the global toast as a secondary summary, not the only feedback.

## Thirteenth Pass: Observability And Incident Debugging

### P1: Runtime Work Needs Correlation Scopes

Evidence:

- Orchestrator and hosted-service classes use `ILogger`, for example preview implementation, preview health, pipeline jobs, runtime secrets, implementation monitoring and preview-source jobs.
- Log messages include some ids, but there is no consistent `BeginScope` or correlation model across:
  - board id,
  - work item id/key,
  - AI run id,
  - implementation run id,
  - Kubernetes job name,
  - pod name,
  - provider.

Impact:

- During incidents, the UI, Kubernetes events and API logs must be manually stitched together.
- Repeated retry/recovery behavior is hard to distinguish from duplicate user actions.
- As LocalGit, provider sync and epic runs grow, correlation becomes essential.

Recommended fix:

- Add a small `RunLogScope` helper and use it in every orchestrator/monitor.
- Standard fields:
  - `boardId`,
  - `workItemId`,
  - `workItemKey`,
  - `aiRunId`,
  - `runId`,
  - `runKind`,
  - `provider`,
  - `jobName`,
  - `podName`.
- Add the same ids to timeline events and terminal logs when available.
- Include `runId` in frontend log panels and support copying it.

### P2: Health And Metrics Are Product Metrics, Not Operational Metrics

Evidence:

- `/healthz` is mapped through ASP.NET health checks, but no custom storage/provider checks are visible.
- `/api/status` exposes useful resource diagnostics including memory and snapshot counters.
- `/api/metrics` returns product-level metrics: tokens, code added/deleted and pipeline runs.
- No OpenTelemetry, Prometheus metrics or tracing are visible.

Impact:

- Kubernetes can know the process is alive, but not whether DB persistence, Forgejo, GitHub App credentials or Kubernetes runtime operations are degraded.
- Product metrics cannot answer operational questions such as queue depth, job duration, failure rate or API memory trend.

Recommended fix:

- Add readiness checks for:
  - database read/write,
  - Kubernetes API access if runtime jobs are enabled,
  - Forgejo availability when LocalGit is enabled,
  - GitHub App config presence when GitHub flows are enabled.
- Keep `/healthz` lightweight for liveness and add `/readyz` for dependency readiness.
- Add OpenTelemetry metrics for:
  - preview-source job duration/result,
  - implementation run duration/result,
  - provider sync duration/result,
  - snapshot bytes/write count/skip count,
  - API memory current/limit.

## Fourteenth Pass: Documentation And Runbooks

### P1: Operator Documentation Has Drifted From The Product Flow

Evidence:

- `README.md` still says Forgejo/Gitea is `LinkExistingFirst` and that repository creation is only exposed as configuration before credentials are wired.
- `deploy/homelab/README.md` still lists Forgejo/Gitea as an optional external API at `https://git.rosenvall.se/api/v1`.
- The current code and local script now support internal-only Forgejo/LocalGit, demo sandbox flows, LocalGit board creation, local pull requests, preview promotion, public app deployment and provider sync.
- `docs/frontend-control-inventory.md` still describes older controls such as "No PR for local preview" and "Approve PR" as a GitHub-style development state, not the current LocalGit review/merge/deploy flow.
- `docs/implementation-plan.md` still has a todo to replace Forgejo configuration-only repository creation with authenticated API creation, even though LocalGit creation has since moved forward.

Impact:

- Operators cannot reliably answer "what is live?", "what should I click?", or "which digest is deployed?" from the docs.
- New incidents are harder to classify because the documented flow does not match the current preview-first and LocalGit-first demo path.
- Local development failures such as stale port-forwards, auth/demo bootstrap issues or Forgejo unavailability look like app bugs instead of documented startup states.

Recommended fix:

- Add a `docs/runbooks/` directory with short operational runbooks:
  1. `local-development.md`: auth on/off, Forgejo port-forward, demo user, common 404/500 causes, stop script behavior.
  2. `deploy-rosenvall-devops.md`: publish images, update Homelab digest pins, verify build SHA/digest, ArgoCD sync, rollback.
  3. `preview-first-web-flow.md`: plan, preview, approve preview to PR, merge PR, public app deploy, preview cleanup.
  4. `localgit-forgejo.md`: internal-only assumptions, service credential, repo creation, PR review, cleanup and provider sync.
  5. `github-personal-repo-creation.md`: personal user authorization, account matching, organization creation disabled in v1.
  6. `runtime-cleanup.md`: board cleanup scope, work-item cleanup scope, what Kubernetes resources and LocalGit repos are deleted.
- Update README to describe the current high-level product flow and link to runbooks instead of trying to be the full operator manual.
- Treat deploy/homelab README as the contract for the standalone manifests only, and move product workflow details into `docs/runbooks`.

### P2: Architectural Decisions Need ADRs Before More Automation Is Added

Evidence:

- Several important product choices are now embedded in code and incident history:
  - Kubernetes Jobs are the sandbox boundary for Codex runner work.
  - Website boards are preview-first; GitOps boards are direct PR.
  - Preview approval promotes exact reviewed source and does not rebuild.
  - LocalGit/Forgejo is internal-only and RDO is the review UI.
  - Public app hosting is board-scoped and defaults to `<board-slug>.rosenvall.se`.
  - SQLite snapshot persistence is still the state store despite high-churn logs.

Impact:

- Future contributors can accidentally reintroduce old behavior, for example building during preview promotion or exposing Forgejo publicly.
- Test names and UI copy carry the design intent better than architecture docs, which makes drift likely as the app grows.

Recommended fix:

- Add ADRs under `docs/adr/`:
  1. `0001-preview-first-web-repositories.md`
  2. `0002-localgit-forgejo-internal-only.md`
  3. `0003-kubernetes-runner-sandbox-boundary.md`
  4. `0004-board-public-app-hosting.md`
  5. `0005-snapshot-store-limits-and-exit-plan.md`
- Keep ADRs short: context, decision, consequences, follow-up triggers.
- Link ADRs from README and from relevant tests so intent stays close to enforcement.

### P2: Release State Should Be Visible Without Reading GitOps Manifests

Evidence:

- Recent issues repeatedly came from "merged but not live" and local-vs-deployed drift.
- The current docs mention digest pinning as a production step, but there is no visible checklist or status contract that ties API/frontend build SHA to Homelab pinned digest.

Impact:

- Users cannot tell whether localhost, `devops.rosenvall.se`, API and frontend are on the same build.
- Operators must inspect manifests, ArgoCD and browser behavior manually.

Recommended fix:

- Expose API build metadata and frontend build metadata in:
  - `/api/status`,
  - the user menu or settings footer,
  - deploy verification output.
- Add a release checklist:
  1. CI passed.
  2. Images published with SHA.
  3. Homelab digest pins updated.
  4. ArgoCD synced.
  5. `/api/status` SHA matches expected.
  6. frontend footer SHA matches expected.
- Add a small `scripts/check-deployed-version.ps1` that compares local git SHA, GHCR digest, Homelab manifest digest and live `/api/status`.

## Fifteenth Pass: Multi-User Authorization Boundaries

### P1: Personal GitHub Integrations Are Visible/Usable Too Broadly

Evidence:

- `CanUseGitHubIntegrationWithoutLock` returns true for any non-demo actor when `integration.AccountType == "User"`.
- Repository creation itself has a stricter check through `CanCreateGitHubRepositoryWithoutLock`: a personal account requires a connected user authorization whose GitHub login matches the integration account login.
- The broader `CanUseGitHubIntegrationWithoutLock` is still used as the visibility/use gate for integrations.

Impact:

- A signed-in user can see another user's personal GitHub installation in integration lists even if they cannot create repositories under it.
- Future features that rely on "can use integration" instead of "can create repository" can accidentally regain cross-user personal account delegation.
- The UI can show confusing authorization prompts for accounts the current user should not be offered at all.

Recommended fix:

- Split integration permission into explicit methods:
  - `CanViewGitHubIntegration`,
  - `CanCreateRepositoryWithGitHubIntegration`,
  - `CanManageGitHubIntegrationPolicy`,
  - `CanReadRepositoriesFromGitHubIntegration`.
- For personal installations, `CanViewGitHubIntegration` should require:
  - installed by actor, or
  - actor has a matching connected GitHub user authorization, or
  - an explicit team/org policy for future organization support.
- Add tests:
  - user A cannot see user B's personal integration,
  - user A with mismatched GitHub authorization cannot see/use user B's installation,
  - demo sees no GitHub integrations,
  - organization installations remain visible only through explicit policy.

### P1: SignalR Broadcasts Ignore Board Authorization

Evidence:

- Many endpoints and background monitors publish events with `hub.Clients.All.SendAsync(...)`.
- The hub is authenticated when auth is enabled, but event delivery is not scoped to board/workspace/team access.
- Events include work item changes, comments, AI run changes, preview changes, implementation run changes, repository cleanup run changes and board deletion notifications.

Impact:

- Any authenticated user can connect directly to the hub and receive updates for boards they cannot view through REST endpoints.
- Even if the current frontend ignores most hub events, the hub itself is a data channel and should obey the same authorization model.
- This becomes more serious as comments and source/review data are used as AI context.

Recommended fix:

- Introduce an `IRealtimeNotifier` abstraction that accepts a board/work-item/run id and resolves authorized recipients.
- Use SignalR groups per board/workspace, populated only after an explicit authorized subscription.
- Replace `Clients.All` with scoped sends:
  - `Clients.Group(BoardGroup(boardId))`,
  - `Clients.User(subject)` for user-specific events,
  - admin/system groups only where needed.
- Add tests for event target selection, or at minimum unit tests for board id resolution from each event type.

### P1: Demo Restrictions Should Be Modeled As Policy, Not Email Specials

Evidence:

- The restricted demo path is keyed to `demo@rosenvall.local`.
- `GetOrCreateUserWithDemoSandbox` ensures a `Demo Sandbox` workspace, `Demo` team and sandbox board.
- Board creation is restricted for demo users to no repo or LocalGit requests.
- Other endpoints, such as team creation, rely on general authorization rather than a central "demo sandbox policy".

Impact:

- Demo behavior is spread across user bootstrap, board creation, GitHub integration filtering and repository request validation.
- It is easy to add a new provider/source/action endpoint without applying the demo restrictions.
- A future "demo tenant" with multiple users or reset behavior will be hard to implement safely.

Recommended fix:

- Add a `UserAccessProfile` or `ActorPolicy` abstraction derived from the authenticated user:
  - `IsDemoRestricted`,
  - allowed workspace ids,
  - allowed providers,
  - can create teams,
  - can link external repositories,
  - can run provider sync.
- Make endpoints ask the policy rather than checking demo email directly.
- Add a small test matrix for demo:
  - can create LocalGit board in Demo Sandbox,
  - cannot create GitHub board,
  - cannot see GitHub integrations,
  - cannot create arbitrary teams if that is not intended,
  - cannot provider-sync to GitHub.

### P2: The Hard-Coded Demo Password Needs Explicit Production Guardrails

Evidence:

- The Homelab Authentik blueprint creates `demo` / `demo@rosenvall.local` with `password: demo`.
- This matches the requested demo-login behavior and avoids storing the demo password in Bitwarden.
- RDO relies on app-side sandboxing to keep this weak account contained.

Impact:

- This is acceptable only if the demo account is intentionally public, rate-limited and fully sandboxed.
- A future endpoint that forgets demo policy checks turns a known weak credential into a real production account.
- Operators may later forget why a weak password is committed in GitOps.

Recommended fix:

- Add explicit documentation in Homelab and RDO:
  - `demo/demo` is intentionally weak,
  - it must only have access to `Demo Sandbox`,
  - it must not see GitHub installations, real boards or provider-sync-to-GitHub.
- Add a startup/status check that reports demo sandbox policy enabled when the demo account is present.
- Add a periodic or testable assertion that demo has no access to non-demo boards.
- If demo ever needs broader access, move away from a fixed password and use a resettable demo session model instead.

### P1: Expensive Actions Need Per-Actor Quotas And Idempotency Keys

Evidence:

- The API does not configure ASP.NET Core rate limiting (`AddRateLimiter` / `UseRateLimiter`) or a visible per-actor quota layer.
- AI planning endpoints call `planner.GeneratePlanAsync(...)` directly in the HTTP request path.
- Preview source generation, implementation, preview-promotion, provider-sync and cleanup actions all create Kubernetes work or provider-side repositories/PRs.
- Some actions have local duplicate guards, for example pending implementation per work item, but there is no central action ledger keyed by actor, board, work item and operation.
- The `demo/demo` account is intentionally easy to access, so it needs stronger server-side quotas than normal trusted users.

Impact:

- A user can repeatedly click expensive actions and create unnecessary AI calls, Kubernetes Jobs, provider repositories or long-running syncs.
- Demo traffic can consume cluster resources or provider API budget even if board authorization is correct.
- Retried browser requests can create duplicate side effects unless every endpoint remembers whether the same operation already started.
- Operators get "it feels slow" symptoms without a clear answer on whether the system is queued, throttled or duplicated.

Recommended fix:

- Add a small action orchestration table/state model for expensive operations:
  - actor subject,
  - board/work item,
  - operation kind,
  - idempotency key,
  - status,
  - created/started/completed timestamps,
  - sanitized failure.
- Require client-generated or server-derived idempotency keys for:
  - AI plan/revise,
  - preview build/retry,
  - preview approval PR,
  - PR review fix,
  - provider sync,
  - board cleanup.
- Add per-actor and per-board quotas:
  - maximum active Kubernetes Jobs,
  - maximum AI plan requests per time window,
  - maximum demo boards/repos/previews,
  - maximum provider-sync runs.
- Return `429` or `409` with a concrete existing operation id when a duplicate or quota breach occurs.
- Surface this in the UI as queued/throttled/pending state, not as a generic disabled button.

### P2: Source And Sync Authorization Need Negative Endpoint Tests

Evidence:

- Source tree/file/clone-info endpoints correctly call `CanViewRepositoryRequest`.
- Provider sync correctly calls `CanMutateBoardRequest` and verifies the source repository is linked to that board.
- Most current tests focus on store behavior, manifest content and happy-path provider behavior.

Impact:

- Regressions in repository visibility are likely to be caught late, because source browsing and sync are attractive features for demo/multi-user use.
- If board repository links are expanded, a small mistake can expose source code from a repo linked to another board.

Recommended fix:

- Add endpoint-level integration tests using authenticated principals:
  - source tree/file returns 403 for a repo linked only to another user's board,
  - clone-info returns 403 for the same case,
  - provider sync rejects source repositories not linked to the board,
  - demo can browse only demo LocalGit repositories,
  - unauthenticated local-dev behavior remains explicit and covered separately.

## Sixteenth Pass: Kubernetes Manifest Contract Drift

### P1: `deploy/homelab` Is No Longer A Reliable Runtime Contract

Evidence:

- `deploy/homelab/rbac.yaml` grants preview-related resources such as namespaces, configmaps, services, deployments and HTTPRoutes, but does not include the recently required `networkpolicies` or `events` permissions.
- The historical incidents showed missing NetworkPolicy and events RBAC in the deployed environment, and those fixes were made in the Homelab GitOps repo rather than this `deploy/homelab` directory.
- `deploy/homelab/api.yaml` still uses `ghcr.io/carnufex/rosenvall-devops-api:main` with `imagePullPolicy: Always`, while the production Homelab flow now relies on pinned digests.
- The same file still configures `Repositories__Forgejo__ApiBaseUrl=https://git.rosenvall.se/api/v1`, while the current LocalGit path uses internal Forgejo service URLs.
- `deploy/homelab/kustomization.yaml` does not include Forgejo resources even though LocalGit is now part of the demo path.

Impact:

- A contributor applying `kubectl apply -k deploy/homelab` from this repo can get an environment that differs materially from `devops.rosenvall.se`.
- Tests that inspect this folder can pass while the real GitOps manifest has different RBAC, image pins or runtime service account names.
- The repo now has two implied deployment contracts: the stale in-repo one and the real Homelab GitOps one.

Recommended fix:

- Decide one of these models:
  1. Remove `deploy/homelab` from the product repo and replace it with a README that points to the authoritative Homelab GitOps path.
  2. Keep it as a local/dev-only manifest and rename it to `deploy/local-k8s`.
  3. Generate both this folder and the Homelab app from a shared kustomize/helm base.
- If kept, update the manifests to include:
  - internal Forgejo resources or an explicit dependency note,
  - NetworkPolicy and events RBAC,
  - pipeline namespace Secret delete RBAC,
  - configured runner image digest support,
  - memory requests/limits matching production.
- Add a CI test that renders the authoritative Homelab path if it is available locally, and otherwise clearly skips with a warning.

### P2: RBAC Tests Should Assert Capabilities, Not String Fragments

Evidence:

- Existing tests search static YAML text for fragments such as `resources: ["events"]` and `verbs: ["get", "list", "watch"]`.
- This style catches some drift, but it cannot prove the rule belongs to the expected service account, namespace, ClusterRole or RoleBinding.

Impact:

- A manifest can contain the right strings in the wrong object and still pass.
- RBAC regressions are likely around scope, not just verbs.

Recommended fix:

- Parse rendered YAML into objects in tests and assert:
  - subject service account,
  - namespace,
  - Role/ClusterRole name,
  - exact apiGroup/resource/verbs,
  - binding from subject to role.
- Add a small helper like `AssertRbacAllows(renderedYaml, subject, namespace, apiGroup, resource, verbs)`.
- Keep one golden smoke test with `kubectl auth can-i` in the deployment verification runbook.

## Seventeenth Pass: UI Accessibility And Interaction Contracts

### P1: Modals And Tabs Need A Shared Accessible Primitive

Evidence:

- `ModalFrame` renders a `role="dialog"` section with `aria-modal="true"` and a close button.
- The modal does not appear to trap focus, restore focus to the opener, or close on Escape.
- Work-item tabs are rendered as ordinary buttons inside a `nav` with `aria-label`, but without `role="tablist"`, `role="tab"`, `aria-selected`, `aria-controls` or arrow-key behavior.
- The same modal shell is now used for work items, PR diff review, provider sync and other flows.

Impact:

- Keyboard-only users can tab behind the modal or lose context after closing it.
- Screen readers get a generic navigation region rather than a tabbed interface.
- As more tabs and review surfaces are added, each feature has to solve focus behavior ad hoc.

Recommended fix:

- Add shared UI primitives:
  - `ModalDialog` with focus trap, Escape handling, focus restore and labelled title.
  - `Tabs` with roving focus, `aria-selected`, `aria-controls` and arrow-key navigation.
  - `Disclosure` for "Show more" plan and log sections.
- Add lightweight tests with React Testing Library for:
  - Escape closes modal,
  - tab focus stays inside modal,
  - arrow keys move tab focus,
  - active tab survives data refresh.

### P2: Review Surfaces Need One Interaction Model

Evidence:

- Local PR diff comments, AI plan markdown comments and future Source comments all need line/block anchoring, markers, composer state and unresolved counts.
- The current AI plan review uses clickable markdown blocks; the PR diff uses continuous line sections; Source is read-only but is likely to need line comments later.

Impact:

- Small UI differences make the app feel inconsistent: users learn one review behavior in PRs and a different one in AI plans.
- Comment anchoring bugs are likely if each surface independently maps text to ids.

Recommended fix:

- Create a shared `ReviewSurface` abstraction with:
  - anchor id,
  - rendered content,
  - marker placement,
  - composer placement,
  - thread rendering,
  - unresolved count.
- Implement adapters:
  - markdown block adapter for AI plans,
  - diff line adapter for PRs,
  - source line adapter later.
- Keep storage separate by domain, but make frontend review interaction shared.

### P2: Text Rendering And Product Copy Should Be Guarded

Evidence:

- The PR/plan/comment UI uses repeated handwritten status separators and delivery-copy fragments across the large `App.tsx`.
- Most UI strings are handwritten in the large `App.tsx`, and several flows mix Swedish and English by feature.

Impact:

- Mixed wording makes it harder to tell whether a message is product state, technical state or user action.
- Repeated handwritten copy makes small UI changes, such as "Approve PR" versus "Merge local PR and deploy app", easy to apply inconsistently.

Recommended fix:

- Move repeated user-facing delivery strings into small domain-specific helpers, for example `copy/prReview.ts`, `copy/preview.ts`, `copy/source.ts`.
- Add a simple render/helper test for the most important state labels:
  - Local PR ready,
  - local PR merged/deployed,
  - preview stopped,
  - app deploying/failed/running.
## Eighteenth Pass: Test Architecture

### P1: Backend Tests Are Over-Concentrated In One Store Test File

Evidence:

- `tests/Rosenvall.DevOps.Tests/DevOpsStoreTests.cs` is about 4,931 lines and roughly 321 KB.
- The backend test project references the API project directly, but does not include `Microsoft.AspNetCore.Mvc.Testing`, `WebApplicationFactory` or `TestServer`.
- Many important behaviors are tested by calling `DevOpsStore` directly or by checking manifest strings.

Impact:

- It is hard to understand the coverage map because store, orchestration, manifest, auth, provider and local-dev tests are mixed in one file.
- Endpoint behavior can drift from store behavior, especially for authorization, route validation, error shape and HTTP status codes.
- Large test files make targeted refactors harder because unrelated tests share helper state and fixtures.

Recommended fix:

- Split tests by feature:
  - `WorkItemHierarchyTests`,
  - `PreviewWorkflowTests`,
  - `LocalGitWorkflowTests`,
  - `GitHubIntegrationTests`,
  - `ProviderSyncTests`,
  - `BoardSecretTests`,
  - `ManifestRendererTests`,
  - `DevOpsStorePersistenceTests`.
- Add an API integration-test project or fixture using `WebApplicationFactory` once the Program file is split enough to support it.
- Start endpoint tests with the highest-risk paths:
  - authenticated `/api/workspaces` demo bootstrap,
  - source tree/file/clone-info authz,
  - provider sync authz,
  - GitHub personal repo creation mismatch,
  - board deletion cleanup failure behavior.

### P2: Manifest Tests Should Parse YAML Before Matching Strings

Evidence:

- Many manifest tests use `Assert.Contains` for YAML fragments.
- Some helper methods validate indentation or env block ordering, but the overall rendered YAML is still mostly treated as text.
- Recent production bugs included malformed YAML indentation and RBAC scope mistakes.

Impact:

- String tests are useful for quick regression checks, but they do not guarantee well-formed Kubernetes objects.
- They also make refactors noisy because harmless ordering/formatting changes can break tests.

Recommended fix:

- Add a lightweight YAML parser test helper.
- For each renderer, assert:
  - documents parse successfully,
  - kind/apiVersion/name/namespace labels are correct,
  - Secret manifests never contain `kubectl.kubernetes.io/last-applied-configuration`,
  - Job pods have expected `automountServiceAccountToken`, security context, volumes and service account.
- Keep a small number of string assertions for shell snippets where structure parsing is not enough.

### P2: Frontend Tests Are Valuable But Too Source-String Heavy

Evidence:

- Frontend tests include helpful pure helper coverage for board chrome, diff parsing, plan comments, source navigation, highlighting and API client behavior.
- Some tests read `App.tsx` source and assert implementation strings such as `CommentBody` usage or deleted component names.

Impact:

- Source-string assertions are brittle and can block harmless refactors.
- They cannot prove real keyboard, modal, tab or review interaction behavior.

Recommended fix:

- Keep pure helper tests for parsing and state derivation.
- Add React Testing Library for the most important UI behaviors:
  - work-item tab navigation,
  - PR diff line comment create/resolve flow,
  - AI plan markdown comment flow,
  - Source file navigation and highlighted code fallback,
  - modal focus and Escape behavior.
- Add one Playwright local smoke for demo LocalGit flow when the local environment is available.

## Nineteenth Pass: Secret Redaction Coverage

### P0: Codex Runs With `ROSENVALL_GIT_TOKEN` Still In Its Environment

Evidence:

- Implementation job rendering injects `ROSENVALL_GIT_TOKEN` from the per-run Secret.
- Before `codex exec`, the shell stores `repository_token_for_runner="$ROSENVALL_GIT_TOKEN"` and runs `unset GITHUB_TOKEN`, but it does not unset `ROSENVALL_GIT_TOKEN`.
- PR review-fix job rendering has the same pattern: `unset GITHUB_TOKEN`, then `codex exec`, while `ROSENVALL_GIT_TOKEN` remains available.
- The runner later prints `cat "$codex_log"` into RDO logs.

Impact:

- Codex is intentionally influenced by repo files, work-item comments, AI plan comments and PR review comments. Those are untrusted prompt inputs.
- A prompt-injection instruction can ask Codex to read environment variables, print `ROSENVALL_GIT_TOKEN`, or use it for network access.
- Even if GitHub-specific `GITHUB_TOKEN` is removed, the provider-neutral token is sufficient for GitHub/LocalGit clone, push and PR API operations.

Recommended fix:

- Do not expose repository write credentials to the Codex process.
- Short-term shell fix:
  - copy `ROSENVALL_GIT_TOKEN` into a non-exported shell variable,
  - `unset ROSENVALL_GIT_TOKEN GITHUB_TOKEN` before `codex exec`,
  - restore/export only after Codex exits and validation begins.
- Prefer a stronger split:
  - container/step A clones with token,
  - container/step B runs Codex with no repository token and no service account token,
  - container/step C validates, commits, pushes and creates PR with token.
- Add manifest tests that assert every `codex exec` command is preceded by unsetting `ROSENVALL_GIT_TOKEN`, `GITHUB_TOKEN` and any provider-specific token env vars.
- Add log-redaction tests for `ROSENVALL_GIT_TOKEN`, Basic auth remotes and `x-access-token` URLs.

### P1: Terminal Redaction Does Not Cover LocalGit Basic Credentials

Evidence:

- `RedactTerminalMessage` redacts:
  - GitHub `x-access-token:...@github.com`,
  - `ghp_`/`ghs_`/`github_pat_` token shapes,
  - environment variables containing `TOKEN`, `SECRET`, `PASSWORD` or `PRIVATE_KEY`,
  - `Authorization: Bearer ...`.
- LocalGit runner scripts build:
  - `Authorization: Basic $forgejo_auth` for Forgejo API calls,
  - authenticated remotes like `http://user:token@forgejo/...` for clone/push.
- Existing redaction tests cover GitHub tokens and Bearer headers, but not Basic auth or generic credentialed URLs.

Impact:

- If `curl`, `git`, `set -x`, shell error output or a failed command prints a LocalGit credentialed URL/header, the terminal tail and persisted snapshot can leak the service token.
- This is especially sensitive because the LocalGit service credential is platform-wide for internal Forgejo in v1.

Recommended fix:

- Extend redaction to cover:
  - `Authorization: Basic <base64-or-token>`,
  - generic URL credentials: `://user:password@host` -> `://[redacted]@host`,
  - Forgejo/Gitea token names if they have recognizable prefixes,
  - `Basic $forgejo_auth` literal shell output if emitted.
- Add tests:
  - `Authorization: Basic abc123` redacts,
  - `http://rdo:service-token@forgejo/rdo/app.git` redacts,
  - `https://user:secret@example.com/repo.git` redacts,
  - existing GitHub redaction still passes.
- Avoid ever writing `set -x` in runner scripts that handle tokens.

### P2: Snapshot Secret Metadata Is Clean, But Runtime Secret Operations Are Split

Evidence:

- `BoardSecretDto` stores metadata only: id, board id, optional repository id, key and timestamps.
- GitHub user OAuth tokens use `GitHubUserAuthorizationTokenStore` and a Kubernetes runtime-secret store.
- Board secrets and per-run provider tokens still render Secret YAML and apply it through `PipelineJobOrchestrator`.

Impact:

- The snapshot metadata model is sound, but secret write paths do not share the same safety guarantees.
- It is easy to fix one secret class and leave another using `kubectl apply`, as already happened.

Recommended fix:

- Consolidate all runtime secret writes behind `IRuntimeSecretStore`:
  - GitHub user auth token,
  - board secret,
  - implementation repository token,
  - cleanup token,
  - provider-sync source/target tokens.
- Keep manifest renderers for delete stubs only, or migrate deletion to the same runtime store.
- Add one cross-cutting test that no rendered Secret manifest contains real secret material or `stringData`.

## Twentieth Pass: Configuration Defaults And Environment Drift

### P1: `appsettings.json` Contains Stale Product Defaults

Evidence:

- `Repositories:Provider` defaults to `Forgejo` and `Repositories:Mode` defaults to `LinkExistingFirst`.
- `Repositories:Forgejo:ApiBaseUrl` defaults to `https://git.rosenvall.se/api/v1`, while the current LocalGit/Forgejo path is internal-only in the `rosenvall-devops` namespace.
- `Preview:KubeconfigPath` and `Pipelines:KubeconfigPath` default to `tofu/output/kubeconfig`, which already caused deployed API drift when production overrides were incomplete.
- `appsettings.Development.json` does not carry a local-dev override contract; the real local behavior is in `scripts/start-local-demo.ps1`.

Impact:

- Running the API outside the blessed script can silently pick old defaults and fail in ways that look like product bugs.
- Production safety depends on remembering every required environment override in Homelab manifests.
- Stale repo/provider defaults make new contributors think the current product model is external Forgejo link-first, not internal LocalGit plus GitHub.

Recommended fix:

- Move product-changing defaults out of `appsettings.json` and into explicit profiles:
  - `appsettings.Local.json` for script-driven localhost,
  - `appsettings.Kubernetes.json` for in-cluster defaults,
  - `appsettings.Development.json` for safe source-only defaults.
- Make empty kubeconfig the Kubernetes default when `DOTNET_RUNNING_IN_CONTAINER=true` or an explicit `Runtime:UseInClusterAuth=true`.
- Add options validation that fails startup with a clear message when:
  - preview-source mode is `kubernetes-job` but no runner image/kube auth is usable,
  - LocalGit is enabled but no credential is configured,
  - GitHub repo creation is enabled but App OAuth client config is missing.
- Update README to say the script is the supported local profile and plain `dotnet run` is a minimal API-only mode.

### P2: Runtime Build Tooling Should Not Be In The API Image By Default

Evidence:

- The API Dockerfile installs git, Node.js, npm, kubectl and global Codex CLI into the same image as the web API.
- This was useful for early vertical slices, but runner responsibilities now include Codex source generation, repo implementation, PR review fixes and provider sync.

Impact:

- The API attack surface and image size grow with every runner capability.
- Updating runner tooling forces API image rollout, even when web API code did not change.
- It blurs whether a failure belongs to the API service or the runner environment.

Recommended fix:

- Split runtime images:
  - `rosenvall-devops-api`: ASP.NET API only, no Node/Codex/git/kubectl except what the API must directly use.
  - `rosenvall-devops-runner`: git, kubectl, Codex, shell tooling for Kubernetes Jobs.
  - `rosenvall-devops-preview-base`: Vite runtime dependencies.
- Keep runner image digest in config and expose it in `/api/status`.
- Use separate vulnerability scanning and update cadence for API and runner images.

## Twenty-First Pass: Background Reconciliation And Readiness

### P1: Public App Deployment Is Marked Running After Apply, Not Readiness

Evidence:

- `BoardPublicAppDeploymentReconciler` renders the board public app manifest and calls `previews.ApplyAsync(manifest, cancellationToken)`.
- On apply success, it calls `store.MarkBoardPublicAppRunning(app.BoardId, apply.Message)`.
- Preview environments have a separate health monitor that waits for Deployment availability and ready pods before exposing the demo link.
- The public app flow does not appear to perform the same readiness/route check before marking the app `Running`.

Impact:

- The UI can show `Go to app` even though the Deployment is not ready or the HTTPRoute has not attached yet.
- This matches the kind of failure already seen with `demo-klocka.rosenvall.se` returning 404 after source was merged.
- Apply success is infrastructure acceptance, not user-facing availability.

Recommended fix:

- Add a `BoardPublicAppHealthMonitor` or extend `PreviewHealthMonitor` with a generic `RuntimeAppHealthMonitor`.
- Public app state should progress:
  - `Queued`,
  - `Applying`,
  - `WaitingForReadiness`,
  - `Running`,
  - `Failed`.
- Gate `Go to app` on readiness, not apply success.
- Health checks should inspect:
  - Deployment available/ready pods,
  - Service exists,
  - HTTPRoute accepted/attached when Gateway status is available.
- Keep preview cleanup after PR approval dependent on public app readiness when the product promise is "merge, deploy app, then clean preview".

### P2: Polling Loops Need Backoff And Work Shaping

Evidence:

- `PreviewHealthMonitor`, `ImplementationRunMonitor` and `BoardPublicAppDeploymentReconciler` poll on fixed intervals.
- The implementation monitor fetches logs and job JSON per awaiting run.
- SignalR events are broadcast with `Clients.All`, while the frontend mostly polls.

Impact:

- A burst of queued runs can cause repeated Kubernetes calls from the API process.
- A single slow Kubernetes/API call can delay the rest of the sequential loop.
- Fixed polling creates unnecessary load and noise for long-lived failures.

Recommended fix:

- Add per-run next-check timestamps and exponential backoff for unchanged states.
- Process a bounded number of runs per tick and record queue depth in `/api/status`.
- Use run-level scopes and metrics for each monitor pass.
- Move from `Clients.All` to scoped SignalR groups before relying more heavily on push updates.

## Twenty-Second Pass: Source And Diff Viewer Performance

### P2: Code Viewers Render Full Files/Diffs Without Virtualization

Evidence:

- `LineNumberedCode` maps every source line to DOM nodes with line numbers and highlighted token spans.
- `PullRequestDiffSectionView` maps every diff line to a button plus optional comments/composer.
- Backend Source file responses truncate files above 1 MB, which protects the API, but a 1 MB source file can still generate many thousands of highlighted tokens and DOM nodes.
- Pull request diffs are capped/truncated, but the frontend still renders the whole loaded diff continuously.

Impact:

- Source and PR review modals can become slow on large generated files or wide diffs.
- Shiki tokenization and React rendering happen on the main thread after the file is loaded.
- Comment anchors make performance more sensitive because every line is interactive.

Recommended fix:

- Add a viewer budget in the frontend:
  - render plain text above a configurable line/token threshold,
  - show "highlighting disabled for large file" rather than freezing,
  - optionally lazy-highlight visible chunks.
- Consider windowing for large source files and diffs using a simple in-house virtual list or a small dependency.
- Keep comment anchors stable by anchoring to file path + side + line number, not DOM index.
- Add tests for large input fallback:
  - 20,000-line plaintext file renders as non-highlighted,
  - diff comment anchors still resolve after windowing/chunking.

### P2: Syntax Highlighting Loads All Languages On First Code View

Evidence:

- `codeHighlight.ts` lazily creates a Shiki highlighter, but the first load imports all configured languages at once.
- The current language set includes TypeScript, TSX, JavaScript, JSX, JSON, CSS, HTML, Markdown, YAML, Dockerfile, shell, PowerShell and C#.

Impact:

- The main board UI does not pay the cost upfront, which is good.
- The first Source/PR diff interaction still pays for every supported language, even if the user opens only `package.json`.

Recommended fix:

- Keep a highlighter cache per language, or load a smaller common set first and dynamically add rarer languages.
- Instrument first-highlight duration in development so regressions are visible.
- If dynamic language loading becomes complex, keep all-language loading but show an inline "Preparing syntax highlighting..." skeleton in Source/diff panes.

## Twenty-Third Pass: Epic And Goal Orchestration

### P1: Epic Goal Is Currently A State Shell, Not A Reconciler

Evidence:

- `StartEpicRun` creates an `EpicRunDto` with one child entry per feature and marks children as `Queued` or `Ready` depending on approved child plans.
- `StartEpicGoal` creates or reuses an epic run and stores an `EpicGoalRunDto`.
- No hosted service or runner loop currently reconciles `EpicGoalRun` children into actual child AI/preview/PR work.
- The UI exposes "Start epic implementation" and "Start goal", which can imply automation beyond the current state record.

Impact:

- Users can start a goal and see it as running, but no child work will progress unless manually driven elsewhere.
- This can create the same trust problem as "PR merged but app not live": the system state says orchestration started, but no durable work loop exists.
- Adding real subagent orchestration later will be harder if the current DTO status names already imply stronger semantics.

Recommended fix:

- Rename current behavior in UI to "Create epic run plan" until a reconciler exists, or implement the first real reconciler slice.
- Define an explicit `EpicGoalStateMachine`:
  - `Queued`,
  - `PlanningChildren`,
  - `RunningChildren`,
  - `WaitingForReview`,
  - `Blocked`,
  - `Complete`,
  - `Cancelled`.
- Add a hosted service or queue worker that:
  - selects the next child feature,
  - starts a child AI plan or implementation based on policy,
  - records child run ids,
  - aggregates status to the epic,
  - stops after a bounded amount of work per tick.
- Keep "subagent" language in UI only when an actual child run has been queued.

### P1: Epic Planning Context Is Too Shallow For Real Decomposition

Evidence:

- `PromptContextRenderer.RenderPlanningContext(...)` lists descendant cards as `KEY title: type, status, priority`.
- It does not include descendant descriptions, human discussion, blockers, preview/PR/development state, or existing child AI plans.
- The requested product direction is that the highest parent becomes a planning root and child agents report back to that root.

Impact:

- A "Plan epic" run can see that children exist, but not enough detail to make a useful master plan or detect conflicts between child scopes.
- Feature/subagent runs will not get a clear contract from the epic unless the user manually copies details into the parent description.
- This can produce duplicate child work or plans that ignore existing PR/preview progress.

Recommended fix:

- Add a dedicated `EpicPlanningContextBuilder` rather than overloading the single-card planning context.
- Include for each direct child:
  - title/type/bug flag/status/priority,
  - description,
  - latest human discussion summary,
  - latest approved plan summary,
  - preview/PR/public app state,
  - blockers/unresolved review comments.
- For deep descendants, include a compact rollup to avoid huge prompts.
- Add tests proving unrelated board cards are excluded while descendants include enough detail for the epic root.

### P2: Hierarchy Progress Needs Clear Rollup Rules

Evidence:

- Work item summaries expose child counts, done child counts, blocked child counts and open PR child counts.
- Rollups count descendants rather than just direct children.

Impact:

- `3/7 done` can mean different things depending on whether it includes feature children, task grandchildren or both.
- Epic planning and goal state will need consistent progress semantics before large epics are reliable.

Recommended fix:

- Define rollups by card type:
  - Epic progress counts descendant Features and Tasks, grouped by Feature.
  - Feature progress counts descendant Tasks.
  - Task progress has no child progress unless nested subtasks are introduced later.
- Show direct child count and total descendant count separately where useful.
- Add tests for progress after child status changes, PR creation, PR merge and delete cleanup.

## Twenty-Fourth Pass: Shell And Kubernetes Job Rendering

### P0: Some Runtime Token Secrets Still Use `stringData` And `kubectl apply`

Evidence:

- `RepositoryImplementationJobManifestRenderer.RenderTokenSecret` renders implementation/repository tokens as a Kubernetes `Secret` with `stringData`.
- `RepositoryCleanupJobManifestRenderer.RenderGitHubTokenSecret` uses the same pattern for cleanup tokens.
- These manifests are submitted through `PipelineJobOrchestrator.ApplyAsync`, which runs `kubectl apply -f -`.
- Earlier GitHub user authorization was fixed with `KubernetesRuntimeSecretStore`, but implementation, cleanup, board secrets and provider-sync credentials now have multiple storage paths.

Impact:

- `kubectl apply` can store the submitted object in the `kubectl.kubernetes.io/last-applied-configuration` annotation. With `stringData`, that can include raw token material.
- The codebase now has one safe runtime-secret abstraction and several unsafe bypasses.
- Every new runner type has to remember the same security constraints manually.

Recommended fix:

- Make `IRuntimeSecretStore` the only path for runtime secrets:
  - implementation tokens,
  - cleanup tokens,
  - provider-sync tokens,
  - board runtime secrets,
  - GitHub user authorization tokens.
- Store all secret values with Kubernetes `data`, never `stringData`.
- Add a test that renders or reads every runtime secret path and fails if any secret manifest contains:
  - `stringData`,
  - `kubectl.kubernetes.io/last-applied-configuration`,
  - token values in annotations.
- After the migration, `PipelineJobOrchestrator` should apply only non-secret manifests.

### P1: Raw YAML String Rendering Is Too Fragile For Job Manifests

Evidence:

- Repository implementation, preview promotion, PR review fix, cleanup, preview-source and provider-sync jobs are large interpolated YAML strings in `Program.cs`.
- A recent production bug was exactly this class of issue: malformed indentation in preview-promotion `securityContext` caused `yaml: mapping values are not allowed`.
- Many values are escaped by local `Escape` helpers that only escape for double-quoted YAML strings, while other fields are inserted as command text, shell strings, labels or single-quoted paths.

Impact:

- It is easy to produce YAML that compiles in C# but is invalid or semantically wrong in Kubernetes.
- Each renderer has its own escaping assumptions.
- Tests currently assert many string fragments, but string assertions do not prove the manifest parses or has the expected typed object shape.

Recommended fix:

- Introduce a typed Kubernetes manifest builder for all RDO-generated runtime objects.
- Render with a YAML serializer from typed records/classes instead of interpolating raw YAML.
- Keep shell scripts as separate string resources or generated scripts mounted from ConfigMaps only after they pass shellcheck-style validation.
- Add manifest tests that parse the resulting YAML and assert:
  - kind/apiVersion/name/namespace,
  - securityContext fields,
  - automount service account token,
  - volumes and secret refs,
  - labels/owner labels,
  - no forbidden flags or commands.
- Keep a small snapshot test only for human readability, not as the primary contract.

### P1: Provider-Sync Token Secret Labels Are Rendered In The Wrong YAML Location

Evidence:

- `RepositoryProviderSyncJobManifestRenderer.RenderTokenSecret(...)` renders:
  - `metadata:`
  - `name: ...`
  - `namespace: ...`
  - then `labels:` at the same indentation as `metadata`, not inside `metadata`.
- The intended ownership labels are therefore not metadata labels on the Secret.
- Existing tests assert string fragments such as `provider-sync-token`, but do not parse the YAML and verify `metadata.labels`.

Impact:

- Provider-sync token Secrets may be created without the labels needed for ownership, cleanup and audit.
- A future label-based cleanup or admission rule would miss exactly the sensitive object it is meant to manage.
- This is another example where string-based manifest tests can pass while the Kubernetes object shape is wrong.

Recommended fix:

- Move provider-sync token Secret rendering to the shared runtime-secret store.
- In the short term, fix the indentation and add a YAML parse test that asserts:
  - `metadata.name`,
  - `metadata.namespace`,
  - `metadata.labels["app.kubernetes.io/part-of"]`,
  - `metadata.labels["rosenvall.devops/pipeline-run"]`,
  - no top-level `labels` property.
- Add the same typed-object assertion pattern to every secret/job renderer.

### P2: Preview Source Paths Are Scope-Checked But Not Manifest-Safe

Evidence:

- `PreviewSourcePolicy.Validate(...)` allows any non-hidden path under `src/` or `public/` as long as it is not empty, absolute or `.`/`..`.
- `PreviewManifestRenderer` interpolates `file.Key` into ConfigMap `data:` keys and `file.Path` into ConfigMap `items.path` without YAML quoting.
- The Kubernetes preview-source collector derives `keyFor(relative)` by normalizing paths, which can collide for different files such as `src/a-b.ts` and `src/a/b.ts`.

Impact:

- Codex can produce a file path that is allowed by the preview source policy but invalid or ambiguous in YAML.
- Duplicate ConfigMap keys can silently overwrite source content or make the rendered manifest fail later.
- This is the same class of issue as previous malformed YAML incidents: the policy accepts data that the renderer cannot safely render.

Recommended fix:

- Extend `PreviewSourcePolicy` to validate:
  - path characters against a conservative regex,
  - no spaces/control characters/colon-heavy paths unless quoted rendering is implemented,
  - unique source paths,
  - unique ConfigMap keys.
- Quote YAML scalar values consistently, or move preview/app manifest rendering to typed Kubernetes objects serialized by a library.
- Add regression tests for:
  - duplicate key rejection,
  - `src/foo: bar.tsx` rejection or correctly quoted rendering,
  - generated `public/asset.svg` still allowed.

### P1: `kubectl` Commands Should Use `ArgumentList`, Not String Arguments

Evidence:

- `PipelineJobOrchestrator.GetOutputAsync` takes arbitrary command strings such as `get applications.argoproj.io -n ... -o json`.
- It builds `ProcessStartInfo.Arguments` manually, including optional `--kubeconfig "path"`.
- `GitOpsStatusReader` appends a label selector directly into that command string after replacing quotes.

Impact:

- The process does not use a shell, so this is not classic shell injection, but it is still fragile argument parsing.
- Paths, label selectors and future arguments with spaces or quotes can break in ways that are hard to diagnose.
- The API surface encourages callers to format kubectl commands instead of passing structured arguments.

Recommended fix:

- Replace `GetOutputAsync(string command)` with `GetOutputAsync(params string[] args)` or a small typed `KubectlCommand`.
- Always populate `ProcessStartInfo.ArgumentList`.
- Model common operations explicitly:
  - `GetJson(kind, name, namespace)`,
  - `Logs(namespace, jobName, tail, allContainers)`,
  - `Apply(manifest)`,
  - `Delete(manifest)`.
- Add tests for selectors, kubeconfig paths with spaces and in-cluster auth.

### P1: Runner Scripts Need A Shared Shell Library

Evidence:

- Multiple job renderers construct credentialed remotes with `sed`, for example inserting `user:token@` after `://`.
- Changed-file detection uses repeated `git status --porcelain | sed ...` snippets.
- JSON payloads are assembled with ad hoc `json_escape()` shell functions.
- Forgejo PR creation parses JSON with `sed` instead of a JSON parser.

Impact:

- URL construction can break if tokens contain URL-reserved characters.
- `sed` JSON parsing can silently misread API errors or response shape changes.
- Small fixes are copied between implementation, preview promotion, cleanup, provider sync and review-fix scripts.

Recommended fix:

- Put shared runner behavior in a versioned script inside the runner image, for example `/opt/rdo-runner/lib.sh`.
- Centralize:
  - authenticated remote construction,
  - JSON POST helpers,
  - JSON parsing with `jq` or a small bundled tool,
  - changed-file collection,
  - token redaction before log output,
  - `RDO_STEP`/`RDO_FAILURE` emission.
- Use `GIT_ASKPASS` or credential helpers instead of embedding tokens in remote URLs where feasible.
- Test runner scripts with shell-focused tests outside Kubernetes.

### P1: LocalGit API Calls Do Not Fail Reliably On HTTP Errors

Evidence:

- LocalGit PR creation in implementation and preview-promotion runners uses `curl -sS -X POST`.
- GitHub calls usually use `curl -fsS`, so non-2xx responses fail the script.
- Forgejo responses are then parsed with `sed` for `number`, `state` or `html_url`.

Impact:

- A Forgejo API error can be treated as "no PR URL returned" instead of the actual error.
- If an error body happens to contain matching fields, the runner could report an incorrect PR state.
- This creates noisy downstream debugging in the UI.

Recommended fix:

- Use `curl -fsS` for Forgejo too, or capture `%{http_code}` and handle non-2xx explicitly.
- Parse response JSON with `jq`.
- Emit sanitized API error summaries through `RDO_FAILURE`.
- Add tests for Forgejo 401/403/409/500 response bodies.

### P2: Runtime Runner Images Are Hard-Coded In Several Renderers

Evidence:

- Implementation, cleanup, preview-promotion and review-fix jobs still render `ghcr.io/carnufex/rosenvall-devops-api:main` directly.
- Preview-source was already changed to accept a configured runner image.
- Homelab deploys digest-pinned API/frontend images, so runner pods using `:main` can drift from the API code that generated their manifests.

Impact:

- A deployed API can submit a runner job from a different code version.
- Debugging becomes ambiguous: the UI/API version and job behavior are not guaranteed to match.
- Digest pinning loses value for the most sensitive execution path.

Recommended fix:

- Add a single `Runner:Image` option and require it in production.
- Pass it into every job renderer.
- Expose the API image, runner image and frontend image in `/api/status`.
- Add static tests that no Kubernetes job renderer contains bare `:main`.

## Twenty-Fifth Pass: API Contract And Type Drift

### P1: Backend And Frontend DTOs Are Manually Duplicated

Evidence:

- `Program.cs` declares the API DTO records inline near the endpoint handlers.
- `frontend/src/App.tsx` repeats the same DTO shape manually for boards, repositories, previews, development, pull requests, source files, settings, epic runs and goals.
- There is no OpenAPI package or generated TypeScript client in the API project.

Impact:

- A backend field rename or default-value change can compile successfully but break the frontend at runtime.
- UI bugs like "same PR is open in one panel and merged in another" are easier to create when each component normalizes its own partial shape.
- TypeScript cannot tell whether it is consuming the real server contract or a stale copy.

Recommended fix:

- Add OpenAPI generation for the API.
- Generate a TypeScript client/types package during frontend build or CI.
- Move frontend domain helpers from ad hoc DTOs to generated DTO inputs plus explicit presentation models.
- Add a CI check that fails when generated API types are out of date.

### P1: Minimal API Results Are Not Typed Enough For Contract Generation

Evidence:

- Many endpoints return `Results.Ok(...)`, `Results.NotFound()`, `Results.Problem(...)` or `Results.BadRequest(...)` directly from inline lambdas.
- Several endpoints can return multiple shapes: DTO, plain string bad request, problem details, no content or accepted response.
- There are no route groups with `.Produces<T>()` metadata or typed result unions.

Impact:

- OpenAPI output, once added, will be incomplete unless endpoint result types are made explicit.
- Frontend callers cannot reliably distinguish validation errors, permission errors, service-unavailable conditions and not-found cases.
- New endpoints tend to copy the loose result style.

Recommended fix:

- Split endpoint registration into feature route groups and use typed handlers.
- Prefer `TypedResults` and explicit result unions for each handler.
- Add `.Produces<T>()`, `.ProducesProblem(statusCode)` and validation metadata where needed.
- Standardize domain errors into a small set of problem-detail codes:
  - `board-forbidden`,
  - `repository-provider-unavailable`,
  - `runtime-cleanup-failed`,
  - `review-comments-unresolved`,
  - `runner-submission-failed`.

### P2: Frontend Error Handling Loses Useful API Context

Evidence:

- `frontend/src/apiClient.ts` parses problem details but throws a plain `Error(message)`.
- Status code, problem title, detail, type and field validation data are discarded.
- UI components mostly display `error.message` and cannot branch on status or problem type.

Impact:

- The UI often cannot tell the difference between "not allowed", "provider unavailable", "needs user action" and "server failed".
- Retry and recovery actions are harder to show correctly.
- Toasts can be generic even when the API returned actionable detail.

Recommended fix:

- Introduce an `ApiError` class with:
  - `status`,
  - `title`,
  - `detail`,
  - `type`,
  - `errors`,
  - `requestPath`.
- Use problem-detail `type` or an RDO-specific `code` extension for user-action branching.
- Add tests for 400/401/403/404/409/500 parsing and timeout behavior.

### P2: Contract Naming Should Reflect Product State Machines

Evidence:

- Related state is spread across `DevelopmentDto`, `ImplementationRunDto`, `PullRequestDiffDto`, `PullRequestApprovalStateDto`, `PreviewDto`, `PipelineRunDto` and frontend presentation helpers.
- Some DTOs expose provider state (`pullRequestState`) while others expose RDO state (`pullRequestApprovedAt`, `approvalStatus`).

Impact:

- Provider state and RDO workflow state can drift in UI if each panel decides which field wins.
- New flows like LocalGit, GitHub and future provider sync require more branching in presentation code.

Recommended fix:

- Define a first-class `DeliveryStateDto` per work item:
  - preview state,
  - PR state,
  - production app state,
  - active run state,
  - allowed actions.
- Make the backend compute canonical action availability.
- Keep provider-native metadata available for details, but let UI buttons and badges read from the canonical delivery state.

## Twenty-Sixth Pass: Design System And UI Maintainability

### P1: The UI Has A CSS File, But Not A Component System

Evidence:

- `frontend/src/styles.css` contains global styles for sidebar, board, source browser, modals, work-item tabs, AI review, PR diff, terminal, settings and timeline.
- Components share broad classes such as `.panel`, `.modal`, `.secondary`, `.primary-action`, `.stepper-item` and `.icon-button`.
- Layout issues have repeatedly appeared when a generic class was reused in a new context, for example vertical/horizontal steppers and modal action placement.

Impact:

- A local visual fix can unintentionally change unrelated areas.
- New features tend to add more CSS selectors instead of reusing strict component primitives.
- The UI gets harder to reason about because the design language is implicit in one stylesheet.

Recommended fix:

- Create a small set of shared primitives:
  - `Button`,
  - `IconButton`,
  - `ModalDialog`,
  - `Tabs`,
  - `Stepper`,
  - `Panel`,
  - `Terminal`,
  - `ReviewSurface`,
  - `CodeViewer`.
- Give each primitive a narrow CSS namespace.
- Keep page-specific CSS close to feature modules after the planned frontend split.
- Add visual smoke tests for:
  - work-item modal tabs,
  - horizontal stepper,
  - source viewer,
  - Local PR diff review,
  - AI plan review.

### P1: Accessibility Is Still A Product Quality Risk

Evidence:

- Work-item tabs are styled button rows; there is no clear `role="tablist"` / `role="tab"` / `aria-selected` contract in the CSS or visible component structure.
- The modal uses a fixed backdrop and sticky header, but earlier review found no shared focus trap or focus restoration primitive.
- General buttons have hover styles, while focus-visible styling is inconsistent outside inputs and a few stepper controls.

Impact:

- Keyboard navigation and screen-reader behavior can be inconsistent.
- Modal-heavy workflows become harder for users who rely on focus order.
- This also makes browser-driven regression testing less precise because semantic roles are missing.

Recommended fix:

- Build shared accessible `ModalDialog` and `Tabs` primitives before the next major UI expansion.
- Add focus trap, Escape handling and focus restore to modal open/close.
- Add global `:focus-visible` styling for actionable controls.
- Add tests using roles rather than CSS selectors once React Testing Library is introduced.

### P2: Layout Breakpoints Are Too Coarse For Dense Workflows

Evidence:

- The stylesheet has one main `@media (max-width: 980px)` breakpoint that collapses many unrelated layouts to one column.
- Dense surfaces like Source, PR diff and Logs have very different responsive needs.

Impact:

- Fixing mobile behavior for one feature can degrade another.
- Horizontal scroll, sticky sidebars and modal overflow compete in the same breakpoint.

Recommended fix:

- Give dense tools their own responsive layout rules:
  - Source: tree above viewer under tablet width.
  - PR diff: files drawer/collapsible list under tablet width.
  - Logs: run list as segmented control on smaller screens.
  - Work-item overview: delivery row wraps as two rows instead of one long grid.
- Add Playwright screenshots for desktop and a narrow viewport for the core flows.

## Twenty-Seventh Pass: Browser Security And Auth Resilience

### P0: GitHub Webhook Is Not Signature-Verified Before Triggering Deploy

Evidence:

- `POST /integrations/github/webhook` parses the request body and trusts `action`, `pull_request.merged`, and `pull_request.html_url`.
- There is no visible check for `X-Hub-Signature-256`, no webhook secret configuration lookup, and no HMAC comparison before calling `QueueBoardPublicAppDeploymentForPullRequest(...)`.
- The GitHub App manifest renders `hook_attributes.url`, but not a webhook secret.

Impact:

- A forged HTTP request that names a known preview-promotion PR URL can make RDO believe a PR was merged.
- That can trigger production app deployment, preview cleanup, and `ApprovePullRequest(...)` state transitions without a real GitHub event.

Recommended fix:

- Add `GitHub:WebhookSecret` and render it into the app/manifest setup path.
- Verify `X-Hub-Signature-256` with constant-time HMAC-SHA256 comparison before parsing/acting on the payload.
- Reject missing/invalid signatures with 401/403 and log only event id/delivery id, never payload secrets.
- Add endpoint tests for missing, invalid and valid signatures.

### P1: Add Browser Security Headers For The RDO UI/API

Evidence:

- The API configures CORS, auth and exception handling, but there is no visible middleware for:
  - Content Security Policy,
  - `X-Frame-Options` / `frame-ancestors`,
  - `X-Content-Type-Options`,
  - `Referrer-Policy`,
  - HSTS/HTTPS forwarding.
- The frontend renders repository source, markdown, diffs, comments and provider URLs.

Impact:

- A future rendering bug in markdown/source/diff could become more damaging without a CSP.
- Clickjacking and MIME-sniffing protections are implicit in the hosting layer, not in the app.
- Security posture is harder to verify locally and in Homelab.

Recommended fix:

- Add a small security headers middleware for production.
- Start with a pragmatic CSP:
  - `default-src 'self'`,
  - `connect-src 'self' https://authentik.rosenvall.se https://api.github.com`,
  - allow the configured Authentik authority and preview/app origins as needed,
  - no `unsafe-inline` for scripts.
- Add tests or a smoke script that checks headers on `/` and `/api/status`.

### P1: Authentication Configuration Fails Open

Evidence:

- Startup enables JWT bearer authentication only when `Authentication:Authority` is non-empty.
- If the authority is missing, endpoint helpers treat requests as allowed because most checks are `user.Identity?.IsAuthenticated != true || ...`.
- This is convenient for local development, but the same code path is active in every environment unless configuration is correct.

Impact:

- A missing or misspelled production auth setting can turn the API into an unauthenticated app rather than failing startup.
- The frontend may still show a login flow, while direct API calls bypass authorization.
- This is especially risky now that Source browsing, provider sync, LocalGit merge/deploy and board cleanup are API actions.

Recommended fix:

- Add an explicit `Authentication:Mode` with values such as `DisabledForLocalDevelopment` and `Required`.
- In non-development environments, fail startup if `Authentication:Mode=Required` and authority/audience are missing.
- In disabled mode, expose `/api/status` with `authMode=disabled` and show a local-dev banner in the UI.
- Add a startup/config test for production settings that refuses an empty authority.

### P1: Work-Item Comment Identity Is Client-Supplied

Evidence:

- `POST /api/work-items/{workItemId}/comments` accepts `AddCommentRequest(string Author, string Kind, string Body)` and passes `request.Author`/`request.Kind` directly into `store.AddComment(...)`.
- `PATCH /api/comments/{commentId}` accepts `UpdateCommentRequest(string Actor, string Body)` and uses `request.Actor` for the "only your own comments" check.
- `DELETE /api/comments/{commentId}` accepts an `actor` query parameter and uses it for the same ownership check.
- Newer PR review and AI plan review comments already use `UserIdentityFromClaims(user).DisplayName` on create/update.
- PR review and AI plan review comments derive the author from claims, but update/delete currently require only board mutation rights, not comment ownership or an admin role.

Impact:

- Any authenticated user who can mutate a board can spoof ordinary comment authorship.
- The same user can edit or delete another human comment by sending that person's display name as `Actor`.
- Because comments are now intentionally included in AI planning context, spoofed comments can affect future plans and implementation context.

Recommended fix:

- Remove `Author`, `Kind` and `Actor` from public comment mutation requests for normal human comments.
- Derive author and actor from `ClaimsPrincipal` in every comment create/update/delete endpoint.
- Keep system comments behind internal store methods rather than accepting arbitrary `Kind` from the client.
- Store both immutable `AuthorSubject` and display `AuthorName`; use subject for ownership checks and display name only for UI.
- For PR/AI review threads, separate actions:
  - edit/delete: author or board admin,
  - resolve/reopen: any reviewer with board mutation rights, if that is the desired collaboration model.
- Add tests where user A cannot edit/delete user B's comment even if the request body/query claims to be user B.

### P1: Delivery Audit Actor Is Client-Supplied In Many Endpoints

Evidence:

- Several delivery endpoints accept `Actor`, `ApprovedBy` or `DiscardedBy` in request bodies:
  - `PreviewActionRequest(string Actor)`,
  - `DeleteAndCleanupRequest(string Actor)`,
  - `ApproveAiRunRequest(string ApprovedBy)`,
  - `ApprovePullRequestRequest(string ApprovedBy)`,
  - `StartImplementationRunRequest(..., string Actor, ...)`,
  - `StartPullRequestReviewFixRequest(string Actor, ...)`.
- Endpoint handlers then pass those values to store methods and timeline messages. Examples include board/work-item cleanup, preview stop/start, preview promotion, epic runs/goals and pipeline execution.
- Several fallbacks still default to `"crille"` when the request actor is missing.

Impact:

- Authenticated users can write misleading audit history by naming another actor in the request body.
- This matters more now that approval means "merge LocalGit PR, deploy app and clean preview"; the audit trail should identify the signed-in user, not a client-provided label.
- Demo/sandbox behavior can also look confusing if system actions fall back to `"crille"`.

Recommended fix:

- Remove human actor fields from public delivery requests.
- Derive actor subject/name from `ClaimsPrincipal` at the endpoint boundary.
- Keep explicit actor fields only for internal runner callbacks where the runner identity is authenticated separately, and store those as service actors.
- Replace hard-coded `"crille"` fallbacks with `"system"` for unauthenticated local-dev or with the signed-in user when auth is enabled.
- Add endpoint tests proving a user cannot approve/merge/delete as another actor by modifying the request payload.

### P1: OIDC Tokens Stored In `localStorage` Increase XSS Blast Radius

Evidence:

- `oidc-client-ts` is configured with `new WebStorageStateStore({ store: window.localStorage })`.
- The requested scope includes `offline_access`.

Impact:

- Any XSS in the app can read long-lived auth state.
- RDO displays user-generated markdown, code and diffs, so the rendering surface is broader than a simple CRUD app.

Recommended fix:

- Prefer `sessionStorage` for the browser client unless persistent login is a hard requirement.
- Consider a BFF/session-cookie model later if RDO becomes multi-user production software.
- Review all markdown/source rendering to ensure it never injects raw HTML.
- Add a test that markdown comments cannot render script/event attributes.

### P2: OAuth Callback State Is Process-Local

Evidence:

- GitHub manifest state and GitHub user authorization state are `ConcurrentDictionary` instances in `Program.cs`.
- The dictionaries are lost on API restart or when the app eventually runs more than one replica.

Impact:

- API restarts during OAuth flows can produce confusing "state missing or expired" failures.
- Horizontal scaling would make callback success depend on which pod receives the request.

Recommended fix:

- Store OAuth callback state in the database with TTL and one-time consumption.
- Include actor subject, installation id, created timestamp and redirect intent.
- Remove expired state in a background cleanup.
- Add restart/reload tests for GitHub user authorization and GitHub App manifest flows.

### P2: CORS Should Be Narrow And Observable

Evidence:

- The API uses a named CORS policy with configured origins and `AllowCredentials`.
- Local development needs `localhost:5173`; production needs the RDO frontend host.

Impact:

- Misconfiguration can silently block the app or overexpose API access.
- CORS failures tend to appear as generic "API unavailable" in the UI.

Recommended fix:

- Validate `Frontend:AllowedOrigins` on startup in production.
- Expose the active allowed origins in `/api/status` only for authenticated admins, or log them at startup.
- Add a local smoke check for `OPTIONS` preflight from `localhost:5173`.

## Twenty-Eighth Pass: Source Provider Correctness

### P1: Nested Source Paths Are Escaped As One Path Segment

Evidence:

- Source tree/file endpoints call provider clients with normalized paths from `NormalizeApiSourcePath(...)`.
- Both provider clients build contents URLs with `Uri.EscapeDataString(path)`:
  - Forgejo: `SourceContentsUrl(...)` in `Program.cs` around line 8141.
  - GitHub: `SourceContentsUrl(...)` in `Program.cs` around line 9587.
- For a path like `src/App.tsx`, this renders `/contents/src%2FApp.tsx` instead of `/contents/src/App.tsx` with each segment escaped.

Impact:

- Source browsing can work for root files while failing or behaving provider-dependently for nested files.
- The failure mode is confusing because the board can show the repository and root tree correctly, then return 404 for normal source files.

Recommended fix:

- Add a shared `EscapeRepositoryPath(path)` helper that splits on `/`, escapes each segment, and rejoins with `/`.
- Use it in GitHub and Forgejo source/file/contents APIs.
- Add tests for `src/App.tsx`, `.github/workflows/ci.yml`, `folder with space/file #1.ts`, and traversal attempts.

### P1: GitHub Source Reads Use Actor Default Installation, Not The Repository's Installation

Evidence:

- `ResolveGitHubRepositoryReadTokenAsync(...)` selects `store.GetDefaultGitHubInstallationId(actorSubject)` and falls back to `github.ConfiguredToken`.
- Source endpoints call this helper for any GitHub repository, independent of the repository's owner or board link.
- `GetGitHubIntegrationForRepository(repository)` exists and is already used elsewhere to choose repository-specific GitHub credentials.

Impact:

- In multi-user or multi-installation setups, Source may read from the wrong GitHub installation token.
- This can fail with 404/403 for a valid linked repo, or worse, succeed using an installation the actor should not rely on for that repo.

Recommended fix:

- Replace the generic default-token resolver with `ResolveGitHubRepositoryReadTokenAsync(repository, actor)`:
  - find the integration for `repository.Owner`/installation metadata;
  - verify the actor can use that integration;
  - mint that installation token;
  - do not fall back to a process-wide configured token for authenticated multi-user requests.
- Add a regression with two GitHub integrations where the actor's default installation differs from the repository owner.

### P1: GitHub Repository Credential Resolution Falls Back To Any Integration

Evidence:

- `DevOpsStore.GetGitHubIntegrationForRepository(repository)` first tries to match `repository.Owner` to `integration.AccountLogin`.
- If no owner match is found, it falls back to the newest `_githubIntegrations` entry.
- This helper is used by cleanup, preview-promotion, implementation runs and monitors to mint GitHub installation tokens for a repository.

Impact:

- A linked GitHub repository with missing/stale owner metadata can use the wrong installation token.
- In the best case, GitHub returns 404/403 and RDO shows a confusing downstream failure.
- In the worst case, a broadly installed app token can make a runner operate under an integration the board/user did not explicitly choose.

Recommended fix:

- Store the GitHub installation id on `RepositoryDto` or board-repository link when a GitHub repo is linked/created.
- Resolve GitHub credentials from that installation id, not from owner string and not from "newest integration" fallback.
- If legacy repository metadata lacks an installation id, require an explicit resync/relink step instead of silently picking another integration.
- Add tests for:
  - exact owner match,
  - missing owner,
  - two personal installations,
  - org repository remains disabled for creation but readable when linked through the correct installation.

### P2: Provider Read Errors Are Collapsed Into `null`

Evidence:

- `ForgejoRepositoryClient.GetSourceTreeAsync`, `GetSourceFileAsync`, `GetPullRequestFilesAsync` and `GetPullRequestDiffAsync` return `null` for non-success HTTP responses.
- Source endpoints often translate `null` into `Results.NotFound()`.
- Some GitHub source/read paths follow the same pattern of returning no typed error context to the endpoint.
- The Source endpoints do not wrap provider calls in a local error mapper. If `httpClient.SendAsync(...)`, JSON parsing or provider connection setup throws, the request can escape as a generic 500 instead of a provider-specific 503/502.

Impact:

- Provider auth errors, rate limits, network failures, missing repositories and actual missing paths can look the same in the UI.
- This recreates the earlier "404 Not Found" confusion around Local PR diff: the UI cannot tell if RDO lacks an endpoint, Forgejo lacks a PR, or provider auth failed.
- Localhost can show "Internal Server Error" when Forgejo is not port-forwarded or returns unexpected content, even though the actionable problem is "LocalGit unavailable".

Recommended fix:

- Return typed provider results, for example `ProviderReadResult<T>` with:
  - `Succeeded`,
  - `StatusCode`,
  - `Reason`,
  - `SanitizedMessage`,
  - `Value`.
- Map provider statuses explicitly:
  - 401/403 -> provider authorization failed,
  - 404 -> path/repo not found,
  - 429 -> retry later,
  - 5xx -> provider unavailable.
- Keep raw provider bodies out of logs unless sanitized and truncated.
- Add endpoint-level tests for Forgejo unavailable, invalid JSON and provider timeout so Source displays a targeted unavailable state rather than a 500.

### P2: Source APIs Need Explicit Ref Validation

Evidence:

- Source endpoints use `@ref.Trim()` when provided and pass it directly to provider APIs as a query value.
- The value is URL-encoded, so this is not shell injection, but the contract still accepts arbitrary strings.

Impact:

- Error handling and caching are harder because `main`, branch names, tags, and invalid ref-like strings all share the same endpoint behavior.
- Future provider implementations could accidentally use the ref in shell commands or path construction.

Recommended fix:

- Add a provider-neutral `GitRefName` validator for read-only Source refs.
- Accept branch/tag/commit-sha forms RDO intentionally supports and reject control characters, whitespace-only values, `..`, `@{`, leading/trailing slash, and lockfile-style suffixes.
- Keep the normalized ref in response DTOs so the frontend can display exactly what was requested.

### P2: Provider Sync Should Report Copy Completion Before Linking As A Board Repo

Evidence:

- `POST /boards/{boardId}/repositories/sync-to-provider` creates the target repository, immediately persists it with `store.CreateRepository(...)`, links it to the board, then queues the provider-sync Job.
- If the Job fails during clone or push, the target repo remains linked even though it may be empty or partial.

Impact:

- The Source page can show a synced secondary repository that is not actually synced.
- Users may start work from an incomplete target provider.

Recommended fix:

- Model provider sync as `PendingTargetRepository` until the Job reports `ProviderSyncReady`.
- Link the target repository to the board only after the sync run succeeds.
- If early linking is needed for UX, expose it as `Syncing` and block primary use until completion.

### P0: Production App Deploys Stored Preview Source, Not Necessarily The Merged PR Contents

Evidence:

- Local PR review supports AI fixes through `StartPullRequestReviewFixRun(...)` / run kind `pr-review-fix`, which checks out the existing PR branch and pushes additional commits.
- Production deployment is queued by `QueueBoardPublicAppDeployment(...)`.
- `QueueBoardPublicAppDeployment(...)` and `RenderBoardPublicAppManifest(...)` resolve `preview.SourceFiles` from the original preview/promotion source and render the app manifest from those files.
- The production app path does not read the final merged PR branch contents before deployment.

Impact:

- If a user reviews a LocalGit PR, comments on code, runs "Fix comments with AI", then approves/merges the PR, the board app can deploy the older preview source instead of the code that was actually merged.
- This breaks the core review contract: "what I approved in the PR is what goes live."

Recommended fix:

- Treat preview source as the initial promotion source only.
- Once a PR branch can receive follow-up commits, production deployment must use a content snapshot captured from the PR head/merge commit.
- Options:
  - after merge, read the repository tree at the merge commit and render production from that source;
  - or have PR creation/fix runners publish a signed/recorded `ApprovedSourceSnapshot` keyed by branch and commit SHA.
- Store the source commit SHA on `BoardPublicAppDto` and verify it matches the merged PR commit before marking the app `Running`.
- Add a regression: create preview source A, push AI review fix B to the PR branch, merge, then assert the production manifest contains B.

### P1: LocalGit Approval Merges Before Production Deployment Is Proven

Evidence:

- `/api/work-items/{workItemId}/approve-pr` handles LocalGit by first reading the Forgejo PR, then calling `localGit.MergePullRequestAsync(...)` when the PR is open.
- Only after the Forgejo merge succeeds does the endpoint call `QueueBoardPublicAppDeployment(...)`, render the public app manifest and apply it to Kubernetes.
- If manifest rendering or Kubernetes apply fails, the endpoint returns a problem response and records failure state, but the Forgejo PR has already been merged.
- `store.ApprovePullRequest(...)`, preview stop and card completion happen later, after production apply and preview cleanup.

Impact:

- A transient Kubernetes failure can leave the repository merged while the RDO card still looks unapproved or failed.
- The user cannot simply retry "Approve PR" as the same operation, because the irreversible source-control merge already happened.
- This weakens the intended contract: approve should mean "merge, deploy and clean up" as one understandable workflow.

Recommended fix:

- Treat approval as a state machine with explicit stages:
  - `ReadyToApprove`,
  - `Merging`,
  - `MergedWaitingForDeploy`,
  - `Deploying`,
  - `PreviewCleanup`,
  - `Complete`,
  - `FailedAfterMerge`.
- Make `FailedAfterMerge` a first-class UI state with a repair action such as "Retry production deploy and preview cleanup".
- Prefer a two-phase implementation:
  - preflight render and Kubernetes dry-run/app validation before merge;
  - merge only after the production deployment plan is renderable;
  - after merge, deploy from the exact merged source snapshot as described above.
- Add tests for:
  - manifest missing before merge does not merge the PR,
  - apply failure after merge stores `MergedWaitingForDeploy` or `FailedAfterMerge`,
  - retry continues deployment without trying to merge again.

### P1: Production Website Hosting Uses The Preview/Vite Dev Server Path

Evidence:

- `CreateBoardPublicAppResources(...)` uses `LocalReactPreviewProject.Image`.
- `PreviewManifestRenderer.Render(...)` is shared for preview and production app resources.
- When `SourceFiles.Count > 0`, the app container runs `npm run dev -- --host 0.0.0.0 --port 8080`.
- Source files are mounted from a ConfigMap and dependencies are copied from `/opt/rosenvall-preview/node_modules`.

Impact:

- `*.rosenvall.se` board apps are not true production builds; they run Vite dev server semantics.
- Runtime performance, caching, dependency behavior and failure modes differ from a built static site.
- This also keeps source code and full dev dependency trees in the runtime pod.

Recommended fix:

- Split preview rendering from public app rendering:
  - preview can keep the Vite dev-server loop for fast feedback;
  - production app should build once in a controlled builder Job and run static output from nginx or another minimal static server.
- Store build artifact metadata (`sourceCommit`, image/artifact hash, builtAt) on `BoardPublicAppDto`.
- Add tests that `devops-preview-*` manifests may use `npm run dev`, while `devops-app-*` manifests do not.

## Twenty-Ninth Pass: Repository Quality Gates

### P2: Add Shared .NET Build Policy Before Splitting More Code Out

Evidence:

- The projects enable nullable reference types, but there is no visible `Directory.Build.props` or `.editorconfig`.
- The project files do not set `AnalysisLevel`, `TreatWarningsAsErrors`, or `WarningsAsErrors`.
- The API currently has 100+ Minimal API endpoints and several large classes in one file, so refactors will rely heavily on compiler/analyzer feedback.

Impact:

- Code movement can introduce style, async, disposal, nullability and security issues that are only caught by review or runtime tests.
- Different projects can drift on analysis settings as the solution grows.

Recommended fix:

- Add `Directory.Build.props` with shared nullable, implicit usings, latest recommended analysis level, deterministic builds and selected warnings-as-errors.
- Add `.editorconfig` for C# and TypeScript formatting conventions.
- Start warnings-as-errors narrowly if needed, then ratchet as the large `Program.cs` split proceeds.

### P2: Frontend Has Tests But No Lint Or Type-Level Contract Gate Beyond Build

Evidence:

- `frontend/package.json` has `test`, `build` and `preview`, but no `lint` or `format` script.
- The frontend has a growing set of stateful UI helpers (`boardChrome`, `codeHighlight`, retry logic), plus a very large `App.tsx`.

Impact:

- Accessibility attributes, hook dependencies, accidental `any`, unused branches and import cycles are not caught consistently.
- The current test suite catches focused helpers, but not broad React correctness or UI contract drift.

Recommended fix:

- Add ESLint with React hooks, TypeScript and accessibility rules.
- Add a lightweight `npm run lint` to CI after `npm test`.
- Keep formatting mechanical and separate from feature PRs to avoid noisy diffs.

### P1: EF Uses `EnsureCreated` Even Though PostgreSQL Is Configurable

Evidence:

- Startup configures `DevOpsStateDbContext` for SQLite by default or PostgreSQL when `ConnectionStrings:DevOps` exists.
- Startup then calls `db.Database.EnsureCreatedAsync()`.
- There are no EF migrations in the API project.

Impact:

- `EnsureCreated` is acceptable for a prototype schema, but it does not support normal incremental schema evolution.
- Once the snapshot document is split into typed tables for logs, events, runs and credentials metadata, production upgrades will need migrations.
- PostgreSQL support looks production-oriented, but schema lifecycle is still prototype-oriented.

Recommended fix:

- Introduce EF migrations before adding typed persistent tables.
- Use `MigrateAsync()` in controlled startup/deploy contexts, or run migrations as a separate Kubernetes Job if startup-time migration is too risky.
- Add a migration smoke test against SQLite and, later, a Postgres test container in CI.

## Thirtieth Pass: Homelab Runtime Blast Radius

### P1: Runtime Service Account Has Broad Namespace And Secret Powers

Evidence:

- The authoritative Homelab `preview-rbac.yaml` binds `rosenvall-devops-runtime` to a `ClusterRole` that can `create` and `delete` namespaces.
- The same service account can manage preview `Deployment`, `Service`, `HTTPRoute`, `NetworkPolicy`, `Job`, `ConfigMap`, pod logs and events across the cluster role scope.
- A namespaced `Role` grants `get`, `create`, `update`, `patch` and `delete` on all `secrets` in the `rosenvall-devops` namespace.
- The API deployment runs as this same `rosenvall-devops-runtime` service account.

Impact:

- An API compromise or a manifest-rendering bug has a large Kubernetes blast radius: namespace deletion, route mutation and app-namespace secret access are all possible from the API pod identity.
- The role is practical for the current all-in-one orchestrator, but it makes every API endpoint that eventually applies YAML part of the cluster control plane.
- Board-owned secrets, GitHub app secrets and Forgejo service credentials share one namespace-level permission surface.

Recommended fix:

- Split identities:
  - API read/metadata identity,
  - preview namespace manager identity,
  - pipeline job submitter identity,
  - runtime secret writer identity,
  - cleanup identity.
- Move dynamic writes behind narrow service classes that use Kubernetes API clients with explicit object builders rather than shared `kubectl apply`.
- Where RBAC cannot express name prefixes, add admission controls or a small controller that only accepts namespaces/resources with `rosenvall.devops/owner=preview` and the expected name prefix.
- Add static tests against the authoritative Homelab manifests that enumerate the exact allowed operations per service account.

### P1: API Pod Likely Runs As Root With The Runtime Service Account Token

Evidence:

- Homelab `api-deployment.yaml` sets `serviceAccountName: rosenvall-devops-runtime`.
- Unlike the frontend Deployment, the API Deployment does not set `automountServiceAccountToken: false`, so the broad runtime Kubernetes token is mounted into the API pod by default.
- The API pod security context sets only `seccompProfile: RuntimeDefault`.
- The API container security context drops capabilities and disables privilege escalation, but does not set `runAsNonRoot`, `runAsUser` or `runAsGroup`.
- `src/Rosenvall.DevOps.Api/Dockerfile` does not declare a non-root `USER`.

Impact:

- If the base ASP.NET image defaults to root in this configuration, the API runs as root while also holding the broad runtime Kubernetes service account token.
- Dropping Linux capabilities helps, but it does not remove the risk of root-owned filesystem writes, root process behavior inside the container, or stronger blast radius after an API compromise.
- This stands out because most runner job manifests explicitly run as UID/GID 1000.

Recommended fix:

- Add a non-root user to the API Dockerfile or use the platform-provided `app` user when available.
- Set Homelab API container security context:
  - `runAsNonRoot: true`,
  - `runAsUser: <non-root uid>`,
  - `runAsGroup: <non-root gid>`,
  - `readOnlyRootFilesystem: true` if the app can write only to mounted PVCs/tmp.
- Keep the state and Codex-home PVC mounts writable only where needed.
- Add a static Homelab test that rejects API/frontend containers without explicit non-root settings.

### P1: Board Secret Metadata Is Persisted Before Kubernetes Writes Succeed

Evidence:

- `POST /api/boards/{boardId}/secrets` calls `store.CreateBoardSecret(...)` before rendering/applying the Kubernetes Secret manifest.
- `CreateBoardSecret(...)` removes any existing secret metadata with the same board/repository/key and persists the new metadata immediately.
- If Kubernetes apply fails, the endpoint deletes the newly created metadata, but the previous metadata has already been lost.
- `PUT /api/boards/{boardId}/secrets/{secretId}` calls `store.UpdateBoardSecret(...)` and persists `UpdatedAt` before the Kubernetes write is attempted; on apply failure there is no rollback.

Impact:

- The snapshot can say a board secret was removed or updated even though Kubernetes did not apply the corresponding change.
- Users can lose metadata for an existing secret if a replacement write fails.
- This makes environment secret behavior hard to reason about during RBAC or Kubernetes API outages.

Recommended fix:

- Treat board secret writes as a two-phase operation:
  1. validate and render desired metadata without mutating persisted state,
  2. write the Kubernetes Secret through the runtime secret store,
  3. persist metadata only after success.
- For replacements, keep the old metadata until the new Secret write succeeds.
- Add tests for create-replace failure and update failure that assert persisted metadata remains unchanged.

### P1: Preview Cleaner Deletes Namespaces By Label Alone

Evidence:

- `preview-cleaner-configmap.yaml` uses selector `app.kubernetes.io/part-of=rosenvall-devops-preview`.
- It deletes every matching namespace older than `TTL_HOURS`, except a single hard-coded `devops-previews` name and namespaces annotated `rosenvall.devops/keep=true`.
- The cleaner service account has cluster-wide `get`, `list` and `delete` on namespaces.

Impact:

- A mislabeled namespace can be deleted after 24 hours even if it was not created by RDO.
- The script does not also require the expected `devops-preview-` prefix, board/work-item ownership labels, or an RDO-created annotation.
- This is exactly the kind of operational safety guard that should be redundant: selector, prefix and ownership labels should all need to match.

Recommended fix:

- Require all of:
  - name starts with `devops-preview-`,
  - `app.kubernetes.io/part-of=rosenvall-devops-preview`,
  - `rosenvall.devops/managed-by=rosenvall-devops`,
  - board/work-item labels are present and valid.
- Log skipped namespaces with the missing reason so cleanup behavior is auditable.
- Add a test that renders the cleaner script and checks the guard conditions, or move cleanup into typed backend/controller code where it can be unit tested directly.

### P1: Forgejo Bootstrap Can Fail Silently

Evidence:

- `forgejo-deployment.yaml` creates the RDO admin user in a `postStart` shell loop.
- The command redirects output to `/tmp/forgejo-rdo-bootstrap.log` and ends with `|| true`.
- The API only receives `LocalGit__Password`; readiness of the Forgejo admin/service account is not part of the API startup contract.

Impact:

- Forgejo can be `Ready` while the RDO service user was not created or updated correctly.
- RDO then reports LocalGit as configured but repo creation/sync fails later with Forgejo API errors.
- Because the bootstrap failure is hidden in a container-local temp file, the failure is easy to miss from RDO status.

Recommended fix:

- Replace lifecycle bootstrap with an explicit Kubernetes Job that:
  - waits for Forgejo,
  - creates/updates the service user idempotently,
  - fails visibly if credentials cannot be established,
  - has logs retained long enough for troubleshooting.
- Add `/api/status` LocalGit checks that call Forgejo `/user` or a minimal authenticated endpoint and report service-user readiness.
- Keep Forgejo internal-only, but make "LocalGit available" mean "the API can authenticate and create a repository," not merely "config values exist."

### P1: Board Deletion Can Leave A Half-Deleted LocalGit State

Evidence:

- `POST /api/boards/{boardId}/delete-and-clean-up` first renders and applies Kubernetes cleanup, then calls `store.GetBoardOwnedLocalGitRepositories(boardId)`, then deletes each Forgejo repository with `ForgejoRepositoryClient.DeleteRepositoryAsync(...)`.
- `store.DeleteBoard(...)` and `store.DeleteRepositoryMetadata(...)` run only after all Forgejo deletes return success.
- `DeleteRepositoryAsync(...)` treats `404 NotFound` as success, but any transient Forgejo failure for a later repository stops the endpoint and keeps the board.

Impact:

- If Kubernetes cleanup succeeds and the first board-owned Forgejo repository is deleted, but a later repository delete fails, the board remains in RDO while some runtime resources and repos are already gone.
- Retrying can be idempotent for already deleted repos because `404` is success, but the UI can still show a board whose repository/source/PR links are partially missing.
- This is the same class of problem as provider-sync early linking: user-visible state is updated only at the end, while external side effects happen incrementally.

Recommended fix:

- Model board deletion as a persisted cleanup run with phases:
  - `KubernetesCleanup`,
  - `CloseOpenPullRequests`,
  - `DeleteBoardOwnedLocalGitRepositories`,
  - `DeleteMetadata`.
- Mark each external resource with cleanup status before mutating it, and persist progress after each phase.
- Let retries resume from the recorded phase and display the exact remaining resource that failed.
- For LocalGit repos, consider tombstoning repository metadata before deletion so Source/PR UI can render `Deleting` or `Deleted externally` instead of stale links.

## Thirty-First Pass: Dependency And CI Hygiene

### P2: Dependency Vulnerability Checks Are Manual, Not CI Gates

Evidence:

- `npm audit --omit=dev --json` currently reports zero frontend production vulnerabilities.
- `dotnet list .\Rosenvall.DevOps.slnx package --vulnerable --include-transitive` currently reports no vulnerable NuGet packages.
- `.github/workflows/ci.yml` runs backend tests and frontend build, but not dependency vulnerability checks.

Impact:

- The current dependency state is clean, but regressions would be discovered only when someone remembers to run the commands manually.
- The frontend now includes `shiki` and a larger dependency graph, so keeping a cheap audit gate is useful.

Recommended fix:

- Add CI steps:
  - `npm audit --omit=dev --audit-level=high`,
  - `dotnet list Rosenvall.DevOps.slnx package --vulnerable --include-transitive`.
- Keep severity threshold pragmatic so low-severity advisories do not block every PR, but high/critical issues do.
- Document the override process for false positives or unreachable dev-only paths.

### P2: Frontend Tests Are Not Run In CI

Evidence:

- Local `npm test -- --runInBand` passed with 54 tests.
- `.github/workflows/ci.yml` installs frontend dependencies and runs only `npm run build`.

Impact:

- The most valuable frontend regression tests for `boardChrome`, syntax highlighting, diff parsing and UI state helpers are not enforced on PRs.
- A PR can break Source/diff behavior while still passing TypeScript/Vite build.

Recommended fix:

- Add `npm test -- --runInBand` or the project's preferred non-watch test command to the frontend CI job before build.
- Add a future `npm run lint` gate once ESLint is introduced.

### P1: Image Publishing Is Not Gated By The CI Test Workflow

Evidence:

- `.github/workflows/ci.yml` runs backend tests and frontend build on `push` to `main`.
- `.github/workflows/publish-images.yml` also runs independently on `push` to `main` and publishes API, frontend and preview-base images.
- There is no `workflow_run` dependency, required status gate or in-workflow test/build step before image publishing.

Impact:

- A commit that breaks backend tests or frontend build can still publish mutable `:main` images.
- Homelab may stay pinned to older digests, but operators then have to know which pushed image digest is safe.
- This contributes to the "merged but not live / wrong digest" confusion seen during several recent fixes.

Recommended fix:

- Make image publishing depend on CI success:
  - combine tests/builds and publishing in one workflow with `needs`,
  - or trigger publish via `workflow_run` only when CI completes successfully.
- Publish immutable SHA tags only after the tested commit passes.
- Emit a small build metadata artifact containing commit SHA, API digest, frontend digest and preview-base digest for Homelab promotion.
- Keep mutable `:main` only as a convenience tag, not as the source of truth for deployment.

## Thirty-Second Pass: Source, Highlighting And Provider Sync

### P1: Provider-Sync Runs Are Queued But Not Completed In RDO State

Evidence:

- `POST /api/boards/{boardId}/repositories/sync-to-provider` creates the target repository, writes a token Secret, applies a Kubernetes Job and then calls `store.MarkPipelineRunExecuting(...)`.
- `RepositoryProviderSyncJobManifestRenderer` prints `RDO_STEP=ProviderSyncReady` from inside the Job.
- There is no corresponding monitor path that watches the provider-sync Job logs and calls a `MarkPipelineRunSucceeded` method. The store has `MarkPipelineRunExecuting(...)` and `MarkPipelineRunFailed(...)`, but no terminal success updater for this run type.

Impact:

- A successful provider sync can remain `Running` indefinitely in RDO even after the Kubernetes Job completes and the target repository has all refs.
- A failed provider-sync Job can also remain stale unless the initial `kubectl apply` failed.
- Users see a new linked repository but cannot trust the sync status, and cleanup/retry logic has no durable completion signal.

Recommended fix:

- Add a provider-sync job observer similar to implementation/preview-source recovery:
  - read Job condition,
  - read runner logs,
  - parse `RDO_STEP=ProviderSyncReady`,
  - set pipeline status to `Succeeded` with `CompletedAt`,
  - set `Failed` with sanitized last event/log on Job failure or timeout.
- Add tests for three cases:
  - queued provider-sync completes and marks the run succeeded,
  - failed Job marks the run failed,
  - API restart adopts an already-complete provider-sync Job instead of leaving the run `Running`.

### P2: Syntax Highlighting Is Functionally Correct But Has A Large First-Use Cost

Evidence:

- `frontend/src/codeHighlight.ts` maps supported file extensions and lazy-loads Shiki on first code view.
- `getHighlighter()` imports every configured language loader at once:
  - TypeScript,
  - TSX,
  - JavaScript,
  - JSX,
  - JSON,
  - CSS,
  - HTML,
  - Markdown,
  - YAML,
  - Dockerfile,
  - shell,
  - PowerShell,
  - C#.
- `LineNumberedCode` and `PullRequestDiffSectionView` correctly render highlighted tokens as React spans, so this is not an XSS problem.
- `frontend/src/codeHighlight.test.ts` covers language detection and diff-prefix splitting, but it does not exercise the async Shiki path, fallback behavior, or "line count is preserved after tokenization" contract.

Impact:

- The first Source or PR diff open can pay for all language grammars even if the user opens a single `package.json`.
- This may be noticeable because Source and PR diff modals already fetch repository data and render large line lists.
- It is acceptable for a first implementation, but it is a likely "it feels slow" hotspot.

Recommended fix:

- Keep the current safe token-rendering model.
- Split highlighters by language group or load grammars on demand:
  - initialize theme/engine once,
  - add only the requested language,
  - cache languages already loaded.
- Add lightweight timing instrumentation around first highlight load so the UI can show `Loading syntax highlighting...` only when it actually takes visible time.
- Add one async unit test with a small real language sample, plus one forced-fallback test, so later Shiki upgrades cannot silently break line preservation.

### P2: Source API Error Handling Should Return Provider-Aware Problems

Evidence:

- Source endpoints call `localGit.GetSourceTreeAsync(...)`, `localGit.GetSourceFileAsync(...)`, `github.GetSourceTreeAsync(...)` and `github.GetSourceFileAsync(...)` directly.
- Provider clients return `null` for non-success HTTP status codes, but transport errors, DNS failures, malformed provider JSON and timeouts can still escape the endpoint as generic 500s.
- This matches the observed local symptom where a missing or stale Forgejo port-forward can produce `Internal Server Error` rather than an actionable Source-page status.

Impact:

- Local development failures look like API crashes instead of `Forgejo unavailable`, `provider timeout`, or `source path not found`.
- The frontend cannot distinguish `repository missing`, `provider unavailable`, `unauthorized`, and `invalid path`.

Recommended fix:

- Wrap provider source reads in a small `SourceProviderResult<T>`:
  - `Found`,
  - `NotFound`,
  - `Unauthorized`,
  - `Unavailable`,
  - `InvalidResponse`.
- Log sanitized provider details server-side and return stable ProblemDetails to the UI.
- In local startup, include the active Forgejo API URL and port-forward state in `/api/status` so the Source page can surface the concrete missing dependency.

### P2: Repository Sync UX Has Two Different Meanings For "Sync"

Evidence:

- The board header still exposes a provider action that opens `SyncBoardRepositoryModal`, titled `Sync board to GitHub`, which only links an existing GitHub repository and still says new GitHub repository creation is disabled in that legacy flow.
- The new Source page exposes `Sync to new provider`, which creates a new target repository, pushes branches/tags and links it as a secondary board repo.
- The Add Board no-repository copy says the board can be synced to GitHub later, while the new provider-neutral path can sync to LocalGit or GitHub depending on source provider and authorization.

Impact:

- Users can reasonably interpret "sync" as either "link an existing repo to this board" or "copy this repo to another provider".
- This matters because the two actions have very different side effects:
  - one changes board metadata only,
  - the other creates a repo, creates Kubernetes credentials, runs a provider-sync Job and pushes git refs.
- It also makes product support harder because "sync failed" could mean GitHub picker, source provider copy, target repository creation, Kubernetes job submission or branch push.

Recommended fix:

- Rename the two actions:
  - `Link existing repository`,
  - `Copy repository to provider`.
- Move the legacy GitHub link flow into Configuration or Source under a `Repositories` management area.
- Use one provider-neutral repository management model in the UI:
  - linked repositories,
  - primary marker,
  - copy/sync runs,
  - external provider blockers.
- Update copy so "new repository creation disabled" applies only to the old GitHub link flow, not to Add Board's personal GitHub creation or LocalGit creation.

### P2: Source Ref Input Fetches Provider Data On Every Keystroke

Evidence:

- `SourceView` keeps `ref` in React state.
- The source tree loading effect depends directly on `ref`, `path` and selected repository.
- The ref field is a free-text input, so typing `main` can issue requests for `m`, `ma`, `mai` and `main`.
- API requests use the default 30 second frontend timeout unless a view overrides it.

Impact:

- Slow Forgejo/GitHub responses can queue overlapping tree requests while the user is still typing.
- The `active` cancellation guard avoids stale state updates, but it does not abort the underlying HTTP request.
- This can make Source feel sluggish and increases load on the provider when users test refs or commit SHAs.

Recommended fix:

- Split displayed ref input from committed ref:
  - `draftRef` updates on every keystroke,
  - `activeRef` updates on Enter, blur, or a short debounce.
- Add a branch/ref selector for known refs and reserve free text for advanced use.
- Use `AbortController` per Source tree/file request so superseded requests are cancelled, not merely ignored.

### P2: Local PR Diff Rendering Needs A Large-Diff Strategy

Evidence:

- The diff API truncates unified diff text above 320,000 characters.
- Forgejo changed-file metadata is requested with `limit=200`.
- The frontend parses the entire returned diff into sections, renders all sections in one continuous scroll pane and starts syntax highlighting per section.

Impact:

- The current reviewed demo diff of roughly 1,200 added lines is fine, but larger real PRs can still create thousands of DOM rows and many highlighter calls in one modal.
- Truncation protects the API and frontend from unbounded data, but it also means the user cannot review or comment beyond loaded lines.
- The UI needs to make that contract very explicit before LocalGit becomes the default provider for larger demo/epic work.

Recommended fix:

- Keep the continuous-scroll interaction, but add virtualization or chunked rendering once line count crosses a threshold.
- Show a sticky `Diff truncated` banner with:
  - loaded byte/line count,
  - provider file limit,
  - statement that comments can only be placed on loaded lines.
- Add backend pagination support later if users need to review very large LocalGit PRs fully inside RDO.

### P2: Source Repository Picker Does Not Separate Unsupported Providers

Evidence:

- `/api/boards/{boardId}/source/repositories` returns all repositories linked to the board.
- `/api/repositories/{repositoryId}/source/tree` and `/source/file` support only `LocalGit` and `GitHub`; other providers return a 400 problem.
- Add Board still supports `Custom URL`, which stores a provider such as `GenericGit`.

Impact:

- A board with a custom/public Git URL can show that repository in Source, but selecting it fails only after the tree request.
- Users perceive this as Source being broken rather than "this provider has no source API adapter yet".

Recommended fix:

- Include source capability in `RepositorySourceRepositoryDto`, for example:
  - `sourceReadable`,
  - `sourceUnavailableReason`.
- Disable unsupported repositories in the Source selector or show a clear empty state before issuing tree/file requests.
- When adding future providers, require each provider adapter to declare:
  - source tree support,
  - file support,
  - clone info support,
  - PR diff/review support.

## Verification Notes

Commands run successfully during this review:

```powershell
dotnet test .\Rosenvall.DevOps.slnx -c Release
dotnet list .\Rosenvall.DevOps.slnx package --vulnerable --include-transitive
cd frontend
npm test -- --runInBand
npm run build
npm audit --omit=dev --json
```

Current successful counts:

- Backend: 211 passed.
- Frontend tests: 54 passed.
- Frontend build: passed.
- npm production audit: 0 vulnerabilities.
- NuGet vulnerability scan: no vulnerable packages reported.

## Suggested Next Slice

Start with the runner credential boundary:

1. Add failing manifest tests that show `codex exec` currently receives `ROSENVALL_GIT_TOKEN`.
2. Update implementation and PR review-fix runners so the token is copied to a non-exported shell variable, then `unset ROSENVALL_GIT_TOKEN GITHUB_TOKEN` before Codex starts.
3. Restore/export the token only after Codex exits and the runner is ready to validate, commit, push and create/update PRs.
4. Add redaction tests for `ROSENVALL_GIT_TOKEN`, Basic auth remotes and provider-neutral credential URLs.
5. Prefer a follow-up split-container runner design where Codex never shares a process environment with repository write credentials.

Then fix the webhook security defect:

1. Add `GitHub:WebhookSecret` and a failing endpoint test for missing/invalid `X-Hub-Signature-256`.
2. Verify HMAC-SHA256 in constant time before parsing or acting on webhook JSON.
3. Add a valid-signature test proving a merged PR webhook still queues deployment.
4. Update GitHub App manifest/secret setup so the webhook secret is configured consistently.

Then fix the product correctness defect in the PR-to-production contract:

1. Add a failing test: preview source A is promoted to PR, PR branch receives review-fix source B, PR is approved, and the production manifest uses B.
2. Introduce a `BoardPublicAppSourceSnapshot` keyed by provider, repository, branch and commit SHA, or read source from the merged PR commit before render.
3. Store the deployed source commit on `BoardPublicAppDto`.
4. Refuse to mark the app `Running` if the production source snapshot does not match the merged PR state.
5. Run backend tests plus a LocalGit smoke through preview, PR review fix, approve PR, and app deploy.

Then take the concrete cleanup defect:

1. Add a failing test: provider-sync board cleanup includes `provider-sync-*` Job and `provider-sync-token-*` Secret.
2. Add `RenderPipelineRunCleanupDocuments` with a `Stage` switch.
3. Use it from both work-item and board cleanup.
4. Add a static RBAC check if provider-sync Secret deletion needs additional namespace permissions.
5. Run full backend tests.

Those four slices are small, high-signal, and cover the most concrete product/security defects found in this pass.

