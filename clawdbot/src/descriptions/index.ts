import { readFileSync } from "fs";
import { dirname, resolve } from "path";
import { fileURLToPath } from "url";

// Tool descriptions are vendored verbatim from the RepoQL MCP server's embedded
// instruction resources so the OpenClaw plugin presents the same guidance an
// agent gets over MCP. They ship as markdown beside the compiled module and are
// read once at load. Keep them in sync with
// src/L3/RepoQL.Hosting.Mcp/Resources/*-instructions.md in RepoQL.Core.
const DIR = dirname(fileURLToPath(import.meta.url));

function load(name: string): string {
  return readFileSync(resolve(DIR, `${name}.md`), "utf8").trim();
}

export const descriptions = {
  query: load("query"),
  read: load("read"),
  explore: load("explore"),
  explain: load("explain"),
  execute: load("execute"),
  command: load("command"),
  keywords: load("keywords"),
  captureConcept: load("capture-concept"),
  import: load("import"),
  watch: load("watch"),
} as const;
