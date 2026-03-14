import { createSignal, createMemo, createEffect, onMount, onCleanup, batch } from 'solid-js';
import { throttle, leadingAndTrailing, scheduleIdle } from '@solid-primitives/scheduled';
import type {
  ActivityEntry,
  ErrorCategory,
  FileEntry,
  FileError,
  FileGroup,
  FileState,
  IndexingDiagnosticsState,
  IndexingWorkerEntry,
  Language,
  LanguageCount,
  OperationKind,
  OperationSnapshot,
  OperationState,
  PipelinePhase,
  PipelineStageLoad,
  QueryEntry,
  QueryState,
  StuckItemEntry,
  SourceSection,
  ToolName,
} from '../types';
import { LANGUAGES } from '../types';
import {
  computeLanguageCounts,
  groupBySources,
} from '../fixtures';
import type { DashboardProps } from '../components/Dashboard/Dashboard';

// --- Server response types ---

interface ServerFile {
  path: string;
  ext: string;
  state: string;
  processing: boolean;
  lines?: number | null;
  tokens?: number | null;
  symbols?: number | null;
  chunks?: number | null;
  indexedAt?: string | null;
  embeddedAt?: string | null;
  error?: string | null;
  tree?: Array<{ n: string; k: string; l?: number | null; e?: number | null; m?: number | null }> | null;
  headline?: string | null;
  structure?: string | null;
}

interface ServerQuery {
  id: number;
  tool?: string | null;
  params?: string | null;
  tokenBudget?: number | null;
  tokensUsed?: number | null;
  elapsedMs?: number | null;
  resultSummary?: string | null;
  timestampUtc?: string | null;
  state?: string | null;
}

interface SnapshotResponse {
  host: {
    repositoryPath: string;
    startedAt: string;
    dashboardUrl: string | null;
    initialIndexingCompleted: boolean;
  };
  pipeline: {
    stages: Array<{ name: string; busy: boolean; queued: number; inProgress: number }>;
    reindexing: boolean;
    writerPending: boolean;
  };
  operations: Array<{
    id: string;
    description: string;
    state: string;
    createdAt: string;
    progress: {
      totalFiles: number;
      indexedCount: number;
      embeddedCount: number;
      failedCount: number;
      readyPercent: number;
    };
  }>;
  queries: ServerQuery[];
  indexing?: SnapshotIndexing;
  files: ServerFile[];
}

interface SnapshotIndexing {
  hotPathTimeouts?: number | null;
  analysisTimeouts?: number | null;
  deferredRetryTimeouts?: number | null;
  deferredRetryPending?: number | null;
  deferredRetryActive?: number | null;
  deferredToIdleCount?: number | null;
  activeWorkers?: Array<{
    queue?: string | null;
    workerId?: number | null;
    uri?: string | null;
    name?: string | null;
    stage?: string | null;
    startedAt?: string | null;
    elapsedMs?: number | null;
    timeoutAttempts?: number | null;
    deferredRetry?: boolean | null;
  }> | null;
  stuckItems?: Array<{
    uri?: string | null;
    name?: string | null;
    stage?: string | null;
    status?: string | null;
    enqueuedAt?: string | null;
    startedAt?: string | null;
    elapsedMs?: number | null;
    workerId?: number | null;
    timeoutAttempts?: number | null;
    deferredRetry?: boolean | null;
    size?: number | null;
    mimeType?: string | null;
  }> | null;
}

interface PipelineStageEvent {
  name: string;
  busy: boolean;
  queued: number;
  inProgress: number;
  processedTotal?: number;
  throughputPerSec?: number;
}

interface PipelineEvent {
  stages?: PipelineStageEvent[];
  ready?: boolean;
  reindexing?: boolean;
  writerPending?: boolean;
}

interface SnapshotHost {
  repositoryPath?: string | null;
  startedAt?: string | null;
  dashboardUrl?: string | null;
  initialIndexingCompleted?: boolean | null;
}

interface SnapshotPipeline {
  stages?: Array<{ name: string; busy: boolean; queued: number; inProgress: number }> | null;
  reindexing?: boolean | null;
  writerPending?: boolean | null;
}

interface IndexingEvent extends SnapshotIndexing {}

interface ActivityEvent {
  type?: string;
  operation?: string;
  message?: string;
  uri?: string;
  path?: string;
  langColor?: string;
  timestamp?: number | string;
  queuedCount?: number;
  processedCount?: number;
}

const ACTIVITY_LABELS: Record<string, string> = {
  IndexingActivityFileChanged: 'Changed',
  IndexingActivityEmbeddingsGenerated: 'Indexed',
};

const HIGH_SIGNAL_FILE_STATES = new Set<FileState>([
  'parsed',
  'struct_embedded',
  'full_embedded',
  'failed',
]);

const LOW_SIGNAL_ACTIVITY_TYPES = new Set([
  'IndexingActivityBatchComplete',
  'IndexingActivityFileDiscovered',
  'IndexingActivityFileParsed',
  'IndexingActivityFileAnalyzed',
]);

interface ActivityDraft {
  operation: string;
  path: string;
  langColor: string;
}

/** Keep enough of the path to distinguish repeated filenames */
function compactPath(uri: string): string {
  const normalized = uri.replace(/\\/g, '/');

  if (normalized.startsWith('github://')) {
    const segments = normalized.slice('github://'.length).split('/').filter(Boolean);
    if (segments.length <= 4) {
      return `github/${segments.join('/')}`;
    }

    return `github/${segments[0]}/${segments[1]}/.../${segments.slice(-2).join('/')}`;
  }

  const withoutScheme = normalized.replace(/^[a-z]+:\/\/\/?/i, '');
  const segments = withoutScheme.split('/').filter(Boolean);
  if (segments.length <= 2) {
    return segments.join('/') || uri;
  }

  return `.../${segments.slice(-2).join('/')}`;
}

interface HealthEvent {
  message?: string;
  status?: string;
  timestamp?: number | string;
}

const MAX_ACTIVITY_ENTRIES = 50;
const MAX_ERROR_ENTRIES = 24;
const MAX_QUERY_ENTRIES = 24;

// --- Referential equality for arrays (used by stability caches) ---

function sameItems<T>(a: T[], b: T[]): boolean {
  if (a.length !== b.length) return false;
  for (let i = 0; i < a.length; i++) {
    if (a[i] !== b[i]) return false;
  }
  return true;
}

// --- Map server files to client FileEntry[] ---

function mapServerFile(f: ServerFile, id: number): FileEntry {
  return {
    id,
    path: f.path,
    ext: f.ext,
    lang: lookupLang(f.ext),
    state: (f.state as FileState) || 'discovered',
    processing: f.processing,
    lines: f.lines,
    tokens: f.tokens,
    symbols: f.symbols,
    chunks: f.chunks,
    indexedAt: f.indexedAt,
    embeddedAt: f.embeddedAt,
    error: f.error,
    tree: f.tree,
    headline: f.headline,
    structure: f.structure,
  };
}

function lookupLang(ext: string): Language {
  const normalized = ext.toLowerCase();
  return LANGUAGES[normalized] ?? { name: normalized.slice(1) || 'Other', color: '#555560' };
}

// --- Map server operations to client type ---

const STATE_MAP: Record<string, OperationState> = {
  Running: 'running',
  Completed: 'completed',
  CompletedWithFailures: 'completed_with_failures',
  Cancelled: 'cancelled',
};

function mapServerOperation(s: SnapshotResponse['operations'][number] | null | undefined): OperationSnapshot {
  const description = s?.description?.trim() || 'Operation';
  const state = STATE_MAP[s?.state ?? ''] ?? 'running';
  return {
    id: s?.id ?? crypto.randomUUID(),
    kind: inferOperationKind(description),
    description,
    state,
    createdAt: Date.parse(s?.createdAt ?? '') || Date.now(),
    completedAt: state !== 'running' ? Date.now() : null,
    totalFiles: s?.progress?.totalFiles ?? 0,
    indexedCount: s?.progress?.indexedCount ?? 0,
    embeddedCount: s?.progress?.embeddedCount ?? 0,
    failedCount: s?.progress?.failedCount ?? 0,
    readyPercent: s?.progress?.readyPercent ?? 0,
    milestones: [],
    recentLog: [],
  };
}

function inferOperationKind(description: string | null | undefined): OperationKind {
  const text = description ?? '';
  if (text.includes('import')) return 'import';
  if (text.includes('reindex') || text.includes('Reindex')) return 'reindex';
  return 'startup';
}

function mapIndexingState(raw: SnapshotIndexing | null | undefined): IndexingDiagnosticsState {
  return {
    hotPathTimeouts: raw?.hotPathTimeouts ?? 0,
    analysisTimeouts: raw?.analysisTimeouts ?? 0,
    deferredRetryTimeouts: raw?.deferredRetryTimeouts ?? 0,
    deferredRetryPending: raw?.deferredRetryPending ?? 0,
    deferredRetryActive: raw?.deferredRetryActive ?? 0,
    deferredToIdleCount: raw?.deferredToIdleCount ?? 0,
    activeWorkers: (raw?.activeWorkers ?? []).map(mapIndexingWorker),
    stuckItems: (raw?.stuckItems ?? []).map(mapStuckItem),
  };
}

function mapIndexingWorker(raw: NonNullable<SnapshotIndexing['activeWorkers']>[number]): IndexingWorkerEntry {
  return {
    queue: raw.queue ?? 'unknown',
    workerId: raw.workerId ?? -1,
    uri: raw.uri ?? '',
    name: raw.name ?? raw.uri ?? 'item',
    stage: raw.stage ?? 'unknown',
    startedAt: Date.parse(raw.startedAt ?? '') || Date.now(),
    elapsedMs: raw.elapsedMs ?? 0,
    timeoutAttempts: raw.timeoutAttempts ?? 0,
    deferredRetry: raw.deferredRetry ?? false,
  };
}

function mapStuckItem(raw: NonNullable<SnapshotIndexing['stuckItems']>[number]): StuckItemEntry {
  return {
    uri: raw.uri ?? '',
    name: raw.name ?? raw.uri ?? 'item',
    stage: raw.stage ?? 'unknown',
    status: raw.status ?? 'queued',
    enqueuedAt: Date.parse(raw.enqueuedAt ?? '') || Date.now(),
    startedAt: raw.startedAt ? (Date.parse(raw.startedAt) || null) : null,
    elapsedMs: raw.elapsedMs ?? null,
    workerId: raw.workerId ?? null,
    timeoutAttempts: raw.timeoutAttempts ?? 0,
    deferredRetry: raw.deferredRetry ?? false,
    size: raw.size ?? 0,
    mimeType: raw.mimeType ?? null,
  };
}

// --- Map server queries to client type ---

function coerceToolName(value: string | null | undefined): ToolName {
  switch ((value ?? '').toLowerCase()) {
    case 'explore':
    case 'explain':
    case 'read':
    case 'query':
      return value!.toLowerCase() as ToolName;
    default:
      return 'query';
  }
}

function coerceQueryState(value: string | null | undefined): QueryState {
  switch ((value ?? '').toLowerCase()) {
    case 'running':
      return 'running';
    case 'failed':
      return 'failed';
    default:
      return 'completed';
  }
}

function mapServerQuery(s: ServerQuery): QueryEntry {
  const state = coerceQueryState(s.state);

  return {
    id: Number.isFinite(s.id) ? s.id : Math.trunc(Date.now() + Math.random() * 1000),
    tool: coerceToolName(s.tool),
    state,
    params: s.params?.trim() || '(no parameters)',
    tokenBudget: Math.max(0, s.tokenBudget ?? 0),
    tokensUsed: Math.max(0, s.tokensUsed ?? 0),
    elapsed: Math.max(0, s.elapsedMs ?? 0),
    resultSummary: s.resultSummary?.trim() || (state === 'running' ? 'Awaiting response' : ''),
    timestamp: coerceTimestamp(s.timestampUtc),
  };
}

function sortQueries(entries: QueryEntry[]): QueryEntry[] {
  const stateRank = (state: QueryState) => {
    switch (state) {
      case 'running':
        return 0;
      case 'failed':
        return 1;
      default:
        return 2;
    }
  };

  return [...entries].sort((a, b) =>
    stateRank(a.state) - stateRank(b.state)
    || b.timestamp - a.timestamp
    || b.id - a.id,
  );
}

function normalizeSnapshot(raw: SnapshotResponse): SnapshotResponse {
  const host = (raw?.host ?? {}) as SnapshotHost;
  const pipeline = (raw?.pipeline ?? {}) as SnapshotPipeline;

  return {
    host: {
      repositoryPath: host.repositoryPath ?? '',
      startedAt: host.startedAt ?? '',
      dashboardUrl: host.dashboardUrl ?? null,
      initialIndexingCompleted: host.initialIndexingCompleted ?? false,
    },
    pipeline: {
      stages: Array.isArray(pipeline.stages) ? pipeline.stages : [],
      reindexing: pipeline.reindexing ?? false,
      writerPending: pipeline.writerPending ?? false,
    },
    operations: Array.isArray(raw?.operations) ? raw.operations : [],
    queries: Array.isArray(raw?.queries) ? raw.queries : [],
    indexing: raw?.indexing ?? {},
    files: Array.isArray(raw?.files) ? raw.files : [],
  };
}

// --- Count files by state ---

function countByState(files: FileEntry[]): Record<string, number> {
  const counts: Record<string, number> = {};
  for (const f of files) {
    counts[f.state] = (counts[f.state] ?? 0) + 1;
  }
  return counts;
}

function classifyErrorCategory(message: string): ErrorCategory {
  const lower = message.toLowerCase();

  if (lower.includes('encoding') || lower.includes('utf-') || lower.includes('byte order mark') || lower.includes('invalid byte')) {
    return 'encoding';
  }
  if (lower.includes('timeout') || lower.includes('timed out') || lower.includes('deadline')) {
    return 'timeout';
  }
  if (lower.includes('unsupported') || lower.includes('not supported') || lower.includes('no loader')) {
    return 'unsupported';
  }
  if (lower.includes('permission denied') || lower.includes('access denied') || lower.includes('file not found') || lower.includes('directory') || lower.includes('path') || lower.includes('i/o') || lower.includes('io exception')) {
    return 'io';
  }
  if (lower.includes('parse') || lower.includes('syntax') || lower.includes('unexpected token')) {
    return 'parse';
  }

  return 'unknown';
}

function summarizeErrorMessage(message: string | null | undefined): string {
  const normalized = (message ?? '').replace(/\s+/g, ' ').trim();
  if (!normalized) {
    return 'Unknown failure';
  }

  return normalized.length <= 140 ? normalized : `${normalized.slice(0, 137)}...`;
}

function buildErrorHint(category: ErrorCategory): string | undefined {
  switch (category) {
    case 'encoding':
      return 'Check file encoding and byte-order markers.';
    case 'timeout':
      return 'Inspect the parser or file size for long-running work.';
    case 'unsupported':
      return 'This format may need a loader or should be excluded.';
    case 'io':
      return 'Check the file path, permissions, and whether it still exists.';
    case 'parse':
      return 'Inspect the file near the reported syntax boundary.';
    default:
      return undefined;
  }
}

function buildFileErrors(files: FileEntry[]): FileError[] {
  return files
    .filter((file) => file.state === 'failed' && !!file.error)
    .map((file) => {
      const message = summarizeErrorMessage(file.error);
      const category = classifyErrorCategory(message);
      return {
        path: compactPath(file.path),
        lang: file.lang,
        category,
        message,
        hint: buildErrorHint(category),
      } satisfies FileError;
    })
    .sort((a, b) => a.path.localeCompare(b.path, undefined, { sensitivity: 'base' }))
    .slice(0, MAX_ERROR_ENTRIES);
}

// --- Hook ---

export function useRepoQLDashboard(): {
  props: () => DashboardProps | null;
  connected: () => boolean;
  error: () => string | null;
} {
  const [connected, setConnected] = createSignal(false);
  const [error, setError] = createSignal<string | null>(null);
  const [snapshot, setSnapshot] = createSignal<SnapshotResponse | null>(null);
  const [pipelineEvent, setPipelineEvent] = createSignal<PipelineEvent | null>(null);
  const [indexingEvent, setIndexingEvent] = createSignal<IndexingEvent | null>(null);
  const [activities, setActivities] = createSignal<ActivityEntry[]>([]);
  const [queries, setQueries] = createSignal<QueryEntry[]>([]);
  const [fileMap, setFileMap] = createSignal<Map<string, ServerFile>>(new Map());
  const [operations, setOperations] = createSignal<OperationSnapshot[]>([]);
  const [now, setNow] = createSignal(Date.now());

  let startTime = Date.now();
  let activityId = 0;

  // --- Referential stability caches ---
  // Solid's <For> uses === to diff items. New objects = full teardown+recreate of 1800 tiles.
  // These caches reuse references when data hasn't changed, so <For> keeps DOM alive.

  const fileCache = new Map<string, FileEntry>();
  let nextFileId = 0;

  function stableFile(f: ServerFile): FileEntry {
    const state = (f.state as FileState) || 'discovered';
    const existing = fileCache.get(f.path);
    if (
      existing
      && existing.state === state
      && existing.processing === f.processing
      && existing.tokens === (f.tokens ?? null)
      && existing.headline === (f.headline ?? null)
      && existing.structure === (f.structure ?? null)
      && existing.error === (f.error ?? null)
    ) {
      return existing;
    }
    const entry = mapServerFile(f, existing?.id ?? nextFileId++);
    fileCache.set(f.path, entry);
    return entry;
  }

  const groupCache = new Map<string, FileGroup>();

  function stableGroups(groups: FileGroup[], prefix: string): FileGroup[] {
    return groups.map((g) => {
      const key = prefix + '\0' + g.label;
      const cached = groupCache.get(key);
      if (cached && sameItems(cached.files, g.files)) return cached;
      groupCache.set(key, g);
      return g;
    });
  }

  const sectionCache = new Map<string, SourceSection>();

  function stabilizeSections(raw: SourceSection[]): SourceSection[] {
    return raw.map((s) => {
      const stableGs = stableGroups(s.groups, s.prefix);
      const cached = sectionCache.get(s.prefix);
      if (cached && sameItems(cached.groups, stableGs) && cached.total === s.total) return cached;
      const section = { ...s, groups: stableGs };
      sectionCache.set(s.prefix, section);
      return section;
    });
  }

  const queryCache = new Map<number, QueryEntry>();

  function stableQueries(rawQueries: ServerQuery[]): QueryEntry[] {
    const nextEntries = sortQueries(rawQueries.map(mapServerQuery))
      .slice(0, MAX_QUERY_ENTRIES)
      .map((entry) => {
        const cached = queryCache.get(entry.id);
        if (
          cached
          && cached.tool === entry.tool
          && cached.state === entry.state
          && cached.params === entry.params
          && cached.tokenBudget === entry.tokenBudget
          && cached.tokensUsed === entry.tokensUsed
          && cached.resultSummary === entry.resultSummary
          && cached.timestamp === entry.timestamp
          && (entry.state === 'running' || cached.elapsed === entry.elapsed)
        ) {
          return cached;
        }

        queryCache.set(entry.id, entry);
        return entry;
      });

    if (queryCache.size > nextEntries.length + MAX_QUERY_ENTRIES) {
      const activeIds = new Set(nextEntries.map((entry) => entry.id));
      for (const id of queryCache.keys()) {
        if (!activeIds.has(id)) {
          queryCache.delete(id);
        }
      }
    }

    return nextEntries;
  }

  function appendActivity(operation: string, path: string, langColor: string, timestamp: number) {
    setActivities((prev) => {
      const latest = prev[0];
      if (
        latest
        && latest.operation === operation
        && latest.path === path
        && Math.abs(latest.timestamp - timestamp) < 1500
      ) {
        return prev;
      }

      activityId += 1;
      return [
        { id: activityId, operation, path, langColor, timestamp },
        ...prev.slice(0, MAX_ACTIVITY_ENTRIES - 1),
      ];
    });
  }

  function buildFileActivity(previous: ServerFile | undefined, next: ServerFile): ActivityDraft | null {
    const nextState = (next.state as FileState) || 'discovered';
    if (!HIGH_SIGNAL_FILE_STATES.has(nextState)) {
      return null;
    }

    const previousState = previous ? ((previous.state as FileState) || 'discovered') : null;
    if (previousState === nextState) {
      return null;
    }

    const path = compactPath(next.path);
    const langColor = nextState === 'failed' ? 'var(--red)' : pickLangColor(next.path);

    switch (nextState) {
      case 'parsed':
        return { operation: 'Parsed', path, langColor };
      case 'struct_embedded':
        return { operation: 'Ready', path, langColor };
      case 'full_embedded':
        return { operation: 'Indexed', path, langColor };
      case 'failed':
        return { operation: 'Failed', path, langColor };
      default:
        return null;
    }
  }

  // 1-second tick for elapsed time
  onMount(() => {
    const id = window.setInterval(() => setNow(Date.now()), 1000);
    onCleanup(() => window.clearInterval(id));
  });

  // Fetch initial snapshot
  onMount(() => {
    const controller = new AbortController();

    void fetch('/api/snapshot', { signal: controller.signal })
      .then(async (res) => {
        if (!res.ok) {
          throw new Error(`Snapshot request failed (${res.status})`);
        }
        return (await res.json()) as SnapshotResponse;
      })
      .then((raw) => {
        const data = normalizeSnapshot(raw);
        batch(() => {
          setSnapshot(data);

          const map = new Map<string, ServerFile>();
          for (const f of data.files) {
            map.set(f.path, f);
          }
          setFileMap(map);

          setOperations(data.operations.map(mapServerOperation));
          setQueries(stableQueries(data.queries ?? []));

          const startedAt = Date.parse(data.host.startedAt);
          if (Number.isFinite(startedAt)) {
            startTime = startedAt;
          } else {
            startTime = Date.now();
          }
        });
      })
      .catch((err: unknown) => {
        if ((err as { name?: string }).name === 'AbortError') {
          return;
        }
        setError(err instanceof Error ? err.message : 'Failed to load snapshot');
      });

    onCleanup(() => controller.abort());
  });

  // SSE stream
  onMount(() => {
    const es = new EventSource('/api/events');

    es.onopen = () => {
      batch(() => {
        setConnected(true);
        setError(null);
      });
    };

    es.onerror = () => {
      batch(() => {
        setConnected(false);
        setError('Connection lost - reconnecting...');
      });
    };

    es.addEventListener('pipeline', (event) => {
      const parsed = parseJson<PipelineEvent>((event as MessageEvent).data);
      if (parsed) {
        setPipelineEvent(parsed);
      }
    });

    es.addEventListener('activity', (event) => {
      const parsed = parseJson<ActivityEvent>((event as MessageEvent).data);
      if (!parsed) return;

      const rawType = parsed.type ?? parsed.operation ?? '';
      if (
        rawType.includes('Idle')
        || rawType.includes('Unspecified')
        || LOW_SIGNAL_ACTIVITY_TYPES.has(rawType)
      ) {
        return;
      }

      const uri = parsed.uri ?? parsed.path ?? '';
      if (!uri) return;

      const label = ACTIVITY_LABELS[rawType] ?? (rawType.startsWith('IndexingActivity') ? 'Updated' : (rawType || 'Updated'));
      appendActivity(
        label,
        compactPath(uri),
        parsed.langColor ?? pickLangColor(uri),
        coerceTimestamp(parsed.timestamp),
      );
    });

    es.addEventListener('health', (event) => {
      const parsed = parseJson<HealthEvent>((event as MessageEvent).data);
      if (!parsed) return;

      const status = (parsed.status ?? '').toLowerCase();
      if (!['degraded', 'error', 'unhealthy'].includes(status)) {
        return;
      }

      const detail = parsed.message ?? parsed.status ?? 'host-health';
      appendActivity('Health', detail, 'var(--red)', coerceTimestamp(parsed.timestamp));
    });

    es.addEventListener('queries', (event) => {
      const parsed = parseJson<ServerQuery[]>((event as MessageEvent).data);
      if (!Array.isArray(parsed)) return;
      setQueries((prev) => {
        const next = stableQueries(parsed);
        return sameItems(prev, next) ? prev : next;
      });
    });

    es.addEventListener('indexing', (event) => {
      const parsed = parseJson<IndexingEvent>((event as MessageEvent).data);
      if (!parsed) return;
      setIndexingEvent(parsed);
    });

    // Delta file updates — merge into map and surface meaningful file progress.
    es.addEventListener('file_updates', (event) => {
      const parsed = parseJson<ServerFile[]>((event as MessageEvent).data);
      if (!parsed || parsed.length === 0) return;

      const drafts: ActivityDraft[] = [];
      setFileMap((prev) => {
        const next = new Map(prev);
        for (const f of parsed) {
          const activity = buildFileActivity(prev.get(f.path), f);
          if (activity) {
            drafts.push(activity);
          }
          next.set(f.path, f);
        }
        return next;
      });

      const timestamp = Date.now();
      for (const draft of drafts) {
        appendActivity(draft.operation, draft.path, draft.langColor, timestamp);
      }
    });

    // File removals
    es.addEventListener('file_removes', (event) => {
      const paths = parseJson<string[]>((event as MessageEvent).data);
      if (!paths || paths.length === 0) return;

      const removed: string[] = [];
      setFileMap((prev) => {
        const next = new Map(prev);
        for (const p of paths) {
          if (next.delete(p)) {
            removed.push(p);
          }
        }
        return next;
      });

      const timestamp = Date.now();
      for (const path of removed) {
        appendActivity('Removed', compactPath(path), pickLangColor(path), timestamp);
      }
    });

    // Legacy full file snapshot (backward compat)
    es.addEventListener('files', (event) => {
      const parsed = parseJson<ServerFile[]>((event as MessageEvent).data);
      if (!parsed) return;
      const map = new Map<string, ServerFile>();
      for (const f of parsed) {
        map.set(f.path, f);
      }
      setFileMap(map);
    });

    es.addEventListener('operations', (event) => {
      const parsed = parseJson<SnapshotResponse['operations']>((event as MessageEvent).data);
      if (Array.isArray(parsed)) setOperations(parsed.map(mapServerOperation));
    });

    onCleanup(() => es.close());
  });

  // Throttle file map updates — leading edge fires immediately, trailing coalesces.
  // During continuous SSE at 150ms cadence, tiles update at most 4x/sec instead of freezing.
  const [renderMap, setRenderMap] = createSignal<Map<string, ServerFile>>(new Map());
  const updateRenderMap = leadingAndTrailing(throttle, setRenderMap, 250);
  createEffect(() => {
    updateRenderMap(fileMap());
  });
  onCleanup(() => updateRenderMap.clear());

  // HEAVY memo: only depends on file data (renderMap + snapshot). Runs at most 4x/sec.
  const fileData = createMemo(() => {
    const snap = snapshot();
    const map = renderMap();

    if (!snap && map.size === 0) return null;

    const serverFiles = map.size > 0
      ? Array.from(map.values()).sort((a, b) => a.path.localeCompare(b.path, undefined, { sensitivity: 'base' }))
      : snap?.files ?? [];

    const files = serverFiles.map((f) => stableFile(f));

    // Prune cache for removed files (lazy — only when cache grows beyond file count)
    if (fileCache.size > files.length + 100) {
      const current = new Set(serverFiles.map((f) => f.path));
      for (const key of fileCache.keys()) {
        if (!current.has(key)) fileCache.delete(key);
      }
    }

    const stateCounts = countByState(files);

    const total = files.length;
    const failed = stateCounts.failed ?? 0;
    const discovered = total - (stateCounts.hidden ?? 0);
    const classified = discovered - (stateCounts.discovered ?? 0);
    const parsed = (stateCounts.parsed ?? 0) + (stateCounts.struct_embedded ?? 0)
      + (stateCounts.full_embedded ?? 0) + failed;
    const structEmbedded = (stateCounts.struct_embedded ?? 0) + (stateCounts.full_embedded ?? 0);
    const fullEmbedded = stateCounts.full_embedded ?? 0;

    return {
      title: snap?.host?.repositoryPath ?? 'Indexing...',
      stateCounts,
      files,
      total,
      discovered,
      classified,
      parsed,
      structEmbedded,
      fullEmbedded,
      failed,
      sections: stabilizeSections(groupBySources(files)),
    };
  });

  const errors = createMemo<FileError[]>(() => {
    const fd = fileData();
    if (!fd) {
      return [];
    }

    return buildFileErrors(fd.files);
  });

  // Language distribution — deferred to idle time so it doesn't block tile rendering.
  const [languages, setLanguages] = createSignal<LanguageCount[]>([]);
  const updateLanguages = scheduleIdle(
    (data: { files: FileEntry[]; total: number }) => {
      setLanguages(computeLanguageCounts(data.files, data.total));
    },
    500,
  );
  createEffect(() => {
    const fd = fileData();
    if (!fd) return;
    updateLanguages({ files: fd.files, total: fd.total });
  });
  onCleanup(() => updateLanguages.clear());

  // Elapsed time — ticks every second but isolated so it doesn't rebuild pipeline/sections.
  const elapsed = createMemo(() => Math.max(0, (now() - startTime) / 1000));

  // LIGHT memo: composes cached file data with event-driven signals. Does NOT track now().
  const props = createMemo<DashboardProps | null>(() => {
    const fd = fileData();
    if (!fd) return null;

    const snap = snapshot();
    const pe = pipelineEvent();
    const ie = indexingEvent();
    const ready = pe?.ready ?? snap?.host?.initialIndexingCompleted ?? false;
    const reindexing = pe?.reindexing ?? snap?.pipeline?.reindexing ?? false;
    const phase = derivePhaseFromFiles(fd.stateCounts, ready, reindexing);
    const stageRate = (pe?.stages ?? snap?.pipeline?.stages ?? [])
      .reduce((sum, s) => sum + ((s as PipelineStageEvent).throughputPerSec ?? 0), 0);
    const stages = mapPipelineStages(pe?.stages ?? snap?.pipeline?.stages ?? []);

    return {
      title: fd.title,
      get elapsed() { return elapsed(); },
      pipeline: {
        total: fd.total,
        discovered: fd.discovered,
        classified: fd.classified,
        parsed: fd.parsed,
        structEmbedded: fd.structEmbedded,
        fullEmbedded: fd.fullEmbedded,
        failed: fd.failed,
        phase,
        rate: stageRate,
      },
      sections: fd.sections,
      languages: languages(),
      activities: activities(),
      errors: errors(),
      queries: queries(),
      operations: operations(),
      stages,
      writerPending: pe?.writerPending ?? snap?.pipeline?.writerPending ?? false,
      indexing: mapIndexingState(ie ?? snap?.indexing),
      get now() { return now(); },
    };
  });

  return { props, connected, error };
}

// --- Phase derivation from real file states ---

function derivePhaseFromFiles(
  stateCounts: Record<string, number>,
  ready: boolean,
  reindexing: boolean,
): PipelinePhase {
  if (ready) return 'complete';
  if (reindexing) return 'rebuilding';

  const total = Object.values(stateCounts).reduce((s, c) => s + c, 0);
  if (total === 0) return 'idle';

  const discovered = stateCounts.discovered ?? 0;
  const classified = stateCounts.classified ?? 0;
  const structEmbedding = stateCounts.struct_embedded ?? 0;

  if (structEmbedding > 0 && stateCounts.parsed) return 'struct_embedding';
  if (classified > 0) return 'parsing';
  if (discovered > 0) return 'discovery';

  const fullEmbedded = stateCounts.full_embedded ?? 0;
  const parsedCount = stateCounts.parsed ?? 0;
  if (parsedCount > 0 && fullEmbedded < total) return 'struct_embedding';

  return 'idle';
}

function mapPipelineStages(
  stages: Array<{ name: string; busy: boolean; queued: number; inProgress: number }> | null | undefined,
): PipelineStageLoad[] {
  if (!stages || stages.length === 0) {
    return [];
  }

  return stages.map((stage) => ({
    name: stage.name,
    busy: stage.busy,
    queued: stage.queued,
    inProgress: stage.inProgress,
  }));
}

// --- Utilities ---

function parseJson<T>(raw: string): T | null {
  try {
    return JSON.parse(raw) as T;
  } catch {
    return null;
  }
}

function coerceTimestamp(value: number | string | null | undefined): number {
  if (typeof value === 'number' && Number.isFinite(value)) {
    return value;
  }
  if (typeof value === 'string') {
    const parsed = Date.parse(value);
    if (Number.isFinite(parsed)) {
      return parsed;
    }
  }
  return Date.now();
}

function pickLangColor(path: string): string {
  const match = path.match(/\.[a-z0-9]+$/i);
  if (!match) {
    return 'var(--fg3)';
  }

  const language = LANGUAGES[match[0].toLowerCase()];
  return language?.color ?? 'var(--fg3)';
}
