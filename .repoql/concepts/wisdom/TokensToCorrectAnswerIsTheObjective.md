---
description: "RepoQL optimizes for the fewest tokens to the correct answer regardless of corpus size; speed is optimized but never traded against correctness or token-efficiency."
tags:
  - "search"
  - "ranking"
  - "design-philosophy"
  - "north-star"
  - "tokens"
  - "latency"
  - "progressive-disclosure"
  - "frankensearch"
  - "phase-gate"
  - "explore"
category: wisdom
relevance: "file:///clawdbot/**;file:///plugins/**;file:///docs/**"
---

## Capsule: TokensToCorrectAnswerIsTheObjective

**Invariant**
RepoQL optimizes for the fewest tokens to the correct answer regardless of corpus size; speed is optimized but never traded against correctness or token-efficiency.

**Why**
RepoQL's consumer is an LLM on a context budget, not a human eye on a search box. A human re-scans as results refine, so a cheap-but-wrong early result is harmless and only latency is a cost. For an agent, a cheap-wrong intermediate is doubly bad: it spends tokens AND risks the agent acting on it before any refinement lands. So "progressive first paint" (frankensearch's phase-gated early emission) is a human-perception affordance that does not transfer — it optimizes latency-to-first-result, which is the wrong cost function. The transferable version: spend COMPUTE freely and invisibly to converge (multi-stage retrieval, rerank), but emit once, minimally, only the converged answer. Compute is hideable; the caller's tokens are not. Output size decoupled from corpus size (1000 files in ~1500 tokens) is a first-class design constraint, on a different axis from frankensearch's latency-bounded-by-HNSW scaling.

**Example**
frankensearch's TwoTierSearcher emits SearchPhase::Initial (cheap embedder + BM25 + RRF) before a gated SearchPhase::Refined — great for a human watching results sharpen, wrong for an agent that pays per emission. RepoQL instead converges internally and returns one ranked answer whose richness scales with the token budget, not with elapsed time.

**Depth**
- Two kinds of "progressive": frankensearch is progressive over wall-clock time (quality climbs as ms elapse); RepoQL is progressive over token budget (same converged answer, representation deepens headline->structure->content as budget allows).
- NotThis: do not adopt phase-gated early emission / "return a cheap result, upgrade if the gate says it's worth the latency" — that bills intermediate wrong answers to the caller's context.
- Speed still matters, but as a secondary objective beneath correctness and token-efficiency, never above them.
- SeeAlso: progressive disclosure of representation in the read/explore tools is RepoQL's correct analog of "progressive".
