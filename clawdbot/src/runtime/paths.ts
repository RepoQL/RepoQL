import { existsSync, mkdirSync, readFileSync, writeFileSync, appendFileSync } from "fs";
import { resolve } from "path";

// Repository-local ".repoql" path conventions. Mirrors RepoqlPaths in
// RepoQL.Hosting.Contracts so the plugin reads and writes the same files the
// host does: the committable root (.repoql/) and the gitignored cache
// (.repoql/cache/). Keep in sync with src/L3/RepoQL.Hosting.Contracts/RepoqlPaths.cs.

const DIRECTORY_NAME = ".repoql";
const CACHE_DIRECTORY_NAME = "cache";
const SOCKET_FILE_NAME = "repoql.sock";
const SOCKET_MAP_FILE_NAME = "socket.path";
const DASHBOARD_BIND_FILE_NAME = "dashboard-bind.json";
const MARKER_FILE_NAME = ".gitignore";
const OTEL_DIRECTORY_NAME = "otel";

export function repoqlDir(repoRoot: string): string {
  return resolve(repoRoot, DIRECTORY_NAME);
}

export function cacheDir(repoRoot: string): string {
  return resolve(repoqlDir(repoRoot), CACHE_DIRECTORY_NAME);
}

export function dashboardBindPath(repoRoot: string): string {
  return resolve(cacheDir(repoRoot), DASHBOARD_BIND_FILE_NAME);
}

export function otelLogRoot(repoRoot: string): string {
  return resolve(cacheDir(repoRoot), OTEL_DIRECTORY_NAME);
}

export function markerPath(repoRoot: string): string {
  return resolve(repoqlDir(repoRoot), MARKER_FILE_NAME);
}

/**
 * Resolve the Unix socket path for the host. Honours the socket.path mapping
 * file the host writes when the default path exceeds the platform limit,
 * falling back to .repoql/cache/repoql.sock.
 */
export function resolveSocketPath(repoRoot: string): string {
  const cache = cacheDir(repoRoot);
  const socketMap = resolve(cache, SOCKET_MAP_FILE_NAME);

  if (existsSync(socketMap)) {
    const mapped = readFileSync(socketMap, "utf8").trim();
    if (mapped) {
      return resolve(repoRoot, mapped);
    }
  }

  return resolve(cache, SOCKET_FILE_NAME);
}

/** Read the dashboard URL the host's HTTP listener recorded, or null. */
export function readDashboardUrl(repoRoot: string): string | null {
  try {
    const bindPath = dashboardBindPath(repoRoot);
    if (!existsSync(bindPath)) {
      return null;
    }
    const parsed = JSON.parse(readFileSync(bindPath, "utf8")) as { url?: unknown };
    return typeof parsed.url === "string" ? parsed.url : null;
  } catch {
    return null;
  }
}

/** Walk up from start looking for a .git directory; null when none is found. */
export function findGitRoot(start: string): string | null {
  let current = resolve(start);
  for (;;) {
    if (existsSync(resolve(current, ".git"))) {
      return current;
    }
    const parent = resolve(current, "..");
    if (parent === current) {
      return null;
    }
    current = parent;
  }
}

/** Walk up from start looking for a .repoql marker; null when none is found. */
export function findMarkerRoot(start: string): string | null {
  let current = resolve(start);
  for (;;) {
    if (existsSync(markerPath(current))) {
      return current;
    }
    const parent = resolve(current, "..");
    if (parent === current) {
      return null;
    }
    current = parent;
  }
}

export type WorkspaceInitOutcome = "initialized" | "alreadyInitialized" | "alreadyGitRepository";

export interface WorkspaceInitResult {
  root: string;
  outcome: WorkspaceInitOutcome;
}

/**
 * Designate a directory as a RepoQL workspace by writing the .repoql marker.
 * Idempotent: a git repository or already-marked directory is left untouched.
 * Mirrors WorkspaceInitializer so `repoql_command "init"` creates the same
 * marker the host's `rql init` does.
 */
export function initializeWorkspace(targetDirectory: string): WorkspaceInitResult {
  const target = resolve(targetDirectory);

  const gitRoot = findGitRoot(target);
  if (gitRoot) {
    return { root: gitRoot, outcome: "alreadyGitRepository" };
  }

  const markerRoot = findMarkerRoot(target);
  if (markerRoot) {
    return { root: markerRoot, outcome: "alreadyInitialized" };
  }

  ensureWorkspaceDir(target);
  return { root: target, outcome: "initialized" };
}

/** The canonical agent-facing message for an initialization outcome. */
export function describeInit(result: WorkspaceInitResult): string {
  switch (result.outcome) {
    case "initialized":
      return (
        `Initialized RepoQL workspace at ${result.root} — created .repoql/. ` +
        "Indexing starts when you next use a RepoQL tool, or run `rql serve` now."
      );
    case "alreadyInitialized":
      return `${result.root} is already a RepoQL workspace (.repoql marker present).`;
    case "alreadyGitRepository":
      return `${result.root} is a git repository — RepoQL already indexes it; no .repoql needed.`;
  }
}

/** Ensure .repoql/ and .repoql/cache/ exist and the cache/ marker is gitignored. */
export function ensureWorkspaceDir(repoRoot: string): void {
  mkdirSync(cacheDir(repoRoot), { recursive: true });
  const gitIgnore = markerPath(repoRoot);
  const expected = "cache/\n";
  if (existsSync(gitIgnore)) {
    if (!readFileSync(gitIgnore, "utf8").includes("cache/")) {
      appendFileSync(gitIgnore, expected);
    }
  } else {
    writeFileSync(gitIgnore, expected);
  }
}
