import { fileURLToPath } from "url";
import { dirname, resolve } from "path";
import * as grpc from "@grpc/grpc-js";
import * as protoLoader from "@grpc/proto-loader";

// The plugin speaks two rings of the rql wire contract, vendored verbatim from
// RepoQL.Core (src/L3/RepoQL.Hosting.Contracts/Protos) by `npm run sync`:
//   repoql.host.v1  — find the host, identify it, hold it open, shut it down.
//   repoql.tools.v1 — the ToolCatalog service (DescribeTools + CallTool), the
//                     dynamic, MCP-shaped door built for harness bridges, plus
//                     ToolService's two session-scoped hint surfaces, which the
//                     plugin calls itself rather than exposing as tools.
// Everything an agent can do goes through CallTool, so a new host tool needs no
// plugin code — only a manifest entry (see scripts/sync.mjs).
const PROTO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "..", "proto");

const packageDefinition = protoLoader.loadSync(
  ["repoql/host/v1/host.proto", "repoql/tools/v1/tools.proto"],
  {
    includeDirs: [PROTO_ROOT],
    defaults: true,
    enums: String,
    longs: Number,
    oneofs: true,
  }
);

const proto = grpc.loadPackageDefinition(packageDefinition) as any;
const HostService = proto.repoql.host.v1.HostService;
const ToolCatalog = proto.repoql.tools.v1.ToolCatalog;
const ToolService = proto.repoql.tools.v1.ToolService;

/** Ask a hint surface for its rendered text only. */
const RENDERED_ONLY = { paths: ["rendered"] };

/** Rendered concepts plus the matches, whose count the caller budgets by. */
const RENDERED_AND_CONCEPTS = { paths: ["rendered", "concepts"] };

/** Concepts surfaced for one write target. */
export interface SurfacedConcepts {
  rendered: string;
  count: number;
}

/** One MCP-compatible tool definition, as DescribeTools returns it. */
export interface ToolDefinition {
  name: string;
  title: string;
  description: string;
  inputSchemaJson: string;
  readOnly: boolean;
  annotationsJson: string;
  metaJson: string;
}

/** The trust footer every tool result carries. */
export interface ResponseFooter {
  totalFiles?: number;
  pendingFiles?: number;
  failedFiles?: number;
  semanticReady?: boolean;
  semanticPercent?: number;
  elapsedMs?: number;
  tokensUsed?: number;
  statusLine?: string;
}

/** The rendered outcome of CallTool. */
export interface CatalogToolResult {
  rendered: string;
  isError: boolean;
  footer?: ResponseFooter | null;
}

export interface CallProgress {
  progress: number;
  total: number;
  message: string;
}

export interface HostInfo {
  version: string;
  commit: string;
  run?: { pid: number; startedAt?: unknown } | null;
  workspaceRoot: string;
  implicitStart: boolean;
  capabilities: string[];
}

export interface Readiness {
  level: string; // READINESS_LEVEL_STARTING | _QUERYABLE | _SEARCHABLE | _COMPLETE
  semanticEnabled: boolean;
  semanticReady: boolean;
  semanticPercent: number;
  totalFiles: number;
  pendingFiles: number;
  failedFiles: number;
}

/** Identity every call carries as gRPC metadata (host.v1 CALL METADATA). */
export interface CallIdentity {
  /** rql-agent: the calling harness as a product token. */
  agent: string;
  /** rql-session: stable id of the logical agent session, when known. */
  sessionId?: string;
}

export interface CallToolOptions {
  identity: CallIdentity;
  timeoutMs: number;
  signal?: AbortSignal;
  onProgress?: (progress: CallProgress) => void;
}

export interface LeaseBeat {
  clientId: string;
  clientName: string;
  clientVersion: string;
  pid: number;
}

export interface LeaseHandlers {
  onAccepted(beatIntervalMs: number): void;
  onStopping(reason: string): void;
  onClosed(error?: grpc.ServiceError): void;
}

/** A held lease; close() releases it. */
export interface LeaseHandle {
  close(): void;
}

/** gRPC client for one rql host socket: the session ring plus the tool catalog. */
export class RqlGrpcClient {
  readonly socketPath: string;
  private readonly host: any;
  private readonly catalog: any;
  private readonly tools: any;
  private readonly defaultTimeoutMs: number;

  constructor(socketPath: string, defaultTimeoutMs: number) {
    this.socketPath = socketPath;
    this.defaultTimeoutMs = defaultTimeoutMs;
    const target = `unix:${socketPath}`;
    const credentials = grpc.credentials.createInsecure();
    this.host = new HostService(target, credentials);
    // Share the HostService channel so the lease and tool calls ride one connection.
    this.catalog = new ToolCatalog(target, credentials, { channelOverride: this.host.getChannel() });
    this.tools = new ToolService(target, credentials, { channelOverride: this.host.getChannel() });
  }

  close(): void {
    this.host.close?.();
  }

  // --- repoql.host.v1 -------------------------------------------------------

  getHostInfo(timeoutMs?: number): Promise<HostInfo> {
    return this.unary(this.host, "GetHostInfo", {}, timeoutMs);
  }

  getReadiness(timeoutMs?: number): Promise<Readiness> {
    return this.unary(this.host, "GetReadiness", {}, timeoutMs);
  }

  shutdown(reason: string, timeoutMs?: number): Promise<{ processId: number; machineId: string }> {
    return this.unary(this.host, "Shutdown", { reason }, timeoutMs);
  }

  /**
   * Open HoldLease and beat at the interval the host advertises. The host keeps
   * an implicitly-started instance alive while any lease is held.
   */
  holdLease(beat: LeaseBeat, handlers: LeaseHandlers): LeaseHandle {
    const call: grpc.ClientDuplexStream<LeaseBeat, any> = this.host.HoldLease(new grpc.Metadata());
    let timer: NodeJS.Timeout | undefined;
    let closed = false;

    const finish = (error?: grpc.ServiceError): void => {
      if (closed) {
        return;
      }
      closed = true;
      if (timer) {
        clearInterval(timer);
      }
      handlers.onClosed(error);
    };

    call.on("data", (event: any) => {
      if (event?.accepted) {
        const intervalMs = durationMs(event.accepted.beatInterval) || 10_000;
        timer = setInterval(() => {
          if (!closed) {
            call.write(beat);
          }
        }, intervalMs);
        timer.unref();
        handlers.onAccepted(intervalMs);
      } else if (event?.stopping) {
        handlers.onStopping(String(event.stopping.reason ?? ""));
      }
    });
    call.on("error", (err: grpc.ServiceError) => finish(err));
    call.on("end", () => finish());

    call.write(beat);
    return {
      close: () => {
        if (closed) {
          return;
        }
        call.end();
        call.cancel();
        finish();
      },
    };
  }

  // --- repoql.tools.v1.ToolCatalog -------------------------------------------

  async describeTools(timeoutMs?: number): Promise<ToolDefinition[]> {
    const response = await this.unary(this.catalog, "DescribeTools", {}, timeoutMs);
    return Array.isArray(response?.tools) ? response.tools : [];
  }

  /** Invoke a tool by name; streams Progress events, resolves with the ToolResult. */
  callTool(tool: string, args: Record<string, unknown>, options: CallToolOptions): Promise<CatalogToolResult> {
    const deadline = new Date(Date.now() + options.timeoutMs);
    return new Promise((resolveCall, rejectCall) => {
      const call: grpc.ClientReadableStream<any> = this.catalog.CallTool(
        { tool, argumentsJson: JSON.stringify(args) },
        metadataFor(options.identity),
        { deadline }
      );
      let result: CatalogToolResult | undefined;

      const onAbort = (): void => call.cancel();
      options.signal?.addEventListener("abort", onAbort, { once: true });
      const detach = (): void => options.signal?.removeEventListener("abort", onAbort);

      call.on("data", (event: any) => {
        if (event?.result) {
          result = event.result as CatalogToolResult;
        } else if (event?.progress) {
          options.onProgress?.(event.progress as CallProgress);
        }
      });
      call.on("error", (err) => {
        detach();
        rejectCall(err);
      });
      call.on("end", () => {
        detach();
        if (result) {
          resolveCall(result);
        } else {
          rejectCall(new Error(`RepoQL host ended the '${tool}' call without a result.`));
        }
      });
    });
  }

  // --- repoql.tools.v1.ToolService hint surfaces -------------------------------
  // Both suppress what the rql-session has already been shown, so repeated
  // calls stay quiet; the identity must carry the session for that to hold.

  /** Concept invariants governing a write target, rendered for an agent. Empty when none apply. */
  async surfaceConcepts(target: string, limit: number, identity: CallIdentity, timeoutMs: number): Promise<SurfacedConcepts> {
    const response = await this.unary(
      this.tools,
      "SurfaceConcepts",
      { target, limit, responseMask: RENDERED_AND_CONCEPTS },
      timeoutMs,
      metadataFor(identity)
    );
    return {
      rendered: String(response?.rendered ?? ""),
      count: Array.isArray(response?.concepts) ? response.concepts.length : 0,
    };
  }

  /** Repository vocabulary defined in delivered read content, rendered for an agent. Empty when none. */
  async surfaceVocabulary(
    target: string,
    content: string,
    limit: number,
    maxChars: number,
    identity: CallIdentity,
    timeoutMs: number
  ): Promise<string> {
    const response = await this.unary(
      this.tools,
      "SurfaceVocabulary",
      { target, content, limit, maxChars, responseMask: RENDERED_ONLY },
      timeoutMs,
      metadataFor(identity)
    );
    return String(response?.rendered ?? "");
  }

  private unary(
    service: any,
    method: string,
    request: Record<string, unknown>,
    timeoutMs?: number,
    metadata: grpc.Metadata = new grpc.Metadata()
  ): Promise<any> {
    const deadline = new Date(Date.now() + (timeoutMs ?? this.defaultTimeoutMs));
    return new Promise((resolveCall, rejectCall) => {
      service[method](request, metadata, { deadline }, (err: grpc.ServiceError | null, response: unknown) =>
        err ? rejectCall(err) : resolveCall(response)
      );
    });
  }
}

function metadataFor(identity: CallIdentity): grpc.Metadata {
  const metadata = new grpc.Metadata();
  metadata.set("rql-agent", identity.agent);
  if (identity.sessionId) {
    metadata.set("rql-session", identity.sessionId);
  }
  return metadata;
}

function durationMs(duration: { seconds?: number; nanos?: number } | null | undefined): number {
  if (!duration) {
    return 0;
  }
  return Number(duration.seconds ?? 0) * 1000 + Math.floor(Number(duration.nanos ?? 0) / 1e6);
}
