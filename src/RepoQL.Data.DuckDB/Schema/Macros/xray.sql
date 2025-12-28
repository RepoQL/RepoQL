-- xray(keywords, tokens, intent, scope, boost, penalize) -> formatted_text
-- Executes an xray query and returns TOON-formatted output.
-- Token budget controls result count via distribution analysis (no k limit).
--
-- Example:
--   SELECT xray('embeddings', tokens := 1000);
--   SELECT xray('authentication', tokens := 1500, intent := 'Examine');
--   SELECT xray('error handling', tokens := 2000, scope := 'file:///src/**');
--
-- Compose with LLM summarization:
--   SELECT llm_summarize(xray('embeddings', tokens := 1000), 'Explain how embeddings work', 200);
--
CREATE OR REPLACE MACRO xray(
    keywords,
    tokens := 1000,
    intent := 'Find',
    scope := NULL,
    boost := NULL,
    penalize := NULL
) AS (
    -- _xray_internal takes (keywords, intent, options_json)
    -- All numeric/optional params go in JSON to work around DuckDB.NET type issues
    _xray_internal(
        keywords,
        intent,
        json_object(
            'tokens', tokens,
            'scope', scope,
            'boost', boost,
            'penalize', penalize
        )
    )
);
