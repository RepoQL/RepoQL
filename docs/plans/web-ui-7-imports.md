---
description: Plan for web UI Imports view - external repository management
tags: [ui, plan, imports, github, external]
audience: { human: 40, agent: 60 }
purpose: { plan: 100 }
---

# Plan: Web UI Imports View

Implements: [Web UI Design](../designs/web-ui.md) — Imports View

## Scope

**Covers:**
- Imports view listing external repositories
- Import status display (ready, indexing, error)
- Add new import with progress streaming
- Remove import with confirmation
- Reindex existing import

**Does not cover:**
- Search scope selection per import (stretch goal)
- Scheduled refresh (not in design)
- Multi-branch import (not in design)

## Enables

Once Imports view exists:
- **Import visibility** — See what external repos are indexed
- **Import management** — Add, remove, reindex without CLI
- **Progress tracking** — Watch import progress in real-time
- **Error visibility** — See why imports failed

## Prerequisites

- Plan: web-ui-1-foundation complete
- gRPC `ImportRepository` streaming method operational
- `Filesystems` view exists in RepoQL schema

## North Star

See all imports and their status. Add a new import, watch progress. Remove stale imports. Never wonder "what external code is in my index?"

## Done Criteria

### Imports View
- The Imports view shall be accessible via navigation (route: `/imports`)
- The view shall display list of imported repositories
- The view shall display "Add Import" button

### Import List
- Each import shall display:
  - URI (e.g., `github://anthropics/claude-code`)
  - Status indicator: ✓ Ready, ⟳ Indexing, ⚠ Error
  - File count (when ready)
  - Last indexed timestamp (when ready)
  - Error message (when error)
- Imports sorted by URI

### Import List Query
```sql
SELECT uri, source_type, status, file_count, last_indexed, error_message
FROM Filesystems
WHERE source_type != 'local'
ORDER BY uri;
```

### Status Indicators
- Ready (✓ green): Fully indexed, searchable
- Indexing (⟳ yellow, animated): Import in progress
- Error (⚠ red): Failed, shows error message
- Pending (◔ gray): Queued, not started

### Import Actions
- Ready imports show: [Reindex] [Remove] [Browse]
- Indexing imports show: [Cancel]
- Error imports show: [Retry] [Remove]

### Add Import
- "Add Import" button opens inline form (not modal)
- Form fields:
  - Repository URI (text input, placeholder: `github://owner/repo`)
  - Branch/Ref (text input, optional, default: main)
- "Start Import" button initiates import
- Validation: URI must match `github://owner/repo` pattern

### Import Progress
- When import starts, entry appears in list with Indexing status
- Progress bar shows: `{completed} / {total} files`
- Current stage shown: Discovery, Indexing, SemanticIndexing, Analysis
- Progress updates via gRPC streaming

### Progress Streaming
- The view shall subscribe to `ImportRepository` gRPC stream
- Progress events update the UI in real-time
- When stream completes, status changes to Ready
- When stream errors, status changes to Error with message

### Remove Import
- "Remove" button shows confirmation
- Confirmation text: "Remove {uri}? This will delete {n} files from the index."
- Confirm/Cancel buttons
- On confirm: Execute removal, refresh list

### Reindex Import
- "Reindex" button triggers reimport
- Clears existing data and re-imports from source
- Shows progress same as new import

### Browse Action
- "Browse" button navigates to Inspect view with import scope
- Shows file tree for that import only (stretch: may link to Explorer)

### Error Display
- Error imports show message inline
- Full error shown on hover or click

### Empty State
- When no imports: "No external repositories imported"
- Shows "Add Import" button prominently

## Constraints

- **No bulk operations** — Add/remove one at a time
- **No authentication setup** — Assumes `GITHUB_TOKEN` or `gh auth` configured
- **No private repo indicator** — All repos treated same

## References

- [Web UI Design](../designs/web-ui.md) — Imports View section
- [Imports Management Flow](../flows/ui/imports-management.md) — Specifications
- [Import Tool Documentation](help:///repoql/tools/import/) — Import syntax

## Error Policy

Import errors:
1. Show error message on the import entry
2. Status changes to Error
3. Retry available

List query errors:
1. Show "Failed to load imports" with retry button
2. Add Import still available

Connection errors:
1. Progress stream disconnects
2. Import status unknown
3. Refresh list to see actual status

## Verification

| Scenario | How to verify |
|----------|---------------|
| List imports | Import a repo via CLI, open view, verify it appears |
| Status ready | Completed import shows ✓ and file count |
| Add import | Click Add, enter URI, verify progress appears |
| Progress | Watch progress bar update during import |
| Cancel | Start import, click Cancel, verify it stops |
| Remove | Click Remove, confirm, verify import disappears |
| Reindex | Click Reindex on existing, verify progress |
| Error | Import invalid repo, verify error message shown |
| Retry | After error, click Retry, verify reimport starts |
