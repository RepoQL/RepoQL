# Writing North Star Documents

A north star is a set of declarations—things that should be possible. Each declaration answers "can we do this?" with yes or no.

---

## Capsule: PaintTheDream

**Invariant**
Open with a paragraph that paints the full picture—the dream in narrative form before breaking into declarations.

**Example**
"An agent points at a codebase and says 'show me all the repository interfaces, except the ones in generated code.' One pattern. One call. The agent doesn't need to know where files live or what symbols are called. It expresses intent, and the system finds exactly what matches."
//BOUNDARY: The paragraph is the vision; declarations are the evaluable pieces.

**Depth**
- Sets the scene before the structure
- Captures what the experience feels like
- Gives context for why the declarations matter
- Readers understand the whole before the parts

---

## Capsule: DeclarationsNotDescriptions

**Invariant**
After the opening, a north star declares what should be achievable, not what a system is.

**Example**
Description: "Globbing is a pattern language for selecting content."
Declaration: "An agent should be able to select any combination of files and symbols with a single pattern."
//BOUNDARY: If you can't evaluate it as achievable or not, it's not a declaration.

**Depth**
- Each declaration is a test: can we do this?
- Descriptions explain; declarations commit
- "I have a dream:" prefix tests if a statement is truly a declaration

---

## Capsule: TheDreamTest

**Invariant**
Prefix any statement with "I have a dream:" — if it reads naturally, it's a declaration; if it's awkward, reframe it.

**Example**
✓ "I have a dream: An agent should be able to select symbols without knowing file locations"
✗ "I have a dream: Pattern matching works against indexed symbols" → reframe as "An agent should be able to trust results reflect indexed reality"
//BOUNDARY: System behaviors and constraints fail the test; agent capabilities pass.

**Depth**
- Descriptions of HOW fail: "exclusions are applied last"
- Constraints fail: "no context-specific escaping"
- What something returns fails: "readiness returns file counts"
- What an agent CAN DO passes: "an agent can verify readiness"

---

## Capsule: EvaluableStatements

**Invariant**
Every declaration can be checked against an implementation: yes or no.

**Example**
"A pattern that works in search scope works identically in read, query, and glob_files."
Evaluation: Try the same pattern in each context. Does it behave the same? Yes/no.
//BOUNDARY: Vague declarations like "patterns are flexible" can't be evaluated.

**Depth**
- Declarations are pass/fail
- Specificity enables evaluation
- Ambiguity prevents accountability

---

## Capsule: VisionNotImplementation

**Invariant**
Declarations describe what the user can do, never how it's built.

**Example**
Right: "An agent should be able to filter symbols by their source location without knowing symbol names."
Wrong: "UriRegistry tracks symbol locations for filtering."
//BOUNDARY: If swapping the implementation would change the declaration, it's not vision.

**Depth**
- Capabilities, not components
- Outcomes, not mechanisms
- The declaration survives any implementation

---

## Capsule: AncestorToDesigns

**Invariant**
A north star's declarations guide many designs; each design achieves some of them.

**Example**
North star declares: "Scope readiness is always known before search."
Design 1 achieves it via UriRegistry.
Design 2 might achieve it via database query.
The declaration doesn't care which.
//BOUNDARY: If a declaration only applies to one design, it may be too narrow.

**Depth**
- Declarations are stable; implementations change
- Multiple designs can satisfy the same declaration
- The north star outlives any single design

---

## Capsule: TimelessDeclarations

**Invariant**
Declarations have no temporal markers; they state what should be achievable, not what's missing today.

**Example**
Right: "An agent should be able to exclude symbols by line range."
Wrong: "Gap: cross-fragment constraints need implementation."
//BOUNDARY: Gap analysis belongs in design or plan, not north star.

**Depth**
- No "currently", "planned", "gap"
- The same declarations read correctly in five years
- Status tracking happens elsewhere

---

## Worth Exploring

Before writing:
- What should an agent be able to achieve with this capability?
- Can each declaration be evaluated as yes/no?
- Would these declarations survive a complete reimplementation?

On framing:
- Does each statement pass the "I have a dream:" test?
- Is it an agent capability, not a system behavior or constraint?
- Can someone point to it and ask "can we do this?"

On scope:
- Are declarations broad enough to guide multiple designs?
- Are they specific enough to evaluate?

---

## Structure

```markdown
# [Capability]: What Great Looks Like

> [One line essence]

[Paint the dream: a paragraph showing what the experience feels like when this capability is fully realized. An agent does X, and Y happens. No jargon, no internals—just the vision in narrative form.]

---

## [Category of Declarations]

- An agent should be able to [specific achievable thing]
- [Another declaration]
- [Another declaration]

---

## [Another Category]

- [Declaration with concrete example]
  ```
  [Example showing declaration in action]
  ```

---

## What Great Looks Like

| Declaration | Why It Matters |
|-------------|----------------|
| [Statement] | [Value it provides] |

---

## Anti-Patterns

| Don't | Do Instead |
|-------|------------|
| [Common mistake] | [Declaration form] |

---

*[Closing declaration]*
```

---

## What to Avoid

| Pattern | Problem | Reframe As |
|---------|---------|------------|
| System behaviors | "Pattern matching works against indexed symbols" | "An agent should be able to trust results reflect indexed reality" |
| Constraints | "No context-specific escaping" | "An agent should be able to use one syntax everywhere" |
| What something returns | "Readiness returns file counts" | "An agent should be able to see how many files are pending" |
| HOW something works | "Exclusions are applied last" | "An agent should be able to write exclusions in any order" |
| Implementation details | "UriRegistry tracks locations" | Omit entirely |
| Vague aspirations | "Patterns are flexible" | "An agent should be able to combine patterns into one selection" |
| Temporal markers | "Gap: cross-fragment needs work" | Omit—belongs in design |

---

## Checklist

- [ ] Opens with a paragraph painting the full dream
- [ ] Each statement passes the "I have a dream:" test
- [ ] Each declaration can be evaluated yes/no against an implementation
- [ ] No implementation details—declarations survive any rebuild
- [ ] No temporal markers or gap analysis
- [ ] No system behaviors or constraints—only agent capabilities
- [ ] Broad enough to guide multiple designs
- [ ] Specific enough to evaluate

---

## Exemplar

See `exemplar.md` — declarations for pattern matching capability.

*A north star is a set of commitments. Each one asks: can we do this?*
