/**
 * Error types and normalization for MCP client.
 *
 * Purpose: Provides consistent error handling across the plugin.
 * Complexity: Converts various error types to McpToolResult with isError: true.
 */

import type { McpToolResult } from "./types.js";

/**
 * Error thrown when MCP client encounters a connection issue.
 */
export class McpConnectionError extends Error {
  constructor(
    message: string,
    public readonly cause?: Error
  ) {
    super(message);
    this.name = "McpConnectionError";
  }
}

/**
 * Error thrown when a request times out.
 */
export class McpTimeoutError extends Error {
  constructor(
    public readonly method: string,
    public readonly timeoutMs: number
  ) {
    super(`Request '${method}' timed out after ${timeoutMs}ms`);
    this.name = "McpTimeoutError";
  }
}

/**
 * Error thrown when MCP server returns a JSON-RPC error.
 */
export class McpRpcError extends Error {
  constructor(
    public readonly code: number,
    message: string,
    public readonly data?: unknown
  ) {
    super(message);
    this.name = "McpRpcError";
  }
}

/**
 * Error thrown when RepoQL executable cannot be found.
 */
export class RepoQlNotFoundError extends Error {
  constructor(searchedPaths: string[]) {
    super(
      `RepoQL executable not found. Searched: ${searchedPaths.join(", ")}. ` +
        `Install RepoQL or set exePath in plugin config.`
    );
    this.name = "RepoQlNotFoundError";
  }
}

/**
 * Normalizes any error to a consistent McpToolResult with isError: true.
 *
 * @param error - The error to normalize
 * @returns McpToolResult with error message as text content
 */
export function normalizeError(error: unknown): McpToolResult {
  const message = getErrorMessage(error);
  return {
    content: [{ type: "text", text: `RepoQL error: ${message}` }],
    isError: true,
  };
}

/**
 * Extracts a human-readable message from any error type.
 */
function getErrorMessage(error: unknown): string {
  if (error instanceof McpConnectionError) {
    return error.cause
      ? `${error.message}: ${error.cause.message}`
      : error.message;
  }

  if (error instanceof McpTimeoutError) {
    return error.message;
  }

  if (error instanceof McpRpcError) {
    return error.data ? `${error.message} (${JSON.stringify(error.data)})` : error.message;
  }

  if (error instanceof RepoQlNotFoundError) {
    return error.message;
  }

  if (error instanceof Error) {
    return error.message;
  }

  return String(error);
}
