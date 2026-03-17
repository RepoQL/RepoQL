---
description: "explore(intent, keywords, scope, tokenBudget) → token-budgeted exploration. Intents: Explore, Find, Examine, Understand."
tags: ["explore", "exploration", "discovery", "search", "understand"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Tools[100%]"]
---

# Explore Tool

Token-budgeted codebase exploration. Choose intent based on your knowledge state.

---

## Capsule: IntentSelection

**Invariant**
Choose intent based on what you know: Explore (nothing), Find (concept), Examine (file), Understand (question).

**Example**
```
-- Don't know what exists
explore intent=Explore scope="file:///src/**" tokenBudget=1500

-- Know concept, need location
explore intent=Find keywords="authentication" tokenBudget=1500

-- Know file, need details
explore intent=Examine scope="file:///src/Auth.cs" tokenBudget=3000

-- Have question, want synthesis
explore intent=Understand keywords="How does JWT validation work?" tokenBudget=2500
```
//BOUNDARY: Intent determines output format. Wrong intent = wasted tokens.

**Depth**
| Intent | Knowledge State | Output |
|--------|-----------------|--------|
| `Explore` | Don't know what exists | Inventory of files, structure overview |
| `Find` | Know concept, need location | Ranked results with snippets |
| `Examine` | Know location, need details | Deep structure with line numbers |
| `Understand` | Have question | LLM-synthesized answer with citations |

---

## Capsule: TokenBudget

**Invariant**
`tokenBudget` controls output size. More tokens = richer detail, not more results.

**Example**
```
-- Quick inventory
explore intent=Explore tokenBudget=800

-- Moderate exploration
explore intent=Find keywords="config" tokenBudget=1500

-- Deep examination
explore intent=Examine scope="file:///src/Auth.cs" tokenBudget=4000
```
//BOUNDARY: Budget is spend target, not maximum. explore optimizes value within budget.

**Depth**
| Intent | Recommended Budget | Notes |
|--------|-------------------|-------|
| Explore | 800-2000 | Breadth over depth |
| Find | 1000-2000 | Balanced results + snippets |
| Examine | 2000-5000 | Full structure, line numbers |
| Understand | 1500-3000 | LLM synthesis length |

---

## Capsule: Scope

**Invariant**
`scope` filters by URI glob pattern. Omit for all indexed files.

**Example**
```
-- All source files
explore intent=Explore scope="file:///src/**" tokenBudget=1500

-- Only C# files
explore intent=Find keywords="service" scope="file:///src/**/*.cs" tokenBudget=1500

-- Specific directory
explore intent=Examine scope="file:///src/Services/**" tokenBudget=3000

-- Embedded documentation
explore intent=Explore scope="help:///**" tokenBudget=1500

-- Exclude tests
explore intent=Find keywords="handler" scope="file:///src/**;!**/test*" tokenBudget=1500
```
//BOUNDARY: Glob syntax: `**` any depth, `*` single level, `;` combine, `!` exclude.

**Depth**
- `file:///` - Local repository files
- `help:///` - Embedded RepoQL documentation
- `github://owner/repo` - Imported repositories
- Patterns: `**/*.cs` (C# files), `**/Services/**` (Services dir), `!**/test*` (exclude tests)

---

## Capsule: Keywords

**Invariant**
`keywords` guide semantic and lexical search. Questions work best for Understand intent.

**Example**
```
-- Concept search
explore intent=Find keywords="authentication token" tokenBudget=1500

-- Question (triggers semantic-heavy search)
explore intent=Understand keywords="How does the caching layer work?" tokenBudget=2500

-- Symbol name
explore intent=Find keywords="ValidateToken" tokenBudget=1500
```
//BOUNDARY: Empty keywords with Explore = structure-only. Questions with Find = suboptimal.

**Depth**
- Phrases work: `"authentication token refresh"`
- Questions trigger semantic mode: `"Why does X fail?"`
- Symbol names trigger lexical mode: `"AuthService.Validate"`
- Combine with scope for precision: `keywords="config" scope="file:///src/**/*.cs"`

---

## Capsule: BoostPenalize

**Invariant**
`boost` elevates regex matches; `penalize` demotes them.

**Example**
```
-- Boost service files
explore intent=Find keywords="handler" boost="(?i)service" tokenBudget=1500

-- Penalize tests
explore intent=Find keywords="parser" penalize="(?i)test|mock|spec" tokenBudget=1500

-- Combined
explore intent=Find keywords="validation" boost="(?i)input|form" penalize="(?i)test" tokenBudget=1500
```
//BOUNDARY: RE2 regex syntax. `(?i)` for case-insensitive. `|` for alternation.

**Depth**
- Boost multiplies score for matches
- Penalize reduces score (doesn't exclude)
- Apply to results after search, not to search itself
- Common patterns:
  - `(?i)service|handler` - case-insensitive OR
  - `(?i)test|mock|spec|fake` - exclude test patterns
  - `Config|Settings` - case-sensitive match

---

## Capsule: ExploreIntent

**Invariant**
Explore maps territory. Returns file inventory, language distribution, structure overview.

**Example**
```
-- What's in this codebase?
explore intent=Explore scope="file:///src/**" tokenBudget=2000

-- What docs exist?
explore intent=Explore scope="help:///**" tokenBudget=1500

-- What's in this directory?
explore intent=Explore scope="file:///src/Services/**" tokenBudget=1500
```
//BOUNDARY: Breadth over depth. Use when you don't know what to look for.

**Depth**
- Returns: file list, language breakdown, directory structure
- Good first step for unfamiliar codebases
- Low budget = headlines only; high budget = structure summaries
- Follow up with Find or Examine on interesting areas

---

## Capsule: FindIntent

**Invariant**
Find locates specific code. Returns ranked results with relevance scores and snippets.

**Example**
```
-- Find authentication code
explore intent=Find keywords="authentication" tokenBudget=1500

-- Find error handling
explore intent=Find keywords="exception handling try catch" tokenBudget=1500

-- Find specific symbol
explore intent=Find keywords="ProcessRequest" tokenBudget=1500
```
//BOUNDARY: Requires keywords. Combines semantic and lexical search.

**Depth**
- Returns: ranked URIs with scores, headlines, code snippets
- Best for: "where is X implemented?", "find code that does Y"
- Adapts to query: symbol names → lexical; concepts → semantic
- Follow up with Examine or read for full content

---

## Capsule: ExamineIntent

**Invariant**
Examine shows structure and code. Returns detailed outline with line numbers.

**Example**
```
-- Examine a specific file
explore intent=Examine scope="file:///src/Auth.cs" tokenBudget=4000

-- Examine a directory
explore intent=Examine scope="file:///src/Services/**" keywords="validation" tokenBudget=3000

-- Examine search results
explore intent=Examine keywords="authentication" tokenBudget=3000
```
//BOUNDARY: Depth over breadth. Use when you know what to look at.

**Depth**
- Returns: class/function structure, signatures, line ranges
- Best for: understanding file organization, finding entry points
- Higher budget = more detail, actual code excerpts
- Use scope to narrow down; keywords to filter within scope

---

## Capsule: ExamineRefinement

**Invariant**
When `intent=Examine` includes `keywords`, RepoQL runs a second semantic narrowing pass over shortlisted files to surface high-value method-body snippets.

**Example**
```
-- Deep inspect with semantic narrowing on top-ranked files
explore intent=Examine keywords="authentication flow token refresh" tokenBudget=3000
```
//BOUNDARY: Refinement is Examine (Inspect)-only. Find keeps headline/structure balance for breadth.

**Depth**
- Stage 1: breadth-first ranking and allocation still runs first
- Stage 2: narrowed snippets are extracted from shortlisted files
- Output favors `headline + focused snippet` evidence over long structure blocks
- Token spend remains budgeted: breadth and refinement split adaptively by score concentration
- If refinement times out or degrades, explore falls back to stage-1 output with an explicit footer hint

---

## Capsule: UnderstandIntent

**Invariant**
Understand synthesizes explanation. Returns LLM-generated answer with citations.

**Example**
```
-- How question
explore intent=Understand keywords="How does the authentication flow work?" tokenBudget=2500

-- Why question
explore intent=Understand keywords="Why does the caching layer use Redis?" tokenBudget=2000

-- What question
explore intent=Understand keywords="What patterns are used for error handling?" tokenBudget=2000
```
//BOUNDARY: Requires LLM provider (OPENROUTER_API_KEY). Falls back to Examine if unavailable.

**Depth**
- Returns: prose explanation with `file:///path#line=N,M` citations
- Best for: "how does X work?", "why is Y implemented this way?"
- Budget controls answer length, not search depth
- Citations let you verify claims with read tool
- Requires configured LLM; check with `SELECT embed_status()`

---

## Common Workflows

### Explore → Find → Examine → Read

```
-- 1. What's here?
explore intent=Explore scope="file:///src/**" tokenBudget=1500

-- 2. Where's authentication?
explore intent=Find keywords="authentication" tokenBudget=1500

-- 3. How is AuthService structured?
explore intent=Examine scope="file:///src/Auth/AuthService.cs" tokenBudget=3000

-- 4. Read the actual code
read("file:///src/Auth/AuthService.cs#symbol=ValidateToken", 2000)
```

### Question → Verify

```
-- 1. Get explanation
explore intent=Understand keywords="How does JWT token refresh work?" tokenBudget=2500

-- 2. Verify citations
read("file:///src/Auth/TokenService.cs#line=42,80", 1500)
```

---

## Common Mistakes

| Mistake | Fix |
|---------|-----|
| Explore with keywords | Use Find for keyword search |
| Find without keywords | Provide search terms or use Explore |
| Understand for simple lookup | Use Find; save Understand for synthesis |
| Very large scope + high budget | Narrow scope or reduce budget |
| Question in Find intent | Use Understand for questions |
| Symbol name in Understand | Use Find for symbol lookup |

---

## See Also

- `help:///quickstart.md` - Tool overview and workflows
- `help:///repoql/tools/read/read-command.md` - Reading specific content
- `help:///repoql/tools/query/functions/search.md` - SQL search function
