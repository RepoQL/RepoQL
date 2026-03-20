# Creation Notes

## Source

All three traps were discovered during a single performance investigation session (2026-03-21). The user reported search() taking 4.8 minutes, which led to a lexical search rewrite (previous session) and then semantic search optimization (this session).

## Discovery Process

The traps were found by progressive elimination:
1. VSS/HNSW benchmark proved linear cosine scan is fast (30ms for 55K vectors) — the bottleneck wasn't where expected
2. Manual CTE reproduction showed 800ms vs 4.4s macro — confirmed macro-specific overhead
3. Progressive build-up (adding one CTE at a time) isolated QUALIFY with raw macro param (18s → 1s)
4. Temp macro vs deployed macro comparison isolated the cast-at-use-site issue (4.4s → 0.8s)
5. Multi-ref CTE was discovered analyzing the query plan structure

## What Worked in Authoring

- The three capsules map directly to the three discrete mechanisms found empirically
- Including exact timing data (4.4s → 0.8s) makes the severity visceral
- The code examples are minimal diffs showing the exact change

## What Could Improve

- The patterns file may accumulate stale timing data as the codebase evolves
- Need a way to validate these traps still exist as DuckDB versions change (the optimizer may improve)
- The diagnosis workflow was written from memory of the actual debugging session — a real second test would validate it
