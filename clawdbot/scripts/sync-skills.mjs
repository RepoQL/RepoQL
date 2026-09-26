#!/usr/bin/env node
// Mirror the Claude Code plugin's skills into the OpenClaw plugin.
//
//   npm run sync:skills
//
// plugins/repoql/skills is the exemplar. Every skill there is copied into
// clawdbot/skills and adapted for OpenClaw: the RepoQL tools are named
// repoql_<tool>, there is no `!` shell prefix, and no Claude-only frontmatter.
// Skills the plugin owns (OPENCLAW_SKILLS) are left alone; any other directory
// in clawdbot/skills that the exemplar no longer has is removed.
//
// Each targeted rewrite must match at least once. When the exemplar's wording
// changes, the sync fails and names the rule, instead of shipping a skill that
// silently kept its Claude-specific instructions.
import { cpSync, existsSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync } from "fs";
import { dirname, join, relative, resolve } from "path";
import { fileURLToPath } from "url";

const PLUGIN_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const SOURCE = resolve(PLUGIN_ROOT, "..", "plugins", "repoql", "skills");
const TARGET = join(PLUGIN_ROOT, "skills");

/** Skills written for OpenClaw itself; the sync never touches them. */
const OPENCLAW_SKILLS = new Set(["repoql"]);

/** Exemplar skills with no meaning outside Claude Code. */
const EXCLUDED = new Set(["statusline-builder"]);

/** Exemplar design notes, not skill content. */
const SKIP_DIRECTORIES = new Set(["meta"]);

const TROUBLESHOOTING_PREFACE = `> **In OpenClaw.** The RepoQL tools are named \`repoql_<tool>\` — \`command("help")\` below is \`repoql_command\` with \`command: "help"\`, \`query\` is \`repoql_query\`, and so on. They reach the host over gRPC through the RepoQL plugin rather than an MCP bridge; \`repoql_status\` reports what the plugin is attached to. The workspace is the agent's workspace (its enclosing git repository, else its \`.repoql\` marker), or the plugin's \`repoRoot\` setting when one is set — not the shell's working directory. While the gateway runs, the plugin holds a lease on the host, so it does not idle out. Commands shown as \`rql …\` run through your shell tool.
`;

/** Rewrites applied to every copied markdown file. */
const GLOBAL_RULES = [
  { name: "drop allowed-tools frontmatter", pattern: /^allowed-tools:.*\n/gm, replace: "" },
  { name: "drop argument-hint frontmatter", pattern: /^argument-hint:.*\n/gm, replace: "" },
  {
    name: "read(uri, budget) → repoql_read",
    pattern: /\bread\("([^"]+)",\s*(\d+)\)/g,
    replace: 'repoql_read(uriGlob="$1", tokenBudget=$2)',
  },
  { name: "`! rql` → `rql`", pattern: /! rql\b/g, replace: "rql" },
];

/** Rewrites that must each match in one file. */
const FILE_RULES = {
  "research/SKILL.md": [
    {
      name: "research subagent brief",
      pattern: /Research subagents get `help:\/\/\/skills\/research\/subagent\.md` — or spawn the `researcher` agent, which carries the brief and a suitable model built in\./,
      replace:
        "When the user asks for parallel research, spawn one subagent per independent direction and tell each to read `help:///skills/research/subagent.md` in full (with `repoql_read`) before gathering evidence.",
    },
  ],
  "skill-builder/SKILL.md": [
    { name: "skill-builder audience", pattern: /Claude Code skills/, replace: "OpenClaw skills" },
  ],
  "troubleshooting-repoql/SKILL.md": [
    { name: "troubleshooting OpenClaw preface", pattern: /^(# Diagnose RepoQL\n)/m, replace: `$1\n${TROUBLESHOOTING_PREFACE}` },
  ],
  "troubleshooting-repoql/references/mcp-bridge.md": [
    { name: "mcp-bridge shell hint", pattern: /Run it yourself with `!`:/, replace: "Run it yourself with your shell tool:" },
  ],
};

if (!existsSync(SOURCE)) {
  console.error(`Exemplar skills not found at ${SOURCE}. Run from a checkout of RepoQL/RepoQL.`);
  process.exit(1);
}

const failures = [];
const exemplar = readdirSync(SOURCE).filter((name) => statSync(join(SOURCE, name)).isDirectory() && !EXCLUDED.has(name));

for (const name of readdirSync(TARGET)) {
  if (!OPENCLAW_SKILLS.has(name) && !exemplar.includes(name)) {
    rmSync(join(TARGET, name), { recursive: true, force: true });
    console.log(`removed  ${name} (not in the exemplar)`);
  }
}

for (const name of exemplar) {
  const destination = join(TARGET, name);
  rmSync(destination, { recursive: true, force: true });
  cpSync(join(SOURCE, name), destination, {
    recursive: true,
    filter: (path) => !SKIP_DIRECTORIES.has(relative(join(SOURCE, name), path).split("/")[0]),
  });
  for (const file of markdownFiles(destination)) {
    adapt(file, relative(TARGET, file));
  }
  console.log(`synced   ${name}`);
}

for (const [file, rules] of Object.entries(FILE_RULES)) {
  for (const rule of rules) {
    if (!rule.applied) {
      failures.push(`${file}: rule "${rule.name}" matched nothing`);
    }
  }
}

if (failures.length) {
  console.error(`\nThe exemplar changed under ${failures.length} adaptation rule(s):`);
  for (const failure of failures) console.error(`  - ${failure}`);
  console.error("Update the rule in scripts/sync-skills.mjs so the OpenClaw copy stays adapted.");
  process.exit(1);
}

function adapt(path, key) {
  let text = readFileSync(path, "utf8");
  for (const rule of GLOBAL_RULES) {
    text = text.replace(rule.pattern, rule.replace);
  }
  for (const rule of FILE_RULES[key] ?? []) {
    const next = text.replace(rule.pattern, rule.replace);
    rule.applied ||= next !== text;
    text = next;
  }
  writeFileSync(path, text);
}

function markdownFiles(dir) {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    return entry.isDirectory() ? markdownFiles(path) : entry.name.endsWith(".md") ? [path] : [];
  });
}
