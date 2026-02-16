import { useState, useEffect, useRef, useCallback } from 'react';
import type { PipelineState, ActivityEntry, FileEntry } from '../../types';

const TOTAL = 847;
const FAIL_COUNT = 2;

/** Compute pipeline state at a given time (seconds) — mirrors the concept HTML timeline */
function computePipeline(t: number): PipelineState {
  const p: PipelineState = {
    total: TOTAL, discovered: 0, classified: 0, parsed: 0,
    structEmbedded: 0, fullEmbedded: 0, failed: 0, phase: 'idle', rate: 0,
  };

  // Discovery
  if (t < 2.5) {
    const progress = Math.min(1, t / 2.2);
    p.discovered = Math.floor((1 - Math.pow(1 - progress, 3)) * TOTAL);
  } else {
    p.discovered = TOTAL;
  }

  // Classification
  if (t >= 0.8) {
    const progress = Math.min(1, (t - 0.8) / 3.5);
    p.classified = Math.min(p.discovered, Math.floor((1 - Math.pow(1 - progress, 2.5)) * TOTAL));
  }

  // Parsing
  if (t >= 3 && t < 18) {
    const progress = Math.min(1, (t - 3) / 14);
    const eased = progress < 0.5 ? 2 * progress * progress : 1 - Math.pow(-2 * progress + 2, 2) / 2;
    p.parsed = Math.min(p.classified, Math.floor(eased * TOTAL));
    p.rate = Math.floor(30 + Math.random() * 35);
  } else if (t >= 18) {
    p.parsed = TOTAL;
  }

  // Structure embedding
  if (t >= 8 && t < 22) {
    const progress = Math.min(1, (t - 8) / 14);
    p.structEmbedded = Math.min(p.parsed, Math.floor(Math.pow(progress, 0.7) * (TOTAL - FAIL_COUNT)));
    p.failed = progress > 0.1 ? FAIL_COUNT : Math.floor(progress / 0.1 * FAIL_COUNT);
  } else if (t >= 22) {
    p.structEmbedded = TOTAL - FAIL_COUNT;
    p.failed = FAIL_COUNT;
  }

  // Full embedding
  if (t >= 20 && t < 36) {
    const progress = Math.min(1, (t - 20) / 16);
    p.fullEmbedded = Math.floor(Math.pow(progress, 0.9) * (TOTAL - FAIL_COUNT));
    p.rate = Math.floor(8 + Math.random() * 12);
  } else if (t >= 36) {
    p.fullEmbedded = TOTAL - FAIL_COUNT;
  }

  // Phase
  if (t < 0.8) p.phase = 'discovery';
  else if (t < 3) p.phase = 'classifying';
  else if (t < 18) p.phase = 'parsing';
  else if (t < 22) p.phase = 'struct_embedding';
  else if (t < 36) p.phase = 'full_embedding';
  else if (t < 38) p.phase = 'rebuilding';
  else { p.phase = 'complete'; p.rate = 0; }

  return p;
}

export interface SimulationState {
  elapsed: number;
  pipeline: PipelineState;
  activities: ActivityEntry[];
}

/**
 * @param files — pass the generated file entries so the stream shows real paths/colors
 */
export function useSimulation(running: boolean, files: FileEntry[]): SimulationState {
  const [state, setState] = useState<SimulationState>({
    elapsed: 0,
    pipeline: computePipeline(0),
    activities: [],
  });

  const filesRef = useRef(files);
  filesRef.current = files;

  const startRef = useRef<number | null>(null);
  const activityIdRef = useRef(0);
  const lastActivityRef = useRef(0);

  const tick = useCallback((ts: number) => {
    if (!startRef.current) startRef.current = ts;
    const t = (ts - startRef.current) / 1000;
    const pipeline = computePipeline(t);

    setState((prev) => {
      let activities = prev.activities;

      if (t - lastActivityRef.current > 0.12 && pipeline.phase !== 'idle' && pipeline.phase !== 'complete') {
        lastActivityRef.current = t;
        const frontier = Math.max(0, Math.min(TOTAL - 1, pipeline.parsed || pipeline.classified || pipeline.discovered));
        const f = filesRef.current[frontier];
        const op =
          pipeline.phase === 'parsing' ? 'parse' :
          pipeline.phase === 'classifying' ? 'classify' :
          pipeline.phase === 'struct_embedding' ? 'embed:struct' :
          pipeline.phase === 'full_embedding' ? 'embed:full' : 'scan';

        activityIdRef.current++;
        activities = [
          { id: activityIdRef.current, operation: op, path: f?.path ?? `file_${frontier}`, langColor: f?.lang.color ?? '#4a9eff', timestamp: Date.now() },
          ...prev.activities.slice(0, 29),
        ];
      }

      return { elapsed: t, pipeline, activities };
    });

    if (pipeline.phase !== 'complete' || t < 40) {
      requestAnimationFrame(tick);
    }
  }, []);

  useEffect(() => {
    if (!running) return;
    startRef.current = null;
    activityIdRef.current = 0;
    lastActivityRef.current = 0;
    const id = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(id);
  }, [running, tick]);

  return state;
}
