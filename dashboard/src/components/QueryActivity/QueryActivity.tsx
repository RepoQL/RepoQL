import { createMemo, For, Show } from 'solid-js';
import type { QueryEntry, ToolName } from '../../types';
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

export function QueryActivity(props: QueryActivityProps) {
  return (
    <div class="query-activity">
      <div class="qa-header">
        <span class="qa-title">Tool Activity</span>
        <span class="qa-count">{props.entries.length}</span>
      </div>
      <div class="qa-list">
        <Show when={props.entries.length === 0}>
          <div class="qa-empty">No queries yet</div>
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
  const efficiency = createMemo(() =>
    props.entry.tokenBudget > 0
      ? Math.round((props.entry.tokensUsed / props.entry.tokenBudget) * 100)
      : 0,
  );

  return (
    <div class="qa-row">
      <div class="qa-row-top">
        <span class="qa-tool" style={{ color: color() }}>{props.entry.tool}</span>
        <span class="qa-elapsed">{props.entry.elapsed}ms</span>
      </div>
      <div class="qa-params">{props.entry.params}</div>
      <div class="qa-row-bottom">
        <span class="qa-result">{props.entry.resultSummary}</span>
        <span class="qa-tokens">
          {props.entry.tokensUsed}/{props.entry.tokenBudget}t
          <span class={`qa-eff ${efficiency() > 85 ? 'high' : efficiency() > 50 ? 'mid' : 'low'}`}>
            {efficiency()}%
          </span>
        </span>
      </div>
    </div>
  );
}
