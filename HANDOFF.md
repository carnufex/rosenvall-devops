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
- Workspaces, boards, work items, comments, AI runs, approvals, preview lifecycle, preview history, and internal pipeline status.
- Local Ollama planning provider.
- Controlled local nginx implementation runner for preview demos.
- Preview manifests target Kubernetes namespaces with Deployment, Service, ConfigMap, and HTTPRoute.

The app currently runs locally as:

- API: `http://localhost:5088`
- Frontend: `http://localhost:5173`
- Ollama: `http://localhost:11434/api`

## Important Runtime Behavior

AI planning:

- Frontend reads provider/model from `GET /api/settings`.
- Current local default model is `qwen3.5:latest`.
- `gpt-oss:20b` was tested locally and Ollama returned that it does not support `generate` or `chat` in this installation.
- Backend timeout is 120 seconds because local models can be slow on first load.
- If Ollama rejects or cannot answer, the API returns an error and does not create a fallback AI-run.

Comment iteration:

- `Comment` stores a human comment.
- `Comment + ask AI` stores the comment and starts a new AI-plan run using the full current work item context and comments.
- `Generate revised AI plan` can start a new plan after an approved/completed run.

Implementation/preview:

- V1 does not yet let Ollama freely implement arbitrary code.
- V1 uses a controlled nginx static-preview generator.
- The generator reads title, description, and human comments.
- Burger-related requests generate an interactive burger ordering page with quantities and a `Beställ` button.
- Approving a plan creates/updates the preview and applies Kubernetes resources through `kubectl`.
- Approving a PR marks it approved, moves the card to `Done`, stops preview, and writes runtime history.
- Start/stop preview actions are exposed from the UI and API.

## Key Files

- `src/Rosenvall.DevOps.Api/Program.cs`: API, store, Ollama adapter, preview orchestration, static preview generator.
- `src/Rosenvall.DevOps.Core/DevOpsDomain.cs`: domain primitives and state transitions.
- `frontend/src/App.tsx`: React shell, board, modal, dashboard, settings, comments, AI and preview actions.
- `frontend/src/styles.css`: Rosenvall dark technical UI styling.
- `tests/Rosenvall.DevOps.Tests/`: backend tests.
- `docs/frontend-control-inventory.md`: visible UI controls and their implementation status.
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

- Backend tests passed: 20 tests.
- Frontend build passed.
- GitHub repo pushed on `main`.

## Known Limitations / Next Work

Highest-value next slices:

1. Replace the controlled nginx generator with a real implementation agent contract.
2. Decide whether implementation runs in GitHub Actions, a local runner, or a Kubernetes job.
3. Replace `kubectl` process execution with a Kubernetes .NET client or make the API image explicitly include `kubectl` for in-cluster service-account use.
4. Add a model picker in UI instead of only reading the default from settings.
5. Store AI conversation/run lineage explicitly so comment-driven revisions are grouped.
6. Add real GitHub App authentication and workflow-dispatch signing.
7. Add CI workflow in this repo for `dotnet test` and frontend build.
8. Move homelab deployment manifests to point at images built from this repo.

## Homelab Relationship

The homelab repository should only contain deployment/GitOps wiring. Product code now belongs here.

If deploying to the homelab cluster later:

- Build/push API and frontend images from this repo.
- Add or update GitOps manifests in `Rosenvalls-Homelab` to consume those images.
- Point in-cluster Ollama endpoint at something like `http://ollama.ollama.svc.cluster.local:11434/api`.
- Preview domains currently use `*.rosenvall.se` / task hostnames, not `*.devops.rosenvall.se`.

## Current GitHub Repo

```text
https://github.com/carnufex/rosenvall-devops
```

Clone from another machine:

```powershell
git clone https://github.com/carnufex/rosenvall-devops.git
```
