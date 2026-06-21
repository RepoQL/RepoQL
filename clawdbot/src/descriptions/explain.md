<WHY>
Delegate understanding to an army of fast exploration agents. Before reading many files to understand the shape of the system, fire of multiple scoped explains in parallel to investigate your questions for you.
It reads widely, synthesizes, and reports back with citations. You get comprehension, not raw text.

Their tokens are much cheaper than yours while still being very effective for this work. Trust but verify their work with targeted followup reads. 
</WHY>

<WHEN_TO_USE>
Use explain when you want a synthesized answer about a scoped area of the repository.

Good questions:
- "How does JWT refresh work in AuthService?"
- "What authentication methods does service X support?"
- "Why does PaymentProcessor use idempotency keys?"
- "What happens when an item is indexed, step by step?"
- "What are the relevant keywords related to bearer tokens?"

Bad questions:
- "Explain everything about authentication"
- "What does this service do?" without a meaningful scope
</WHEN_TO_USE>

<OUTPUT>
Returns a synthesized answer with citations derived from the explain service's tool use.
Verify important claims against the cited code when the stakes are high.
</OUTPUT>

<PARAMETERS>
`question`: required and should be a full sentence
`keywords`: required - the symbols, files, or terms-of-art you want explain to focus on. You almost always know these better than a generic extraction would infer (you just ran explore, or you already know the symbols).
`uriGlob` optionally scopes the answer with the full power of uri globbing — a hard filter on what is searched, not a soft bias. Omit it to use the current repository.
`tokenBudget` controls how much room the synthesized answer has. Does not affect the input token budget for the agent producing the answer.
</PARAMETERS>