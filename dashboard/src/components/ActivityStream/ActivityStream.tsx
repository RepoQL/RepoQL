import type { ActivityEntry } from '../../types';
import './ActivityStream.css';

export interface ActivityStreamProps {
  entries: ActivityEntry[];
}

export function ActivityStream({ entries }: ActivityStreamProps) {
  return (
    <div className="stream-section">
      <div className="stream-label">Activity</div>
      <div className="stream-list">
        {entries.map((entry) => (
          <div key={entry.id} className="s-item">
            <span className="s-dot" style={{ background: entry.langColor }} />
            <span className="s-op">{entry.operation}</span>
            <span className="s-path">{entry.path}</span>
          </div>
        ))}
      </div>
    </div>
  );
}
