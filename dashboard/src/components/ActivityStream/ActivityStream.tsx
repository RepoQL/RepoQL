import { For, Show } from 'solid-js';
import type { ActivityEntry } from '../../types';
import './ActivityStream.css';

export interface ActivityStreamProps {
  entries: ActivityEntry[];
}

export function ActivityStream(props: ActivityStreamProps) {
  return (
    <div class="stream-section">
      <div class="stream-header">
        <span class="stream-label">Activity</span>
        <span class="stream-count">{props.entries.length}</span>
      </div>
      <div class="stream-list">
        <Show when={props.entries.length === 0}>
          <div class="stream-empty">Waiting for file activity</div>
        </Show>
        <For each={props.entries}>
          {(entry) => (
            <div class="s-item">
              <span class="s-dot" style={{ background: entry.langColor }} />
              <span class="s-op">{entry.operation}</span>
              <span class="s-path">{entry.path}</span>
            </div>
          )}
        </For>
      </div>
    </div>
  );
}
