import { useState } from 'react';
import type { OperationSnapshot, OperationState } from '../../types';
import './OperationTracker.css';

export interface OperationTrackerProps {
  operations: OperationSnapshot[];
  now: number;
}

const STATE_CONFIG: Record<OperationState, { label: string; className: string }> = {
  running: { label: 'Running', className: 'op-state running' },
  completed: { label: 'Completed', className: 'op-state completed' },
  completed_with_failures: { label: 'Completed (failures)', className: 'op-state with-failures' },
  cancelled: { label: 'Cancelled', className: 'op-state cancelled' },
};

const KIND_ICONS: Record<string, string> = {
  startup: '\u25B6',   // ▶
  reindex: '\u21BB',   // ↻
  import: '\u2193',    // ↓
};

export function OperationTracker({ operations, now }: OperationTrackerProps) {
  const active = operations.filter((o) => o.state === 'running');
  const completed = operations.filter((o) => o.state !== 'running');

  return (
    <div className="operation-tracker">
      <div className="ot-header">
        <span className="ot-title">Operations</span>
        <div className="ot-counts">
          {active.length > 0 && <span className="ot-active-count">{active.length} active</span>}
          <span className="ot-total-count">{operations.length} total</span>
        </div>
      </div>

      {operations.length === 0 && (
        <div className="ot-empty">No operations</div>
      )}

      <div className="ot-list">
        {active.map((op) => (
          <OperationRow key={op.id} op={op} now={now} />
        ))}
        {completed.map((op) => (
          <OperationRow key={op.id} op={op} now={now} />
        ))}
      </div>
    </div>
  );
}

function OperationRow({ op, now }: { op: OperationSnapshot; now: number }) {
  const [expanded, setExpanded] = useState(op.state === 'running');
  const config = STATE_CONFIG[op.state];
  const elapsed = formatDuration((op.completedAt ?? now) - op.createdAt);
  const icon = KIND_ICONS[op.kind] ?? '\u25CF';

  return (
    <div className={`ot-row ${op.state === 'running' ? 'active' : ''}`}>
      <button className="ot-row-header" onClick={() => setExpanded(!expanded)}>
        <div className="ot-row-left">
          <span className="ot-kind-icon">{icon}</span>
          <div className="ot-row-info">
            <span className="ot-description">{op.description}</span>
            <span className={config.className}>{config.label}</span>
          </div>
        </div>
        <span className="ot-elapsed">{elapsed}</span>
      </button>

      {/* Progress bar */}
      {op.totalFiles > 0 && (
        <div className="ot-progress">
          <div className="ot-progress-bar">
            <div
              className="ot-progress-indexed"
              style={{ width: `${(op.indexedCount / op.totalFiles) * 100}%` }}
            />
            <div
              className="ot-progress-embedded"
              style={{ width: `${(op.embeddedCount / op.totalFiles) * 100}%` }}
            />
            {op.failedCount > 0 && (
              <div
                className="ot-progress-failed"
                style={{ width: `${(op.failedCount / op.totalFiles) * 100}%` }}
              />
            )}
          </div>
          <div className="ot-progress-labels">
            <span>{op.readyPercent}% ready</span>
            <span>{op.indexedCount}/{op.totalFiles} indexed</span>
            <span>{op.embeddedCount} embedded</span>
            {op.failedCount > 0 && <span className="ot-failed-label">{op.failedCount} failed</span>}
          </div>
        </div>
      )}

      {/* Expanded: milestones + recent log */}
      {expanded && (
        <div className="ot-detail">
          {op.milestones.length > 0 && (
            <div className="ot-milestones">
              {op.milestones.map((m, i) => (
                <div key={i} className="ot-milestone">
                  <span className="ot-ms-dot" />
                  <span className="ot-ms-name">{m.name}</span>
                  {m.detail && <span className="ot-ms-detail">{m.detail}</span>}
                  <span className="ot-ms-time">{formatTime(m.timestamp, op.createdAt)}</span>
                </div>
              ))}
            </div>
          )}

          {op.recentLog.length > 0 && (
            <div className="ot-log">
              {op.recentLog.slice(0, 8).map((entry, i) => (
                <div key={i} className="ot-log-entry">
                  <span className={`ot-log-type ${entry.type.includes('failed') ? 'failed' : ''}`}>
                    {entry.type}
                  </span>
                  {entry.uri && <span className="ot-log-uri">{truncateUri(entry.uri)}</span>}
                  {entry.message && <span className="ot-log-msg">{entry.message}</span>}
                </div>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

function formatDuration(ms: number): string {
  const sec = Math.floor(ms / 1000);
  if (sec < 60) return `${sec}s`;
  const min = Math.floor(sec / 60);
  if (min < 60) return `${min}m ${sec % 60}s`;
  const hr = Math.floor(min / 60);
  return `${hr}h ${min % 60}m`;
}

function formatTime(timestamp: number, base: number): string {
  const delta = Math.floor((timestamp - base) / 1000);
  return `+${delta}s`;
}

function truncateUri(uri: string): string {
  if (uri.length <= 50) return uri;
  return '...' + uri.slice(-47);
}
