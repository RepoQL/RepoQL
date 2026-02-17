import { createMemo, createSignal, For, Show } from 'solid-js';
import type { ErrorCategory, FileError } from '../../types';
import { ERROR_CATEGORY_LABELS } from '../../types';
import './ErrorPanel.css';

export interface ErrorPanelProps {
  errors: FileError[];
}

interface ErrorGroup {
  category: ErrorCategory;
  label: string;
  errors: FileError[];
}

export function ErrorPanel(props: ErrorPanelProps) {
  const groups = createMemo(() => groupErrors(props.errors));

  return (
    <Show
      when={props.errors.length === 0}
      fallback={(
        <div class="error-panel">
          <div class="error-panel-header">
            <span class="error-panel-title">Errors</span>
            <span class="error-panel-count">{props.errors.length}</span>
          </div>
          <div class="error-panel-body">
            <For each={groups()}>
              {(group) => (
                <ErrorGroupSection group={group} />
              )}
            </For>
          </div>
        </div>
      )}
    >
      <div class="error-panel">
        <div class="error-panel-header">
          <span class="error-panel-title">Errors</span>
          <span class="error-panel-count zero">0</span>
        </div>
      </div>
    </Show>
  );
}

function ErrorGroupSection(props: { group: ErrorGroup }) {
  const [expanded, setExpanded] = createSignal(true);

  return (
    <div class="err-group">
      <button class="err-group-header" onClick={() => setExpanded((value) => !value)}>
        <span class="err-group-chevron">{expanded() ? '\u25BE' : '\u25B8'}</span>
        <span class="err-group-label">{props.group.label}</span>
        <span class="err-group-count">{props.group.errors.length}</span>
      </button>
      <Show when={expanded()}>
        <div class="err-group-items">
          <For each={props.group.errors}>
            {(err) => (
              <div class="err-item">
                <div class="err-item-path">
                  <span class="err-lang-dot" style={{ background: err.lang.color }} />
                  {err.path}
                </div>
                <div class="err-item-msg">{err.message}</div>
                <Show when={err.hint}>
                  <div class="err-item-hint">{err.hint}</div>
                </Show>
              </div>
            )}
          </For>
        </div>
      </Show>
    </div>
  );
}

function groupErrors(errors: FileError[]): ErrorGroup[] {
  const map = new Map<ErrorCategory, FileError[]>();
  for (const err of errors) {
    let arr = map.get(err.category);
    if (!arr) {
      arr = [];
      map.set(err.category, arr);
    }
    arr.push(err);
  }
  return Array.from(map.entries())
    .map(([category, errs]) => ({
      category,
      label: ERROR_CATEGORY_LABELS[category],
      errors: errs,
    }))
    .sort((a, b) => b.errors.length - a.errors.length);
}
