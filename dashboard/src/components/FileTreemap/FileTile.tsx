import { createMemo } from 'solid-js';
import type { JSX } from 'solid-js';
import type { FileEntry } from '../../types';

export interface FileTileProps {
  file: FileEntry;
  selected?: boolean;
  onSelect?: (file: FileEntry) => void;
  onHover?: (file: FileEntry | null, x: number, y: number) => void;
}

export function FileTile(props: FileTileProps) {
  const tile = createMemo(() => computeTileStyle(props.file));
  const cls = createMemo(() => (props.selected ? `${tile().class} selected` : tile().class));

  const handleEnter = (event: MouseEvent) => {
    props.onHover?.(props.file, event.clientX, event.clientY);
  };

  const handleMove = (event: MouseEvent) => {
    props.onHover?.(props.file, event.clientX, event.clientY);
  };

  const handleLeave = () => {
    props.onHover?.(null, 0, 0);
  };

  return (
    <div
      class={cls()}
      style={tile().style}
      onClick={props.onSelect ? (event) => { event.stopPropagation(); props.onSelect?.(props.file); } : undefined}
      onMouseEnter={handleEnter}
      onMouseMove={handleMove}
      onMouseLeave={handleLeave}
    />
  );
}

/** Map token count to tile size (px) via log scale. 0→4, ~100→5, ~1k→7, ~5k→9, ~20k→11, ~50k+→13 */
function tileSize(tokens: number | null | undefined): number {
  if (!tokens || tokens <= 0) return 4;
  const s = Math.min(13, 4 + Math.log2(tokens / 50));
  return Math.max(4, Math.round(s));
}

function computeTileStyle(file: FileEntry): { style: JSX.CSSProperties; class: string } {
  const color = file.lang.color;
  const base = 'ftile';
  const proc = file.processing ? ' processing' : '';
  const size = `${tileSize(file.tokens)}px`;

  switch (file.state) {
    case 'hidden':
      return { class: base, style: { width: size, height: size, opacity: 0.15 } };

    case 'discovered':
      return {
        class: `${base} discovered`,
        style: { width: size, height: size, background: 'rgba(255,255,255,.07)', opacity: 0.3 },
      };

    case 'classified':
      return {
        class: `${base} classified${proc}`,
        style: { width: size, height: size, background: 'rgba(255,255,255,.04)', 'border-color': color, opacity: 0.5 },
      };

    case 'parsed':
      return {
        class: `${base} parsed${proc}`,
        style: { width: size, height: size, background: color, 'border-color': color, opacity: 0.45 },
      };

    case 'struct_embedded':
      return {
        class: `${base} searchable${proc}`,
        style: { width: size, height: size, background: color, 'border-color': color, opacity: 0.75 },
      };

    case 'full_embedded':
      return {
        class: `${base} full-embedded`,
        style: { width: size, height: size, background: color, 'border-color': color, opacity: 1 },
      };

    case 'failed':
      return {
        class: `${base} failed`,
        style: { width: size, height: size, background: 'var(--red)', 'border-color': 'var(--red)', opacity: 0.8 },
      };

    default:
      return { class: base, style: { width: size, height: size } };
  }
}
