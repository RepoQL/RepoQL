---
description: Plan index for intelligent context selection — 5 increments from focused snippets to three-level allocation
tags: [explore, search, plans, intelligent-context]
audience: { human: 70, agent: 30 }
purpose: { plan: 80, gestalt: 20 }
---

# Intelligent Context Selection — Plans

Implements: [Intelligent Context Design](../../designs/future/intelligent-context.md)

## Plans

| # | Plan | Scope | Depends On |
|---|------|-------|------------|
| 1 | [Focused Snippets](01-focused-snippets.md) | Chunk propagation, Focused representation, estimation, rendering | None |
| 2 | [Query Expansion](02-query-expansion.md) | Abbreviation dictionary, dual search, RRF fusion | None |
| 3 | [SimHash Dedup](03-simhash-dedup.md) | SimHash indexing, artifact column, duplicate detection, output | None |
| 4 | [Clustered Output](04-clustered-output.md) | Result clustering, labeling, cluster rendering | None (enhanced by 3) |
| 5 | [Three-Level Allocation](05-three-level-allocation.md) | Cluster-aware allocation, Focused in allocation, duplicate demotion | 1, 4 |

Each plan is independently deployable except Plan 5, which requires Plan 1 (Focused representation) and Plan 4 (clusters as input).

## Order

Build in order 1 → 5. Plans 1-3 are independent and could run in parallel. Plan 4 benefits from Plan 3 (duplicate clusters) but works without it. Plan 5 ties everything together.

Delete each plan when its increment is complete.
