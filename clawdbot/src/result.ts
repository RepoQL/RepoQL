import type { ServiceError } from "@grpc/grpc-js";
import { status as GrpcStatus } from "@grpc/grpc-js";

/** Structured details attached to every tool result for logs/UI rendering. */
export interface RepoQlToolDetails {
  tool: string;
  [key: string]: unknown;
}

/** Minimal structural form of an OpenClaw AgentToolResult text block. */
export interface ToolResult {
  content: Array<{ type: "text"; text: string }>;
  details: RepoQlToolDetails;
}

/** Build a successful text tool result. */
export function textResult(text: string, details: RepoQlToolDetails): ToolResult {
  return {
    content: [{ type: "text", text: text.length > 0 ? text : "(no output)" }],
    details,
  };
}

/**
 * Throw an actionable tool error. OpenClaw surfaces the thrown message to the
 * model as the tool's error result, so the message must point the way back to
 * the path — never a bare stack trace.
 */
export function toolError(message: string): never {
  throw new Error(message);
}

/**
 * Map a failed gRPC call to an actionable message. UNAVAILABLE means the host
 * socket is gone; everything else carries the server's own detail, which the
 * host writes as a recovery hint (e.g. "Unknown command 'x'. Did you mean 'y'?").
 */
export function describeGrpcError(error: unknown, timeoutMs?: number): string {
  if (isServiceError(error)) {
    if (error.code === GrpcStatus.UNAVAILABLE) {
      return "RepoQL host is unavailable. Run `rql serve` in the workspace, or retry — the plugin relaunches it when autoStart is on.";
    }
    if (error.code === GrpcStatus.DEADLINE_EXCEEDED) {
      const limit = timeoutMs ? ` after ${Math.round(timeoutMs / 1000)}s` : "";
      return (
        `RepoQL request timed out${limit}. Retry with a narrower scope. If every call times out, the host is ` +
        "busy or stuck: `rql host status` in the workspace shows which, and `rql host restart` restarts it."
      );
    }
    if (error.code === GrpcStatus.CANCELLED) {
      return "RepoQL request was cancelled.";
    }
    const detail = error.details?.trim();
    if (detail) {
      return detail;
    }
  }
  return error instanceof Error ? error.message : String(error);
}

function isServiceError(error: unknown): error is ServiceError {
  return (
    typeof error === "object" &&
    error !== null &&
    "code" in error &&
    typeof (error as { code: unknown }).code === "number"
  );
}
