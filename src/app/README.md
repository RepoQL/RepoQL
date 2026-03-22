# app/

Composition, wiring, and user surfaces. Depends on everything.

Put it here if it composes the system or presents it to users — the gRPC host, the MCP server, the CLI, the shared client infrastructure, the Aspire dev orchestrator. This is where DI wiring, startup, and transport-specific code live.

Projects: `Core` (composition root), `Client` (shared client infra), `McpServer` (MCP tools), `ConsoleApp` (host + CLI), `Documentation` (help:// content), `Orchestrator` (Aspire dev).
