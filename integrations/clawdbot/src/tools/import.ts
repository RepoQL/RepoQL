/**
 * repoql_import tool implementation.
 *
 * Purpose: Registers the import tool for adding external repositories.
 * Complexity: Delegates to InstanceManager and normalizes errors.
 */

import type { InstanceManager } from "../lifecycle/InstanceManager.js";
import type { ResolvedConfig } from "../config/types.js";
import { TOOL_TIMEOUTS } from "../config/types.js";
import { normalizeError } from "../mcp/errors.js";
import { ImportParams, type ImportParamsType } from "./schemas.js";
import type { McpToolResult } from "../mcp/types.js";

const IMPORT_DESCRIPTION = `Import or remove an external data source (e.g., a GitHub repository) from the current repoql datastore.

To import: Provide a URI such as \`github://owner/repo@ref\`.
To remove: Prefix the URI with \`-\` (e.g., \`-github://owner/repo\`) to delete the import and all its indexed data.

Optionally specify which pipeline stage to wait for [Discovery|Indexing|SemanticIndexing|Analysis|Unspecified]. Defaults to SemanticIndexing to ensure embeddings are ready for search. Use Unspecified to return immediately.

To see all imports: \`SELECT * FROM Filesystems\``;

/**
 * Registers the repoql_import tool.
 */
export function registerImportTool(
  api: any,
  manager: InstanceManager,
  config: ResolvedConfig,
  getWorkdir: () => string
): void {
  api.registerTool({
    name: "repoql_import",
    description: IMPORT_DESCRIPTION,
    parameters: ImportParams,
    async execute(_id: string, params: ImportParamsType): Promise<McpToolResult> {
      try {
        const workdir = getWorkdir();
        const client = await manager.getInstance(workdir);
        const timeout = TOOL_TIMEOUTS.import ?? config.defaultTimeoutMs;

        const args: Record<string, unknown> = {
          uri: params.uri,
        };

        if (params.waitFor !== undefined) {
          args.waitFor = params.waitFor;
        }

        return await client.callTool("import", args, timeout);
      } catch (err) {
        return normalizeError(err);
      }
    },
  });
}
