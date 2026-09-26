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
        logger.debug?.(`RepoQL orientation skipped: ${err instanceof Error ? err.message : String(err)}`);
        cache.delete(host.repoRoot);
        return entry?.text ?? "";
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
        ? "(not checked — the RepoQL host was not running; repoql_status shows it)"
        : imports.length
          ? `Use these github:// URIs directly with repoql_read / repoql_explore / repoql_query:\n${imports.join("\n")}`
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

/** github:// imports, or null when no host is connected — orientation never launches one. */
async function listImports(host: RqlHostManager): Promise<string[] | null> {
  const client = await host.runningClient(PROBE_TIMEOUT_MS);
  if (!client) {
    return null;
  }
  const result = await client.callTool(
    "query",
    { sql: "SELECT source_uri FROM Filesystems WHERE starts_with(source_uri, 'github://') ORDER BY source_uri" },
    { identity: { agent: `openclaw-repoql/${PLUGIN_VERSION}` }, timeoutMs: PROBE_TIMEOUT_MS }
  );
  if (result.isError) {
    return null;
  }
  return result.rendered
    .split("\n")
    .map((line) => line.trim())
    .filter((line) => line.startsWith("github://"));
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
