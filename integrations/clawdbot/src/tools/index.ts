/**
 * Tool registration entry point.
 *
 * Purpose: Registers all RepoQL tools with the Clawdbot API.
 * Complexity: Simple aggregation of tool registrations.
 */

import type { InstanceManager } from "../lifecycle/InstanceManager.js";
import type { ResolvedConfig } from "../config/types.js";
import { registerExploreTool } from "./explore.js";
import { registerQueryTool } from "./query.js";
import { registerReadTool } from "./read.js";
import { registerImportTool } from "./import.js";

/**
 * Registers all RepoQL tools with the Clawdbot API.
 *
 * @param api - Clawdbot plugin API
 * @param manager - Instance manager for MCP clients
 * @param config - Resolved plugin configuration
 * @param getWorkdir - Function to get the current workspace directory
 */
export function registerTools(
  api: any,
  manager: InstanceManager,
  config: ResolvedConfig,
  getWorkdir: () => string
): void {
  registerExploreTool(api, manager, config, getWorkdir);
  registerQueryTool(api, manager, config, getWorkdir);
  registerReadTool(api, manager, config, getWorkdir);
  registerImportTool(api, manager, config, getWorkdir);
}
