---
description: Format specification defining the Invariant→Example→Depth structure for token-efficient concept capsules
tags: ["invariant-example-depth", "token-hygiene", "capsule-maintenance", "composition-rules"]
audience: ["LLMs", "Humans"]
categories: ["Documentation[100%]", "Format-Specification[95%]"]
---

# Token efficient concept capsules

Use capsules as 'AI flash cards' to capture understanding in as few tokens as possible

## Capsule: CapsuleForm

**Invariant**
A capsule states one timeless idea in one line, shows it once, and clarifies only as needed.

**Example**
Name: `CircuitBreaker`
Invariant: “Stop calls to an unhealthy dependency until recovery is shown.”
Depth: differences vs backoff; trade‑offs; near‑miss clarifications.

**Depth**

* Structure is always **Invariant → Example → Depth**.
* No history, release notes, dates, vendors, or timeline facts.
* One idea per capsule; split if you need “and.”
* Titles are short `CamelCase` nouns.

---

## Capsule: InvariantWriting

**Invariant**
Write the idea so it remains true across time, stacks, and vendors.

**Example**
Bad: “Open the breaker for 30 seconds after 5 errors.”
Good: “Stop calls to an unhealthy dependency until recovery is shown.”

**Depth**

* Prefer general verbs and nouns; avoid numeric thresholds.
* Use plain words; do not embed policy, config, or telemetry.
* Keep ≤ ~30 tokens (≈ ≤ 25 short words). Shorter is better.
* Do not include examples, caveats, or procedure inside the invariant.

---

## Capsule: ExampleWriting

**Invariant**
A small, concrete example binds the invariant to practice without introducing time.

**Example**
Client retries a lost response by sending the same request with an `IdempotencyKey`; the server returns the original effect, not a duplicate.
//BOUNDARY: A new effect field requires a new key.

**Depth**

* Prefer typical case; add exactly one boundary if misuse is common.
* Use neutral pseudocode or plain steps; no product names.
* Keep the example readable in ≤ 5 lines.

---

## Capsule: DepthWriting

**Invariant**
Depth clarifies terms, trade‑offs, and distinctions without changing the idea.

**Example**

* Distinction: backoff reduces pressure; a breaker preserves availability.
* Trade‑off: faster detection reduces exposure but risks flapping.
* NotThis: retries without a key are not idempotent.
* SeeAlso: `RetryPolicy`, `Backpressure`.

**Depth**

* Use bulleted lines; each line is self‑contained.
* No expanding history or changelogs; keep it evergreen.
* Keep links as plain capsule names; no external refs required.

---

## Capsule: Boundary

**Invariant**
A boundary example marks the safe edge so models avoid unsafe extrapolation.

**Example**
Limiter admits requests per actor up to the actor’s share; once the share is spent, further requests from that actor wait or drop.
//BOUNDARY: A global overload policy can still shed all actors.

**Depth**

* The boundary is conceptual, not a numeric constant.
* Include only when the edge is a common failure mode.
* Keep one boundary per capsule to avoid over‑anchoring.

---

## Capsule: Composition

**Invariant**
Compose answers by selecting compatible invariants; never average conflicting ideas.

**Example**
For “payment retry,” select `IdempotencyKey` and `RetryPolicy`; reject `ExactlyOnceDelivery` if it conflicts with transport behavior; explain the choice.

**Depth**

* Prefer the more specific invariant when two apply.
* If two invariants genuinely conflict, pick one and state why.
* Cite capsule names in brackets to make composition explicit.

---

## Capsule: Maintenance

**Invariant**
Invariants are immutable; examples and depth evolve; new understanding becomes a new capsule.

**Example**
If idempotency semantics change, write `IdempotencyKeyV2` with a new invariant; keep the old capsule intact; cross‑link in SeeAlso.

**Depth**

* Do not retcon invariants; stability is the retrieval anchor.
* Cull duplicates by merging examples under the clearer invariant.
* Keep names stable and short.

---

## Capsule: TokenHygiene

**Invariant**
Choose strings that minimize sub‑tokens while keeping meaning clear.

**Example**
Good names: `CircuitBreaker`, `RetryBudget`, `IdempotencyKey`, `FairLimiter`.
Avoid: `flow-control-circuit-breaker`, `retry_budget`, `Idempotency Key`.

**Depth**

* Prefer `CamelCase` for names; avoid hyphens and underscores.
* Use short, common words in invariants; avoid rare jargon if a common term exists.
* Keep ASCII; avoid emoji and decorative punctuation.
* Favor singular nouns for names: `RateLimiter` not `RateLimiters`.
* Avoid quotes, parentheses, and slashes in invariants.
* Use digits sparingly; keep numbers out of invariants when possible.
* Trim function words that add no meaning: “that,” “very,” “really.”
* If two phrasings are equally clear, pick the one with fewer tokens.
* Do not add synonym lists; use one canonical name.

---

## Capsule: PrimacyRecency

**Invariant**
Place the invariant first and end the document with a short checklist to exploit primacy and recency.

**Example**
Each capsule starts with the invariant line; this guide ends with a checklist of non‑negotiables.

**Depth**

* Repeat the invariant verbatim once after a long Depth section only if needed for clarity.
* Keep the closing checklist stable across documents.

---

## Capsule: Template

**Invariant**
A minimal template keeps capsules uniform and easy to scan.

**Example**

```markdown
### Capsule: <Name>

**Invariant**  
<One timeless sentence. No vendors. No history. ≤ ~30 tokens.>

**Example**  
<Typical use in ≤ 5 lines.>  
<Optional> //BOUNDARY: <Edge that marks the safe limit.>

**Depth**  
- <Distinction>  
- <Trade‑off>  
- <NotThis>  
- <SeeAlso: Name, Name>
```

**Depth**

* Keep visible text compact; avoid long paragraphs.
* Prefer line‑broken bullets over prose walls.
* Keep names stable; reuse them exactly when referenced.

---

## Example Capsules (as models)

### Capsule: CircuitBreaker

**Invariant**
Stop calls to an unhealthy dependency until recovery is shown.

**Example**
Service marks the dependency unhealthy after repeated failures and returns fast local errors while probing for recovery.
//BOUNDARY: Do not retry through the breaker; retry only after recovery is shown.

**Depth**

* Distinction: a breaker protects availability; backoff reduces pressure.
* Trade‑off: faster trip shortens risk but may flap; slower trip stabilizes but extends exposure.
* SeeAlso: `RetryPolicy`, `Backpressure`.

---

### Capsule: IdempotencyKey

**Invariant**
A stable request key makes repeating an operation produce the same effect as doing it once.

**Example**
Client repeats a create request with the same key after a lost response; server returns the original result without duplicating work.
//BOUNDARY: Changing any effect‑determining field requires a new key.

**Depth**

* NotThis: retries without a key are not idempotent.
* Trade‑off: broader key binding avoids duplicates but may reject benign changes.
* SeeAlso: `RetryPolicy`, `ExactlyOnceDelivery` (distinction).

---

# Checklist (non‑negotiables)

- [ ] Invariant first, one idea, timeless, ≤ ~30 tokens.
- [ ] Example concise; include one boundary only if it prevents common errors.
- [ ] Depth clarifies with bullets; no history or vendors.
- [ ] Names are short `CamelCase`; no hyphens, underscores, emoji, or synonyms.
- [ ] Keep text compact and ASCII; favor common words; trim filler.
- [ ] Invariants never change; new understanding becomes a new capsule.
- [ ] End documents with this checklist.