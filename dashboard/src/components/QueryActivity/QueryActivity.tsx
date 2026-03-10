import { createMemo, For, Show } from 'solid-js';
import type { QueryEntry, QueryState, ToolName } from '../../types';
import './QueryActivity.css';

export interface QueryActivityProps {
  entries: QueryEntry[];
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

  return (
    <div class="query-activity">
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
            <QueryRow entry={entry} />
          )}
        </For>
      </div>
    </div>
  );
}

function QueryRow(props: { entry: QueryEntry }) {
  const color = createMemo(() => TOOL_COLORS[props.entry.tool]);
  const tokens = createMemo(() => {
    if (props.entry.tokenBudget > 0) {
      return `${props.entry.tokensUsed}/${props.entry.tokenBudget}t`;
    }

    return `${props.entry.tokensUsed}t`;
  });

  const elapsedLabel = createMemo(() =>
    props.entry.state === 'running' ? `${props.entry.elapsed}ms live` : `${props.entry.elapsed}ms`,
  );

  const resultLabel = createMemo(() => {
    if (props.entry.resultSummary) {
      return props.entry.resultSummary;
    }

    return props.entry.state === 'running' ? 'Awaiting response' : 'No summary';
  });

  return (
    <div class={`qa-row ${props.entry.state}`} style={{ '--qa-accent': color() }}>
      <div class="qa-row-top">
        <div class="qa-tool-group">
          <span class="qa-tool">{props.entry.tool}</span>
          <span class={`qa-state ${props.entry.state}`}>{STATE_LABELS[props.entry.state]}</span>
        </div>
        <span class="qa-elapsed">{elapsedLabel()}</span>
      </div>
      <div class="qa-params">{props.entry.params}</div>
      <div class="qa-row-bottom">
        <span class="qa-result">{resultLabel()}</span>
        <span class="qa-meta">{tokens()}</span>
      </div>
    </div>
  );
}
