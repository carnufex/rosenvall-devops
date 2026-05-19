# Rosenvall DevOps Implementation Plan

Use this file as the durable checklist for the active product slice. Keep items checked only after implementation and verification are done.

## Repo-Bound Boards Slice

- [x] Map current backend, frontend, and persisted snapshot contracts.
- [x] Add provider-neutral repository model.
- [x] Link boards to repositories.
- [x] Add API support for creating boards from existing or inline repositories.
- [x] Add board selector to the board view.
- [x] Add `Add board...` flow with repository fields.
- [x] Persist selected board locally during development.
- [x] Remove the small card drag handle.
- [x] Make the entire card draggable.
- [x] Add timeline event model and board timeline endpoint.
- [x] Record timeline events for card changes, previews, pull requests, commits, and pipeline runs.
- [x] Add timeline navigation and filtered timeline view.
- [x] Add pipeline run model.
- [x] Add Kubernetes Job manifest rendering for pipeline runs.
- [x] Update backend tests for boards, repositories, timeline, and pipeline manifests.
- [x] Update README and handoff documentation.
- [x] Verify with `dotnet test .\Rosenvall.DevOps.slnx`.
- [x] Verify with `npm run build`.
- [x] Smoke-test local API and frontend.

## Next Candidate Slices

- [x] Add first-class Forgejo/Gitea integration for self-hosted repositories.
- [x] Decide whether RDO creates Forgejo repositories directly or links existing repositories first.
- [x] Add real pipeline execution against Kubernetes Jobs instead of manifest rendering only.
- [x] Add pipeline run UI per board/repository.
- [x] Add token and code-delta metrics to timeline and dashboard.
- [x] Add Authentik user lookup for assignee options.
- [x] Add CI workflow for backend tests and frontend build.
- [x] Move homelab deployment manifests to consume images built from this repo.

## Preview Lifecycle Slice

- [x] Add preview statuses for implementing, applying, provisioning, running, and failed.
- [x] Keep preview URLs hidden until Kubernetes readiness is confirmed.
- [x] Store preview phase, message, last check time, pod name, failure reason, and failure logs.
- [x] Add Kubernetes health monitoring for deployment availability and ready pods.
- [x] Mark previews failed on CrashLoopBackOff, image pull/config errors, rollout timeouts, and related pod failures.
- [x] Preserve preview source and manifests for retry after failed apply or failed readiness.
- [x] Show step-by-step preview status in the card modal.
- [x] Show failed preview reason/log output and retry action in the card modal.
- [x] Update local development wording to say source generated instead of preview ready.
- [x] Add frontend polling while an open card preview is waiting for readiness.
- [x] Verify with `dotnet test .\Rosenvall.DevOps.slnx`.
- [x] Verify with `npm run build`.
- [x] Smoke-test TASK-4825 CrashLoopBackOff state in the local UI.

## Codex Provider And Preview Permission Slice

- [x] Add provider abstraction and route AI planning to `ollama` or `codex`.
- [x] Add server-side Codex CLI plan provider using ChatGPT-managed Codex auth.
- [x] Expose configured providers, models, and provider status in settings.
- [x] Let frontend Settings choose AI provider and provider-specific model.
- [x] Keep provider/model choice local to the browser for v1.
- [x] Add Codex CLI and `/app/codex-home` PVC support to homelab API deployment.
- [x] Remove unsafe preview initContainer `chmod -R /workspace`.
- [x] Verify provider routing, Codex CLI fake execution, and manifest regression with backend tests.
- [x] Verify with `dotnet test .\Rosenvall.DevOps.slnx`.
- [x] Verify with `npm run build`.

## Codex Preview Source Slice

- [x] Add tests proving generated preview source is used in manifests and survives snapshot restore.
- [x] Add Codex CLI preview source provider that seeds a Vite React/Tailwind workspace and reads back generated files.
- [x] Persist preview source files on the preview record for retry/re-render.
- [x] Make approve/implement use Codex-generated source before Kubernetes apply.
- [x] Allow already-approved legacy plans to be rebuilt with Codex source.
- [x] Keep running preview URLs gated by readiness while rebuilding/provisioning.
- [x] Verify with `dotnet test .\Rosenvall.DevOps.slnx`.
- [x] Verify with `npm run build`.
- [x] Smoke-test rebuilding TASK-4825 with Codex preview source.

## Follow-Up Production Hardening

- [ ] Replace configured Authentik users with live Authentik API calls using a Kubernetes secret token.
- [ ] Replace Forgejo configuration-only repository creation with authenticated Forgejo API creation.
- [ ] Move Codex implementation from API-process execution into an isolated Kubernetes Job or runner with streamed logs.
- [ ] Pin homelab manifests to immutable image tags after the first production promotion.
- [ ] Move runtime persistence from SQLite PVC to CloudNativePG once the bootstrap secret exists.
