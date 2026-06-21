import { resolve } from "path";
import type { RepoQlPluginConfig } from "./config.js";
import { describeGrpcError, textResult, toolError, type ToolResult } from "./result.js";
import type { RqlHostManager } from "./runtime/host.js";
import type { LoginProgress, RqlGrpcClient } from "./runtime/rqlGrpcClient.js";
import { CommandSurface } from "./runtime/rqlGrpcClient.js";
import { describeInit, initializeWorkspace } from "./runtime/paths.js";

/** Browser login is human-paced; give it far longer than a normal RPC. */
const LOGIN_TIMEOUT_MS = 5 * 60 * 1000;

type Report = (text: string) => void;

export interface RunCommandOptions {
  host: RqlHostManager;
  config: RepoQlPluginConfig;
  command: string;
  signal?: AbortSignal;
  report?: Report;
}

/**
 * The `command` tool — one CLI-shaped surface that accepts the same words the
 * `rql` CLI does. account/host/init/dashboard are dispatched to the typed gRPC
 * services (and local workspace init); everything else falls through to the
 * host's ManagementCommandService. Mirrors RepoQL.Hosting.Mcp.CommandTool.
 */
export async function runCommand(opts: RunCommandOptions): Promise<ToolResult> {
  const { host, command, report } = opts;
  if (!command.trim()) {
    return textResult(topLevelHelp(), { tool: "command" });
  }

  const argv = tokenize(command);
  if (isTopLevelHelp(argv)) {
    return textResult(topLevelHelp(), { tool: "command" });
  }
  if (isHelpCommand(argv[0])) {
    return dispatchHelp(argv, host);
  }

  switch (argv[0].toLowerCase()) {
    case "account":
      return dispatchAccount(argv, host, report);
    case "host":
      return dispatchHost(argv, host, report);
    case "init":
      return dispatchInit(argv, host);
    case "dashboard":
      if (argv.length > 1 && isHelpFlag(argv[1])) {
        return textResult(dashboardHelp(), { tool: "command" });
      }
      return executeManagement(command, host);
    default:
      return executeManagement(command, host);
  }
}

// ---------------------------------------------------------------------------
// account
// ---------------------------------------------------------------------------

async function dispatchAccount(argv: string[], host: RqlHostManager, report?: Report): Promise<ToolResult> {
  if (argv.length < 2 || isHelpFlag(argv[1])) {
    return textResult(accountRootHelp(), { tool: "command" });
  }

  const sub = argv[1].toLowerCase();
  const wantsHelp = hasFlag(argv.slice(2), "help", "h");

  switch (sub) {
    case "whoami":
      return wantsHelp ? textResult(accountHelp("whoami"), { tool: "command" }) : accountWhoami(host);
    case "logout":
      return wantsHelp ? textResult(accountHelp("logout"), { tool: "command" }) : accountLogout(host);
    case "login":
      return wantsHelp ? textResult(accountHelp("login"), { tool: "command" }) : accountLogin(argv, host, report);
    default:
      return toolError(`Unknown subcommand 'account ${argv[1]}'. Try "account --help".`);
  }
}

async function accountWhoami(host: RqlHostManager): Promise<ToolResult> {
  const client = await getClientOrThrow(host);
  try {
    const response = await client.whoAmI();
    return textResult(String(response?.message ?? ""), { tool: "command" });
  } catch (err) {
    return toolError(describeGrpcError(err));
  }
}

async function accountLogout(host: RqlHostManager): Promise<ToolResult> {
  const client = await getClientOrThrow(host);
  try {
    const response = await client.logout();
    return textResult(String(response?.message ?? ""), { tool: "command" });
  } catch (err) {
    return toolError(describeGrpcError(err));
  }
}

async function accountLogin(argv: string[], host: RqlHostManager, report?: Report): Promise<ToolResult> {
  const mode = optionValue(argv, "mode");
  const deviceCode = parseLoginMode(mode);

  const client = await getClientOrThrow(host);
  const lines: string[] = [];
  let failed = false;

  try {
    await client.login(
      { deviceCode },
      (frame: LoginProgress) => {
        const line = frame.kind === 2 ? `Logged in as ${frame.displayName}` : frame.message;
        if (frame.kind === 3) {
          failed = true;
        }
        lines.push(line);
        report?.(line);
      },
      LOGIN_TIMEOUT_MS
    );
  } catch (err) {
    return toolError(describeGrpcError(err));
  }

  const output = lines.join("\n").trimEnd();
  return failed ? toolError(output || "Login failed.") : textResult(output, { tool: "command" });
}

/** Returns whether device-code mode was requested; throws on an unknown mode. */
function parseLoginMode(mode: string | undefined): boolean {
  if (!mode || !mode.trim()) {
    return false;
  }
  const trimmed = mode.trim();
  if (trimmed === "browser") {
    return false;
  }
  if (trimmed === "device-code") {
    return true;
  }
  return toolError(
    "Unknown login mode. Use account login, account login --mode browser, or account login --mode device-code."
  );
}

// ---------------------------------------------------------------------------
// host
// ---------------------------------------------------------------------------

async function dispatchHost(argv: string[], host: RqlHostManager, report?: Report): Promise<ToolResult> {
  if (argv.length < 2 || isHelpFlag(argv[1])) {
    return textResult(hostRootHelp(), { tool: "command" });
  }

  const sub = argv[1].toLowerCase();
  if (hasFlag(argv.slice(2), "help", "h")) {
    return textResult(hostHelp(sub), { tool: "command" });
  }

  switch (sub) {
    case "status":
      return hostStatus(host);
    case "stop":
      return hostStop(host, report);
    case "start":
      return hostStart(host, report);
    case "restart":
      return hostRestart(host, report);
    default:
      return toolError(`Unknown subcommand 'host ${argv[1]}'. Try "host --help".`);
  }
}

async function hostStatus(host: RqlHostManager): Promise<ToolResult> {
  if (!(await host.isReachable())) {
    return textResult("Host: not running.", { tool: "command" });
  }

  const client = host.connect();
  try {
    const update = await client.getStatus();
    return textResult(renderStatus(update), { tool: "command" });
  } catch (err) {
    return toolError(describeGrpcError(err));
  } finally {
    client.close();
  }
}

function renderStatus(u: any): string {
  const readiness = u?.ready
    ? "ready"
    : u?.searchable
      ? "searchable"
      : u?.queryable
        ? "queryable"
        : u?.initialized
          ? "initialized"
          : !u?.phase
            ? "starting"
            : String(u.phase);

  const phase = u?.phase ? String(u.phase) : "";
  const lines = [
    `Host: ${readiness}` + (!phase || readiness === phase ? "" : ` (${phase})`),
    `Files: ${num(u?.completeFiles)} complete, ${num(u?.indexedFiles)} indexed, ` +
      `${num(u?.structureEmbeddedFiles)} embedded, ${num(u?.failedFiles)} failed of ${num(u?.totalFiles)} total`,
  ];

  if (num(u?.queuedOperations) > 0) {
    lines.push(`Queued operations: ${num(u.queuedOperations)}`);
  }
  const inFlight = num(u?.activeFiles) + num(u?.dirtyFiles) + num(u?.discoveredFiles);
  if (inFlight > 0) {
    lines.push(`In flight: ${num(u?.activeFiles)} active, ${num(u?.dirtyFiles)} dirty, ${num(u?.discoveredFiles)} discovered`);
  }

  return lines.join("\n");
}

async function hostStop(host: RqlHostManager, report?: Report): Promise<ToolResult> {
  if (!(await host.isReachable())) {
    return textResult("Host is not running.", { tool: "command" });
  }

  report?.("Shutting down host...");

  const client = host.connect();
  let pid: number;
  try {
    const response = await client.shutdown();
    pid = num(response?.processId);
  } catch (err) {
    return toolError(describeGrpcError(err));
  } finally {
    client.close();
  }

  const outcome = await confirmExit(pid, report);
  const message = describeStopOutcome(pid, outcome);
  return outcome === "failedToExit" ? toolError(message) : textResult(message, { tool: "command" });
}

async function hostStart(host: RqlHostManager, report?: Report): Promise<ToolResult> {
  if (!(await host.isReachable())) {
    report?.("Launching host process...");
  }
  try {
    await host.ensureReady();
  } catch (err) {
    return toolError(describeGrpcError(err));
  }
  return textResult("Host running.", { tool: "command" });
}

async function hostRestart(host: RqlHostManager, report?: Report): Promise<ToolResult> {
  let stoppedPid = 0;

  report?.("Shutting down host...");
  if (await host.isReachable()) {
    const client = host.connect();
    try {
      const response = await client.shutdown();
      stoppedPid = num(response?.processId);
    } catch {
      // Unavailable mid-shutdown — treat as already gone.
      stoppedPid = 0;
    } finally {
      client.close();
    }
  }

  if (stoppedPid > 0) {
    report?.("Waiting for old host to exit...");
    const outcome = await confirmExit(stoppedPid, report);
    if (outcome === "failedToExit") {
      return toolError(
        `Host (pid ${stoppedPid}) is wedged and survived SIGKILL — refusing to launch a successor ` +
          "that would race it for the database lock. Manual intervention required."
      );
    }
  }

  report?.("Launching new host...");
  try {
    await host.ensureReady();
  } catch (err) {
    return toolError(describeGrpcError(err));
  }

  return textResult(
    stoppedPid > 0
      ? `Host restarted (old pid ${stoppedPid}).`
      : "Host started (previous instance was not running).",
    { tool: "command" }
  );
}

type StopOutcome = "stopped" | "killed" | "failedToExit" | "pidUnknown";

/**
 * Confirm the host process actually exited — polling the pid, not the socket,
 * because a closed socket with a live pid still holds the DuckDB file lock.
 * Escalate to SIGKILL on overrun.
 */
async function confirmExit(pid: number, report?: Report): Promise<StopOutcome> {
  if (!pid || pid <= 0) {
    return "pidUnknown";
  }
  if (await waitForExit(pid, 5_000)) {
    return "stopped";
  }

  report?.("Host did not exit gracefully; sending SIGKILL...");
  try {
    process.kill(pid, "SIGKILL");
  } catch {
    // Already gone between the check and the kill.
  }

  return (await waitForExit(pid, 3_000)) ? "killed" : "failedToExit";
}

function describeStopOutcome(pid: number, outcome: StopOutcome): string {
  switch (outcome) {
    case "killed":
      return `Host (pid ${pid}) did not exit gracefully — force-killed.`;
    case "failedToExit":
      return `Host (pid ${pid}) did not exit even after SIGKILL — manual intervention may be required.`;
    case "pidUnknown":
      return "Host stop requested, but the host did not report a process id — could not confirm exit.";
    default:
      return `Host stopped (pid ${pid}).`;
  }
}

async function waitForExit(pid: number, timeoutMs: number): Promise<boolean> {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    if (!isAlive(pid)) {
      return true;
    }
    await sleep(100);
  }
  return !isAlive(pid);
}

function isAlive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch (err) {
    // ESRCH = gone; EPERM = exists but not ours.
    return (err as NodeJS.ErrnoException).code === "EPERM";
  }
}

// ---------------------------------------------------------------------------
// init / dashboard / help
// ---------------------------------------------------------------------------

function dispatchInit(argv: string[], host: RqlHostManager): ToolResult {
  if (argv.length > 1 && isHelpFlag(argv[1])) {
    return textResult(initHelp(), { tool: "command" });
  }

  const pathArg = argv.length > 1 && !argv[1].startsWith("-") ? argv[1] : null;
  const target = pathArg ? resolve(pathArg) : host.repoRoot;

  try {
    return textResult(describeInit(initializeWorkspace(target)), { tool: "command" });
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err);
    return toolError(`Failed to initialize workspace at ${target}: ${message}`);
  }
}

async function dispatchHelp(argv: string[], host: RqlHostManager): Promise<ToolResult> {
  if (argv.length < 2) {
    return textResult(topLevelHelp(), { tool: "command" });
  }

  const target = argv.slice(1);
  switch (target[0].toLowerCase()) {
    case "account":
      return textResult(target.length === 1 ? accountRootHelp() : accountHelp(target[1]), { tool: "command" });
    case "host":
      return textResult(target.length === 1 ? hostRootHelp() : hostHelp(target[1]), { tool: "command" });
    case "dashboard":
      return textResult(dashboardHelp(), { tool: "command" });
    case "init":
      return textResult(initHelp(), { tool: "command" });
    default:
      return executeManagement(argv.join(" "), host);
  }
}

async function executeManagement(command: string, host: RqlHostManager): Promise<ToolResult> {
  const client = await getClientOrThrow(host);
  try {
    const response = await client.command({ command, surface: CommandSurface.Mcp });
    const body = response?.success || !nonEmpty(response?.error)
      ? String(response?.rendered ?? "")
      : String(response.error);
    return response?.success ? textResult(body, { tool: "command" }) : toolError(body || "Command failed.");
  } catch (err) {
    return toolError(describeGrpcError(err));
  }
}

// ---------------------------------------------------------------------------
// Help text — verbatim from RepoQL.Hosting.Mcp.CommandTool
// ---------------------------------------------------------------------------

function topLevelHelp(): string {
  return [
    "Available commands:",
    "  init [path]                   Designate a directory as a RepoQL workspace (creates .repoql).",
    "  account whoami                Show the current RepoQL cloud identity.",
    "  account login [--mode <mode>] Log in. Mode is browser or device-code.",
    "  account logout                Clear the locally stored session.",
    "  config list                   List settings.",
    "  config read --key <key>       Show one setting.",
    "  config set --key <key> --value <value>",
    "  import add --uri <uri>        Import a repository.",
    "  import list                   List imported repositories.",
    "  import remove --uri <uri>     Remove an imported repository.",
    "  diagnostics memory            Host memory and storage diagnostics.",
    "  host status                   Snapshot of host readiness, phase, and file counts.",
    "  host start                    Ensure the gRPC host is running.",
    "  host stop                     Shut down the gRPC host.",
    "  host restart                  Shut down and re-launch the gRPC host.",
    "  dashboard                     Open the live dashboard in the default browser.",
    "",
    'Append --help to any command for details (e.g. "account login --help").',
  ].join("\n");
}

function accountRootHelp(): string {
  return [
    "account — RepoQL cloud account commands.",
    "",
    "Subcommands:",
    "  whoami  Show the current cloud identity.",
    "  login   Log in to RepoQL cloud services.",
    "  logout  Clear the locally stored session.",
    "",
    'Use "account <subcommand> --help" for details.',
  ].join("\n");
}

function accountHelp(sub: string): string {
  switch (sub.toLowerCase()) {
    case "whoami":
      return "account whoami — Show the current RepoQL cloud identity (session holder or API-key fallback).";
    case "logout":
      return "account logout — Clear the locally stored OAuth session. Does not touch any configured API key.";
    case "login":
      return [
        "account login [--mode <mode>] — Log in to RepoQL cloud services.",
        "  Default: opens a local loopback listener and completes in the browser.",
        "  --mode device-code: returns a verification URL and code to complete on any device.",
        "  --mode browser: opens the browser flow explicitly.",
      ].join("\n");
    default:
      return `Unknown subcommand 'account ${sub}'. Try "account --help".`;
  }
}

function hostRootHelp(): string {
  return [
    "host — Control the long-running gRPC host process.",
    "",
    "Subcommands:",
    "  status   Snapshot of readiness, phase, and file counts.",
    "  start    Ensure the host is running (launches on demand).",
    "  stop     Shut down the host.",
    "  restart  Shut down and re-launch.",
    "",
    'Use "host <subcommand> --help" for details.',
  ].join("\n");
}

function hostHelp(sub: string): string {
  switch (sub.toLowerCase()) {
    case "status":
      return "host status — One-shot snapshot of host readiness, indexing phase, and file counts.";
    case "start":
      return "host start — Ensure the host is running. Launches it on demand if no instance is reachable.";
    case "stop":
      return "host stop — Shut down the host via gRPC. Reports the exiting process id.";
    case "restart":
      return "host restart — Shut down the current host, wait for exit, and re-launch a fresh instance.";
    default:
      return `Unknown subcommand 'host ${sub}'. Try "host --help".`;
  }
}

function dashboardHelp(): string {
  return (
    "dashboard — Open the live dashboard in the default browser.\n" +
    "  Reads the dashboard-bind.json written by the host's HTTP listener."
  );
}

function initHelp(): string {
  return (
    "init [path] — Designate a directory as a RepoQL workspace so RepoQL will index it.\n" +
    "  Creates a .repoql marker in the directory (defaults to the current directory).\n" +
    "  A git repository is already indexable and needs no marker."
  );
}

// ---------------------------------------------------------------------------
// Parsing helpers
// ---------------------------------------------------------------------------

/** Split a command line into tokens, honouring single and double quotes. */
function tokenize(input: string): string[] {
  const tokens: string[] = [];
  let current = "";
  let has = false;
  let quote: '"' | "'" | null = null;

  for (const ch of input) {
    if (quote) {
      if (ch === quote) {
        quote = null;
      } else {
        current += ch;
      }
      continue;
    }
    if (ch === '"' || ch === "'") {
      quote = ch;
      has = true;
    } else if (ch === " " || ch === "\t" || ch === "\n" || ch === "\r") {
      if (has) {
        tokens.push(current);
        current = "";
        has = false;
      }
    } else {
      current += ch;
      has = true;
    }
  }
  if (has) {
    tokens.push(current);
  }
  return tokens;
}

function isTopLevelHelp(argv: string[]): boolean {
  return argv.length === 1 && (isHelpCommand(argv[0]) || isHelpFlag(argv[0]));
}

function isHelpCommand(token: string): boolean {
  const lower = token.toLowerCase();
  return lower === "help" || lower === "?";
}

function isHelpFlag(token: string): boolean {
  const lower = token.toLowerCase();
  return lower === "--help" || lower === "-h";
}

function hasFlag(argv: string[], ...names: string[]): boolean {
  const flags = new Set(names.map((name) => (name.length === 1 ? `-${name}` : `--${name}`)));
  return argv.some((token) => flags.has(token.toLowerCase()));
}

function optionValue(argv: string[], name: string): string | undefined {
  const long = `--${name}`;
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === long) {
      return argv[i + 1];
    }
    if (argv[i].startsWith(`${long}=`)) {
      return argv[i].slice(long.length + 1);
    }
  }
  return undefined;
}

async function getClientOrThrow(host: RqlHostManager): Promise<RqlGrpcClient> {
  try {
    return await host.getClient();
  } catch (err) {
    return toolError(describeGrpcError(err));
  }
}

function nonEmpty(value: unknown): boolean {
  return typeof value === "string" && value.trim().length > 0;
}

function num(value: unknown): number {
  return typeof value === "number" && Number.isFinite(value) ? value : 0;
}

function sleep(ms: number): Promise<void> {
  return new Promise((done) => setTimeout(done, ms));
}
