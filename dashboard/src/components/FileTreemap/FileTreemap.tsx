import { useState, useCallback, useRef } from 'react';
import type { FileEntry, FileGroup } from '../../types';
import { FileTile } from './FileTile';
import { FileTooltip } from './FileTooltip';
import './FileTreemap.css';

export interface FileTreemapProps {
  groups: FileGroup[];
  stats: {
    total: number;
    classified: number;
    parsed: number;
    searchable: number;
  };
}

const STATE_LABELS: Record<string, string> = {
  hidden: 'Pending',
  discovered: 'Discovered',
  classified: 'Classifying',
  parsed: 'Parsed',
  struct_embedded: 'Searchable',
  full_embedded: 'Fully embedded',
  failed: 'Failed',
};

interface HoverState {
  file: FileEntry;
  x: number;
  y: number;
}

export function FileTreemap({ groups, stats }: FileTreemapProps) {
  const [selected, setSelected] = useState<FileEntry | null>(null);
  const [hover, setHover] = useState<HoverState | null>(null);
  const hoverTimeout = useRef<ReturnType<typeof setTimeout> | null>(null);

  const handleSelect = useCallback((file: FileEntry) => {
    setSelected((prev) => (prev?.path === file.path ? null : file));
  }, []);

  const handleBackdrop = useCallback(() => setSelected(null), []);

  const handleHover = useCallback((file: FileEntry | null, x: number, y: number) => {
    if (hoverTimeout.current) {
      clearTimeout(hoverTimeout.current);
      hoverTimeout.current = null;
    }
    if (file) {
      // Offset tooltip from cursor
      setHover({ file, x: x + 12, y: y + 12 });
    } else {
      // Small delay on leave to prevent flicker between tiles
      hoverTimeout.current = setTimeout(() => setHover(null), 50);
    }
  }, []);

  return (
    <div className="treemap-area" onClick={handleBackdrop}>
      <div className="treemap-header">
        <span className="treemap-title">Codebase</span>
        <div className="treemap-stats">
          <div className="tm-stat"><span>{stats.total}</span> files</div>
          <div className="tm-stat"><span>{stats.classified}</span> classified</div>
          <div className="tm-stat"><span>{stats.parsed}</span> parsed</div>
          <div className="tm-stat"><span>{stats.searchable}</span> searchable</div>
        </div>
      </div>

      {selected && (
        <div className="treemap-detail" onClick={(e) => e.stopPropagation()}>
          <span
            className="detail-swatch"
            style={{ background: selected.state === 'failed' ? 'var(--red)' : selected.lang.color }}
          />
          <span className="detail-path">{selected.path}</span>
          <span className="detail-lang">{selected.lang.name}</span>
          <span className={`detail-state ${selected.state}`}>
            {STATE_LABELS[selected.state] ?? selected.state}
          </span>
          <button className="detail-close" onClick={() => setSelected(null)} aria-label="Close">
            &times;
          </button>
        </div>
      )}

      <div className="treemap-body">
        {groups.map((group) => (
          <TileGroup
            key={group.label}
            group={group}
            totalFiles={stats.total || 1}
            selectedPath={selected?.path ?? null}
            onSelect={handleSelect}
            onHover={handleHover}
          />
        ))}
      </div>

      {hover && (
        <FileTooltip file={hover.file} x={hover.x} y={hover.y} />
      )}
    </div>
  );
}

function TileGroup({ group, totalFiles, selectedPath, onSelect, onHover }: {
  group: FileGroup;
  totalFiles: number;
  selectedPath: string | null;
  onSelect: (file: FileEntry) => void;
  onHover: (file: FileEntry | null, x: number, y: number) => void;
}) {
  const width = Math.max(80, Math.sqrt(group.files.length / totalFiles) * 440);

  return (
    <div className="dir-group" style={{ width }}>
      <div className="dir-label">
        {group.label}
        <span className="dir-count">{group.files.length}</span>
      </div>
      <div className="dir-files">
        {group.files.map((f) => (
          <FileTile
            key={f.id}
            file={f}
            selected={f.path === selectedPath}
            onSelect={onSelect}
            onHover={onHover}
          />
        ))}
      </div>
    </div>
  );
}
