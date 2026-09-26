import {
  definePluginEntry,
  type OpenClawPluginApi,
  type OpenClawPluginDefinition,
} from "openclaw/plugin-sdk/plugin-entry";
import { resolvePluginConfig } from "./src/config.js";
import { RqlHostManager } from "./src/runtime/host.js";
import { registerRepoQlHints } from "./src/hints.js";
import { registerRepoQlOrientation } from "./src/orientation.js";
import { registerRepoQlTools } from "./src/tools.js";
import type { Logger } from "./src/runtime/types.js";

export const id = "repoql";
export const name = "RepoQL";
export const description = "Queryable repository intelligence powered by rql.";

const plugin: OpenClawPluginDefinition = definePluginEntry({
  id,
  name,
  description,
  register(api: OpenClawPluginApi): void {
    const logger: Logger = api.logger;
    const config = resolvePluginConfig(api.pluginConfig ?? {});
    const hosts = new Map<string, RqlHostManager>();

    // One manager per workspace root: every agent working in the same
    // repository shares one connection, one lease, and one catalog.
    const getHost = (workspaceDir?: string): RqlHostManager => {
      const candidate = new RqlHostManager({ config, logger, workspaceDir: workspaceDir ?? process.cwd() });
      const existing = hosts.get(candidate.repoRoot);
      if (existing) {
        return existing;
      }
      hosts.set(candidate.repoRoot, candidate);
      return candidate;
    };

    api.registerService({
      id: "repoql-service",
      async start(ctx) {
        if (!config.prewarm) {
          return;
        }
        try {
          await getHost(ctx.workspaceDir).getClient();
          logger.info("RepoQL host prewarmed");
        } catch (err) {
          const message = err instanceof Error ? err.message : String(err);
          logger.warn(`RepoQL host prewarm failed; the first tool call will retry: ${message}`);
        }
      },
      async stop() {
        // Releasing the leases is enough: the host is shared with other
        // clients, and an implicitly-started one idles out on its own.
        for (const host of hosts.values()) {
          host.dispose();
        }
        hosts.clear();
      },
    });

    registerRepoQlTools(api, getHost, config);

    // Repository memory rides on newer OpenClaw surfaces (tool-result
    // middleware, prompt and finalize hooks) and on permissions the operator
    // grants. Where one is missing, say so once and keep the tools working.
    const optional = (feature: string, register: () => void): void => {
      try {
        register();
      } catch (err) {
        logger.warn(`RepoQL ${feature} unavailable: ${err instanceof Error ? err.message : String(err)}`);
      }
    };
    optional("concept and vocabulary hints", () => registerRepoQlHints(api, getHost, logger));
    optional("session orientation", () => registerRepoQlOrientation(api, getHost, config, logger));
  },
});

export default plugin;
