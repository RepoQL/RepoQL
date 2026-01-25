# Read Content Flow

Force full source content representation for matched files.

## Why This Matters

Content is the ground truth—actual code, actual text. Required for understanding implementation details, making modifications, or verifying behavior.

| Without | With |
|---------|------|
| Infer from structure what code does | See exactly what code does |
| Risk misunderstanding implementation | Full visibility for accurate changes |

## Trigger

`read("<pattern> => content", tokenBudget)`

## Stages

### 1. Pattern Resolution
**Actor**: Read tool
**Action**: Resolve glob/URI pattern to matching files
**Output**: Set of file URIs
**Failure**: Invalid pattern returns error with suggestion

### 2. Budget Check
**Actor**: Read tool
**Action**: Calculate total token cost for all file contents
**Output**: Proceed if within budget, or confirmation request if exceeds

All content must fit, or confirmation is requested. No partial results without explicit consent.

**Failure**: If budget exceeded, request confirmation with file count and estimated cost

### 3. Content Retrieval
**Actor**: Read tool
**Action**: Retrieve full content for each matched file
**Output**: Source content with line numbers

Content elements:
- Line numbers matching actual file (1-indexed)
- Full source text (no truncation within files)
- Language/format hint for syntax awareness
- File URI header for each file

### 4. Output Assembly
**Actor**: Read tool
**Action**: Assemble content with file URIs and line numbers
**Output**: Complete source with precise line references

## Termination

Flow completes when:
- All matched files rendered as full content
- Footer reports file count, total lines, and tokens used
- Or: confirmation requested when results exceed budget

## Error Handling

| Condition | Behaviour |
|-----------|-----------|
| No files match pattern | Return empty result with message |
| Content exceeds budget | Request confirmation; show file count and estimated tokens |
| Binary file matched | Show headline with binary indicator, skip content |
| File unreadable | Show URI with error message |

## Verification

| Environment | How |
|-------------|-----|
| Local | Request content for known file; verify line numbers match, content complete |
| Automated tests | Assert content output matches direct file read; line counts equal |
| Production | Track content requests; monitor for encoding issues or truncation |

## Related

- `default.md` — automatic representation selection
- `headline.md` — single-line summary representation
- `structure.md` — hierarchical outline representation
