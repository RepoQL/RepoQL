import type { Meta, StoryObj } from '@storybook/react';
import { ConnectionStatus } from './ConnectionStatus';

const meta: Meta<typeof ConnectionStatus> = {
  title: 'Components/ConnectionStatus',
  component: ConnectionStatus,
  decorators: [(Story) => <div style={{ padding: 20, background: 'var(--surface)' }}><Story /></div>],
};
export default meta;

type Story = StoryObj<typeof ConnectionStatus>;

export const Connected: Story = {
  args: {
    health: { status: 'connected', latencyMs: 3, uptimeSeconds: 1847, version: 'v0.9.2' },
  },
};

export const Disconnected: Story = {
  args: {
    health: { status: 'disconnected', latencyMs: 0, uptimeSeconds: 0, version: '' },
  },
};

export const Reconnecting: Story = {
  args: {
    health: { status: 'reconnecting', latencyMs: 0, uptimeSeconds: 0, version: 'v0.9.2' },
  },
};

export const HighLatency: Story = {
  args: {
    health: { status: 'connected', latencyMs: 340, uptimeSeconds: 86400, version: 'v0.9.2' },
  },
};
