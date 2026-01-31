---
description: Plan for web UI Read view - read tool testing with progressive disclosure
tags: [ui, plan, read, progressive-disclosure, modifiers]
audience: { human: 40, agent: 60 }
purpose: { plan: 100 }
---

# Plan: Web UI Read View

Implements: [Web UI Design](../designs/web-ui.md) — Read View, IReadService

## Scope

**Covers:**
- `IReadService` interface and implementation
- Read view with URI input and fragment support
- Token budget slider with detail level feedback
- Modifier selector (tree, history, blame, lint)
- Output display showing what agents would see
- Detail level explanation

**Does not cover:**
- Question syntax with LLM synthesis (stretch goal)
- Side-by-side comparison of different budgets (stretch goal)
- Modifier comparison view (stretch goal)

## Enables

Once Read view exists:
- **Read tool testing** — Developers can test exact read commands
- **Progressive disclosure visible** — See how budget affects output
- **Fragment testing** — Test #line=, #symbol=, #char= syntax
- **Modifier testing** — Test tree, history, blame, lint modifiers

## Prerequisites

- Plan: web-ui-1-foundation complete
- gRPC `Read` method operational on host

## North Star

Enter a read command, see exactly what an agent would get back. Adjust budget slider, watch output change. Understand progressive disclosure by experiencing it.

## Done Criteria

### IReadService
- The ReadService shall accept `ReadParams` (URI, budget, modifier, question)
- The ReadService shall call gRPC `Read` method
- The ReadService shall return `ReadResult` with content, tokens used, detail level, full token count
- When read fails, the result shall include error message

### Read View
- The Read view shall be accessible via navigation (route: `/read`)
- The Read view shall display input fields:
  - URI (text input with placeholder showing fragment examples)
  - Token Budget (slider, 50-10000, default 2000, shows current value)
  - Modifier (dropdown: None, tree, tree:folders, tree:headlines, history, blame, lint, lint:errors)
- The view shall display a Read button

### URI Input
- The input shall accept full URIs: `file:///path`, `repoql-docs:///path`
- The input shall accept fragments: `#line=N,M`, `#symbol=Name`, `#char=N,M`
- The input shall accept globs: `file:///src/**/*.cs`
- The input shall accept compound patterns: `file:///a;file:///b;!file:///c`
- Invalid URI shall show validation error

### Budget Slider
- Slider range: 50 to 10000
- Current value displayed next to slider
- Preset buttons: 100, 500, 2000, 5000

### Modifier Selection
- Dropdown with options:
  - (None) — default content
  - tree — directory structure
  - tree: folders — folders only
  - tree: headlines — with summaries
  - history — git commits
  - history: {keyword} — filtered history (text input appears)
  - blame — git blame
  - lint — diagnostics
  - lint: errors — errors only

### Read Execution
- When Read clicked, show loading state
- When complete, display output

### Output Display
- Display the raw output as agents would see it
- Display metadata header:
  - Tokens used / budget (e.g., "1,247 / 2,000")
  - Detail level: "headline", "structure", or "full"
  - If not full: "Full content would be {N} tokens"
- Content displayed in monospace font, preserving formatting

### Detail Level Explanation
- When detail level is not "full", show explanation:
  - "Showing structure because full content ({N} tokens) exceeds budget ({M})"
  - "Showing headline because structure ({N} tokens) exceeds budget ({M})"
- This makes progressive disclosure visible and educational

### Budget Exploration
- When user changes budget slider, show preview of what level would be used
- "At {N} tokens, you would get: {level}"
- Actual read only executes on button click (not on slider change)

### Error Handling
- Invalid URI: Show validation error before execution
- No files match glob: "No files match pattern"
- File not indexed: "File not in index"
- Modifier incompatible: "Blame requires file URI, not glob"

## Constraints

- **No live preview on slider** — Only preview detail level, don't execute on every change
- **No question syntax** — LLM synthesis deferred; just URI + budget + modifier
- **No comparison view** — Single output only

## References

- [Web UI Design](../designs/web-ui.md) — Read View section, IReadService contract
- [Read Testing Flow](../flows/ui/read-testing.md) — Detailed specifications
- [Read Tool Documentation](repoql-docs:///repoql/tools/read/) — Modifier reference

## Error Policy

Read errors:
1. Display error message above output area
2. Show what was attempted (URI, budget, modifier)
3. For validation errors, highlight the invalid input

Connection errors:
1. StatusStore shows offline
2. Read execution fails with connection error
3. Display "Connection lost" with retry

## Verification

| Scenario | How to verify |
|----------|---------------|
| Simple read | Read `file:///src/README.md` at 2000 budget, verify content |
| Line fragment | Read `file:///src/foo.cs#line=10,20`, verify only those lines |
| Symbol fragment | Read `file:///src/foo.cs#symbol=MyClass`, verify class content |
| Glob | Read `file:///src/**/*.md`, verify multiple files |
| Low budget | Read large file at 100 budget, verify headline only |
| Medium budget | Read large file at 500 budget, verify structure |
| High budget | Read small file at 5000 budget, verify full content |
| Tree modifier | Read `file:///src/** => tree`, verify directory structure |
| History modifier | Read `file:///src/foo.cs => history`, verify commits |
| Detail explanation | Read large file at low budget, verify explanation shown |
| Budget preview | Move slider, verify level preview updates |
