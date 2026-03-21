# Globbing Plans

Implementation plans for line-range-based globbing, enabling precise pattern matching with exclusions.

## Overview

These plans implement the [Line-Range Globbing Design](../../designs/future/globbing.md) to fulfill the [Globbing North Star](../../north-star/globbing.md).

## Dependency Order

```
┌─────────────────────┐
│  01-registry-model  │  Foundation: SymbolEntry, FileEntry, LineRange
└──────────┬──────────┘
           │
     ┌─────┴─────┐
     ▼           ▼
┌─────────┐ ┌─────────────────────┐
│ 02-     │ │ 03-line-range-      │
│indexing │ │ calculator          │  Can proceed in parallel
│         │ │                     │
└────┬────┘ └──────────┬──────────┘
     │                 │
     └────────┬────────┘
              ▼
┌─────────────────────────┐
│  04-pattern-matching    │  Core algorithm
└────────────┬────────────┘
             │
             ▼
┌─────────────────────────┐
│  05-sql-surface         │  Expose via UDF
└─────────────────────────┘
```

## Plans

| # | Plan | What it delivers |
|---|------|------------------|
| 01 | [Registry Model](01-registry-model.md) | `SymbolEntry` with spans, updated `FileEntry`, `LineRange` struct |
| 02 | [Indexing Integration](02-indexing-integration.md) | Populate symbol spans during indexing |
| 03 | [Line Range Calculator](03-line-range-calculator.md) | Union and subtract operations on line ranges |
| 04 | [Pattern Matching](04-pattern-matching.md) | Core algorithm: expand → union → subtract → simplify |
| 05 | [SQL Surface](05-sql-surface.md) | `glob_files` UDF using registry |

## Execution Strategy

**Phase 1: Foundation (01)**
- Update data structures
- Maintain backward compatibility

**Phase 2: Parallel work (02 + 03)**
- Indexing integration can proceed once model is ready
- Line range calculator is pure algorithm, no dependencies on indexing

**Phase 3: Integration (04)**
- Bring together registry data and calculator
- Implement full pattern matching flow

**Phase 4: Exposure (05)**
- Wire up SQL surface
- Verify end-to-end

## Success Criteria

When complete:
```sql
-- This pattern works and returns correct results
SELECT * FROM glob_files('src/**/*.cs#symbol=*;!#line=1,30');

-- Symbols in first 30 lines excluded
-- Symbols partially in range return as line ranges
-- Symbols fully outside range return as symbol URIs
```

## Related

- [Globbing North Star](../../north-star/globbing.md) — what great looks like
- [Pattern Matching Flow](../../flows/future/globbing/pattern-matching.md) — how it works
- [Line-Range Globbing Design](../../designs/future/globbing.md) — architecture
