# Writing Findings Documents

Someone asked a question. Answer it, then prove it.

## Capsule: AnswerFirst

**Invariant**
The answer goes first; evidence follows. If readers stop early, they still have the finding.

**Example**
Wrong: "I investigated the SMS system. First I looked at SmsService..."
Right: "**SMS**: Two pathways. AT&T routes through Bandwidth API; others use email gateways."
//BOUNDARY: Your investigation journey wastes their time. Answer the question, then prove it.

**Depth**
- For humans: they stop reading; give them the answer early
- For agents: primacy frames everything after; the answer shapes interpretation
- Paraphrase the question first — surfaces interpretation differences before work begins

## The Core Pattern

```markdown
**Question**: [Paraphrased in your understanding]

---

**Finding**: [Complete answer. Not a teaser—the actual answer.]

---

## Evidence

[Diagrams for structure, tables for detail]

> [Source](link) — what it proves
```

Paraphrasing the question surfaces interpretation differences before you've done the work. Documents get forwarded—without the question, findings are facts without context.

The answer goes first. For humans: they stop reading. For agents: primacy frames everything after. Evidence follows.

## Worth Asking

Before writing:
- What exactly was asked? How do you understand it?
- What's the complete answer?
- What evidence supports each claim?

After drafting:
- Could someone read just the opening and know the answer?
- Are sources near the claims they support?
- Have you described what exists, or speculated about why?

## Why This Works

**Effect, not action** — "Sets `email_unsubscribed=1`" is what happens. Adding "—stops non-transactional email" is what it means.

**Sources near claims** — Not inline (breaks prose). Not at end (disconnected). Blockquotes keep evidence close while separating it from narrative. Link format depends on audience:
- Agents: file paths or RepoQL URIs—they read files directly
- Humans: clickable links. GitHub permalinks with line numbers are ideal. Consider forwarding—will the link work for recipients?

SeeAlso: `../source-links/` for platform-specific URL formats.

**Diagrams for structure, tables for detail** — Diagrams show how pieces connect. Tables show what each piece contains. Don't cram specifics into nodes.

**Explicit uncertainty** — State what you don't know. "I couldn't determine X" or "This is based on code inspection only—no runtime verification" is more useful than false confidence. Mark confidence levels on claims where evidence is indirect or incomplete.

## What to Avoid

| Pattern | Problem |
|---------|---------|
| Missing question | Facts without context |
| Buried answer | Reader hunts |
| Action without effect | Impact unclear |
| Speculation about intent | Often wrong |
| Process narrative | Your journey wastes their time |
| Timelines | Always wrong |
| False confidence | Hides knowledge gaps |

## The Transformation

```
I investigated the SMS system. First I looked at SmsService...
```

becomes:

```
**Question**: How does SMS delivery work?

---

**SMS**: Two pathways. AT&T routes through Bandwidth API;
others use email-to-SMS gateways.

> [SmsService.php:36-80](link) — routing logic
```

One narrates. One answers.

## Checklist

- [ ] Question paraphrased
- [ ] Answer before evidence
- [ ] Sources near claims
- [ ] Effects, not just actions
- [ ] No speculation or timelines
- [ ] Gaps and uncertainties explicit

## Exemplar

See `exemplar.md` — the SMS sketch from The Transformation, fully realized.

## Template

See `template.md` — a structural compass with self-check questions.
