---
description: Index of web UI implementation plans
tags: [ui, plan, index]
audience: { human: 60, agent: 40 }
purpose: { reference: 70, plan: 30 }
---

# Web UI Implementation Plans

Plans for implementing the RepoQL web UI. Each plan is reviewed by humans and implemented by agents.

**Design**: [docs/designs/web-ui.md](../designs/web-ui.md)
**North Star**: [docs/north-star/web-ui.md](../north-star/web-ui.md)
**Flows**: [docs/flows/ui/](../flows/ui/)

## Build Order

Plans are ordered by dependency. Each plan enables downstream work.

| # | Plan | Scope | Depends On |
|---|------|-------|------------|
| 1 | [Foundation + Status](web-ui-1-foundation.md) | StatusStore, Navigation, Shell, Status View | — |
| 2 | [Query](web-ui-2-query.md) | SQL execution, results grid | Foundation |
| 3 | [Inspect](web-ui-3-inspect.md) | File details, nodes, edges, traversal | Foundation |
| 4 | [Search](web-ui-4-search.md) | Explore testing, score breakdown | Foundation, Inspect |
| 5 | [Read](web-ui-5-read.md) | Read tool testing, progressive disclosure | Foundation |
| 6 | [Annotations](web-ui-6-annotations.md) | Repo-wide diagnostics | Foundation, Inspect |
| 7 | [Imports](web-ui-7-imports.md) | External repository management | Foundation |
| 8 | [Git](web-ui-8-git.md) | Blame, history, hotspots | Foundation, Inspect |

## Dependency Graph

```
Plan 1: Foundation + Status
    │
    ├───► Plan 2: Query
    │
    ├───► Plan 3: Inspect ◄─────────┐
    │         │                     │
    │         ├───► Plan 4: Search ─┘
    │         │
    │         ├───► Plan 6: Annotations
    │         │
    │         └───► Plan 8: Git
    │
    ├───► Plan 5: Read
    │
    └───► Plan 7: Imports
```

## Plan Status

| Plan | Status |
|------|--------|
| Foundation + Status | Complete |
| Query | Not started |
| Inspect | Not started |
| Search | Not started |
| Read | Not started |
| Annotations | Not started |
| Imports | Not started |
| Git | Not started |

*Update this table as plans are completed. Delete completed plans per lifecycle guidance.*

## What Each Plan Enables

| After Plan | You Can |
|------------|---------|
| 1. Foundation | See if RepoQL is working |
| 2. Query | Run SQL, verify macros work |
| 3. Inspect | See what RepoQL extracted from files |
| 4. Search | Test explore, understand ranking |
| 5. Read | Test read tool, see progressive disclosure |
| 6. Annotations | See all errors across repo |
| 7. Imports | Manage external repositories |
| 8. Git | See blame, history, hotspots |

## Key References

- [Web UI Design](../designs/web-ui.md) — Architecture, contracts, components
- [Status Streaming Flow](../flows/ui/status-streaming.md) — Real-time updates
- [File Inspection Flow](../flows/ui/file-inspection.md) — File details
- [Search Testing Flow](../flows/ui/search-testing.md) — Score breakdown
- [RepoQL.Protocol](../../src/RepoQL.Protocol/) — gRPC contracts
