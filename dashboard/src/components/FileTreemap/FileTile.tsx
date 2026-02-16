import { useMemo, useCallback } from 'react';
import type { FileEntry } from '../../types';

export function FileTile({ file, selected, onSelect, onHover }: {
  file: FileEntry;
  selected?: boolean;
  onSelect?: (file: FileEntry) => void;
  onHover?: (file: FileEntry | null, x: number, y: number) => void;
}) {
  const { style, className } = useMemo(() => computeTileStyle(file), [file.state, file.processing, file.lang.color]);
  const cls = selected ? `${className} selected` : className;

  const handleEnter = useCallback((e: React.MouseEvent) => {
    onHover?.(file, e.clientX, e.clientY);
  }, [file, onHover]);

  const handleMove = useCallback((e: React.MouseEvent) => {
    onHover?.(file, e.clientX, e.clientY);
  }, [file, onHover]);

  const handleLeave = useCallback(() => {
    onHover?.(null, 0, 0);
  }, [onHover]);

  return (
    <div
      className={cls}
      style={style}
      onClick={onSelect ? (e) => { e.stopPropagation(); onSelect(file); } : undefined}
      onMouseEnter={handleEnter}
      onMouseMove={handleMove}
      onMouseLeave={handleLeave}
    />
  );
}

function computeTileStyle(file: FileEntry): { style: React.CSSProperties; className: string } {
  const color = file.lang.color;
  const base = 'ftile';
  const proc = file.processing ? ' processing' : '';

  switch (file.state) {
    case 'hidden':
      return { className: base, style: { filter: 'blur(3px)' } };

    case 'discovered':
      return {
        className: `${base} discovered`,
        style: { background: 'rgba(255,255,255,.07)', borderColor: 'rgba(255,255,255,.05)', filter: 'blur(2px)' },
      };

    case 'classified':
      return {
        className: `${base} classified${proc}`,
        style: { background: 'rgba(255,255,255,.04)', borderColor: color, filter: 'blur(1.2px)' },
      };

    case 'parsed':
      return {
        className: `${base} parsed${proc}`,
        style: { background: color, borderColor: color, opacity: 0.45, filter: 'blur(0.5px)' },
      };

    case 'struct_embedded':
      return {
        className: `${base} searchable${proc}`,
        style: { background: color, borderColor: color, opacity: 0.75, boxShadow: `0 0 3px ${color}40` },
      };

    case 'full_embedded':
      return {
        className: `${base} full-embedded`,
        style: { background: color, borderColor: color, opacity: 1, boxShadow: `0 0 4px ${color}50` },
      };

    case 'failed':
      return {
        className: `${base} failed`,
        style: { background: 'var(--red)', borderColor: 'var(--red)', opacity: 0.8 },
      };
  }
}
