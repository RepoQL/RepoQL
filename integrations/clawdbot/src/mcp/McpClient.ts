/**
 * MCP client over subprocess stdio using JSON-RPC 2.0.
 *
 * Purpose: Spawns `repoql mcp` as a subprocess and communicates via JSON-RPC.
 * Complexity: Manages subprocess lifecycle, request/response correlation, timeouts,
 *   and MCP initialize handshake. Serializes concurrent requests via queue to respect
 *   DuckDB's single-writer architecture.
 */

import { spawn, type ChildProcess } from "child_process";
import { EventEmitter } from "events";
import type {
  JsonRpcRequest,
  JsonRpcResponse,
  McpInitializeParams,
  McpInitializeResult,
  McpToolCallParams,
  McpToolResult,
  PendingRequest,
} from "./types.js";
import { McpConnectionError, McpRpcError, McpTimeoutError } from "./errors.js";

const MCP_PROTOCOL_VERSION = "2024-11-05";
const CLIENT_NAME = "clawdbot-repoql";
const CLIENT_VERSION = "1.0.0";

export interface McpClientEvents {
  error: [error: Error];
  exit: [code: number | null, signal: string | null];
}

export interface McpClientOptions {
  /** Timeout for MCP initialize handshake (ms). Default: 30000 */
  initializeTimeoutMs?: number;
}

/**
 * MCP client that communicates with RepoQL via subprocess stdio.
 */
export class McpClient extends EventEmitter<McpClientEvents> {
  private process: ChildProcess | null = null;
  private requestId = 0;
  private pendingRequests = new Map<number, PendingRequest>();
  private buffer = "";
  private _isConnected = false;
  private initializeTimeoutMs: number;

  constructor(options: McpClientOptions = {}) {
    super();
    this.initializeTimeoutMs = options.initializeTimeoutMs ?? 30000;
  }

  /**
   * Whether the client is connected to a running RepoQL instance.
   */
  get isConnected(): boolean {
    return this._isConnected;
  }

  /**
   * Spawns the RepoQL MCP server and performs the initialize handshake.
   *
   * @param exePath - Path to the repoql executable
   * @param workdir - Working directory for the subprocess
   * @throws McpConnectionError if spawn fails or initialize handshake fails
   */
  async spawn(exePath: string, workdir: string): Promise<void> {
    if (this.process) {
      throw new McpConnectionError("Client already spawned");
    }

    return new Promise((resolve, reject) => {
      try {
        this.process = spawn(exePath, ["mcp"], {
          cwd: workdir,
          stdio: ["pipe", "pipe", "pipe"],
          env: { ...process.env },
        });

        const proc = this.process;

        proc.on("error", (err) => {
          this._isConnected = false;
          this.rejectAllPending(new McpConnectionError("Process error", err));
          this.emit("error", new McpConnectionError("Failed to spawn RepoQL", err));
          reject(new McpConnectionError("Failed to spawn RepoQL", err));
        });

        proc.on("exit", (code, signal) => {
          this._isConnected = false;
          this.rejectAllPending(
            new McpConnectionError(`Process exited with code ${code}, signal ${signal}`)
          );
          this.emit("exit", code, signal);
        });

        proc.stdout?.on("data", (chunk: Buffer) => {
          this.handleData(chunk.toString("utf-8"));
        });

        proc.stderr?.on("data", (chunk: Buffer) => {
          // Log stderr but don't treat as fatal - RepoQL may log info/warnings there
          const text = chunk.toString("utf-8").trim();
          if (text) {
            // Could emit a 'log' event here if needed for debugging
          }
        });

        // Perform MCP initialize handshake
        this.initialize()
          .then(() => {
            this._isConnected = true;
            resolve();
          })
          .catch((err) => {
            this.kill().catch(() => {});
            reject(err);
          });
      } catch (err) {
        reject(
          new McpConnectionError(
            "Failed to spawn RepoQL",
            err instanceof Error ? err : new Error(String(err))
          )
        );
      }
    });
  }

  /**
   * Calls an MCP tool and returns the result.
   *
   * @param name - Tool name (e.g., "explore", "query")
   * @param args - Tool arguments
   * @param timeoutMs - Timeout for this request in milliseconds
   * @returns The tool result
   * @throws McpTimeoutError if request times out
   * @throws McpRpcError if server returns an error
   * @throws McpConnectionError if not connected
   */
  async callTool(
    name: string,
    args: Record<string, unknown>,
    timeoutMs: number
  ): Promise<McpToolResult> {
    if (!this._isConnected) {
      throw new McpConnectionError("Client not connected");
    }

    const params: McpToolCallParams = {
      name,
      arguments: args,
    };

    const result = await this.sendRequest<McpToolResult>("tools/call", params, timeoutMs);
    return result;
  }

  /**
   * Gracefully kills the subprocess.
   */
  async kill(): Promise<void> {
    if (!this.process) {
      return;
    }

    return new Promise((resolve) => {
      const proc = this.process!;
      this.process = null;
      this._isConnected = false;

      // Give it a moment to exit gracefully
      const timeout = setTimeout(() => {
        proc.kill("SIGKILL");
        resolve();
      }, 2000);

      proc.once("exit", () => {
        clearTimeout(timeout);
        resolve();
      });

      proc.kill("SIGTERM");
    });
  }

  /**
   * Performs the MCP initialize handshake.
   */
  private async initialize(): Promise<McpInitializeResult> {
    const params: McpInitializeParams = {
      protocolVersion: MCP_PROTOCOL_VERSION,
      capabilities: {
        tools: {},
      },
      clientInfo: {
        name: CLIENT_NAME,
        version: CLIENT_VERSION,
      },
    };

    const result = await this.sendRequest<McpInitializeResult>(
      "initialize",
      params,
      this.initializeTimeoutMs
    );

    // Send initialized notification (no response expected)
    this.sendNotification("notifications/initialized", {});

    return result;
  }

  /**
   * Sends a JSON-RPC request and waits for the response.
   */
  private sendRequest<T>(
    method: string,
    params: object,
    timeoutMs: number
  ): Promise<T> {
    return new Promise((resolve, reject) => {
      if (!this.process?.stdin?.writable) {
        reject(new McpConnectionError("Process stdin not writable"));
        return;
      }

      const id = ++this.requestId;
      const request: JsonRpcRequest = {
        jsonrpc: "2.0",
        id,
        method,
        params,
      };

      const timeoutId = setTimeout(() => {
        this.pendingRequests.delete(id);
        reject(new McpTimeoutError(method, timeoutMs));
      }, timeoutMs);

      this.pendingRequests.set(id, {
        resolve: resolve as (value: unknown) => void,
        reject,
        timeoutId,
      });

      const json = JSON.stringify(request) + "\n";
      this.process.stdin.write(json);
    });
  }

  /**
   * Sends a JSON-RPC notification (no response expected).
   */
  private sendNotification(method: string, params: Record<string, unknown>): void {
    if (!this.process?.stdin?.writable) {
      return;
    }

    const notification = {
      jsonrpc: "2.0",
      method,
      params,
    };

    const json = JSON.stringify(notification) + "\n";
    this.process.stdin.write(json);
  }

  /**
   * Handles incoming data from stdout, buffering and parsing JSON-RPC messages.
   */
  private handleData(data: string): void {
    this.buffer += data;

    // Parse complete lines (newline-delimited JSON)
    let newlineIndex: number;
    while ((newlineIndex = this.buffer.indexOf("\n")) !== -1) {
      const line = this.buffer.slice(0, newlineIndex).trim();
      this.buffer = this.buffer.slice(newlineIndex + 1);

      if (line) {
        this.handleMessage(line);
      }
    }
  }

  /**
   * Handles a single JSON-RPC message.
   */
  private handleMessage(json: string): void {
    let response: JsonRpcResponse;
    try {
      response = JSON.parse(json) as JsonRpcResponse;
    } catch {
      // Not valid JSON, ignore (could be startup logging)
      return;
    }

    // Only handle responses (with id)
    if (typeof response.id !== "number") {
      return;
    }

    const pending = this.pendingRequests.get(response.id);
    if (!pending) {
      return;
    }

    this.pendingRequests.delete(response.id);
    clearTimeout(pending.timeoutId);

    if ("error" in response) {
      pending.reject(
        new McpRpcError(
          response.error.code,
          response.error.message,
          response.error.data
        )
      );
    } else {
      pending.resolve(response.result);
    }
  }

  /**
   * Rejects all pending requests with the given error.
   */
  private rejectAllPending(error: Error): void {
    for (const [id, pending] of this.pendingRequests) {
      clearTimeout(pending.timeoutId);
      pending.reject(error);
    }
    this.pendingRequests.clear();
  }
}
