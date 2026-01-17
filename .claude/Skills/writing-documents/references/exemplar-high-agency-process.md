# Choosing Dependencies

## Where You Are

- You need functionality that exists in libraries
- Your job: decide whether to depend or build

## Core Objective

Minimize total cost of ownership while meeting the need.

## Worth Exploring

- What's the cost if this dependency disappears tomorrow?
- What's the cost if you build it wrong?
- Is this core to your business, or scaffolding?
- How many strangers' judgment are you trusting?
- What's the replacement path if you need one?

Start with the assumption that you should build. Let the dependency earn its place.

## The Lens: Replaceability

How hard would it be to swap this out?

| Replaceability | Characteristics | Posture |
|----------------|-----------------|---------|
| Trivial | Standard interface, many implementations | Depend freely |
| Moderate | Some coupling, alternatives exist | Depend with abstraction |
| Hard | Deep integration, few alternatives | Depend only if proven |
| Impossible | Lock-in by design | Build unless no choice |

## Boundaries

- Never depend on abandoned projects for core functionality
- Never skip checking the transitive dependency tree
- Never abstract "just in case"

## Success Looks Like

- Can explain why you chose depend or build
- Replacement cost proportional to value received
- Dependency count as low as possible, no lower

## Final Thought

Every dependency is a bet on someone else's judgment. Know what you're betting.
