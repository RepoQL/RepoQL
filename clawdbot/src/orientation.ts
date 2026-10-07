import { execFile } from "child_process";
import { readFile } from "fs/promises";
import { join } from "path";
import type { OpenClawPluginApi } from "openclaw/plugin-sdk/plugin-entry";
import type { RepoQlPluginConfig } from "./config.js";
import type { RqlHostManager } from "./runtime/host.js";
import type { Logger } from "./runtime/types.js";
import { PLUGIN_VERSION } from "./version.js";

// The session orientation the Claude Code plugin's SessionStart hook injects:
// what RepoQL is for, the imported repositories, the reachable uplinks, and the
// repository's concept index. OpenClaw's seam is before_prompt_build returning
// appendSystemContext, which providers can cache across turns, so the text is
// rebuilt at most every few minutes and stays byte-identical in between.
//
// OpenClaw gates before_prompt_build for installed plugins behind
// plugins.entries.repoql.hooks.allowConversationAccess. Without it the hook is
// not registered and everything else in the plugin works unchanged.

type GetHost = (workspaceDir?: string) => RqlHostManager;

const REFRESH_MS = 5 * 60_000;
const PROBE_TIMEOUT_MS = 5_000;
const CONCEPT_INDEX_CANDIDATES = [".repoql/concepts/readme.md", ".repoql/concepts/README.md"];

const PREAMBLE = `# RepoQL: Repository Orientation

RepoQL is a pre-built structural index of this workspace. Before grepping or opening files to understand code, reach for the repoql_* tools: repoql_explore to survey, repoql_discover_vocabulary to learn the repository's own names, repoql_read for a precise slice (\` => structure\` before bodies), repoql_query for SQL over the graph. The repoql skill has the full orientation.`;

interface CachedOrientation {
  text: string;
  builtAt: number;
  refresh?: Promise<string>;
}

export function registerRepoQlOrientation(
  api: OpenClawPluginApi,
  getHost: GetHost,
  config: RepoQlPluginConfig,
  logger: Logger
): void {
  if (typeof api.on !== "function") {
    throw new Error("this OpenClaw has no typed plugin hooks; update OpenClaw to enable it");
  }
  const cache = new Map<string, CachedOrientation>();

  const build = (host: RqlHostManager): Promise<string> => {
    const entry = cache.get(host.repoRoot);
    if (entry?.refresh) {
      return entry.refresh;
    }
    const refresh = buildOrientation(host, config)
      .then((text) => {
        cache.set(host.repoRoot, { text, builtAt: Date.now() });
        return text;
      })
      .catch((err) => {
        // Keep what was good: a busy host should not cost the next turn its orientation.
        logger.debug?.(`RepoQL orientation refresh failed: ${err instanceof Error ? err.message : String(err)}`);
        const previous = entry?.text ?? "";
        if (previous) {
          cache.set(host.repoRoot, { text: previous, builtAt: Date.now() });
        } else {
          cache.delete(host.repoRoot);
        }
        return previous;
      });
    cache.set(host.repoRoot, { text: entry?.text ?? "", builtAt: entry?.builtAt ?? 0, refresh });
    return refresh;
  };

  api.on("before_prompt_build", async (_event, ctx) => {
    if (!ctx.workspaceDir) {
      return;
    }
    const host = getHost(ctx.workspaceDir);
    const entry = cache.get(host.repoRoot);
    let text = entry?.text ?? "";
    if (!entry || !entry.text) {
      text = await build(host);
    } else if (Date.now() - entry.builtAt > REFRESH_MS) {
      void build(host); // Serve the cached text now; the next turn gets the refresh.
    }
    return text ? { appendSystemContext: text } : undefined;
  });
}

async function buildOrientation(host: RqlHostManager, config: RepoQlPluginConfig): Promise<string> {
  const [imports, uplinks, concepts] = await Promise.all([
    listImports(host),
    listUplinks(config, host.repoRoot),
    readConceptIndex(host.repoRoot),
  ]);

  const sections = [PREAMBLE];
  sections.push(
    "## Imported Repositories\n" +
      (imports === null
        ? "(not checked — the RepoQL host was not running or did not answer in time; repoql_status shows it)"
        : imports.length
          ? `Use these URIs directly with repoql_read / repoql_explore / repoql_query:\n${imports.join("\n")}`
          : "(none — import one with repoql_import)")
  );
  if (uplinks) {
    sections.push(`## Accessible Uplinks\n${uplinks}`);
  }
  sections.push(
    '## Concepts\nRepository invariants, if any, are addressable at concept:// — browse them with repoql_read(uriGlob="concept:///**").'
  );
  if (concepts) {
    sections.push(`## Repository Concepts Index (${concepts.path})\n\n${concepts.text}`);
  }
  return sections.join("\n\n") + "\n";
}

// Every mounted source except the ones named in the WHERE clause, which the agent already knows or did not ask
// for: the primary file:///, help, memory, and worktrees. A new kind of mount is listed by default, with its kind
// in brackets. A host that predates Filesystems.kind rejects this and answers the GitHub-only listing instead.
const MOUNTS_SQL =
  "SELECT source_uri || CASE WHEN kind NOT IN ('workspace', 'import') THEN ' (' || kind || ')' ELSE '' END AS line " +
  "FROM Filesystems WHERE scheme NOT IN ('file', 'help', 'concept', 'vocabulary', 'worktree') " +
  "AND coalesce(kind, '') NOT IN ('primary', 'worktree') ORDER BY source_uri";
const LEGACY_MOUNTS_SQL =
  "SELECT source_uri AS line FROM Filesystems WHERE starts_with(source_uri, 'github://') ORDER BY source_uri";
const SOURCE_LINE = /^[a-z][a-z0-9+.-]*:\/\//i;

/**
 * The mounted sources, or null when they could not be listed — no host serving
 * (orientation never launches one), or a host too busy to answer in time.
 */
async function listImports(host: RqlHostManager): Promise<string[] | null> {
  const client = await host.runningClient(PROBE_TIMEOUT_MS);
  if (!client) {
    return null;
  }
  const ask = (sql: string) =>
    client
      .callTool("query", { sql }, { identity: { agent: `openclaw-repoql/${PLUGIN_VERSION}` }, timeoutMs: PROBE_TIMEOUT_MS })
      .catch(() => null);
  let result = await ask(MOUNTS_SQL);
  if (result?.isError) {
    result = await ask(LEGACY_MOUNTS_SQL);
  }
  if (!result || result.isError) {
    return null;
  }
  return result.rendered
    .split("\n")
    .map((line) => line.trim())
    .filter((line) => SOURCE_LINE.test(line));
}

/** `rql uplinks` output, or empty when the CLI is missing, slow, or signed out. */
function listUplinks(config: RepoQlPluginConfig, cwd: string): Promise<string> {
  return new Promise((resolveUplinks) => {
    execFile(config.rqlPath, ["uplinks"], { cwd, timeout: PROBE_TIMEOUT_MS }, (err, stdout) =>
      resolveUplinks(err ? "" : stdout.trim())
    );
  });
}

async function readConceptIndex(repoRoot: string): Promise<{ path: string; text: string } | null> {
  for (const path of CONCEPT_INDEX_CANDIDATES) {
    try {
      const text = (await readFile(join(repoRoot, path), "utf8")).trim();
      if (text) {
        return { path, text };
      }
    } catch {
      // Try the next spelling.
    }
  }
  return null;
}
