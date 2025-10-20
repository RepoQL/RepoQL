# RepoQL.Web — Local Blazor Companion

## Purpose

Deliver a Blazor Server front end that rides on the existing RepoQL host to make local exploration easier. The app must:

- Provide an **arbitrary SQL studio** with result rendering and lightweight persistence of saved queries.
- Offer a **repository browser** that lists documents, shows structure/snippets, and surfaces linked records (nodes, edges, annotations) alongside the file.
- Expose **host status** (lease, indexing, reindex control) so users can tell whether background indexing is idle.

Durability, security, and multi-user concerns are explicitly out of scope; we optimise for local development ergonomics.

## High-Level Architecture

```text
Blazor Server (RepoQL.Web)
 ├─ Services
 │   ├─ RepoQlConnectionManager (singleton)
 │   ├─ SqlExecutionService (scoped)
 │   ├─ DocumentExplorerService (scoped)
 │   ├─ HostStatusService (hosted background)
 │   └─ SavedQueryStore (in-memory JSON file cache)
 └─ Components
     ├─ Pages/QueryStudio.razor
     ├─ Pages/Explorer.razor
     ├─ Shared/SqlResultGrid.razor
     ├─ Shared/SnippetViewer.razor
     └─ Shared/LinkedRecordsPanel.razor
```

- **Blazor Server** is chosen so we can reuse the Unix-domain-socket gRPC client from `RepoQL.Protocol` without extra authentication, tunnelling, or API hosting. Every circuit executes server-side C# near the repo.
- **RepoQlClient** connectivity is singleton per process; the app manages the lease heartbeat already implemented in `RepoQlClient` (src/RepoQL.Protocol/RepoQlClient.cs:81).
- **Services** expose higher-level operations to components, wrapping raw SQL and macros into typed methods with cancellation.

## Dependencies & Integration

- Reference `RepoQL.Protocol` from the new project to obtain `RepoQlClient` and the generated gRPC contracts.
- Reuse the macros already registered by the host for structured data:
  - `xray_documents()` for document inventory.
  - `xray_items(include_kinds, max_per_document)` for per-file structure.
  - `snippet(uri, context)` for preview windows.
  - `annotations_all(kind_filter, min_severity)` in ad-hoc SQL to show diagnostics.
- Execute `WaitForPipelineAsync` and `ReindexAllAsync` from the client to report/back up indexing state.

## Service Layer Responsibilities

### RepoQlConnectionManager (singleton)

- Holds the `RepoQlClient` instance.
- Offers `ValueTask<IRepoQlClient> GetClientAsync(CancellationToken)` and surfaces connection status (last error, last heartbeat).
- On application start, tries to connect immediately so UI feels responsive.
- Raises simple events (e.g., `ConnectionStateChanged`) via `Action` delegates for live UI updates.

### SqlExecutionService (scoped)

- `Task<SqlResult> ExecuteAsync(string sql, int? limit, CancellationToken token)` wraps `ExecuteRawQueryAsync`.
- `IAsyncEnumerable<SqlRow>` for streaming results (used by long-running queries).
- Shapes `SqlResult.Columns` and `SqlResult.Rows` for the grid component.
- Captures raw errors and returns them to the caller so the UI can display messages inline.

### DocumentExplorerService (scoped)

- Provides abstractions atop macros: `ListDocumentsAsync`, `GetDocumentStructureAsync`, `GetSnippetAsync`, `ListLinkedRecordsAsync`.
- Performs lightweight caching per scope (e.g., memoise the last snippet for a URI) to avoid repeated SQL calls when switching tabs.
- Accepts filter parameters (text search, media kind) translated into SQL.

### HostStatusService (hosted background)

- Polls `WaitForPipelineAsync` on an interval (e.g., every 3 seconds).
- Publishes pipeline snapshots and active lease count through a `Channel` or simple state container consumed by a layout component.
- Provides `TriggerReindexAsync(bool clear)` and surfaces progress using the streaming `ReindexAllAsync`.

### SavedQueryStore (singleton or file-backed helper)

- Stores a small list of saved SQL statements in `.repoql/ui/queries.json` within the repo.
- Exposes CRUD operations for the query studio sidebar.
- Because the app is single-user, simple `File.WriteAllText` with try/catch and no locking suffices.

## UI Composition

### Layout

- **NavBar**: tabs for *Query Studio*, *Explorer*, and a simple *Status* panel.
- **Status banner**: shows server lease details (client id, beats), pipeline stage counts, and buttons for *Reindex* / *Cancel*.

### Query Studio Page

- Editor: use `<textarea>` initially; allow swapping in Monaco later. Provide controls for *Run*, *Run (limit 100)*, *Save*, and *Open Saved Query*.
- Results grid: render column headers from `RawQueryResponse.Columns`, cells from `RowData`. Support toggling between tabular view and JSON (call the NDJSON format by re-running with `ResultFormat.Json` if needed).
- Allow export as CSV by hydrating `StringBuilder` on the server.
- Error area: display SQL text and the gRPC status message when execution fails.

### Explorer Page

- **Left rail**: tree/list fed by `xray_documents()`; includes search box that runs a filtered SQL (matches file name or URI).
- **Main panel**: tabs
  1. *Overview*: show artifact headline/summary/structure (fall back to `text_content` truncated if absent).
  2. *Structure*: table from `xray_items`; clicking an item updates the snippet context.
  3. *Snippet*: lines from `snippet()` with focus highlighting.
  4. *Linked Records*: run pre-built SQL to list nodes/edges/annotations referencing the document; allow severity filters.
- Each tab loads lazily on first view to keep startup light.

### Status Page (optional combined in layout)

- Show the latest `PipelineSnapshot` and aggregate counts.
- Provide *Reindex All* with a progress list that appends `ReindexProgress` entries streamed via `await foreach`.

## Data Flow Examples

1. **Run SQL query**
   - Component invokes `SqlExecutionService.ExecuteAsync`.
   - Service retrieves `RepoQlClient` and calls `ExecuteRawQueryAsync(sql, limit)`.
   - Response mapped into `SqlResult`.
   - Component renders grid; errors appear inline.

2. **Select document in explorer**
   - Document list item carries the document URI.
   - Selecting triggers three asynchronous loads in parallel:
     - `GetDocumentStructureAsync(uri)` to build the structure tab (calls `xray_items`).
     - `GetSnippetAsync(uri, context)` to populate snippet.
     - `ListLinkedRecordsAsync(uri)` to build node/edge/annotation tables.
   - Results cached in component state; subsequent tab switches reuse the same data.

3. **Reindex command**
   - User clicks *Reindex* button.
   - `HostStatusService.TriggerReindexAsync(clear: false)` launches `await foreach` loop over `ReindexAllAsync`.
   - Service pushes progress events to UI subscribers.
   - When stream completes, service forces a pipeline status refresh.

## Implementation Steps

1. **Project scaffolding**
   - Create `RepoQL.Web` project (`dotnet new blazorserver --no-https`).
   - Reference `RepoQL.Protocol` and shared packages (Grpc.Net.Client, etc.).
   - Register services in `Program.cs`, configure Razor components.

2. **Connection & status services**
   - Implement `RepoQlConnectionManager`, `HostStatusService`.
   - Add basic layout showing connection status.

3. **Query Studio**
   - Build page + reusable `SqlResultGrid`.
   - Implement saved query persistence.

4. **Explorer**
   - Build document list component calling `xray_documents`.
   - Implement tabs for overview, structure, snippet, linked records.

5. **Polish**
   - Add simple theming, keyboard shortcuts (Ctrl+Enter to run).
   - Document key macros/queries inside the app (help tooltip).

6. **Stretch (post-MVP)**
   - Monaco editor integration.
   - Diff viewer for recent file changes.
   - Custom query packs loaded from repo folder.

## Open Questions

- Do we need to support Windows named pipes in addition to Unix sockets? (Current client code handles Windows via `.repoql/socket.path` mapping, so no extra work should be needed.)
- Should snippets auto-refresh after reindex completes? For now we rely on manual refresh; can add signal later.
- Saved queries path: is `.repoql/ui/queries.json` acceptable or should we store in `%LOCALAPPDATA%` equivalents?

## Risks & Mitigations

- **Long-running SQL**: run everything with cancellation tokens and UI cancel button; default limit for grid to keep memory low.
- **Autostart failure**: surface raw error and provide link to launch `repoql serve` manually.
- **Schema changes**: since macros encapsulate schema-shaped data, using them as primary data source minimises breakage when tables evolve.
