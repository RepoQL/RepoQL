import type { Meta, StoryObj } from '@storybook/react';
import { PhaseIndicator } from './PhaseIndicator';

const meta: Meta<typeof PhaseIndicator> = {
  title: 'Components/PhaseIndicator',
  component: PhaseIndicator,
  decorators: [(Story) => <div style={{ width: 220, background: 'var(--surface)' }}><Story /></div>],
};
export default meta;

type Story = StoryObj<typeof PhaseIndicator>;

export const Idle: Story = { args: { phase: 'idle' } };
export const Discovery: Story = { args: { phase: 'discovery' } };
export const Parsing: Story = { args: { phase: 'parsing' } };
export const StructEmbedding: Story = { args: { phase: 'struct_embedding' } };
export const FullEmbedding: Story = { args: { phase: 'full_embedding' } };
export const Complete: Story = { args: { phase: 'complete' } };
