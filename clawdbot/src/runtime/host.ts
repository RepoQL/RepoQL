import { spawn, type ChildProcess } from "child_process";
import { mkdirSync } from "fs";
import { connect } from "net";
import type { RepoQlPluginConfig } from "../config.js";
import { resolveRepoRoot } from "../config.js";
import { cacheDir, resolveSocketPath } from "./paths.js";
import { RqlGrpcClient } from "./rqlGrpcClient.js";
import type { Logger } from "./types.js";

export interface RqlHostManagerOptions {
  config: RepoQlPluginConfig;
  logger: Logger;
  workspaceDir: string;
}

export class RqlHostManager {
  private readonly config: RepoQlPluginConfig;
  private readonly logger: Logger;
  private readonly workspaceDir: string;
  private client: RqlGrpcClient | null = null;
  private process: ChildProcess | null = null;
  private ensurePromise: Promise<void> | null = null;

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

  get requestTimeoutMs(): number {
    return this.config.requestTimeoutMs;
  }

  async ensureReady(): Promise<void> {
    this.ensurePromise ??= this.ensureReadyCore().finally(() => {
      this.ensurePromise = null;
    });
    return this.ensurePromise;
  }

  async getClient(): Promise<RqlGrpcClient> {
    await this.ensureReady();

    const socketPath = this.socketPath;
    if (!this.client || this.client.socketPath !== socketPath) {
      this.client?.close();
      this.client = new RqlGrpcClient(socketPath, this.config.requestTimeoutMs);
    }

    return this.client;
  }

  /**
   * Connect a short-lived client to the host socket WITHOUT auto-starting it.
   * The caller owns the returned client and must close it. Used by `host status`
   * and `host stop`, which must observe the host rather than launch one.
   */
  connect(): RqlGrpcClient {
    return new RqlGrpcClient(this.socketPath, this.config.requestTimeoutMs);
  }

  /** Probe whether a host is already serving on the socket, without starting one. */
  isReachable(): Promise<boolean> {
    return isRepoQlServiceReachable(this.socketPath);
  }

  async dispose(): Promise<void> {
    this.client?.close();
    this.client = null;

    if (this.process && !this.process.killed) {
      this.process.kill("SIGTERM");
    }
    this.process = null;
  }

  private async ensureReadyCore(): Promise<void> {
    const repoRoot = this.repoRoot;
    const socketPath = this.socketPath;

    if (await isRepoQlServiceReachable(socketPath)) {
      return;
    }

    if (!this.config.autoStart) {
      throw new Error(`RepoQL host is not reachable at ${socketPath}; enable autoStart or run rql serve.`);
    }

    this.startHost(repoRoot);
    await this.waitForReachable();
  }

  private startHost(repoRoot: string): void {
    if (this.process && !this.process.killed) {
      return;
    }

    mkdirSync(cacheDir(repoRoot), { recursive: true });

    this.logger.info(`Starting RepoQL host for ${repoRoot}`);
    this.process = spawn(this.config.rqlPath, ["serve", "--implicit-start"], {
      cwd: repoRoot,
      env: process.env,
      stdio: ["ignore", "ignore", "pipe"],
    });

    this.process.stderr?.on("data", (chunk: Buffer) => {
      const text = chunk.toString("utf8").trim();
      if (text) {
        this.logger.debug?.(`[rql] ${text}`);
      }
    });

    this.process.on("exit", (code, signal) => {
      this.logger.warn(`RepoQL host exited (code=${code}, signal=${signal})`);
      this.process = null;
      this.client?.close();
      this.client = null;
    });

    this.process.on("error", (err) => {
      this.logger.error(`Failed to start RepoQL host: ${err.message}`);
    });
  }

  private async waitForReachable(): Promise<void> {
    const started = Date.now();
    let lastSocket = this.socketPath;

    while (Date.now() - started < this.config.startupTimeoutMs) {
      lastSocket = this.socketPath;
      if (await isRepoQlServiceReachable(lastSocket)) {
        this.logger.info(`RepoQL host reachable at ${lastSocket}`);
        return;
      }
      await sleep(150);
    }

    throw new Error(
      `RepoQL host did not become reachable within ${this.config.startupTimeoutMs}ms (socket: ${lastSocket})`
    );
  }
}

async function isSocketReachable(socketPath: string): Promise<boolean> {
  return new Promise((resolveReachable) => {
    const socket = connect({ path: socketPath });
    const timer = setTimeout(() => {
      socket.destroy();
      resolveReachable(false);
    }, 500);

    socket.once("connect", () => {
      clearTimeout(timer);
      socket.end();
      resolveReachable(true);
    });

    socket.once("error", () => {
      clearTimeout(timer);
      resolveReachable(false);
    });
  });
}

export async function isRepoQlServiceReachable(socketPath: string): Promise<boolean> {
  if (!(await isSocketReachable(socketPath))) {
    return false;
  }

  const client = new RqlGrpcClient(socketPath, 2_000);
  try {
    await client.query({ sql: "SELECT 1 AS ok", maxRows: 1, tokenBudget: 0 }, 2_000);
    return true;
  } catch {
    return false;
  } finally {
    client.close();
  }
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolveSleep) => setTimeout(resolveSleep, ms));
}
