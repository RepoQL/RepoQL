import { Show } from 'solid-js';
import type { FileEntry } from '../../types';
import './FileTooltip.css';

export interface FileTooltipProps {
  file: FileEntry;
  x: number;
  y: number;
}

export function FileTooltip(props: FileTooltipProps) {
  const dir = () => props.file.path.substring(0, props.file.path.lastIndexOf('/') + 1);
  const name = () => props.file.path.substring(dir().length);
  const isFailed = () => props.file.state === 'failed';
  const hasStructure = () => Boolean(props.file.structure);

  return (
    <div
      class="ft-tooltip"
      style={{ left: `${props.x}px`, top: `${props.y}px` }}
    >
      {/* Language accent bar */}
      <div
        class="ft-accent"
        style={{ background: isFailed() ? 'var(--red)' : props.file.lang.color }}
      />

      <div class="ft-content">
        {/* Path */}
        <div class="ft-path">
          <Show when={dir()}>
            <span class="ft-dir">{dir()}</span>
          </Show>
          <span class="ft-name">{name()}</span>
        </div>

        {/* Structure */}
        <Show when={hasStructure()}>
          <pre class="ft-structure">{props.file.structure}</pre>
        </Show>

        {/* Error — only for failed files */}
        <Show when={isFailed() && !!props.file.error}>
          <div class="ft-error">{props.file.error}</div>
        </Show>
      </div>
    </div>
  );
}
