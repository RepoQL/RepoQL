---
description: Plan for web UI foundation - status streaming, navigation, shell
tags: [ui, plan, foundation, status, blazor]
audience: { human: 40, agent: 60 }
purpose: { plan: 100 }
---

# Plan: Web UI Foundation + Status View

Implements: [Web UI Design](../designs/web-ui.md) — Components, StatusStore, NavigationState, Status View

## Scope

**Covers:**
- Blazor Server project setup and configuration
- gRPC connection to RepoQL host via Unix socket
- `IStatusStore` and implementation
- `StatusStreamService` background service
- `INavigationState` and implementation
- Shell layout (status bar, navigation, content area)
- Status view with pipeline display and problem cards

**Does not cover:**
- Query view (Plan: web-ui-2-query)
- Inspect view (Plan: web-ui-3-inspect)
- Search view (Plan: web-ui-4-search)
- Read view (Plan: web-ui-5-read)
- Annotations view (Plan: web-ui-6-annotations)
- Imports view (Plan: web-ui-7-imports)
- Git view (Plan: web-ui-8-git)

## Enables

Once Foundation exists:
- **"Is it working?"** — User opens browser, sees green/red indicator immediately
- **All subsequent plans** — Every view depends on StatusStore and NavigationState
- **Real-time updates validated** — gRPC streaming → StatusStore → SignalR push architecture proven
- **Problem surfacing** — Diagnosis flow functional via problem cards

This is the foundation. All downstream plans depend on it.

## Prerequisites

- RepoQL host running with gRPC `WatchStatus` stream operational
- `RepoQL.Protocol` project with gRPC client contracts
- .NET 10 SDK installed

## North Star

Open browser, see health status within 500ms. Connection loss visible within 2 seconds. Reconnection automatic. Problems surface without clicking anything.

## Done Criteria

### Project Setup
- The project shall be a Blazor Server application targeting .NET 10
- The project shall reference `RepoQL.Protocol` for gRPC contracts
- The project shall configure gRPC channel for Unix socket (or named pipe on Windows)

### StatusStore
- The StatusStore shall expose current `HostStatus` (Online/Offline/Reconnecting, message, timestamp)
- The StatusStore shall expose current `PipelineStatus` (stages, queue depths, busy flags)
- The StatusStore shall expose recent `HealthEvent` list (last 10 events)
- The StatusStore shall expose current `StatsSnapshot` (file count, node count, etc.)
- The StatusStore shall fire `OnChange` event when any state changes
- When StatusStore updates, subscribed components shall re-render

### StatusStreamService
- The StatusStreamService shall be a `BackgroundService` starting on application startup
- The StatusStreamService shall call `WatchStatus` gRPC streaming RPC
- The StatusStreamService shall dispatch received events to StatusStore
  - `PipelineStatusEvent` → update pipeline state
  - `HealthEvent` → add to health history, update connection state
  - `StatsSnapshotEvent` → update stats
- When stream disconnects, the service shall set StatusStore to Offline state
- When stream disconnects, the service shall wait 5 seconds then reconnect
- When reconnection succeeds, the service shall set StatusStore to Online state

### NavigationState
- The NavigationState shall track current view and parameters
- The NavigationState shall maintain history stack (10 entries max)
- The NavigationState shall expose `CanGoBack` property
- When `NavigateTo` called, the state shall push current to history and update current
- When `GoBack` called, the state shall pop from history and update current
- The NavigationState shall fire `OnChange` event on navigation

### Shell Layout
- The shell shall display a status bar at top showing connection state
  - Online: green indicator, "Online" or "Idle" or "Processing: {stages}"
  - Offline: red indicator, "Offline: {message}"
  - Reconnecting: yellow pulsing indicator, "Reconnecting..."
- The shell shall display navigation menu with links to all views
- The shell shall display content area that renders current view
- When NavigationState changes, the shell shall render the new view

### Status View
- The Status view shall be the default/landing view (route: `/`)
- The Status view shall display pipeline visualization
  - Hot path stages with busy/idle indicators
  - Queue depth progress bars when non-zero
  - Idle processing phase when active
- The Status view shall display health panel
  - "All systems healthy" when no issues
  - Problem cards when issues detected
- The Status view shall display quick stats (files, nodes, edges, annotations)

### Problem Cards (Diagnosis)
- When a queue item has been processing > 30 seconds, a "Stuck File" card shall appear
  - Card shows: file URI, stage, duration
  - Card shows: "Skip" and "Inspect" actions
- When `last_error` in diagnostics is non-empty, an "Error" card shall appear
  - Card shows: error message
- When connection is lost, a "Connection Lost" card shall appear
  - Card shows: last error message, "Reconnect" action
- When embeddings haven't progressed for > 5 minutes, a "Stalled" card shall appear
  - Card shows: pending count, last progress time

### Keyboard Shortcuts
- `Alt+←` shall trigger GoBack navigation when CanGoBack is true

## Constraints

- **Blazor Server only** — Design chose this for direct gRPC access; no WebAssembly
- **No component library** — Design specifies vanilla HTML + CSS
- **Session-only state** — No localStorage, no cookies, no persistence beyond session
- **Unix socket / named pipe** — Must use same transport as CLI; no HTTP fallback

## References

- [Web UI Design](../designs/web-ui.md) — Component diagram, contracts, data flow
- [Status Streaming Flow](../flows/ui/status-streaming.md) — Detailed stage descriptions
- [Diagnosis Flow](../flows/ui/diagnosis.md) — Problem card specifications
- [Grpc.Net.Client](https://www.nuget.org/packages/Grpc.Net.Client) — gRPC client package
- `RepoQL.Protocol` project — gRPC contracts and `RepoQlClient`

## Error Policy

Connection errors are expected and handled:
1. Set StatusStore to Offline with error message
2. Display offline indicator in status bar
3. Show "Connection Lost" problem card
4. Auto-reconnect after 5 second delay
5. On successful reconnect, clear error state

Service exceptions during event processing:
1. Log warning with exception details
2. Continue processing stream (don't crash the service)
3. If repeated failures, surface in health panel

## Verification

| Scenario | How to verify |
|----------|---------------|
| Startup | Open browser, verify green indicator within 500ms |
| Offline | Stop RepoQL host, verify red indicator within 2s |
| Reconnect | Restart host, verify green indicator returns |
| Pipeline busy | Trigger reindex, verify stages show busy |
| Problem card | Add huge file to trigger stuck, verify card appears |
| Navigation | Click nav links, verify views change |
| Back | Navigate twice, click back, verify previous view |
