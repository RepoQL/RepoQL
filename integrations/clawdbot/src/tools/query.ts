/**
 * repoql_query tool implementation.
 *
 * Purpose: Registers the query tool for DuckDB SQL access.
 * Complexity: Delegates to InstanceManager and normalizes errors.
 */

import type { InstanceManager } from "../lifecycle/InstanceManager.js";
import type { ResolvedConfig } from "../config/types.js";
import { TOOL_TIMEOUTS } from "../config/types.js";
import { normalizeError } from "../mcp/errors.js";
import { QueryParams, type QueryParamsType } from "./schemas.js";
import type { McpToolResult } from "../mcp/types.js";

const QUERY_DESCRIPTION = `<CONCEPT>
DuckDB SQL for computation on the indexed repository.
Use query when you need to COMPUTE (aggregate, filter, join, extract) - not just DISCOVER.
</CONCEPT>

<DECISION>
| Need | Tool |
|------|------|
| "What exists? Where is X?" | explore |
| "Show me this file/symbol" | read |
| "How many? Which ones? What pattern?" | **query** |

Query when: aggregating, complex filtering, joining results, regex extraction, graph traversal.
</DECISION>

<VIEWS>
Primary views - cover 90% of queries. Start here:

**Files** - documents with diagnostics
\`uri, lang, lines, error_count, warning_count, headline, summary, structure\`
\`\`\`sql
SELECT uri, error_count FROM Files WHERE lang = 'code.csharp' AND error_count > 0;
SELECT lang, COUNT(*), SUM(lines) FROM Files GROUP BY lang;
\`\`\`

**Functions** - methods/constructors across languages
\`name, qualified_name, declaring_type, signature, return_type, is_async, start_line\`
\`\`\`sql
SELECT name, signature FROM Functions WHERE declaring_type = 'UserService';
SELECT file_uri, name FROM Functions WHERE is_async AND return_type LIKE '%Task%';
\`\`\`

**Types** - classes/interfaces/structs
\`name, qualified_name, type_kind, namespace, extends, implements, start_line\`
\`\`\`sql
SELECT name, file_uri FROM Types WHERE extends = 'BaseService';
SELECT name FROM Types WHERE type_kind = 'interface';
\`\`\`

**Annotations** - errors/warnings/lint
\`resolved_target_uri, severity, rule_id, message\`
\`\`\`sql
SELECT resolved_target_uri, message FROM Annotations WHERE severity = 'error';
SELECT rule_id, COUNT(*) FROM Annotations GROUP BY rule_id ORDER BY 2 DESC;
\`\`\`
</VIEWS>

<FUNCTIONS>
**search(q, k)** - semantic + lexical document search
\`\`\`sql
SELECT uri, score FROM search('authentication', k := 10);
\`\`\`

**search_symbol(q, scope, kind_filter, k)** - find functions, classes, methods by name
\`\`\`sql
SELECT symbol, uri FROM search_symbol('ValidateToken');
SELECT symbol FROM search_symbol('Service', kind_filter := 'type', scope := 'src/**/*.cs');
\`\`\`

**snippet(uri, context)** - code preview around location
\`\`\`sql
SELECT line_number, text FROM snippet('file:///src/api.cs#line=42', 3);
\`\`\`

**glob_files(pattern)** - path pattern matching
\`\`\`sql
SELECT uri FROM glob_files('src/**/*.cs;!src/**/tests/**');
\`\`\`

**tree(uris_json, headlines_json, foldersOnly)** - format URIs as ASCII directory tree
\`\`\`sql
SELECT tree(json_group_array(uri ORDER BY uri), json_group_array(headline ORDER BY uri), false)
FROM Files WHERE uri LIKE 'file:///src/%';
\`\`\`

**Composition with LATERAL** - expand each row
\`\`\`sql
SELECT s.uri, sn.text
FROM search('config', k := 5) s, LATERAL snippet(s.uri, 2) sn
WHERE sn.is_focus;
\`\`\`
</FUNCTIONS>

<MORE>
**Format-specific views** - prefixed by format (e.g., \`markdown_headings\`, \`csharp_types\`)
See \`repoql-docs:///repoql/tools/query/formats/*\` for available views per format.

**ask()** - LLM-powered question answering on query results
\`\`\`sql
SELECT ask((SELECT json_group_array(json_object('uri', uri)) FROM search('auth', k := 5)), 'How is auth implemented?');
\`\`\`

**related()** - find similar documents
\`\`\`sql
SELECT uri, score FROM related('file:///src/Auth.cs', k := 10);
\`\`\`

**Git history** - \`git_status()\`, \`git_diff()\`, \`git_blame()\`, \`git_hotspots\`, \`changes_related_to()\`.
</MORE>

<BUDGET>
Large results are auto-summarized when they exceed your token budget.
Repeat the exact query to bypass summarization and get full results.
</BUDGET>

<REMEMBER>
- Views first: Files, Functions, Types, Annotations
- search() finds, snippet() shows context, LATERAL composes them
- Format-specific views/functions documented at repoql-docs:///repoql/tools/query/formats/*
- Large results auto-summarize; repeat query for full output
- Docs at \`repoql-docs:///\` - query or explore them to learn more
</REMEMBER>`;

/**
 * Registers the repoql_query tool.
 */
export function registerQueryTool(
  api: any,
  manager: InstanceManager,
  config: ResolvedConfig,
  getWorkdir: () => string
): void {
  api.registerTool({
    name: "repoql_query",
    description: QUERY_DESCRIPTION,
    parameters: QueryParams,
    async execute(_id: string, params: QueryParamsType): Promise<McpToolResult> {
      try {
        const workdir = getWorkdir();
        const client = await manager.getInstance(workdir);
        const timeout = TOOL_TIMEOUTS.query ?? config.defaultTimeoutMs;

        const args: Record<string, unknown> = {
          sql: params.sql,
        };

        if (params.tokenBudget !== undefined) {
          args.tokenBudget = params.tokenBudget;
        }

        return await client.callTool("query", args, timeout);
      } catch (err) {
        return normalizeError(err);
      }
    },
  });
}
