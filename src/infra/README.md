# infra/

Low-level plumbing. Depends only on contracts.

Put it here if it's infrastructure that many layers depend on but that has no business logic of its own — file system abstraction, gRPC protocol/transport, templating engine, build-time analyzers.

Projects: `FileSystem`, `Protocol`, `Templating`, `Analyzers`.
