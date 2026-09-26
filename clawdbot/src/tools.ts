import type { AnyAgentTool, OpenClawPluginApi } from "openclaw/plugin-sdk/plugin-entry";
import {
  BUNDLED_RQL_VERSION,
  BUNDLED_TOOL_NAMES,
  TOOL_PREFIX,
  callCatalogTool,
  parseSchema,
  resolveDefinition,
} from "./catalog.js";
import { runCommand } from "./command.js";
import type { RepoQlPluginConfig } from "./config.js";
import { describeGrpcError, textResult, toolError } from "./result.js";
import type { RqlHostManager } from "./runtime/host.js";
import type { ToolDefinition } from "./runtime/rqlGrpcClient.js";
import { PLUGIN_VERSION } from "./version.js";

type GetHost = (workspaceDir?: string) => RqlHostManager;
type ToolContext = { workspaceDir?: string; sessionId?: string };

/**
 * Register the plugin's tools. Every RepoQL tool is the host's own: its
 * description, schema, and behaviour come from ToolCatalog, so the plugin
 * carries no per-tool code. OpenClaw needs each name declared up front
 * (openclaw.plugin.json contracts.tools, written by `npm run sync`), and
 * builds tools per assembly through a factory — which is where the live
 * workspace, and so the live host catalog, is resolved.
 */
export function registerRepoQlTools(api: OpenClawPluginApi, getHost: GetHost, config: RepoQlPluginConfig): void {
  api.registerTool((ctx) => statusTool(getHost(ctx.workspaceDir), config), { name: `${TOOL_PREFIX}status` });

  for (const name of BUNDLED_TOOL_NAMES) {
    api.registerTool(
      (ctx) => {
        const host = getHost(ctx.workspaceDir);
        host.warm();
        const definition = resolveDefinition(host, name);
        return definition ? catalogTool(definition, host, config, ctx) : null;
      },
      { name: TOOL_PREFIX + name }
    );
  }
}

function catalogTool(
  definition: ToolDefinition,
  host: RqlHostManager,
  config: RepoQlPluginConfig,
  ctx: ToolContext
): AnyAgentTool {
  const toolName = TOOL_PREFIX + definition.name;
  return {
    name: toolName,
    label: definition.title || `RepoQL ${definition.name}`,
    description: definition.description,
    parameters: parseSchema(definition) as AnyAgentTool["parameters"],
    execute: async (_toolCallId, params, signal, onUpdate) => {
      const args = (params ?? {}) as Record<string, unknown>;
      const report = onUpdate
        ? (text: string) =>
            onUpdate({
              content: [],
              details: { tool: definition.name },
              progress: { text, visibility: "channel", privacy: "public" },
            })
        : undefined;

      if (definition.name === "command") {
        return runCommand({
          host,
          config,
          command: String(args.command ?? ""),
          sessionId: ctx.sessionId,
          signal,
          report,
        });
      }

      return callCatalogTool({
        host,
        config,
        tool: definition.name,
        args,
        sessionId: ctx.sessionId,
        signal,
        report,
      });
    },
  };
}

function statusTool(host: RqlHostManager, config: RepoQlPluginConfig): AnyAgentTool {
  return {
    name: `${TOOL_PREFIX}status`,
    label: "RepoQL Status",
    description:
      "Check that the RepoQL host for this workspace is running (starting it if needed) and report its " +
      "version, workspace root, and index readiness. Use it when another RepoQL tool fails to connect.",
    parameters: { type: "object", properties: {}, additionalProperties: false } as AnyAgentTool["parameters"],
    execute: async () => {
      try {
        const client = await host.getClient();
        const [info, readiness] = await Promise.all([client.getHostInfo(), client.getReadiness()]);
        const catalog = host.liveCatalog;
        return textResult(
          [
            `RepoQL host is running: rql ${info.version}, readiness ${readiness.level
              .replace("READINESS_LEVEL_", "")
              .toLowerCase()}.`,
            `Workspace: ${info.workspaceRoot}`,
            `Files: ${readiness.totalFiles} indexed, ${readiness.pendingFiles} pending, ${readiness.failedFiles} failed`,
            `Socket: ${host.socketPath}`,
            `Tools: ${catalog ? `${catalog.length} from the live host catalog` : `bundled catalog (rql ${BUNDLED_RQL_VERSION})`}`,
            `Plugin: ${PLUGIN_VERSION} · rql executable: ${config.rqlPath}`,
          ].join("\n"),
          { tool: "status", workspaceRoot: info.workspaceRoot, hostVersion: info.version }
        );
      } catch (err) {
        return toolError(describeGrpcError(err));
      }
    },
  };
}
