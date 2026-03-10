/** Pipeline phase names — matches the indexing pipeline stages */
export type PipelinePhase =
  | 'idle'
  | 'discovery'
  | 'classifying'
  | 'parsing'
  | 'struct_embedding'
  | 'full_embedding'
  | 'rebuilding'
  | 'complete';

/** Display labels for each phase */
export const PHASE_LABELS: Record<PipelinePhase, string> = {
  idle: 'Initializing',
  discovery: 'Discovering files',
  classifying: 'Classifying languages',
  parsing: 'Parsing & building graph',
  struct_embedding: 'Structure embedding → searchable',
  full_embedding: 'Full-text embedding',
  rebuilding: 'Building search index',
  complete: 'Codebase ready',
};

/** File processing state — ordered by pipeline progression */
export type FileState =
  | 'hidden'
  | 'discovered'
  | 'classified'
  | 'parsed'
  | 'struct_embedded'
  | 'full_embedded'
  | 'failed';

/** Language metadata */
export interface Language {
  name: string;
  color: string;
}

/** Well-known language colors — GitHub-inspired */
export const LANGUAGES: Record<string, Language> = {
  '.cs': { name: 'C#', color: '#68217a' },
  '.ts': { name: 'TypeScript', color: '#3178c6' },
  '.py': { name: 'Python', color: '#3572a5' },
  '.rs': { name: 'Rust', color: '#dea584' },
  '.go': { name: 'Go', color: '#00add8' },
  '.java': { name: 'Java', color: '#b07219' },
  '.md': { name: 'Markdown', color: '#083fa1' },
  '.json': { name: 'JSON', color: '#40d47e' },
  '.yaml': { name: 'YAML', color: '#cb171e' },
  '.toml': { name: 'TOML', color: '#9c4221' },
  '.js': { name: 'JavaScript', color: '#f1e05a' },
  '.rb': { name: 'Ruby', color: '#701516' },
  '.cpp': { name: 'C++', color: '#f34b7d' },
  '.css': { name: 'CSS', color: '#563d7c' },
  '.html': { name: 'HTML', color: '#e34c26' },
  '.sql': { name: 'SQL', color: '#e38c00' },
  '.sh': { name: 'Shell', color: '#89e051' },
  '.xml': { name: 'XML', color: '#0060ac' },
  '.proto': { name: 'Protobuf', color: '#4a6a87' },
};

/** A file in the treemap */
export interface FileEntry {
  id: number;
  path: string;
  ext: string;
  lang: Language;
  state: FileState;
  /** Currently being processed */
  processing: boolean;
  /** Line count (when indexed) */
  lines?: number | null;
  /** Token count (from artifact) */
  tokens?: number | null;
  /** Symbol count (when indexed) */
  symbols?: number | null;
  /** Embedding chunk count */
  chunks?: number | null;
  /** When indexed (ISO string) */
  indexedAt?: string | null;
  /** When embedded (ISO string) */
  embeddedAt?: string | null;
  /** Error message (when failed) */
  error?: string | null;
  /** Symbol tree for tooltip */
  tree?: SymbolNode[] | null;
  /** X-ray headline — essential identity in a single line */
  headline?: string | null;
  /** X-ray structure — detailed outline for navigation */
  structure?: string | null;
}

/** Compact symbol node from the server */
export interface SymbolNode {
  /** Short name */
  n: string;
  /** Simplified kind (type, member, function, etc.) */
  k: string;
  /** Start line */
  l?: number | null;
  /** End line */
  e?: number | null;
  /** Member count (for containers) */
  m?: number | null;
}

/** A group of files in the treemap — by phase, directory, or any other axis */
export interface FileGroup {
  label: string;
  files: FileEntry[];
}

/** A source section — local repo or an imported repo */
export interface SourceSection {
  /** Display label — repo name or "Local" */
  label: string;
  /** Source prefix for matching — empty string for local, "github://owner/repo" for imports */
  prefix: string;
  /** File groups within this source */
  groups: FileGroup[];
  /** Total file count */
  total: number;
}

/** Aggregate pipeline counts */
export interface PipelineState {
  total: number;
  discovered: number;
  classified: number;
  parsed: number;
  structEmbedded: number;
  fullEmbedded: number;
  failed: number;
  phase: PipelinePhase;
  /** Files/sec throughput */
  rate: number;
}

/** An entry in the activity stream */
export interface ActivityEntry {
  id: number;
  operation: string;
  path: string;
  langColor: string;
  timestamp: number;
}

/** Language distribution entry */
export interface LanguageCount {
  ext: string;
  lang: Language;
  count: number;
  fraction: number;
}

// ─── Error Panel ───

export type ErrorCategory = 'parse' | 'encoding' | 'timeout' | 'unsupported' | 'io' | 'unknown';

export const ERROR_CATEGORY_LABELS: Record<ErrorCategory, string> = {
  parse: 'Parse error',
  encoding: 'Encoding error',
  timeout: 'Timeout',
  unsupported: 'Unsupported format',
  io: 'I/O error',
  unknown: 'Unknown error',
};

export interface FileError {
  path: string;
  lang: Language;
  category: ErrorCategory;
  message: string;
  /** Optional hint for recovery — e.g. "check file encoding" */
  hint?: string;
}

// ─── Query Activity ───

export type ToolName = 'explore' | 'explain' | 'query' | 'read';

export type QueryState = 'running' | 'completed' | 'failed';

export interface QueryEntry {
  id: number;
  tool: ToolName;
  state: QueryState;
  /** Key parameter summary — e.g. intent, keywords, URI glob, SQL snippet */
  params: string;
  /** Token budget requested */
  tokenBudget: number;
  /** Tokens actually returned */
  tokensUsed: number;
  /** Wall-clock ms */
  elapsed: number;
  /** Brief result summary — e.g. "12 files matched", "3 symbols found" */
  resultSummary: string;
  timestamp: number;
}
// ─── Connection Status ───

export type HostStatus = 'connected' | 'disconnected' | 'reconnecting';

export interface HostHealth {
  status: HostStatus;
  /** Round-trip latency in ms */
  latencyMs: number;
  /** Seconds since host started */
  uptimeSeconds: number;
  /** Version string from the host */
  version: string;
}

// ─── Client Leases ───

export interface InFlightRequest {
  /** Which tool is being called */
  tool: ToolName;
  /** Key params summary */
  params: string;
  /** When this request started (epoch ms) */
  startedAt: number;
  /** Token budget for this request */
  tokenBudget: number;
}

export interface ClientLease {
  /** Unique client/session ID */
  id: string;
  /** Human-readable client name — e.g. "claude-code", "web-ui", "clawdbot" */
  name: string;
  /** When the client connected (epoch ms) */
  connectedAt: number;
  /** Currently in-flight request, or null if idle */
  activeRequest: InFlightRequest | null;
  /** Completed request count this session */
  requestCount: number;
  /** Total tokens consumed this session */
  totalTokensUsed: number;
}

// ─── Operations ───

export type OperationKind = 'startup' | 'reindex' | 'import';

export type OperationState =
  | 'running'
  | 'completed'
  | 'completed_with_failures'
  | 'cancelled';

export interface OperationMilestone {
  name: string;
  detail?: string;
  timestamp: number;
}

export interface OperationLogEntry {
  type: string;
  message?: string;
  uri?: string;
  timestamp: number;
}

export interface OperationSnapshot {
  id: string;
  kind: OperationKind;
  /** Human-readable — e.g. "reindex: file:///src/**" or "import: github://owner/repo" */
  description: string;
  state: OperationState;
  /** When created (epoch ms) */
  createdAt: number;
  /** When completed (epoch ms), null if still running */
  completedAt: number | null;
  /** Progress counts */
  totalFiles: number;
  indexedCount: number;
  embeddedCount: number;
  failedCount: number;
  /** 0-100 */
  readyPercent: number;
  /** Milestones reached — scan_complete, hot_path_complete, ready, etc. */
  milestones: OperationMilestone[];
  /** Recent log entries (state transitions) */
  recentLog: OperationLogEntry[];
}
