import { readFileSync } from "fs";
import { dirname, resolve } from "path";
import { fileURLToPath } from "url";
import { status as GrpcStatus, type ServiceError } from "@grpc/grpc-js";
import type { RepoQlPluginConfig } from "./config.js";
import { describeGrpcError, textResult, toolError, type ToolResult } from "./result.js";
import type { RqlHostManager } from "./runtime/host.js";
import type { CallIdentity, ToolDefinition } from "./runtime/rqlGrpcClient.js";
import { PLUGIN_VERSION } from "./version.js";

/** OpenClaw tool names are the host's catalog names under this prefix. */
export const TOOL_PREFIX = "repoql_";

/** Grace added to a tool's own timeout argument so the host reports the timeout, not the transport. */
const TIMEOUT_GRACE_MS = 30_000;

interface CatalogSnapshot {
  rqlVersion: string;
  tools: ToolDefinition[];
}

// The catalog snapshot `npm run sync` captured from a live host. OpenClaw
// assembles tools synchronously, so descriptions and schemas must exist
// before any host is contacted; once a host answers DescribeTools, its live
// catalog replaces this one on the next assembly.
const SNAPSHOT: CatalogSnapshot = JSON.parse(
  readFileSync(resolve(dirname(fileURLToPath(import.meta.url)), "catalog.json"), "utf8")
);

/** Host catalog names bundled with this build — the set the manifest declares. */
export const BUNDLED_TOOL_NAMES: readonly string[] = SNAPSHOT.tools.map((tool) => tool.name);

/** The rql version the bundled catalog was captured from. */
export const BUNDLED_RQL_VERSION = SNAPSHOT.rqlVersion;

/**
 * The definition to present for a host tool. The live catalog wins; a tool
 * the live host no longer offers resolves to null so it is withheld rather
 * than advertised and then refused.
 */
export function resolveDefinition(host: RqlHostManager, name: string): ToolDefinition | null {
  const live = host.liveCatalog;
  if (live) {
    return live.find((tool) => tool.name === name) ?? null;
  }
  return SNAPSHOT.tools.find((tool) => tool.name === name) ?? null;
}

export function parseSchema(definition: ToolDefinition): Record<string, unknown> {
  try {
    const schema = JSON.parse(definition.inputSchemaJson);
    if (schema && typeof schema === "object") {
      return schema;
    }
  } catch {
    // Fall through to the permissive schema below.
  }
  return { type: "object", properties: {} };
}

export interface CatalogCallOptions {
  host: RqlHostManager;
  config: RepoQlPluginConfig;
  tool: string;
  args: Record<string, unknown>;
  sessionId?: string;
  signal?: AbortSignal;
  report?: (text: string) => void;
}

/**
 * Invoke a host tool through ToolCatalog.CallTool and shape its rendered
 * result for OpenClaw. A tool-reported failure (is_error) and a transport
 * failure both surface as thrown, actionable messages. A host that went away
 * between calls (restart, idle exit) is re-discovered once.
 */
export async function callCatalogTool(options: CatalogCallOptions): Promise<ToolResult> {
  const { host, tool, args } = options;
  const identity: CallIdentity = { agent: `openclaw-repoql/${PLUGIN_VERSION}`, sessionId: options.sessionId };
  const timeoutMs = callTimeout(options.config.requestTimeoutMs, args);

  for (let attempt = 0; ; attempt++) {
    let client;
    try {
      client = await host.getClient();
    } catch (err) {
      return toolError(describeGrpcError(err));
    }

    try {
      const result = await client.callTool(tool, args, {
        identity,
        timeoutMs,
        signal: options.signal,
        onProgress: (progress) => {
          if (progress.message) {
            options.report?.(progress.message);
          }
        },
      });
      const text = String(result.rendered ?? "");
      if (result.isError) {
        return toolError(text || `RepoQL ${tool} failed without a message.`);
      }
      const footer = result.footer ?? undefined;
      return textResult(text, {
        tool,
        tokensUsed: footer?.tokensUsed,
        pendingFiles: footer?.pendingFiles,
        semanticReady: footer?.semanticReady,
      });
    } catch (err) {
      if (attempt === 0 && (err as ServiceError)?.code === GrpcStatus.UNAVAILABLE) {
        host.reset();
        continue;
      }
      return toolError(describeGrpcError(err, timeoutMs));
    }
  }
}

/** Tools that take their own deadline (query's timeoutMs, execute's timeout) get it honoured end to end. */
function callTimeout(baseMs: number, args: Record<string, unknown>): number {
  const own = [args.timeoutMs, args.timeout].find((value) => typeof value === "number" && value > 0) as
    | number
    | undefined;
  return own ? Math.max(baseMs, own + TIMEOUT_GRACE_MS) : baseMs;
}
