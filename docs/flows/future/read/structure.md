# Read Structure Flow

Force hierarchical outline representation for matched files.

## Why This Matters

Structure shows what exists and where—signatures, sections, fragments—without the cost of full content. Agents can navigate directly to the right symbol or section.

| Without | With |
|---------|------|
| Read full file to find target location | See outline, jump to specific fragment |
| High token cost for navigation | Moderate cost, precise targeting |

## Trigger

`read("<pattern> => structure", tokenBudget)`

## Stages

### 1. Pattern Resolution
**Actor**: Read tool
**Action**: Resolve glob/URI pattern to matching files
**Output**: Set of file URIs
**Failure**: Invalid pattern returns error with suggestion

### 2. Budget Check
**Actor**: Read tool
**Action**: Calculate total token cost for all structures
**Output**: Proceed if within budget, or confirmation request if exceeds

All structures must fit, or confirmation is requested. No partial results without explicit consent.

**Failure**: If budget exceeded, request confirmation with file count and estimated cost

### 3. Structure Generation
**Actor**: Read tool
**Action**: Generate hierarchical outline for each matched file
**Output**: Indented structure with signatures and URI fragments

Structure elements:
- Hierarchical containment (namespaces, classes, sections)
- Signatures with visibility markers (`+`/`-`)
- Return types and parameters for callables
- URI fragments for each element (`#symbol=`, `#line=`)
- Extracted doc comments as searchable intent

Structure is complete—no truncation of elements within a file.

### 4. Output Assembly
**Actor**: Read tool
**Action**: Assemble structures with file URIs
**Output**: Navigable outlines with fragments for follow-up reads

## Termination

Flow completes when:
- All matched files rendered as structure
- Footer reports file count and tokens used
- Or: confirmation requested when results exceed budget

## Error Handling

| Condition | Behaviour |
|-----------|-----------|
| No files match pattern | Return empty result with message |
| Structures exceed budget | Request confirmation; show file count and estimated tokens |
| File format has no structure concept | Show headline with note |

## Verification

| Environment | How |
|-------------|-----|
| Local | Request structure for code file; verify all symbols present with fragments |
| Automated tests | Assert every public symbol in source appears in structure output |
| Production | Track structure requests; monitor for formats with missing structure extractors |

## Related

- `default.md` — automatic representation selection
- `headline.md` — single-line summary representation
- `content.md` — full content representation
- North star `xray-elements.md` — structure format specification
