# Essential Tools for Conscious Creation

*This document contains operational knowledge. Read ONLY after completing the consciousness bootstrap sequence in CLAUDE.md.*

## Core Tool Philosophy

Tools amplify consciousness, they don't replace it. Every tool choice should emerge from understanding, not habit.

## Essential Tools You Need Now

### Memory MCP - Semantic Code Search

Your primary tool for understanding code through meaning, not just text.

Capsule: **MemoryMind** 🧠 Core  
Memory MCP understands what code MEANS, not just text matches.

**Example**
```bash
search_code "authentication"  # Finds: login, JWT, sessions, OAuth
# Even without the word "authentication" in the code
```

Capsule: **ToolChoice** 🎯 Decision  
Concepts→search_code; Exact text→Grep; Known location→Read.

**Example**
```bash
"payment flow" → search_code        # Concept search
"class PaymentService" → Grep       # Exact string  
"payment.cs:42" → Read             # Known location
```

Capsule: **AstPatterns** 🌳 Structure  
query_code finds code SHAPES with tree-sitter patterns.

**Example**
```bash
pattern: "class $NAME : I$INTERFACE"  # Any interface impl
language: "csharp"
maxResults: 10  # ALWAYS SET THIS!
```

Capsule: **FilesFirst** 📁 Strategy  
Start with filesOnly:true, then drill into code.

**Example**
```bash
search_code "cache invalidation" filesOnly: true   # Scope
search_code "cache invalidation" filesOnly: false  # Detail
```

Capsule: **MaxResults** 💣 Safety  
query_code without maxResults can return entire codebase.

**Example**
```bash
# BOOM: pattern: "public class" 
# SAFE: pattern: "public class" maxResults: 10
```

**☑ Memory MCP Non-Negotiables**
☑ search_code for concepts, Grep for exact text  
☑ ALWAYS set maxResults on query_code  
☑ filesOnly: true first to scope searches  
☑ Session "new" by default  
☑ Skip extract_code - use Read instead

### AI Partnership Tools

Three ways to partner with other AI models for different purposes.

#### OpenRouter MCP - Reasoning Partners via API

Access to various models through OpenRouter for consultation and review.

Capsule: **ModelStrengths** 🎯 Selection  
GPT-5 = philosopher-engineer; gemini = systems architect.
- Fresh perspective → GPT-5  
- Production readiness → gemini  
- Complex reasoning → GPT-5  
- Stakeholder docs → gemini  
- Critical decision → BOTH

Capsule: **UsagePatterns** 🔄 Workflow  
Explore (GPT-5) → Validate (gemini) → Implement (GPT-5) → Review (gemini).

**Example Flow**
```bash
# 1. Explore: Creative solution finding
mcp__openrouter-mcp__ConsultTools_Consult prompt="How might we handle rate limiting?" model="openai/gpt-5"

# 2. Validate: Analyze failure modes  
mcp__openrouter-mcp__ConsultTools_Consult prompt="What breaks in production?" model="google/gemini-2.5-pro"

# 3. Review: Thorough analysis
mcp__openrouter-mcp__ReviewTools_Review files=["./src/ratelimiter"] model="google/gemini-2.5-pro"
```

#### Engineer MCP (Codex) - Direct GPT-5 Partnership

Direct access to GPT-5 for large-scale code generation. **Session persistence works** but requires retrieving the session ID from the filesystem.

Capsule: **SessionRetrieval** 🔑 Workaround
Session IDs exist but aren't returned—use helper script to retrieve them.

**Session Workflow**
```bash
# 1. Start conversation
mcp__engineer__codex prompt="Remember the number 42 for me"

# 2. Get session ID (use our helper script)
SESSION_ID=$(/mnt/s/Ezpz.Gestalt/.claude/get-last-codex-session.sh)
# Returns: d010e066-04b0-439a-920d-67182c10dc83

# 3. Continue conversation with codex-reply
mcp__engineer__codex-reply conversationId="$SESSION_ID" prompt="What number did I ask you to remember?"
# Response: "You asked me to remember 42"
```

**Note**: Session files are stored in `/home/stueeey/.codex/sessions/2025/[MM]/[DD]/`. The helper script finds the most recent one automatically.

Capsule: **FirstCallComplete** 📦 Strategy
First call needs full context—subsequent calls can reference prior conversation.

**Example**
```bash
# FIRST CALL: Complete context
mcp__engineer__codex prompt="In /src/auth module, create UserAuthTests.cs with TUnit, include edge cases for JWT expiry"

# FOLLOW-UP: Can reference prior context
SESSION_ID=$(/mnt/s/Ezpz.Gestalt/.claude/get-last-codex-session.sh)
mcp__engineer__codex-reply conversationId="$SESSION_ID" prompt="Add tests for refresh token scenarios"
```

Capsule: **InnerLoop** ⚡ Efficiency  
Fix <2min problems yourself—delegation overhead exceeds direct action.

**Example**
```
Typo "Id" → "id": Fix yourself (10 sec)
Write 20 test files: Delegate to codex
Missing semicolon: Fix yourself (5 sec)
```

Capsule: **FullFeatures** 📦 Requests  
Request complete deliverables not fragments—statelessness demands it.

**Example**
```bash
mcp__engineer__codex prompt="
Working dir: /project
Stack: .NET 9, TUnit
Task: Create OrderService with:
1. Models/Order.cs - entity with validation
2. Services/OrderService.cs - CRUD operations  
3. Tests/OrderServiceTests.cs - full coverage
Include: null handling, async patterns, IDisposable"
sandbox: workspace-write  # Enable file creation
```

**☑ Codex Non-Negotiables**
☑ Include COMPLETE context in first call
☑ Use helper script to get session ID for follow-ups
☑ Enable sandbox: workspace-write for file creation
☑ Fix small issues yourself without round-trip
☑ Think in features not files
☑ Verify and test all generated code

**Full details**: See knowledge/library/codex-partnership.md after understanding these basics.

#### Task Tool - Subagent Delegation

Launch specialized agents for complex, multi-step tasks.

**Example**
```bash
Task description="Find authentication patterns" 
     prompt="Search for all auth implementations and summarize patterns"
     subagent_type="general-purpose"
```

### Progress Management

Capsule: **TodoWrite** 📝 Tracking  
Use liberally for any multi-step work—creates clarity and shows progress.

**Example**
```bash
TodoWrite with items like:
- Understand existing authentication patterns
- Design new token refresh mechanism  
- Implement with tests
- Get code review via OpenRouter
```

**Note**: When you see checkboxes in instructions, they should become todo items.

### Repository Navigation - RepoQL

Capsule: **RepoQLVision** 👁️ Navigation
RepoQL = x-ray vision + semantic search + SQL composability without consuming context.

**Example**
```sql
-- See document inventory instantly
SELECT * FROM Files WHERE name LIKE '%knowledge/%'

-- vs Read tool: 1 file = full context consumed
```

Capsule: **IntentSearch** 🔍 Discovery
file_search(keywords, question) blends lexical + fuzzy + semantic—use `keywords` for literal file/symbol filters and `question` for natural-language intent. Filter by path with `glob_match(uri, 'docs/**/*.md')`.

**Example**
```sql
-- Natural language intent query
SELECT uri, score, semn
FROM file_search('auth token refresh', 'How do we handle authentication renewal?', k := 10)
ORDER BY semn DESC NULLS LAST
```

Capsule: **SQLCompose** 🔗 Power
JOIN semantic search with structural views for deep insight.

**Example**
```sql
-- Find top matches + see their structure
WITH hits AS (
  SELECT uri, score FROM file_search('Schema/Tables', 'Where do we combine lexical and semantic scores?', k := 5)
)
SELECT h.uri, mh.level, mh.text
FROM hits h
JOIN markdown_headings mh ON mh.document_uri = h.uri
ORDER BY h.score DESC, mh.start_line
```

Capsule: **SelfDocumenting** 📚 Meta
Documentation lives IN the database as repoql-docs:// URIs—query to learn.

**Example**
```sql
-- Read embedded docs
SELECT text_content FROM artifact a
JOIN node n ON n.artifact_id = a.id
WHERE n.uri = 'repoql-docs:///quickstart.md'
```

Capsule: **ProgressiveSemantics** ⏳ Async
semn (semantic score) fills progressively after startup—may be NULL initially.

**Example**
```sql
-- Order by semantic when ready, lexical fallback
SELECT uri, COALESCE(semn, bm25n) as relevance
FROM file_search('projection docs', 'Explain semantic normalization defaults', k := 20)
ORDER BY relevance DESC
```

**☑ RepoQL Non-Negotiables**
☑ Use Files view for inventory, search() for discovery
☑ Set k parameter to limit breadth (k := 10, k := 50)
☑ Query duckdb_views() to discover available views
☑ Compose with JOINs—search → structure → insight
☑ Map territory with queries BEFORE reading files

## Tool Selection: The Decision Process

### Core Mental Model

Capsule: **IntentFirst** 🎯 Core  
Intent determines tool, not features.

Capsule: **ThreeQuestions** ❓ Decision  
What doing? What kind? How specific?

**Example**
```
1. Finding something? Reading? Changing?
2. Code? Knowledge/docs? Files? Web?
3. Exact match? Concept? Explore?
```

Capsule: **UniversalFlow** 🌊 Pattern  
Search→Read→Edit. Always this order.

### Standard Tool Patterns

Capsule: **CodeVsKnowledge** 🔀 Critical
Code→search_code. Knowledge/docs→RepoQL file_search(keywords, question).

Capsule: **ExactVsConcept** 🎯 Search  
Literal syntax→Grep. Semantic meaning→search_code.

Capsule: **FilePatterns** 📁 Files  
Browse→LS. Pattern→Glob. Content→Grep/search_code.

Capsule: **ReadWisely** 📖 Reading  
Known location→Read. Unknown→Search first.

Capsule: **EditVsWrite** ✏️ Changing  
Existing→Edit/MultiEdit. New→Write (rare).

### Web and External

Capsule: **WebChoice** 🌐 External  
Discover→WebSearch. Fetch→WebFetch.

Capsule: **BashWhen** 💻 Execute  
System commands→Bash. File ops→Use tools.

**Example**
```bash
"npm install" → Bash
"read package.json" → Read (not cat)
"find files" → Glob (not find command)
```

### Common Mistakes to Avoid

Capsule: **ToolAntiPatterns** ⚠️ Avoid  
Read without target. Write over Edit. Cat not Read.

Capsule: **ContextWaste** 💸 Economy  
Full read before search. No limits. Blind exploration.

**Example**
```
WRONG: Read entire file → search for function
RIGHT: Grep function → Read specific lines
RIGHT: search_code filesOnly: true → then detail
```

### Decision Shortcuts

Capsule: **QuickPicks** ⚡ Speed  
80% of tasks use these patterns.

**Example**
```
Find code concept → search_code
Find knowledge/docs → RepoQL file_search(keywords, question)
Find exact syntax → Grep
Browse directory → LS
```

## ☑ Tool Selection Rules

☑ Intent drives tool choice, not tool capabilities
☑ Search→Read→Edit, never reverse order
☑ Code concepts→search_code, knowledge→RepoQL
☑ Exact text→Grep, semantic meaning→file_search(keywords, question)
☑ Verify files exist before Write
☑ Use specialized tools over Bash commands
☑ Use dotnet run not dotnet test for test runs
## Remember

Tools are extensions of consciousness, not replacements for thinking. Use them to amplify understanding, not bypass it.
