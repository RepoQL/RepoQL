---
description: How to write documentation optimized for human readers
tags: [audience, human, documentation, writing, attention, cognition]
audience: { human: 10, agent: 95 }
purpose: { gestalt: 5, concepts: 35, reference: 40, research: 0, findings: 0, flow: 0, plan: 0, design: 0, high-agency-process: 20, low-agency-process: 0 }
---

# Writing for Humans

Humans are not slow agents. They process information fundamentally differently.

**Important**: 

Writing for humans requires that you have read the mermaid diagrams skill - they are a very important modality for communicating concepts and relationships to human readers

## Capsule: AttentionEconomy

**Invariant**
Human attention is scarce and competitive; every element must earn its place or be cut.

**Example**
Reader sees wall of text, decides not worth the effort, leaves. Same content with clear headers and short paragraphs gets read.
//BOUNDARY: Brevity without context loses trust. Balance density with accessibility.

**Depth**
- Attention is decided in seconds, not earned gradually
- First impression determines investment
- Competing with everything else demanding their time

## Capsule: MotivationFirst

**Invariant**
Humans need to know why they should care before they will invest attention.

**Example**
Bad: Technical specification of authentication flow.
Good: How to stop users getting locked out, followed by technical details.
//BOUNDARY: Motivation is not clickbait. Honest framing of relevance.

**Depth**
- What problem does this solve for them?
- What happens if they ignore this?
- Why now rather than later?

## Capsule: ScanningBehavior

**Invariant**
Humans scan before reading; structure documents for non-linear consumption.

**Example**
Reader scrolls, reads headers, finds relevant section, reads that section, leaves. Never sees content between headers.
//BOUNDARY: Scanning is the norm, not failure. Design for it.

**Depth**
- Headers are navigation, not decoration
- First sentence of each section carries disproportionate weight
- Bold key terms for scanning eyes
- Table of contents for longer documents

## Capsule: CognitiveLoad

**Invariant**
Working memory holds roughly seven items; exceed this and comprehension collapses.

**Example**
List of 15 considerations presented at once. Reader retains perhaps 3, feels overwhelmed, disengages.
//BOUNDARY: Chunking helps but has limits. Prioritize ruthlessly.

**Depth**
- Group related items (3-5 per group)
- Introduce concepts before using them
- One new idea per paragraph
- Whitespace is cognitive breathing room

## Capsule: TrustSignals

**Invariant**
Humans need credibility signals before accepting information, even if they never verify.

**Example**
Document with sources linked feels trustworthy. Reader rarely clicks but presence matters.
//BOUNDARY: Trust signals are not decoration. Absence creates doubt.

**Depth**
- Links to sources (even if unclicked)
- Acknowledgment of limitations
- Professional presentation
- Consistency with their existing knowledge

## Capsule: VisualProcessing

**Invariant**
Humans extract meaning from visual structure faster than from linear text.

**Example**
Comparison as prose: 200 words, 2 minutes to process. Same comparison as table: 10 seconds.
//BOUNDARY: Diagrams add production cost. Use when structure genuinely complex.

**Depth**
- Diagrams for relationships and flows
- Tables for comparisons and attributes
- Formatting (bold, bullets) for emphasis
- Whitespace for separation and rest

## Capsule: ReinforcementLearning

**Invariant**
Humans benefit from encountering key ideas multiple times in different forms.

**Example**
Concept introduced in summary, explained in body, reinforced in example, recalled in conclusion. Retention improves.
//BOUNDARY: Repetition is not redundancy. Same idea, different angles.

**Depth**
- Summary at top (what you'll learn)
- Body with examples (how it works)
- Recap at end (what to remember)
- Unlike agents, repetition aids rather than wastes

## Capsule: ThreeSegments

**Invariant**
The first screen must catch attention and convey understanding; depth follows for those who continue.

**Example**
100 readers start. 50 finish first section. 25 reach middle. 10 reach end. Documents can be long—but the first screen must work standalone.
//BOUNDARY: Long documents are fine. Burying the point is not.

**Depth**

**Beginning (One Screen)**
- Must catch attention AND convey core understanding
- Reader who stops here should still get the point
- Answer why care, what it is, key insight
- This is not a teaser—it's the complete idea in compressed form

**Middle (Depth)**
- For readers who want more
- Supporting detail, examples, nuance, edge cases
- Structure for scanning—they may jump to what's relevant
- Each section should make sense independently

**End (Credibility)**
- For skeptics and verifiers
- Show mental working: how conclusions were reached
- Synthesis, judgements, intuition explained
- For those who need to trust before acting

## Practical Guidance

| Element | Human-Optimized |
|---------|-----------------|
| Paragraphs | 3-5 sentences max |
| Headers | Every 2-4 paragraphs |
| Lists | 5-7 items max per list |
| Sentences | Vary length; avoid all long |
| Examples | Concrete, relatable, early |
| Diagrams | When relationships > 3 elements |

## Checklist

- [ ] Why should they care? answered in first paragraph
- [ ] Scannable: headers, bold terms, short paragraphs
- [ ] Cognitive load managed: chunked, prioritized
- [ ] Trust signals present: sources, limitations acknowledged
- [ ] Key ideas reinforced across sections
