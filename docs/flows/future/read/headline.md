# Read Headline Flow

Force single-line summary representation for matched files.

## Why This Matters

Headlines enable rapid scanning of many files. An agent can review 100+ files in ~500 tokens, filtering to the few that matter before investing in deeper reads.

| Without | With |
|---------|------|
| Read structure/content of many files to filter | Scan headlines, read deeply only what matters |
| High token cost for initial orientation | Minimal cost to understand scope |

## Trigger

`read("<pattern> => headline", tokenBudget)`

## Stages

### 1. Pattern Resolution
**Actor**: Read tool
**Action**: Resolve glob/URI pattern to matching files
**Output**: Set of file URIs
**Failure**: Invalid pattern returns error with suggestion

### 2. Budget Check
**Actor**: Read tool
**Action**: Calculate total token cost for all headlines
**Output**: Proceed if within budget, or confirmation request if exceeds

All headlines must fit, or confirmation is requested. No partial results without explicit consent.

**Failure**: If budget exceeded, request confirmation with file count and estimated cost

### 3. Headline Generation
**Actor**: Read tool
**Action**: Generate headline for each matched file
**Output**: Single-line summaries with size proxy and key identifiers

Headline elements:
- File identity (name, type, primary entity)
- Key searchable content (method names, section headings, etc.)
- Size proxy (lines, tokens, or format-appropriate measure)

### 4. Output Assembly
**Actor**: Read tool
**Action**: Assemble headlines with file URIs
**Output**: Scannable list with URIs for follow-up reads

## Termination

Flow completes when:
- All matched files rendered as headlines
- Footer reports file count and tokens used
- Or: confirmation requested when results exceed budget

## Error Handling

| Condition | Behaviour |
|-----------|-----------|
| No files match pattern | Return empty result with message |
| Headlines exceed budget | Request confirmation; show file count and estimated tokens |
| File has no headline available | Show URI with placeholder indicator |

## Verification

| Environment | How |
|-------------|-----|
| Local | Request headlines for large directory; verify single-line format, size proxies present |
| Automated tests | Assert headline token count roughly matches (file_count × avg_headline_size) |
| Production | Track headline requests; monitor for files missing headline data |

## Related

- `default.md` — automatic representation selection
- `structure.md` — hierarchical outline representation
- `content.md` — full content representation
- `tree.md` — directory tree representation
