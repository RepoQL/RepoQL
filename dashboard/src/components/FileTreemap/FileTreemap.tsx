import { createMemo, createSignal, Index, onCleanup, Show } from 'solid-js';
import type { FileEntry, SourceSection } from '../../types';
import { FileTile } from './FileTile';
import { FileTooltip } from './FileTooltip';
import './FileTreemap.css';

export interface FileTreemapProps {
  sections: SourceSection[];
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
  classified: 'Indexing',
  parsed: 'Parsed',
  struct_embedded: 'Ready',
  full_embedded: 'Fully Indexed',
  failed: 'Failed',
};

interface HoverState {
  file: FileEntry;
  x: number;
  y: number;
}

export function FileTreemap(props: FileTreemapProps) {
  const [selected, setSelected] = createSignal<FileEntry | null>(null);
  const [hover, setHover] = createSignal<HoverState | null>(null);
  let hoverTimeout: ReturnType<typeof setTimeout> | null = null;

  onCleanup(() => {
    if (hoverTimeout) {
      clearTimeout(hoverTimeout);
      hoverTimeout = null;
    }
  });

  const handleSelect = (file: FileEntry) => {
    setSelected((prev) => (prev?.path === file.path ? null : file));
  };

  const handleBackdrop = () => setSelected(null);

  const handleHover = (file: FileEntry | null, x: number, y: number) => {
    if (hoverTimeout) {
      clearTimeout(hoverTimeout);
      hoverTimeout = null;
    }

    if (file) {
      setHover({ file, x: x + 12, y: y + 12 });
    } else {
      hoverTimeout = setTimeout(() => setHover(null), 50);
    }
  };

  return (
    <div class="treemap-area" onClick={handleBackdrop}>
      <div class="treemap-header">
        <span class="treemap-title">Codebase</span>
        <div class="treemap-stats">
          <div class="tm-stat"><span>{props.stats.total}</span> files</div>
          <div class="tm-stat"><span>{props.stats.classified}</span> classified</div>
          <div class="tm-stat"><span>{props.stats.parsed}</span> parsed</div>
          <div class="tm-stat"><span>{props.stats.searchable}</span> ready</div>
        </div>
      </div>

      <Show when={selected()}>
        {(selectedFile) => (
          <div class="treemap-detail" onClick={(event) => event.stopPropagation()}>
            <span
              class="detail-swatch"
              style={{ background: selectedFile().state === 'failed' ? 'var(--red)' : selectedFile().lang.color }}
            />
            <span class="detail-path">{selectedFile().path}</span>
            <span class="detail-lang">{selectedFile().lang.name}</span>
            <span class={`detail-state ${selectedFile().state}`}>
              {STATE_LABELS[selectedFile().state] ?? selectedFile().state}
            </span>
            <button class="detail-close" onClick={() => setSelected(null)} aria-label="Close">
              &times;
            </button>
          </div>
        )}
      </Show>

      <div class="treemap-body">
        <Index each={props.sections}>
          {(section) => {
            const totalTokens = createMemo(() => {
              const allFiles = section().groups.flatMap((g) => g.files);
              return sumTokens(allFiles) || 1;
            });
            return (
              <SourceBlock
                section={section()}
                totalTokens={totalTokens()}
                selectedPath={selected()?.path ?? null}
                onSelect={handleSelect}
                onHover={handleHover}
              />
            );
          }}
        </Index>
      </div>

      <Show when={hover()}>
        {(hoverState) => (
          <FileTooltip file={hoverState().file} x={hoverState().x} y={hoverState().y} />
        )}
      </Show>
    </div>
  );
}

function sumTokens(files: FileEntry[]): number {
  let total = 0;
  for (const f of files) total += f.tokens ?? 0;
  return total;
}

function SourceBlock(props: {
  section: SourceSection;
  totalTokens: number;
  selectedPath: string | null;
  onSelect: (file: FileEntry) => void;
  onHover: (file: FileEntry | null, x: number, y: number) => void;
}) {
  const isImport = () => props.section.prefix !== '';
  const normalGroups = () => props.section.groups.filter((g) => g.label !== 'Failed');
  const failedGroups = () => props.section.groups.filter((g) => g.label === 'Failed');

  return (
    <div class={`treemap-source ${isImport() ? 'treemap-import' : ''}`}>
      <Show when={isImport()}>
        <div class="source-label">
          <span class="source-icon">&#x2197;</span>
          {props.section.label}
          <span class="source-count">{props.section.total}</span>
        </div>
      </Show>
      <div class="treemap-pipeline">
        <Index each={normalGroups()}>
          {(group) => (
            <TileGroup
              group={group()}
              totalTokens={props.totalTokens}
              selectedPath={props.selectedPath}
              onSelect={props.onSelect}
              onHover={props.onHover}
            />
          )}
        </Index>
      </div>
      <Index each={failedGroups()}>
        {(group) => (
          <div class="treemap-failed">
            <TileGroup
              group={group()}
              totalTokens={props.totalTokens}
              selectedPath={props.selectedPath}
              onSelect={props.onSelect}
              onHover={props.onHover}
            />
          </div>
        )}
      </Index>
    </div>
  );
}

function TileGroup(props: {
  group: { label: string; files: FileEntry[] };
  totalTokens: number;
  selectedPath: string | null;
  onSelect: (file: FileEntry) => void;
  onHover: (file: FileEntry | null, x: number, y: number) => void;
}) {
  const groupTokens = () => sumTokens(props.group.files);
  const flexGrow = () => {
    if (props.totalTokens <= 0) return 1;
    return Math.max(1, Math.round((groupTokens() / props.totalTokens) * 100));
  };

  return (
    <div class="dir-group" style={{ 'flex-grow': flexGrow() }}>
      <div class="dir-label">
        {props.group.label}
        <span class="dir-count">{props.group.files.length}</span>
      </div>
      <div class="dir-files">
        <Index each={props.group.files}>
          {(file) => (
            <FileTile
              file={file()}
              selected={file().path === props.selectedPath}
              onSelect={props.onSelect}
              onHover={props.onHover}
            />
          )}
        </Index>
      </div>
    </div>
  );
}
