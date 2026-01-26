/**
 * RepoQL Clawdbot Plugin
 * 
 * Provides native agent tools for repository understanding via RepoQL's
 * queryable knowledge graph. Shells out to mcporter for MCP communication.
 */

import { execSync } from "child_process";
import { Type, Static } from "@sinclair/typebox";

// Plugin config
interface RepoQlConfig {
  exePath?: string;
  workspaceAsRepo?: boolean;
}

// Tool parameter schemas
const ExploreParams = Type.Object({
  intent: Type.Union([
    Type.Literal("Inventory"),
    Type.Literal("Locate"),
    Type.Literal("Inspect"),
    Type.Literal("Explain"),
  ], { description: "Knowledge state: Inventory (discover), Locate (find), Inspect (structure), Explain (synthesize)" }),
  tokenBudget: Type.Number({ description: "Tokens to invest in response (800-5000 typical)" }),
  scope: Type.Optional(Type.String({ description: "Path filter glob (e.g., file:///src/**/*.cs)" })),
  keywords: Type.Optional(Type.String({ description: "Search terms - questions work best" })),
  boost: Type.Optional(Type.String({ description: "Regex patterns to boost (e.g., (?i)auth|token)" })),
  penalize: Type.Optional(Type.String({ description: "Regex patterns to demote (e.g., (?i)test|mock)" })),
});

const QueryParams = Type.Object({
  sql: Type.String({ description: "DuckDB SQL query (use Files, Functions, Types, Annotations views)" }),
  tokenBudget: Type.Optional(Type.Number({ description: "Token budget for response (default: 15000)", default: 15000 })),
});

const ReadParams = Type.Object({
  uri: Type.String({ description: "URI or glob (e.g., file:///src/Auth.cs). Append ' // question' for LLM synthesis." }),
  tokenBudget: Type.Number({ description: "Token budget - controls representation depth" }),
});

// Helper to call mcporter
function callRepoQl(tool: string, args: Record<string, unknown>, workdir: string, timeoutMs = 60000): string {
  // Build mcporter command
  const argParts: string[] = [];
  for (const [key, value] of Object.entries(args)) {
    if (value !== undefined && value !== null) {
      // Handle different types
      if (typeof value === "string") {
        argParts.push(`${key}=${JSON.stringify(value)}`);
      } else {
        argParts.push(`${key}=${value}`);
      }
    }
  }

  const cmd = `mcporter call repoql.${tool} ${argParts.join(" ")}`;
  
  try {
    const result = execSync(cmd, {
      cwd: workdir,
      encoding: "utf-8",
      timeout: timeoutMs,
      stdio: ["pipe", "pipe", "pipe"],
      maxBuffer: 10 * 1024 * 1024, // 10MB
    });
    return result;
  } catch (err: any) {
    if (err.stdout) {
      return err.stdout; // Might have partial output
    }
    throw err;
  }
}

// Plugin exports
export const id = "repoql";
export const name = "RepoQL";

export function register(api: any) {
  const logger = api.logger;
  const config: RepoQlConfig = api.config?.plugins?.entries?.repoql?.config ?? {};
  
  // Determine workspace
  const workdir = config.workspaceAsRepo !== false 
    ? (api.workspace || process.cwd())
    : process.cwd();

  logger.info(`RepoQL plugin registered (workdir: ${workdir})`);

  // Register explore tool
  api.registerTool({
    name: "repoql_explore",
    description: `X-ray vision for repositories. See structure and find things without reading files.

INTENTS (match to your knowledge state):
- Inventory: "What exists here?" → broad discovery
- Locate: "Where is X?" → ranked results with snippets  
- Inspect: "Show me the structure" → detailed view with line numbers
- Explain: "How does X work?" → LLM-synthesized answer

WORKFLOW: Inventory → Locate → Inspect → Explain (accumulate knowledge)

TOKEN BUDGETS: Inventory 800-2000, Locate 1000-2000, Inspect 2000-5000, Explain 1000-3000`,
    parameters: ExploreParams,
    async execute(_id: string, params: Static<typeof ExploreParams>) {
      try {
        const result = callRepoQl("explore", params as Record<string, unknown>, workdir);
        return { content: [{ type: "text", text: result }] };
      } catch (err: any) {
        return { content: [{ type: "text", text: `RepoQL error: ${err.message}` }], isError: true };
      }
    },
  });

  // Register query tool
  api.registerTool({
    name: "repoql_query",
    description: `DuckDB SQL on indexed repository. Use for aggregation, filtering, pattern matching.

KEY VIEWS:
- Files: uri, lang, lines, error_count, headline, summary
- Functions: name, qualified_name, signature, declaring_type, is_async
- Types: name, qualified_name, type_kind, extends, implements
- Annotations: resolved_target_uri, severity, rule_id, message

KEY FUNCTIONS:
- search('query', k := 10) → semantic + lexical search
- search_symbol('name') → find functions/types by name
- snippet(uri, context) → code preview around location`,
    parameters: QueryParams,
    async execute(_id: string, params: Static<typeof QueryParams>) {
      try {
        const result = callRepoQl("query", params as Record<string, unknown>, workdir);
        return { content: [{ type: "text", text: result }] };
      } catch (err: any) {
        return { content: [{ type: "text", text: `RepoQL error: ${err.message}` }], isError: true };
      }
    },
  });

  // Register read tool
  api.registerTool({
    name: "repoql_read",
    description: `Fetch repository content with token-budget-aware representation.

Progressive disclosure: full → structure → headline (based on budget)

EXAMPLES:
- read("file:///src/Auth.cs", 3000) → full content if fits
- read("file:///src/**/*.cs", 5000) → distribute budget across matches
- read("file:///src/Auth.cs#symbol=ValidateToken", 1500) → specific symbol
- read("file:///docs/API.md // What auth methods?", 2000) → LLM synthesis

BUDGETS: 500-1000 headlines, 1000-3000 structure, 3000+ full content`,
    parameters: ReadParams,
    async execute(_id: string, params: Static<typeof ReadParams>) {
      try {
        const result = callRepoQl("read", params as Record<string, unknown>, workdir);
        return { content: [{ type: "text", text: result }] };
      } catch (err: any) {
        return { content: [{ type: "text", text: `RepoQL error: ${err.message}` }], isError: true };
      }
    },
  });
}
