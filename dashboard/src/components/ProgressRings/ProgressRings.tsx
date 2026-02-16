import './ProgressRings.css';

export interface ProgressRingsProps {
  total: number;
  parsed: number;
  structEmbedded: number;
  fullEmbedded: number;
  /** Whether indexing is complete */
  complete: boolean;
}

export function ProgressRings({ total, parsed, structEmbedded, fullEmbedded, complete }: ProgressRingsProps) {
  const t = total || 1;
  const parsedP = parsed / t;
  const structP = structEmbedded / t;
  const fullP = fullEmbedded / t;

  const c65 = 2 * Math.PI * 65;
  const c50 = 2 * Math.PI * 50;
  const c35 = 2 * Math.PI * 35;

  const readyPct = complete ? 100 : Math.min(100, Math.floor((structEmbedded / t) * 100));

  return (
    <div className="rings-section">
      <svg width="150" height="150" viewBox="-75 -75 150 150" style={{ overflow: 'visible' }}>
        {/* Background rings */}
        <circle className="ring-bg" cx="0" cy="0" r="65" />
        <circle className="ring-bg" cx="0" cy="0" r="50" />
        <circle className="ring-bg" cx="0" cy="0" r="35" />

        {/* Fill rings */}
        <circle
          className="ring-fill"
          cx="0" cy="0" r="65"
          stroke="var(--blue)"
          strokeDasharray={`${parsedP * c65} ${c65}`}
          transform="rotate(-90)"
        />
        <circle
          className="ring-fill"
          cx="0" cy="0" r="50"
          stroke="var(--amber)"
          strokeDasharray={`${structP * c50} ${c50}`}
          transform="rotate(-90)"
        />
        <circle
          className="ring-fill"
          cx="0" cy="0" r="35"
          stroke="var(--green)"
          strokeDasharray={`${fullP * c35} ${c35}`}
          transform="rotate(-90)"
        />

        {/* Center text */}
        <text className={`ring-center-pct${complete ? ' done' : ''}`} x="0" y="4">
          {readyPct}%
        </text>
        <text className="ring-center-label" x="0" y="16">ready</text>
      </svg>

      <div className="ring-legend">
        <div className="rl">
          <span className="rl-left">
            <span className="rl-dot" style={{ background: 'var(--blue)' }} />
            Parsed
          </span>
          <span className="rl-val">{parsed} / {total}</span>
        </div>
        <div className="rl">
          <span className="rl-left">
            <span className="rl-dot" style={{ background: 'var(--amber)' }} />
            Struct embed
          </span>
          <span className="rl-val">{structEmbedded} / {total}</span>
        </div>
        <div className="rl">
          <span className="rl-left">
            <span className="rl-dot" style={{ background: 'var(--green)' }} />
            Full embed
          </span>
          <span className="rl-val">{fullEmbedded} / {total}</span>
        </div>
      </div>
    </div>
  );
}
