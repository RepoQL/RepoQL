---
description: North star for the cloud inference service — LLM synthesis with client-side tool use
tags: [north-star, inference, llm, grok, cloud]
audience: { human: 50, agent: 50 }
purpose: { north-star: 100 }
---

# Inference Service: What Great Looks Like

> Ask a question about a codebase. Get an answer with citations. One prompt, one response, tools in between.

An agent needs to understand how token refresh works across a 200-file codebase. It gathers 30k tokens of context with a local explore — structure, summaries, the landscape. It sends that context and its question to a cloud inference service. The LLM reads the context, decides it needs the actual implementation of three functions, and calls `read` three times with precise URIs and token budgets. Each read returns exactly the code it asked for. The LLM synthesizes an answer with file and line citations. The whole exchange costs a fraction of a cent. The agent never configured a model, never managed an API key, never decided how hard to think. It asked a question and got an answer.

---

## The Right Split

- The client should be able to gather broad context locally (explore, query) and send it as input — the LLM starts informed, not blind.

- The LLM should be able to read specific files and symbols as follow-up — drilling into what the initial context revealed, not searching from scratch.

- The client should never need to know which model answered — it says how hard to think, and the service maps that to the right model and settings.

- An operator should be able to swap providers, add models, or change routing without any client changes.

---

## Budget as Contract

- The client should be able to set a total token budget for tool use, and the service should never exceed it — the LLM allocates within the pool, the service enforces before dispatch.

- The LLM should be able to decide how to spend the pool — 500 tokens on a headline versus 3000 on full content — without the client micromanaging each call.

- The client should see exactly what was spent in the response — input tokens, output tokens, tool tokens, reasoning tokens — so cost is transparent, not estimated.

---

## One Question, One Answer

- The client should be able to send a question and get a complete answer in one exchange — no conversation management, no session state, no continuation tokens.

- The reasoning trace should be available for logging and diagnostics — visible in the host console, not hidden behind the response.

- If the LLM runs out of budget or rounds, it should answer from what it gathered — a partial answer is better than an error.

---

## Failure as Guidance

- If the service is unreachable, LLM-powered features should degrade gracefully — no crashes, clear messaging, the rest of RepoQL works fine.

- If a tool call is rejected (budget exhausted), the LLM should be told why and how much remains — enough information to adjust its approach.

- If the LLM produces a malformed tool call, it should get an error message and a chance to retry — one bad call shouldn't end the exchange.

---

## Simplicity

- The client surface should be minimal — prompt, context, system, effort, budget. Everything else is the server's problem.

- The read tool exposed to the LLM should match the one Claude uses — same core capability, minus modifiers that trigger LLM calls (no `=> question:`). One source of truth for the tool surface, with inference-safe restrictions.

- Adding a new LLM provider should require zero client changes — the Effort enum is the contract, not the model name.

---

*The cloud earns its keep on the hard questions. Everything else stays local.*
