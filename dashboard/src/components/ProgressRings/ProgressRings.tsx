import { createMemo } from 'solid-js';
import './ProgressRings.css';

export interface ProgressRingsProps {
  total: number;
  parsed: number;
  structEmbedded: number;
  fullEmbedded: number;
  /** Whether indexing is complete */
  complete: boolean;
}

export function ProgressRings(props: ProgressRingsProps) {
  const totalSafe = createMemo(() => props.total || 1);
  const parsedP = createMemo(() => props.parsed / totalSafe());
  const structP = createMemo(() => props.structEmbedded / totalSafe());
  const fullP = createMemo(() => props.fullEmbedded / totalSafe());

  const c65 = 2 * Math.PI * 65;
  const c50 = 2 * Math.PI * 50;
  const c35 = 2 * Math.PI * 35;

  const readyPct = createMemo(() =>
    props.complete ? 100 : Math.min(100, Math.floor((props.structEmbedded / totalSafe()) * 100)),
  );

  return (
    <div class="rings-section">
      <svg width="150" height="150" viewBox="-75 -75 150 150" style={{ overflow: 'visible' }}>
        {/* Background rings */}
        <circle class="ring-bg" cx="0" cy="0" r="65" />
        <circle class="ring-bg" cx="0" cy="0" r="50" />
        <circle class="ring-bg" cx="0" cy="0" r="35" />

        {/* Fill rings — last step outermost */}
        <circle
          class="ring-fill"
          cx="0"
          cy="0"
          r="65"
          stroke="var(--green)"
          stroke-dasharray={`${fullP() * c65} ${c65}`}
          transform="rotate(-90)"
        />
        <circle
          class="ring-fill"
          cx="0"
          cy="0"
          r="50"
          stroke="var(--amber)"
          stroke-dasharray={`${structP() * c50} ${c50}`}
          transform="rotate(-90)"
        />
        <circle
          class="ring-fill"
          cx="0"
          cy="0"
          r="35"
          stroke="var(--blue)"
          stroke-dasharray={`${parsedP() * c35} ${c35}`}
          transform="rotate(-90)"
        />

        {/* Center text */}
        <text class={`ring-center-pct${props.complete ? ' done' : ''}`} x="0" y="4">
          {readyPct()}%
        </text>
        <text class="ring-center-label" x="0" y="16">ready</text>
      </svg>

      <div class="ring-legend">
        <div class="rl">
          <span class="rl-left">
            <span class="rl-dot" style={{ background: 'var(--green)' }} />
            Fully indexed
          </span>
          <span class="rl-val">{props.fullEmbedded} / {props.total}</span>
        </div>
        <div class="rl">
          <span class="rl-left">
            <span class="rl-dot" style={{ background: 'var(--amber)' }} />
            Ready
          </span>
          <span class="rl-val">{props.structEmbedded} / {props.total}</span>
        </div>
        <div class="rl">
          <span class="rl-left">
            <span class="rl-dot" style={{ background: 'var(--blue)' }} />
            Parsed
          </span>
          <span class="rl-val">{props.parsed} / {props.total}</span>
        </div>
      </div>
    </div>
  );
}
