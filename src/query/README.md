# query/

How agents interact with the graph. One project per tool's business logic.

Put it here if it's the pure logic behind a tool — search orchestration, read dispatch, query execution, LLM synthesis, command framework, sandboxed execution. No MCP, no gRPC, no direct DuckDB access. Takes abstractions, returns results.

Projects: `Explore`, `Read`, `Query`, `Explain`, `Commands`, `Sandbox`.
