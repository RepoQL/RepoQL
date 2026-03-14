import { createMemo, For, Show } from 'solid-js';
import type { IndexingDiagnosticsState, StuckItemEntry } from '../../types';
import './StuckItems.css';

export interface StuckItemsProps {
  indexing: IndexingDiagnosticsState;
  now: number;
}

export function StuckItems(props: StuckItemsProps) {
  const visibleItems = createMemo(() =>
    props.indexing.stuckItems
      .slice()
      .sort((a, b) => (b.elapsedMs ?? 0) - (a.elapsedMs ?? 0))
      .slice(0, 5),
  );

  const headline = createMemo(() => {
    if (props.indexing.deferredRetryActive > 0) {
      return 'Deferred retries are running';
    }
    if (props.indexing.deferredRetryPending > 0) {
      return `${props.indexing.deferredRetryPending} files waiting for idle retry`;
    }
    if (visibleItems().length > 0) {
      return 'Slow items need attention';
    }
    return 'No deferred or slow items';
  });

  return (
    <div class="stuck-items">
      <div class="stuck-items-header">
        <div>
          <div class="stuck-items-title">Stuck Items</div>
          <div class="stuck-items-headline">{headline()}</div>
        </div>
        <div class="stuck-items-counts">
          <span>{props.indexing.hotPathTimeouts} hot</span>
          <span>{props.indexing.deferredToIdleCount} deferred</span>
          <span>{props.indexing.deferredRetryTimeouts} failed</span>
        </div>
      </div>

      <Show when={visibleItems().length > 0} fallback={<div class="stuck-items-empty">Nothing is currently wedged.</div>}>
        <div class="stuck-items-list">
          <For each={visibleItems()}>
            {(item) => <StuckRow item={item} now={props.now} />}
          </For>
        </div>
      </Show>
    </div>
  );
}

function StuckRow(props: { item: StuckItemEntry; now: number }) {
  const elapsed = () => {
    if (props.item.elapsedMs != null) {
      return formatDuration(props.item.elapsedMs);
    }

    return formatDuration(Math.max(0, props.now - props.item.enqueuedAt));
  };

  return (
    <div class="stuck-row">
      <div class="stuck-row-main">
        <div class="stuck-row-path">{compactPath(props.item.uri || props.item.name)}</div>
        <div class="stuck-row-meta">
          <span>{props.item.stage}</span>
          <span>{props.item.status}</span>
          <Show when={props.item.workerId != null}>
            <span>worker {props.item.workerId}</span>
          </Show>
          <Show when={props.item.timeoutAttempts > 0}>
            <span>{props.item.timeoutAttempts} timeouts</span>
          </Show>
        </div>
      </div>
      <div class="stuck-row-elapsed">{elapsed()}</div>
    </div>
  );
}

function compactPath(path: string): string {
  const normalized = path.replace(/\\/g, '/').replace(/^[a-z]+:\/\/\/?/i, '');
  const segments = normalized.split('/').filter(Boolean);
  if (segments.length <= 3) {
    return segments.join('/') || path;
  }

  return `.../${segments.slice(-3).join('/')}`;
}

function formatDuration(ms: number): string {
  const totalSeconds = Math.max(0, Math.round(ms / 1000));
  if (totalSeconds < 60) {
    return `${totalSeconds}s`;
  }

  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${minutes}m ${seconds}s`;
}
