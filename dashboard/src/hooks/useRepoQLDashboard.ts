import { useEffect, useMemo, useRef, useState, useCallback } from 'react';
import type { ActivityEntry, FileEntry, FileState, Language, PipelinePhase } from '../types';
import { LANGUAGES } from '../types';
import {
  computeLanguageCounts,
  groupByPhase,
} from '../fixtures';
import type { DashboardProps } from '../components/Dashboard/Dashboard';

// --- Server response types ---

interface ServerFile {
  path: string;
  ext: string;
  state: string;
  processing: boolean;
  lines?: number | null;
  symbols?: number | null;
  chunks?: number | null;
  indexedAt?: string | null;
  embeddedAt?: string | null;
  error?: string | null;
  tree?: Array<{ n: string; k: string; l?: number | null; e?: number | null; m?: number | null }> | null;
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
}

interface HealthEvent {
  message?: string;
  status?: string;
  timestamp?: number | string;
}

const MAX_ACTIVITY_ENTRIES = 50;

// --- Map server files to client FileEntry[] ---

function mapServerFiles(serverFiles: ServerFile[]): FileEntry[] {
  return serverFiles.map((f, i) => ({
    id: i,
    path: f.path,
    ext: f.ext,
    lang: lookupLang(f.ext),
    state: (f.state as FileState) || 'discovered',
    processing: f.processing,
    lines: f.lines,
    symbols: f.symbols,
    chunks: f.chunks,
    indexedAt: f.indexedAt,
    embeddedAt: f.embeddedAt,
    error: f.error,
    tree: f.tree,
  }));
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
  props: DashboardProps | null;
  connected: boolean;
  error: string | null;
} {
  const [connected, setConnected] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [snapshot, setSnapshot] = useState<SnapshotResponse | null>(null);
  const [pipelineEvent, setPipelineEvent] = useState<PipelineEvent | null>(null);
  const [activities, setActivities] = useState<ActivityEntry[]>([]);
  const [liveFiles, setLiveFiles] = useState<ServerFile[] | null>(null);
  const [operations, setOperations] = useState<SnapshotResponse['operations']>([]);
  const [now, setNow] = useState(() => Date.now());
  const startTime = useRef(Date.now());
  const activityId = useRef(0);

  const appendActivity = useCallback(
    (operation: string, path: string, langColor: string, timestamp: number) => {
      activityId.current += 1;
      setActivities((prev) => [
        {
          id: activityId.current,
          operation,
          path,
          langColor,
          timestamp,
        },
        ...prev.slice(0, MAX_ACTIVITY_ENTRIES - 1),
      ]);
    },
    [],
  );

  useEffect(() => {
    const id = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(id);
  }, []);

  // Fetch initial snapshot
  useEffect(() => {
    const controller = new AbortController();

    void fetch('/api/snapshot', { signal: controller.signal })
      .then(async (res) => {
        if (!res.ok) {
          throw new Error(`Snapshot request failed (${res.status})`);
        }

        return (await res.json()) as SnapshotResponse;
      })
      .then((data) => {
        setSnapshot(data);
        setOperations(data.operations);

        const startedAt = Date.parse(data.host.startedAt);
        if (Number.isFinite(startedAt)) {
          startTime.current = startedAt;
        } else {
          startTime.current = Date.now();
        }
      })
      .catch((err: unknown) => {
        if ((err as { name?: string }).name === 'AbortError') {
          return;
        }
        setError(err instanceof Error ? err.message : 'Failed to load snapshot');
      });

    return () => controller.abort();
  }, []);

  // SSE stream
  useEffect(() => {
    const es = new EventSource('/api/events');

    es.onopen = () => {
      setConnected(true);
      setError(null);
    };

    es.onerror = () => {
      setConnected(false);
      setError('Connection lost - reconnecting...');
    };

    es.addEventListener('pipeline', (event) => {
      const parsed = parseJson<PipelineEvent>((event as MessageEvent).data);
      if (!parsed) {
        return;
      }
      setPipelineEvent(parsed);
    });

    es.addEventListener('activity', (event) => {
      const parsed = parseJson<ActivityEvent>((event as MessageEvent).data);
      if (!parsed) {
        return;
      }

      const operation = parsed.operation ?? parsed.type ?? 'event';
      const path = parsed.path ?? parsed.uri ?? parsed.message ?? '';
      const color = parsed.langColor ?? pickLangColor(path);
      const timestamp = coerceTimestamp(parsed.timestamp);

      appendActivity(operation, path, color, timestamp);
    });

    es.addEventListener('health', (event) => {
      const parsed = parseJson<HealthEvent>((event as MessageEvent).data);
      if (!parsed) {
        return;
      }

      const detail = parsed.message ?? parsed.status ?? 'host-health';
      appendActivity('health', detail, 'var(--fg3)', coerceTimestamp(parsed.timestamp));
    });

    es.addEventListener('files', (event) => {
      const parsed = parseJson<ServerFile[]>((event as MessageEvent).data);
      if (parsed) {
        setLiveFiles(parsed);
      }
    });

    es.addEventListener('leases', (event) => {
      parseJson<SnapshotResponse['leases']>((event as MessageEvent).data);
    });

    es.addEventListener('operations', (event) => {
      const parsed = parseJson<SnapshotResponse['operations']>((event as MessageEvent).data);
      if (parsed) {
        setOperations(parsed);
      }
    });

    return () => {
      es.close();
    };
  }, [appendActivity]);

  const props = useMemo<DashboardProps | null>(() => {
    if (!snapshot) {
      return null;
    }

    // Use live files from SSE if available, otherwise snapshot files
    const serverFiles = liveFiles ?? snapshot.files ?? [];
    const files = mapServerFiles(serverFiles);
    const stateCounts = countByState(files);

    const total = files.length;
    const failed = stateCounts['failed'] ?? 0;
    const discovered = total - (stateCounts['hidden'] ?? 0);
    const classified = discovered - (stateCounts['discovered'] ?? 0);
    const parsed = (stateCounts['parsed'] ?? 0) + (stateCounts['struct_embedded'] ?? 0)
      + (stateCounts['full_embedded'] ?? 0) + failed;
    const structEmbedded = (stateCounts['struct_embedded'] ?? 0) + (stateCounts['full_embedded'] ?? 0);
    const fullEmbedded = stateCounts['full_embedded'] ?? 0;

    const ready = pipelineEvent?.ready ?? snapshot.host.initialIndexingCompleted ?? false;
    const phase = derivePhaseFromFiles(stateCounts, ready,
      pipelineEvent?.reindexing ?? snapshot.pipeline.reindexing);

    const stageRate = (pipelineEvent?.stages ?? snapshot.pipeline.stages)
      .reduce((sum, s) => sum + ((s as PipelineStageEvent).throughputPerSec ?? 0), 0);

    const pipeline = {
      total,
      discovered,
      classified,
      parsed,
      structEmbedded,
      fullEmbedded,
      failed,
      phase,
      rate: stageRate,
    };

    const groups = groupByPhase(files);
    const languages = computeLanguageCounts(files, files.length);

    return {
      title: snapshot.host.repositoryPath,
      elapsed: Math.max(0, (now - startTime.current) / 1000),
      pipeline,
      groups,
      languages,
      activities,
    };
  }, [snapshot, pipelineEvent, liveFiles, operations, now, activities]);

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

  // Check from latest phase backward
  const discovered = stateCounts['discovered'] ?? 0;
  const classified = stateCounts['classified'] ?? 0;
  const structEmbedding = stateCounts['struct_embedded'] ?? 0;

  if (structEmbedding > 0 && stateCounts['parsed']) return 'struct_embedding';
  if (classified > 0) return 'parsing';
  if (discovered > 0) return 'discovery';

  // Mostly done — check what's still pending
  const fullEmbedded = stateCounts['full_embedded'] ?? 0;
  const parsed = stateCounts['parsed'] ?? 0;
  if (parsed > 0 && fullEmbedded < total) return 'struct_embedding';

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
