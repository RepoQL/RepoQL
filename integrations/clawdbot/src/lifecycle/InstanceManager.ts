/**
 * Manages multiple MCP client instances across workspaces.
 *
 * Purpose: Provides single point of access for MCP clients per workspace,
 *   handling spawn, health checks, and auto-restart with backoff.
 * Complexity: Manages concurrent spawn requests, tracks instance state,
 *   and coordinates health check lifecycle. Normalizes workspace paths
 *   to prevent duplicate instances.
 */

import { normalize, resolve } from "path";
import { McpClient } from "../mcp/McpClient.js";
import { McpConnectionError } from "../mcp/errors.js";
import { BackoffStrategy } from "./BackoffStrategy.js";

export interface InstanceManagerConfig {
  /** Path to the repoql executable */
  exePath: string;
  /** Health check interval in milliseconds. Default: 60000 */
  healthCheckIntervalMs?: number;
  /** Maximum restart attempts before giving up. Default: 3 */
  maxRestartAttempts?: number;
  /** Timeout for MCP initialize handshake. Default: 30000 */
  initializeTimeoutMs?: number;
}

export interface Logger {
  info(message: string): void;
  warn(message: string): void;
  error(message: string): void;
}

interface InstanceState {
  client: McpClient;
  workdir: string;
  backoff: BackoffStrategy;
  restartAttempts: number;
  isRestarting: boolean;
}

/**
 * Manages MCP client instances per workspace.
 */
export class InstanceManager {
  private instances = new Map<string, InstanceState>();
  private pendingSpawns = new Map<string, Promise<McpClient>>();
  private healthCheckTimer: NodeJS.Timeout | null = null;
  private readonly config: Required<InstanceManagerConfig>;
  private readonly logger: Logger;
  private stopped = false;

  constructor(config: InstanceManagerConfig, logger: Logger) {
    this.config = {
      exePath: config.exePath,
      healthCheckIntervalMs: config.healthCheckIntervalMs ?? 60000,
      maxRestartAttempts: config.maxRestartAttempts ?? 3,
      initializeTimeoutMs: config.initializeTimeoutMs ?? 30000,
    };
    this.logger = logger;
  }

  /**
   * Gets an existing instance or spawns a new one for the workspace.
   *
   * @param workdir - The workspace directory
   * @returns The MCP client for this workspace
   * @throws McpConnectionError if spawn fails after max attempts
   */
  async getInstance(workdir: string): Promise<McpClient> {
    // Auto-reset if stopped (handles hot reload where start() may not have been called yet)
    if (this.stopped) {
      this.logger.info("InstanceManager: auto-resetting from stopped state");
      this.stopped = false;
    }

    const key = this.normalizeWorkdir(workdir);

    // Return existing connected instance
    const existing = this.instances.get(key);
    if (existing?.client.isConnected) {
      return existing.client;
    }

    // Return pending spawn if one is in progress
    const pending = this.pendingSpawns.get(key);
    if (pending) {
      return pending;
    }

    // Spawn new instance
    const spawnPromise = this.spawnInstance(key, workdir);
    this.pendingSpawns.set(key, spawnPromise);

    try {
      const client = await spawnPromise;
      return client;
    } finally {
      this.pendingSpawns.delete(key);
    }
  }

  /**
   * Stops a specific workspace instance.
   */
  async stopInstance(workdir: string): Promise<void> {
    const key = this.normalizeWorkdir(workdir);
    const state = this.instances.get(key);
    if (state) {
      this.instances.delete(key);
      await state.client.kill();
      this.logger.info(`Stopped RepoQL instance for ${workdir}`);
    }
  }

  /**
   * Stops all instances and clears state.
   */
  async stopAll(): Promise<void> {
    this.stopped = true;
    this.stopHealthChecks();

    const killPromises = Array.from(this.instances.values()).map((state) =>
      state.client.kill()
    );
    this.instances.clear();

    await Promise.all(killPromises);
    this.logger.info("Stopped all RepoQL instances");
  }

  /**
   * Resets the stopped state, allowing the manager to be reused after a restart.
   */
  reset(): void {
    this.stopped = false;
    this.logger.info("InstanceManager reset");
  }

  /**
   * Starts the periodic health check loop.
   */
  startHealthChecks(): void {
    if (this.healthCheckTimer) {
      return;
    }

    this.healthCheckTimer = setInterval(() => {
      this.performHealthChecks();
    }, this.config.healthCheckIntervalMs);

    this.logger.info(
      `Started health checks (interval: ${this.config.healthCheckIntervalMs}ms)`
    );
  }

  /**
   * Stops the health check loop.
   */
  stopHealthChecks(): void {
    if (this.healthCheckTimer) {
      clearInterval(this.healthCheckTimer);
      this.healthCheckTimer = null;
      this.logger.info("Stopped health checks");
    }
  }

  /**
   * Spawns a new MCP client instance.
   */
  private async spawnInstance(key: string, workdir: string): Promise<McpClient> {
    const client = new McpClient({
      initializeTimeoutMs: this.config.initializeTimeoutMs,
    });

    const state: InstanceState = {
      client,
      workdir,
      backoff: new BackoffStrategy(),
      restartAttempts: 0,
      isRestarting: false,
    };

    // Set up exit handler for auto-restart
    client.on("exit", (code, signal) => {
      this.logger.warn(
        `RepoQL instance exited (workdir: ${workdir}, code: ${code}, signal: ${signal})`
      );
      this.handleInstanceExit(key, state);
    });

    client.on("error", (err) => {
      this.logger.error(`RepoQL instance error (workdir: ${workdir}): ${err.message}`);
    });

    await client.spawn(this.config.exePath, workdir);
    this.instances.set(key, state);
    this.logger.info(`Spawned RepoQL instance for ${workdir}`);

    // Reset backoff on successful spawn
    state.backoff.reset();
    state.restartAttempts = 0;

    return client;
  }

  /**
   * Handles instance exit with auto-restart.
   */
  private async handleInstanceExit(key: string, state: InstanceState): Promise<void> {
    if (this.stopped || state.isRestarting) {
      return;
    }

    // Check if we've exceeded max restart attempts
    if (state.restartAttempts >= this.config.maxRestartAttempts) {
      this.logger.error(
        `RepoQL instance failed ${state.restartAttempts} times, giving up (workdir: ${state.workdir})`
      );
      this.instances.delete(key);
      return;
    }

    state.isRestarting = true;
    state.restartAttempts++;

    const delay = state.backoff.nextDelay();
    this.logger.info(
      `Restarting RepoQL instance in ${delay}ms (attempt ${state.restartAttempts}/${this.config.maxRestartAttempts}, workdir: ${state.workdir})`
    );

    await this.sleep(delay);

    if (this.stopped) {
      return;
    }

    try {
      const newClient = new McpClient({
        initializeTimeoutMs: this.config.initializeTimeoutMs,
      });

      newClient.on("exit", (code, signal) => {
        this.logger.warn(
          `RepoQL instance exited (workdir: ${state.workdir}, code: ${code}, signal: ${signal})`
        );
        this.handleInstanceExit(key, state);
      });

      newClient.on("error", (err) => {
        this.logger.error(
          `RepoQL instance error (workdir: ${state.workdir}): ${err.message}`
        );
      });

      await newClient.spawn(this.config.exePath, state.workdir);

      state.client = newClient;
      state.isRestarting = false;
      state.backoff.reset();
      state.restartAttempts = 0;

      this.logger.info(`Restarted RepoQL instance successfully (workdir: ${state.workdir})`);
    } catch (err) {
      state.isRestarting = false;
      this.logger.error(
        `Failed to restart RepoQL instance (workdir: ${state.workdir}): ${err instanceof Error ? err.message : err}`
      );
      // Exit handler will be called if spawn fails, triggering another restart attempt
    }
  }

  /**
   * Performs health checks on all instances.
   */
  private performHealthChecks(): void {
    for (const [key, state] of this.instances) {
      if (!state.client.isConnected && !state.isRestarting) {
        this.logger.warn(
          `Health check: instance not connected (workdir: ${state.workdir})`
        );
        // Trigger restart through the exit handler logic
        this.handleInstanceExit(key, state);
      }
    }
  }

  /**
   * Normalizes a workspace directory path for use as a map key.
   */
  private normalizeWorkdir(workdir: string): string {
    return normalize(resolve(workdir)).toLowerCase();
  }

  /**
   * Promisified sleep.
   */
  private sleep(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }
}
