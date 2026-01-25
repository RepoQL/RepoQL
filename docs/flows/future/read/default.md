# Read Default Flow

Automatic representation selection when no modifier is specified.

## Why This Matters

The default behavior is the 80% case—agents requesting content without specifying how to display it. Getting this right means agents receive maximum insight without needing to think about representation.

| Without | With |
|---------|------|
| Agent must guess appropriate modifier | Optimal representation chosen automatically |
| Over-fetching wastes tokens | Budget spent efficiently |
| Under-fetching requires follow-up reads | Single read delivers useful result |

## Trigger

`read("<pattern>", tokenBudget)` called without a `=>` modifier.

## Stages

### 1. Pattern Resolution
**Actor**: Read tool
**Action**: Resolve glob/URI pattern to matching files
**Output**: Set of file URIs with their token costs at each representation level
**Failure**: Invalid pattern returns error with suggestion

### 2. Representation Selection
**Actor**: Read tool
**Action**: Determine richest uniform representation that fits all matched files within budget
**Output**: Selected representation level (content, structure, or headline)

Selection priority:
1. **Content** — if all files fit as full content
2. **Structure** — if all files fit as structure outlines
3. **Headline** — if all files fit as single-line summaries

All files receive the same representation level for consistency.

**Failure**: If even headlines exceed budget, request confirmation before proceeding

### 3. Output Generation
**Actor**: Read tool
**Action**: Generate output at selected representation level for all matched files
**Output**: Formatted content with file URIs and representation

### 4. Budget Reporting
**Actor**: Read tool
**Action**: Include footer with tokens used and representation chosen
**Output**: Footer indicating actual cost and what representation was applied

## Termination

Flow completes when:
- All matched files rendered at selected representation level
- Footer reports tokens used and representation chosen
- Or: confirmation requested when results exceed budget

## Error Handling

| Condition | Behaviour |
|-----------|-----------|
| No files match pattern | Return empty result with message |
| Pattern syntax invalid | Error with corrected pattern suggestion |
| Results exceed budget | Request confirmation; agent can repeat to override |
| Single file too large for budget | Show what fits with truncation indicator |

## Verification

| Environment | How |
|-------------|-----|
| Local | Request with varying budgets; verify representation scales appropriately |
| Automated tests | Assert: 3 small files + large budget = content; same files + small budget = headlines |
| Production | Track representation selection distribution; alert if content selection rate drops |

## Related

- `read-tool.md` (north-star) — outcomes for all read modifiers
- `headline.md` — forced headline representation
- `structure.md` — forced structure representation
- `content.md` — forced content representation
