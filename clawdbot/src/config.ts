import { existsSync } from "fs";
import { homedir } from "os";
import { resolve } from "path";

export interface RepoQlPluginConfig {
  rqlPath: string;
  repoRoot?: string;
  autoStart: boolean;
  prewarm: boolean;
  startupTimeoutMs: number;
  requestTimeoutMs: number;
  defaultTokenBudget: number;
  queryMaxRows: number;
}

export function resolvePluginConfig(raw: Record<string, unknown>): RepoQlPluginConfig {
  return {
    rqlPath: readString(raw.rqlPath, "rql"),
    repoRoot: readOptionalPath(raw.repoRoot),
    autoStart: readBoolean(raw.autoStart, true),
    prewarm: readBoolean(raw.prewarm, false),
    startupTimeoutMs: readNumber(raw.startupTimeoutMs, 120_000),
    requestTimeoutMs: readNumber(raw.requestTimeoutMs, 120_000),
    defaultTokenBudget: readNumber(raw.defaultTokenBudget, 1_500),
    queryMaxRows: readNumber(raw.queryMaxRows, 0),
  };
}

export function resolveRepoRoot(configuredRoot: string | undefined, workspaceDir: string): string {
  if (configuredRoot) {
    return resolve(expandHome(configuredRoot));
  }

  const workspace = resolve(expandHome(workspaceDir));
  return findRepoMarker(workspace) ?? workspace;
}

function findRepoMarker(start: string): string | undefined {
  let current = resolve(start);

  while (true) {
    if (existsSync(resolve(current, ".git")) || existsSync(resolve(current, ".repoql"))) {
      return current;
    }

    const parent = resolve(current, "..");
    if (parent === current) {
      return undefined;
    }
    current = parent;
  }
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
