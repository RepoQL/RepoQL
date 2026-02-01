/**
 * Configuration resolution and executable discovery.
 *
 * Purpose: Resolves plugin configuration, including finding the RepoQL executable.
 * Complexity: Handles PATH lookup and platform-specific executable names.
 */

import { existsSync } from "fs";
import { join } from "path";
import type { RepoQlConfig, ResolvedConfig } from "./types.js";
import { RepoQlNotFoundError } from "../mcp/errors.js";

const DEFAULT_CONFIG: Omit<ResolvedConfig, "exePath"> = {
  healthCheckIntervalMs: 60_000,
  maxRestartAttempts: 3,
  defaultTimeoutMs: 60_000,
  workspaceAsRepo: true,
};

/**
 * Resolves plugin configuration with defaults applied.
 *
 * @param config - Raw configuration from plugin settings
 * @returns Resolved configuration with all values set
 * @throws RepoQlNotFoundError if executable cannot be found
 */
export function resolveConfig(config: RepoQlConfig): ResolvedConfig {
  const exePath = config.exePath ?? findRepoQlExecutable();

  return {
    exePath,
    healthCheckIntervalMs: config.healthCheckIntervalMs ?? DEFAULT_CONFIG.healthCheckIntervalMs,
    maxRestartAttempts: config.maxRestartAttempts ?? DEFAULT_CONFIG.maxRestartAttempts,
    defaultTimeoutMs: config.defaultTimeoutMs ?? DEFAULT_CONFIG.defaultTimeoutMs,
    workspaceAsRepo: config.workspaceAsRepo ?? DEFAULT_CONFIG.workspaceAsRepo,
    workspace: config.workspace,
  };
}

/**
 * Finds the RepoQL executable on PATH.
 *
 * @returns Path to the repoql executable
 * @throws RepoQlNotFoundError if not found
 */
function findRepoQlExecutable(): string {
  const isWindows = process.platform === "win32";
  const executableName = isWindows ? "repoql.exe" : "repoql";
  const searchedPaths: string[] = [];

  // Get PATH directories
  const pathEnv = process.env.PATH ?? "";
  const pathSeparator = isWindows ? ";" : ":";
  const pathDirs = pathEnv.split(pathSeparator).filter(Boolean);

  for (const dir of pathDirs) {
    const fullPath = join(dir, executableName);
    searchedPaths.push(fullPath);

    if (existsSync(fullPath)) {
      return fullPath;
    }
  }

  // Also check common installation locations
  const commonPaths = isWindows
    ? [
        join(process.env.LOCALAPPDATA ?? "", "Programs", "repoql", "repoql.exe"),
        join(process.env.PROGRAMFILES ?? "", "repoql", "repoql.exe"),
        "C:\\repoql\\repoql.exe",
      ]
    : [
        "/usr/local/bin/repoql",
        "/usr/bin/repoql",
        join(process.env.HOME ?? "", ".local", "bin", "repoql"),
      ];

  for (const path of commonPaths) {
    if (path) {
      searchedPaths.push(path);
      if (existsSync(path)) {
        return path;
      }
    }
  }

  throw new RepoQlNotFoundError(searchedPaths);
}
