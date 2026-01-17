# XY Chart Reference

**Use for**: Time-series metrics, performance trends, multi-series comparisons

**Don't use for**: Conceptual data, scatter plots (not supported)

---

## Basic Syntax

```mermaid
xychart-beta
    title "Response Time by Load"
    x-axis "Users" [10, 50, 100, 200, 500]
    y-axis "ms" 0 --> 1500

    line "Sync" [45, 89, 234, 678, 1200]
    line "Async" [42, 71, 145, 298, 450]

    %% MEANING: Performance comparison
```
*Trend: Async scales better at high concurrency*

---

## Time-Series Example

```mermaid
xychart-beta
    title "24h Response Time"
    x-axis ["00:00", "04:00", "08:00", "12:00", "16:00", "20:00"]
    y-axis "ms"
    line [245, 230, 380, 420, 390, 280]
    line [250, 250, 250, 250, 250, 250]

    %% PATTERN: Second line is baseline for visual reference
```

---

## Rules

- Label axes with units
- **Quote time labels**: `["00:00"]` not `[00:00]`
- Max 4 lines per chart
- Sample 6-8 points for readability
- Use flat line for baseline comparison

---

## Configuration

```yaml
---
config:
  xyChart:
    showValues: false
    valueFormat: ","
---
```

---

## Best Practices

- Always label both axes
- Include units in axis labels
- Use consistent intervals on x-axis
- Add baseline for comparison when useful

---

## Common Mistakes

- Unquoted time labels
- Too many data points (>10)
- Missing axis labels
- Too many lines (>4)

---

**Note**: Experimental (v10.0.0+) - syntax may evolve.

---

*XY charts show TRENDS over a dimension. Use for time-series and comparisons.*
