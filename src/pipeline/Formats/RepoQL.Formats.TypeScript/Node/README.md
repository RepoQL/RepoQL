# TypeScript/JavaScript Parser for RepoQL

This directory contains a Node.js helper that provides TypeScript and JavaScript parsing for RepoQL.

## Prerequisites

**Node.js** must be installed and available in your PATH:
- Minimum version: Node.js 14.x or later
- Recommended: Node.js 18.x or later

Check if Node.js is installed:
```bash
node --version
```

If not installed, download from: https://nodejs.org/

## Setup

Before using TypeScript/JavaScript format support, install the dependencies:

```bash
cd src/Formats/RepoQL.Formats.TypeScript/Node
npm install
```

This installs the TypeScript compiler API (`typescript` package) which is required for parsing.

## How It Works

The `ts-parser.js` script:
- Runs as a long-lived Node.js process
- Communicates with RepoQL via line-delimited JSON over stdin/stdout
- Uses the official TypeScript compiler API for parsing
- Supports `.ts`, `.tsx`, `.js`, and `.jsx` files
- Returns AST information: imports, exports, declarations, members, diagnostics

## Troubleshooting

### Error: "Node helper failed to start"

**Cause**: Node.js not installed or not in PATH

**Fix**: Install Node.js from https://nodejs.org/ and restart your terminal

### Error: "TypeScript compiler API not found"

**Cause**: Dependencies not installed

**Fix**: Run `npm install` in this directory

### Parser times out after 30 seconds

**Cause**: File is too large or contains complex syntax

**Fix**: This is a safety timeout. The file will be skipped and logged as an error.

## Development

The parser extracts:
- **Imports**: Module specifiers, import styles (default, named, namespace, side-effect)
- **Exports**: Named exports, default exports, re-exports
- **Declarations**: Functions, classes, interfaces, types, enums, variables
- **Members**: Methods, properties, constructors, getters/setters
- **React Components**: Detects PascalCase functions/classes with JSX
- **Diagnostics**: Syntax errors from TypeScript parser

To test the parser standalone:
```bash
node ts-parser.js --stdio
```

Then send JSON requests via stdin:
```json
{"id":"test1","path":"sample.ts","mediaKind":"code.typescript","text":"export const x = 1;"}
```

## License

This helper uses the TypeScript compiler API which is licensed under the Apache License 2.0.
See: https://github.com/microsoft/TypeScript/blob/main/LICENSE.txt
