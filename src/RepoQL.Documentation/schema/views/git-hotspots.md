---
description: "git_hotspots — files ranked by change frequency: uri, commits, authors, churn, total_insertions, total_deletions, first_changed, last_changed"
tags: ["git", "hotspots", "churn", "analysis"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# git_hotspots

All-time file change frequency — ranked by commit count, with author breadth, churn, and change window.

## Quick Reference

```sql
-- Most frequently changed files
SELECT uri, commits, authors, churn FROM git_hotspots ORDER BY commits DESC LIMIT 10;

-- Files touched by many authors (coordination overhead)
SELECT uri, commits, authors FROM git_hotspots ORDER BY authors DESC LIMIT 10;

-- Highest churn (insertions + deletions)
SELECT uri, churn, total_insertions, total_deletions FROM git_hotspots ORDER BY churn DESC LIMIT 10;
```

---

## Capsule: GitHotspots

**Invariant**
`git_hotspots` aggregates the full git history per file. Each row is one file with cumulative change stats across all commits.

**Example**
```sql
-- Active files changed recently with high commit counts
SELECT uri, commits, last_changed FROM git_hotspots
WHERE last_changed > NOW() - INTERVAL '30 days'
ORDER BY commits DESC LIMIT 10;

-- Files with many authors but low commit count (collaborative, stable)
SELECT uri, authors, commits FROM git_hotspots WHERE authors > 5 AND commits < 20;

-- Join with Files for language breakdown of hotspots
SELECT f.lang, COUNT(*) as hotspot_files, SUM(h.commits) as total_commits
FROM git_hotspots h JOIN Files f ON h.uri = f.uri
GROUP BY f.lang ORDER BY total_commits DESC;
```
//BOUNDARY: Covers all git history, not just recent commits. Deleted files may appear if they existed in history. Use `JOIN Files` to restrict to currently indexed files.

**Depth**
- `commits`: Total number of commits that touched this file — the primary hotspot signal
- `authors`: Count of distinct authors — high values indicate shared ownership
- `churn`: `total_insertions + total_deletions` — measures total edit volume
- `first_changed` / `last_changed`: Timestamps of the oldest and newest commits touching this file
- SeeAlso: `git_recent` for commits within the last 7 days

---

## Column Reference

| Column | Type | Description |
|--------|------|-------------|
| `uri` | string | File URI |
| `commits` | bigint | Total commits that touched this file |
| `authors` | bigint | Distinct author count |
| `churn` | bigint | Total lines changed (insertions + deletions) |
| `total_insertions` | bigint | Cumulative lines added |
| `total_deletions` | bigint | Cumulative lines removed |
| `first_changed` | timestamp | Earliest commit touching this file (timezone-aware) |
| `last_changed` | timestamp | Most recent commit touching this file (timezone-aware) |
