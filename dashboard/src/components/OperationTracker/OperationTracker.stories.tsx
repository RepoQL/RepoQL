import type { Meta, StoryObj } from '@storybook/react';
import { OperationTracker } from './OperationTracker';
import type { OperationSnapshot } from '../../types';

const meta: Meta<typeof OperationTracker> = {
  title: 'Components/OperationTracker',
  component: OperationTracker,
  decorators: [(Story) => <div style={{ maxWidth: 560 }}><Story /></div>],
};
export default meta;

type Story = StoryObj<typeof OperationTracker>;

const now = Date.now();

const operations: OperationSnapshot[] = [
  {
    id: 'op_001',
    kind: 'reindex',
    description: 'reindex: file:///src/**/*.cs',
    state: 'running',
    createdAt: now - 18_000,
    completedAt: null,
    totalFiles: 347,
    indexedCount: 280,
    embeddedCount: 142,
    failedCount: 1,
    readyPercent: 41,
    milestones: [
      { name: 'scan_complete', detail: '347 files, 0.8s', timestamp: now - 16_500 },
      { name: 'hot_path_complete', detail: '280 indexed, 12.4s', timestamp: now - 5_600 },
    ],
    recentLog: [
      { type: 'file_indexed', uri: 'file:///src/core/Pipeline.cs', timestamp: now - 200 },
      { type: 'file_embedded', uri: 'file:///src/core/Engine.cs', timestamp: now - 500 },
      { type: 'file_failed', uri: 'file:///src/legacy/Broken.cs', message: 'Parse error at line 47', timestamp: now - 3_000 },
      { type: 'file_indexed', uri: 'file:///src/auth/JwtHandler.cs', timestamp: now - 4_000 },
    ],
  },
  {
    id: 'op_002',
    kind: 'import',
    description: 'import: github://anthropics/claude-code',
    state: 'running',
    createdAt: now - 45_000,
    completedAt: null,
    totalFiles: 1247,
    indexedCount: 890,
    embeddedCount: 620,
    failedCount: 3,
    readyPercent: 50,
    milestones: [
      { name: 'clone_complete', detail: '4.2s', timestamp: now - 40_000 },
      { name: 'scan_complete', detail: '1247 files, 2.1s', timestamp: now - 38_000 },
      { name: 'hot_path_complete', detail: '890 indexed', timestamp: now - 12_000 },
    ],
    recentLog: [
      { type: 'file_embedded', uri: 'file:///src/index.ts', timestamp: now - 100 },
      { type: 'file_embedded', uri: 'file:///src/cli.ts', timestamp: now - 300 },
      { type: 'embedding_failed', uri: 'file:///tests/large_fixture.json', message: 'Timeout', timestamp: now - 8_000 },
    ],
  },
  {
    id: 'op_003',
    kind: 'startup',
    description: 'startup: C:\\Source\\RepoQL',
    state: 'completed',
    createdAt: now - 300_000,
    completedAt: now - 262_000,
    totalFiles: 847,
    indexedCount: 847,
    embeddedCount: 845,
    failedCount: 2,
    readyPercent: 100,
    milestones: [
      { name: 'scan_complete', detail: '847 files, 1.1s', timestamp: now - 298_000 },
      { name: 'hot_path_complete', detail: '847 indexed, 14.2s', timestamp: now - 285_000 },
      { name: 'ready', detail: '845 embedded, 38.1s', timestamp: now - 262_000 },
    ],
    recentLog: [],
  },
  {
    id: 'op_004',
    kind: 'reindex',
    description: 'reindex: file:///docs/**',
    state: 'completed_with_failures',
    createdAt: now - 180_000,
    completedAt: now - 170_000,
    totalFiles: 42,
    indexedCount: 40,
    embeddedCount: 38,
    failedCount: 2,
    readyPercent: 95,
    milestones: [
      { name: 'scan_complete', detail: '42 files', timestamp: now - 179_000 },
      { name: 'ready', timestamp: now - 170_000 },
    ],
    recentLog: [
      { type: 'file_failed', uri: 'file:///docs/binary.pdf', message: 'Unsupported format', timestamp: now - 175_000 },
      { type: 'file_failed', uri: 'file:///docs/diagram.svg', message: 'Parser timeout', timestamp: now - 174_000 },
    ],
  },
];

export const Mixed: Story = {
  args: { operations, now },
};

export const ActiveOnly: Story = {
  args: { operations: operations.filter((o) => o.state === 'running'), now },
};

export const CompletedOnly: Story = {
  args: { operations: operations.filter((o) => o.state !== 'running'), now },
};

export const Empty: Story = {
  args: { operations: [], now },
};
