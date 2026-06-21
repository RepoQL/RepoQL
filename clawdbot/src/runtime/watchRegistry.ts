import { spawn, type ChildProcess } from "child_process";
import { createWriteStream, mkdirSync } from "fs";
import { basename, isAbsolute, join, resolve } from "path";
import { randomUUID } from "crypto";
import { describeGrpcError, textResult, toolError, type ToolResult } from "../result.js";
import type { RqlHostManager } from "./host.js";
import { otelLogRoot, readDashboardUrl } from "./paths.js";

/** The header the host collector routes telemetry by (HostOtelWatchRegistry.RunIdHeader). */
const RUN_ID_HEADER = "repoql-watch-run-id";

/** OTLP variables whose RepoQL value is appended to a caller value rather than replacing it. */
const APPENDABLE_KEYS = new Set([
  "OTEL_EXPORTER_OTLP_HEADERS",
  "OTEL_EXPORTER_OTLP_TRACES_HEADERS",
  "OTEL_EXPORTER_OTLP_METRICS_HEADERS",
  "OTEL_EXPORTER_OTLP_LOGS_HEADERS",
  "OTEL_RESOURCE_ATTRIBUTES",
]);

interface WatchRunHandle {
  runId: string;
  process: ChildProcess;
}

interface WatchRegistration {
  runId: string;
  schema: string;
  databasePath: string;
  lockPath: string;
  endpoint: string;
}

interface EnvPair {
  key: string;
  value: string;
}

/**
 * Keeps watch processes the plugin started alive after the tool call returns,
 * the way RepoQL.Hosting.Mcp.WatchRegistry does, and tears them down when the
 * plugin service stops.
 */
export class WatchRegistry {
  private readonly runs = new Map<string, WatchRunHandle>();

  add(run: WatchRunHandle): void {
    this.runs.set(run.runId, run);
  }

  remove(runId: string): void {
    this.runs.delete(runId);
  }

  async dispose(): Promise<void> {
    for (const run of this.runs.values()) {
      if (!run.process.killed) {
        try {
          run.process.kill("SIGTERM");
        } catch {
          // Already gone.
        }
      }
    }
    this.runs.clear();
  }
}

export interface RunWatchOptions {
  host: RqlHostManager;
  watches: WatchRegistry;
  executable: string;
  arguments: string[];
  workingDirectory?: string;
  environment?: string;
  signal?: AbortSignal;
}

/**
 * Start an executable under the host OTEL collector and return a live handle.
 * Mirrors RepoQL.Hosting.Mcp.WatchTool: ensure the host is up, read its HTTP
 * endpoint, register the run, then spawn the child wired to export OTLP to the
 * collector — supervising it and reporting completion when it exits.
 */
export async function runWatch(opts: RunWatchOptions): Promise<ToolResult> {
  const { host, watches, executable, workingDirectory, environment, signal } = opts;
  const args = opts.arguments ?? [];

  if (!executable.trim()) {
    return toolError("Executable cannot be empty.");
  }

  let userEnv: EnvPair[];
  try {
    userEnv = parseEnvironmentSpec(environment);
  } catch (err) {
    return toolError(err instanceof Error ? err.message : String(err));
  }

  try {
    await host.ensureReady();
  } catch (err) {
    return toolError(describeGrpcError(err));
  }

  const repoRoot = host.repoRoot;
  const dashboardUrl = await waitForDashboardUrl(repoRoot, signal);
  if (!dashboardUrl) {
    return toolError(
      "RepoQL host did not publish its HTTP endpoint. Is the host running with an HTTP listener? (rql serve)"
    );
  }

  const cwd = workingDirectory ? resolve(workingDirectory) : repoRoot;
  const resolvedExecutable = resolveExecutable(executable, cwd);
  const serviceName = serviceNameFor(resolvedExecutable);
  const runId = randomUUID().replace(/-/g, "");

  let registration: WatchRegistration;
  try {
    registration = await registerRun(dashboardUrl, runId, resolvedExecutable, args, cwd);
  } catch (err) {
    return toolError(`Watch registration failed: ${err instanceof Error ? err.message : String(err)}`);
  }

  const logRoot = otelLogRoot(repoRoot);
  mkdirSync(logRoot, { recursive: true });
  const stdoutPath = join(logRoot, `${runId}.stdout.log`);
  const stderrPath = join(logRoot, `${runId}.stderr.log`);

  const childEnv = buildChildEnv(userEnv, composeOtelEnv(registration.endpoint, runId, serviceName));

  const child = spawn(resolvedExecutable, args, {
    cwd,
    env: childEnv,
    stdio: ["ignore", "pipe", "pipe"],
  });

  const launch = await new Promise<{ ok: true } | { ok: false; error: Error }>((settle) => {
    child.once("spawn", () => settle({ ok: true }));
    child.once("error", (error) => settle({ ok: false, error }));
  });

  if (!launch.ok || child.pid === undefined) {
    await completeRun(dashboardUrl, runId, null);
    const detail = launch.ok ? "process did not report a pid" : launch.error.message;
    return toolError(`Failed to start ${executable}: ${detail}`);
  }

  // Keep the child alive past this call; pipe its output to the run logs.
  child.on("error", () => {
    // Late spawn errors are surfaced through the logs and completion record.
  });
  const stdoutStream = createWriteStream(stdoutPath);
  const stderrStream = createWriteStream(stderrPath);
  child.stdout?.pipe(stdoutStream);
  child.stderr?.pipe(stderrStream);

  watches.add({ runId, process: child });
  child.once("exit", (code) => {
    watches.remove(runId);
    stdoutStream.end();
    stderrStream.end();
    void completeRun(dashboardUrl, runId, code);
  });

  return textResult(
    renderWatch({
      runId,
      pid: child.pid,
      executablePath: resolvedExecutable,
      arguments: args,
      workingDirectory: cwd,
      registration,
      userEnv,
      stdoutPath,
      stderrPath,
    }),
    { tool: "watch", runId, pid: child.pid }
  );
}

// ---------------------------------------------------------------------------
// OTLP environment — mirrors WatchTelemetryEnvironment.Compose + WatchProcessBuilder
// ---------------------------------------------------------------------------

function composeOtelEnv(endpoint: string, runId: string, serviceName: string): EnvPair[] {
  const base = endpoint.replace(/\/+$/, "");
  const header = `${RUN_ID_HEADER}=${runId}`;
  const resourceAttributes = `service.instance.id=${runId},repoql.watch.run_id=${runId}`;

  const vars: EnvPair[] = [
    { key: "OTEL_TRACES_EXPORTER", value: "otlp" },
    { key: "OTEL_METRICS_EXPORTER", value: "otlp" },
    { key: "OTEL_LOGS_EXPORTER", value: "otlp" },
    { key: "OTEL_TRACES_SAMPLER", value: "always_on" },
    { key: "OTEL_EXPORTER_OTLP_ENDPOINT", value: `${base}/api/otel` },
    { key: "OTEL_EXPORTER_OTLP_PROTOCOL", value: "http/protobuf" },
    { key: "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", value: `${base}/api/otel/v1/traces` },
    { key: "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT", value: `${base}/api/otel/v1/metrics` },
    { key: "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT", value: `${base}/api/otel/v1/logs` },
    { key: "OTEL_EXPORTER_OTLP_TRACES_PROTOCOL", value: "http/protobuf" },
    { key: "OTEL_EXPORTER_OTLP_METRICS_PROTOCOL", value: "http/protobuf" },
    { key: "OTEL_EXPORTER_OTLP_LOGS_PROTOCOL", value: "http/protobuf" },
    { key: "OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE", value: "delta" },
    { key: "OTEL_EXPORTER_OTLP_METRICS_DEFAULT_HISTOGRAM_AGGREGATION", value: "explicit_bucket_histogram" },
    { key: "OTEL_METRIC_EXPORT_INTERVAL", value: "1000" },
    { key: "OTEL_BSP_SCHEDULE_DELAY", value: "1000" },
    { key: "OTEL_BLRP_SCHEDULE_DELAY", value: "1000" },
    { key: "OTEL_EXPORTER_OTLP_HEADERS", value: header },
    { key: "OTEL_EXPORTER_OTLP_TRACES_HEADERS", value: header },
    { key: "OTEL_EXPORTER_OTLP_METRICS_HEADERS", value: header },
    { key: "OTEL_EXPORTER_OTLP_LOGS_HEADERS", value: header },
    { key: "OTEL_RESOURCE_ATTRIBUTES", value: resourceAttributes },
    { key: "REPOQL_WATCH_RUN_ID", value: runId },
  ];

  if (serviceName.trim()) {
    vars.push({ key: "OTEL_SERVICE_NAME", value: serviceName });
  }

  return vars;
}

function buildChildEnv(userEnv: EnvPair[], wiring: EnvPair[]): NodeJS.ProcessEnv {
  const env: NodeJS.ProcessEnv = { ...process.env };

  // Caller-supplied variables are applied first so RepoQL's wiring takes precedence.
  for (const { key, value } of userEnv) {
    env[key] = value;
  }

  for (const { key, value } of wiring) {
    if (APPENDABLE_KEYS.has(key)) {
      const existing = env[key];
      env[key] = existing && existing.trim() ? `${existing},${value}` : value;
    } else if (key === "OTEL_SERVICE_NAME") {
      // A caller-supplied service name keeps the watched process's own identity.
      if (env[key] === undefined) {
        env[key] = value;
      }
    } else {
      env[key] = value;
    }
  }

  return env;
}

// ---------------------------------------------------------------------------
// Host HTTP collector — register / complete
// ---------------------------------------------------------------------------

async function registerRun(
  dashboardUrl: string,
  runId: string,
  executablePath: string,
  args: string[],
  workingDirectory: string
): Promise<WatchRegistration> {
  const response = await fetch(new URL("/api/watch/register", dashboardUrl), {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ runId, executablePath, workingDirectory, arguments: args }),
  });

  const text = await response.text();
  if (!response.ok) {
    throw new Error(text || `HTTP ${response.status}`);
  }

  const node = JSON.parse(text) as Record<string, unknown>;
  return {
    runId: required(node, "runId"),
    schema: required(node, "schema"),
    databasePath: required(node, "databasePath"),
    lockPath: required(node, "lockPath"),
    endpoint: required(node, "endpoint"),
  };
}

async function completeRun(dashboardUrl: string, runId: string, exitCode: number | null): Promise<void> {
  try {
    await fetch(new URL("/api/watch/complete", dashboardUrl), {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ runId, exitCode: exitCode ?? null }),
    });
  } catch {
    // Best-effort: a missed completion only leaves a run marked active.
  }
}

async function waitForDashboardUrl(repoRoot: string, signal?: AbortSignal): Promise<string | null> {
  for (let i = 0; i < 100; i++) {
    if (signal?.aborted) {
      return null;
    }
    const url = readDashboardUrl(repoRoot);
    if (url) {
      return url;
    }
    await sleep(100);
  }
  return null;
}

// ---------------------------------------------------------------------------
// environment spec parsing — mirrors WatchEnvironmentSpec.Parse
// ---------------------------------------------------------------------------

function parseEnvironmentSpec(spec: string | undefined): EnvPair[] {
  const result: EnvPair[] = [];
  if (!spec || !spec.trim()) {
    return result;
  }

  for (const entry of splitEntries(spec)) {
    if (!entry.trim()) {
      continue;
    }

    const separator = indexOfUnescaped(entry, "=");
    if (separator < 0) {
      throw new Error(
        `Invalid environment entry '${unescape(entry).trim()}'. Expected 'key=value' pairs separated by ';' (escape a literal ';' or '=' with '\\').`
      );
    }

    const key = unescape(entry.slice(0, separator)).trim();
    if (key.length === 0) {
      throw new Error(`Environment variable name cannot be empty in entry '${unescape(entry).trim()}'.`);
    }

    result.push({ key, value: expandVars(unescape(entry.slice(separator + 1))) });
  }

  return result;
}

function splitEntries(spec: string): string[] {
  const entries: string[] = [];
  let start = 0;
  for (let i = 0; i < spec.length; i++) {
    if (spec[i] === ";" && !isEscaped(spec, i)) {
      entries.push(spec.slice(start, i));
      start = i + 1;
    }
  }
  entries.push(spec.slice(start));
  return entries;
}

function indexOfUnescaped(value: string, target: string): number {
  for (let i = 0; i < value.length; i++) {
    if (value[i] === target && !isEscaped(value, i)) {
      return i;
    }
  }
  return -1;
}

function isEscaped(value: string, index: number): boolean {
  let backslashes = 0;
  for (let i = index - 1; i >= 0 && value[i] === "\\"; i--) {
    backslashes++;
  }
  return backslashes % 2 === 1;
}

function unescape(value: string): string {
  if (!value.includes("\\")) {
    return value;
  }
  let out = "";
  for (let i = 0; i < value.length; i++) {
    const next = value[i + 1];
    if (value[i] === "\\" && (next === ";" || next === "=" || next === "\\")) {
      out += next;
      i++;
    } else {
      out += value[i];
    }
  }
  return out;
}

/** Expand %VAR% references against the spawning process's environment. */
function expandVars(value: string): string {
  return value.replace(/%([^%]+)%/g, (match, name: string) => process.env[name] ?? match);
}

// ---------------------------------------------------------------------------
// Rendering + small helpers
// ---------------------------------------------------------------------------

interface RenderWatchInput {
  runId: string;
  pid: number;
  executablePath: string;
  arguments: string[];
  workingDirectory: string;
  registration: WatchRegistration;
  userEnv: EnvPair[];
  stdoutPath: string;
  stderrPath: string;
}

function renderWatch(input: RenderWatchInput): string {
  const environmentLine =
    input.userEnv.length === 0
      ? ""
      : `environment: ${input.userEnv.map((pair) => pair.key).join(", ")}\n`;

  return [
    "Started OTEL watch",
    `run_id: ${input.runId}`,
    `pid: ${input.pid}`,
    `executable: ${input.executablePath}`,
    `arguments: ${input.arguments.join(" ")}`,
    `working_directory: ${input.workingDirectory}`,
    `${environmentLine}endpoint: ${input.registration.endpoint}`,
    `database: ${input.registration.databasePath}`,
    `telemetry_schema: ${input.registration.schema}`,
    `lock: ${input.registration.lockPath}`,
    `stdout_log: ${input.stdoutPath}`,
    `stderr_log: ${input.stderrPath}`,
    "",
    "Check process:",
    `  ps -p ${input.pid}`,
    "",
    "Query telemetry:",
    `  SELECT * FROM watch.summary('${input.runId}');`,
    "  SELECT * FROM watch.surface;",
  ].join("\n");
}

function resolveExecutable(executable: string, base: string): string {
  if (isAbsolute(executable)) {
    return executable;
  }
  if (executable.includes("/") || executable.includes("\\") || executable.startsWith(".")) {
    return resolve(base, executable);
  }
  // A bare name resolves through PATH at spawn time.
  return executable;
}

function serviceNameFor(executablePath: string): string {
  return basename(executablePath).replace(/\.[^.]+$/, "");
}

function required(node: Record<string, unknown>, key: string): string {
  const value = node[key];
  if (typeof value !== "string" || value.length === 0) {
    throw new Error(`Watch registration response omitted ${key}.`);
  }
  return value;
}

function sleep(ms: number): Promise<void> {
  return new Promise((done) => setTimeout(done, ms));
}
