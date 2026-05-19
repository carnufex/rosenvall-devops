# Rosenvall DevOps Handoff

Use this repository for continued work:

- Local path: `C:\Users\Crille\source\repos\rosenvall-devops`
- GitHub: `https://github.com/carnufex/rosenvall-devops`
- Product name: Rosenvall DevOps, abbreviated `RDO`

This repo was split out from `Rosenvalls-Homelab` so future sessions should start here, not in the homelab repository.

## Current State

RDO is a vertical MVP for an AI-assisted DevOps workspace:

- .NET 10 ASP.NET Core Minimal API backend.
- React + TypeScript frontend.
- SQLite for local state when no PostgreSQL connection string is configured.
- Workspaces, provider-neutral repositories, repo-bound boards, work items, comments, AI runs, approvals, preview lifecycle, preview history, timeline events, pipeline execution, code/token metrics, and internal pipeline status.
- Local Ollama planning provider.
- Server-side Codex CLI provider can generate both AI plans and preview source when `CODEX_HOME` is logged in on the API host.
- Controlled local Vite React/Tailwind fallback generator remains available for tests/legacy previews.
- Preview pods use `ghcr.io/carnufex/rosenvall-devops-preview-base:main`, a prewarmed image with shared npm dependencies installed.
- Preview manifests target Kubernetes namespaces with Deployment, Service, ConfigMap, and HTTPRoute.

The app currently runs locally as:

- API: `http://localhost:5088`
- Frontend: `http://localhost:5173`
- Ollama: `http://localhost:11434/api`
- Codex provider: uses server-side Codex CLI auth from `CODEX_HOME` or configured `Ai:Codex:Home`.

## Important Runtime Behavior

AI planning:

- Frontend reads provider/model from `GET /api/settings`.
- Current local default provider/model is `ollama` / `qwen3.5:latest`.
- `codex` is available as a planning and preview-source provider when the API host is logged in with `codex login --device-auth`; homelab stores that state under `/app/codex-home`.
- `gpt-oss:20b` was tested locally and Ollama returned that it does not support `generate` or `chat` in this installation.
- Backend timeout is 120 seconds because local models can be slow on first load.
- If the selected provider rejects or cannot answer, the API returns an error and does not create a fallback AI-run.

Comment iteration:

- `Comment` stores a human comment.
- `Comment + ask AI` stores the comment and starts a new AI-plan run using the full current work item context and comments.
- `Generate revised AI plan` can start a new plan after an approved/completed run.

Boards and repositories:

- Boards can be created per repository from the UI.
- Repositories are provider-neutral and currently model Forgejo, GitHub, AzureDevOps, and GenericGit.
- First self-hosted Git target is Forgejo/Gitea, not a native Git server inside RDO.
- Repository policy is `LinkExistingFirst`; Forgejo repository creation is exposed as configuration but live API credentials are a follow-up.
- Board timelines combine cards, previews, pull requests, commits, and pipeline runs.
- Pipeline runs can render Kubernetes Job manifests through `GET /api/pipeline-runs/{pipelineRunId}/manifest` and submit them with `POST /api/pipeline-runs/{pipelineRunId}/execute`.
- Assignee options come from configured Authentik users plus existing board assignees.

Implementation/preview:

- Approving/implementing a plan now asks server-side Codex CLI to modify a seeded Vite React/Tailwind workspace from the approved plan before Kubernetes deployment.
- V1 still does not let Ollama freely implement arbitrary code; Ollama can generate plans, while implementation source is Codex-backed.
- V1 uses a controlled Vite React/Tailwind preview scaffold inspired by Lovable/shadcn project structure.
- On Windows local dev, Codex preview-source execution uses Codex CLI bypass mode inside a throwaway temp workspace because `workspace-write` currently behaves as read-only there. Linux/homelab keeps `workspace-write` unless `Ai:Codex:ImplementationBypassSandbox` is set.
- Per-ticket preview source is mounted from a ConfigMap; the prewarmed preview-base image supplies `node_modules` so startup runs Vite directly instead of `npm install`.
- The Codex source step reads title, description, comments, and the selected plan. Legacy fallback generation reads title, description, and human comments.
- Burger-related requests generate an interactive burger ordering page with quantities and a `Beställ` button.
- Approving a plan creates/updates the preview and applies Kubernetes resources through `kubectl`.
- Approving a PR marks it approved, moves the card to `Done`, stops preview, and writes runtime history.
- Start/stop preview actions are exposed from the UI and API.

## Key Files

- `src/Rosenvall.DevOps.Api/Program.cs`: API, store, Ollama adapter, preview orchestration, React preview generator, repository/board/timeline model, and pipeline manifests.
- `src/Rosenvall.DevOps.Core/DevOpsDomain.cs`: domain primitives and state transitions.
- `frontend/src/App.tsx`: React shell, board, modal, dashboard, settings, comments, AI and preview actions.
- `frontend/src/styles.css`: Rosenvall dark technical UI styling.
- `tests/Rosenvall.DevOps.Tests/`: backend tests.
- `docs/implementation-plan.md`: durable checklist for completed and next implementation slices.
- `docs/frontend-control-inventory.md`: visible UI controls and their implementation status.
- `deploy/homelab/`: Kubernetes manifests for the homelab deployment at `devops.rosenvall.se`.
- `.github/workflows/ci.yml`: backend test and frontend build validation.
- `workflows/ai-implementation.yml`: placeholder target-repo workflow for future agent-driven implementation.

## How To Run

One-command local demo:

```powershell
.\scripts\start-local-demo.ps1
```

Then open:

```text
http://localhost:5173
```

Stop local demo:

```powershell
.\scripts\stop-local-demo.ps1
```

Manual API:

```powershell
dotnet run --project .\src\Rosenvall.DevOps.Api\Rosenvall.DevOps.Api.csproj --urls http://localhost:5088
```

Manual frontend:

```powershell
cd .\frontend
npm install
npm run dev
```

## Verification Commands

```powershell
dotnet test .\Rosenvall.DevOps.slnx
cd .\frontend
npm ci
npm run build
```

Last known verification before handoff:

- Backend tests passed: 28 tests.
- Frontend build passed.
- GitHub repo pushed on `main`.

## Known Limitations / Next Work

Highest-value next slices:

1. Move Codex implementation from API-process execution into an isolated Kubernetes Job or runner with streamed logs.
2. Decide how Codex-generated source is promoted into real repositories and pull requests.
3. Replace `kubectl` process execution with a Kubernetes .NET client or make the API image explicitly include `kubectl` for in-cluster service-account use.
4. Store AI conversation/run lineage explicitly so comment-driven revisions are grouped.
5. Add real GitHub App authentication and workflow-dispatch signing.

## Homelab Relationship

The homelab repository should only contain deployment/GitOps wiring. Product code now belongs here.

If deploying to the homelab cluster later:

- Build/push API and frontend images from this repo.
- Add or update GitOps manifests in `Rosenvalls-Homelab` to consume those images.
- Point in-cluster Ollama endpoint at something like `http://ollama.ollama.svc.cluster.local:11434/api`.
- After deploying the API image, run `kubectl -n rosenvall-devops exec -it deploy/rosenvall-devops-api -- codex login --device-auth` if the `codex` provider should be usable in homelab.
- Preview domains currently use `*.rosenvall.se` / task hostnames, not `*.devops.rosenvall.se`.

## Current GitHub Repo

```text
https://github.com/carnufex/rosenvall-devops
```

Clone from another machine:

```powershell
git clone https://github.com/carnufex/rosenvall-devops.git
```
