import { resolve } from "path";
import { callCatalogTool } from "./catalog.js";
import type { RepoQlPluginConfig } from "./config.js";
import { describeGrpcError, textResult, toolError, type ToolResult } from "./result.js";
import type { RqlHostManager } from "./runtime/host.js";
import { describeInit, initializeWorkspace, readDashboardUrl } from "./runtime/paths.js";

type Report = (text: string) => void;

export interface RunCommandOptions {
  host: RqlHostManager;
  config: RepoQlPluginConfig;
  command: string;
  sessionId?: string;
  signal?: AbortSignal;
  report?: Report;
}

/**
 * The `command` tool. The host runs almost every command itself (config,
 * account, import, connector, diagnostics, …) and refuses the few that only
 * make sense beside the client: controlling the host process, designating a
 * workspace on disk, and finding the dashboard. Those three run here, as they
 * do in rql's own MCP adapter; everything else is forwarded verbatim.
 */
export async function runCommand(opts: RunCommandOptions): Promise<ToolResult> {
  const argv = tokenize(opts.command);
  const verb = argv[0]?.toLowerCase();
  const wantsHelp = argv.slice(1).some(isHelpFlag);

  switch (verb) {
    case "host":
      return wantsHelp || argv.length < 2 ? text(HOST_HELP) : dispatchHost(argv[1].toLowerCase(), opts);
    case "init":
      return wantsHelp ? text(INIT_HELP) : dispatchInit(argv, opts.host);
    case "dashboard":
      return wantsHelp ? text(DASHBOARD_HELP) : dispatchDashboard(opts.host);
    case undefined:
    case "help":
    case "--help":
    case "-h":
      if (argv.length > 1 && ["host", "init", "dashboard"].includes(argv[1].toLowerCase())) {
        return runCommand({ ...opts, command: `${argv[1]} --help` });
      }
      return withLocalHelp(await forward({ ...opts, command: opts.command.trim() || "help" }));
    default:
      return forward(opts);
  }
}

function forward(opts: RunCommandOptions): Promise<ToolResult> {
  return callCatalogTool({
    host: opts.host,
    config: opts.config,
    tool: "command",
    args: { command: opts.command },
    sessionId: opts.sessionId,
    signal: opts.signal,
    report: opts.report,
  });
}

/** Append the client-local commands to the host's own command list. */
function withLocalHelp(result: ToolResult): ToolResult {
  const body = result.content.map((block) => block.text).join("\n");
  return textResult(`${body}\n\nRun beside this client:\n${LOCAL_COMMANDS}`, result.details);
}

// ---------------------------------------------------------------------------
// host
// ---------------------------------------------------------------------------

async function dispatchHost(sub: string, opts: RunCommandOptions): Promise<ToolResult> {
  switch (sub) {
    case "status":
      return hostStatus(opts.host);
    case "start":
      return hostStart(opts.host, opts.report);
    case "stop":
      return hostStop(opts.host, opts.report);
    case "restart":
      return hostRestart(opts.host, opts.report);
    default:
      // host reindex / host reset and anything newer belong to the host.
      return forward(opts);
  }
}

async function hostStatus(host: RqlHostManager): Promise<ToolResult> {
  if (!(await host.isReachable())) {
    return text(`Host: not running for ${host.repoRoot}.`);
  }
  const client = host.connect();
  try {
    const [info, readiness] = await Promise.all([client.getHostInfo(), client.getReadiness()]);
    const level = readiness.level.replace("READINESS_LEVEL_", "").toLowerCase();
    const semantic = readiness.semanticEnabled
      ? `${readiness.semanticPercent}% embedded${readiness.semanticReady ? "" : " (not yet ready)"}`
      : "disabled";
    return text(
      [
        `Host: ${level} — rql ${info.version} (pid ${info.run?.pid ?? "?"}${info.implicitStart ? ", implicit start" : ""})`,
        `Workspace: ${info.workspaceRoot}`,
        `Files: ${readiness.totalFiles} total, ${readiness.pendingFiles} pending, ${readiness.failedFiles} failed`,
        `Semantic: ${semantic}`,
      ].join("\n")
    );
  } catch (err) {
    return toolError(describeGrpcError(err));
  } finally {
    client.close();
  }
}

async function hostStart(host: RqlHostManager, report?: Report): Promise<ToolResult> {
  if (!(await host.isReachable())) {
    report?.("Launching host process...");
  }
  try {
    await host.getClient();
  } catch (err) {
    return toolError(describeGrpcError(err));
  }
  return text("Host running.");
}

async function hostStop(host: RqlHostManager, report?: Report): Promise<ToolResult> {
  if (!(await host.isReachable())) {
    return text("Host is not running.");
  }
  report?.("Shutting down host...");
  const pid = await requestShutdown(host, "openclaw host stop");
  const outcome = await confirmExit(pid, report);
  const message = describeStopOutcome(pid, outcome);
  return outcome === "failedToExit" ? toolError(message) : text(message);
}

async function hostRestart(host: RqlHostManager, report?: Report): Promise<ToolResult> {
  report?.("Shutting down host...");
  const pid = (await host.isReachable()) ? await requestShutdown(host, "openclaw host restart") : 0;
  if (pid > 0) {
    report?.("Waiting for old host to exit...");
    if ((await confirmExit(pid, report)) === "failedToExit") {
      return toolError(
        `Host (pid ${pid}) survived SIGKILL — refusing to launch a successor that would race it for the ` +
          "database lock. Manual intervention required."
      );
    }
  }
  report?.("Launching new host...");
  try {
    await host.getClient();
  } catch (err) {
    return toolError(describeGrpcError(err));
  }
  return text(pid > 0 ? `Host restarted (old pid ${pid}).` : "Host started (previous instance was not running).");
}

/** Shutdown, returning the stopping pid (0 when unknown). Drops this plugin's lease first. */
async function requestShutdown(host: RqlHostManager, reason: string): Promise<number> {
  host.reset();
  const client = host.connect();
  try {
    const response = await client.shutdown(reason);
    return Number(response?.processId ?? 0);
  } catch {
    return 0; // Unavailable mid-shutdown — already going.
  } finally {
    client.close();
  }
}

type StopOutcome = "stopped" | "killed" | "failedToExit" | "pidUnknown";

/**
 * Confirm the host process actually exited — polling the pid, not the socket,
 * because a closed socket with a live pid still holds the database lock.
 * Escalate to SIGKILL on overrun.
 */
async function confirmExit(pid: number, report?: Report): Promise<StopOutcome> {
  if (!pid || pid <= 0) {
    return "pidUnknown";
  }
  if (await waitForExit(pid, 10_000)) {
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
    await new Promise((r) => setTimeout(r, 100));
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
// init / dashboard
// ---------------------------------------------------------------------------

function dispatchInit(argv: string[], host: RqlHostManager): ToolResult {
  const pathArg = argv.length > 1 && !argv[1].startsWith("-") ? argv[1] : null;
  const target = pathArg ? resolve(host.repoRoot, pathArg) : host.repoRoot;
  try {
    return text(describeInit(initializeWorkspace(target)));
  } catch (err) {
    return toolError(`Failed to initialize workspace at ${target}: ${err instanceof Error ? err.message : String(err)}`);
  }
}

function dispatchDashboard(host: RqlHostManager): ToolResult {
  const url = readDashboardUrl(host.repoRoot);
  return url
    ? text(`RepoQL dashboard: ${url}`)
    : toolError("No dashboard is published for this workspace yet. Start the host (`host start`), then retry.");
}

// ---------------------------------------------------------------------------
// Help and parsing
// ---------------------------------------------------------------------------

const LOCAL_COMMANDS = [
  "  host status         Host readiness, version, and file counts.",
  "  host start          Ensure the host is running (launches it on demand).",
  "  host stop           Shut down the host and confirm the process exited.",
  "  host restart        Shut down, wait for exit, and launch a fresh host.",
  "  init [path]         Designate a directory as a RepoQL workspace (creates .repoql/).",
  "  dashboard           Show the live dashboard URL.",
].join("\n");

const HOST_HELP = [
  "host — Control the long-running rql host process for this workspace.",
  LOCAL_COMMANDS.split("\n").slice(0, 4).join("\n"),
  '  host reindex --glob <glob> and host reset [--hard] run in the host itself.',
].join("\n");

const INIT_HELP =
  "init [path] — Designate a directory as a RepoQL workspace so RepoQL will index it.\n" +
  "  Creates .repoql/ there. Git repositories need no init; they are workspaces already.";

const DASHBOARD_HELP = "dashboard — Show the URL of the live dashboard the host's HTTP listener published.";

function text(body: string): ToolResult {
  return textResult(body, { tool: "command" });
}

function isHelpFlag(token: string): boolean {
  return token === "--help" || token === "-h";
}

/** Split a CLI-shaped command, honouring single and double quotes. */
function tokenize(command: string): string[] {
  const tokens: string[] = [];
  const pattern = /"([^"]*)"|'([^']*)'|(\S+)/g;
  for (const match of command.matchAll(pattern)) {
    tokens.push(match[1] ?? match[2] ?? match[3]);
  }
  return tokens;
}
