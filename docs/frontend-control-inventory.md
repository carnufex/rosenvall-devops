# Frontend Control Inventory

This inventory keeps the v1 UI honest: every visible control must either perform a real action, show API-backed read-only state, or be absent from the interface.

| Surface | Control | Status | Behavior |
| --- | --- | --- | --- |
| Sidebar | New card | Implemented | Opens the create work item modal with `Todo` as default status. |
| Sidebar | Board | Implemented | Navigates to the board route. |
| Sidebar | Dashboard | Implemented | Navigates to API-backed dashboard state. |
| Sidebar | Settings | Implemented | Navigates to API-backed settings from `/api/settings`. |
| Sidebar | Documentation / Support | Removed from v1 | Removed until real destinations exist. |
| Topbar | Search work items | Implemented | Filters visible board cards by key, title, type, status, assignee, or priority. |
| Topbar | Pipelines / Logs / Deploy / notifications / help | Removed from v1 | Removed until pipelines, logs, deploys, notifications, and help are implemented. |
| Board | Column plus button | Implemented | Opens create modal with the clicked column as default status. |
| Board | Card click | Implemented | Opens a work item modal instead of navigating away from the board. |
| Board | Drag card | Implemented | Moves cards between columns and persists `status`/`sortOrder` via API. |
| Board card | AI plan | Removed from v1 | AI actions live in the modal so the card stays reliable for click/open and drag handling. |
| Work item modal | Save | Implemented | Patches title, description, type, status, priority, and assignee. |
| Work item modal | Delete and clean up | Implemented | Confirms deletion, calls the API delete endpoint, and blocks state removal if Kubernetes preview cleanup fails. |
| Work item modal | Comment | Implemented | Posts a human comment to the work item. |
| Work item modal | Comment + ask AI | Implemented | Posts the comment, then starts a new AI-plan run using the full current work item context and comments. |
| Work item modal | Generate AI plan | Implemented | Starts a plan when no active plan exists. |
| Work item modal | Generate revised AI plan | Implemented | Starts a fresh AI-plan run after an approved/completed run so users can iterate from comments. |
| Work item modal | Implement plan | Implemented | Visible for a `PlanReady` AI run when no preview is busy/running; asks Codex to generate React/Tailwind preview source and then deploys it. |
| Work item modal | Discard plan | Implemented | Marks the plan as discarded and clears active AI state. |
| Work item modal | Rebuild with Codex | Implemented | Available for approved legacy plans whose running preview lacks persisted generated source. |
| Work item modal | Open demo environment | Implemented | Opens the preview URL only after Kubernetes health checks mark the preview `Running`. |
| Work item modal | Start preview | Implemented | Restarts a stopped preview from saved runtime data via `/api/work-items/{id}/preview/start`. |
| Work item modal | Stop preview | Implemented | Tears down preview runtime resources while keeping the card via `/api/work-items/{id}/preview/stop`. |
| Work item modal | Approve PR | Implemented | Visible when development state includes a PR URL; marks the PR as human-approved, moves the card to `Done`, and stops the preview. |
| Work item modal | No PR for local preview | Read-only | Disabled state for local preview runs that do not produce a GitHub PR. |
| Work item modal | Development tools / Simulate GitHub callback | Removed from v1 | Removed from the user flow because implementation now produces a Kubernetes-gated preview. |
| Work item modal | Preview environment | Implemented | Rendered only when API returns an actual preview and includes lifecycle status/actions. |
| Dashboard | Demo environments | Implemented | Lists real preview environments with status, URL, namespace, and start/stop actions. |
| Dashboard | Runtime history | Read-only | Lists preview/cleanup/PR lifecycle events from `/api/preview-events`. |
| Dashboard | Pipeline status | Read-only | Lists internal AI, preview, and PR pipeline state from `/api/pipelines`. |
| Settings | Back to board | Implemented | Returns to the board route. |
| Settings | AI provider/model | Implemented | Selects the provider/model used for subsequent AI-plan requests in the current UI session. |
| Settings | GitHub, AI endpoint/provider/review, and preview fields | Read-only | Shows API settings that do not yet have editable controls in v1. |
