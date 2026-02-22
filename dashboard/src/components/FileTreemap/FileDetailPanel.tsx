import { For, Show } from 'solid-js';
import type { FileEntry, SymbolNode } from '../../types';
import './FileDetailPanel.css';

const STATE_LABELS: Record<string, string> = {
  hidden: 'Pending',
  discovered: 'Discovered',
  classified: 'Indexing',
  parsed: 'Indexed',
  struct_embedded: 'Ready',
  full_embedded: 'Fully Indexed',
  failed: 'Failed',
};

const KIND_ICONS: Record<string, string> = {
  type: '\u25c7',
  member: '\u2500',
  function: '\u0192',
  constant: '\u2022',
  property: '\u2022',
  variable: '\u2022',
};

export interface FileDetailPanelProps {
  file: FileEntry;
  onClose: () => void;
}

export function FileDetailPanel(props: FileDetailPanelProps) {
  const isFailed = () => props.file.state === 'failed';
  const hasMetrics = () => Boolean(props.file.lines || props.file.symbols || props.file.chunks);
  const hasTree = () => Boolean(props.file.tree && props.file.tree.length > 0);
  const hasTiming = () => Boolean(props.file.indexedAt || props.file.embeddedAt);

  return (
    <div class="fdp" onClick={(e) => e.stopPropagation()}>
      {/* Header row — same elements as the old strip */}
      <div class="fdp-header">
        <span
          class="fdp-swatch"
          style={{ background: isFailed() ? 'var(--red)' : props.file.lang.color }}
        />
        <span class="fdp-path">{props.file.path}</span>
        <span class="fdp-lang">{props.file.lang.name}</span>
        <span class={`fdp-state ${props.file.state}`}>
          {STATE_LABELS[props.file.state] ?? props.file.state}
        </span>
        <button class="fdp-close" onClick={() => props.onClose()} aria-label="Close">
          &times;
        </button>
      </div>

      {/* Body — scrollable detail content */}
      <div class="fdp-body">
        {/* Headline */}
        <Show when={props.file.headline}>
          <div class="fdp-headline">{props.file.headline}</div>
        </Show>

        {/* Metrics row */}
        <Show when={hasMetrics()}>
          <div class="fdp-metrics">
            <Show when={props.file.lines != null && props.file.lines > 0}>
              <span class="fdp-metric">
                <span class="fdp-val">{props.file.lines?.toLocaleString()}</span> lines
              </span>
            </Show>
            <Show when={props.file.symbols != null && props.file.symbols > 0}>
              <span class="fdp-metric">
                <span class="fdp-val">{props.file.symbols}</span> symbols
              </span>
            </Show>
            <Show when={props.file.chunks != null && props.file.chunks > 0}>
              <span class="fdp-metric">
                <span class="fdp-val">{props.file.chunks}</span> chunks
              </span>
            </Show>
            <Show when={props.file.tokens != null && props.file.tokens > 0}>
              <span class="fdp-metric">
                <span class="fdp-val">{props.file.tokens?.toLocaleString()}</span> tokens
              </span>
            </Show>
          </div>
        </Show>

        {/* Timing */}
        <Show when={hasTiming()}>
          <div class="fdp-timing">
            <Show when={props.file.indexedAt}>
              <span class="fdp-phase">
                <span class="fdp-phase-label">indexed</span>
                <span class="fdp-phase-val">{relativeTime(props.file.indexedAt!)}</span>
              </span>
            </Show>
            <Show when={props.file.embeddedAt}>
              <span class="fdp-phase">
                <span class="fdp-phase-label">embedded</span>
                <span class="fdp-phase-val">{relativeTime(props.file.embeddedAt!)}</span>
              </span>
            </Show>
            <Show when={props.file.indexedAt && props.file.embeddedAt}>
              <span class="fdp-phase">
                <span class="fdp-phase-label">embed time</span>
                <span class="fdp-phase-val">{duration(props.file.indexedAt!, props.file.embeddedAt!)}</span>
              </span>
            </Show>
          </div>
        </Show>

        {/* Symbol tree */}
        <Show when={hasTree()}>
          <div class="fdp-tree">
            <For each={props.file.tree ?? []}>
              {(sym) => <SymbolRow sym={sym} />}
            </For>
          </div>
        </Show>

        {/* Error — full message, not truncated */}
        <Show when={isFailed() && !!props.file.error}>
          <div class="fdp-error">{props.file.error}</div>
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
    <div class={`fdp-sym ${isContainer() ? 'fdp-sym-type' : ''}`}>
      <span class="fdp-sym-icon">{icon()}</span>
      <span class="fdp-sym-name">{props.sym.n}</span>
      <span class="fdp-sym-kind">{props.sym.k}</span>
      <Show when={isContainer()}>
        <span class="fdp-sym-members">{props.sym.m}</span>
      </Show>
      <Show when={lineRange()}>
        {(line) => <span class="fdp-sym-line">{line()}</span>}
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
  if (!Number.isFinite(ms) || ms < 0) return '\u2014';
  if (ms < 1000) return `${ms}ms`;
  const sec = ms / 1000;
  if (sec < 60) return `${sec.toFixed(1)}s`;
  const min = sec / 60;
  return `${min.toFixed(1)}m`;
}
