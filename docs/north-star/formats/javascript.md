# JavaScript/TypeScript Format: What Great Looks Like

> An agent should know what a module exports, what it depends on, and how it fits into the application — without reading it.

An agent exploring a repository encounters 2,000 JavaScript and TypeScript files across a React frontend, a Node.js backend, and a shared library. It scans 2,000 headlines and knows what each file is: a React component with 3 props and 2 hooks, an Express route handler with 4 endpoints, a utility module exporting 12 pure functions, a TypeScript interface file defining the API contract, a test file covering 8 scenarios. It filters to the 40 files related to authentication, reads their structures — exported functions with signatures, imported dependencies, component prop types — and understands the auth flow end to end. It queries the graph: "what imports TokenService?" and traces the dependency chain from the login component through the API client to the token refresh middleware. Every file used a different module style — ESM imports, CommonJS requires, re-exports, barrel files. The agent saw one dependency graph.

---

## Discovery

- An agent should be able to distinguish file roles from a headline alone — component, hook, utility, route handler, middleware, test, type definition, configuration, barrel
- An agent should be able to see what a module exports and what it depends on without opening it
- An agent should be able to tell whether a file is TypeScript or JavaScript, and whether it uses JSX, from the headline
- An agent should be able to see the scale of a module — number of exports, imports, declarations — as a filtering signal

```
headline  →  "TokenService.ts | code.typescript | 320 ln, ~1.8k tok | exports: refreshToken, validateToken, revokeToken | imports: 4"
headline  →  "LoginForm.tsx | code.typescript.react | 180 ln, ~1.1k tok | component: LoginForm(props: LoginFormProps) | hooks: useState, useAuth"
headline  →  "index.ts | code.typescript | 45 ln, ~0.3k tok | barrel: re-exports 12 symbols from 6 modules"
```

---

## Structure

- An agent should be able to see every exported symbol with its signature — function parameters, return types, component props
- An agent should be able to see every import and where it comes from — package imports vs relative imports, named vs default vs namespace
- An agent should be able to see class and interface members with their types
- An agent should be able to navigate to any declaration by symbol name without reading the whole file

```
structure →
  TokenService.ts (code.typescript)
    Imports:
      jwt from 'jsonwebtoken'
      { TokenStore } from './store'
      { config } from '../config'
      { UnauthorizedError } from '../errors'
    Exports:
      +async refreshToken(token: string): Promise<TokenPair>    #symbol=refreshToken
      +async validateToken(token: string): Promise<Claims>      #symbol=validateToken
      +revokeToken(tokenId: string): void                       #symbol=revokeToken
    Internal:
      -decodePayload(raw: string): JwtPayload                   #symbol=decodePayload
```

---

## Module Graph

- An agent should be able to trace the full import chain of any module — what it depends on, directly and transitively
- An agent should be able to find all consumers of a module — everything that imports from it
- An agent should be able to distinguish between package dependencies (npm) and internal dependencies (relative imports)
- An agent should be able to find circular dependencies in one query
- An agent should be able to see barrel files and re-export chains resolved to their original sources

```sql
-- What imports TokenService?
SELECT source.uri, e.properties->>'specifier'
FROM edge e
JOIN node source ON source.id = e.source_node_id
JOIN node target ON target.id = e.destination_node_id
WHERE e.type = 'IMPORTS' AND target.uri LIKE '%TokenService%'
```

---

## Components

- An agent should be able to find all React components and see their prop types from structure alone
- An agent should be able to see which hooks a component uses without reading it
- An agent should be able to distinguish container components from presentational components from structure
- An agent should be able to find components that render a given component — the component tree as a queryable graph

---

## Type System

- An agent should be able to query TypeScript interfaces and type aliases as first-class graph entities
- An agent should be able to find all types that extend or implement a given interface
- An agent should be able to see generic type parameters and constraints in structure
- An agent should be able to find where a type is used — as a parameter, return type, prop type, or variable annotation
- An agent should be able to query JavaScript files that lack types and see what structure can still be inferred from usage

---

## Exports and API Surface

- An agent should be able to see a module's public API — its exports — without reading internals
- An agent should be able to distinguish between named exports, default exports, and re-exports
- An agent should be able to find every module that contributes to a package's public surface
- An agent should be able to find unexported functions and dead code — declarations with no internal callers and no exports

---

## Dependencies

- An agent should be able to see all npm package dependencies referenced across the codebase
- An agent should be able to find which files import a given package
- An agent should be able to find unused imports — imports that are declared but never referenced
- An agent should be able to find version-sensitive imports — dynamic imports, conditional requires, polyfill patterns

---

## Ecosystem Patterns

- An agent should be able to recognize framework patterns from structure — Express routes, React hooks, Next.js pages, test suites — without hardcoding framework knowledge
- An agent should be able to find all route definitions across an Express/Fastify/Koa application
- An agent should be able to find all test files and see which modules they cover
- An agent should be able to find configuration files (webpack, tsconfig, eslint, vite) and see how they affect the build

---

## Integrity

- An agent should be able to find files with parse errors and see what was recoverable
- An agent should be able to find TypeScript files with type errors surfaced as diagnostics
- An agent should be able to find import specifiers that resolve to nothing — missing modules, wrong paths
- An agent should be able to trust that JSX, decorators, and other syntax extensions parse correctly

---

## What Great Looks Like

| Declaration | Why It Matters |
|-------------|----------------|
| Distinguish module roles from headlines | 2,000 files become navigable in one scan |
| See exports with signatures | Know a module's API without reading it |
| Trace the full import graph | "What depends on X?" answers in one query |
| Find components with their props and hooks | Understand UI structure from the graph |
| Query types as first-class entities | TypeScript's type system becomes searchable |
| Resolve re-exports to sources | Barrel files don't hide what they re-export |
| Surface parse and type errors | Problems found before runtime |

---

## Anti-Patterns

| Don't | Declaration Form |
|-------|------------------|
| Read a file to learn its exports | An agent should see exports from the headline |
| Trace imports by grepping strings | An agent should traverse the import graph |
| Ignore JavaScript because it lacks types | An agent should infer what it can from structure |
| Treat JSX as a different language | An agent should query components through the same surface |
| Model AST nodes in the graph | An agent should query declarations, not syntax trees |
| Hardcode framework detection | An agent should recognize patterns from structure |

---

*An agent should be able to understand a JavaScript/TypeScript codebase as a graph of modules, types, and components — navigable from headline to signature to source — without reading a single file to discover what it exports.*
