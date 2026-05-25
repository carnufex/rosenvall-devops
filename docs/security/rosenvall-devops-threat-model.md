# Rosenvall DevOps Threat Model

Date: 2026-05-25

## Scope

This pass covers the RDO browser/API boundary, GitHub App integration, Kubernetes preview and repository runner jobs, cleanup runs, board secrets, and repository creation/sync.

## Trust Boundaries

- Browser to API: users can mutate boards, work items, teams, repo links, secrets, implementation runs, and cleanup runs through API calls.
- API to GitHub: the API mints GitHub App installation tokens and should never expose those tokens to the browser or snapshots.
- API to Kubernetes: the API applies preview, implementation, cleanup, token-secret, and board-secret manifests.
- Codex runner to repository: runner jobs clone repositories with short-lived per-run GitHub tokens and ask Codex to edit files.
- GitOps repository to cluster: merged PRs become cluster state through ArgoCD, so cleanup is PR-first unless explicitly reviewed as a fallback.

## Findings And Controls

- Board/work-item authorization is still mostly coarse-grained. Store-level team access exists, but several mutation endpoints do not consistently enforce board membership before mutating state. Add endpoint-level `CanMutateBoard` checks before multi-user use.
- GitHub App run tokens are short-lived and stored as per-run Kubernetes Secrets. Cleanup manifests include per-run token Secrets and explicitly exclude the shared Codex auth PVC, which reduces blast radius.
- Runner jobs use isolated writable `CODEX_HOME`, non-root UID/GID, dropped capabilities, and `RuntimeDefault` seccomp. This should remain mandatory for implementation and cleanup runners.
- Cleanup now keeps repository cleanup as a tracked artifact on the original card. Open implementation PRs are closed; merged implementation PRs require cleanup PRs, avoiding direct cluster mutation as the default.
- Board secrets store metadata in snapshots and put secret values only into Kubernetes Secrets. The browser still sends secret values during create/update, so transport/authZ hardening matters.
- Preview routes are public and predictable under the preview domain. Preview namespaces need TTL cleanup and should never mount board or GitHub credentials unless explicitly required.
- Repository creation defaults to private repositories with README initialization. The GitHub App needs narrow installation scope and repository administration permission only where repository creation is expected.
- Stuck cleanup jobs now surface job/pod/event diagnostics instead of leaving cards indefinitely in a running state.

## Follow-Up Hardening

- Enforce board authorization in every mutation endpoint, including repository link/sync, cleanup adoption, secrets, and work-item actions.
- Add audit events for secret create/update/delete, GitHub repo creation, PR adoption, and explicit delete-without-repo-cleanup.
- Add TTL or finalizer checks for preview namespaces and runner Jobs/Secrets.
- Avoid logging GitHub token material by keeping manifests with token values out of terminal logs and snapshots.
- Add admission-policy checks for runner pods: non-root, no host mounts, no privileged mode, no shared Codex PVC in runner containers.
