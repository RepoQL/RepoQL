import type { FileGroup, FileEntry, PipelineState, ActivityEntry, LanguageCount, FileState } from './types';
import { LANGUAGES } from './types';

/** Seeded PRNG for deterministic fixture generation */
function seededRandom(seed: number) {
  let s = seed;
  return () => {
    s = (s * 16807 + 0) % 2147483647;
    return (s - 1) / 2147483646;
  };
}

const DIRS = [
  'src/', 'src/auth/', 'src/api/', 'src/core/', 'lib/',
  'tests/', 'src/models/', 'src/utils/', 'src/services/', 'src/middleware/',
];
const DIR_WEIGHTS = [0.18, 0.09, 0.12, 0.14, 0.08, 0.11, 0.07, 0.06, 0.10, 0.05];

const NAMES = [
  'Auth', 'Config', 'Handler', 'Service', 'Model', 'Router', 'Parser',
  'Builder', 'Factory', 'Manager', 'Utils', 'Index', 'Types', 'Schema', 'Query',
  'Token', 'Cache', 'Logger', 'Validator', 'Mapper', 'Store', 'Provider', 'Client',
  'Server', 'Worker', 'Task', 'Job', 'Event', 'Stream', 'Pipeline', 'Module',
  'Registry', 'Loader', 'Encoder', 'Decoder', 'Proxy', 'Gateway', 'Filter',
];

const EXT_POOL = Object.keys(LANGUAGES);
const TOTAL = 847;

/** Generate a stable set of file entries */
export function generateFiles(total = TOTAL): FileEntry[] {
  const rng = seededRandom(42);
  const files: FileEntry[] = [];
  let dirIdx = 0;
  let dirRemaining = Math.floor(DIR_WEIGHTS[0]! * total);

  for (let i = 0; i < total; i++) {
    while (dirRemaining <= 0 && dirIdx < DIRS.length - 1) {
      dirIdx++;
      dirRemaining = Math.floor(DIR_WEIGHTS[dirIdx]! * total);
    }
    dirRemaining--;

    const ext = EXT_POOL[Math.floor(rng() * EXT_POOL.length)]!;
    const name = NAMES[Math.floor(rng() * NAMES.length)];
    const lang = LANGUAGES[ext] ?? { name: 'Unknown', color: '#555560' };

    files.push({
      id: i,
      path: DIRS[dirIdx]! + name + ext,
      ext,
      lang,
      state: 'hidden',
      processing: false,
    });
  }

  return files;
}

/** Phase display order and labels */
const PHASE_ORDER: FileState[] = [
  'full_embedded', 'struct_embedded', 'parsed', 'classified', 'discovered', 'hidden', 'failed',
];

const PHASE_GROUP_LABELS: Record<FileState, string> = {
  hidden: 'Pending',
  discovered: 'Discovered',
  classified: 'Classified',
  parsed: 'Parsed',
  struct_embedded: 'Searchable',
  full_embedded: 'Fully Embedded',
  failed: 'Failed',
};

/** Group files by pipeline phase — most-processed first, failed last */
export function groupByPhase(files: FileEntry[]): FileGroup[] {
  const map = new Map<FileState, FileEntry[]>();
  for (const f of files) {
    let arr = map.get(f.state);
    if (!arr) {
      arr = [];
      map.set(f.state, arr);
    }
    arr.push(f);
  }

  return PHASE_ORDER
    .filter((phase) => map.has(phase))
    .map((phase) => ({
      label: PHASE_GROUP_LABELS[phase],
      files: map.get(phase)!,
    }));
}

/** Group files by directory */
export function groupByDirectory(files: FileEntry[]): FileGroup[] {
  const map = new Map<string, FileEntry[]>();
  for (const f of files) {
    const dir = f.path.substring(0, f.path.lastIndexOf('/') + 1) || '/';
    let arr = map.get(dir);
    if (!arr) {
      arr = [];
      map.set(dir, arr);
    }
    arr.push(f);
  }
  return Array.from(map.entries()).map(([path, dirFiles]) => ({ label: path.replace(/\/$/, ''), files: dirFiles }));
}

/** Apply pipeline progress to file states — mutates in place, returns the files */
export function applyPipelineState(files: FileEntry[], pipeline: PipelineState): FileEntry[] {
  const failIndices = new Set([287, 654]);

  for (let i = 0; i < files.length; i++) {
    const f = files[i]!;

    if (failIndices.has(i) && pipeline.failed > 0 && i < pipeline.parsed + 10) {
      f.state = 'failed';
      f.processing = false;
      continue;
    }

    let state: FileState = 'hidden';
    if (i < pipeline.fullEmbedded) state = 'full_embedded';
    else if (i < pipeline.structEmbedded) state = 'struct_embedded';
    else if (i < pipeline.parsed) state = 'parsed';
    else if (i < pipeline.classified) state = 'classified';
    else if (i < pipeline.discovered) state = 'discovered';

    f.state = state;

    // Mark files near the frontier as processing
    f.processing =
      (state === 'classified' && i >= pipeline.parsed - 5 && i < pipeline.parsed + 8) ||
      (state === 'parsed' && i >= pipeline.structEmbedded - 3 && i < pipeline.structEmbedded + 5) ||
      (state === 'struct_embedded' && i >= pipeline.fullEmbedded - 2 && i < pipeline.fullEmbedded + 4);
  }

  return files;
}

/** Snapshot pipeline states for stories */
export const PIPELINE_EMPTY: PipelineState = {
  total: TOTAL, discovered: 0, classified: 0, parsed: 0,
  structEmbedded: 0, fullEmbedded: 0, failed: 0, phase: 'idle', rate: 0,
};

export const PIPELINE_DISCOVERING: PipelineState = {
  total: TOTAL, discovered: 340, classified: 120, parsed: 0,
  structEmbedded: 0, fullEmbedded: 0, failed: 0, phase: 'discovery', rate: 0,
};

export const PIPELINE_PARSING: PipelineState = {
  total: TOTAL, discovered: TOTAL, classified: TOTAL, parsed: 520,
  structEmbedded: 180, fullEmbedded: 0, failed: 1, phase: 'parsing', rate: 45,
};

export const PIPELINE_EMBEDDING: PipelineState = {
  total: TOTAL, discovered: TOTAL, classified: TOTAL, parsed: TOTAL,
  structEmbedded: 700, fullEmbedded: 350, failed: 2, phase: 'full_embedding', rate: 12,
};

export const PIPELINE_COMPLETE: PipelineState = {
  total: TOTAL, discovered: TOTAL, classified: TOTAL, parsed: TOTAL,
  structEmbedded: TOTAL - 2, fullEmbedded: TOTAL - 2, failed: 2, phase: 'complete', rate: 0,
};

/** Compute language distribution from classified files */
export function computeLanguageCounts(files: FileEntry[], classified: number): LanguageCount[] {
  const counts = new Map<string, number>();
  for (let i = 0; i < Math.min(classified, files.length); i++) {
    const ext = files[i]!.ext;
    counts.set(ext, (counts.get(ext) ?? 0) + 1);
  }

  const total = classified || 1;
  return Array.from(counts.entries())
    .map(([ext, count]) => ({
      ext,
      lang: LANGUAGES[ext] ?? { name: 'Unknown', color: '#555560' },
      count,
      fraction: count / total,
    }))
    .sort((a, b) => b.count - a.count);
}

/** Generate activity entries for a snapshot */
export function generateActivityEntries(files: FileEntry[], pipeline: PipelineState, count = 15): ActivityEntry[] {
  const entries: ActivityEntry[] = [];
  const frontier = Math.max(0, Math.min(files.length - 1, pipeline.parsed || pipeline.classified || pipeline.discovered));

  for (let i = 0; i < count; i++) {
    const idx = Math.max(0, frontier - i);
    const f = files[idx]!;
    const op =
      pipeline.phase === 'parsing' ? 'parse' :
      pipeline.phase === 'classifying' ? 'classify' :
      pipeline.phase === 'struct_embedding' ? 'embed:struct' :
      pipeline.phase === 'full_embedding' ? 'embed:full' : 'scan';

    entries.push({
      id: i,
      operation: op,
      path: f.path,
      langColor: f.lang.color,
      timestamp: Date.now() - i * 120,
    });
  }

  return entries;
}
