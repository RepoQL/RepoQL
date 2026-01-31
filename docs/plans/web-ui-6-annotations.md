---
description: Plan for web UI Annotations view - repo-wide diagnostics browsing
tags: [ui, plan, annotations, lint, diagnostics]
audience: { human: 40, agent: 60 }
purpose: { plan: 100 }
---

# Plan: Web UI Annotations View

Implements: [Web UI Design](../designs/web-ui.md) — Annotations View

## Scope

**Covers:**
- Annotations view showing all diagnostics across repository
- Filtering by severity, rule, file pattern
- Grouping by file, rule, or flat list
- Click-through to Inspect view
- Summary statistics

**Does not cover:**
- Trend over time (requires historical data)
- Suppression/ignore functionality
- Bulk fix actions
- Export to CSV (stretch goal)

## Enables

Once Annotations view exists:
- **Repo-wide visibility** — See all errors/warnings at a glance
- **Pattern detection** — Which rule fires most? Which files have most issues?
- **Click-through debugging** — Jump to any annotation in context
- **Quality tracking** — Understand overall codebase health

## Prerequisites

- Plan: web-ui-1-foundation complete
- Plan: web-ui-3-inspect complete (for click-through)
- `Annotations` view exists in RepoQL schema

## North Star

See all problems across the codebase in one place. Filter to what matters. Click to jump to the source.

## Done Criteria

### Annotations View
- The Annotations view shall be accessible via navigation (route: `/annotations`)
- The view shall display summary counts: Errors, Warnings, Info, Hints

### Summary Display
- Summary bar shows: "Errors: {n} | Warnings: {n} | Info: {n} | Hints: {n}"
- Counts update when filters applied
- Clicking a count sets filter to that severity

### Filtering
- The view shall provide filter controls:
  - Severity dropdown: All, Error, Warning, Info, Hint
  - Rule dropdown: All, or list of rule IDs found in repo
  - File pattern input: glob pattern (e.g., `src/**/*.cs`)
- Filters apply immediately on change
- Active filters shown as removable chips

### Grouping
- The view shall provide grouping options (radio buttons):
  - By File — annotations grouped under file headers
  - By Rule — annotations grouped under rule headers
  - None — flat list sorted by severity then file
- Default grouping: By Rule

### Annotation List Display

**By File grouping:**
```
src/Auth/AuthService.cs (5 issues)
  ├─ ✕ CS0168: Variable declared but never used [line 23]
  ├─ ⚠ CA1822: Consider making static [line 112]
  └─ ⚠ CA1822: Consider making static [line 156]

src/Data/UserRepository.cs (2 issues)
  └─ ...
```

**By Rule grouping:**
```
CA1822: Consider making method static (23)
  ├─ src/Auth/AuthService.cs:112
  ├─ src/Auth/AuthService.cs:156
  └─ [+21 more]

CS0168: Variable declared but never used (8)
  └─ ...
```

**Flat list:**
```
✕ src/Auth/AuthService.cs:23 — CS0168: Variable declared but never used
⚠ src/Auth/AuthService.cs:112 — CA1822: Consider making static
⚠ src/Auth/AuthService.cs:156 — CA1822: Consider making static
...
```

### Severity Icons
- Error: ✕ (red)
- Warning: ⚠ (yellow)
- Info: ℹ (blue)
- Hint: 💡 (gray)

### Click-Through
- File paths/names shall be clickable
- Clicking navigates to Inspect view with file URI
- If line number available, NavigationParams includes line

### Pagination
- Default page size: 100 annotations
- "Load more" button or infinite scroll
- Total count always visible

### Collapse/Expand
- Groups (file or rule) shall be collapsible
- Large groups (>10 items) start collapsed with "+N more" indicator
- Click to expand

### Loading State
- Show skeleton while loading
- Summary counts load first
- Annotation list loads after

## Constraints

- **No historical comparison** — Current snapshot only
- **No suppression** — Can't mark annotations as ignored
- **Query-based** — Uses `Annotations` view via QueryService

## References

- [Web UI Design](../designs/web-ui.md) — Annotations View section
- [Annotations Browsing Flow](../flows/ui/annotations-browsing.md) — Specifications
- [Schema.md](../Schema.md) — `Annotations` view columns

## Error Policy

Query errors:
1. Show error message with retry button
2. Summary shows "—" for counts
3. List shows "Failed to load annotations"

Empty results:
1. "No annotations match filters" if filters active
2. "No annotations in repository" if no filters

## Verification

| Scenario | How to verify |
|----------|---------------|
| Summary counts | Open view, verify counts match query result |
| Filter severity | Select "Error", verify only errors shown |
| Filter rule | Select specific rule, verify only that rule shown |
| Filter pattern | Enter `src/Auth/**`, verify only Auth files shown |
| Group by file | Select "By File", verify grouping correct |
| Group by rule | Select "By Rule", verify grouping correct |
| Flat list | Select "None", verify sorted flat list |
| Click-through | Click a file path, verify Inspect view loads |
| Pagination | Repo with >100 annotations, verify "Load more" works |
| Collapse | Expand/collapse groups, verify behavior |
