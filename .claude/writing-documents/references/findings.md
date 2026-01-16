# Writing Findings Documents

## Where You Are

You scored high on findings. Someone asked a question. Your job is to answer it completely, with evidence they can verify.

## Core Objective

Answer the question. Then prove it.

## The Audience

Intelligent, skeptical, time-constrained. They may not be the person who asked—documents get forwarded.

Human readers may stop after the first few paragraphs. Agent readers will process everything but still need structure for extraction. Both need the question stated—without it, findings are facts without context.

## Worth Exploring

- What exactly was asked? How do you understand it?
- What's the complete answer in 2-3 paragraphs?
- What evidence supports each claim?
- What's the practical effect, not just the technical action?

On what you've drafted:
- Could someone read just the opening and know the answer?
- Are sources near the claims they support?
- Have you described what exists, or speculated about why?

## The Recipe

```markdown
# Title

**Question**: What was asked, paraphrased in your understanding

---

**Topic 1**: Complete answer in prose. Not a teaser—the actual finding.

**Topic 2**: More complete answers if multiple topics.

---

## Topic 1 Detail

[Diagram — structure/flow]

[Table — details that would clutter the diagram]

[1-2 sentences — the key insight]

> [Source](link) — what it proves
```

The opening answers the question. Everything below is evidence.

## Required Practices

- Question paraphrased at top (surfaces interpretation differences)
- Answer front-loaded before any diagrams
- Every claim has evidence nearby
- Effect explained, not just action: "Sets X — stops email"
- Sources as blockquotes near claims, not collected at end

For human audiences: diagrams show structure visually; tables show detail.

For agent audiences: tables may be more efficient than diagrams (fewer tokens, always parseable).

## Why This Works

### Paraphrase the question
Documents get forwarded. The person reading may not be the person who asked. Without the question, findings are facts without context. Paraphrasing in your own words surfaces interpretation differences early—before you've done the work.

### Front-load answers
The first few paragraphs should contain the complete answer. Diagrams, tables, and sources are evidence—not the findings themselves. A reader who stops after the opening should still know the answer.

### Diagrams show structure; tables show detail
Don't cram specifics into diagram nodes. Use diagrams for relationships and flow. Use tables for attributes, examples, mechanisms. The diagram shows how pieces connect; the table shows what each piece contains.

### Effect, not just action
Technical actions need practical meaning. "Sets `email_unsubscribed=1`" tells you what happens in the database. "Sets `email_unsubscribed=1`—stops non-transactional email" tells you what it means for users.

### Sources near claims
Not inline (breaks prose). Not at end (disconnected from what they support). Blockquotes keep sources close while visually separating evidence from narrative:

```markdown
The `send_type` field determines the path—exclusive routing, not fallback.

> [SmsService.php:36-80](link) — routing logic
```

## What to Avoid

| Mistake | Why it's wrong |
|---------|----------------|
| Missing question | Findings without context |
| Answers buried | Reader has to hunt |
| Action without effect | Reader doesn't understand impact |
| Speculation about intent | Often wrong, rationalizes |
| Process narrative | "First I looked at..." wastes time |
| Timelines or estimates | Always wrong |
| Unsolicited recommendations | Not what was asked |
| Sources at end | Disconnected from claims |

## Success Looks Like

- Reader of just the opening knows the answer
- Every claim has visible evidence
- Effects are clear, not just actions
- No speculation, no estimates, no unrequested advice
- Document works when forwarded to someone who didn't ask

## The Transformation

You will write this:
```
I investigated the SMS delivery system. First I looked at the
SmsService class, which handles routing. Then I traced through
the queue system. Based on this, I believe the system works by...
```

Write this instead:
```
**Question**: How does SMS delivery work?

---

**SMS**: Two pathways based on carrier. AT&T routes through
Bandwidth API; all others use email-to-SMS gateways.

---

## SMS Routing Detail

[diagram]

> [SmsService.php:36-80](link) — routing logic
```

One narrates your process. One answers the question.

## Final Thought

The finding is the answer. Everything else is evidence.
