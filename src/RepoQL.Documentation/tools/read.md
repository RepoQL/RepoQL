---
description: "read(uri, tokenBudget) → content at richest level that fits budget. Supports globs, fragments, exclusions, => question: syntax."
tags: ["read", "fetch", "content", "budget", "progressive", "globs", "fragments"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Tools[100%]"]
---

# read Tool

Fetch content with automatic representation selection based on token budget.

---

## Capsule: OutputFormat

**Invariant**
Output shows representation level chosen, alternatives with costs, then content.

**Example**
```
[Representation: structure (1247 tok) | Alternatives: full=4521 tok, headline=89 tok]

file:///src/Auth.cs
├── class AuthService
│   ├── ValidateToken(string token) → bool
│   ├── RefreshToken(string refresh) → TokenPair
│   └── RevokeToken(string token) → void
└── class TokenPair { AccessToken, RefreshToken, ExpiresAt }
```
//BOUNDARY: Header shows what you got and what you could get with different budgets.

**Depth**
- `full`: Complete file content with line numbers
- `structure`: X-ray outline (classes, methods, headings)
- `headline`: Single-line summary only
- Multi-file: Each file gets header + content block

---

## Capsule: BudgetStrategy

**Invariant**
Budget distributes across matches. Per-file allocation = total / match_count.

**Example**
```
read("file:///src/**/*.cs", 10000)  -- 100 files → 100 tok each → headlines
read("file:///src/Auth.cs", 10000)  -- 1 file → 10000 tok → full content
read("file:///src/Services/*.cs", 5000)  -- 5 files → 1000 tok each → structure
```
//BOUNDARY: Glob matches many files = less budget per file = shallower representation.

**Depth**
- 50-500: Headlines only (inventory)
- 500-2000: Structure (navigate without reading)
- 2000-10000: Full content (actual code)
- Question mode: Budget controls answer length, not input

---

## Capsule: FragmentPatterns

**Invariant**
`#line=N,M` extracts lines; `#symbol=Name` extracts symbol; wildcards `.*` and `.**` match children.

**Example**
```
read("file:///src/Auth.cs#line=50,100", 2000)         -- lines 50-100
read("file:///src/Auth.cs#symbol=AuthService", 1500)  -- just AuthService
read("file:///src/Auth.cs#symbol=AuthService.*", 3000)  -- direct members
read("file:///src/Auth.cs#symbol=AuthService.**", 5000) -- all descendants
```
//BOUNDARY: `.*` = direct children only. `.**` = all nested members.

**Depth**
- `#line=N`: Single line N
- `#line=N,M`: Lines N through M (inclusive, 1-based)
- `#symbol=Name`: Exact symbol match
- `#symbol=Name.*`: Direct children (one level)
- `#symbol=Name.**`: All descendants (any depth)
- Combine: `#line=10,50&symbol=Foo` (both constraints)

---

## Capsule: CompoundPatterns

**Invariant**
Semicolon joins patterns (OR). Exclamation excludes globally.

**Example**
```
read("file:///src/**;file:///lib/**", 8000)           -- src OR lib
read("file:///src/**/*.cs;!**/test*;!**/Test*", 5000) -- exclude tests
read("file:///src/**;!file:///src/generated/**", 6000) -- exclude generated
```
//BOUNDARY: `!` exclusions apply to ALL includes, not just adjacent.

**Depth**
- `a;b;c`: Match any of a, b, c
- `!pattern`: Exclude from all includes
- Order doesn't matter for includes
- Multiple exclusions: all applied (AND logic)

---

## Capsule: QuestionSyntax

**Invariant**
` => question: <question>` triggers LLM synthesis. Response includes citations.

**Example**
```
read("file:///src/Auth.cs => question: How does token refresh work?", 2000)
read("file:///src/**/*.cs => question: What error handling patterns are used?", 3000)
read("help:///api.md => question: List all endpoints", 1500)
```
//BOUNDARY: Budget controls answer length. Question applies to all matched files.

**Depth**
- Uses the `=> question:` modifier syntax
- LLM reads matched content, synthesizes answer
- Citations as `file:///path#line=N,M`
- Focused questions → better answers
- Broad scope + vague question → diluted results
- Requires `OPENROUTER_API_KEY` or configured LLM

---

## Capsule: SchemeSupport

**Invariant**
All indexed schemes work: `file:///`, `help:///`, `github://owner/repo`.

**Example**
```
read("file:///src/App.cs", 3000)                    -- local file
read("help:///quickstart.md", 2000)                 -- embedded docs
read("github://anthropics/claude-code@main/src/index.ts", 4000) -- imported repo
```
//BOUNDARY: Imported repos (github://) must be imported first via import tool.

---

## Capsule: VsExplore

**Invariant**
Use read when you KNOW the path. Use explore when you need to FIND it.

**Example**
```
-- Know the file → read
read("file:///src/AuthService.cs", 3000)

-- Don't know where → explore first
explore(intent="Locate", keywords="authentication service", scope="file:///src/**")
-- then read specific results
read("file:///src/Services/AuthService.cs", 3000)
```
//BOUNDARY: read with broad globs wastes budget. explore Locate → read specific is cheaper.

**Depth**
- explore Inventory: What exists?
- explore Locate: Where is X?
- read: Get content of known URI
- Workflow: Inventory → Locate → Read

---

## Capsule: Modifiers

**Invariant**
Append ` => modifier` to request a specific view of the content.

**Example**
```
read("file:///src/** => tree", 2000)           -- folder structure with files
read("file:///src/** => tree: folders", 3000)   -- folders only with file counts
read("file:///src/** => tree: headlines", 3000) -- folders + files + summaries
read("file:///src/Auth.cs => history", 1500)   -- what changed
read("file:///src/Auth.cs => blame", 2000)     -- who changed each line
read("file:///src/** => lint: errors", 1000)   -- show errors only
```
//BOUNDARY: Default is content; modifiers override progressive disclosure.

**Depth**
- `tree`: folder structure with detail levels:
  - `folders`: directory tree with file counts by type
  - `files`: folders + individual file names (default)
  - `headlines`: folders + files + one-line summaries
- `headline`: one-line summary per file
- `structure`: signatures without bodies
- `content`: full file content (explicit default)
- `history`: commits affecting file; `: keyword` filters by message/author (works on all schemes including github://)
- `blame`: git blame showing who changed each line (**file:// only** — requires local git repository)
- `lint`: diagnostics; `: errors` or `: warnings` filters severity
- `find`: semantic search within matched files; `: keywords` to search
  - `read => find` has a file-scope cap (default 96 files); broader scopes are rejected with guidance
  - This is intentional: find is for snippet extraction, not broad repo discovery
  - Use `explore(intent=Inspect, keywords="...")` first to shortlist likely files, then run `read(... => find: ...)`
- `similar`: find semantically related files; `: seed_uri` specifies what to match against
  - **The URI pattern controls WHERE to search; the seed controls WHAT to look for**
  - `file:///src/tests/** => similar: file:///src/Auth.cs` — find tests for this code
  - `file:///docs/** => similar: file:///src/Auth.cs` — find docs for this code
  - `file:///src/**/*.cs => similar: file:///docs/design.md` — find code implementing this design
  - `github://owner/repo/src/** => similar: file:///src/Logging.cs` — find similar code in another repo
  - Works across repos and across languages when there is genuine semantic overlap
  - Returns 0.00 when the seed and scope have no semantic relationship — that's signal, not failure
- `grep`: case-insensitive literal text search; `: search_text` specifies the string
  - `file:///src/** => grep: connectionString` — find every line containing the text
- `regex`: regular expression search; `: pattern` specifies the regex
  - `file:///src/**/*.cs => regex: class\s+\w+Handler` — find all Handler class declarations
- `changes`: working copy diffs grouped by changelist (staged, unstaged, untracked)
  - Shows patches for modified files, binary markers, and line counts

---

## Capsule: CrossRepoBehavior

**Invariant**
Not all modifiers work across repository boundaries. Imported repos (github://) have different capabilities than local repos (file:///).

**Example**
```
-- Works everywhere: content, structure, headline, tree, history, find, grep, regex, similar
read("github://owner/repo/src/** => structure", 3000)
read("github://owner/repo/src/** => similar: file:///src/Logging.cs", 2000)

-- Works only on file:// URIs
read("file:///src/Auth.cs => blame", 2000)
read("file:///src/** => changes", 2000)
```
//BOUNDARY: blame/changes require local git. Everything else works on all URI schemes.

**Depth**
- `blame`: Only `file:///` — requires local git repository
- `changes`: Only `file:///` — working copy is local only
- `similar`: Works across repos when content is genuinely related; returns 0.00 when it isn't
- `history`: Works on both — imported repos index git history
- `find`, `grep`, `regex`: Work on both — operate on indexed content
- `tree`, `headline`, `structure`, `content`: Work on both — use x-ray data
- Cross-language similar works within a repo (markdown ↔ code)
- SQL queries (`search()`, `Files`, `Types`) work across all repos uniformly

---

## Common Mistakes

| Mistake | Fix |
|---------|-----|
| `read("**/*.cs", 50000)` | Too broad, use explore Locate first |
| `read("file:///src/Foo.cs", 100)` | Budget too low, will get headline only |
| `read("src/Foo.cs", 3000)` | Missing scheme, use `file:///src/Foo.cs` |
| `read("file:///src/Foo.cs => question:...", 2000)` | Missing the actual question after colon |
| `read("file:///src/*.cs;!/tests/", 3000)` | Exclusion path wrong, use `!**/tests/**` |
| `=> similar: file:///path` returns 0.00 | Seed and scope aren't semantically related — that's valid signal, not a bug |
