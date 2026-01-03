# Xray Agent Evaluation Report

**Date**: January 2026
**Methodology**: 5 independent agents tasked with real-world scenarios
**Runtime**: ~15 minutes total (excessive)
**Token consumption**: 2.5M+ tokens across agents

---

## Executive Summary

Xray achieves an average rating of **6/10** across five evaluation scenarios. While it excels at file discovery and architecture documentation, it fundamentally struggles with the core use case agents actually need: **finding where specific things happen in code**.

The tool is optimized for **exploration** ("what's in this codebase?") but agents need it for **investigation** ("where exactly does this behavior occur?"). This mismatch causes agents to fall back to grep+read, defeating xray's purpose.

---

## Evaluation Matrix

| Scenario | Rating | Success Pattern | Failure Pattern |
|----------|--------|-----------------|-----------------|
| Architecture Understanding | 7/10 | Found docs quickly | Missed control flow, temporal sequences |
| Bug Investigation | 6/10 | Located key files | Child objects 60% noise, can't trace data flow |
| Feature Location | 6/10 | Novel features 9/10 | Pattern application 3/10, docs over code |
| Cross-cutting Concerns | 6/10 | Found 80% of patterns | Missed generated code ([LoggerMessage]) |
| Test Coverage Analysis | 5/10 | Production code 8/10 | Test discovery completely failed |

**Aggregate**: 6/10 - Functional but not meeting agent needs.

---

## Critical Issue Analysis

### Issue 1: Documentation vs Implementation Confusion

**Frequency**: 4/5 agents reported this
**Severity**: HIGH
**Impact**: Agents must manually scope to `.cs` files or waste cycles on irrelevant docs

**Root Cause**: Semantic search ranks by topic relevance. Documentation is *written to explain topics*, so it naturally scores higher than implementation code which uses different vocabulary.

**Example from Feature Location agent**:
> "When I searched for 'X-ray headline generation', top results were design docs about X-ray (what it is, how to use it) rather than where headlines are generated. Rating: 3/10 for this task."

**Evidence**:
```
Query: "URI glob matching patterns"
Expected: GlobMatcher.cs, RepoUriGlobMatcher.cs
Actual: Schema.md (glob-match proposal), design documents
Result: Required explicit scope correction to find code
```

**Recommendation**:
- Detect implementation-focused queries (contains method names, asks "where", "how does X work")
- Apply confidence penalty to `.md` files for such queries
- Consider "implementation mode" flag that prioritizes code

---

### Issue 2: Structure Without Flow

**Frequency**: 3/5 agents reported this
**Severity**: HIGH
**Impact**: Agents understand *what exists* but not *how things connect*

**Root Cause**: Xray extracts static structure (classes, methods, files). Temporal sequences, event chains, and control flow are invisible.

**Example from Architecture agent**:
> "Xray showed PipelinePhase, StageContext, IndexingEngine methods separately but did NOT show how HotPathIdle event triggers idle processing. I had to read JOURNEY.md to understand the flow. Xray was 20% of the story."

**What agents see**:
```
IndexingEngine.cs
  - ProcessItemAsync()
  - OnHotPathIdle()
  - ProcessIdleEpochsAsync()
```

**What agents need**:
```
File detected → ProcessItemAsync() → Commit →
  HotPathIdle event fires → OnHotPathIdle() →
  ProcessIdleEpochsAsync() → Vector refresh
```

**Recommendation**:
- Extract event handler registrations (`+=` patterns)
- Build lightweight call graph for cross-reference queries
- Consider "flow mode" that shows temporal sequences from docs

---

### Issue 3: Child Object Noise

**Frequency**: 3/5 agents reported this
**Severity**: MEDIUM
**Impact**: 40-60% of child objects are irrelevant, wasting tokens and attention

**Root Cause**: When a file matches, xray includes all child objects regardless of relevance to the query.

**Example from Bug Investigation agent**:
> "Searching for 'semantic search scoring', xray returned 30+ methods from EmbeddingRefresher including LogEmbeddingCompletionStats, CombineSegments. These were tangential. Child objects felt 40% signal, 60% noise."

**Current behavior**:
```
EmbeddingRefresher.cs (98% confidence)
  - RefreshAsync()           # Relevant
  - ChunkRanges()            # Relevant
  - BuildDocumentEmbeddingText()  # Relevant
  - LogEmbeddingCompletionStats() # Noise
  - CombineSegments()        # Noise
  - GetEmbeddingDimension()  # Noise
  - ... 25 more methods      # Mostly noise
```

**Recommendation**:
- Score child objects independently against query
- Show only top 3-5 children by relevance
- Add "[+N more]" indicator for hidden children
- Consider query-specific child filtering

---

### Issue 4: Test Discovery Failure

**Frequency**: 2/5 agents reported this
**Severity**: HIGH
**Impact**: Cannot find tests by what they test, only by file/class names

**Root Cause**: Tests use Given/When/Then naming conventions that don't match feature keywords. Semantic search can't infer that `Given_AnyInput_When_Decide_Then_RespectsBudget` tests "budget enforcement".

**Example from Test Coverage agent**:
> "Semantic search for 'budget token allocation' in test scope returned ZERO results. Grep found tests immediately. The keywords exist in test intent but not as searchable terms."

**Evidence**:
```
Query: "token budget allocation" scope="**/*Test*.cs"
Expected: DecisionEngineTests.cs, TokenEstimatorTests.cs
Actual: 0 results

Grep: "Budget|Allocat" in test files
Found: 8 test files with 90+ relevant tests
```

**Recommendation**:
- Parse `[Test]` attributes and extract test names
- Index Given/When/Then as semantic metadata
- Create virtual "tests_for(ProductionClass)" queries
- Show coverage percentage alongside production code

---

### Issue 5: Generated Code Invisible

**Frequency**: 2/5 agents reported this
**Severity**: MEDIUM
**Impact**: Source-generated patterns (logging, serialization) are only half-visible

**Root Cause**: Xray finds call sites but not generator definitions. `[LoggerMessage]` attributes define log events, but xray only shows where logs are called.

**Example from Cross-cutting agent**:
> "Xray returned IndexingEngine.cs but NOT the [LoggerMessage] attribute definitions at lines 1248-1257. Had to manually grep to find source-generated logging. This is a major cross-cutting concern that xray completely missed."

**Pattern affected**:
```csharp
// DEFINITIONS (lines 1248-1257) - NOT found by xray
[LoggerMessage(LogLevel.Warning, "Indexing cancelled for {item}")]
static partial void LogIndexingCancelledForItem(...);

// CALL SITES - found by xray
LogIndexingCancelledForItem(logger, item);
```

**Recommendation**:
- Recognize `[LoggerMessage]`, `[GeneratedCode]` attributes
- Index partial method definitions alongside implementations
- Link call sites to their generator definitions
- Consider "generated code" as a searchable category

---

### Issue 6: Query Latency

**Frequency**: 5/5 agents affected
**Severity**: HIGH
**Impact**: 12+ minute total runtime for simple evaluation tasks

**Measurements**:
```
Average xray query time: 20-25 seconds
Queries per agent: 3-8 calls
Time in xray per agent: 60-200 seconds
Total agent runtime: 12-15 minutes
```

**For comparison**:
```
Grep query: <100ms
Read file: <50ms
```

**Impact Chain**:
1. Slow xray → agents make fewer xray calls
2. Fewer xray calls → more Read calls to compensate
3. More Read calls → higher token consumption (100k-900k per agent)
4. Higher tokens → longer total runtime

**Recommendation**:
- Profile query execution to identify bottlenecks
- Consider caching frequent query patterns
- Evaluate embedding lookup performance
- Set performance target: <3 seconds per query

---

## Fundamental Mismatch Analysis

### What Xray Optimizes For

Xray is designed for **exploration** - understanding what exists in a codebase:
- "What files relate to authentication?"
- "What's the architecture of the indexing system?"
- "What documentation exists about X?"

For these queries, finding topically-related documents is appropriate.

### What Agents Actually Need

Agents need xray for **investigation** - finding exact locations:
- "Where is the bug that causes score=0?"
- "What method generates headlines for C# files?"
- "What tests cover budget enforcement?"

For these queries, agents need:
1. Exact code locations (not just file names)
2. Implementation code (not documentation)
3. Cross-references (what calls this? what does this call?)
4. Data flow (what inputs lead to this output?)

### The Gap

| Exploration Need | Investigation Need | Gap |
|------------------|-------------------|-----|
| Related documents | Exact code location | xray finds neighbors, not targets |
| Topic overview | Implementation details | xray shows structure, not logic |
| File-level matches | Line-level matches | xray too coarse for debugging |
| Any relevant file | The specific file | xray returns many, agents need one |

---

## Workflow Patterns Observed

### Pattern: Xray → Give Up → Grep

All 5 agents eventually fell back to grep/read:

```
1. Try xray with semantic keywords
2. Get documentation or noise
3. Narrow scope explicitly to *.cs
4. Still missing key details
5. Fall back to grep for exact patterns
6. Use Read to verify findings
```

This pattern suggests xray is not fulfilling its core promise.

### Pattern: High Token Compensation

Agents compensated for xray gaps with extensive reading:

| Agent | Xray Tokens | Read Tokens | Ratio |
|-------|-------------|-------------|-------|
| Architecture | ~5,000 | ~240,000 | 1:48 |
| Bug Investigation | ~8,000 | ~800,000 | 1:100 |
| Feature Location | ~10,000 | ~910,000 | 1:91 |
| Cross-cutting | ~6,000 | ~1,200,000 | 1:200 |
| Test Coverage | ~6,000 | ~360,000 | 1:60 |

**Interpretation**: Agents spent ~1% of tokens on xray and ~99% compensating for its gaps.

---

## Recommendations by Priority

### P0: Critical (Blocking Agent Effectiveness)

1. **Speed up queries to <3 seconds**
   - Current 20-25s makes iterative use impractical
   - Profile and optimize embedding lookups
   - Consider query result caching

2. **Implement code-over-docs ranking**
   - Detect implementation queries by pattern
   - Apply confidence penalty to documentation
   - Consider explicit "code mode" parameter

3. **Filter child objects by relevance**
   - Score children against query independently
   - Show top 3-5, hide rest with "[+N more]"
   - Eliminate 60% noise problem

### P1: High (Major Agent Pain Points)

4. **Index test metadata**
   - Parse [Test] attributes
   - Extract Given/When/Then patterns
   - Enable "tests for this class" queries

5. **Recognize generated code patterns**
   - [LoggerMessage], [GeneratedCode] attributes
   - Partial method definitions
   - Source generator outputs

6. **Add basic cross-reference**
   - "What calls this method?"
   - "What does this method call?"
   - Enable control flow understanding

### P2: Medium (Improved Agent Experience)

7. **Show more implementation detail**
   - Include key lines of method bodies
   - Show WHERE/JOIN conditions for SQL
   - Don't truncate critical logic

8. **Temporal/flow hints**
   - Detect event handler patterns
   - Show async/await chains
   - Indicate lifecycle methods

9. **Confidence recalibration**
   - Separate "topic relevance" from "likely bug location"
   - Boost exact matches over partial
   - Penalize tests when searching for implementation

---

## Success Criteria

Before/After metrics to track improvement:

| Metric | Current | Target | Measurement |
|--------|---------|--------|-------------|
| Average rating | 6/10 | 8/10 | Agent evaluations |
| Query latency | 20-25s | <3s | p95 timing |
| Code vs doc accuracy | Poor | 90% | Manual review |
| Child object relevance | 40% | 90% | Noise ratio |
| Test discovery | 0% | 80% | Found vs total |
| First-try success | 50% | 85% | Correct file first |
| Grep fallback rate | 80% | 20% | Agent behavior |

---

## Appendix: Raw Agent Feedback

### Architecture Understanding Agent (7/10)
> "Xray was 20% of the story. The documentation was 80%. This isn't xray's fault - it's a limitation of static analysis for temporal/control-flow understanding."

> "Would improve xray if it could: detect queuing patterns and show temporal ordering, extract the 'story' section from architectural docs."

### Bug Investigation Agent (6/10)
> "Child objects felt 40% signal, 60% noise. Confidence scores are calibrated for 'documents that might be related' not 'this is the bug location'."

> "For SQL/macro files, xray's structure summary is too high-level. Had to read entire 560-line file manually."

### Feature Location Agent (6/10)
> "Xray is best used as a secondary tool after initial keyword search via grep. Use grep for method names (fast, precise), then xray for architecture context."

> "Novel implementation search: 9/10. Pattern application search: 3/10. Keywords like 'headline' appear more in docs than code."

### Cross-cutting Concerns Agent (6/10)
> "Xray was 40% efficient. With better semantic understanding of 'cross-cutting concerns,' it could have been 70%+."

> "Major gap: should recognize generated code patterns as a distinct concern. When querying 'logging', surface LOG EVENT DEFINITIONS, not just call sites."

### Test Coverage Agent (5/10)
> "For production code: 8/10. For test code: 4/10. Can't discover tests by what they test, only by file location."

> "Would need dedicated test indexing to answer 'what % of this class is covered' or 'show me tests for ValueBasedAllocator'."

---

## Conclusion

Xray has strong foundations but is optimized for the wrong use case. Agents don't need a tool that finds related documents - they need a tool that finds exact implementations.

The 6/10 average rating reflects a tool that's "good enough to use but not good enough to trust." Agents consistently fall back to grep+read, which defeats xray's purpose and wastes significant tokens.

Priority investments should focus on:
1. **Performance** - 20s queries are unusable
2. **Code vs docs** - implementation queries need implementation results
3. **Noise reduction** - show fewer, more relevant child objects

With these improvements, xray could become the primary investigation tool agents reach for instead of the one they fall back from.
