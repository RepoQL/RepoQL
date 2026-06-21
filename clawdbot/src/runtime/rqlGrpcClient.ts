import { fileURLToPath } from "url";
import { dirname, resolve } from "path";
import * as grpc from "@grpc/grpc-js";
import * as protoLoader from "@grpc/proto-loader";

const PROTO_PATH = resolve(
  dirname(fileURLToPath(import.meta.url)),
  "..",
  "proto",
  "hosting.proto"
);

const packageDefinition = protoLoader.loadSync(PROTO_PATH, {
  defaults: true,
  enums: Number,
  longs: Number,
  oneofs: true,
});

const proto = grpc.loadPackageDefinition(packageDefinition) as any;
const services = proto.repoql.hosting.v1;
const ToolService = services.ToolService;
const ImportService = services.ImportService;
const ManagementCommandService = services.ManagementCommandService;
const AccountService = services.AccountService;
const HostLifecycle = services.HostLifecycle;
const StatusService = services.StatusService;

/** Proto CommandSurface enum values (enums loaded as numbers). */
export const CommandSurface = {
  Unspecified: 0,
  Cli: 1,
  Mcp: 2,
} as const;

/** Proto ConceptCategory enum values. */
export const ConceptCategory = {
  Unspecified: 0,
  Wisdom: 1,
  Rule: 2,
  Knowledge: 3,
} as const;

/** One streamed AccountService.Login progress frame. */
export interface LoginProgress {
  kind: number; // 0=INFO, 1=WARNING, 2=COMPLETE, 3=ERROR
  message: string;
  displayName: string;
}

/**
 * Typed gRPC client for the rql host. Mirrors the surface the MCP server uses:
 * the agent-facing ToolService plus the ImportService, ManagementCommandService,
 * AccountService, HostLifecycle, and StatusService that back the command tool.
 */
export class RqlGrpcClient {
  readonly socketPath: string;
  private readonly tools: any;
  private readonly imports: any;
  private readonly management: any;
  private readonly account: any;
  private readonly lifecycle: any;
  private readonly status: any;
  private readonly defaultTimeoutMs: number;

  constructor(socketPath: string, defaultTimeoutMs: number) {
    this.socketPath = socketPath;
    this.defaultTimeoutMs = defaultTimeoutMs;
    const target = `unix:${socketPath}`;
    const credentials = grpc.credentials.createInsecure();
    this.tools = new ToolService(target, credentials);
    this.imports = new ImportService(target, credentials);
    this.management = new ManagementCommandService(target, credentials);
    this.account = new AccountService(target, credentials);
    this.lifecycle = new HostLifecycle(target, credentials);
    this.status = new StatusService(target, credentials);
  }

  close(): void {
    this.tools.close?.();
    this.imports.close?.();
    this.management.close?.();
    this.account.close?.();
    this.lifecycle.close?.();
    this.status.close?.();
  }

  // --- ToolService — the agent-facing API ---------------------------------

  query(request: Record<string, unknown>, timeoutMs?: number): Promise<any> {
    return this.call(this.tools, "Query", request, timeoutMs);
  }

  explore(request: Record<string, unknown>, timeoutMs?: number): Promise<any> {
    return this.call(this.tools, "Explore", request, timeoutMs);
  }

  explain(request: Record<string, unknown>, timeoutMs?: number): Promise<any> {
    return this.call(this.tools, "Explain", request, timeoutMs);
  }

  read(request: Record<string, unknown>, timeoutMs?: number): Promise<any> {
    return this.call(this.tools, "Read", request, timeoutMs);
  }

  keywords(request: Record<string, unknown>, timeoutMs?: number): Promise<any> {
    return this.call(this.tools, "Keywords", request, timeoutMs);
  }

  execute(request: Record<string, unknown>, timeoutMs?: number): Promise<any> {
    return this.call(this.tools, "Execute", request, timeoutMs);
  }

  captureConcept(request: Record<string, unknown>, timeoutMs?: number): Promise<any> {
    return this.call(this.tools, "CaptureConcept", request, timeoutMs);
  }

  // --- ImportService -------------------------------------------------------

  importRepository(request: Record<string, unknown>, timeoutMs?: number): Promise<any> {
    const method = String(request.uri ?? "").trim().startsWith("-") ? "RemoveImport" : "Import";
    return this.call(this.imports, method, request, timeoutMs);
  }

  listImports(timeoutMs?: number): Promise<any> {
    return this.call(this.imports, "ListImports", {}, timeoutMs);
  }

  // --- ManagementCommandService — the `command` tool's fallthrough --------

  command(request: Record<string, unknown>, timeoutMs?: number): Promise<any> {
    return this.call(this.management, "Execute", request, timeoutMs);
  }

  commandList(request: Record<string, unknown>, timeoutMs?: number): Promise<any> {
    return this.call(this.management, "List", request, timeoutMs);
  }

  // --- AccountService — cloud identity ------------------------------------

  whoAmI(timeoutMs?: number): Promise<any> {
    return this.call(this.account, "WhoAmI", {}, timeoutMs);
  }

  logout(timeoutMs?: number): Promise<any> {
    return this.call(this.account, "Logout", {}, timeoutMs);
  }

  /**
   * Server-streaming login. Invokes onProgress for each frame and resolves once
   * the stream ends. The browser/device-code flow is driven entirely host-side;
   * the frames carry the user-facing instructions.
   */
  login(
    request: Record<string, unknown>,
    onProgress: (frame: LoginProgress) => void,
    timeoutMs?: number
  ): Promise<void> {
    const deadline = new Date(Date.now() + (timeoutMs ?? this.defaultTimeoutMs));
    return new Promise((resolveLogin, rejectLogin) => {
      const stream: grpc.ClientReadableStream<LoginProgress> = this.account.Login(
        request,
        new grpc.Metadata(),
        { deadline }
      );
      stream.on("data", (frame: LoginProgress) => onProgress(frame));
      stream.on("error", (err: grpc.ServiceError) => rejectLogin(err));
      stream.on("end", () => resolveLogin());
    });
  }

  // --- HostLifecycle / StatusService — host control -----------------------

  shutdown(timeoutMs?: number): Promise<any> {
    return this.call(this.lifecycle, "Shutdown", {}, timeoutMs);
  }

  getStatus(timeoutMs?: number): Promise<any> {
    return this.call(this.status, "GetStatus", {}, timeoutMs);
  }

  private call(
    service: any,
    method: string,
    request: Record<string, unknown>,
    timeoutMs?: number
  ): Promise<any> {
    const deadline = new Date(Date.now() + (timeoutMs ?? this.defaultTimeoutMs));

    return new Promise((resolveCall, rejectCall) => {
      service[method](
        request,
        new grpc.Metadata(),
        { deadline },
        (err: grpc.ServiceError | null, response: unknown) => {
          if (err) {
            rejectCall(err);
            return;
          }
          resolveCall(response);
        }
      );
    });
  }
}
