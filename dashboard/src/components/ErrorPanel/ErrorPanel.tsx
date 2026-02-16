import { useMemo, useState } from 'react';
import type { FileError, ErrorCategory } from '../../types';
import { ERROR_CATEGORY_LABELS } from '../../types';
import './ErrorPanel.css';

export interface ErrorPanelProps {
  errors: FileError[];
}

interface ErrorGroup {
  category: ErrorCategory;
  label: string;
  errors: FileError[];
}

export function ErrorPanel({ errors }: ErrorPanelProps) {
  const groups = useMemo(() => groupErrors(errors), [errors]);

  if (errors.length === 0) {
    return (
      <div className="error-panel">
        <div className="error-panel-header">
          <span className="error-panel-title">Errors</span>
          <span className="error-panel-count zero">0</span>
        </div>
      </div>
    );
  }

  return (
    <div className="error-panel">
      <div className="error-panel-header">
        <span className="error-panel-title">Errors</span>
        <span className="error-panel-count">{errors.length}</span>
      </div>
      <div className="error-panel-body">
        {groups.map((group) => (
          <ErrorGroupSection key={group.category} group={group} />
        ))}
      </div>
    </div>
  );
}

function ErrorGroupSection({ group }: { group: ErrorGroup }) {
  const [expanded, setExpanded] = useState(true);

  return (
    <div className="err-group">
      <button className="err-group-header" onClick={() => setExpanded(!expanded)}>
        <span className="err-group-chevron">{expanded ? '\u25BE' : '\u25B8'}</span>
        <span className="err-group-label">{group.label}</span>
        <span className="err-group-count">{group.errors.length}</span>
      </button>
      {expanded && (
        <div className="err-group-items">
          {group.errors.map((err, i) => (
            <div key={i} className="err-item">
              <div className="err-item-path">
                <span className="err-lang-dot" style={{ background: err.lang.color }} />
                {err.path}
              </div>
              <div className="err-item-msg">{err.message}</div>
              {err.hint && <div className="err-item-hint">{err.hint}</div>}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

function groupErrors(errors: FileError[]): ErrorGroup[] {
  const map = new Map<ErrorCategory, FileError[]>();
  for (const err of errors) {
    let arr = map.get(err.category);
    if (!arr) {
      arr = [];
      map.set(err.category, arr);
    }
    arr.push(err);
  }
  return Array.from(map.entries())
    .map(([category, errs]) => ({
      category,
      label: ERROR_CATEGORY_LABELS[category],
      errors: errs,
    }))
    .sort((a, b) => b.errors.length - a.errors.length);
}
