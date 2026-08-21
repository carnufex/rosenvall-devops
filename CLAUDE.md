# Rosenvall DevOps (RDO) — Claude guide

Start with `HANDOFF.md` (current state, how to run locally, conventions), then `README.md`.
Global work style and build/deploy rules live in `~/.claude/CLAUDE.md`.

- Stack: .NET 10 ASP.NET Core Minimal API + SignalR (`src/`), React + TypeScript frontend
  (`frontend/`), tests in `tests/`. SQLite locally when `ConnectionStrings__DevOps` is empty;
  CloudNativePG in the cluster.
- Local run: API `http://localhost:5088`, frontend `http://localhost:5173`, Ollama
  `http://localhost:11434/api`.
- Deploy: images are built locally and pushed to `registry.rosenvall.se` (GitHub Actions is
  billing-blocked — never wait for CI). The cluster manifests live in the Rosenvalls-Homelab
  repo (`kubernetes/applications/rosenvall-devops/`), NOT in `deploy/homelab/` here; ArgoCD
  syncs from that repo, so bump image refs there.
- Claude inside the cluster authenticates with `CLAUDE_CODE_OAUTH_TOKEN` (long-lived token
  from `claude setup-token`) — see project memory.
