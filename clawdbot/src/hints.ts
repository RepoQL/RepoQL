import { statSync } from "fs";
import { isAbsolute, resolve } from "path";
import type { OpenClawPluginApi } from "openclaw/plugin-sdk/plugin-entry";
import type { RqlHostManager } from "./runtime/host.js";
import type { CallIdentity } from "./runtime/rqlGrpcClient.js";
import type { Logger } from "./runtime/types.js";
import { PLUGIN_VERSION } from "./version.js";

// Repository memory, delivered where the agent is already looking. The Claude
// Code plugin does this with PreToolUse/PostToolUse hooks; OpenClaw's seam is
// tool-result middleware, which rewrites a tool's result before the model
// reads it. The host owns matching, ranking, and once-per-session suppression
// — this file only forwards what was read or written and appends the answer.
//
// Where a hint can land depends on the runtime:
//   - OpenClaw's embedded runtime runs read / write / edit / apply_patch / exec
//     itself, so middleware appends the hint to the result the model reads.
//   - The Codex runtime owns its native tools (it reads through exec and
//     writes with apply_patch); OpenClaw may observe them but cannot rewrite
//     what Codex feeds its model. Concepts for those writes are held and
//     delivered by before_agent_finalize, which asks for one more pass before
//     the turn ends. Vocabulary for native reads is left to repoql_read.
//   - RepoQL's own repoql_read is an OpenClaw tool on every runtime and adds
//     its hint itself (withVocabularyHint), where workspace and session are known.
//
// Hints are best effort: they adopt a host that is already serving, never
// launch one, and any failure returns the tool result untouched.

type GetHost = (workspaceDir?: string) => RqlHostManager;

/** The runtimes whose tool results the plugin enriches (manifest contracts.agentToolResultMiddleware). */
const RUNTIMES = ["openclaw", "codex"] as const;

const READ_TOOLS: readonly [string, ...string[]] = ["read", "exec"];
const WRITE_TOOLS: readonly [string, ...string[]] = ["write", "edit", "apply_patch"];

const HINT_TIMEOUT_MS = 3_000;
const MAX_CONCEPTS = 5;
const MAX_WRITE_TARGETS = 8;
const VOCABULARY_LIMIT = 5;
const VOCABULARY_MAX_CHARS = 2_000;
/** The host accepts 131072 UTF-16 characters of read content. */
const MAX_READ_CONTENT = 131_072;

/** Shell commands whose output is a file's content, so the file is the read's target. */
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
  api.registerAgentToolResultMiddleware(
    (event, ctx) => {
      // Codex owns its native tools' records, so a rewrite never reaches the
      // model — and asking the host would mark the terms as shown, hiding them
      // from the next read that could deliver them. Leave those reads alone.
      if (ctx.runtime === "codex") {
        return;
      }
      return enrich(event, ctx, getHost, logger, vocabularyHint);
    },
    { runtimes: [...RUNTIMES], matcher: READ_TOOLS }
  );

  api.registerAgentToolResultMiddleware(
    async (event, ctx) => {
      if (ctx.runtime !== "codex") {
        return enrich(event, ctx, getHost, logger, conceptHint);
      }
      // Codex-native writes cannot carry the concepts inline; hold them for
      // the finalize gate below, which gives the agent one pass to check them.
      const sessionId = ctx.sessionId ?? ctx.sessionKey;
      const hint = await enrichText(event, ctx, getHost, logger, conceptHint);
      if (sessionId && hint) {
        guardrails.add(sessionId, hint);
      }
    },
    { runtimes: [...RUNTIMES], matcher: WRITE_TOOLS }
  );

  api.on("before_agent_finalize", (event) => {
    const pending = guardrails.take(event.sessionId);
    if (!pending) {
      return;
    }
    const instruction =
      "Before finishing: RepoQL concepts govern files you changed in this turn. Check your changes keep these " +
      "invariants — fix any change that breaks one, or say why it is acceptable.\n\n" +
      pending;
    return {
      action: "revise",
      reason: instruction,
      retry: { instruction, idempotencyKey: "repoql-concepts", maxAttempts: 1 },
    };
  });
}

/**
 * Concept hints computed during a Codex turn, held until the turn tries to
 * finish. OpenClaw can load this module more than once in one process — the
 * middleware and the finalize hook have been observed in different module
 * instances — so the holding lives on a process-wide symbol, keyed by session.
 */
class PendingGuardrails {
  private readonly bySession: Map<string, string[]>;

  constructor(store: Map<string, string[]>) {
    this.bySession = store;
  }

  add(sessionId: string, hint: string): void {
    const pending = this.bySession.get(sessionId) ?? [];
    pending.push(hint.replace(/^RepoQL concepts that govern the file\(s\) just changed[^\n]*\n\n/, ""));
    this.bySession.set(sessionId, pending);
  }

  take(sessionId: string): string | undefined {
    const pending = this.bySession.get(sessionId);
    this.bySession.delete(sessionId);
    return pending?.length ? pending.join("\n\n") : undefined;
  }
}

const GUARDRAIL_STORE = Symbol.for("repoql.openclaw.pendingConceptGuardrails");
const guardrails = new PendingGuardrails(
  ((globalThis as Record<symbol, unknown>)[GUARDRAIL_STORE] ??= new Map<string, string[]>()) as Map<string, string[]>
);

/** repoql_read's own hint: the vocabulary defined in what it just returned. */
export async function withVocabularyHint(
  result: ToolResult,
  host: RqlHostManager,
  args: ToolArgs,
  sessionId: string | undefined,
  logger: Logger
): Promise<ToolResult> {
  const target = stripAddressing(firstString(args.uriGlob, args.uri));
  if (!sessionId || !target) {
    return result;
  }
  try {
    const text = (await surfaceVocabulary(host, target, resultText(result), identityFor(sessionId))).trim();
    return text ? append(result, text) : result;
  } catch (err) {
    logger.debug?.(`RepoQL read hint skipped: ${errorMessage(err)}`);
    return result;
  }
}

type HintSource = (event: MiddlewareEvent, host: RqlHostManager, identity: CallIdentity) => Promise<string>;

async function enrich(
  event: MiddlewareEvent,
  ctx: MiddlewareContext,
  getHost: GetHost,
  logger: Logger,
  hint: HintSource
): Promise<{ result: ToolResult } | void> {
  const text = await enrichText(event, ctx, getHost, logger, hint);
  return text ? { result: append(event.result, text) } : undefined;
}

/** The hint for one tool result, or empty when there is none or it could not be had. */
async function enrichText(
  event: MiddlewareEvent,
  ctx: MiddlewareContext,
  getHost: GetHost,
  logger: Logger,
  hint: HintSource
): Promise<string> {
  const sessionId = ctx.sessionId ?? ctx.sessionKey;
  if (event.isError || !sessionId || !event.cwd) {
    return "";
  }
  try {
    return (await hint(event, getHost(event.cwd), identityFor(sessionId))).trim();
  } catch (err) {
    logger.debug?.(`RepoQL ${event.toolName} hint skipped: ${errorMessage(err)}`);
    return "";
  }
}

/** Repository vocabulary defined in the file the agent just read. */
function vocabularyHint(event: MiddlewareEvent, host: RqlHostManager, identity: CallIdentity): Promise<string> {
  const target = event.toolName === "exec" ? shellReadTarget(event.args, event.cwd!) : readTarget(event.args, event.cwd!);
  return target ? surfaceVocabulary(host, target, resultText(event.result), identity) : Promise.resolve("");
}

async function surfaceVocabulary(host: RqlHostManager, target: string, content: string, identity: CallIdentity): Promise<string> {
  const delivered = content.slice(0, MAX_READ_CONTENT);
  if (!delivered.trim()) {
    return "";
  }
  const client = await host.runningClient(HINT_TIMEOUT_MS);
  if (!client) {
    return "";
  }
  return client.surfaceVocabulary(target, delivered, VOCABULARY_LIMIT, VOCABULARY_MAX_CHARS, identity, HINT_TIMEOUT_MS);
}

/**
 * The concept invariants governing each file the agent just changed. The host
 * built this surface as a pre-edit guardrail; OpenClaw offers no pre-call
 * context seam, so it arrives with the change's result — in time to correct it.
 */
async function conceptHint(event: MiddlewareEvent, host: RqlHostManager, identity: CallIdentity): Promise<string> {
  const targets = writeTargets(event.args, event.cwd!).slice(0, MAX_WRITE_TARGETS);
  if (targets.length === 0) {
    return "";
  }
  const client = await host.runningClient(HINT_TIMEOUT_MS);
  if (!client) {
    return "";
  }
  const sections: string[] = [];
  let remaining = MAX_CONCEPTS;
  for (const target of targets) {
    const rendered = (await client.surfaceConcepts(target, remaining, identity, HINT_TIMEOUT_MS)).trim();
    if (rendered) {
      sections.push(rendered);
      remaining -= countConcepts(rendered);
      if (remaining <= 0) {
        break;
      }
    }
  }
  if (sections.length === 0) {
    return "";
  }
  return (
    "RepoQL concepts that govern the file(s) just changed — check the change keeps these invariants:\n\n" +
    sections.join("\n\n")
  );
}

/** The file a read tool addressed, absolute against the tool's working directory. */
export function readTarget(args: ToolArgs, cwd: string): string {
  const path = firstString(args.path, args.file_path, args.filePath);
  return path ? resolve(cwd, path) : "";
}

/**
 * The file a shell command printed, when the command is a plain file read:
 * `cat f`, `sed -n '1,80p' f`, `nl -ba f | sed -n '1,40p'`. Anything else —
 * a search, a build, a pipeline that transforms the content — has no single
 * target, so it gets no hint.
 */
export function shellReadTarget(args: ToolArgs, cwd: string): string {
  const command = firstString(args.command, args.cmd);
  const first = command.split("|")[0];
  if (/[;&<>`$]/.test(first)) {
    return "";
  }
  const words = shellWords(first);
  if (!FILE_READERS.has(words[0] ?? "")) {
    return "";
  }
  for (const word of words.slice(1).reverse()) {
    if (word.startsWith("-")) {
      continue;
    }
    const path = isAbsolute(word) ? word : resolve(cwd, word);
    if (isFile(path)) {
      return path;
    }
  }
  return "";
}

/** Every file a write, edit, or patch leaves behind; a deleted file has nothing left to govern. */
export function writeTargets(args: ToolArgs, cwd: string): string[] {
  const direct = firstString(args.path, args.file_path, args.filePath);
  if (direct) {
    return [resolve(cwd, direct)];
  }
  // OpenClaw's apply_patch takes the patch as `input`; Codex's arrives as `command`.
  const patch = firstString(args.input, args.patch, args.command);
  const paths = new Set<string>();
  for (const match of patch.matchAll(/^\*\*\* (?:Add File|Update File|Move to): (.+)$/gm)) {
    paths.add(resolve(cwd, match[1].trim()));
  }
  return [...paths];
}

/** A read address without its modifier or fragment: what was read, not how. */
function stripAddressing(address: string): string {
  return address.split(" =>")[0].split("#")[0].trim();
}

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

/** The same result with one more text block; details and other blocks are kept as they were. */
export function append(result: ToolResult, text: string): ToolResult {
  const content = Array.isArray((result as { content?: unknown })?.content) ? (result as any).content : [];
  return { ...(result as object), content: [...content, { type: "text", text }] } as ToolResult;
}

/** The host renders one unindented "uri<TAB>invariant" line per concept; its why lines are indented. */
export function countConcepts(rendered: string): number {
  return Math.max(1, rendered.split("\n").filter((line) => line && !/^\s/.test(line)).length);
}

/** Split a command into words, honouring single and double quotes. */
export function shellWords(command: string): string[] {
  const words: string[] = [];
  for (const match of command.matchAll(/'([^']*)'|"([^"]*)"|(\S+)/g)) {
    words.push(match[1] ?? match[2] ?? match[3]);
  }
  return words;
}

function identityFor(sessionId: string): CallIdentity {
  return { agent: `openclaw-repoql/${PLUGIN_VERSION}`, sessionId };
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

function errorMessage(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}
