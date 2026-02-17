import type { PipelinePhase } from '../../types';
import { PHASE_LABELS } from '../../types';
import './PhaseIndicator.css';

export interface PhaseIndicatorProps {
  phase: PipelinePhase;
}

export function PhaseIndicator(props: PhaseIndicatorProps) {
  return (
    <div class="phase-section">
      <div class="phase-label">Phase</div>
      <div class="phase-val">{PHASE_LABELS[props.phase]}</div>
    </div>
  );
}
