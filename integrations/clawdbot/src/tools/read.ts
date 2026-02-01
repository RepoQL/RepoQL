/**
 * repoql_read tool implementation.
 *
 * Purpose: Registers the read tool for token-budget-aware content retrieval.
 * Complexity: Delegates to InstanceManager and normalizes errors.
 */

import type { InstanceManager } from "../lifecycle/InstanceManager.js";
import type { ResolvedConfig } from "../config/types.js";
import { TOOL_TIMEOUTS } from "../config/types.js";
import { normalizeError } from "../mcp/errors.js";
import { ReadParams, type ReadParamsType } from "./schemas.js";
import type { McpToolResult } from "../mcp/types.js";

const READ_DESCRIPTION = `Fetch repository content by URI with token-budget-aware representation selection.

### Capsule: ReadBasic
**Invariant**
\`read(uri, budget)\` returns content at the richest level that fits the budget.
**Example**
read("file:///src/Auth.cs", 5000)              -> full content if <=5000 tokens
read("file:///src/Auth.cs", 500)               -> headline + structure if full too large
read("file:///src/Auth.cs", 50)                -> headline only
**Depth**
- Progressive disclosure: full -> structure -> headline
- Globs distribute budget across matches: read("file:///src/**/*.cs", 10000)
- Fragments work: #line=10,50, #symbol=Foo.Bar
---

### Capsule: ReadWithQuestion
**Invariant**
Append \` // <question>\` for LLM-synthesized answer with citations.
**Example**
read("file:///src/Auth.cs // How does JWT validation work?", 2000)
read("file:///src/**/*.cs // What patterns are used for error handling?", 3000)
**Depth**
- Internally uses explore Understand pipeline (search + LLM synthesis)
- Budget controls LLM response size
- Citations as file:///path#line=N,M - always verify before trusting
- Broad questions dilute; focused questions concentrate relevance
---

### Capsule: WhenToUse
**Invariant**
Use read when you KNOW the URI; use explore when you need to FIND it.
**Example**
+ read("file:///src/Auth.cs", 2000)           -> you know the file
+ read("help:///quickstart.md // How?", 1500) -> known doc, specific question
- read("file:///src/**/*.cs", 50000)          -> too broad, use explore Examine
**Depth**
- explore: discover what exists, find by concept, understand architecture
- read: retrieve known content, answer questions about specific files
- Workflow: explore Explore -> explore Find -> read specific files
---

<EXAMPLES>
Single file, full content:
  read("file:///src/Auth.cs", 5000)

Line range:
  read("file:///src/Auth.cs#line=42,100", 2000)

Symbol:
  read("file:///src/Auth.cs#symbol=ValidateToken", 1500)

Symbol pattern (all descendants):
  read("file:///src/Auth.cs#symbol=AuthService.**", 3000)

Glob pattern:
  read("file:///src/Services/**/*.cs", 8000)

Compound pattern (multiple includes):
  read("file:///src/**/*.cs;file:///lib/**/*.cs", 10000)

Compound with exclusions:
  read("file:///src/**/*.cs;!file:///src/tests/**", 8000)

With question (LLM synthesis):
  read("file:///docs/API.md // What authentication methods are supported?", 2000)

Multiple files with question:
  read("file:///src/Auth/**/*.cs // How is the refresh token rotated?", 3000)

Tree overview:
  read("file:///src/** => tree", 2000)

History with keyword filter:
  read("file:///src/Auth.cs => history: token", 1500)
</EXAMPLES>

### Capsule: Modifiers
**Invariant**
Append \` => modifier\` to request a specific view of the content.
**Example**
read("file:///src/** => tree", 2000)       // folder structure
read("file:///src/Auth.cs => history", 1500) // what changed
**Depth**
- tree: folder structure with file counts by type
- headline: one-line summary per file
- structure: signatures without bodies
- history: commits affecting file; \`: keyword\` filters by message/author
- lint: diagnostics; \`: errors\` or \`: warnings\` filters severity
---

### Capsule: BudgetAsInvestment
**Invariant**
Budget is how much context you spend to get the answer; invest wisely.
**Example**
Low confidence what you need? Start small: read("file:///src/**", 500)
Know exactly what you need? Invest more: read("file:///src/Auth.cs", 5000)
**Depth**
- 500: inventory scan; see what exists before committing
- 1500: understand shape; enough for navigation decisions
- 3000: read implementation; enough for most single-file tasks
- 5000+: deep dive; multiple files or complex analysis
- NotThis: large budget on broad glob wastes tokens on low-relevance files
---`;

/**
 * Registers the repoql_read tool.
 */
export function registerReadTool(
  api: any,
  manager: InstanceManager,
  config: ResolvedConfig,
  getWorkdir: () => string
): void {
  api.registerTool({
    name: "repoql_read",
    description: READ_DESCRIPTION,
    parameters: ReadParams,
    async execute(_id: string, params: ReadParamsType): Promise<McpToolResult> {
      try {
        const workdir = getWorkdir();
        const client = await manager.getInstance(workdir);
        const timeout = TOOL_TIMEOUTS.read ?? config.defaultTimeoutMs;

        const args: Record<string, unknown> = {
          uri: params.uri,
          tokenBudget: params.tokenBudget,
        };

        return await client.callTool("read", args, timeout);
      } catch (err) {
        return normalizeError(err);
      }
    },
  });
}
