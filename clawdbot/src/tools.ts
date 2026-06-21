import { Type, type Static } from "typebox";
import type { AnyAgentTool, OpenClawPluginApi } from "openclaw/plugin-sdk/plugin-entry";
import type { RepoQlPluginConfig } from "./config.js";
import { descriptions } from "./descriptions/index.js";
import { runCommand } from "./command.js";
import { describeGrpcError, textResult, toolError, type ToolResult } from "./result.js";
import type { RqlHostManager } from "./runtime/host.js";
import type { RqlGrpcClient } from "./runtime/rqlGrpcClient.js";
import { ConceptCategory } from "./runtime/rqlGrpcClient.js";
import type { WatchRegistry } from "./runtime/watchRegistry.js";
import { runWatch } from "./runtime/watchRegistry.js";

type GetHost = (workspaceDir?: string) => RqlHostManager;

// ---------------------------------------------------------------------------
// Parameter schemas — descriptions mirror the RepoQL MCP server's [Description]
// attributes so the plugin presents the same parameter guidance.
// ---------------------------------------------------------------------------

const StatusParams = Type.Object({}, { additionalProperties: false });

const QueryParams = Type.Object(
  {
    sql: Type.String({
      description:
        "DuckDB-style SQL. Use `DESCRIBE SELECT * FROM <view_or_macro(...)> LIMIT 0;` to introspect any view or table-macro's columns.",
    }),
    tokenBudget: Type.Optional(
      Type.Number({ description: "Token budget for response.", default: 15000 })
    ),
    timeoutMs: Type.Optional(
      Type.Number({
        description:
          "Hard deadline in milliseconds for the query. Defaults to 5 minutes. Raise this only if a single statement legitimately needs longer than 5 minutes.",
        default: 300000,
      })
    ),
  },
  { additionalProperties: false }
);

const ReadParams = Type.Object(
  {
    uri: Type.String({
      description:
        "Canonical URI of what to read. Supported fragments: #line=M,N, #symbol=NAME. Globs allowed in the path. Modifiers include content, structure, headlines, tree, lint, grep, regex, find, similar, where, changes, concepts, history, blame, and question.",
    }),
    tokenBudget: Type.Optional(
      Type.Number({ description: "Token budget for response.", default: 15000 })
    ),
  },
  { additionalProperties: false }
);

const ExploreParams = Type.Object(
  {
    keywords: Type.String({
      description:
        "Vocabulary probe — class names, concepts, or short phrases. Hybrid lexical + semantic match.",
    }),
    uriGlob: Type.Optional(
      Type.String({
        description: 'URI glob to restrict the search scope (e.g. "file:///src/**/*.cs").',
      })
    ),
    breadth: Type.Optional(
      Type.Number({ description: "Maximum number of results. 0 = auto.", default: 0 })
    ),
    tokenBudget: Type.Optional(
      Type.Number({ description: "Token budget for response.", default: 1500 })
    ),
    question: Type.Optional(
      Type.String({
        description:
          "Full-sentence question describing your intent. Pass it when you have one — it drives reranking so results are ordered by how well they ANSWER you, not just match keywords. Skip only when you're surveying what exists.",
      })
    ),
  },
  { additionalProperties: false }
);

const ExplainParams = Type.Object(
  {
    question: Type.String({
      description: "The question you want answered. Full sentences work best.",
    }),
    keywords: Type.String({
      description:
        "The symbols, files, or terms-of-art the explain service should focus on. Bring what you already know — you almost always have better vocabulary than we can infer.",
    }),
    uriGlob: Type.Optional(
      Type.String({ description: "URI glob to scope the search. Defaults to the current repo." })
    ),
    tokenBudget: Type.Optional(
      Type.Number({ description: "Token budget for response.", default: 5000 })
    ),
  },
  { additionalProperties: false }
);

const KeywordsParams = Type.Object(
  {
    keywords: Type.String({
      description:
        "Required. The rough handles or terms you already have — the tool reshapes them into the repository's real vocabulary.",
    }),
    question: Type.Optional(
      Type.String({
        description:
          "Optional full-sentence intent. Used to rerank candidates against what you are actually trying to find.",
      })
    ),
    uriGlob: Type.Optional(
      Type.String({
        description:
          'URI glob to restrict the evidence scope (for example, "file:///src/**/*.cs;!**/*Tests.cs").',
      })
    ),
    tokenBudget: Type.Optional(
      Type.Number({
        description:
          "Token budget for the rendered response. This is the representation dial: small budgets render names only, larger budgets add location, the largest add a one-line summary.",
        default: 1500,
      })
    ),
  },
  { additionalProperties: false }
);

const ExecuteParams = Type.Object(
  {
    code: Type.String({
      description:
        "JavaScript source. Your script runs as a QuickJS-WASM module — top-level `return` is a syntax error; wrap in `(() => { ... })()` to return a value. Imported sandbox modules can be any WASM-compatible language.",
    }),
    intent: Type.Optional(
      Type.String({
        description:
          "What you are trying to accomplish. Keep it short — it should stand alone without reading your conversation.",
        default: "",
      })
    ),
    tokenBudget: Type.Optional(
      Type.Number({ description: "Token budget for response.", default: 15000 })
    ),
    timeout: Type.Optional(
      Type.Number({ description: "Execution timeout in milliseconds.", default: 300000 })
    ),
  },
  { additionalProperties: false }
);

const CaptureConceptParams = Type.Object(
  {
    name: Type.String({ description: "Short CamelCase capsule name, acts as identifier" }),
    category: Type.String({ description: "Concept category — one of: wisdom, rule, knowledge" }),
    invariant: Type.String({
      description:
        "One timeless idea in one short sentence — the capsule's invariant. Mechanism, stakes, and context belong in why; if tempted to use 'and' or 'or', split the concept",
    }),
    tags: Type.String({
      description:
        "Comma-separated search tags. Name linked capsules here, and strategically pick keywords that an agent would use within the scope of relevance",
    }),
    relevance: Type.String({
      description:
        "Semicolon-delimited URI glob spec indicating where the concept applies. Drives contextual surfacing when an agent touches a matching file; whether it also loads up front is controlled by isUniversal.",
    }),
    subcategory: Type.Optional(
      Type.String({
        description: "Optional lowercase shelf under the category. Input is lowercased before filing.",
      })
    ),
    verification: Type.Optional(
      Type.String({
        description:
          "Semicolon-delimited URI globs or web URLs that fact-check the concept. Anchor URIs to the proof with #symbol=NAME or #line=M,N — the checker reads only the head of each source. Required for knowledge; optional but encouraged for rule; ignored for wisdom.",
        default: "",
      })
    ),
    ttlDays: Type.Optional(
      Type.Number({
        description:
          "How many days the concept's verification stays accurate before re-checking. Required when you provide verification; leave 0 for timeless or unverified concepts, which carry no TTL.",
        default: 0,
      })
    ),
    why: Type.Optional(
      Type.String({
        description:
          "Why the invariant holds — mechanism, stakes, context. Lives in its own capsule section so the invariant stays one short line.",
      })
    ),
    example: Type.Optional(
      Type.String({ description: "Concrete example that makes the invariant click." })
    ),
    depth: Type.Optional(
      Type.String({
        description: "Newline-separated depth bullets: distinctions, trade-offs, NotThis, SeeAlso.",
      })
    ),
    isUniversal: Type.Optional(
      Type.Boolean({
        description:
          "Load this concept into the always-on CLAUDE.md index every agent sees. Universal is the default — most concepts are broad enough to be worth knowing up front, and the token spend pays off. Set false only when the concept applies in a genuinely narrow scope; it will still surface contextually via relevance.",
        default: true,
      })
    ),
  },
  { additionalProperties: false }
);

const ImportParams = Type.Object(
  {
    importUri: Type.String({
      description:
        "URI to import (e.g., github://owner/repo@ref). Prefix with '-' to remove an import, e.g. -github://owner/repo.",
    }),
  },
  { additionalProperties: false }
);

const CommandParams = Type.Object(
  {
    command: Type.String({
      description:
        'Management command. Examples: "help", "config list", "config read --key search.rerankEnabled", "config set --key search.rerankEnabled --value true", "account whoami", "import list", "host status".',
    }),
  },
  { additionalProperties: false }
);

const WatchParams = Type.Object(
  {
    executable: Type.String({
      description: "Executable to run under the OTEL collector. Use a binary name on PATH or a file path.",
    }),
    arguments: Type.Optional(
      Type.Array(Type.String(), { description: "Arguments to pass to the executable." })
    ),
    workingDirectory: Type.Optional(
      Type.String({
        description:
          "Working directory for the watched executable. Defaults to the caller's current working directory.",
      })
    ),
    environment: Type.Optional(
      Type.String({
        description:
          "Extra environment variables for the watched process, as 'key=value;key2=value2'. Inherits the host environment; %VAR% references are expanded; escape a literal ';' or '=' with '\\'. RepoQL's OTEL exporter variables always take precedence.",
      })
    ),
  },
  { additionalProperties: false }
);

type QueryParamsType = Static<typeof QueryParams>;
type ReadParamsType = Static<typeof ReadParams>;
type ExploreParamsType = Static<typeof ExploreParams>;
type ExplainParamsType = Static<typeof ExplainParams>;
type KeywordsParamsType = Static<typeof KeywordsParams>;
type ExecuteParamsType = Static<typeof ExecuteParams>;
type CaptureConceptParamsType = Static<typeof CaptureConceptParams>;
type ImportParamsType = Static<typeof ImportParams>;
type CommandParamsType = Static<typeof CommandParams>;
type WatchParamsType = Static<typeof WatchParams>;

// ---------------------------------------------------------------------------
// Registration
// ---------------------------------------------------------------------------

export function registerRepoQlTools(
  api: OpenClawPluginApi,
  getHost: GetHost,
  config: RepoQlPluginConfig,
  watches: WatchRegistry
): void {
  // Each tool is registered as its own factory so the live OpenClawPluginToolContext
  // — and therefore the agent's workspace — is resolved per invocation, routing to
  // the right per-workspace host. A factory's name is not known until it runs, so the
  // tool name is declared up front in opts (and must match contracts.tools); without
  // it OpenClaw registers the tool with no name and it never surfaces.
  const register = (name: string, build: (gh: GetHost) => AnyAgentTool): void => {
    api.registerTool((ctx) => {
      const bound: GetHost = () => getHost(ctx.workspaceDir);
      return build(bound);
    }, { name });
  };

  register("repoql_status", (gh) => statusTool(gh, config));
  register("repoql_query", (gh) => queryTool(gh, config));
  register("repoql_explore", (gh) => exploreTool(gh, config));
  register("repoql_read", (gh) => readTool(gh));
  register("repoql_explain", (gh) => explainTool(gh));
  register("repoql_keywords", (gh) => keywordsTool(gh, config));
  register("repoql_execute", (gh) => executeTool(gh));
  register("repoql_capture_concept", (gh) => captureConceptTool(gh));
  register("repoql_import", (gh) => importTool(gh));
  register("repoql_command", (gh) => commandTool(gh, config));
  register("repoql_watch", (gh) => watchTool(gh, watches));
}

// ---------------------------------------------------------------------------
// Tool factories
// ---------------------------------------------------------------------------

function statusTool(getHost: GetHost, config: RepoQlPluginConfig): AnyAgentTool {
  return {
    name: "repoql_status",
    label: "RepoQL Status",
    description: "Check the RepoQL plugin, repository root, socket path, and host reachability.",
    parameters: StatusParams,
    execute: async (_id, _params, _signal, _onUpdate) => {
      const host = getHost();
      try {
        await host.ensureReady();
      } catch (err) {
        return toolError(describeGrpcError(err));
      }
      return textResult(
        [
          "RepoQL host is reachable.",
          `Repository: ${host.repoRoot}`,
          `Socket: ${host.socketPath}`,
          `rql: ${config.rqlPath}`,
        ].join("\n"),
        { tool: "status", repoRoot: host.repoRoot }
      );
    },
  };
}

function queryTool(getHost: GetHost, config: RepoQlPluginConfig): AnyAgentTool {
  return {
    name: "repoql_query",
    label: "RepoQL Query",
    description: descriptions.query,
    parameters: QueryParams,
    execute: async (_id, params) => {
      const p = params as QueryParamsType;
      const client = await getClient(getHost);
      try {
        const result = await client.query({
          sql: p.sql,
          maxRows: config.queryMaxRows,
          tokenBudget: p.tokenBudget ?? 0,
        }, p.timeoutMs);
        return textResult(formatQuery(result), { tool: "query", rows: result?.totalRows });
      } catch (err) {
        return toolError(describeGrpcError(err));
      }
    },
  };
}

function exploreTool(getHost: GetHost, config: RepoQlPluginConfig): AnyAgentTool {
  return {
    name: "repoql_explore",
    label: "RepoQL Explore",
    description: descriptions.explore,
    parameters: ExploreParams,
    execute: async (_id, params) => {
      const p = params as ExploreParamsType;
      const client = await getClient(getHost);
      try {
        const result = await client.explore({
          keywords: p.keywords,
          uriGlob: p.uriGlob ?? "",
          breadth: p.breadth ?? 0,
          tokenBudget: p.tokenBudget ?? config.defaultTokenBudget,
          question: p.question ?? "",
        });
        return rendered(result, "explore");
      } catch (err) {
        return toolError(describeGrpcError(err));
      }
    },
  };
}

function readTool(getHost: GetHost): AnyAgentTool {
  return {
    name: "repoql_read",
    label: "RepoQL Read",
    description: descriptions.read,
    parameters: ReadParams,
    execute: async (_id, params) => {
      const p = params as ReadParamsType;
      const client = await getClient(getHost);
      try {
        const result = await client.read({ uri: p.uri, tokenBudget: p.tokenBudget ?? 15_000 });
        return rendered(result, "read");
      } catch (err) {
        return toolError(describeGrpcError(err));
      }
    },
  };
}

function explainTool(getHost: GetHost): AnyAgentTool {
  return {
    name: "repoql_explain",
    label: "RepoQL Explain",
    description: descriptions.explain,
    parameters: ExplainParams,
    execute: async (_id, params) => {
      const p = params as ExplainParamsType;
      const client = await getClient(getHost);
      try {
        const result = await client.explain({
          question: p.question,
          keywords: p.keywords,
          uriGlob: p.uriGlob ?? "",
          tokenBudget: p.tokenBudget ?? 5_000,
        });
        return rendered(result, "explain");
      } catch (err) {
        return toolError(describeGrpcError(err));
      }
    },
  };
}

function keywordsTool(getHost: GetHost, config: RepoQlPluginConfig): AnyAgentTool {
  return {
    name: "repoql_keywords",
    label: "RepoQL Keywords",
    description: descriptions.keywords,
    parameters: KeywordsParams,
    execute: async (_id, params) => {
      const p = params as KeywordsParamsType;
      const client = await getClient(getHost);
      try {
        const result = await client.keywords({
          keywords: p.keywords,
          question: p.question ?? "",
          uriGlob: p.uriGlob ?? "",
          tokenBudget: p.tokenBudget ?? config.defaultTokenBudget,
        });
        return rendered(result, "keywords");
      } catch (err) {
        return toolError(describeGrpcError(err));
      }
    },
  };
}

function executeTool(getHost: GetHost): AnyAgentTool {
  return {
    name: "repoql_execute",
    label: "RepoQL Execute",
    description: descriptions.execute,
    parameters: ExecuteParams,
    execute: async (_id, params) => {
      const p = params as ExecuteParamsType;
      const client = await getClient(getHost);
      try {
        const result = await client.execute(
          {
            code: p.code,
            intent: p.intent ?? "",
            tokenBudget: p.tokenBudget ?? 15_000,
            timeoutMs: p.timeout ?? 300_000,
          },
          p.timeout
        );
        if (!result?.success) {
          const message = nonEmpty(result?.rendered) ?? nonEmpty(result?.errorMessage) ?? "Execution failed.";
          return toolError(message);
        }
        return rendered(result, "execute");
      } catch (err) {
        return toolError(describeGrpcError(err));
      }
    },
  };
}

function captureConceptTool(getHost: GetHost): AnyAgentTool {
  return {
    name: "repoql_capture_concept",
    label: "RepoQL Capture Concept",
    description: descriptions.captureConcept,
    parameters: CaptureConceptParams,
    execute: async (_id, params) => {
      const p = params as CaptureConceptParamsType;
      const category = parseCategory(p.category);
      if (category === undefined) {
        return toolError(`Unknown category '${p.category}'. Use one of: wisdom, rule, knowledge.`);
      }
      const client = await getClient(getHost);
      try {
        const result = await client.captureConcept({
          name: p.name,
          category,
          subcategory: p.subcategory ?? "",
          invariant: p.invariant,
          tags: splitOn(p.tags, ","),
          relevance: p.relevance,
          verification: p.verification ?? "",
          ttlDays: p.ttlDays ?? 0,
          example: p.example ?? "",
          depth: splitOn(p.depth, "\n"),
          isUniversal: p.isUniversal ?? true,
          why: p.why ?? "",
        });
        if (!result?.success) {
          return toolError(nonEmpty(result?.rendered) ?? nonEmpty(result?.message) ?? "Concept capture failed.");
        }
        return rendered(result, "capture_concept");
      } catch (err) {
        return toolError(describeGrpcError(err));
      }
    },
  };
}

function importTool(getHost: GetHost): AnyAgentTool {
  return {
    name: "repoql_import",
    label: "RepoQL Import",
    description: descriptions.import,
    parameters: ImportParams,
    execute: async (_id, params) => {
      const p = params as ImportParamsType;
      const client = await getClient(getHost);
      try {
        const result = await client.importRepository({ uri: p.importUri });
        return textResult(formatImport(result), { tool: "import", operationId: result?.operationId });
      } catch (err) {
        return toolError(describeGrpcError(err));
      }
    },
  };
}

function commandTool(getHost: GetHost, config: RepoQlPluginConfig): AnyAgentTool {
  return {
    name: "repoql_command",
    label: "RepoQL Command",
    description: descriptions.command,
    parameters: CommandParams,
    execute: async (_id, params, signal, onUpdate) => {
      const p = params as CommandParamsType;
      const report = onUpdate
        ? (text: string) =>
            onUpdate({
              content: [],
              details: { tool: "command" },
              progress: { text, visibility: "channel", privacy: "public" },
            })
        : undefined;
      return runCommand({ host: getHost(), config, command: p.command, signal, report });
    },
  };
}

function watchTool(getHost: GetHost, watches: WatchRegistry): AnyAgentTool {
  return {
    name: "repoql_watch",
    label: "RepoQL Watch",
    description: descriptions.watch,
    parameters: WatchParams,
    execute: async (_id, params, signal) => {
      const p = params as WatchParamsType;
      return runWatch({
        host: getHost(),
        watches,
        executable: p.executable,
        arguments: p.arguments ?? [],
        workingDirectory: p.workingDirectory,
        environment: p.environment,
        signal,
      });
    },
  };
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

async function getClient(getHost: GetHost): Promise<RqlGrpcClient> {
  try {
    return await getHost().getClient();
  } catch (err) {
    return toolError(describeGrpcError(err));
  }
}

/** Render a tool response that carries a server-rendered text block plus footer. */
function rendered(result: any, tool: string): ToolResult {
  const body = String(result?.rendered ?? "");
  const footer = formatFooter(result?.footer);
  return textResult(footer ? `${body}\n${footer}` : body, {
    tool,
    tokensUsed: result?.footer?.tokensUsed,
  });
}

function formatImport(result: any): string {
  const lines: string[] = [result?.message ? String(result.message) : "Import request accepted."];

  if (result?.operationId) {
    lines.push("", `Operation ID: ${result.operationId}`);
    lines.push(`Check progress: SELECT * FROM Operations WHERE id = '${result.operationId}'`);
  }

  if (typeof result?.fileCount === "number" && result.fileCount > 0) {
    lines.push("", `Files in scope: ${result.fileCount}`);
  }

  return lines.join("\n");
}

function formatQuery(result: any): string {
  if (result?.rendered) {
    const footer = formatFooter(result.footer);
    return footer ? `${result.rendered}\n${footer}` : String(result.rendered);
  }

  const columns: any[] = Array.isArray(result?.columns) ? result.columns : [];
  const rows: any[] = Array.isArray(result?.rows) ? result.rows : [];
  const headers = columns.map((column) => String(column?.name ?? ""));
  const lines: string[] = [];

  if (headers.length > 0) {
    lines.push(headers.join("\t"));
  }

  for (const row of rows) {
    const values = Array.isArray(row?.values) ? row.values : [];
    lines.push(values.map(formatValue).join("\t"));
  }

  if (lines.length === 0) {
    lines.push("No results.");
  }

  lines.push("", `query | ${result?.totalRows ?? rows.length} rows`);
  return lines.join("\n");
}

function formatValue(value: any): string {
  // QueryValue wraps a google.protobuf.StringValue: null when absent, { value }
  // when present. proto-loader surfaces the wrapper as { text: { value } } or null.
  if (value === null || value === undefined) {
    return "NULL";
  }
  if (typeof value === "object" && "text" in value) {
    const inner = value.text;
    return inner === null || inner === undefined ? "NULL" : String(inner?.value ?? inner);
  }
  return typeof value === "object" ? JSON.stringify(value) : String(value);
}

function formatFooter(footer: any): string {
  if (!footer) {
    return "";
  }

  const parts = [
    `files=${footer.totalFiles ?? "?"}`,
    `pending=${footer.pendingFiles ?? "?"}`,
    `failed=${footer.failedFiles ?? "?"}`,
    `semantic=${footer.semanticReady ? "ready" : "not-ready"}`,
    typeof footer.semanticPercent === "number" ? `${footer.semanticPercent}%` : undefined,
    typeof footer.elapsedMs === "number" ? `${footer.elapsedMs}ms` : undefined,
    typeof footer.tokensUsed === "number" ? `${footer.tokensUsed} tokens` : undefined,
  ].filter(Boolean);

  return `---\n${parts.join(" | ")}`;
}

function parseCategory(value: string): number | undefined {
  switch (value.trim().toLowerCase()) {
    case "wisdom":
      return ConceptCategory.Wisdom;
    case "rule":
      return ConceptCategory.Rule;
    case "knowledge":
      return ConceptCategory.Knowledge;
    default:
      return undefined;
  }
}

function splitOn(value: string | undefined, separator: string): string[] {
  if (!value) {
    return [];
  }
  return value
    .split(separator)
    .map((part) => part.trim())
    .filter((part) => part.length > 0);
}

function nonEmpty(value: unknown): string | undefined {
  return typeof value === "string" && value.trim().length > 0 ? value : undefined;
}
