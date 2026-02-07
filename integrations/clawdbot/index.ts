/**
 * RepoQL Clawdbot Plugin Entry Point
 *
 * Purpose: Main entry point for the RepoQL Clawdbot plugin.
 *   Registers tools and background service for repository intelligence.
 * Complexity: Coordinates configuration resolution, instance management,
 *   and tool registration. Handles errors gracefully.
 */

import { InstanceManager } from "./src/lifecycle/InstanceManager.js";
import { resolveConfig } from "./src/config/resolver.js";
import { registerService } from "./src/service/RepoQlService.js";
import { registerTools } from "./src/tools/index.js";
import type { RepoQlConfig } from "./src/config/types.js";

/** Plugin identifier */
export const id = "repoql";

/** Plugin display name */
export const name = "RepoQL";

/**
 * Registers the RepoQL plugin with Clawdbot.
 *
 * @param api - Clawdbot plugin API
 */
export function register(api: any): void {
  const logger = api.logger;

  try {
    // Extract configuration from Clawdbot settings
    const rawConfig: RepoQlConfig =
      api.config?.plugins?.entries?.repoql?.config ?? {};

    // Resolve configuration with defaults
    const config = resolveConfig(rawConfig);

    // Determine workspace directory from Clawdbot API
    const getWorkdir = (): string => {
      // Use resolvePath('.') to get the workspace root
      // This matches how other Clawdbot plugins resolve workspace-relative paths
      return api.resolvePath('.');
    };

    // Create instance manager
    const manager = new InstanceManager(
      {
        exePath: config.exePath,
        healthCheckIntervalMs: config.healthCheckIntervalMs,
        maxRestartAttempts: config.maxRestartAttempts,
      },
      logger
    );

    // Register background service (with eager spawn)
    registerService(api, manager, getWorkdir);

    // Register tools
    registerTools(api, manager, config, getWorkdir);

    logger.info(`RepoQL plugin registered (exePath: ${config.exePath})`);
  } catch (err) {
    // Log error but don't throw - plugin will be non-functional but won't crash Gateway
    const message = err instanceof Error ? err.message : String(err);
    logger.error(`RepoQL plugin failed to initialize: ${message}`);

    // Register a placeholder tool that explains the error
    api.registerTool({
      name: "repoql_status",
      description: "Check RepoQL plugin status",
      parameters: {},
      async execute() {
        return {
          content: [
            {
              type: "text",
              text: `RepoQL plugin is not functional: ${message}`,
            },
          ],
          isError: true,
        };
      },
    });
  }
}
