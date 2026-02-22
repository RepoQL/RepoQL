import { createSignal, createMemo, createEffect, onMount, onCleanup, batch } from 'solid-js';
import type { ActivityEntry, FileEntry, FileState, Language, PipelinePhase } from '../types';
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
  leases: Array<{ clientId: string; lastBeatUtc: string }>;
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
  files: ServerFile[];
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
}

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
  IndexingActivityFileDiscovered: 'Discovered',
  IndexingActivityFileParsed: 'Parsed',
  IndexingActivityFileAnalyzed: 'Analyzed',
  IndexingActivityEmbeddingsGenerated: 'Embedded',
  IndexingActivityBatchComplete: 'Batch done',
};

/** Trim a URI or path to just the filename */
function shortPath(uri: string): string {
  const last = uri.replace(/\\/g, '/').split('/').pop();
  return last ?? uri;
}

interface HealthEvent {
  message?: string;
  status?: string;
  timestamp?: number | string;
}

const MAX_ACTIVITY_ENTRIES = 50;

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

// --- Count files by state ---

function countByState(files: FileEntry[]): Record<string, number> {
  const counts: Record<string, number> = {};
  for (const f of files) {
    counts[f.state] = (counts[f.state] ?? 0) + 1;
  }
  return counts;
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
  const [activities, setActivities] = createSignal<ActivityEntry[]>([]);
  const [fileMap, setFileMap] = createSignal<Map<string, ServerFile>>(new Map());
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
    if (existing && existing.state === state && existing.processing === f.processing) {
      return existing;
    }
    const entry = mapServerFile(f, existing?.id ?? nextFileId++);
    fileCache.set(f.path, entry);
    return entry;
  }

  const groupCache = new Map<string, FileGroup>();

  function stableGroups(groups: FileGroup[], prefix: string): FileGroup[] {
    return groups.map(g => {
      const key = prefix + '\0' + g.label;
      const cached = groupCache.get(key);
      if (cached && sameItems(cached.files, g.files)) return cached;
      groupCache.set(key, g);
      return g;
    });
  }

  const sectionCache = new Map<string, SourceSection>();

  function stabilizeSections(raw: SourceSection[]): SourceSection[] {
    return raw.map(s => {
      const stableGs = stableGroups(s.groups, s.prefix);
      const cached = sectionCache.get(s.prefix);
      if (cached && sameItems(cached.groups, stableGs) && cached.total === s.total) return cached;
      const section = { ...s, groups: stableGs };
      sectionCache.set(s.prefix, section);
      return section;
    });
  }

  function appendActivity(operation: string, path: string, langColor: string, timestamp: number) {
    activityId += 1;
    const id = activityId;
    setActivities((prev) => [
      { id, operation, path, langColor, timestamp },
      ...prev.slice(0, MAX_ACTIVITY_ENTRIES - 1),
    ]);
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
      .then((data) => {
        batch(() => {
          setSnapshot(data);

          // Populate file map from snapshot
          const map = new Map<string, ServerFile>();
          for (const f of data.files) {
            map.set(f.path, f);
          }
          setFileMap(map);

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

      // Skip noise
      if (rawType.includes('Idle') || rawType.includes('Unspecified')) return;

      const label = ACTIVITY_LABELS[rawType] ?? rawType;
      const uri = parsed.uri ?? parsed.path ?? '';
      const path = uri ? shortPath(uri) : parsed.message ?? '';
      const counts = parsed.processedCount ? ` (${parsed.processedCount})` : '';
      const display = path ? `${path}${counts}` : `${label}${counts}`;
      const color = parsed.langColor ?? pickLangColor(uri || path);
      const timestamp = coerceTimestamp(parsed.timestamp);

      appendActivity(label, display, color, timestamp);
    });

    es.addEventListener('health', (event) => {
      const parsed = parseJson<HealthEvent>((event as MessageEvent).data);
      if (!parsed) return;

      const detail = parsed.message ?? parsed.status ?? 'host-health';
      appendActivity('health', detail, 'var(--fg3)', coerceTimestamp(parsed.timestamp));
    });

    // Delta file updates — merge into map
    es.addEventListener('file_updates', (event) => {
      const parsed = parseJson<ServerFile[]>((event as MessageEvent).data);
      if (!parsed || parsed.length === 0) return;

      setFileMap((prev) => {
        const next = new Map(prev);
        for (const f of parsed) {
          next.set(f.path, f);
        }
        return next;
      });
    });

    // File removals
    es.addEventListener('file_removes', (event) => {
      const paths = parseJson<string[]>((event as MessageEvent).data);
      if (!paths || paths.length === 0) return;

      setFileMap((prev) => {
        const next = new Map(prev);
        for (const p of paths) {
          next.delete(p);
        }
        return next;
      });
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

    es.addEventListener('leases', (event) => {
      parseJson<SnapshotResponse['leases']>((event as MessageEvent).data);
    });

    es.addEventListener('operations', (event) => {
      parseJson<SnapshotResponse['operations']>((event as MessageEvent).data);
    });

    onCleanup(() => es.close());
  });

  // Throttle file map updates to max 1/sec to avoid freezing during reindex.
  const [renderMap, setRenderMap] = createSignal<Map<string, ServerFile>>(new Map());
  let throttleTimer: ReturnType<typeof setTimeout> | undefined;
  createEffect(() => {
    const map = fileMap(); // track dependency
    if (throttleTimer) clearTimeout(throttleTimer);
    throttleTimer = setTimeout(() => setRenderMap(map), 250);
  });
  onCleanup(() => { if (throttleTimer) clearTimeout(throttleTimer); });

  // HEAVY memo: only depends on file data (renderMap + snapshot). Runs at most 1/sec.
  const fileData = createMemo(() => {
    const snap = snapshot();
    const map = renderMap();

    if (!snap && map.size === 0) return null;

    const serverFiles = map.size > 0
      ? Array.from(map.values()).sort((a, b) => a.path.localeCompare(b.path, undefined, { sensitivity: 'base' }))
      : snap?.files ?? [];

    const files = serverFiles.map(f => stableFile(f));

    // Prune cache for removed files (lazy — only when cache grows beyond file count)
    if (fileCache.size > files.length + 100) {
      const current = new Set(serverFiles.map(f => f.path));
      for (const key of fileCache.keys()) {
        if (!current.has(key)) fileCache.delete(key);
      }
    }

    const stateCounts = countByState(files);

    const total = files.length;
    const failed = stateCounts['failed'] ?? 0;
    const discovered = total - (stateCounts['hidden'] ?? 0);
    const classified = discovered - (stateCounts['discovered'] ?? 0);
    const parsed = (stateCounts['parsed'] ?? 0) + (stateCounts['struct_embedded'] ?? 0)
      + (stateCounts['full_embedded'] ?? 0) + failed;
    const structEmbedded = (stateCounts['struct_embedded'] ?? 0) + (stateCounts['full_embedded'] ?? 0);
    const fullEmbedded = stateCounts['full_embedded'] ?? 0;

    return {
      title: snap?.host.repositoryPath ?? 'Indexing...',
      stateCounts,
      total, discovered, classified, parsed, structEmbedded, fullEmbedded, failed,
      sections: stabilizeSections(groupBySources(files)),
      languages: computeLanguageCounts(files, files.length),
    };
  });

  // LIGHT memo: composes cached file data with volatile signals. Runs on every tick.
  const props = createMemo<DashboardProps | null>(() => {
    const fd = fileData();
    if (!fd) return null;

    const snap = snapshot();
    const pe = pipelineEvent();
    const ready = pe?.ready ?? snap?.host.initialIndexingCompleted ?? false;
    const reindexing = pe?.reindexing ?? snap?.pipeline.reindexing ?? false;
    const phase = derivePhaseFromFiles(fd.stateCounts, ready, reindexing);
    const stageRate = (pe?.stages ?? snap?.pipeline.stages ?? [])
      .reduce((sum, s) => sum + ((s as PipelineStageEvent).throughputPerSec ?? 0), 0);

    return {
      title: fd.title,
      elapsed: Math.max(0, (now() - startTime) / 1000),
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
      languages: fd.languages,
      activities: activities(),
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

  const discovered = stateCounts['discovered'] ?? 0;
  const classified = stateCounts['classified'] ?? 0;
  const structEmbedding = stateCounts['struct_embedded'] ?? 0;

  if (structEmbedding > 0 && stateCounts['parsed']) return 'struct_embedding';
  if (classified > 0) return 'parsing';
  if (discovered > 0) return 'discovery';

  const fullEmbedded = stateCounts['full_embedded'] ?? 0;
  const parsedCount = stateCounts['parsed'] ?? 0;
  if (parsedCount > 0 && fullEmbedded < total) return 'struct_embedding';

  return 'idle';
}

// --- Utilities ---

function parseJson<T>(raw: string): T | null {
  try {
    return JSON.parse(raw) as T;
  } catch {
    return null;
  }
}

function coerceTimestamp(value: number | string | undefined): number {
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
