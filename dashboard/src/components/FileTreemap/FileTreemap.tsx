import { createMemo, createSelector, createSignal, Index, onCleanup, Show } from 'solid-js';
import type { FileEntry, SourceSection } from '../../types';
import { FileTile } from './FileTile';
import { FileTooltip } from './FileTooltip';
import { FileDetailPanel } from './FileDetailPanel';
import { SourceCard } from './SourceCard';
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

interface HoverState {
  file: FileEntry;
  x: number;
  y: number;
}

export function FileTreemap(props: FileTreemapProps) {
  const [selectedSection, setSelectedSection] = createSignal(0);
  const [selected, setSelected] = createSignal<FileEntry | null>(null);
  const isSelected = createSelector<string | null, string>(() => selected()?.path ?? null);
  const [hover, setHover] = createSignal<HoverState | null>(null);
  let hoverTimeout: ReturnType<typeof setTimeout> | null = null;

  onCleanup(() => {
    if (hoverTimeout) {
      clearTimeout(hoverTimeout);
      hoverTimeout = null;
    }
  });

  const activeSection = createMemo(() => {
    return props.sections[selectedSection()] ?? props.sections[0] ?? null;
  });

  const sectionStats = createMemo(() => {
    const section = activeSection();
    if (!section) return props.stats;
    const allFiles = section.groups.flatMap((g) => g.files);
    const byState: Record<string, number> = {};
    for (const f of allFiles) {
      byState[f.state] = (byState[f.state] ?? 0) + 1;
    }
    const total = allFiles.length;
    const classified = total - (byState['hidden'] ?? 0) - (byState['discovered'] ?? 0);
    const parsed = (byState['parsed'] ?? 0) + (byState['struct_embedded'] ?? 0)
      + (byState['full_embedded'] ?? 0) + (byState['failed'] ?? 0);
    const searchable = (byState['struct_embedded'] ?? 0) + (byState['full_embedded'] ?? 0);
    return { total, classified, parsed, searchable };
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

  const handleTabClick = (i: number, e: MouseEvent) => {
    e.stopPropagation();
    setSelectedSection(i);
    setSelected(null);
    setHover(null);
  };

  return (
    <div class="treemap-area" onClick={handleBackdrop}>
      <div class="treemap-header">
        <div class="treemap-tabs">
          <Index each={props.sections}>
            {(section, i) => (
              <SourceCard
                section={section()}
                active={i === selectedSection()}
                onClick={(e) => handleTabClick(i, e)}
              />
            )}
          </Index>
        </div>
        <div class="treemap-stats">
          <div class="tm-stat"><span>{sectionStats().total}</span> files</div>
          <div class="tm-stat"><span>{sectionStats().classified}</span> classified</div>
          <div class="tm-stat"><span>{sectionStats().parsed}</span> parsed</div>
          <div class="tm-stat"><span>{sectionStats().searchable}</span> ready</div>
        </div>
      </div>

      {/* Active section content */}
      <Show when={activeSection()}>
        {(section) => {
          const totalTokens = createMemo(() => {
            const allFiles = section().groups.flatMap((g) => g.files);
            return sumTokens(allFiles) || 1;
          });
          return (
            <div class="treemap-body">
              <SourceBlock
                section={section()}
                totalTokens={totalTokens()}
                isSelected={isSelected}
                onSelect={handleSelect}
                onHover={handleHover}
              />
            </div>
          );
        }}
      </Show>

      {/* Tooltip on hover (hidden when flyout is open) */}
      <Show when={!selected() ? hover() : null}>
        {(hoverState) => (
          <FileTooltip file={hoverState().file} x={hoverState().x} y={hoverState().y} />
        )}
      </Show>

      {/* Flyout on click */}
      <Show when={selected()}>
        {(file) => (
          <div class="treemap-flyout" onClick={(e) => e.stopPropagation()}>
            <FileDetailPanel file={file()} onClose={() => setSelected(null)} />
          </div>
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
  isSelected: (key: string) => boolean;
  onSelect: (file: FileEntry) => void;
  onHover: (file: FileEntry | null, x: number, y: number) => void;
}) {
  const normalGroups = () => props.section.groups.filter((g) => g.label !== 'Failed');
  const failedGroups = () => props.section.groups.filter((g) => g.label === 'Failed');

  return (
    <div class="treemap-source">
      <div class="treemap-pipeline">
        <Index each={normalGroups()}>
          {(group) => (
            <TileGroup
              group={group()}
              totalTokens={props.totalTokens}
              isSelected={props.isSelected}
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
              isSelected={props.isSelected}
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
  isSelected: (key: string) => boolean;
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
              selected={props.isSelected(file().path)}
              onSelect={props.onSelect}
              onHover={props.onHover}
            />
          )}
        </Index>
      </div>
    </div>
  );
}
