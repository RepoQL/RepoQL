import type { Meta, StoryObj } from '@storybook/react';
import { LanguageSpectrum } from './LanguageSpectrum';
import { generateFiles, computeLanguageCounts } from '../../fixtures';

const meta: Meta<typeof LanguageSpectrum> = {
  title: 'Components/LanguageSpectrum',
  component: LanguageSpectrum,
  decorators: [(Story) => <div style={{ width: 220, background: 'var(--surface)' }}><Story /></div>],
};
export default meta;

type Story = StoryObj<typeof LanguageSpectrum>;

const files = generateFiles();

export const Partial: Story = {
  args: { languages: computeLanguageCounts(files, 400) },
};

export const Full: Story = {
  args: { languages: computeLanguageCounts(files, 847) },
};

export const Empty: Story = {
  args: { languages: [] },
};
