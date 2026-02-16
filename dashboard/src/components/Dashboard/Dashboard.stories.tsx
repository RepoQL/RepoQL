import { useState } from 'react';
import type { Meta, StoryObj } from '@storybook/react';
import { Dashboard } from './Dashboard';
import type { DashboardProps } from './Dashboard';
import { useSimulation } from './useSimulation';
import {
  generateFiles, groupByPhase, applyPipelineState, computeLanguageCounts,
  generateActivityEntries,
  PIPELINE_EMPTY, PIPELINE_PARSING, PIPELINE_COMPLETE,
} from '../../fixtures';
import type { PipelineState } from '../../types';

const meta: Meta<typeof Dashboard> = {
  title: 'Dashboard/Full',
  component: Dashboard,
  parameters: { layout: 'fullscreen' },
};
export default meta;

type Story = StoryObj<typeof Dashboard>;

function makeStaticProps(pipeline: PipelineState, elapsed: number): DashboardProps {
  const files = applyPipelineState(generateFiles(), pipeline);
  return {
    title: 'import github://owner/repo',
    elapsed,
    pipeline,
    groups: groupByPhase(files),
    languages: computeLanguageCounts(files, pipeline.classified),
    activities: generateActivityEntries(files, pipeline),
  };
}

/** Animated story — runs the full simulation */
function AnimatedDashboard() {
  const [files] = useState(() => generateFiles());
  const sim = useSimulation(true, files);

  const applied = applyPipelineState(files, sim.pipeline);
  const groups = groupByPhase(applied);
  const languages = computeLanguageCounts(applied, sim.pipeline.classified);

  return (
    <Dashboard
      title="import github://owner/repo"
      elapsed={sim.elapsed}
      pipeline={sim.pipeline}
      groups={groups}
      languages={languages}
      activities={sim.activities}
    />
  );
}

export const Animated: Story = {
  render: () => <AnimatedDashboard />,
};

export const Empty: Story = {
  args: makeStaticProps(PIPELINE_EMPTY, 0),
};

export const MidParse: Story = {
  args: makeStaticProps(PIPELINE_PARSING, 12.4),
};

export const Complete: Story = {
  args: makeStaticProps(PIPELINE_COMPLETE, 38.2),
};
