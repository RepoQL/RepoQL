/**
 * Configuration types for the RepoQL Clawdbot plugin.
 *
 * Purpose: Defines the shape of plugin configuration.
 * Complexity: Minimal - just type definitions.
 */

/**
 * RepoQL plugin configuration.
 */
export interface RepoQlConfig {
  /** Path to repoql executable. Auto-detected from PATH if not set. */
  exePath?: string;

  /** Health check interval in milliseconds. Default: 60000 */
  healthCheckIntervalMs?: number;

  /** Maximum restart attempts before giving up. Default: 3 */
  maxRestartAttempts?: number;

  /** Default timeout for tool calls in milliseconds. Default: 60000 */
  defaultTimeoutMs?: number;

  /** Use the agent workspace as the repository root. Default: true */
  workspaceAsRepo?: boolean;

  /** Explicit workspace path. Overrides workspaceAsRepo when set. */
  workspace?: string;
}

/**
 * Resolved configuration with all defaults applied.
 */
export interface ResolvedConfig {
  exePath: string;
  healthCheckIntervalMs: number;
  maxRestartAttempts: number;
  defaultTimeoutMs: number;
  workspaceAsRepo: boolean;
  workspace?: string;
}

/**
 * Tool-specific timeout configuration.
 */
export const TOOL_TIMEOUTS: Record<string, number> = {
  explore: 60_000,
  query: 120_000,
  read: 60_000,
  import: 300_000,
};
