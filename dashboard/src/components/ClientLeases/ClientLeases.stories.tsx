import type { Meta, StoryObj } from '@storybook/react';
import { ClientLeases } from './ClientLeases';
import type { ClientLease } from '../../types';

const meta: Meta<typeof ClientLeases> = {
  title: 'Components/ClientLeases',
  component: ClientLeases,
  decorators: [(Story) => <div style={{ maxWidth: 500 }}><Story /></div>],
};
export default meta;

type Story = StoryObj<typeof ClientLeases>;

const now = Date.now();

const clients: ClientLease[] = [
  {
    id: 'a1b2c3d4-e5f6-7890-abcd-ef1234567890',
    name: 'claude-code',
    connectedAt: now - 342_000,
    activeRequest: {
      tool: 'explore',
      params: 'intent=Locate keywords="authentication middleware"',
      startedAt: now - 1_200,
      tokenBudget: 2000,
    },
    requestCount: 47,
    totalTokensUsed: 84_230,
  },
  {
    id: 'b2c3d4e5-f6a7-8901-bcde-f12345678901',
    name: 'web-ui',
    connectedAt: now - 120_000,
    activeRequest: {
      tool: 'query',
      params: "SELECT kind, count(*) FROM node GROUP BY kind",
      startedAt: now - 300,
      tokenBudget: 500,
    },
    requestCount: 12,
    totalTokensUsed: 6_100,
  },
  {
    id: 'c3d4e5f6-a7b8-9012-cdef-123456789012',
    name: 'clawdbot',
    connectedAt: now - 7_200_000,
    activeRequest: null,
    requestCount: 231,
    totalTokensUsed: 412_000,
  },
  {
    id: 'd4e5f6a7-b8c9-0123-defa-234567890123',
    name: 'claude-code',
    connectedAt: now - 60_000,
    activeRequest: {
      tool: 'read',
      params: 'file:///src/Auth/TokenService.cs#symbol=ValidateToken',
      startedAt: now - 80,
      tokenBudget: 1500,
    },
    requestCount: 8,
    totalTokensUsed: 12_450,
  },
];

export const MultipleClients: Story = {
  args: { clients, now },
};

export const AllIdle: Story = {
  args: {
    clients: clients.map((c) => ({ ...c, activeRequest: null })),
    now,
  },
};

export const SingleActive: Story = {
  args: { clients: [clients[0]!], now },
};

export const NoClients: Story = {
  args: { clients: [], now },
};
