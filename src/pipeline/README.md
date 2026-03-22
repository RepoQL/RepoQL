# pipeline/

How files become graph data. Depends on contracts + infra + data.

Put it here if it discovers, classifies, parses, or analyzes files — the indexing pipeline, epoch tracking, and all format loaders. Format loaders live in `Formats/` and return `DocumentModel` records without touching the database directly.

Projects: `Indexing`, `Formats/*`.
