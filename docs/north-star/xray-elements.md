# X-Ray Elements: Headline, Structure, Snippet

> Definitions of greatness for the three levels of progressive disclosure.

---

## Capsule: Headline

**Invariant**
A single scannable line with size proxy, enabling agents to filter 1000 files to 20 candidates.

**Example**
```
PaymentService.cs | PaymentService : IPaymentService | ProcessPayment, Refund, Subscribe | 450 ln, ~2.1k tok
```
//BOUNDARY: Every element must answer "how would an agent use this to decide investigate vs skip?"

**Depth**
- Size proxy always present: ln+tok (code/text), bytes (binary), dimensions+bytes (images)
- Method/section names over counts—"ProcessPayment, Refund" beats "12 methods"
- Omit obvious facts: "public class" for .cs, "namespace" when path shows it
- NotThis: prose descriptions, multi-line, elements without filtering purpose
- SeeAlso: `Structure`, `Snippet`

---

## Capsule: Structure

**Invariant**
A hierarchical outline with signatures and URI fragments, enabling direct navigation via `read(uri + fragment)`.

**Example**
```
Pushpay.Services
  +class PaymentService : IPaymentService
    // Processes payment and charges customer's payment method
    +Task<PaymentResult> ProcessPayment(PaymentRequest request)    #symbol=ProcessPayment
    +Task<RefundResult> RefundPayment(Guid paymentId, decimal amount)    #line=122,145
    -Task<bool> ValidatePayment(PaymentRequest request)    #line=180,195
    +static decimal ParseAmount(string input)    #symbol=ParseAmount
```
//BOUNDARY: Signatures not bodies—show what exists and where, not how it works.

**Depth**
- Complete: every element listed—structure is vector-indexed for search
- Never truncate: hidden elements can't be found; that defeats the purpose
- `+` public, `-` private/internal, `static` when applicable
- `static` explains: "why can't I access instance state?", "why is this mockable?"
- Return types including `Task<>` are meaningful; `async` keyword is not
- Args and return types are search signals
- Doc comments extracted to single-line `//` above signature—searchable intent
- Fragments append directly to URI for `read(uri + fragment)`
- NotThis: flat lists, `async` keyword, truncated sections
- SeeAlso: `Headline`, `Snippet`

---

## Capsule: Snippet

**Invariant**
A focused code window with line numbers and context, showing exactly what the agent requested plus enough surrounding code to understand it.

**Example**
```
file:///src/Auth/TokenService.cs#line=42,48  [csharp]

 40:     private readonly ITokenStore _store;
 41:
>42:     public async Task<Token> RefreshAsync(string refreshToken)
>43:     {
>44:         var existing = await _store.GetAsync(refreshToken);
>45:         if (existing?.IsExpired ?? true)
>46:             throw new TokenExpiredException();
>47:         return await GenerateTokenPair(existing.UserId);
>48:     }
 49:
 50:     private async Task<Token> GenerateTokenPair(Guid userId)
```
//BOUNDARY: Focus lines marked; context sufficient to understand scope but not the whole file.

**Depth**
- Focus visually distinguished: `>` prefix or equivalent marking
- Line numbers match file: enables precise follow-up references
- Language hint included: enables syntax awareness
- Fragment-aware: symbol fragments show that symbol; line fragments show those lines
- NotThis: whole file dumps, unnumbered code, orphaned lines without context
- SeeAlso: `Headline`, `Structure`

---

## Capsule: ProgressiveDisclosure

**Invariant**
Headline → Structure → Snippet forms a funnel: scan thousands, navigate dozens, read few.

**Example**
```
headline  →  "Is this relevant?"     (2-10 tokens × 1000 files)
structure →  "Where in this file?"   (~30 tokens × 20 files)
snippet   →  "What does it do?"      (~100 tokens × 3 locations)
```
//BOUNDARY: Each level answers a different question; skipping levels wastes tokens.

**Depth**
- Headline filters candidates; structure locates targets; snippet reveals code
- Agent should rarely need full file if all three levels are well-formed
- Token cost compounds: poor headlines force reading structures; poor structures force reading files

---

## Capsule: HeadlinePrinciples

**Invariant**
Every headline element must pass the filter test: "how would an agent use this to decide investigate vs skip?"

**Example**
| Principle | C# Application | Markdown Application | API Spec Application |
|-----------|----------------|---------------------|---------------------|
| Size proxy | `450 ln, ~2.1k tok` | `85 ln, ~600 tok` | `12 endpoints, 45 schemas` |
| Identity | `PaymentService : IPaymentService` | `Authentication Guide` | `Payments API v2` |
| Searchable content | `ProcessPayment, Refund, Subscribe` | `JWT, refresh tokens, PKCE` | `POST /payments, GET /refunds` |

//BOUNDARY: If you can't explain how an element helps filter, cut it.

**Depth**
- Size proxy format varies: ln+tok (text), bytes (binary), dimensions+bytes (images), counts (structured)
- Identity answers "what is this?"—type, title, name
- Searchable content answers "is this what I'm looking for?"—key terms an agent would grep
- Omit the obvious: info derivable from path, extension, or format conventions
- SeeAlso: `StructurePrinciples`, `SnippetPrinciples`

---

## Capsule: StructurePrinciples

**Invariant**
Structure is a complete, searchable, addressable map—every element navigable, none hidden.

**Example**
| Principle | How It Manifests |
|-----------|------------------|
| Complete | Every navigable element listed; never truncate |
| Addressable | Each element has fragment appendable to URI |
| Hierarchical | Indentation reflects containment relationships |
| Signatures over bodies | What exists + where, not how it works |
| Compact modifiers | Symbols over keywords (`+`/`-` vs `public`/`private`) |
| Meaningful modifiers | Include what affects usage patterns; omit implementation details |
| Searchable intent | One-line doc comments above elements when available |

//BOUNDARY: Structure is vector-indexed—what's hidden can't be found.

**Depth**
- **Addressable**: fragments enable `read(uri + fragment)` without construction
- **Meaningful vs implementation modifiers**:
  - Meaningful: affects how you call/use it (visibility, static, abstract, readonly)
  - Implementation: internal mechanics (async, virtual when not overriding, partial)
- **Compact modifiers**: use shortest unambiguous representation
  - Visibility: `+`/`-` or equivalent
  - Format-specific: choose symbols that scan well vertically
- **Searchable intent**: extract summary from doc comments, annotations, descriptions
- **Hierarchy**: use indentation native to format (2-space, tree chars, etc.)
- SeeAlso: `HeadlinePrinciples`, `SnippetPrinciples`

---

## Capsule: SnippetPrinciples

**Invariant**
A snippet shows the requested content with enough context to understand it, marked so focus is unambiguous.

**Example**
| Principle | How It Manifests |
|-----------|------------------|
| Focus marked | Requested lines/region visually distinguished (`>`, highlight, etc.) |
| Context sufficient | Surrounding content explains scope, not entire file |
| Line numbers | Match actual file for precise follow-up references |
| Language/format hint | Enables syntax awareness for consumer |
| Fragment-aware | Respects the fragment type (symbol, line range, JSON pointer) |

//BOUNDARY: Agent should understand what code does without reading more.

**Depth**
- **Focus marking**: consistent symbol (`>` prefix, `***` surround, etc.)
- **Context window**: enough to see containing scope, not arbitrary fixed count
- **Line numbers**: 1-indexed, matching file—enables "look at line 47"
- **Fragment types**: honor semantics (symbol = that symbol's span; lines = those lines)
- SeeAlso: `HeadlinePrinciples`, `StructurePrinciples`

---

## Capsule: FormatAdaptation

**Invariant**
Each format has a natural essence; structure should express that essence, not impose foreign concepts.

**Example**
| Format | Natural Essence | Structure Expresses |
|--------|-----------------|---------------------|
| **Code** | Types, callables, dependencies, visibility | Class hierarchy, method signatures, access levels |
| **Markdown** | Ideas, flow, connections | Heading tree, link targets, content arc |
| **Config** | Keys, values, relationships | Key hierarchy, value types, cross-references |
| **API Spec** | Resources, operations, shapes | Endpoints, methods, request/response schemas |
| **Database** | Tables, columns, constraints | Schema structure, relationships, keys |
| **Build/CI** | Steps, dependencies, triggers | Stage flow, conditions, artifacts |

//BOUNDARY: If the structure feels forced, you haven't found the format's essence yet.

**Depth**
- **Code**: The essence is *what can I call and what does it need?* → signatures, dependencies, visibility
- **Markdown**: The essence is *what does this document argue/explain?* → heading flow, key assertions, links
- **Config**: The essence is *what knobs exist and how do they relate?* → key paths, value constraints, references
- **API Spec**: The essence is *what can I ask for and what will I get?* → operations, parameters, response shapes
- **Data files**: The essence is *what's the shape and what are the records?* → schema, sample values, counts
- Ask: "What question does someone have when they encounter this format?" Structure answers that question.
- SeeAlso: `HeadlinePrinciples`, `StructurePrinciples`

---

# Checklist (non-negotiables)

- [ ] Headline: single line, size proxy present, every element justifies its place
- [ ] Structure: hierarchical, complete (no truncation), fragments for navigation
- [ ] Snippet: focus marked, line numbers match file, context sufficient but minimal
- [ ] Each level answers a distinct question in the progressive disclosure funnel
- [ ] Format-specific structure expresses the format's natural essence
