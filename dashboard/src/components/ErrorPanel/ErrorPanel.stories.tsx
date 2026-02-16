import type { Meta, StoryObj } from '@storybook/react';
import { ErrorPanel } from './ErrorPanel';
import type { FileError } from '../../types';
import { LANGUAGES } from '../../types';

const meta: Meta<typeof ErrorPanel> = {
  title: 'Components/ErrorPanel',
  component: ErrorPanel,
  decorators: [(Story) => <div style={{ maxWidth: 500 }}><Story /></div>],
};
export default meta;

type Story = StoryObj<typeof ErrorPanel>;

const sampleErrors: FileError[] = [
  {
    path: 'src/auth/TokenValidator.cs',
    lang: LANGUAGES['.cs']!,
    category: 'parse',
    message: 'Unexpected token at line 47: missing closing brace',
    hint: 'Check for unbalanced braces in the class definition',
  },
  {
    path: 'src/utils/legacy_encoder.py',
    lang: LANGUAGES['.py']!,
    category: 'encoding',
    message: 'Invalid UTF-8 sequence at byte offset 2341',
    hint: 'File may use Latin-1 encoding — convert to UTF-8',
  },
  {
    path: 'lib/data/dump.sql',
    lang: LANGUAGES['.sql']!,
    category: 'timeout',
    message: 'Parse exceeded 30s limit (file: 48MB)',
  },
  {
    path: 'src/core/generated.proto',
    lang: LANGUAGES['.proto']!,
    category: 'parse',
    message: 'Unsupported proto3 map syntax at line 12',
  },
  {
    path: 'tests/fixtures/binary.dat',
    lang: { name: 'Unknown', color: '#555560' },
    category: 'unsupported',
    message: 'Binary file — no parser registered for .dat',
  },
];

export const WithErrors: Story = {
  args: { errors: sampleErrors },
};

export const SingleError: Story = {
  args: { errors: [sampleErrors[0]!] },
};

export const NoErrors: Story = {
  args: { errors: [] },
};

export const ManyErrors: Story = {
  args: {
    errors: [
      ...sampleErrors,
      { path: 'src/api/routes.ts', lang: LANGUAGES['.ts']!, category: 'parse', message: 'Unexpected EOF in template literal' },
      { path: 'src/models/User.java', lang: LANGUAGES['.java']!, category: 'parse', message: 'Invalid annotation syntax at line 3' },
      { path: 'config/settings.yaml', lang: LANGUAGES['.yaml']!, category: 'parse', message: 'Indentation error at line 28' },
      { path: 'src/core/engine.rs', lang: LANGUAGES['.rs']!, category: 'timeout', message: 'Parse exceeded 30s limit (deep macro expansion)' },
    ],
  },
};
