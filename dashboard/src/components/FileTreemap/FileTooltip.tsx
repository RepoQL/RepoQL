import type { FileEntry, SymbolNode } from '../../types';
import './FileTooltip.css';

const STATE_LABELS: Record<string, string> = {
  hidden: 'Pending',
  discovered: 'Discovered',
  classified: 'Classifying',
  parsed: 'Indexed',
  struct_embedded: 'Searchable',
  full_embedded: 'Ready',
  failed: 'Failed',
};

const KIND_ICONS: Record<string, string> = {
  type: '\u25c7',      // diamond
  member: '\u2500',    // horizontal line
  function: '\u0192',  // function f
  constant: '\u2022',  // bullet
  property: '\u2022',
  variable: '\u2022',
};

export interface FileTooltipProps {
  file: FileEntry;
  x: number;
  y: number;
}

export function FileTooltip({ file, x, y }: FileTooltipProps) {
  const dir = file.path.substring(0, file.path.lastIndexOf('/') + 1);
  const name = file.path.substring(dir.length);
  const stateLabel = STATE_LABELS[file.state] ?? file.state;
  const isFailed = file.state === 'failed';
  const hasMetrics = !!(file.lines || file.symbols);
  const hasTree = !!(file.tree && file.tree.length > 0);
  const hasTiming = !!(file.indexedAt || file.embeddedAt);

  return (
    <div
      className="ft-tooltip"
      style={{ left: x, top: y }}
      data-state={file.state}
    >
      {/* Language accent bar */}
      <div
        className="ft-accent"
        style={{ background: isFailed ? 'var(--red)' : file.lang.color }}
      />

      <div className="ft-content">
        {/* Path */}
        <div className="ft-path">
          {dir && <span className="ft-dir">{dir}</span>}
          <span className="ft-name">{name}</span>
        </div>

        {/* State + language row */}
        <div className="ft-meta">
          <span className={`ft-state ${file.state}`}>{stateLabel}</span>
          {file.processing && <span className="ft-processing" />}
          <span className="ft-lang">{file.lang.name}</span>
        </div>

        {/* Metrics row */}
        {hasMetrics && (
          <div className="ft-metrics">
            {file.lines != null && file.lines > 0 && (
              <span className="ft-metric">
                <span className="ft-val">{file.lines.toLocaleString()}</span> lines
              </span>
            )}
            {file.symbols != null && file.symbols > 0 && (
              <span className="ft-metric">
                <span className="ft-val">{file.symbols}</span> symbols
              </span>
            )}
            {file.chunks != null && file.chunks > 0 && (
              <span className="ft-metric">
                <span className="ft-val">{file.chunks}</span> chunks
              </span>
            )}
          </div>
        )}

        {/* Symbol tree */}
        {hasTree && (
          <div className="ft-tree">
            {file.tree!.map((sym, i) => (
              <SymbolRow key={i} sym={sym} />
            ))}
          </div>
        )}

        {/* Phase timing */}
        {hasTiming && (
          <div className="ft-timing">
            {file.indexedAt && (
              <span className="ft-phase">
                <span className="ft-phase-label">indexed</span>
                <span className="ft-phase-val">{relativeTime(file.indexedAt)}</span>
              </span>
            )}
            {file.embeddedAt && (
              <span className="ft-phase">
                <span className="ft-phase-label">embedded</span>
                <span className="ft-phase-val">{relativeTime(file.embeddedAt)}</span>
              </span>
            )}
            {file.indexedAt && file.embeddedAt && (
              <span className="ft-phase">
                <span className="ft-phase-label">embed time</span>
                <span className="ft-phase-val">{duration(file.indexedAt, file.embeddedAt)}</span>
              </span>
            )}
          </div>
        )}

        {/* Error — only for failed files */}
        {isFailed && file.error && (
          <div className="ft-error">{truncate(file.error, 120)}</div>
        )}
      </div>
    </div>
  );
}

function SymbolRow({ sym }: { sym: SymbolNode }) {
  const icon = KIND_ICONS[sym.k] ?? '\u2022';
  const isContainer = sym.m != null && sym.m > 0;
  const lineRange = sym.l && sym.e && sym.e > sym.l
    ? `${sym.l}\u2013${sym.e}`
    : sym.l ? `${sym.l}` : null;

  return (
    <div className={`ft-sym ${isContainer ? 'ft-sym-type' : ''}`}>
      <span className="ft-sym-icon">{icon}</span>
      <span className="ft-sym-name">{sym.n}</span>
      <span className="ft-sym-kind">{sym.k}</span>
      {isContainer && (
        <span className="ft-sym-members">{sym.m}</span>
      )}
      {lineRange && (
        <span className="ft-sym-line">{lineRange}</span>
      )}
    </div>
  );
}

function relativeTime(iso: string): string {
  const ms = Date.now() - Date.parse(iso);
  if (!Number.isFinite(ms) || ms < 0) return 'just now';
  const sec = Math.floor(ms / 1000);
  if (sec < 60) return `${sec}s ago`;
  const min = Math.floor(sec / 60);
  if (min < 60) return `${min}m ago`;
  const hr = Math.floor(min / 60);
  if (hr < 24) return `${hr}h ago`;
  const d = Math.floor(hr / 24);
  return `${d}d ago`;
}

function duration(from: string, to: string): string {
  const ms = Date.parse(to) - Date.parse(from);
  if (!Number.isFinite(ms) || ms < 0) return '—';
  if (ms < 1000) return `${ms}ms`;
  const sec = ms / 1000;
  if (sec < 60) return `${sec.toFixed(1)}s`;
  const min = sec / 60;
  return `${min.toFixed(1)}m`;
}

function truncate(s: string, max: number): string {
  const firstLine = s.split('\n')[0] ?? s;
  const clean = firstLine.replace(/^.*Exception.*?:\s*/i, '');
  if (clean.length <= max) return clean;
  return clean.substring(0, max - 1) + '\u2026';
}
