import type { Meta, StoryObj } from '@storybook/react';
import { FileTreemap } from './FileTreemap';
import {
  generateFiles, groupByPhase, applyPipelineState,
  PIPELINE_EMPTY, PIPELINE_DISCOVERING, PIPELINE_PARSING, PIPELINE_EMBEDDING, PIPELINE_COMPLETE,
} from '../../fixtures';
import type { PipelineState } from '../../types';

const meta: Meta<typeof FileTreemap> = {
  title: 'Components/FileTreemap',
  component: FileTreemap,
  decorators: [(Story) => <div style={{ height: '70vh', display: 'flex' }}><Story /></div>],
};
export default meta;

type Story = StoryObj<typeof FileTreemap>;

function makeProps(pipeline: PipelineState) {
  const files = applyPipelineState(generateFiles(), pipeline);
  return {
    groups: groupByPhase(files),
    stats: {
      total: pipeline.discovered,
      classified: pipeline.classified,
      parsed: pipeline.parsed,
      searchable: pipeline.structEmbedded,
    },
  };
}

export const Empty: Story = { args: makeProps(PIPELINE_EMPTY) };
export const Discovering: Story = { args: makeProps(PIPELINE_DISCOVERING) };
export const Parsing: Story = { args: makeProps(PIPELINE_PARSING) };
export const Embedding: Story = { args: makeProps(PIPELINE_EMBEDDING) };
export const Complete: Story = { args: makeProps(PIPELINE_COMPLETE) };
