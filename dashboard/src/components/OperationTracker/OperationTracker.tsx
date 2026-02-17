import { createMemo, createSignal, For, Show } from 'solid-js';
import type { OperationSnapshot, OperationState } from '../../types';
import './OperationTracker.css';

export interface OperationTrackerProps {
  operations: OperationSnapshot[];
  now: number;
}

const STATE_CONFIG: Record<OperationState, { label: string; class: string }> = {
  running: { label: 'Running', class: 'op-state running' },
  completed: { label: 'Completed', class: 'op-state completed' },
  completed_with_failures: { label: 'Completed (failures)', class: 'op-state with-failures' },
  cancelled: { label: 'Cancelled', class: 'op-state cancelled' },
};

const KIND_ICONS: Record<string, string> = {
  startup: '\u25B6',   // ▶
  reindex: '\u21BB',   // ↻
  import: '\u2193',    // ↓
};

export function OperationTracker(props: OperationTrackerProps) {
  const active = createMemo(() => props.operations.filter((operation) => operation.state === 'running'));
  const completed = createMemo(() => props.operations.filter((operation) => operation.state !== 'running'));

  return (
    <div class="operation-tracker">
      <div class="ot-header">
        <span class="ot-title">Operations</span>
        <div class="ot-counts">
          <Show when={active().length > 0}>
            <span class="ot-active-count">{active().length} active</span>
          </Show>
          <span class="ot-total-count">{props.operations.length} total</span>
        </div>
      </div>

      <Show when={props.operations.length === 0}>
        <div class="ot-empty">No operations</div>
      </Show>

      <div class="ot-list">
        <For each={active()}>
          {(operation) => (
            <OperationRow op={operation} now={props.now} />
          )}
        </For>
        <For each={completed()}>
          {(operation) => (
            <OperationRow op={operation} now={props.now} />
          )}
        </For>
      </div>
    </div>
  );
}

function OperationRow(props: { op: OperationSnapshot; now: number }) {
  const [expanded, setExpanded] = createSignal(props.op.state === 'running');
  const config = createMemo(() => STATE_CONFIG[props.op.state]);
  const elapsed = createMemo(() => formatDuration((props.op.completedAt ?? props.now) - props.op.createdAt));
  const icon = createMemo(() => KIND_ICONS[props.op.kind] ?? '\u25CF');
  const recentLog = createMemo(() => props.op.recentLog.slice(0, 8));

  return (
    <div class={`ot-row ${props.op.state === 'running' ? 'active' : ''}`}>
      <button class="ot-row-header" onClick={() => setExpanded((value) => !value)}>
        <div class="ot-row-left">
          <span class="ot-kind-icon">{icon()}</span>
          <div class="ot-row-info">
            <span class="ot-description">{props.op.description}</span>
            <span class={config().class}>{config().label}</span>
          </div>
        </div>
        <span class="ot-elapsed">{elapsed()}</span>
      </button>

      {/* Progress bar */}
      <Show when={props.op.totalFiles > 0}>
        <div class="ot-progress">
          <div class="ot-progress-bar">
            <div
              class="ot-progress-indexed"
              style={{ width: `${(props.op.indexedCount / props.op.totalFiles) * 100}%` }}
            />
            <div
              class="ot-progress-embedded"
              style={{ width: `${(props.op.embeddedCount / props.op.totalFiles) * 100}%` }}
            />
            <Show when={props.op.failedCount > 0}>
              <div
                class="ot-progress-failed"
                style={{ width: `${(props.op.failedCount / props.op.totalFiles) * 100}%` }}
              />
            </Show>
          </div>
          <div class="ot-progress-labels">
            <span>{props.op.readyPercent}% ready</span>
            <span>{props.op.indexedCount}/{props.op.totalFiles} indexed</span>
            <span>{props.op.embeddedCount} embedded</span>
            <Show when={props.op.failedCount > 0}>
              <span class="ot-failed-label">{props.op.failedCount} failed</span>
            </Show>
          </div>
        </div>
      </Show>

      {/* Expanded: milestones + recent log */}
      <Show when={expanded()}>
        <div class="ot-detail">
          <Show when={props.op.milestones.length > 0}>
            <div class="ot-milestones">
              <For each={props.op.milestones}>
                {(milestone) => (
                  <div class="ot-milestone">
                    <span class="ot-ms-dot" />
                    <span class="ot-ms-name">{milestone.name}</span>
                    <Show when={milestone.detail}>
                      <span class="ot-ms-detail">{milestone.detail}</span>
                    </Show>
                    <span class="ot-ms-time">{formatTime(milestone.timestamp, props.op.createdAt)}</span>
                  </div>
                )}
              </For>
            </div>
          </Show>

          <Show when={props.op.recentLog.length > 0}>
            <div class="ot-log">
              <For each={recentLog()}>
                {(entry) => (
                  <div class="ot-log-entry">
                    <span class={`ot-log-type ${entry.type.includes('failed') ? 'failed' : ''}`}>
                      {entry.type}
                    </span>
                    <Show when={entry.uri}>
                      <span class="ot-log-uri">{truncateUri(entry.uri ?? '')}</span>
                    </Show>
                    <Show when={entry.message}>
                      <span class="ot-log-msg">{entry.message}</span>
                    </Show>
                  </div>
                )}
              </For>
            </div>
          </Show>
        </div>
      </Show>
    </div>
  );
}

function formatDuration(ms: number): string {
  const sec = Math.floor(ms / 1000);
  if (sec < 60) return `${sec}s`;
  const min = Math.floor(sec / 60);
  if (min < 60) return `${min}m ${sec % 60}s`;
  const hr = Math.floor(min / 60);
  return `${hr}h ${min % 60}m`;
}

function formatTime(timestamp: number, base: number): string {
  const delta = Math.floor((timestamp - base) / 1000);
  return `+${delta}s`;
}

function truncateUri(uri: string): string {
  if (uri.length <= 50) return uri;
  return '...' + uri.slice(-47);
}
