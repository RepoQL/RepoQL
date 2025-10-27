# MCP Resources

RepoQL’s MCP server now advertises a resource template so agents can retrieve repository content directly from the index without running ad-hoc SQL.

## `repoql.document`

| Field | Value |
| --- | --- |
| **Name** | `repoql.document` |
| **URI template** | `{+uri}` (absolute RepoURI) |
| **MIME type** | `text/plain; charset=utf-8` |
| **Description** | “Fetch repository files or embedded documents by RepoURI (file:///…, embed:///…). Supports #line= and #char= fragments for slicing results.” |

### Supported fragments

- `#line=start,end` (1-based, inclusive)
- `#char=start,end` (0-based, half-open `[start,end)`)
- Plain anchors or JSON pointers are accepted; the server currently returns the full document for these forms.

### Usage flow

1. Call `list_resource_templates` and select `repoql.document`.
2. Provide an absolute RepoURI when invoking `read_resource`.
   - Example: `file:///src/RepoQL.ConsoleApp/Program.cs#line=20,60`
   - Embedded docs: `embed:///quickstart.md`
3. The server returns a single `text` resource containing the requested slice (or the entire document if no range fragment is supplied).

The content is sourced from the indexed `artifact.text_content` column, so responses are fast and consistent with RepoQL query results. If the URI cannot be resolved, the server returns a not-found error.

## `repoql.summary`

| Field | Value |
| --- | --- |
| **Name** | `repoql.summary` |
| **URI template** | `repoql-summary:{+uri}` (pass the RepoURI as `uri`) |
| **MIME type** | `text/markdown; charset=utf-8` |
| **Description** | “Return headline, summary, structure, and recent annotations for a RepoURI.” |

### Response layout

The Markdown payload contains:

- **Headline** – `artifact.headline`
- **Summary** – `artifact.summary` if available
- **Structure** – `artifact.structure` in a fenced block
- **Metadata** – canonical RepoURI and media type
- **Annotations** – up to 20 recent rows from `annotations_for(uri, NULL, NULL)` with severity, source/rule, resolved target URI, created timestamp, and compact JSON payloads

### Usage flow

1. Call `list_resource_templates` and choose `repoql.summary`.
2. Expand the template with the desired RepoURI, e.g. `repoql-summary:file:///src/RepoQL.ConsoleApp/Program.cs`.
3. Invoke `read_resource` with that URI. The server returns the abstract; range fragments are ignored (use `repoql.document` when you need a specific slice).

This view is useful for quick context lookups without pulling the full file contents. Annotations include their resolved targets so agents can drill back into precise locations via tools or follow-up queries.

### CLI equivalent

The CLI mirrors these resources via the `repoql resource <uri>` command:

- `repoql resource file:///...` → full document (with optional `#line=` / `#char=` fragments)
- `repoql resource repoql-summary:file:///...` → abstract + annotations

This makes it easy to script resource retrieval outside of MCP clients while keeping behavior consistent.
