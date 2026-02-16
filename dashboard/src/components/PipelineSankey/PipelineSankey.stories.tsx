import type { Meta, StoryObj } from '@storybook/react';
import { PipelineSankey } from './PipelineSankey';
import { PIPELINE_EMPTY, PIPELINE_DISCOVERING, PIPELINE_PARSING, PIPELINE_EMBEDDING, PIPELINE_COMPLETE } from '../../fixtures';

const meta: Meta<typeof PipelineSankey> = {
  title: 'Components/PipelineSankey',
  component: PipelineSankey,
};
export default meta;

type Story = StoryObj<typeof PipelineSankey>;

export const Empty: Story = { args: { pipeline: PIPELINE_EMPTY } };
export const Discovering: Story = { args: { pipeline: PIPELINE_DISCOVERING } };
export const Parsing: Story = { args: { pipeline: PIPELINE_PARSING } };
export const Embedding: Story = { args: { pipeline: PIPELINE_EMBEDDING } };
export const Complete: Story = { args: { pipeline: PIPELINE_COMPLETE } };
