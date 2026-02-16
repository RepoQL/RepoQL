import type { Meta, StoryObj } from '@storybook/react';
import { ActivityStream } from './ActivityStream';
import { generateFiles, generateActivityEntries, PIPELINE_PARSING, PIPELINE_EMBEDDING } from '../../fixtures';

const meta: Meta<typeof ActivityStream> = {
  title: 'Components/ActivityStream',
  component: ActivityStream,
  decorators: [(Story) => <div style={{ width: 220, height: 300, background: 'var(--surface)', display: 'flex' }}><Story /></div>],
};
export default meta;

type Story = StoryObj<typeof ActivityStream>;

const files = generateFiles();

export const Parsing: Story = {
  args: { entries: generateActivityEntries(files, PIPELINE_PARSING) },
};

export const Embedding: Story = {
  args: { entries: generateActivityEntries(files, PIPELINE_EMBEDDING) },
};

export const Empty: Story = {
  args: { entries: [] },
};
