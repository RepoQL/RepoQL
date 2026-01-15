---
description: "Git history and working copy queries: status, diff, blame, file history, hotspots, and semantic change search."
tags: ["git", "git_status", "git_diff", "git_blame", "git_file_history", "git_hotspots", "git_recent", "changes_related_to", "history", "blame", "churn"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Tools[100%]"]
---

# Git Functions

Query git history, working copy status, and find commits related to semantic concepts.

**On-demand (live queries):** `git_status()`, `git_diff()`, `git_blame()`
**Indexed history:** `git_commit`, `git_file_change`, `git_recent`, `git_hotspots`, `git_file_history()`
**Semantic search:** `changes_related_to()`

---

## Capsule: GitStatus

**Invariant**
`git_status()` returns working copy status (modified, staged, untracked files) equivalent to `git status --porcelain`.

**Example**
```sql
SELECT * FROM git_status();
SELECT uri, category FROM git_status('src/**/*.cs');
SELECT uri FROM git_status() WHERE category = 'staged';
```
//BOUNDARY: Live query against working copy. Not from indexed history.

**Depth**
- `scope`: Optional glob pattern to filter results (e.g., `'src/**/*.cs'`, `'**/*.cs;!**/tests/**'`)
- `include_untracked`: Include untracked files (default TRUE)
- `include_ignored`: Include ignored files (default FALSE)
- Returns: `uri`, `index_status`, `work_tree_status`, `category`, `is_conflicted`
- Categories: `staged`, `modified`, `staged+modified`, `untracked`, `conflict`, `ignored`

---

## Capsule: GitDiff

**Invariant**
`git_diff(from_ref, to_ref)` returns file changes between two git refs (branches, commits, tags).

**Example**
```sql
SELECT * FROM git_diff('HEAD~1');
SELECT * FROM git_diff('main', 'feature-branch');
SELECT uri, change_type, insertions, deletions FROM git_diff('HEAD~5', 'HEAD', 'src/**/*.cs');
```
//BOUNDARY: Live diff computed at query time. Use indexed history for historical queries.

**Depth**
- `from_ref`: Starting ref (branch, tag, or commit SHA) - required
- `to_ref`: Ending ref (default `'HEAD'`)
- `scope`: Optional glob pattern to filter results
- Returns: `uri`, `change_type`, `old_uri`, `insertions`, `deletions`, `is_binary`
- Change types: `A` (add), `M` (modify), `D` (delete), `R` (rename), `C` (copy)

---

## Capsule: GitBlame

**Invariant**
`git_blame(scope)` returns line-by-line blame information for files matching a pattern.

**Example**
```sql
SELECT * FROM git_blame('file:///src/Foo.cs');
SELECT uri, line_number, author_name, message FROM git_blame('src/**/*.cs', 1, 50);
SELECT author_name, COUNT(*) FROM git_blame('file:///src/Core.cs') GROUP BY author_name;
```
//BOUNDARY: Live blame computed at query time. Can be slow for large files.

**Depth**
- `scope`: File URI or glob pattern (required)
- `start_line`: Optional start line filter (1-based)
- `end_line`: Optional end line filter (1-based)
- Returns: `uri`, `line_number`, `commit_hash`, `author_name`, `author_email`, `author_date`, `message`

---

## Capsule: GitFileHistory

**Invariant**
`git_file_history(uri)` returns commit history for a specific file from indexed data.

**Example**
```sql
SELECT * FROM git_file_history('file:///src/Foo.cs');
SELECT hash, author_name, message FROM git_file_history('file:///src/Core.cs') LIMIT 10;
```
//BOUNDARY: Queries indexed history (last 12 months). Includes renames.

**Depth**
- `uri`: File URI (required)
- Returns: `hash`, `author_name`, `author_email`, `author_date`, `message`, `change_type`, `old_uri`, `insertions`, `deletions`
- Tracks file across renames via `old_uri`

---

## Capsule: GitRecent

**Invariant**
`git_recent` view shows commits from the last 7 days.

**Example**
```sql
SELECT * FROM git_recent;
SELECT author_name, COUNT(*) as commits FROM git_recent GROUP BY author_name;
SELECT * FROM git_recent WHERE message LIKE '%fix%';
```
//BOUNDARY: View over indexed `git_commit` table. Updates on reindex.

**Depth**
- Returns: `hash`, `author_name`, `author_email`, `author_date`, `message`, `files_changed`, `insertions`, `deletions`
- Ordered by `author_date DESC`
- For custom time ranges, query `git_commit` directly with date filter

---

## Capsule: GitHotspots

**Invariant**
`git_hotspots` view ranks files by change frequency for churn analysis.

**Example**
```sql
SELECT * FROM git_hotspots ORDER BY commits DESC LIMIT 20;
SELECT uri, commits, churn FROM git_hotspots WHERE uri LIKE '%Service%';
SELECT * FROM git_hotspots WHERE authors > 3 ORDER BY churn DESC;
```
//BOUNDARY: High-churn files often correlate with complexity and bugs.

**Depth**
- Returns: `uri`, `commits`, `authors`, `churn`, `total_insertions`, `total_deletions`, `first_changed`, `last_changed`
- `churn` = `total_insertions + total_deletions`
- Join with `Files` view for file metadata: `SELECT h.*, f.lines FROM git_hotspots h JOIN Files f ON h.uri = f.uri`

---

## Capsule: ChangesRelatedTo

**Invariant**
`changes_related_to(keywords)` finds commits that touched files semantically related to a concept.

**Example**
```sql
SELECT * FROM changes_related_to('authentication flow', since := '14 days');
SELECT commit, message, files FROM changes_related_to('UDF implementation', since := '30 days');
SELECT * FROM changes_related_to('error handling') WHERE related_files > 3;
```
//BOUNDARY: Combines semantic search with git history. Finds conceptually related changes, not just keyword matches.

**Depth**
- `keywords`: Semantic search terms (required)
- `since`: Time filter - interval string (`'7 days'`) or timestamp
- `until`: End time filter
- `since_commit`: Start from commit hash (exclusive)
- `until_commit`: End at commit hash (inclusive, prefix match)
- `k`: Max files to consider from semantic search (default 30)
- Returns: `commit`, `hash`, `date`, `author`, `message`, `files_changed`, `related_files`, `files`, `insertions`, `deletions`
- `files` column contains semicolon-delimited URIs of semantically related files that were changed

**Use Cases**
1. "What changes might have caused this problem?" - Search for the concept you're debugging
2. "Find an example change like the one I'm planning" - Search for similar implementations
3. "Who has worked on this area?" - Check authors of related commits

---

## Capsule: GitCommitTable

**Invariant**
`git_commit` table stores indexed commit metadata (last 12 months).

**Example**
```sql
SELECT * FROM git_commit WHERE author_date > NOW() - INTERVAL '30 days';
SELECT author_email, COUNT(*) FROM git_commit GROUP BY author_email ORDER BY 2 DESC;
SELECT * FROM git_commit WHERE message LIKE '%refactor%';
```
//BOUNDARY: Raw indexed data. Use views/macros for common queries.

**Depth**
- Columns: `hash`, `author_name`, `author_email`, `author_date`, `committer_name`, `committer_email`, `committer_date`, `message`, `parent_hashes`, `files_changed`, `insertions`, `deletions`, `indexed_at`
- `parent_hashes` is an array for merge commit detection
- Indexed on startup and after reindex

---

## Capsule: GitFileChangeTable

**Invariant**
`git_file_change` table links commits to file URIs with change details.

**Example**
```sql
SELECT * FROM git_file_change WHERE uri = 'file:///src/Foo.cs';
SELECT uri, COUNT(*) as changes FROM git_file_change GROUP BY uri ORDER BY 2 DESC LIMIT 20;
SELECT * FROM git_file_change WHERE change_type = 'R';  -- Renames
```
//BOUNDARY: Join with `git_commit` for full commit context.

**Depth**
- Columns: `id`, `commit_hash`, `uri`, `change_type`, `old_uri`, `insertions`, `deletions`, `is_binary`
- `old_uri` populated for renames/copies
- Change types: `A`, `M`, `D`, `R`, `C`, `T` (type change)

---

## Common Patterns

| Goal | Query |
|------|-------|
| Working copy status | `SELECT * FROM git_status()` |
| Staged files only | `SELECT uri FROM git_status() WHERE category = 'staged'` |
| Changes in directory | `SELECT * FROM git_status('src/**/*.cs')` |
| Diff from last commit | `SELECT * FROM git_diff('HEAD~1')` |
| Diff between branches | `SELECT * FROM git_diff('main', 'feature')` |
| Blame a file | `SELECT * FROM git_blame('file:///src/Foo.cs')` |
| File history | `SELECT * FROM git_file_history('file:///src/Foo.cs')` |
| Recent commits | `SELECT * FROM git_recent` |
| Most changed files | `SELECT uri, commits, churn FROM git_hotspots ORDER BY churn DESC LIMIT 20` |
| Files with many authors | `SELECT uri, authors FROM git_hotspots WHERE authors > 3` |
| Find related changes | `SELECT * FROM changes_related_to('auth', since := '7 days')` |
| Example implementations | `SELECT commit, files FROM changes_related_to('UDF implementation')` |
| Commits by author | `SELECT * FROM git_commit WHERE author_email = 'user@example.com'` |
| Merge commits | `SELECT * FROM git_commit WHERE len(parent_hashes) > 1` |

---

## Common Mistakes

| Mistake | Fix |
|---------|-----|
| Using `git_diff()` for history | Use `git_file_history()` or `git_commit` for indexed history |
| `git_status()` for old commits | `git_status()` is live working copy only |
| Expecting real-time updates | Indexed tables update on reindex, not continuously |
| Large blame queries | Add line range filters: `git_blame(uri, 1, 100)` |
| `changes_related_to` with no results | Increase `k` parameter or broaden keywords |
| Missing commits in history | Index covers 12 months; older commits not available |
