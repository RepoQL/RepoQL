import type { Meta, StoryObj } from '@storybook/react';
import { StatusHeader } from './StatusHeader';

const meta: Meta<typeof StatusHeader> = {
  title: 'Components/StatusHeader',
  component: StatusHeader,
};
export default meta;

type Story = StoryObj<typeof StatusHeader>;

export const Idle: Story = {
  args: { title: 'awaiting operation', phase: 'idle', elapsed: 0 },
};

export const Indexing: Story = {
  args: { title: 'import github://owner/repo', phase: 'parsing', elapsed: 12.4 },
};

export const Complete: Story = {
  args: { title: 'import github://owner/repo', phase: 'complete', elapsed: 38.2 },
};
