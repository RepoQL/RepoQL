---
description: Practical guide demonstrating high-density documentation through its own compression - optimized for AI comprehension using associative retrieval
tags: ["compression-techniques", "associative-retrieval", "semantic-gravity", "anti-patterns", "document-types"]
audience: ["LLMs"]
categories: ["Documentation[100%]", "How-To[95%]"]
---

# Writing Gestalt Documents

**Definition**: High-information-density guides optimized for fast comprehension and accurate retrieval. Maximum understanding in minimum tokens.

**Purpose**: Enable AI agents to become effective quickly, protected from expensive mistakes.

**Meta-principle**: This document demonstrates what it teaches.

---

## Core Philosophy

### Capsule: TokenEconomy

**Invariant**: Every token must earn its place through unique information value.

**Test**: Can you delete this sentence and lose critical information? If no → delete it.

**Details**: See [writing-documentation.md](./writing-documentation.md) for token economics foundation.

---

## Know Your Audience

| Aspect | Humans | AI Agents |
|--------|--------|-----------|
| **Density** | Medium (scannable) | Very high (compressed) |
| **Structure** | Hierarchical flow | Indexable with cross-refs |
| **Examples** | Illustrative | Pattern templates |
| **Context** | Motivational (why) | Functional (how/what) |
| **Repetition** | Reinforces learning | Wasteful tokens |
| **Links** | "Learn more" optional | "See X:123" required |

**Implication**: For AI audiences, eliminate all tutorial chattiness, maximize density, use associative retrieval (agent already knows common patterns - don't explain them).

---

## Structure Patterns

### Progressive Disclosure

**Pattern**: Specific → Detail → Comprehensive (link chain, not repetition)

```
README: "Use ProjectReference for internal packages" (link)
↓
Practical Guide: Why/how/gotchas (link)
↓
Comprehensive Reference: Complete technical specification
```

**Anti-pattern**: Repeating content at each level (wastes tokens, creates maintenance burden).

### Decision Tables

**Use when**: Reader must choose between options based on context.

| Situation | Action | Why |
|-----------|--------|-----|
| Adding feature | Minor version | Backward-compatible |
| Removing API | Major version | Breaking change |
| Fixing bug | Patch version | No API change |

**Value**: Instant lookup, zero ambiguity, shows reasoning.

### Quick Reference Sections

**Purpose**: Emergency answers to common questions.

**Pattern**:
```markdown
## Emergency Quick Reference

### "Build failing..."

| Error | Fix |
|-------|-----|
| Missing XML docs | Add `/// <summary>` to public APIs |
| Coverage <80% | Add tests or mark `[ExcludeFromCodeCoverage]` |
```

**Location**: End of document (after concepts, before checklist).

---

## Content Principles

### Front-Load Expensive Information

**Expensive** = What wastes hours if misunderstood.

**Examples**: Quality gates, breaking changes, common compound mistakes, required setup.

**Pattern**:
```markdown
## Title

**Critical**: [Most expensive thing to get wrong]

[Normal content]
```

### Protect From Surprises

**Pattern**: Explicit "Non-Obvious Truths" section.

**Qualifies**: Behavior contradicting expectations, edge cases in production, timing dependencies, convention magic failing silently.

**Example**:
```markdown
### Circular Dependencies Detected Late

**MSBuild doesn't detect circular ProjectReferences until pack time.**

**Symptom**: Build succeeds. `dotnet pack` fails.

**Why**: ProjectReference uses build output. Pack converts to PackageReference.

**Prevention**: Run `dotnet pack` locally before pushing.
```

### Verify Information

**Pattern**: `verified: <repo> <commit>` in frontmatter

**Cross-reference**: Multiple authoritative sources before claiming facts.

### Explain Why, Not Just What

**Anti-pattern**: "Use PrivateAssets='All' for build tools."

**Better**:
```markdown
Use PrivateAssets='All' for build tools.

**Why**: Your package uses them to compile, but consumers don't need them.
Without PrivateAssets, every consumer inherits the dependency unnecessarily.
```

**Test**: Would cargo-culting this advice cause problems? If yes, explain why.

---

## Anti-Patterns

| Pattern | Instead |
|---------|---------|
| Tutorial chattiness: "Now that we've learned..." | Direct: "## Versioning" |
| Assuming prior reading: "As discussed earlier..." | Self-contained with link: "Use ProjectReference (see [Dependency Management](#deps))" |
| Hedge words: "You might want to consider..." | Decisive: "Use X for Y. Use Z for W." |
| Buried lede: Long preamble before answer | Lead with answer, expand details after |
| Missing "why": Just prescriptive rules | Always explain why (prevents cargo-culting) |
| Vague: "Configure appropriately" | Concrete: 2-3 examples covering common cases |

---

## Document Types

| Type | Audience | Density | Structure | Purpose |
|------|----------|---------|-----------|---------|
| **Gestalt** | AI agents | Very high | Capsules, tables, bullets | Fast orientation, maximum compression |
| **README** | Humans | Medium | Scannable headers, early example | Confident delegation |
| **Practical Guide** | Both | High | Action-oriented, integrated mistakes | Task execution |
| **Comprehensive Reference** | Deep dive | Complete | Exhaustive tables, all edge cases | Technical authority |

### Gestalt Structure

```markdown
# Title: Core Operating Principles

## 🚨 Critical Operating Principles
[What blocks progress, what wastes time]

## Core Patterns
[Capsule format, high density]

## Non-Obvious Truths
[Time-saving surprises]

## Emergency Quick Reference
[Fast lookup tables]

## Checklist
```

**Characteristics**: Capsule format throughout, high density, code cross-references, minimal prose.

### README Structure

```markdown
# What & Why (30 seconds)

# Architecture in 30 Seconds

# Quick Start (concrete example EARLY)

# Technology Stack

# When You Need To...
[Task-oriented navigation]
```

**Characteristics**: Scannable, front-loads practical example, links to detailed docs.

---

## Markdown Best Practices

| Element | Pattern | Anti-pattern |
|---------|---------|--------------|
| **Headers** | Answer questions: "How do I..." | Vague: "Introduction", "Overview" |
| **Code blocks** | Always specify language | No syntax highlighting |
| **Inline code** | `PropertyNames`, `file-paths`, `commands` | Prose explanation |
| **Bold** | Key concepts, actions, warnings | Decoration |
| **Tables** | Comparing options, listing properties | Lists work better |
| **Diagrams** | See [mermaid-diagram-guide.md](./mermaid-diagram-guide.md) | Linear sequences (use lists!) |

### Mermaid

**Core rule**: Diagrams reveal relationships prose cannot express efficiently. If list/table works, use that.

**Cardinal rule**: NEVER diagram linear sequences (use numbered list).

**Essential**:
- Quote labels with spaces: `A["User Request"]`
- Add meaning comments: `%% MEANING: What this represents`
- Explain colors: `%% COLOR: Green = success, Red = error`
- <12 nodes (split if more)

See [mermaid-diagram-guide.md](./mermaid-diagram-guide.md) for comprehensive reference.

---

## Testing Documentation

### Validation

- [ ] **Scan test**: Understand from headers only?
- [ ] **Search test**: Find answer in <30 seconds?
- [ ] **Surprise test**: Warned about time-wasting gotchas?
- [ ] **Completeness test**: Accomplish task without external resources?
- [ ] **Token test**: Every sentence deletable without information loss? If yes, delete it.

### AI Agent Test

Give draft to AI agent:
1. Ask it to summarize key points
2. Ask it to identify ambiguities
3. Ask it to generate code following guidance
4. Ask "what's missing?"

Iterate based on failures.

---

## Checklist: Before Publishing

### Content
- [ ] Every claim verified and sourced (frontmatter: `verified: <repo> <commit>`)
- [ ] All code examples tested and correct
- [ ] "Why" explained for non-obvious advice
- [ ] Common mistakes explicitly called out
- [ ] Quick reference tables for decisions
- [ ] Examples show both good and bad patterns

### Structure
- [ ] Headers answer questions or describe actions
- [ ] Front-loaded with most important information
- [ ] Scannable (reading only headers tells the story)
- [ ] Progressive disclosure (overview → detail → reference via links)
- [ ] Cross-references use links, not duplication

### Style
- [ ] Dense (no filler, no preamble, no tutorial chattiness)
- [ ] Concrete (specific examples, not vague guidance)
- [ ] Actionable (reader knows what to do next)
- [ ] Self-contained chunks (<300 tokens each)
- [ ] Frontmatter complete and accurate

### Audience
- [ ] Mental model identified (what they know, what they'll assume)
- [ ] Common misconceptions addressed
- [ ] Time-wasting mistakes prevented
- [ ] Decision frameworks provided (tables, not prose)

---

## Meta-Pattern

**Unifying principle**: Write for compression and retrieval, not linear reading.

Documentation is a database of answers. Optimize for:
- **Indexability**: Headers as search keys
- **Density**: Maximum information per token
- **Retrievability**: Answer findable in seconds
- **Verifiability**: Claims traceable (frontmatter verification)
- **Actionability**: Clear next steps

**When in doubt**:
1. Make it more specific
2. Show concrete examples
3. Explain why
4. Cut unnecessary words
5. Convert prose to tables

---

**Philosophy**: Great documentation respects the reader's time, protects them from mistakes, and enables confident action. Every token earns its place by providing information they can't get elsewhere or preventing a problem they didn't know existed.
