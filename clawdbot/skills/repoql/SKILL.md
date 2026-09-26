---
name: repoql
description: "Orientation for the RepoQL tools (repoql_*): when to reach for them instead of raw file reads, the URI grammar every tool shares, and the explore → read workflow. Use when starting work in a repository, before grepping or opening files to understand code, or when unsure which repoql_ tool fits."
---

# RepoQL in OpenClaw

RepoQL is a pre-built structural index of the workspace: every file, symbol, and relationship parsed and summarized. You can survey a thousand files in about 1,500 tokens, where grep and file reads cost dozens of calls and tens of thousands of tokens. A bad call costs little and a good one saves a lot, so experiment freely.

Each `repoql_<tool>` is the RepoQL host's own tool, and its description is the full reference. This page is the orientation that ties them together.

## Reach for RepoQL first when

- you need to know what exists, or where something lives, before opening files;
- you would otherwise grep for a name you are guessing;
- the question is about relationships (what calls this, what depends on that) or history (who changed this span, and why);
- you want only a slice — one method body, a line range, signatures across a whole directory.

Use your ordinary file tools to edit. RepoQL reads and answers; it never writes source.

## Everything is addressable

- **Schemes:** `file:///` (this workspace) · `github://owner/repo` (imports) · `help:///` (RepoQL's own docs) · `concept:///` and `vocabulary:///` (repository memory)
- **Globs:** `*` `**` `?` `{a,b}` `[a-z]` · combine with `;` · exclude with `!**/tests/**`
- **Fragments:** `#symbol=Name` · `#symbol=Class.*` (members) · `#symbol=*Service` · `#line=42,60`
- **Modifiers:** append ` => structure`, ` => tree: headlines`, ` => find: keywords`, ` => history`, ` => blame`, and more to any `repoql_read`

They compose in one call: `file:///src/**/*.ts#symbol=*Client => structure`.

## The workflow

1. **Name the thing.** On unfamiliar ground, `repoql_discover_vocabulary` turns your words into the repository's words. Plausible search results can hide the local names you lack.
2. **Survey.** `repoql_explore` with `uriGlob`, `keywords`, and a `question`. It ranks by how well each match answers the question and teaches you the real names for everything after.
3. **Read precisely.** `repoql_read` on the symbol or span you need. Try ` => structure` before bodies.
4. **Count, join, traverse.** `repoql_query` runs SQL over the whole graph. `DESCRIBE SELECT * FROM <view> LIMIT 0` shows any view's columns, and the host's error messages suggest the right name when you guess wrong.
5. **Delegate understanding.** `repoql_explain` reads widely and returns a cited answer. Scope it with `uriGlob`.

The token budget is a ceiling, not a target. Start small and widen when a result is too thin.

## When a capability seems missing

Search `help:///` before concluding it does not exist: `repoql_explore` with `uriGlob: "help:///**"`. For more:

- **effective-repoql** — the techniques and composition patterns, loaded from the installed rql.
- **troubleshooting-repoql** — when a tool errors, results look wrong, or the host will not respond. Start with `repoql_status`.
- **monitoring-repoql** — waiting for indexing or an import without polling.
- **using-uplinks** — shared, remotely hosted indexes.
