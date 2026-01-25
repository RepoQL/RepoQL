# Workflow Patterns

> Common patterns for composing RepoQL tools.

## Pattern 1: The Funnel

**Broad → Narrow → Deep**

Use when: Starting from uncertainty, narrowing to specifics.

```
1. explore (Inventory)  → What's in this codebase?
2. explore (Locate)     → Where is authentication?
3. query                → Which files in the auth module?
4. read                 → Show me AuthService.cs
```

## Pattern 2: The Expand

**Specific → Related → Context**

Use when: Starting from a known point, discovering connections.

```
1. read              → Read the file I know about
2. query (edges)     → What calls this? What does it call?
3. explore (Explain) → Understand the whole cluster
```

## Pattern 3: The Correlate

**Multiple Sources → Join → Insight**

Use when: Answering questions that span different data sources.

```
# When did this behavior change?
1. explore (Locate)  → Find the relevant code
2. query (git_log)   → Get history for those files
3. query (git_diff)  → What changed?

# What symbols are only used by tests?
1. query (symbols)   → All public symbols
2. query (edges)     → Find usages
3. query (filter)    → Where all usages are in test files

# How does our code use this external API?
1. mcp (external)    → Get API documentation
2. explore (Locate)  → Find our usage
3. Join insights     → Understand the gap
```

## Pattern 4: The Pipeline

**Query → Transform → Use**

Use when: Analyzing data or producing reports.

```
1. query             → Get raw data (from repo, git, MCP, data files)
2. query (aggregate) → Transform and summarize
3. Use result        → Feed to another tool, export, or report
```

## Pattern 5: The Investigation

**Symptom → Trace → Root Cause**

Use when: Debugging or incident investigation.

```
1. explore (Locate)  → Find where error originates
2. query (edges)     → Trace call chain backward
3. query (git)       → When did this area last change?
4. read              → Examine the suspicious code
```

## Pattern 6: The Informed Modification

**Understand → Find All → Change → Verify**

Use when: Making changes with confidence.

```
1. explore (Explain) → How does this work today?
2. query (edges)     → Find all affected locations
3. explore (Locate)  → Find similar code as pattern
4. Edit              → Make the changes
5. Bash              → Build and test
```

## Pattern 7: The Cross-Reference

**Content Type A ↔ Content Type B**

Use when: Linking code to docs, tests to implementation, config to code.

```
# Find docs for this code
1. read (code)            → Get the code
2. explore (docs scope)   → Search docs for related concepts
3. query (edges)          → Check for explicit doc links

# Find what config affects this code
1. read (code)            → See what config keys it reads
2. explore (config scope) → Find where those keys are set
3. Trace the chain        → Environment → config file → code
```

## Pattern 8: The Federation

**Import → Unify → Query Across**

Use when: Working across multiple sources - repos, monitoring data, analysis reports.

```
1. import               → Bring external sources into the graph
2. explore (Inventory)  → See unified view across all sources
3. query                → Compare, correlate, join across sources
4. read                 → Examine specific content
```

## Choosing a Pattern

| Situation | Pattern |
|-----------|---------|
| New to codebase | Funnel |
| Know one file, need context | Expand |
| Question spans sources | Correlate |
| Need aggregate/report | Pipeline |
| Something broke | Investigation |
| Making a change | Informed Modification |
| Linking code↔docs↔config | Cross-Reference |
| Multiple sources as one | Federation |

## Combining Patterns

Real tasks often combine patterns:

**"Add a new API endpoint"**
```
Funnel     → Find where endpoints are defined
Expand     → Understand the pattern from existing endpoint
Cross-Ref  → Find related docs, tests, config
Modify     → Implement following the pattern
```

**"Debug a production issue"**
```
Investigate → Trace the error
Correlate   → Check git history for recent changes
Expand      → Understand the affected area
Modify      → Fix and verify
```
