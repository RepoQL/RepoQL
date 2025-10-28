# RepoQL Design Ethos

## Design Pillars

### Agent-First
**Assume that Repoql is always being consumed via an AI Agent**
1. Prefer standards or well known formats that an agent innately understands
2. When documenting things, consider whether simply naming the thing (e.g. DuckDB SQL with elastic search) will suffice
3. Things that would be challenging for humans but easy for AI (e.g. AST, Grammars, complex SQL) are to be embraced
4. Everything should be stated using just enough tokens, no more, no less

### Intuitive
**Everything should work the way the agent expects**
1. If possible, the agent's first instinct for how to use the tool should be how it actually works
2. The less there is to explain, the better
3. Consider how functionality will be discovered, if it will not be discovered, it is not valuable
4. If the agent consistently makes mistakes on first use of some functionality, it is not the right shape or is not well explained
5. Be consistent. We do not have unlimited tokens for the instructions, so understanding one part of RepoQL should translate into understanding others
6. Can we piggy-back on a concept from the LLM's training data or from repoQL to ease understanding?
7. Is there a single sentence that will describe each of the necessary concepts and allow the agent to extrapolate successfully?

### Convenient
**Agents are already very capable, repoql should only offer functionality that is more powerful than the standard agent tools**
1. Functionality must carefully consider value vs cognitive complexity of understanding it:
   1. How much of it needs to be understood to make use of it (i.e. is it implicit like lint on write, or explicit like query?)
   2. How many tokens will it take to understand it?
   3. Will an agent understand how to use it fully without a full tutorial?
   4. Does it save tokens? Add new capabilities? Make certain tasks succeed more consistently?
2. Does it have an extremely high success rate and low false positives? Rework is unacceptably expensive, it is usually better not to have the feature at all.

## Golden Rules
- We do not extend the core schema without a very, very good reason
- We use or extend common formats at the edges (SARIF, Sql, URIs mime)
- RepoQL must 'just work' - no complex config without sensible defaults 
- The heavy lifting happens in the host - the consumer specifies intent. If we have to do something hard/computationally expensive to make agent's work easier then we should.
- DuckDB does not support multiple writers. all writes MUST go through SingleThreadedDatabaseWriter
- The amount of time it takes for the host to be ready to accept simple queries should be kept as low as possible
- Errors when processing a file should never stop repoql from being usable

## Checklist
- [ ] Do we need to explain it?
- [ ] How will we explain it? 
- [ ] How many tokens will it take to convey insights?
- [ ] Does the agent already understand something we can leverage?