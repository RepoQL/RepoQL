import { createMemo, For, Show } from 'solid-js';
import type { SourceSection, FileState } from '../../types';
import './SourceCard.css';

export interface SourceCardProps {
  section: SourceSection;
  active: boolean;
  onClick: (e: MouseEvent) => void;
}

const STATE_ORDER: FileState[] = [
  'full_embedded', 'struct_embedded', 'parsed', 'classified', 'discovered', 'hidden', 'failed',
];

export function SourceCard(props: SourceCardProps) {
  const isImport = () => props.section.prefix !== '';

  const stats = createMemo(() => {
    const allFiles = props.section.groups.flatMap((g) => g.files);
    const byState: Record<string, number> = {};
    let tokens = 0;

    for (const f of allFiles) {
      byState[f.state] = (byState[f.state] ?? 0) + 1;
      tokens += f.tokens ?? 0;
    }

    const ready = (byState['struct_embedded'] ?? 0) + (byState['full_embedded'] ?? 0);
    const failed = byState['failed'] ?? 0;
    const total = props.section.total || 1;

    const segments = STATE_ORDER
      .filter((s) => (byState[s] ?? 0) > 0)
      .map((s) => ({ state: s, fraction: (byState[s] ?? 0) / total }));

    return { ready, failed, tokens, segments, total };
  });

  return (
    <button
      class={`source-tab ${props.active ? 'active' : ''}`}
      onClick={(e) => props.onClick(e)}
    >
      <div class="st-top">
        <Show when={isImport()}>
          <span class="st-icon">&#x2197;</span>
        </Show>
        <span class="st-label">{props.section.label}</span>
        <span class="st-count">{props.section.total.toLocaleString()}</span>
      </div>

      <div class="st-progress">
        <For each={stats().segments}>
          {(seg) => (
            <div
              class={`st-seg ${seg.state}`}
              style={{ 'flex-grow': Math.round(seg.fraction * 100) }}
            />
          )}
        </For>
      </div>
    </button>
  );
}
