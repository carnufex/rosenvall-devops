# Release Checklist

Use this checklist when promoting `rosenvall-devops` to `devops.rosenvall.se`.

1. CI passed for the commit that will be released.
2. Images published with SHA tags for API, frontend and preview-base.
3. Homelab digest pins updated for the API and frontend images.
4. ArgoCD synced the `rosenvall-devops` application and reports Healthy.
5. Live `/api/status` reports the expected API commit SHA, API image, frontend image, runner image and configuration mode.
6. The frontend build shown in Settings matches the expected release identity.
7. Run `scripts/check-deployed-version.ps1` and verify local git SHA, GHCR digest, Homelab manifest digest and live `/api/status` agree.
8. Run `scripts/doctor-local-demo.ps1` when debugging localhost drift before assuming deployed behavior is broken.

Notes:

- Keep digest pinning. Do not switch Homelab manifests to mutable `:main` tags.
- If `/api/status` and the Settings release diagnostics disagree with the expected commit or image, treat the issue as deployment drift before debugging product logic.
- The local doctor script can check Source and Local PR diff endpoints when `-BoardId` and `-WorkItemId` are provided.
