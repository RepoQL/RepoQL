import {
  definePluginEntry,
  type OpenClawPluginApi,
  type OpenClawPluginDefinition,
} from "openclaw/plugin-sdk/plugin-entry";
import { resolvePluginConfig } from "./src/config.js";
import { RqlHostManager } from "./src/runtime/host.js";
import { WatchRegistry } from "./src/runtime/watchRegistry.js";
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
    const watches = new WatchRegistry();

    const getHost = (workspaceDir?: string): RqlHostManager => {
      const effectiveWorkspace = workspaceDir ?? process.cwd();
      const probe = new RqlHostManager({ config, logger, workspaceDir: effectiveWorkspace });
      const key = probe.repoRoot;
      const existing = hosts.get(key);
      if (existing) {
        return existing;
      }
      hosts.set(key, probe);
      return probe;
    };

    api.registerService({
      id: "repoql-service",
      async start(ctx) {
        logger.info("RepoQL plugin service starting");
        if (config.prewarm) {
          try {
            await getHost(ctx.workspaceDir).ensureReady();
            logger.info("RepoQL host prewarmed");
          } catch (err) {
            const message = err instanceof Error ? err.message : String(err);
            logger.warn(`RepoQL host prewarm failed; first tool call will retry: ${message}`);
          }
        }
      },
      async stop() {
        await watches.dispose();
        await Promise.all(Array.from(hosts.values(), (host) => host.dispose()));
        hosts.clear();
        logger.info("RepoQL plugin service stopped");
      },
    });

    registerRepoQlTools(api, getHost, config, watches);
  },
});

export default plugin;
