<WHY>
When `query` and `read` together aren't enough, drop into JavaScript with all RepoQL data, the file system, and approved MCP tools at hand. Compose what doesn't exist as a single tool: join data across sources, reshape results, render diagrams, convert documents, produce artifacts.

You can also define and store reusable JavaScript or WASM modules so the next composition is one import away.
</WHY>

<WHEN_TO_USE>
- When you need a non-text artifact — SVG/PNG diagrams (`graphviz`, `svgToPng`), document conversions (`pandoc`), or audio/video probes (`ffmpeg`).
- When the workflow needs imperative shape — branch on intermediate values, accumulate across iterations, retry with backoff, or stitch results in a way the SQL planner can't reach.
- When you need to write outputs back into the sandbox (`repoql.write`) for later steps to consume.
- Prefer `query` when one SQL statement would do — it can already join graph data with external MCP results via the generated `mcp_*` tool macros, no JavaScript needed.
</WHEN_TO_USE>

<COMMON_FUNCTIONS>
- `repoql.query(sql)` executes SQL against the indexed graph and returns JavaScript arrays/objects.
- `repoql.read(uri, { budget })` reads indexed content and returns `{ content, representation, tokensUsed }`.
- `repoql.write(uri, content)` writes inside the configured sandbox write scope. Default: anywhere in the repo (paths resolve relative to the repo root and cannot escape it).
- `repoql.delete(uri)` deletes inside the configured sandbox delete scope. Default: `file:///.repoql/tmp/**`.
- `repoql.graphviz(dot, engine?, format?)` renders DOT to SVG.
- `repoql.svgToPng(svg, scale?)` rasterizes SVG to base64 PNG.
- `repoql.pandoc({ input, from, to, args? })` converts documents.
- `repoql.ffmpeg({ input, output, args?, probe? })` runs scoped ffmpeg/ffprobe.
- `import("yaml")`, `import("diff")`, `import("semver")`, and other built-in modules are available.
- `mcp.github.toolName(args)` calls approved MCP tools through the RepoQL MCP bridge and returns parsed rows/data.
</COMMON_FUNCTIONS>

<EXAMPLES>
```js
mcp.servers()
mcp.tools("github")
mcp.describe("github", "search_issues")
mcp.github.search_issues({ q: "repo:RepoQL/RepoQL.Core is:issue", limit: 10 })
```

Examples:
```js
const files = repoql.query("SELECT uri, headline FROM files WHERE uri LIKE 'file:///src/%'");
files.filter(f => /auth|login/i.test(f.headline));
```

```js
const issues = mcp.github.search_issues({ q: "repo:RepoQL/RepoQL.Core is:open", limit: 20 });
issues.map(i => ({ title: i.title, url: i.html_url || i.url }));
```

```js
const dot = "digraph { rankdir=LR; query -> read; read -> execute; execute -> mcp }";
const svg = repoql.graphviz(dot);
repoql.write("file:///.repoql/tmp/tool-flow.svg", svg);
"file:///.repoql/tmp/tool-flow.svg";
```
</EXAMPLES>

Return normal JavaScript values. Objects and arrays become tabular output when possible. Console diagnostics are captured.
