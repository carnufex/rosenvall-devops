# AI Runner Security Audit - 2026-05-25

## Scope

This pass reviewed the Rosenvall DevOps browser/API boundary, board and work-item authorization, GitHub App token handling, Kubernetes runner jobs, cleanup flows, preview environments, and AI prompt/runner guardrails.

Four parallel subagents reviewed frontend UX failure modes, backend authorization and data integrity, runner/cleanup behavior, and AI guardrails.

## Fixed In This Pass

- Mutating board, work-item, preview, cleanup, AI-plan, and repository implementation endpoints now check board/work-item/AI-run mutation authorization when authentication is active.
- Work item creation, update, and move now reject unknown board/status combinations instead of creating hidden or invalid items.
- Frontend API requests now have a default timeout, and create/comment/team/secret/adopt flows only clear or close forms after a successful backend action.
- Markdown links rendered in comments/plans now allow only safe `http`, `https`, and relative URLs.
- Repository implementation and cleanup jobs now have `activeDeadlineSeconds: 3600`.
- Runner status only emits `RDO_STEP=PullRequestReady` after a pull request URL has been resolved.
- Implementation and cleanup Codex runner manifests now use `--sandbox workspace-write` instead of `--dangerously-bypass-approvals-and-sandbox`.
- Preview, implementation, and cleanup terminal logs redact common token/secret patterns before persistence/broadcast.
- Cleanup PR adoption now verifies that the PR is readable and belongs to the same repository as the source implementation run.
- GitHub App manifest and installation callbacks now require a short-lived state issued by the start page.
- Board secret create rolls back metadata if Kubernetes apply fails, and delete removes the Kubernetes Secret before removing local metadata.
- GitOps allowed paths, ArgoCD namespace, and application selector values are normalized to safe relative paths and shell-safe Kubernetes selector input.

## Remaining High-Risk Items

- The Codex runner still receives GitHub and board secret material in the same process environment as model-driven shell execution. The next hardening step should split credentials into a runner-owned helper or sidecar so Codex edits files without direct token/secret access.
- Board/repository read authorization is still coarse. Mutations are guarded, but lists such as boards, work items, repositories, integrations, pipelines, and preview events should be filtered by the caller's board/team access.
- GitHub App manifest callbacks now validate state, but the start page is still unauthenticated. Binding installation state to a specific authenticated user/session would be stronger.
- GitHub installations and repository picker data are still globally visible to authenticated users. They should be scoped by installer, organization policy, or board/team membership.
- Board secret update should reconcile failure states more explicitly; create/delete are now safer, but update still has limited rollback because secret values are not persisted.
- Model-generated repository profile skill drafts are persisted and later injected as context. Treat them as untrusted data and render them as suggestions, not authoritative instructions, until explicitly approved.
- Preview configuration should get stricter schema validation for hostnames and route naming to prevent public route collisions.

## Recommended Next Guardrails

- Run Codex with no GitHub token in its environment. Let the runner own clone/push/PR operations through a minimal helper API or wrapper.
- Mount board secrets only into validation/execution phases that need them, never into the Codex editing phase by default.
- Add endpoint-level integration tests for forbidden cross-board mutations and read filtering.
- Add a repository allow-list per board/team for GitHub App installations and repository creation.
- Add log redaction tests for Kubernetes event summaries and job apply/delete failure messages.
- Make tests/build failures blocking for repository implementations unless the board explicitly opts into best-effort validation.
