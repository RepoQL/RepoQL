---
description: Vision for a development harness that keeps agents in flow while iterating on RepoQL
tags: [dev-harness, mcp, testing, iteration, telemetry]
audience: { human: 40, agent: 60 }
purpose: { north-star: 100 }
---

# Dev Harness: What Great Looks Like

> Unbroken flow from code change to verified result.

Claude is iterating on RepoQL. A code change is made, the harness rebuilds, the new version activates, and the conversation continues—no manual reconnection, no switching windows, no lost telemetry. The agent sees build output, queries logs, traces a failing call, fixes the bug, rebuilds again. The cycle is seconds, not minutes. The orchestrator crashes? The harness recovers. The host needs a restart? The harness handles it. The agent stays in flow, the human stays informed, and progress compounds.

---

## Current Reality

Today, iterating on RepoQL requires manual choreography. After code changes, the agent runs `deploy.ps1`, asks the human to reconnect via `/mcp`, waits, hopes the orchestrator is still running. Telemetry requires opening a browser to the Aspire dashboard. If the orchestrator restarts, the Aspire MCP needs manual reconnection. Context is lost, flow is broken, the human becomes a process manager instead of a collaborator.

---

## A Day in the Life

Claude is tracking down a race condition in the indexer. It makes a change, the harness rebuilds in under 30 seconds, the new host activates. Claude runs the failing test, queries the trace, sees the interleaving that causes corruption. Another change, another rebuild, the test passes. The whole cycle takes four minutes. The human watching sees progress indicators but never touches the keyboard. At one point the host crashes from the bug—the harness notices, restarts it, and Claude continues without asking for help.

---

## Development Cycle

- An agent should be able to rebuild RepoQL and use the new version within 30 seconds
- An agent should be able to see build errors and warnings directly in the conversation
- An agent should be able to deploy changes and have subsequent tool calls use the new deployment
- An agent should be able to run tests and see results without leaving the conversation

---

## Lifecycle Management

- An agent should be able to invoke tools without knowing whether the host is running
- An agent should be able to restart the host when code changes require it
- An agent should be able to recover from transient host failures without human intervention
- An agent should be able to know when a failure requires human intervention and why
- An agent should be able to see the current state of all managed processes

---

## Visibility

- An agent should be able to query structured logs filtered by time, level, or content
- An agent should be able to see distributed traces for recent operations
- An agent should be able to identify which trace corresponds to a specific tool call
- An agent should be able to access visual telemetry tools when inspection is needed
- An agent should be able to explore the system interactively when conversation isn't enough
- An agent should be able to see why a tool call failed, with actionable context

---

## Continuity

- An agent should be able to keep working after managed services restart
- An agent should not need human intervention to reconnect to services
- An agent should be able to know if another session is mid-deploy and wait or warn

---

## Human Experience

- A human should be able to see what the harness is doing without asking
- A human should be able to intervene without breaking the agent's flow
- A human should be able to trust the agent is using the version they expect

---

## Tool Access

- An agent should be able to use all RepoQL MCP tools through the harness
- An agent should be able to know if tools are temporarily unavailable and why
- An agent should be able to verify which version of RepoQL is currently active
- An agent should be able to trust that tool calls reach the deployed version

---

## Fail Fast, Fail Loud

The harness controls lifecycle—rebuild, deploy, restart are all harness-initiated. Any *unexpected* exit from RepoQL is a bug that should be loudly surfaced, not silently recovered.

- An agent should see unexpected exits immediately with full context
- An agent should get crash details: exit code, stack traces, recent logs, last operation
- An agent should be able to distinguish "harness restarted it" from "it crashed"
- An agent should never wonder "did it exit on purpose?"

**The rule:** If the harness didn't ask RepoQL to stop, and it stopped, that's a bug. Surface it. Don't retry. Don't hide it. The whole point of iterating is to find and fix these.

---

## Boundaries

The harness is for development iteration, not:
- Production deployments
- CI/CD pipelines
- Replacing the IDE for humans
- Long-running unattended operation
- Hiding bugs behind retry logic

---

## Success Signals

We've arrived when:
- An agent completes a build-test-fix cycle without mentioning process management
- A human observes an agent iterating for 30 minutes without intervention
- Zero "please reconnect" messages in a typical development session
- The harness recovers from host crashes faster than the human notices

---

## What Great Looks Like

| Declaration | Why It Matters |
|-------------|----------------|
| Rebuild and use within 30 seconds | Tight feedback loop; no context switching |
| Recover from transient failures | Human doesn't babysit; agent stays productive |
| Query logs in conversation | Debug without browser; stay in flow |
| Identify trace for specific call | Understand what happened, not just what failed |
| Tools work without lifecycle awareness | Agent thinks about the problem, not the infrastructure |
| Human sees progress without asking | Trust without micromanagement |

---

## Anti-Patterns

| Don't | Do Instead |
|-------|------------|
| "The harness manages process lifecycle" | "An agent should be able to restart the host when needed" |
| "Logs are exposed via MCP resources" | "An agent should be able to query logs filtered by time and level" |
| "Auto-reconnection on failure" | "An agent should be able to keep working after services restart" |
| "Opens the Aspire dashboard" | "An agent should be able to access visual telemetry tools" |

---

*The agent stays in flow. The infrastructure stays invisible. The human stays informed.*
