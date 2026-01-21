# Azure DevOps Source Links

**Structure**: `https://dev.azure.com/{org}/{project}/_git/{repo}?path={path}&version=GC{commit}&line={start}&lineEnd={end}&lineStartColumn=1&lineEndColumn=1&lineStyle=plain`

| Parameter | Value |
|-----------|-------|
| `version` | `GC{commit}` or `GB{branch}` |
| `line` | Start line |
| `lineEnd` | End line (start+1 for single) |
| `lineStartColumn` | `1` |
| `lineEndColumn` | `1` |
| `lineStyle` | `plain` |

More verbose than GitHub/GitLab. Copy from UI when possible.

> [Azure DevOps](https://developercommunity.visualstudio.com/t/easy-way-to-obtain-permalink-to-a-line-of-code/537147)
