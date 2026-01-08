---
description: "read(uri, tokenBudget) → content at richest level that fits budget. Supports globs, fragments, exclusions, // question syntax."
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
` // question` triggers LLM synthesis. Response includes citations.

**Example**
```
read("file:///src/Auth.cs // How does token refresh work?", 2000)
read("file:///src/**/*.cs // What error handling patterns are used?", 3000)
read("docs:///api.md // List all endpoints", 1500)
```
//BOUNDARY: Budget controls answer length. Question applies to all matched files.

**Depth**
- Space before `//` required
- LLM reads matched content, synthesizes answer
- Citations as `file:///path#line=N,M`
- Focused questions → better answers
- Broad scope + vague question → diluted results
- Requires `OPENROUTER_API_KEY` or configured LLM

---

## Capsule: SchemeSupport

**Invariant**
All indexed schemes work: `file:///`, `docs:///`, `github://owner/repo`.

**Example**
```
read("file:///src/App.cs", 3000)                    -- local file
read("docs:///quickstart.md", 2000)                 -- embedded docs
read("github://anthropics/claude-code@main/src/index.ts", 4000) -- imported repo
```
//BOUNDARY: Imported repos (github://) must be imported first via import tool.

---

## Capsule: VsXray

**Invariant**
Use read when you KNOW the path. Use xray when you need to FIND it.

**Example**
```
-- Know the file → read
read("file:///src/AuthService.cs", 3000)

-- Don't know where → xray first
xray(intent="Find", keywords="authentication service", scope="file:///src/**")
-- then read specific results
read("file:///src/Services/AuthService.cs", 3000)
```
//BOUNDARY: read with broad globs wastes budget. xray Find → read specific is cheaper.

**Depth**
- xray Explore: What exists? (inventory)
- xray Find: Where is X? (locate)
- read: Get content of known URI
- Workflow: Explore → Find → Read

---

## Common Mistakes

| Mistake | Fix |
|---------|-----|
| `read("**/*.cs", 50000)` | Too broad, use xray Find first |
| `read("file:///src/Foo.cs", 100)` | Budget too low, will get headline only |
| `read("src/Foo.cs", 3000)` | Missing scheme, use `file:///src/Foo.cs` |
| `read("file:///src/Foo.cs//question", 2000)` | Missing space before `//` |
| `read("file:///src/*.cs;!/tests/", 3000)` | Exclusion path wrong, use `!**/tests/**` |
