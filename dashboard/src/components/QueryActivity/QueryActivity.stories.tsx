import type { Meta, StoryObj } from '@storybook/react';
import { QueryActivity } from './QueryActivity';
import type { QueryEntry } from '../../types';

const meta: Meta<typeof QueryActivity> = {
  title: 'Components/QueryActivity',
  component: QueryActivity,
  decorators: [(Story) => <div style={{ maxWidth: 480 }}><Story /></div>],
};
export default meta;

type Story = StoryObj<typeof QueryActivity>;

const entries: QueryEntry[] = [
  {
    id: 1,
    tool: 'explore',
    params: 'intent=Locate keywords="authentication middleware"',
    tokenBudget: 2000,
    tokensUsed: 1847,
    elapsed: 340,
    resultSummary: '8 files matched, 3 symbols highlighted',
    timestamp: Date.now() - 2000,
  },
  {
    id: 2,
    tool: 'read',
    params: 'file:///src/auth/JwtHandler.cs#symbol=ValidateToken',
    tokenBudget: 1500,
    tokensUsed: 1210,
    elapsed: 45,
    resultSummary: '1 symbol, 42 lines',
    timestamp: Date.now() - 5000,
  },
  {
    id: 3,
    tool: 'query',
    params: "SELECT kind, count(*) FROM node GROUP BY kind",
    tokenBudget: 500,
    tokensUsed: 320,
    elapsed: 12,
    resultSummary: '7 rows returned',
    timestamp: Date.now() - 8000,
  },
  {
    id: 4,
    tool: 'explain',
    params: 'question="How does the caching layer invalidate?"',
    tokenBudget: 3000,
    tokensUsed: 2890,
    elapsed: 4200,
    resultSummary: 'Synthesized from 12 sources',
    timestamp: Date.now() - 15000,
  },
  {
    id: 5,
    tool: 'explore',
    params: 'intent=Inventory uriGlob="src/api/**"',
    tokenBudget: 1000,
    tokensUsed: 980,
    elapsed: 180,
    resultSummary: '34 files inventoried',
    timestamp: Date.now() - 20000,
  },
  {
    id: 6,
    tool: 'read',
    params: 'file:///src/core/Pipeline.cs => tree: structure',
    tokenBudget: 800,
    tokensUsed: 220,
    elapsed: 22,
    resultSummary: '1 file, structure view',
    timestamp: Date.now() - 25000,
  },
];

export const Active: Story = { args: { entries } };
export const FewQueries: Story = { args: { entries: entries.slice(0, 2) } };
export const NoQueries: Story = { args: { entries: [] } };
