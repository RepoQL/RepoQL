import { For, Show } from 'solid-js';
import type { FileEntry, SymbolNode } from '../../types';
import './FileTooltip.css';

const STATE_LABELS: Record<string, string> = {
  hidden: 'Pending',
  discovered: 'Discovered',
  classified: 'Classifying',
  parsed: 'Indexed',
  struct_embedded: 'Ready',
  full_embedded: 'Fully Indexed',
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

export function FileTooltip(props: FileTooltipProps) {
  const dir = () => props.file.path.substring(0, props.file.path.lastIndexOf('/') + 1);
  const name = () => props.file.path.substring(dir().length);
  const stateLabel = () => STATE_LABELS[props.file.state] ?? props.file.state;
  const isFailed = () => props.file.state === 'failed';
  const hasMetrics = () => Boolean(props.file.lines || props.file.symbols);
  const hasTree = () => Boolean(props.file.tree && props.file.tree.length > 0);
  const hasTiming = () => Boolean(props.file.indexedAt || props.file.embeddedAt);

  return (
    <div
      class="ft-tooltip"
      style={{ left: `${props.x}px`, top: `${props.y}px` }}
      data-state={props.file.state}
    >
      {/* Language accent bar */}
      <div
        class="ft-accent"
        style={{ background: isFailed() ? 'var(--red)' : props.file.lang.color }}
      />

      <div class="ft-content">
        {/* Path */}
        <div class="ft-path">
          <Show when={dir()}>
            <span class="ft-dir">{dir()}</span>
          </Show>
          <span class="ft-name">{name()}</span>
        </div>

        {/* State + language row */}
        <div class="ft-meta">
          <span class={`ft-state ${props.file.state}`}>{stateLabel()}</span>
          <Show when={props.file.processing}>
            <span class="ft-processing" />
          </Show>
          <span class="ft-lang">{props.file.lang.name}</span>
        </div>

        {/* Metrics row */}
        <Show when={hasMetrics()}>
          <div class="ft-metrics">
            <Show when={props.file.lines != null && props.file.lines > 0}>
              <span class="ft-metric">
                <span class="ft-val">{props.file.lines?.toLocaleString()}</span> lines
              </span>
            </Show>
            <Show when={props.file.symbols != null && props.file.symbols > 0}>
              <span class="ft-metric">
                <span class="ft-val">{props.file.symbols}</span> symbols
              </span>
            </Show>
            <Show when={props.file.chunks != null && props.file.chunks > 0}>
              <span class="ft-metric">
                <span class="ft-val">{props.file.chunks}</span> chunks
              </span>
            </Show>
          </div>
        </Show>

        {/* Symbol tree */}
        <Show when={hasTree()}>
          <div class="ft-tree">
            <For each={props.file.tree ?? []}>
              {(sym) => (
                <SymbolRow sym={sym} />
              )}
            </For>
          </div>
        </Show>

        {/* Phase timing */}
        <Show when={hasTiming()}>
          <div class="ft-timing">
            <Show when={props.file.indexedAt}>
              <span class="ft-phase">
                <span class="ft-phase-label">indexed</span>
                <span class="ft-phase-val">{relativeTime(props.file.indexedAt ?? '')}</span>
              </span>
            </Show>
            <Show when={props.file.embeddedAt}>
              <span class="ft-phase">
                <span class="ft-phase-label">embedded</span>
                <span class="ft-phase-val">{relativeTime(props.file.embeddedAt ?? '')}</span>
              </span>
            </Show>
            <Show when={props.file.indexedAt && props.file.embeddedAt}>
              <span class="ft-phase">
                <span class="ft-phase-label">embed time</span>
                <span class="ft-phase-val">{duration(props.file.indexedAt ?? '', props.file.embeddedAt ?? '')}</span>
              </span>
            </Show>
          </div>
        </Show>

        {/* Error — only for failed files */}
        <Show when={isFailed() && !!props.file.error}>
          <div class="ft-error">{truncate(props.file.error ?? '', 120)}</div>
        </Show>
      </div>
    </div>
  );
}

function SymbolRow(props: { sym: SymbolNode }) {
  const icon = () => KIND_ICONS[props.sym.k] ?? '\u2022';
  const isContainer = () => props.sym.m != null && props.sym.m > 0;
  const lineRange = () =>
    props.sym.l && props.sym.e && props.sym.e > props.sym.l
      ? `${props.sym.l}\u2013${props.sym.e}`
      : props.sym.l ? `${props.sym.l}` : null;

  return (
    <div class={`ft-sym ${isContainer() ? 'ft-sym-type' : ''}`}>
      <span class="ft-sym-icon">{icon()}</span>
      <span class="ft-sym-name">{props.sym.n}</span>
      <span class="ft-sym-kind">{props.sym.k}</span>
      <Show when={isContainer()}>
        <span class="ft-sym-members">{props.sym.m}</span>
      </Show>
      <Show when={lineRange()}>
        {(line) => <span class="ft-sym-line">{line()}</span>}
      </Show>
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
