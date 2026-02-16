import type { QueryEntry, ToolName } from '../../types';
import './QueryActivity.css';

export interface QueryActivityProps {
  entries: QueryEntry[];
}

const TOOL_COLORS: Record<ToolName, string> = {
  explore: 'var(--blue)',
  explain: 'var(--amber)',
  query: 'var(--green)',
  read: 'var(--fg2)',
};

export function QueryActivity({ entries }: QueryActivityProps) {
  return (
    <div className="query-activity">
      <div className="qa-header">
        <span className="qa-title">Tool Activity</span>
        <span className="qa-count">{entries.length}</span>
      </div>
      <div className="qa-list">
        {entries.length === 0 && (
          <div className="qa-empty">No queries yet</div>
        )}
        {entries.map((entry) => (
          <QueryRow key={entry.id} entry={entry} />
        ))}
      </div>
    </div>
  );
}

function QueryRow({ entry }: { entry: QueryEntry }) {
  const color = TOOL_COLORS[entry.tool];
  const efficiency = entry.tokenBudget > 0
    ? Math.round((entry.tokensUsed / entry.tokenBudget) * 100)
    : 0;

  return (
    <div className="qa-row">
      <div className="qa-row-top">
        <span className="qa-tool" style={{ color }}>{entry.tool}</span>
        <span className="qa-elapsed">{entry.elapsed}ms</span>
      </div>
      <div className="qa-params">{entry.params}</div>
      <div className="qa-row-bottom">
        <span className="qa-result">{entry.resultSummary}</span>
        <span className="qa-tokens">
          {entry.tokensUsed}/{entry.tokenBudget}t
          <span className={`qa-eff ${efficiency > 85 ? 'high' : efficiency > 50 ? 'mid' : 'low'}`}>
            {efficiency}%
          </span>
        </span>
      </div>
    </div>
  );
}
