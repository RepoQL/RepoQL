import type { PipelinePhase } from '../../types';
import { PHASE_LABELS } from '../../types';
import './PhaseIndicator.css';

export interface PhaseIndicatorProps {
  phase: PipelinePhase;
}

export function PhaseIndicator({ phase }: PhaseIndicatorProps) {
  return (
    <div className="phase-section">
      <div className="phase-label">Phase</div>
      <div className="phase-val">{PHASE_LABELS[phase]}</div>
    </div>
  );
}
