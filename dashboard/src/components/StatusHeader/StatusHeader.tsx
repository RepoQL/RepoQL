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

export function StatusHeader({ title, phase, elapsed }: StatusHeaderProps) {
  const dotClass =
    phase === 'complete' ? 'hdr-dot done' :
    phase === 'idle' ? 'hdr-dot' :
    'hdr-dot active';

  return (
    <header className="status-header">
      <div className="hdr-left">
        <div className={dotClass} />
        <span className="hdr-title">repoql · {title}</span>
      </div>
      <div className="hdr-right">{elapsed.toFixed(1)}s</div>
    </header>
  );
}
