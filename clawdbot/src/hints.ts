import { createHash } from "crypto";
import { statSync } from "fs";
import { isAbsolute, resolve } from "path";
import type { OpenClawPluginApi } from "openclaw/plugin-sdk/plugin-entry";
import type { RqlHostManager } from "./runtime/host.js";
import type { CallIdentity, RqlGrpcClient, SurfacedConcepts } from "./runtime/rqlGrpcClient.js";
import type { Logger } from "./runtime/types.js";
import { PLUGIN_VERSION } from "./version.js";

// Repository memory, delivered where the agent is already looking. The Claude
// Code plugin does this with PreToolUse/PostToolUse hooks; OpenClaw's seam is
// tool-result middleware, which rewrites a tool's result before the model
// reads it. The host owns matching, ranking, and once-per-session suppression
// — this file only forwards what was read or written and appends the answer.
//
// The host marks a concept or term as shown the moment it hands it out. So the
// rule here is: ask only on a path that will deliver the answer. A hint that is
// fetched and then dropped is hidden for the rest of the session.
//
// Where a hint can land depends on the runtime:
//   - OpenClaw's embedded runtime runs read / write / edit / apply_patch / exec
//     itself, so middleware appends the hint to the result the model reads.
//   - The Codex runtime owns its native tools (it reads through exec and
//     writes with apply_patch); OpenClaw may observe them but discards any
//     rewrite. Concepts for those writes are held and delivered by
//     before_agent_finalize — asked for only when that hook can run. Native
//     reads get no vocabulary; repoql_read carries it on every runtime.
//   - RepoQL's own repoql_read adds its hint itself (withVocabularyHint),
//     where workspace and session are always known.
//
// Hints are best effort: they adopt a host that is already serving and never
// launch one, share one short time budget per tool result, and on any failure
// leave the tool result as it was.

type GetHost = (workspaceDir?: string) => RqlHostManager;

const READ_TOOLS: readonly [string, ...string[]] = ["read", "exec"];
const WRITE_TOOLS: readonly [string, ...string[]] = ["write", "edit", "apply_patch"];

/** The whole time one tool result may wait for its hint, connection included. */
const HINT_BUDGET_MS = 3_000;
const MAX_CONCEPTS = 5;
const MAX_WRITE_TARGETS = 8;
const VOCABULARY_LIMIT = 5;
const VOCABULARY_MAX_CHARS = 2_000;
/** The host accepts 131072 UTF-16 characters of read content. */
const MAX_READ_CONTENT = 131_072;

const INLINE_CONCEPT_HEADER =
  "RepoQL concepts that govern the file(s) just changed — check the change keeps these invariants:";
const FINALIZE_CONCEPT_HEADER =
  "Before finishing: RepoQL concepts govern files you changed in this turn. Check your changes keep these " +
  "invariants — fix any change that breaks one, or say why it is acceptable.";

/** Commands whose output is one file's content. */
const FILE_READERS = new Set(["cat", "nl", "head", "tail", "sed", "less", "more", "bat"]);

type Middleware = Parameters<OpenClawPluginApi["registerAgentToolResultMiddleware"]>[0];
type MiddlewareEvent = Parameters<Middleware>[0];
type MiddlewareContext = Parameters<Middleware>[1];
export type ToolResult = MiddlewareEvent["result"];
type ToolArgs = Record<string, unknown>;

export function registerRepoQlHints(api: OpenClawPluginApi, getHost: GetHost, logger: Logger): void {
  if (typeof api.registerAgentToolResultMiddleware !== "function" || typeof api.on !== "function") {
    throw new Error("this OpenClaw has no tool-result middleware; update OpenClaw to enable them");
  }

  // Reads: only the embedded runtime can carry a hint on a native read.
  api.registerAgentToolResultMiddleware(
    async (event, ctx) => {
      const identity = identityOf(event, ctx);
      if (!identity) {
        return;
      }
      const target = event.toolName === "exec" ? shellReadTarget(event.args, event.cwd!) : readTarget(event.args, event.cwd!);
      if (!target) {
        return;
      }
      const text = await bestEffort(logger, event.toolName, () =>
        surfaceVocabulary(getHost(event.cwd), target, resultText(event.result), identity, deadlineIn(HINT_BUDGET_MS))
      );
      return text ? { result: append(event.result, text) } : undefined;
    },
    { runtimes: ["openclaw"], matcher: READ_TOOLS }
  );

  // Writes: inline on the embedded runtime; held for finalize on Codex.
  api.registerAgentToolResultMiddleware(
    async (event, ctx) => {
      const identity = identityOf(event, ctx);
      if (!identity) {
        return;
      }
      if (ctx.runtime === "codex") {
        // Without the finalize hook nothing would deliver them, so do not ask.
        if (!finalizeHookAllowed(api)) {
          return;
        }
        const targets = codexPatchTargets(event.toolName, nativeResponseText(event.result), event.cwd!);
        const sections = await bestEffort(logger, event.toolName, () =>
          conceptSections(getHost(event.cwd), targets, identity, deadlineIn(HINT_BUDGET_MS))
        );
        if (sections) {
          heldConcepts.add(identity.sessionId!, ctx.runId, sections);
        }
        return;
      }
      const sections = await bestEffort(logger, event.toolName, () =>
        conceptSections(getHost(event.cwd), writeTargets(event.args, event.cwd!), identity, deadlineIn(HINT_BUDGET_MS))
      );
      return sections ? { result: append(event.result, `${INLINE_CONCEPT_HEADER}\n\n${sections}`) } : undefined;
    },
    { runtimes: ["openclaw", "codex"], matcher: WRITE_TOOLS }
  );

  api.on("before_agent_finalize", (event) => {
    const sections = heldConcepts.take(event.sessionId ?? event.sessionKey, event.runId);
    if (!sections) {
      return;
    }
    const instruction = `${FINALIZE_CONCEPT_HEADER}\n\n${sections}`;
    return {
      action: "revise",
      reason: instruction,
      // OpenClaw counts retries per run and key; a key per batch lets a later
      // batch in the same run revise again instead of being turned into continue.
      retry: { instruction, idempotencyKey: `repoql-concepts:${digest(sections)}`, maxAttempts: 1 },
    };
  });
}

/** repoql_read's own hint: the vocabulary defined in what it just returned. */
export async function withVocabularyHint(
  result: ToolResult,
  host: RqlHostManager,
  args: ToolArgs,
  sessionId: string | undefined,
  logger: Logger
): Promise<ToolResult> {
  // A read through an uplink returns another repository's text; this host's
  // vocabulary does not describe it, and would be spent on it.
  if (args.uplink) {
    return result;
  }
  const target = stripAddressing(firstString(args.uriGlob, args.uri));
  if (!sessionId || !target) {
    return result;
  }
  const text = await bestEffort(logger, "repoql_read", () =>
    surfaceVocabulary(host, target, resultText(result), identityFor(sessionId), deadlineIn(HINT_BUDGET_MS))
  );
  return text ? append(result, text) : result;
}

// --- Delivery --------------------------------------------------------------

async function surfaceVocabulary(
  host: RqlHostManager,
  target: string,
  content: string,
  identity: CallIdentity,
  deadline: Deadline
): Promise<string> {
  const delivered = content.slice(0, MAX_READ_CONTENT);
  if (!delivered.trim()) {
    return "";
  }
  const client = await host.runningClient(deadline.remaining());
  if (!client || deadline.remaining() <= 0) {
    return "";
  }
  const text = await client.surfaceVocabulary(
    target,
    delivered,
    VOCABULARY_LIMIT,
    VOCABULARY_MAX_CHARS,
    identity,
    deadline.remaining()
  );
  return text.trim();
}

/**
 * The concept sections for the files a change left behind, fetched one target
 * at a time so the limit stays exact — the host marks every concept it returns
 * as shown, so over-fetching would hide concepts nobody delivers. A target that
 * fails is skipped; what earlier targets returned is kept.
 */
async function conceptSections(
  host: RqlHostManager,
  targets: string[],
  identity: CallIdentity,
  deadline: Deadline
): Promise<string> {
  if (targets.length === 0) {
    return "";
  }
  const client = await host.runningClient(deadline.remaining());
  if (!client) {
    return "";
  }
  const sections: string[] = [];
  let remaining = MAX_CONCEPTS;
  for (const target of targets.slice(0, MAX_WRITE_TARGETS)) {
    if (remaining <= 0 || deadline.remaining() <= 0) {
      break;
    }
    const surfaced = await surfaceConceptsFor(client, target, remaining, identity, deadline);
    if (surfaced && surfaced.rendered.trim()) {
      sections.push(surfaced.rendered.trim());
      remaining -= Math.max(1, surfaced.count);
    }
  }
  return sections.join("\n\n");
}

async function surfaceConceptsFor(
  client: RqlGrpcClient,
  target: string,
  limit: number,
  identity: CallIdentity,
  deadline: Deadline
): Promise<SurfacedConcepts | null> {
  try {
    return await client.surfaceConcepts(target, limit, identity, deadline.remaining());
  } catch {
    return null;
  }
}

type HeldEntry = { runId?: string; sections: string; at: number };

/**
 * Concept sections held from Codex-native writes until the turn tries to
 * finish. OpenClaw can load this module more than once in one process — the
 * middleware and the finalize hook have been observed in different module
 * instances — so the holding lives on a process-wide symbol. Entries are
 * tagged with their run, so an aborted turn's concepts never reach the next
 * turn, and they expire and are capped so nothing grows without bound.
 */
export class HeldConcepts {
  private static readonly TTL_MS = 60 * 60_000;
  private static readonly MAX_SESSIONS = 256;

  constructor(private readonly bySession: Map<string, HeldEntry[]>) {}

  add(sessionId: string, runId: string | undefined, sections: string): void {
    const now = Date.now();
    const held = (this.bySession.get(sessionId) ?? []).filter((entry) => now - entry.at < HeldConcepts.TTL_MS);
    held.push({ runId, sections, at: now });
    this.bySession.delete(sessionId);
    this.bySession.set(sessionId, held);
    while (this.bySession.size > HeldConcepts.MAX_SESSIONS) {
      this.bySession.delete(this.bySession.keys().next().value!);
    }
  }

  /** This run's held sections, clearing everything held for the session. */
  take(sessionId: string | undefined, runId: string | undefined): string | undefined {
    if (!sessionId) {
      return undefined;
    }
    const held = this.bySession.get(sessionId) ?? [];
    this.bySession.delete(sessionId);
    const now = Date.now();
    const current = held.filter((entry) => entry.runId === runId && now - entry.at < HeldConcepts.TTL_MS);
    return current.length ? current.map((entry) => entry.sections).join("\n\n") : undefined;
  }
}

const HELD_CONCEPTS = Symbol.for("repoql.openclaw.heldConcepts");
const heldConcepts = new HeldConcepts(
  ((globalThis as Record<symbol, unknown>)[HELD_CONCEPTS] ??= new Map<string, HeldEntry[]>()) as Map<string, HeldEntry[]>
);

/**
 * Whether OpenClaw will run this plugin's before_agent_finalize hook. For an
 * installed plugin it is blocked, with only a warning, unless the operator
 * allowed conversation access.
 */
function finalizeHookAllowed(api: OpenClawPluginApi): boolean {
  const config = api.config as { plugins?: { entries?: Record<string, { hooks?: { allowConversationAccess?: unknown } }> } };
  return config?.plugins?.entries?.[api.id ?? "repoql"]?.hooks?.allowConversationAccess === true;
}

// --- Targets ---------------------------------------------------------------

/** The file a read tool addressed, as a literal host target. */
export function readTarget(args: ToolArgs, cwd: string): string {
  const path = firstString(args.path, args.file_path, args.filePath);
  return path ? literalTarget(resolve(cwd, path)) : "";
}

/**
 * The file a shell command printed, when the command is a plain read of one
 * file: `cat f`, `sed -n '1,80p' f`, `nl -ba f | sed -n '1,40p'`. Later pipeline
 * stages may only number lines or cut a line range; anything that transforms
 * the content, reads several files, or chains commands has no single target.
 */
export function shellReadTarget(args: ToolArgs, cwd: string): string {
  const stages = pipelineStages(firstString(args.command, args.cmd));
  if (!stages || stages.length === 0 || !stages.slice(1).every(isLineCutter)) {
    return "";
  }
  const operand = singleFileOperand(stages[0]);
  if (!operand) {
    return "";
  }
  const workdir = resolve(cwd, firstString(args.workdir) || ".");
  const path = isAbsolute(operand) ? operand : resolve(workdir, operand);
  return isFile(path) ? literalTarget(path) : "";
}

/** Every file a write, edit, or embedded apply_patch leaves behind, as literal host targets. */
export function writeTargets(args: ToolArgs, cwd: string): string[] {
  const direct = firstString(args.path, args.file_path, args.filePath);
  if (direct) {
    return [literalTarget(resolve(cwd, direct))];
  }
  const patch = firstString(args.input, args.patch, args.command);
  const paths = new Set<string>();
  for (const match of patch.matchAll(/^\*\*\* (?:Add File|Update File|Move to): (.+)$/gm)) {
    paths.add(literalTarget(resolve(cwd, match[1].trim())));
  }
  return [...paths];
}

/**
 * The files a Codex apply_patch actually added or modified, from its own
 * report. The relay carries no error flag, so a patch that did not apply —
 * no "Updated the following files" list — has no targets.
 */
export function codexPatchTargets(toolName: string, output: string, cwd: string): string[] {
  if (toolName !== "apply_patch") {
    return [];
  }
  const list = output.split(/Updated the following files:[ \t]*\r?\n/)[1];
  if (list === undefined) {
    return [];
  }
  const paths = new Set<string>();
  for (const line of list.split(/\r?\n/)) {
    const match = /^\s*([AMD])\s+(.+?)\s*$/.exec(line);
    if (!match) {
      if (line.trim()) {
        break;
      }
      continue;
    }
    if (match[1] !== "D") {
      paths.add(literalTarget(resolve(cwd, match[2])));
    }
  }
  return [...paths];
}

/**
 * A filesystem path as a literal URI target: the host reads targets as URI
 * globs, so glob metacharacters in a real path (`app/[slug]/page.tsx`) are
 * percent-encoded. A literal `%` is left as it is; the host takes it as-is.
 */
export function literalTarget(path: string): string {
  return path.replace(/[*?[{;]/g, (char) => `%${char.charCodeAt(0).toString(16).toUpperCase()}`);
}

/** A read address without its modifier or fragment: what was read, not how. */
function stripAddressing(address: string): string {
  return address.split(" =>")[0].split("#")[0].trim();
}

// --- Shell parsing ---------------------------------------------------------

/**
 * The words of each pipeline stage, honouring single and double quotes; null
 * for anything but a plain pipeline — a chain, a redirect, a substitution, an
 * expansion, an escape, or unbalanced quotes.
 */
export function pipelineStages(command: string): string[][] | null {
  const stages: string[][] = [[]];
  let word = "";
  let inWord = false;
  let quote: "'" | '"' | null = null;
  const endWord = (): void => {
    if (inWord) {
      stages[stages.length - 1].push(word);
      word = "";
      inWord = false;
    }
  };
  for (const char of command) {
    if (quote) {
      if (char === quote) {
        quote = null;
      } else if (quote === '"' && "$`\\".includes(char)) {
        return null;
      } else {
        word += char;
      }
    } else if (char === "'" || char === '"') {
      quote = char;
      inWord = true;
    } else if (/\s/.test(char)) {
      endWord();
    } else if (char === "|") {
      endWord();
      stages.push([]);
    } else if (";&<>`$()\\".includes(char)) {
      return null;
    } else {
      word += char;
      inWord = true;
    }
  }
  if (quote) {
    return null;
  }
  endWord();
  return stages.some((stage) => stage.length === 0) ? null : stages;
}

/** The one file a reader stage reads, or "" when it reads none or several. */
function singleFileOperand(stage: string[]): string {
  const [command, ...rest] = stage;
  if (!FILE_READERS.has(command)) {
    return "";
  }
  const operands: string[] = [];
  let scriptGiven = false;
  for (let i = 0; i < rest.length; i++) {
    const word = rest[i];
    if (command === "sed" && (word === "-e" || word === "--expression" || word === "-f")) {
      scriptGiven = true;
      i++;
    } else if ((command === "head" || command === "tail") && (word === "-n" || word === "-c")) {
      i++;
    } else if (!word.startsWith("-")) {
      operands.push(word);
    }
  }
  if (command === "sed" && !scriptGiven) {
    operands.shift(); // sed's first operand is its script.
  }
  return operands.length === 1 ? operands[0] : "";
}

/** A later pipeline stage that only numbers lines or keeps a line range. */
function isLineCutter(stage: string[]): boolean {
  const [command, ...rest] = stage;
  if (command === "nl") {
    return rest.every((word) => word.startsWith("-"));
  }
  if (command === "head" || command === "tail") {
    for (let i = 0; i < rest.length; i++) {
      if (rest[i] === "-n" && /^\+?\d+$/.test(rest[i + 1] ?? "")) {
        i++;
      } else if (!/^-(n\+?)?\d+$/.test(rest[i])) {
        return false;
      }
    }
    return true;
  }
  if (command === "sed") {
    return rest.length === 2 && rest[0] === "-n" && /^(\d+|\$)(,(\d+|\$))?p$/.test(rest[1]);
  }
  return false;
}

// --- Results ---------------------------------------------------------------

/** The model-visible text of a tool result: its text content blocks. */
export function resultText(result: ToolResult): string {
  const content = (result as { content?: unknown })?.content;
  if (!Array.isArray(content)) {
    return "";
  }
  return content
    .filter((block): block is { type: "text"; text: string } => block?.type === "text" && typeof block.text === "string")
    .map((block) => block.text)
    .join("\n");
}

/**
 * The text of a relayed Codex-native tool response. The relay keeps the raw
 * response in `details` and renders an object response as JSON in `content`,
 * so read the raw strings — a JSON-escaped newline would hide the report's lines.
 */
export function nativeResponseText(result: ToolResult): string {
  const raw = (result as { details?: unknown })?.details;
  const strings: string[] = [];
  const collect = (value: unknown, depth: number): void => {
    if (typeof value === "string") {
      strings.push(value);
    } else if (depth < 4 && value && typeof value === "object") {
      for (const item of Array.isArray(value) ? value : Object.values(value)) {
        collect(item, depth + 1);
      }
    }
  };
  collect(raw, 0);
  return strings.length ? strings.join("\n") : resultText(result);
}

/** The same result with one more text block; details and other blocks are kept as they were. */
export function append(result: ToolResult, text: string): ToolResult {
  const content = Array.isArray((result as { content?: unknown })?.content) ? (result as any).content : [];
  return { ...(result as object), content: [...content, { type: "text", text }] } as ToolResult;
}

// --- Plumbing --------------------------------------------------------------

interface Deadline {
  remaining(): number;
}

function deadlineIn(ms: number): Deadline {
  const at = Date.now() + ms;
  return { remaining: () => Math.max(0, at - Date.now()) };
}

/** The call identity for a tool result, or null when it cannot carry a hint. */
function identityOf(event: MiddlewareEvent, ctx: MiddlewareContext): CallIdentity | null {
  const sessionId = ctx.sessionId ?? ctx.sessionKey;
  return event.isError || !sessionId || !event.cwd ? null : identityFor(sessionId);
}

function identityFor(sessionId: string): CallIdentity {
  return { agent: `openclaw-repoql/${PLUGIN_VERSION}`, sessionId };
}

async function bestEffort(logger: Logger, tool: string, work: () => Promise<string>): Promise<string> {
  try {
    return await work();
  } catch (err) {
    logger.debug?.(`RepoQL ${tool} hint skipped: ${err instanceof Error ? err.message : String(err)}`);
    return "";
  }
}

function digest(text: string): string {
  return createHash("sha256").update(text).digest("hex").slice(0, 16);
}

function isFile(path: string): boolean {
  try {
    return statSync(path).isFile();
  } catch {
    return false;
  }
}

function firstString(...values: unknown[]): string {
  for (const value of values) {
    if (typeof value === "string" && value.trim()) {
      return value.trim();
    }
  }
  return "";
}
