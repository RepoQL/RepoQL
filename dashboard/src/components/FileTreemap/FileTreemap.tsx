import { createMemo, createSelector, createSignal, Index, onCleanup, onMount, Show } from 'solid-js';
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
    pending: number;
    parsed: number;
    ready: number;
    indexed: number;
    failed: number;
  };
}

interface HoverState {
  file: FileEntry;
  x: number;
  y: number;
}

interface FolderNode {
  name: string;
  path: string;
  files: FileEntry[];
  children: FolderNode[];
  totalFiles: number;
  totalTokens: number;
  ownFileWeight: number;
  visualWeight: number;
}

const FULLY_INDEXED_LABEL = 'Fully Indexed';
const MAX_FULLY_INDEXED_DEPTH = 2;
const D3_SQUARIFY_RATIO = (1 + Math.sqrt(5)) / 2;

interface Rect {
  x: number;
  y: number;
  width: number;
  height: number;
}

interface FolderLayout {
  node: FolderNode;
  rect: Rect;
  compact: boolean;
  childHeight: number;
  fileHeight: number;
  visibleFiles: FileEntry[];
  children: FolderLayout[];
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
    const hidden = byState['hidden'] ?? 0;
    const total = allFiles.length - hidden;
    const pending = (byState['discovered'] ?? 0) + (byState['classified'] ?? 0);
    const parsed = byState['parsed'] ?? 0;
    const ready = byState['struct_embedded'] ?? 0;
    const indexed = byState['full_embedded'] ?? 0;
    const failed = byState['failed'] ?? 0;
    return { total, pending, parsed, ready, indexed, failed };
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
          <div class="tm-stat"><span>{sectionStats().pending}</span> pending</div>
          <div class="tm-stat"><span>{sectionStats().parsed}</span> parsed</div>
          <div class="tm-stat"><span>{sectionStats().ready}</span> ready</div>
          <div class="tm-stat"><span>{sectionStats().indexed}</span> indexed</div>
          <Show when={sectionStats().failed > 0}>
            <div class="tm-stat failed"><span>{sectionStats().failed}</span> failed</div>
          </Show>
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
              sourcePrefix={props.section.prefix}
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
              sourcePrefix={props.section.prefix}
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
  sourcePrefix: string;
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
      <Show
        when={props.group.label === FULLY_INDEXED_LABEL}
        fallback={(
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
        )}
      >
        <DirectoryBlocks
          files={props.group.files}
          sourcePrefix={props.sourcePrefix}
          totalTokens={groupTokens()}
          isSelected={props.isSelected}
          onSelect={props.onSelect}
          onHover={props.onHover}
        />
      </Show>
    </div>
  );
}

function DirectoryBlocks(props: {
  files: FileEntry[];
  sourcePrefix: string;
  totalTokens: number;
  isSelected: (key: string) => boolean;
  onSelect: (file: FileEntry) => void;
  onHover: (file: FileEntry | null, x: number, y: number) => void;
}) {
  const nodes = createMemo(() => buildFolderTree(props.files, props.sourcePrefix));
  const [width, setWidth] = createSignal(0);
  let host: HTMLDivElement | undefined;

  onMount(() => {
    if (!host) {
      return;
    }

    const observer = new ResizeObserver((entries) => {
      const nextWidth = Math.floor(entries[0]?.contentRect.width ?? 0);
      setWidth((current) => current === nextWidth ? current : nextWidth);
    });

    observer.observe(host);
    onCleanup(() => observer.disconnect());
  });

  const layout = createMemo(() => createFolderTreemap(nodes(), width()));

  return (
    <div ref={host} class="dir-tree-host">
      <div class="dir-tree" style={{ height: `${layout().height}px` }}>
        <Index each={layout().layouts}>
          {(node) => (
            <FolderBlock
              layout={node()}
              isSelected={props.isSelected}
              onSelect={props.onSelect}
              onHover={props.onHover}
            />
          )}
        </Index>
      </div>
    </div>
  );
}

function FolderBlock(props: {
  layout: FolderLayout;
  isSelected: (key: string) => boolean;
  onSelect: (file: FileEntry) => void;
  onHover: (file: FileEntry | null, x: number, y: number) => void;
}) {
  const blockStyle = createMemo(() => toRectStyle(props.layout.rect));

  return (
    <div class={`folder-block${props.layout.compact ? ' compact' : ''}`} style={blockStyle()}>
      <div class="folder-label">
        <span class="folder-name">{props.layout.node.name}</span>
        <span class="folder-meta">{props.layout.node.totalFiles}</span>
      </div>

      <Show when={props.layout.children.length > 0}>
        <div class="folder-children" style={{ height: `${props.layout.childHeight}px` }}>
          <Index each={props.layout.children}>
            {(child) => (
              <FolderBlock
                layout={child()}
                isSelected={props.isSelected}
                onSelect={props.onSelect}
                onHover={props.onHover}
              />
            )}
          </Index>
        </div>
      </Show>

      <Show when={props.layout.visibleFiles.length > 0 && props.layout.fileHeight > 0}>
        <div class={`folder-files${props.layout.compact ? ' compact' : ''}`} style={{ height: `${props.layout.fileHeight}px` }}>
          <Index each={props.layout.visibleFiles}>
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
      </Show>
    </div>
  );
}

function buildFolderTree(files: FileEntry[], sourcePrefix: string): FolderNode[] {
  const root = createMutableFolderNode('', '');

  for (const file of files) {
    const relative = relativePath(file.path, sourcePrefix);
    const segments = relative.split('/').filter(Boolean);
    const fileName = segments.pop();
    if (!fileName) {
      root.files.push(file);
      continue;
    }

    let current = root;
    let currentPath = '';
    for (const segment of segments.slice(0, MAX_FULLY_INDEXED_DEPTH)) {
      currentPath = currentPath ? `${currentPath}/${segment}` : segment;
      let child = current.children.get(segment);
      if (!child) {
        child = createMutableFolderNode(segment, currentPath);
        current.children.set(segment, child);
      }

      current = child;
    }

    current.files.push(file);
  }

  const materialized = materializeFolderNodes(root.children);
  if (root.files.length > 0) {
    materialized.unshift(createLeafRootNode(root.files));
  }

  return materialized;
}

function relativePath(path: string, sourcePrefix: string): string {
  const normalizedPath = path.replace(/\\/g, '/');
  const normalizedPrefix = sourcePrefix.replace(/\\/g, '/').replace(/\/$/, '');

  if (normalizedPrefix && normalizedPath.startsWith(`${normalizedPrefix}/`)) {
    return normalizedPath.slice(normalizedPrefix.length + 1);
  }

  return normalizedPath;
}

function createMutableFolderNode(name: string, path: string) {
  return {
    name,
    path,
    files: [] as FileEntry[],
    children: new Map<string, ReturnType<typeof createMutableFolderNode>>(),
  };
}

function materializeFolderNodes(nodes: Map<string, ReturnType<typeof createMutableFolderNode>>): FolderNode[] {
  return Array.from(nodes.values())
    .map(materializeFolderNode)
    .sort((a, b) => b.totalTokens - a.totalTokens || b.totalFiles - a.totalFiles || a.name.localeCompare(b.name));
}

function materializeFolderNode(node: ReturnType<typeof createMutableFolderNode>): FolderNode {
  const children = materializeFolderNodes(node.children);
  const totalFiles = node.files.length + children.reduce((sum, child) => sum + child.totalFiles, 0);
  const totalTokens = sumTokens(node.files) + children.reduce((sum, child) => sum + child.totalTokens, 0);
  const ownFileWeight = sumFileWeights(node.files);
  const childWeight = children.reduce((sum, child) => sum + child.visualWeight, 0);
  const visualWeight = Math.max(320, ownFileWeight + childWeight + folderOverheadWeight(node.name, node.files.length, children.length));

  return {
    name: node.name,
    path: node.path,
    files: [...node.files].sort((a, b) => (b.tokens ?? 0) - (a.tokens ?? 0) || a.path.localeCompare(b.path)),
    children,
    totalFiles,
    totalTokens,
    ownFileWeight,
    visualWeight,
  };
}

function createLeafRootNode(files: FileEntry[]): FolderNode {
  const sorted = [...files].sort((a, b) => (b.tokens ?? 0) - (a.tokens ?? 0) || a.path.localeCompare(b.path));
  const ownFileWeight = sumFileWeights(sorted);
  return {
    name: '(root)',
    path: '',
    files: sorted,
    children: [],
    totalFiles: sorted.length,
    totalTokens: sumTokens(sorted),
    ownFileWeight,
    visualWeight: Math.max(240, ownFileWeight + folderOverheadWeight('(root)', sorted.length, 0)),
  };
}

function createFolderTreemap(nodes: FolderNode[], width: number): { height: number; layouts: FolderLayout[] } {
  if (nodes.length === 0 || width <= 0) {
    return { height: 0, layouts: [] };
  }

  const availableWidth = Math.max(220, width);
  const targetAspect = nodes.length > 8 ? 0.78 : 0.68;
  const height = clamp(Math.round(availableWidth * targetAspect), 320, 720);
  const layouts = layoutFolderNodes(nodes, { x: 0, y: 0, width: availableWidth, height }, 0);
  return { height, layouts };
}

function layoutFolderNodes(nodes: FolderNode[], rect: Rect, depth: number): FolderLayout[] {
  return squarify(nodes, rect)
    .map(({ node, rect: nodeRect }) => layoutFolderNode(node, nodeRect, depth))
    .filter((layout) => layout.rect.width >= 10 && layout.rect.height >= 10);
}

function layoutFolderNode(node: FolderNode, rect: Rect, depth: number): FolderLayout {
  const compact = rect.width < 92 || rect.height < 66;
  const pad = compact ? 3 : 4;
  const labelHeight = compact ? 11 : depth === 0 ? 15 : 13;
  const gap = 3;
  const innerWidth = Math.max(0, rect.width - pad * 2);
  const contentHeight = Math.max(0, rect.height - pad * 2 - labelHeight - gap);
  const displayChildren = compressChildrenForDisplay(node, depth);
  const childWeight = displayChildren.reduce((sum, child) => sum + child.visualWeight, 0);

  let childHeight = 0;
  let fileHeight = 0;

  if (displayChildren.length > 0 && depth < MAX_FULLY_INDEXED_DEPTH - 1 && contentHeight > 0) {
    if (childWeight > 0 && node.ownFileWeight > 0) {
      const ratio = node.ownFileWeight / Math.max(node.visualWeight, 1);
      fileHeight = clamp(Math.round(contentHeight * ratio), compact ? 14 : 18, Math.round(contentHeight * 0.42));
      childHeight = contentHeight - fileHeight - gap;
      if (childHeight < 26) {
        childHeight = contentHeight;
        fileHeight = 0;
      }
    } else if (childWeight > 0) {
      childHeight = contentHeight;
    } else {
      fileHeight = contentHeight;
    }
  } else {
    fileHeight = contentHeight;
  }

  const children = childHeight > 0
    ? layoutFolderNodes(displayChildren, { x: 0, y: 0, width: innerWidth, height: childHeight }, depth + 1)
    : [];

  const visibleFiles = fileHeight > 0 ? node.files : [];

  return {
    node,
    rect,
    compact,
    childHeight: children.length > 0 ? childHeight : 0,
    fileHeight: visibleFiles.length > 0 ? fileHeight : 0,
    visibleFiles,
    children,
  };
}

function squarify(nodes: FolderNode[], rect: Rect): Array<{ node: FolderNode; rect: Rect }> {
  if (nodes.length === 0 || rect.width <= 0 || rect.height <= 0) {
    return [];
  }

  const totalWeight = nodes.reduce((sum, node) => sum + node.visualWeight, 0);
  if (totalWeight <= 0) {
    return [];
  }

  const layouts: Array<{ node: FolderNode; rect: Rect }> = [];
  const sorted = [...nodes].sort((a, b) => b.visualWeight - a.visualWeight);
  let i0 = 0;
  let i1 = 0;
  let x0 = rect.x;
  let y0 = rect.y;
  let x1 = rect.x + rect.width;
  let y1 = rect.y + rect.height;
  let remainingValue = totalWeight;

  while (i0 < sorted.length) {
    let dx = x1 - x0;
    let dy = y1 - y0;
    let sumValue = sorted[i1++]?.visualWeight ?? 0;
    if (sumValue <= 0) {
      continue;
    }

    let minValue = sumValue;
    let maxValue = sumValue;
    const alpha = Math.max(dy / Math.max(dx, 1), dx / Math.max(dy, 1)) / (remainingValue * D3_SQUARIFY_RATIO);
    let beta = sumValue * sumValue * alpha;
    let minRatio = Math.max(maxValue / beta, beta / minValue);

    for (; i1 < sorted.length; ++i1) {
      const nodeValue = sorted[i1]!.visualWeight;
      sumValue += nodeValue;
      if (nodeValue < minValue) minValue = nodeValue;
      if (nodeValue > maxValue) maxValue = nodeValue;
      beta = sumValue * sumValue * alpha;
      const newRatio = Math.max(maxValue / beta, beta / minValue);
      if (newRatio > minRatio) {
        sumValue -= nodeValue;
        break;
      }
      minRatio = newRatio;
    }

    const row = sorted.slice(i0, i1);
    const dice = dx < dy;
    if (dice) {
      const yNext = remainingValue ? y0 + dy * sumValue / remainingValue : y1;
      let rowX = x0;
      for (const node of row) {
        const rowWidth = sumValue ? (x1 - x0) * node.visualWeight / sumValue : 0;
        layouts.push({
          node,
          rect: { x: rowX, y: y0, width: rowWidth, height: yNext - y0 },
        });
        rowX += rowWidth;
      }
      y0 = yNext;
    } else {
      const xNext = remainingValue ? x0 + dx * sumValue / remainingValue : x1;
      let rowY = y0;
      for (const node of row) {
        const rowHeight = sumValue ? (y1 - y0) * node.visualWeight / sumValue : 0;
        layouts.push({
          node,
          rect: { x: x0, y: rowY, width: xNext - x0, height: rowHeight },
        });
        rowY += rowHeight;
      }
      x0 = xNext;
    }

    remainingValue -= sumValue;
    i0 = i1;
  }

  return layouts
    .map((entry) => ({ node: entry.node, rect: normalizeRect(entry.rect) }))
    .filter((entry) => entry.rect.width > 0 && entry.rect.height > 0);
}

function normalizeRect(rect: Rect): Rect {
  return {
    x: Math.round(rect.x),
    y: Math.round(rect.y),
    width: Math.max(0, Math.round(rect.width)),
    height: Math.max(0, Math.round(rect.height)),
  };
}

function toRectStyle(rect: Rect): Record<string, string> {
  return {
    left: `${rect.x}px`,
    top: `${rect.y}px`,
    width: `${rect.width}px`,
    height: `${rect.height}px`,
  };
}

function sumFileWeights(files: FileEntry[]): number {
  return files.reduce((sum, file) => sum + fileVisualWeight(file), 0);
}

function fileVisualWeight(file: FileEntry): number {
  const size = tileSize(file.tokens) + 2;
  return size * size + 10;
}

function tileSize(tokens: number | null | undefined): number {
  if (!tokens || tokens <= 0) return 4;
  const s = Math.min(13, 4 + Math.log2(tokens / 50));
  return Math.max(4, Math.round(s));
}

function folderOverheadWeight(name: string, fileCount: number, childCount: number): number {
  return 180 + Math.min(6, fileCount + childCount) * 18 + Math.min(20, name.length) * 2;
}

function clamp(value: number, min: number, max: number): number {
  return Math.max(min, Math.min(max, value));
}

function compressChildrenForDisplay(node: FolderNode, depth: number): FolderNode[] {
  if (depth > 0 || node.children.length <= 12) {
    return node.children;
  }

  const sorted = [...node.children].sort((a, b) => b.visualWeight - a.visualWeight);
  const keep: FolderNode[] = [];
  const collapsed: FolderNode[] = [];
  const parentWeight = Math.max(1, node.visualWeight);

  for (const child of sorted) {
    const share = child.visualWeight / parentWeight;
    if (keep.length < 10 || share >= 0.015) {
      keep.push(child);
    } else {
      collapsed.push(child);
    }
  }

  const collapsedWeight = collapsed.reduce((sum, child) => sum + child.visualWeight, 0);
  const collapsedShare = collapsedWeight / parentWeight;

  if (collapsed.length < 4 || collapsedShare < 0.04) {
    return keep;
  }

  return [...keep, createCollapsedFolderNode(collapsed, node.path)];
}

function createCollapsedFolderNode(nodes: FolderNode[], parentPath: string): FolderNode {
  const files = nodes.flatMap(collectFiles)
    .sort((a, b) => (b.tokens ?? 0) - (a.tokens ?? 0) || a.path.localeCompare(b.path));
  const totalFiles = files.length;
  const totalTokens = sumTokens(files);
  const ownFileWeight = sumFileWeights(files);

  return {
    name: 'other',
    path: parentPath ? `${parentPath}/(other)` : '(other)',
    files,
    children: [],
    totalFiles,
    totalTokens,
    ownFileWeight,
    visualWeight: Math.max(260, ownFileWeight + folderOverheadWeight('other', totalFiles, 0)),
  };
}

function collectFiles(node: FolderNode): FileEntry[] {
  return [
    ...node.files,
    ...node.children.flatMap(collectFiles),
  ];
}
