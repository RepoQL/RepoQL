import { For } from 'solid-js';
import type { ActivityEntry } from '../../types';
import './ActivityStream.css';

export interface ActivityStreamProps {
  entries: ActivityEntry[];
}

export function ActivityStream(props: ActivityStreamProps) {
  return (
    <div class="stream-section">
      <div class="stream-label">Activity</div>
      <div class="stream-list">
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
