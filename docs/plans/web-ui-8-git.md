---
description: Plan for web UI Git view - blame, history, hotspots
tags: [ui, plan, git, blame, history, hotspots]
audience: { human: 40, agent: 60 }
purpose: { plan: 100 }
---

# Plan: Web UI Git View

Implements: [Web UI Design](../designs/web-ui.md) — Git View

## Scope

**Covers:**
- Hotspots view showing most-changed files
- Blame display for files (accessible from Inspect)
- History display for files (accessible from Inspect)
- Related commits search

**Does not cover:**
- Commit graph visualization
- Branch comparison
- Working copy diff view
- Commit detail page

## Enables

Once Git view exists:
- **Hotspots visibility** — Find high-churn files (potential complexity)
- **Blame access** — See who changed each line
- **History access** — See commits affecting a file
- **Semantic + Git connection** — Find commits related to concepts

## Prerequisites

- Plan: web-ui-1-foundation complete
- Plan: web-ui-3-inspect complete (Blame/History triggered from Inspect)
- Git functions operational: `git_blame()`, `git_file_history()`, `git_hotspots`, `changes_related_to()`

## North Star

Know who changed what and when. Find files that change constantly. Connect semantic concepts to git history.

## Done Criteria

### Git View (Hotspots)
- The Git view shall be accessible via navigation (route: `/git`)
- The default sub-view shall be Hotspots

### Hotspots Sub-View
- The view shall display files ranked by change frequency
- Each file shows:
  - File path (clickable → Inspect)
  - Commit count
  - Author count
  - Last modified timestamp
  - Churn indicator (bar or badge)
- Default limit: 50 files
- "Load more" for additional files

### Hotspots Query
```sql
SELECT uri, commit_count, author_count, last_modified, churn_score
FROM git_hotspots
ORDER BY commit_count DESC
LIMIT 50;
```

### Blame Sub-View
- Blame shall be accessible from Inspect view via "Blame" button
- Navigates to `/git/blame?uri={fileUri}`
- The view shall display file content with blame annotations
- Each line shows:
  - Line number
  - Author (abbreviated)
  - Relative time (e.g., "3d ago")
  - Line content
- Lines by same author in same commit grouped visually (same background color)
- Clicking author/date shows commit detail tooltip

### Blame Query
```sql
SELECT line_number, content, author, email, commit_sha, commit_date, commit_message
FROM git_blame('{uri}');
```

### Blame Display
```
 47 │ alice │ 3d  │ public bool ValidateToken(string token)
 48 │ alice │ 3d  │ {
 49 │ bob   │ 2w  │     if (string.IsNullOrEmpty(token))
 50 │ bob   │ 2w  │         return false;
```

### History Sub-View
- History shall be accessible from Inspect view via "History" button
- Navigates to `/git/history?uri={fileUri}`
- The view shall display commits affecting the file
- Each commit shows:
  - Short SHA (clickable for detail)
  - Author
  - Relative time
  - Commit message (first line)
  - Lines added/removed

### History Query
```sql
SELECT commit_sha, author, date, message, lines_added, lines_removed
FROM git_file_history('{uri}')
ORDER BY date DESC
LIMIT 50;
```

### History Filter
- Text input to filter commits by message/author
- Filter applied client-side or via query
- "Showing commits matching '{filter}'"

### Related Commits Sub-View
- Accessible via navigation tab or from search context
- Text input for semantic query
- Results show commits whose changed files are semantically related
- Each result shows: SHA, message, files changed, relevance score

### Related Query
```sql
SELECT commit_sha, message, date, files_changed, relevance_score
FROM changes_related_to('{keywords}')
ORDER BY relevance_score DESC
LIMIT 20;
```

### Commit Detail (Tooltip/Popover)
- When commit SHA or author clicked, show detail:
  - Full SHA
  - Author name and email
  - Full date
  - Full commit message
- "View in GitHub" link if applicable

### Navigation Integration
- Blame and History accessible from Inspect view buttons
- Clicking file in Hotspots navigates to Inspect
- Back navigation returns to previous view

### Error Handling
- Not a git repo: "Git integration unavailable (not a repository)"
- File not tracked: "File is not tracked by git"
- No history: "No git history for this file"

## Constraints

- **Query-based** — All data via git_* functions
- **No commit graph** — Linear list only
- **No working copy** — Committed history only
- **50 commit limit** — Pagination for more

## References

- [Web UI Design](../designs/web-ui.md) — Git View section
- [Git Integration Flow](../flows/ui/git-integration.md) — Specifications
- [Schema.md](../Schema.md) — Git functions documentation

## Error Policy

Git errors:
1. Show error message in view area
2. "Not a git repository" or "Git not available"
3. Other views still functional

Query errors:
1. Show "Failed to load" with retry
2. Partial results shown if available

## Verification

| Scenario | How to verify |
|----------|---------------|
| Hotspots | Open Git view, verify files sorted by commit count |
| Hotspots click | Click file in hotspots, verify Inspect loads |
| Blame | From Inspect, click Blame, verify line-by-line attribution |
| Blame colors | Verify consecutive lines by same author share color |
| History | From Inspect, click History, verify commit list |
| History filter | Enter filter text, verify commits filtered |
| Related | Search semantic term, verify related commits shown |
| Commit detail | Click commit SHA, verify detail popover |
| Not tracked | Open blame for untracked file, verify error message |
