import type { Preview } from '@storybook/react';
import '../src/theme.css';

const preview: Preview = {
  parameters: {
    backgrounds: {
      default: 'repoql',
      values: [
        { name: 'repoql', value: '#0e0e11' },
        { name: 'surface', value: '#161619' },
      ],
    },
    layout: 'fullscreen',
  },
};

export default preview;
