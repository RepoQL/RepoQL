import { spawn } from "child_process";
import { randomUUID } from "crypto";
import { connect } from "net";
import { status as GrpcStatus, type ServiceError } from "@grpc/grpc-js";
import type { RepoQlPluginConfig } from "../config.js";
import { resolveRepoRoot } from "../config.js";
import { PLUGIN_VERSION } from "../version.js";
import { resolveSocketPath } from "./paths.js";
import { RqlGrpcClient, type LeaseHandle, type ToolDefinition } from "./rqlGrpcClient.js";
import type { Logger } from "./types.js";

/** One id per gateway process: every lease this plugin holds is the same logical client. */
const CLIENT_ID = randomUUID();

const PROBE_TIMEOUT_MS = 2_000;
const WARM_INTERVAL_MS = 30_000;
const STDERR_TAIL_CHARS = 4_000;

export interface RqlHostManagerOptions {
  config: RepoQlPluginConfig;
  logger: Logger;
  workspaceDir: string;
}

/** Outcome of probing a socket without starting anything. */
type Probe = { kind: "absent" } | { kind: "serving" } | { kind: "incompatible"; detail: string };

/**
 * Owns the connection to the rql host for one workspace, following the
 * repoql.host.v1 session protocol: discover the socket, probe it, launch an
 * implicit host only when nothing is serving, then hold a lease so the host
 * knows this gateway still needs it. The host is shared with every other
 * RepoQL client on the machine, so the plugin never kills it — releasing the
 * lease lets an implicit host idle out on its own.
 */
export class RqlHostManager {
  private readonly config: RepoQlPluginConfig;
  private readonly logger: Logger;
  private readonly workspaceDir: string;
  private client: RqlGrpcClient | null = null;
  private lease: LeaseHandle | null = null;
  private catalog: ToolDefinition[] | null = null;
  private ensurePromise: Promise<RqlGrpcClient> | null = null;
  private lastWarmAt = 0;

  constructor(options: RqlHostManagerOptions) {
    this.config = options.config;
    this.logger = options.logger;
    this.workspaceDir = options.workspaceDir;
  }

  get repoRoot(): string {
    return resolveRepoRoot(this.config.repoRoot, this.workspaceDir);
  }

  get socketPath(): string {
    return resolveSocketPath(this.repoRoot);
  }

  /** The tool catalog the connected host last described, or null before first contact. */
  get liveCatalog(): ToolDefinition[] | null {
    return this.catalog;
  }

  /** Connect to the host, launching it when autoStart allows, and return a leased client. */
  getClient(): Promise<RqlGrpcClient> {
    if (this.client) {
      return Promise.resolve(this.client);
    }
    this.ensurePromise ??= this.connectCore().finally(() => {
      this.ensurePromise = null;
    });
    return this.ensurePromise;
  }

  /**
   * Adopt an already-running host in the background so its live catalog is in
   * place for the next tool assembly. Never launches a host — an agent that
   * never touches RepoQL should never start one — and probes at most every 30s.
   */
  warm(): void {
    const now = Date.now();
    if (this.client || this.ensurePromise || now - this.lastWarmAt < WARM_INTERVAL_MS) {
      return;
    }
    this.lastWarmAt = now;
    void probe(this.socketPath)
      .then((found) => (found.kind === "serving" && !this.client ? this.getClient() : undefined))
      .catch((err) => this.logger.debug?.(`RepoQL warm-up skipped: ${errorMessage(err)}`));
  }

  /**
   * A leased client for a host that is already serving, adopting it when this
   * gateway has not connected yet; null when none is. For work that rides
   * along with the agent's own tools (hints, orientation): it never launches a
   * host, and gives up after maxWaitMs — a launch another call started, or a
   * slow first connection, is not worth holding the agent's tool result for.
   */
  async runningClient(maxWaitMs: number): Promise<RqlGrpcClient | null> {
    if (this.client) {
      return this.client;
    }
    let timer: NodeJS.Timeout | undefined;
    const giveUp = new Promise<null>((resolveWait) => {
      timer = setTimeout(() => resolveWait(null), maxWaitMs);
      timer.unref();
    });
    const adopt = (async () => {
      if (!this.ensurePromise && (await probe(this.socketPath)).kind !== "serving") {
        return null;
      }
      return this.getClient();
    })();
    try {
      return await Promise.race([adopt, giveUp]);
    } finally {
      clearTimeout(timer);
    }
  }

  /**
   * A short-lived client that never launches a host. The caller owns it and
   * must close it. Used by `host status` and `host stop`, which observe the
   * host rather than start one.
   */
  connect(): RqlGrpcClient {
    return new RqlGrpcClient(this.socketPath, this.config.requestTimeoutMs);
  }

  /** Probe whether a compatible host is already serving, without starting one. */
  async isReachable(): Promise<boolean> {
    return (await probe(this.socketPath)).kind === "serving";
  }

  /** Forget the current connection so the next call re-discovers the host. */
  reset(): void {
    this.lease?.close();
    this.lease = null;
    this.client?.close();
    this.client = null;
  }

  dispose(): void {
    this.reset();
  }

  private async connectCore(): Promise<RqlGrpcClient> {
    const found = await probe(this.socketPath);
    if (found.kind === "incompatible") {
      throw new Error(found.detail);
    }
    if (found.kind === "absent") {
      if (!this.config.autoStart) {
        throw new Error(
          `RepoQL host is not running for ${this.repoRoot}. Start it with \`rql serve\` in that directory, ` +
            "or enable the plugin's autoStart setting."
        );
      }
      await this.launchAndWait();
    }

    const client = new RqlGrpcClient(this.socketPath, this.config.requestTimeoutMs);
    this.client = client;
    this.holdLease(client);
    await this.refreshCatalog(client);
    return client;
  }

  private async refreshCatalog(client: RqlGrpcClient): Promise<void> {
    try {
      this.catalog = await client.describeTools(PROBE_TIMEOUT_MS * 5);
    } catch (err) {
      this.logger.warn(`RepoQL DescribeTools failed; using the bundled catalog: ${errorMessage(err)}`);
    }
  }

  private holdLease(client: RqlGrpcClient): void {
    this.lease = client.holdLease(
      { clientId: CLIENT_ID, clientName: "openclaw-repoql", clientVersion: PLUGIN_VERSION, pid: process.pid },
      {
        onAccepted: (intervalMs) => this.logger.debug?.(`RepoQL lease accepted (beat every ${intervalMs}ms)`),
        onStopping: (reason) => {
          this.logger.info(`RepoQL host is stopping (${reason || "no reason given"}); will reconnect on next call`);
          if (this.client === client) {
            this.reset();
          }
        },
        onClosed: (error) => {
          if (this.client === client) {
            if (error && error.code !== GrpcStatus.CANCELLED) {
              this.logger.debug?.(`RepoQL lease closed: ${error.message}`);
            }
            this.reset();
          }
        },
      }
    );
  }

  private async launchAndWait(): Promise<void> {
    const repoRoot = this.repoRoot;
    this.logger.info(`Starting RepoQL host for ${repoRoot}`);

    // Detached and unreferenced: the host is shared machine state that outlives
    // this gateway. stderr is read only until the host is serving, so a host
    // that refuses to start (a forbidden root, a bad flag) explains itself.
    const child = spawn(this.config.rqlPath, ["serve", "--implicit-start"], {
      cwd: repoRoot,
      env: process.env,
      detached: true,
      stdio: ["ignore", "ignore", "pipe"],
    });

    let stderrTail = "";
    let exited: string | null = null;
    child.stderr?.on("data", (chunk: Buffer) => {
      stderrTail = (stderrTail + chunk.toString("utf8")).slice(-STDERR_TAIL_CHARS);
    });
    child.on("exit", (code, signal) => {
      exited = signal ? `signal ${signal}` : `exit code ${code}`;
    });
    child.on("error", (err: NodeJS.ErrnoException) => {
      exited =
        err.code === "ENOENT"
          ? `'${this.config.rqlPath}' was not found. Install rql (https://repoql.ai) or set the plugin's rqlPath.`
          : err.message;
    });

    const release = (): void => {
      child.stderr?.destroy();
      child.unref();
    };

    const started = Date.now();
    try {
      while (Date.now() - started < this.config.startupTimeoutMs) {
        const found = await probe(this.socketPath);
        if (found.kind === "serving") {
          this.logger.info(`RepoQL host serving at ${this.socketPath}`);
          return;
        }
        if (found.kind === "incompatible") {
          throw new Error(found.detail);
        }
        if (exited !== null) {
          // Another client may have won the launch race; its host still counts.
          if ((await probe(this.socketPath)).kind === "serving") {
            return;
          }
          const tail = stderrTail.trim();
          throw new Error(`RepoQL host failed to start (${exited}).${tail ? `\n${tail}` : ""}`);
        }
        await sleep(200);
      }
    } finally {
      release();
    }

    throw new Error(
      `RepoQL host did not start serving within ${this.config.startupTimeoutMs}ms (socket: ${this.socketPath}). ` +
        `Check ${repoRoot}/.repoql/cache for the host log, or run \`rql serve\` there to see the error.`
    );
  }
}

/**
 * The host.v1 PROBE step. GetHostInfo is cheap, side-effect free, and safe
 * before readiness. An UNIMPLEMENTED answer means a host is serving an older
 * wire contract — say so rather than launch a competing host on its socket.
 */
async function probe(socketPath: string): Promise<Probe> {
  if (!(await isSocketListening(socketPath))) {
    return { kind: "absent" };
  }

  const client = new RqlGrpcClient(socketPath, PROBE_TIMEOUT_MS);
  try {
    await client.getHostInfo(PROBE_TIMEOUT_MS);
    return { kind: "serving" };
  } catch (err) {
    if ((err as ServiceError)?.code === GrpcStatus.UNIMPLEMENTED) {
      return {
        kind: "incompatible",
        detail:
          `The rql host at ${socketPath} predates the repoql.host.v1 contract this plugin needs (rql 1.6.30+). ` +
          "Run `rql update`, then `rql host restart` in the workspace.",
      };
    }
    return { kind: "absent" };
  } finally {
    client.close();
  }
}

function isSocketListening(socketPath: string): Promise<boolean> {
  return new Promise((resolveProbe) => {
    const socket = connect({ path: socketPath });
    const done = (result: boolean): void => {
      socket.destroy();
      resolveProbe(result);
    };
    socket.setTimeout(PROBE_TIMEOUT_MS, () => done(false));
    socket.once("connect", () => done(true));
    socket.once("error", () => done(false));
  });
}

function errorMessage(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolveSleep) => setTimeout(resolveSleep, ms));
}
