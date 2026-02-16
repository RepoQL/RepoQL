import { useState, useEffect, useCallback, useRef } from 'react';
import type { PipelineState, ActivityEntry } from '../types';

export type ConnectionStatus = 'disconnected' | 'connecting' | 'connected';

export interface RepoQLConnection {
  status: ConnectionStatus;
  pipeline: PipelineState | null;
  activities: ActivityEntry[];
  connect: (url: string) => void;
  disconnect: () => void;
}

const EMPTY: PipelineState = {
  total: 0, discovered: 0, classified: 0, parsed: 0,
  structEmbedded: 0, fullEmbedded: 0, failed: 0, phase: 'idle', rate: 0,
};

/**
 * Hook for connecting to a live RepoQL host.
 *
 * The host would expose an SSE or WebSocket endpoint that streams
 * pipeline state updates. This hook manages the connection lifecycle
 * and accumulates activity entries.
 *
 * TODO: Wire to real endpoint once the host exposes one.
 * Expected protocol: JSON messages with shape { type: 'pipeline' | 'activity', ... }
 */
export function useRepoQLConnection(): RepoQLConnection {
  const [status, setStatus] = useState<ConnectionStatus>('disconnected');
  const [pipeline, setPipeline] = useState<PipelineState | null>(null);
  const [activities, setActivities] = useState<ActivityEntry[]>([]);
  const sourceRef = useRef<EventSource | null>(null);
  const activityIdRef = useRef(0);

  const disconnect = useCallback(() => {
    sourceRef.current?.close();
    sourceRef.current = null;
    setStatus('disconnected');
  }, []);

  const connect = useCallback((url: string) => {
    disconnect();
    setStatus('connecting');
    setPipeline(EMPTY);
    setActivities([]);

    const es = new EventSource(url);
    sourceRef.current = es;

    es.onopen = () => setStatus('connected');
    es.onerror = () => setStatus('disconnected');

    es.addEventListener('pipeline', (e) => {
      const data = JSON.parse(e.data) as PipelineState;
      setPipeline(data);
    });

    es.addEventListener('activity', (e) => {
      const data = JSON.parse(e.data) as Omit<ActivityEntry, 'id'>;
      activityIdRef.current++;
      setActivities((prev) => [
        { ...data, id: activityIdRef.current },
        ...prev.slice(0, 99),
      ]);
    });
  }, [disconnect]);

  useEffect(() => disconnect, [disconnect]);

  return { status, pipeline, activities, connect, disconnect };
}
