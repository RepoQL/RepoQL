<WHY>
The index already knows everything. Explore searches it exhaustively — every file, symbol, and relationship — and shows you what exists, ranked by relevance. One call reveals the landscape AND the vocabulary: the real class names, patterns, and terms-of-art you need for everything after.
Without that first explore, you're guessing names and grepping blind.

Hybrid lexical + semantic match, ranked by relevance, with budget spent on the matches most likely to answer you. Where grep would have you read 30 files to find the right name, one explore returns it.
</WHY>

<WORKFLOW>
Explore is your first action. It teaches you the vocabulary for everything after.

Vocabulary cold — new to this codebase, or your terms generic or borrowed from elsewhere? Run `keywords` first — it reshapes rough handles into this repo's real vocabulary, then explore WITH what it returns.

1. **Explore broadly** — `explore(keywords="authentication", question="What is the nomenclature related to authentication in this repository?")` → discovers `JwtTokenValidator`, `SessionMiddleware`, `OAuthConfig`
2. **Explore with intent** — `explore(keywords="jwt validation", question="where is the JWT signature verified before the token is accepted?")` → rerank promotes the file that answers the question above peers that merely mention JWTs
3. **Read precisely** — `read("file:///src/Auth.cs#symbol=JwtTokenValidator.Validate => content", 800)`
4. **Iterate** — explore again with refined keywords from what you learned

The first explore is never wasted — even unexpected results teach you what IS there.
</WORKFLOW>

<PARAMETERS>
**keywords** (required): Your vocabulary probe — class names, concepts, short phrases. No filler words. Search is hybrid (lexical + semantic).
→ `"authentication middleware"` — conceptual search
→ `"ValidateToken"` — exact name search
→ `"token refresh flow"` — topic probe

**`question`** is your intent — what specifically are you trying to find out? Results are ordered by how well they ANSWER you, not just how well they match the keywords. When you have a specific question, pass it. When you're just surveying what exists, omit it.

**uriGlob** (recommended): Restrict the search scope. Unscoped searches span every imported filesystem — `file:///` (your repo), `github://owner/repo` (imported repos), `help:///` (embedded docs) — which is noisier than you usually want. Scope at least to the scheme you care about. Full shell glob vocabulary — `*`, `**`, `?`, `[a-z]` POSIX character classes, `{a,b}` brace expansion, `;` alternation, `!` exclusion.
→ `"file:///**"` — current repo only (good default)
→ `"file:///src/**/*.cs"` — only C# files
→ `"file:///src/Auth/**"` — only the Auth directory
→ `"file:///src/{L1,L2,L3}/**/*.{cs,md}"` — layered trees, multi-extension
→ `"file:///src/**;!**/tests/**"` — source without tests
→ `"github://owner/repo/**"` — a specific imported repo
→ `"help:///**"` — search the embedded docs (schemas, patterns, MCP bridge guides, troubleshooting) instead of guessing the right page from a URL
→ Omit only when you explicitly want cross-repo search (e.g. comparing your code against an imported reference).

**breadth** (optional): Maximum results. 0 = auto (server decides based on budget).
→ `breadth=2` — deep dive into top matches
→ `breadth=10` — broad survey

**tokenBudget** (optional, default 800): How many tokens to spend on the response.
→ Start at 800, increase if you need richer representations

Budget controls depth vs breadth automatically:
- 800 tokens: mostly headlines — a map of what exists
- 3000+ tokens: structure and snippets start appearing
- 10000+ tokens: expect to find everything relevant, with the key symbols expanded
- Breadth parameter caps result count (0 = auto)

Results include confidence scores. High-confidence matches get richer representations.
</PARAMETERS>

<VS>
Use **explore** when you need to FIND something (what exists, where is X, how does Y work).
Use **explain** when you want to understand something.
Use **read** when you KNOW the URI(s) and want the content.
Explore reveals. Read retrieves.
</VS>
