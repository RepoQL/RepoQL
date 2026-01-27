/**
 * JSON-RPC 2.0 and MCP protocol type definitions.
 *
 * Purpose: Provides type-safe interfaces for MCP client communication.
 * Complexity: Minimal - these are just type definitions for the standard protocols.
 */

/**
 * JSON-RPC 2.0 request structure.
 */
export interface JsonRpcRequest {
  jsonrpc: "2.0";
  id: number;
  method: string;
  params?: object;
}

/**
 * JSON-RPC 2.0 successful response.
 */
export interface JsonRpcSuccessResponse<T = unknown> {
  jsonrpc: "2.0";
  id: number;
  result: T;
}

/**
 * JSON-RPC 2.0 error response.
 */
export interface JsonRpcErrorResponse {
  jsonrpc: "2.0";
  id: number;
  error: {
    code: number;
    message: string;
    data?: unknown;
  };
}

/**
 * JSON-RPC 2.0 response (success or error).
 */
export type JsonRpcResponse<T = unknown> =
  | JsonRpcSuccessResponse<T>
  | JsonRpcErrorResponse;

/**
 * MCP tool call result - single content item.
 */
export interface McpContentItem {
  type: "text" | "image" | "resource";
  text?: string;
  data?: string;
  mimeType?: string;
  resource?: {
    uri: string;
    mimeType?: string;
    text?: string;
  };
}

/**
 * MCP tool call result.
 */
export interface McpToolResult {
  content: McpContentItem[];
  isError?: boolean;
}

/**
 * MCP initialize request params.
 */
export interface McpInitializeParams {
  protocolVersion: string;
  capabilities: {
    tools?: Record<string, unknown>;
    resources?: Record<string, unknown>;
    prompts?: Record<string, unknown>;
  };
  clientInfo: {
    name: string;
    version: string;
  };
}

/**
 * MCP initialize response result.
 */
export interface McpInitializeResult {
  protocolVersion: string;
  capabilities: {
    tools?: Record<string, unknown>;
    resources?: Record<string, unknown>;
    prompts?: Record<string, unknown>;
  };
  serverInfo: {
    name: string;
    version: string;
  };
}

/**
 * MCP tools/call request params.
 */
export interface McpToolCallParams {
  name: string;
  arguments?: Record<string, unknown>;
}

/**
 * Represents a pending request waiting for a response.
 */
export interface PendingRequest<T = unknown> {
  resolve: (value: T) => void;
  reject: (error: Error) => void;
  timeoutId: NodeJS.Timeout;
}
