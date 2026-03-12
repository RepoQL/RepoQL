import { createMemo, For, Show } from 'solid-js';
import type { QueryEntry, QueryState, ToolName } from '../../types';
import './QueryActivity.css';

export interface QueryActivityProps {
  entries: QueryEntry[];
  variant?: 'sidebar' | 'hero';
  now?: number;
}

const TOOL_COLORS: Record<ToolName, string> = {
  explore: 'var(--blue)',
  explain: 'var(--amber)',
  query: 'var(--green)',
  read: 'var(--fg2)',
};

const STATE_LABELS: Record<QueryState, string> = {
  running: 'Active',
  completed: 'Done',
  failed: 'Failed',
};

export function QueryActivity(props: QueryActivityProps) {
  const activeCount = createMemo(() => props.entries.filter((entry) => entry.state === 'running').length);
  const failedCount = createMemo(() => props.entries.filter((entry) => entry.state === 'failed').length);
  const variant = () => props.variant ?? 'sidebar';

  return (
    <div class={`query-activity ${variant()}`}>
      <div class="qa-header">
        <span class="qa-title">Tool Activity</span>
        <span class="qa-summary">
          {activeCount()} active
          <Show when={failedCount() > 0}> · {failedCount()} failed</Show>
        </span>
      </div>
      <div class="qa-list">
        <Show when={props.entries.length === 0}>
          <div class="qa-empty">No tool activity yet</div>
        </Show>
        <For each={props.entries}>
          {(entry) => (
            <QueryRow entry={entry} now={props.now} />
          )}
        </For>
      </div>
    </div>
  );
}

function QueryRow(props: { entry: QueryEntry; now?: number }) {
  const color = createMemo(() => TOOL_COLORS[props.entry.tool]);
  const tokens = createMemo(() => formatTokens(props.entry.tokensUsed, props.entry.tokenBudget));
  const elapsedLabel = createMemo(() => {
    const liveElapsed = props.entry.state === 'running'
      ? Math.max(0, (props.now ?? Date.now()) - props.entry.timestamp)
      : props.entry.elapsed;
    return formatElapsed(liveElapsed, props.entry.state === 'running');
  });
  const resultLabel = createMemo(() => {
    if (props.entry.resultSummary) {
      return props.entry.resultSummary;
    }

    return props.entry.state === 'running' ? 'Awaiting response' : 'No summary';
  });
  const whenLabel = createMemo(() => formatRelativeTime(props.entry.timestamp, props.now ?? Date.now()));

  return (
    <div class={`qa-row ${props.entry.state}`} style={{ '--qa-accent': color() }}>
      <div class="qa-row-main">
        <div class="qa-tool-group">
          <span class="qa-tool">{props.entry.tool}</span>
          <span class={`qa-state ${props.entry.state}`}>{STATE_LABELS[props.entry.state]}</span>
        </div>
        <span class="qa-primary" title={`${props.entry.params} · ${resultLabel()}`}>
          <span class="qa-params">{props.entry.params}</span>
          <span class="qa-sep">·</span>
          <span class="qa-result">{resultLabel()}</span>
        </span>
        <div class="qa-meta-strip">
          <span class="qa-elapsed">{elapsedLabel()}</span>
          <span class="qa-meta">{tokens()}</span>
          <span class="qa-when">{whenLabel()}</span>
        </div>
      </div>
    </div>
  );
}

function formatTokens(tokensUsed: number, tokenBudget: number): string {
  if (tokenBudget > 0) {
    return `${tokensUsed}/${tokenBudget}t`;
  }

  return `${tokensUsed}t`;
}

function formatElapsed(ms: number, live: boolean): string {
  if (ms < 1000) {
    return live ? `${ms}ms live` : `${ms}ms`;
  }

  const sec = Math.floor(ms / 1000);
  if (sec < 60) {
    return live ? `${sec}s live` : `${sec}s`;
  }

  const min = Math.floor(sec / 60);
  const rem = sec % 60;
  return live ? `${min}m ${rem}s live` : `${min}m ${rem}s`;
}

function formatRelativeTime(timestamp: number, now: number): string {
  const deltaMs = Math.max(0, now - timestamp);
  const seconds = Math.floor(deltaMs / 1000);
  if (seconds < 5) return 'just now';
  if (seconds < 60) return `${seconds}s ago`;
  const minutes = Math.floor(seconds / 60);
  return `${minutes}m ago`;
}
