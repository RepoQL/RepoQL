/**
 * RepoQL background service for Clawdbot.
 *
 * Purpose: Manages MCP client lifecycle as a background service,
 *   ensuring clean startup and shutdown.
 * Complexity: Coordinates InstanceManager with Clawdbot service API
 *   and handles process exit cleanup.
 */

import type { InstanceManager } from "../lifecycle/InstanceManager.js";

export interface Logger {
  info(message: string): void;
  warn(message: string): void;
  error(message: string): void;
}

/**
 * Background service that manages RepoQL MCP instances.
 */
export class RepoQlService {
  private readonly manager: InstanceManager;
  private readonly logger: Logger;
  private exitHandler: (() => void) | null = null;

  constructor(manager: InstanceManager, logger: Logger) {
    this.manager = manager;
    this.logger = logger;
  }

  /**
   * Starts the service.
   * Registers process exit handler and starts health checks.
   */
  async start(): Promise<void> {
    this.logger.info("RepoQL service starting");

    // Register exit handler to clean up on unexpected exit
    this.exitHandler = () => {
      this.logger.info("Process exiting, stopping RepoQL instances");
      // Synchronous stop - best effort cleanup
      this.manager.stopAll().catch(() => {});
    };

    process.on("exit", this.exitHandler);
    process.on("SIGINT", this.exitHandler);
    process.on("SIGTERM", this.exitHandler);

    // Start health check loop
    this.manager.startHealthChecks();

    this.logger.info("RepoQL service started");
  }

  /**
   * Stops the service.
   * Stops all instances and removes exit handlers.
   */
  async stop(): Promise<void> {
    this.logger.info("RepoQL service stopping");

    // Remove exit handlers
    if (this.exitHandler) {
      process.removeListener("exit", this.exitHandler);
      process.removeListener("SIGINT", this.exitHandler);
      process.removeListener("SIGTERM", this.exitHandler);
      this.exitHandler = null;
    }

    // Stop all instances
    await this.manager.stopAll();

    this.logger.info("RepoQL service stopped");
  }
}

/**
 * Registers the RepoQL background service with Clawdbot.
 */
export function registerService(api: any, manager: InstanceManager): RepoQlService {
  const logger = api.logger;
  const service = new RepoQlService(manager, logger);

  api.registerService({
    id: "repoql-service",
    start: () => service.start(),
    stop: () => service.stop(),
  });

  return service;
}
