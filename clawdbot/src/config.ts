import { homedir } from "os";
import { resolve } from "path";
import { findGitRoot, findMarkerRoot } from "./runtime/paths.js";

export interface RepoQlPluginConfig {
  rqlPath: string;
  repoRoot?: string;
  autoStart: boolean;
  prewarm: boolean;
  startupTimeoutMs: number;
  requestTimeoutMs: number;
}

export function resolvePluginConfig(raw: Record<string, unknown>): RepoQlPluginConfig {
  return {
    rqlPath: readString(raw.rqlPath, "rql"),
    repoRoot: readOptionalPath(raw.repoRoot),
    autoStart: readBoolean(raw.autoStart, true),
    prewarm: readBoolean(raw.prewarm, false),
    startupTimeoutMs: readNumber(raw.startupTimeoutMs, 120_000),
    requestTimeoutMs: readNumber(raw.requestTimeoutMs, 120_000),
  };
}

/**
 * The workspace a tool call targets. Mirrors rql's WorkspaceResolver: the
 * nearest enclosing git repository wins, then the nearest .repoql marker; a
 * directory with neither is used as-is and the host decides whether to index it.
 */
export function resolveRepoRoot(configuredRoot: string | undefined, workspaceDir: string): string {
  if (configuredRoot) {
    return resolve(expandHome(configuredRoot));
  }

  const workspace = resolve(expandHome(workspaceDir));
  return findGitRoot(workspace) ?? findMarkerRoot(workspace) ?? workspace;
}

function readString(value: unknown, fallback: string): string {
  return typeof value === "string" && value.trim() ? value.trim() : fallback;
}

function readOptionalPath(value: unknown): string | undefined {
  return typeof value === "string" && value.trim() ? value.trim() : undefined;
}

function readBoolean(value: unknown, fallback: boolean): boolean {
  return typeof value === "boolean" ? value : fallback;
}

function readNumber(value: unknown, fallback: number): number {
  return typeof value === "number" && Number.isFinite(value) ? value : fallback;
}

function expandHome(path: string): string {
  if (path === "~") {
    return homedir();
  }
  if (path.startsWith("~/")) {
    return resolve(homedir(), path.slice(2));
  }
  return path;
}
