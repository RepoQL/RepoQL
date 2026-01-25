# Read Question Flow

LLM-synthesized answer to a question about matched files.

## Why This Matters

Questions get direct answers synthesized from multiple locations within the selected files. Agents don't need to read and interpret—they get an explanation with citations they can verify.

| Without | With |
|---------|------|
| Read files, interpret, synthesize manually | Ask question, get synthesized answer |
| Risk missing relevant context | LLM finds and combines relevant parts |
| No traceability to source | Citations point to exact locations |

## Trigger

`read("<pattern> => question: <question>", tokenBudget)`

## Stages

### 1. Pattern Resolution
**Actor**: Read tool
**Action**: Resolve glob/URI pattern to matching files
**Output**: Set of file URIs with content
**Failure**: Invalid pattern returns error with suggestion

### 2. Context Assembly
**Actor**: Read tool
**Action**: Gather content from matched files for LLM context
**Output**: File contents prepared for synthesis

Context selection respects token budget—prioritizes files most likely relevant to the question if not all fit.

### 3. LLM Synthesis
**Actor**: LLM (via ask macro)
**Action**: Answer the question using only the provided file contents
**Output**: Synthesized answer with source references

Answer constraints:
- Based only on provided content (no external knowledge)
- Claims linked to specific file locations
- Uncertainty acknowledged when content is ambiguous

### 4. Citation Formatting
**Actor**: Read tool
**Action**: Format citations as verifiable URI fragments
**Output**: Answer with derivation section showing sources

Citation format follows existing ask/explore behavior—derivation section with URIs pointing to exact locations.

## Termination

Flow completes when:
- Answer synthesized from matched file contents
- Derivation section lists source URIs with line references
- Footer reports files consulted and tokens used

## Error Handling

| Condition | Behaviour |
|-----------|-----------|
| No files match pattern | Return error—cannot answer without content |
| Question unanswerable from content | Return "cannot determine from provided files" with what was found |
| Content exceeds context window | Prioritize most relevant files; note files excluded |

## Verification

| Environment | How |
|-------------|-----|
| Local | Ask question about known code; verify answer accuracy and citation validity |
| Automated tests | Assert citations point to real locations; content at citations supports claims |
| Production | Track citation accuracy; monitor for hallucinated file references |

## Related

- `find.md` — semantic search for locations (not synthesis)
- `explore(Explain)` — repo-wide question answering
