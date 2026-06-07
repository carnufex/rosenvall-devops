# Homelab Deployment

These manifests are the product-side contract for the Rosenvall homelab GitOps repo.

Apply from this repo during local testing:

```powershell
kubectl apply -k deploy/homelab
```

Expected homelab dependencies:

- Gateway API with a `gateway/external` parent gateway.
- Authentik application/client for `rosenvall-devops`.
- DNS/Cloudflare route for `devops.rosenvall.se`.
- Ollama service at `http://ollama.ollama.svc.cluster.local:11434/api`.
- Optional Codex provider login in the API pod: `kubectl -n rosenvall-devops exec -it deploy/rosenvall-devops-api -- codex login --device-auth`.
- Internal-only Forgejo for LocalGit repositories in the `rosenvall-devops` namespace.

The frontend uses Authentik OIDC. The API validates JWTs with the same Authentik authority and also needs Kubernetes RBAC because it creates preview and pipeline resources.
Codex auth is stored in the `rosenvall-devops-codex-home` PVC and is never sent to the browser.
LocalGit/Forgejo is not exposed with an HTTPRoute in v1. RDO is the UI for demo repository creation, source browsing, local pull request review, merge/deploy and board-owned repository cleanup.
