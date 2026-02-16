import type { Meta, StoryObj } from '@storybook/react';
import { ProgressRings } from './ProgressRings';

const meta: Meta<typeof ProgressRings> = {
  title: 'Components/ProgressRings',
  component: ProgressRings,
  decorators: [(Story) => <div style={{ width: 220, background: 'var(--surface)' }}><Story /></div>],
};
export default meta;

type Story = StoryObj<typeof ProgressRings>;

export const Empty: Story = {
  args: { total: 847, parsed: 0, structEmbedded: 0, fullEmbedded: 0, complete: false },
};

export const Parsing: Story = {
  args: { total: 847, parsed: 520, structEmbedded: 180, fullEmbedded: 0, complete: false },
};

export const Embedding: Story = {
  args: { total: 847, parsed: 847, structEmbedded: 700, fullEmbedded: 350, complete: false },
};

export const Complete: Story = {
  args: { total: 847, parsed: 847, structEmbedded: 845, fullEmbedded: 845, complete: true },
};
