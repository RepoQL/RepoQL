---
description: "ASOF joins for fuzzy temporal matching, time bucketing for aggregation"
tags: ["AsofJoin", "TimeBucket", "EventCorrelation", "TemporalAnalysis"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Patterns[100%]"]
---

# Temporal Join Patterns

## Capsule: AsofJoin

**Invariant**
Match each row to the closest prior row by time, not exact equality.

**Example**
```sql
SELECT * FROM events e
ASOF JOIN prices p ON e.ts >= p.ts AND e.symbol = p.symbol
```

**Depth**
- Matches latest record not after the left timestamp
- Requires >= or <= on temporal column
- Other conditions must be equalities
- LEFT ASOF keeps unmatched rows with NULL
- SeeAlso: LagLead

---

## Capsule: TimeBucket

**Invariant**
Truncate timestamps to fixed intervals for aggregation.

**Example**
```sql
SELECT time_bucket(INTERVAL '1 hour', mtime) as hour, COUNT(*)
FROM Files GROUP BY 1
```

**Depth**
- time_bucket: Arbitrary intervals (15 min, 4 hours)
- date_trunc: Standard units (day, week, month)
- Both return timestamp truncated to bucket start
- SeeAlso: AsofJoin

---

## Capsule: EventCorrelation

**Invariant**
Combine window functions with time for sequence analysis.

**Example**
```sql
SELECT uri, mtime, mtime - LAG(mtime) OVER (ORDER BY mtime) as gap
FROM Files
```

**Depth**
- LAG for time deltas within partition
- CASE + SUM window for sessionization
- date_diff for explicit duration calculation
- SeeAlso: LagLead, AsofJoin

---

# Checklist

- [ ] Invariant first, one idea, timeless, ≤30 tokens
- [ ] Example concise, ≤5 lines
- [ ] Depth clarifies with bullets; no history or vendors
