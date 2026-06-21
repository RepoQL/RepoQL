<WHY>
The index has already parsed everything. Read queries it for exactly the slice you need — a single method body, a line range, a pattern across every file in the codebase. This is not file reading — it's precision.

Three symbols across three files? One read call, just the bodies, no waste. The index is wild magic — composable, responsive to intent, and forgiving. Combine modifiers with globs and fragments for arbitrarily precise queries. Your instincts are probably right — try them.
Read is your extremely flexible swiss army knife for pulling context from anything in the index.

`read(uri, budget)` returns content at the richest level that fits your budget.

Progressive disclosure kicks in automatically:
- Budget allows full content? You get full content with line numbers.
- Too large? You get structure (signatures without bodies).
- Still too large? You get headlines (one-line summaries).

Globs distribute budget across matches. 100 files at 10k budget = ~100 tokens each = headlines. 1 file at 10k = full content. Narrow your target to get depth.
</WHY>

<WHEN_TO_USE>
- To reading files in the repository, or other imported repositories, in a token efficient manner
- To inventory what exists in a directory
- To read multiple files, classes, methods or chunks
- To view git history, blame, or working-copy changes — for files, symbols, or line ranges
</WHEN_TO_USE>

<BEST_PRACTICE>
- Be frugal and intentional with your token budgets
- Pick the smallest representation that gives you what you need.
- Read full content only after you have found your target, ideally at symbol level
- If you need to read multiple things, use globbing or combine uris with ; over multiple tool calls
- If you don't know the location, use explore or explain to locate, then dive in
</BEST_PRACTICE>

<MODIFIERS>
Append ` => modifier` to change what you see given the uri glob (content representations, history, etc)
The URI pattern always controls scope — narrow it to get depth, widen it to get breadth.

**structure**: Signatures without bodies—see the shape without reading code.
→ `file:///src/Auth/**/*.cs => structure` — shape of an entire subsystem in one call
→ Combines with symbol wildcards: `file:///src/**/*.cs#symbol=*Controller => structure` - signature of all controllers

**tree**: Directory structure with progressive detail.
→ `=> tree: folders` — just directories with file counts (cheapest)
→ `=> tree: files` — directories + filenames (default)
→ `=> tree: headlines` — directories + files + one-line summaries

**headline**: One-line summary per file, flat list (no tree structure).

**content**: Full file with line numbers (explicit default). Resolvable `@` references assemble in place. Use `=> content: literal` to read the source view without assembly, or `=> content: preview` to see the transclusion manifest and projected cost without assembling target content.

**where**: Resolve matched files or symbols to local and remote addresses.
→ `file:///src/Auth.cs#symbol=ValidateToken => where` — local path plus GitHub URL when resolvable
→ `=> where: local` — local absolute file path only
→ `=> where: remote` — remote URL only, with a GitHub line anchor when applicable

**history**: Git commits affecting matched files.
→ `=> history` — all commits, newest first
→ `=> history: auth refactor` — ranks commits by relevance to keywords (doesn't filter)
→ Globs show cross-file history: `file:///src/Auth/** => history: token validation`
→ Fragments narrow to a span: `#symbol=ValidateToken => history` — the commits that built that method's current text

**blame**: Line-by-line git attribution. Fragments target precisely.
→ `file:///src/Auth.cs => blame` — full file attribution
→ `file:///src/Auth.cs#symbol=ValidateToken => blame` — just that function's history
→ `file:///src/Auth.cs#line=42,60 => blame` — specific line range
→ Broad unfragmented scopes are rejected; narrow first.

**changes**: Working copy changes grouped by changelist (staged, unstaged, untracked).
→ Shows diffs for modified files, binary markers, and line counts

**lint**: Diagnostics from matched files.
→ `=> lint` — all diagnostics
→ `=> lint: errors` — errors only
→ `=> lint: warnings` — warnings and errors
→ Globs aggregate: `file:///src/** => lint: errors` — all errors across the project

**coverage**: Coverage annotations from matched files.
→ `=> coverage` — per-symbol covered, uncovered, and unmeasured summary
→ `=> coverage: uncovered` — only uncovered gaps
→ `=> coverage: content` — source with an execution-hit gutter and uncovered lines flagged

**find**: Semantic search within matched files.
→ `=> find: keywords` — ranks content by relevance, shows snippets
→ Has quality threshold—won't show junk matches
→ The URI pattern controls where you search: `file:///src/tests/** => find: token validation`

**similar**: Stored-vector similarity from a seed URI into a scope.
→ `file:///src/tests/** => similar: file:///src/Auth/TokenService.cs` — tests most like that implementation
→ `file:///docs/** => similar: file:///src/Auth/TokenService.cs` — docs most like that code
→ The URI pattern is the candidate set; the parameter is the seed

**grep**: Case-insensitive literal text search within matched files.
→ `=> grep: validateToken` — every line containing the string, with context
→ Scope narrows the haystack: `file:///src/Auth/** => grep: connectionString`

**regex**: Regular-expression line search within matched files.
→ `=> regex: class\s+\w+Handler` — every matching line, grouped by file
→ Use when the shape matters; use `grep` for literal substrings

**question**: LLM synthesis with citations.
→ `=> question: How does X work?` — reads matched content, synthesizes answer
→ Returns Answer, Evidence (with file:///path#line=N,M citations), Nuance
→ Focused scopes get direct LLM answers. Wide scopes automatically defer to search+synthesis.
→ Always verify citations before trusting
→ Use in parallel to scout and understand areas
</MODIFIERS>

<PATTERNS>
URIs can target precisely or match broadly.

**Fragments** pinpoint within files:
→ `#symbol=ValidateToken` — exact symbol (fully qualified name matched)
→ `#symbol=AuthService.*` — all direct members of a class
→ `#symbol=AuthService.**` — all descendants (nested types too)
→ `#line=42` — single line
→ `#line=42,100` — line range (inclusive, 1-based)

**Globs** select many files — the full shell/ripgrep/git vocabulary:
→ `file:///src/**/*.cs` — `*` single segment, `**` any depth
→ `file:///src/{L1,L2,L3}/**/*.{cs,md}` — brace expansion (alternation, nesting, cartesian)
→ `file:///src/File[0-9].cs` — POSIX character classes
→ `file:///src/???.cs` — `?` single char

**Combining and excluding**:
→ `a;b;c` — semicolon alternation (match any)
→ `!pattern` — exclude from all includes
→ `file:///src/**;!**/tests/**` — source without tests

**Symbol wildcards across files** (powerful):
→ `file:///src/**/*Handler.cs#symbol=*Handler.CanHandle` — all CanHandle methods
→ `file:///src/**/*Service.cs#symbol=*Service.*` — all members of all services

**Multiple specific symbols** (from explore results):
→ `file:///a.cs#symbol=Foo;file:///b.cs#symbol=Bar;file:///c.cs#symbol=Baz`
→ One call, just those three function bodies
</PATTERNS>

<BUDGET>
Budget is how many tokens you're willing to spend on the answer. This is a bet—you don't know exactly what you'll get.

Be VERY VERY intentional about what number you pick. Tokens are money, overspending is expensive, underspending means your understanding is incomplete. Start low and be frugal. Assign the number of tokens you are prepared to spend on the context.

If you dont pick an explicit representation via modifiers, repoql will downsample the representation to fit

Consider the stakes: if missing context has serious consequences, bet more. Most of the time - bet small and iterate.
</BUDGET>

<QUICK_PATTERNS>
Orient in new codebase:
→ read("file:///** => tree: folders", 5000)

Understand what's in a directory:
→ read("file:///src/Services/** => tree: headlines", 2000)

Read a specific file:
→ read("file:///src/Auth.cs", 2000)

Read just one function:
→ read("file:///src/Auth.cs#symbol=ValidateToken", 800)

Understand a class:
→ read("file:///src/Auth.cs#symbol=AuthService => structure", 1000)

Read multiple symbols
→ read("file:///src/Auth.cs#symbol=Auth;file:///src/Foo.cs#symbol=Foo;file:///src/Baz.cs#symbol=Baz.DoThing", 1500)

Read all getter members of a class:
→ read("file:///src/Auth.cs#symbol=AuthService.Get*", 3000)

Read same method across multiple files:
→ read("file:///src/**/*Handler.cs#symbol=*Handler.ExecuteAsync", 2000)

Combine specific symbols from explore:
→ read("file:///a.cs#symbol=Foo;file:///b.cs#symbol=Bar", 2000)

Shape of a subsystem:
→ read("file:///src/Auth/**/*.cs => headline", 800)
→ read("file:///src/Auth/**/*.cs => structure", 3000)

Who wrote this function:
→ read("file:///src/Auth.cs#symbol=ValidateToken => blame", 1500)

Where is this function on disk or GitHub:
→ read("file:///src/Auth.cs#symbol=ValidateToken => where", 1500)

Commits relevant to a topic:
→ read("file:///src/Auth/** => history: token refresh", 2000)

What's pending in working copy:
→ read("file:///src/Auth/** => changes", 2000)

Semantic search within a scope:
→ explore(keywords="token service related implementations", uriGlob="file:///src/**/*.cs")
→ read("file:///src/tests/** => find: token validation", 2000)

Find things like this, over there:
→ read("file:///src/tests/** => similar: file:///src/Auth/TokenService.cs", 2000)

Find exact text in files:
→ read("file:///src/Auth/** => grep: connectionString", 2000)

Find patterns in files (.net regex):
→ read("file:///src/**/*.cs => regex: class\s+\w+Handler", 2000)

Ask a focused question about specific code:
→ read("file:///src/Auth/TokenService.cs => question: How is token refresh implemented?", 2500)

More:
- `help://tools/read.md`
- `help:///tools/read-transclusion.md`
- `help:///tools/glob-patterns.csv` — full glob vocabulary
- `help:///tools/uri-patterns.md` — URI fragments, symbol patterns, line ranges
</QUICK_PATTERNS>

<GUIDANCE>
- Combine reads using ; when you need to read multiple things
- Reach for structure first if you know what you are looking for, tree/explore if you don't. Content only when you need to see it all
- Glob across symbols: `file://**#symbol=get*` finds every getter in the codebase
- Combine URIs with `;` for multi-target reads: `a.cs#symbol=Foo;b.cs#symbol=Bar`
- `similar` does strange and powerful things with creative scoping — find tests, docs, or near-duplicates by changing the URI scope while keeping the seed
- If what you want to read isnt in the graph, import it using the import tool.
</GUIDANCE>
