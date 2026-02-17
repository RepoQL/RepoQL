import type { PipelinePhase } from '../../types';
import './StatusHeader.css';

export interface StatusHeaderProps {
  /** What's being indexed — e.g. "github://owner/repo" */
  title: string;
  /** Current pipeline phase */
  phase: PipelinePhase;
  /** Elapsed seconds */
  elapsed: number;
}

export function StatusHeader(props: StatusHeaderProps) {
  const dotClass = () =>
    props.phase === 'complete' ? 'hdr-dot done' :
    props.phase === 'idle' ? 'hdr-dot' :
    'hdr-dot active';

  return (
    <header class="status-header">
      <div class="hdr-left">
        <div class={dotClass()} />
        <span class="hdr-title">repoql · {props.title}</span>
      </div>
      <div class="hdr-right">{props.elapsed.toFixed(1)}s</div>
    </header>
  );
}
