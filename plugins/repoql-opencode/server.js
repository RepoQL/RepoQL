// RepoQL plugin for OpenCode 2: registers RepoQL's MCP server and its skills.
//
// OpenCode loads this module's default export through its Promise plugin API
// ({ id, setup }). It imports nothing outside Node's standard library, so it
// needs no build step and no installed dependencies.

import { existsSync, readdirSync, readFileSync, statSync } from "node:fs"
import { homedir } from "node:os"
import path from "node:path"
import { fileURLToPath } from "node:url"

const SERVER_NAME = "repoql"
const SKILLS_DIR = path.join(path.dirname(fileURLToPath(import.meta.url)), "skills")

// The first rql on PATH, then where the hosted installers put it. OpenCode can be
// started by a desktop app with a minimal PATH, so the well-known directories matter.
export function findRql(env = process.env, platform = process.platform, home = homedir()) {
  const windows = platform === "win32"
  const names = windows ? ["rql.exe", "rql.cmd", "rql"] : ["rql"]
  const separator = windows ? ";" : ":"
  const directories = (env.PATH ?? env.Path ?? "").split(separator).filter(Boolean)
  directories.push(path.join(home, ".local", "bin"))
  if (windows && env.LOCALAPPDATA) directories.push(path.join(env.LOCALAPPDATA, "rql"))

  for (const directory of directories) {
    for (const name of names) {
      const candidate = path.join(directory, name)
      if (isFile(candidate)) return candidate
    }
  }
  return undefined
}

// The frontmatter fields OpenCode reads from a SKILL.md, and the body after it.
export function parseSkill(text) {
  const match = /^---\r?\n([\s\S]*?)\r?\n---\r?\n?([\s\S]*)$/.exec(text)
  if (!match) return undefined
  const fields = {}
  for (const line of match[1].split(/\r?\n/)) {
    const field = /^([A-Za-z][\w-]*):\s*(.*)$/.exec(line)
    if (field) fields[field[1]] = scalar(field[2].trim())
  }
  return { name: fields.name, description: fields.description, content: match[2] }
}

function scalar(value) {
  if (value.startsWith('"')) {
    try {
      return JSON.parse(value)
    } catch {
      return value
    }
  }
  if (value.startsWith("'") && value.endsWith("'")) return value.slice(1, -1).replaceAll("''", "'")
  return value
}

// One skill per <skill>/SKILL.md, named the way OpenCode names a skill directory.
export function loadSkills(directory = SKILLS_DIR) {
  if (!existsSync(directory)) return []
  const skills = []
  for (const id of readdirSync(directory).sort()) {
    const file = path.join(directory, id, "SKILL.md")
    if (!isFile(file)) continue
    const parsed = parseSkill(readFileSync(file, "utf8"))
    if (!parsed) continue
    skills.push({
      id,
      name: parsed.name ?? id,
      ...(parsed.description ? { description: parsed.description } : {}),
      path: file,
      content: parsed.content,
    })
  }
  return skills
}

function isFile(candidate) {
  try {
    return statSync(candidate).isFile()
  } catch {
    return false
  }
}

export default {
  id: "repoql",
  async setup(ctx) {
    const rql = findRql()
    if (rql) {
      await ctx.mcp.transform((editor) => {
        editor.set(SERVER_NAME, {
          type: "local",
          command: [rql, "mcp"],
          environment: { REPOQL_RECOVERY_HINT: "Read the troubleshooting-repoql skill." },
        })
      })
    } else {
      // A server whose command cannot start would only fail later and less clearly.
      console.warn(
        "RepoQL: rql was not found on PATH, in ~/.local/bin or in %LOCALAPPDATA%\\rql, so the RepoQL MCP server is not registered. " +
          "Install it from https://repoql.com, then restart OpenCode.",
      )
    }

    const skills = loadSkills()
    if (skills.length > 0) {
      await ctx.skill.transform((editor) => {
        // A skill the user installed under the same id is theirs to keep.
        for (const skill of skills) if (!editor.get(skill.id)) editor.add(skill)
      })
    }
  },
}
