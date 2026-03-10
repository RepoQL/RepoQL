import { PHASE_LABELS, type PipelinePhase } from '../../types';
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

  const phaseClass = () =>
    props.phase === 'complete' ? 'hdr-phase complete' : 'hdr-phase';

  return (
    <header class="status-header">
      <div class="hdr-left">
        <div class={dotClass()} />
        <span class="hdr-title">repoql · {props.title}</span>
      </div>
      <div class="hdr-right">
        <span class={phaseClass()}>{PHASE_LABELS[props.phase]}</span>
        <span class="hdr-elapsed">{props.elapsed.toFixed(1)}s</span>
      </div>
    </header>
  );
}
