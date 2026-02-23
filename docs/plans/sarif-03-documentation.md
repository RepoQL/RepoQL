---
description: Plan for SARIF import help:// documentation — embedded docs for agents and humans
tags: [sarif, documentation, help, plan]
audience: { human: 50, agent: 50 }
purpose: { plan: 95, design: 5 }
---

# Plan: SARIF Import Documentation

Implements: [SARIF Import Design](../designs/future/sarif-import.md) — self-documenting promise, help:// docs

## Scope

**Covers:**
- Embedded documentation at `src/RepoQL.Documentation/repoql/tools/import/sarif.md`
- Queryable via `read("help:///tools/import/sarif.md", 2000)`
- Content: supported producers, `sarif://` URI scheme, import/re-import behavior, semantic key format, example queries after import, error messages, limitations
- YAML frontmatter following existing help:// doc conventions
- Capsule structure following existing view documentation patterns

**Does not cover:**
- Any code changes (completed in Plans 1 and 2)
- Tutorial-style walkthroughs (the feature should be self-evident)

## Enables

- Agents can discover SARIF import capabilities via `explore(uriGlob="help://**", keywords="sarif")`
- Agents have a reference for query patterns after importing SARIF
- Self-documenting promise upheld: the feature exists in `help://`

## Prerequisites

- Plan: sarif-02-import-service (the feature must work before we document it)

## North Star

An agent that has never used SARIF import can discover it via `help://` and use it correctly on the first attempt. The doc answers: "How do I import?", "What happens to old findings?", "How do I query results?", "What producers are supported?"

## Done Criteria

### File Structure

- The file shall live at `src/RepoQL.Documentation/repoql/tools/import/sarif.md`
- The file shall have YAML frontmatter with `description`, `tags` (sarif, import, annotations, lint, static-analysis), `audience`, and `categories`
- The file shall be automatically embedded as a resource (existing `EmbeddedResource Include="**/*.md"` in the .csproj handles this)

### Content

- The file shall open with a one-line description and a Quick Reference section showing the import command and 2-3 post-import queries
- The file shall explain the `sarif://` URI scheme: `sarif:///absolute/path.sarif` and `sarif:///./relative/path.sarif`
- The file shall explain re-import behavior: stale findings expire (source-wide), unchanged findings preserved, new findings added
- The file shall list supported producers with their source slugs (snyk-code, snyk-oss, qodana-jvm, qodana-js, qodana-dotnet, qodana-python, qodana-go, qodana-php, codeql, semgrep, eslint, roslyn, trivy, sonarqube) and note that unknown producers are auto-slugified
- The file shall show example SQL queries for: severity summary by source, error-level findings, findings for a specific file, policy gate
- The file shall document error scenarios: file not found, invalid JSON, unresolved paths
- The file shall document that results without `ruleId` are skipped (optional per SARIF spec, required by RepoQL for semantic key stability) and that this is reported in the import summary
- The file shall document that `kind = 'lint'` and the severity mapping (SARIF error→error, warning→warning, note→info, none→hint)
- The file shall not document aspirational features (partial scan support, custom source override) — only what Plan 2 delivers

### Capsule Structure

- The file shall use the Knowledge Capsule pattern with Invariant/Example/Depth sections
- At minimum: capsules for Import Behavior, Re-Import Lifecycle, Query Patterns, and Supported Producers
- Each capsule shall have a `//BOUNDARY:` line stating what agents must not assume

## Constraints

- **Document only what exists** — no aspirational content about future extensions
- **Follow existing patterns** — use the capsule structure from `src/RepoQL.Documentation/repoql/tools/query/views/annotations.md`
- **Token-efficient** — agent audience means structure over prose, tables over paragraphs

## References

- Existing doc pattern: `src/RepoQL.Documentation/repoql/tools/query/views/annotations.md` — capsule structure
- [SARIF Import North Star](../north-star/sarif-import.md) — query examples to include
- [SARIF Query Patterns Flow](../flows/future/sarif/sarif-query-patterns.md) — agent consumption patterns
- [SARIF Producer Landscape](../research/sarif-producer-landscape.md) — supported producer list and quirks
