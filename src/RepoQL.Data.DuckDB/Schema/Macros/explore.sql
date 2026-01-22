-- explore(keywords, tokens, intent, scope, boost, penalize) -> formatted_text
-- Executes an explore query and returns TOON-formatted output.
-- Token budget controls result count via distribution analysis (no k limit).
--
-- Example:
--   SELECT explore('embeddings', tokens := 1000);
--   SELECT explore('authentication', tokens := 1500, intent := 'Examine');
--   SELECT explore('error handling', tokens := 2000, scope := 'file:///src/**');
--
-- Compose with LLM:
--   SELECT ask(explore('embeddings', tokens := 1000), 'Explain how embeddings work', 200);
--
CREATE OR REPLACE MACRO explore(
    keywords,
    tokens := 1000,
    intent := 'Find',
    scope := NULL,
    boost := NULL,
    penalize := NULL
) AS (
    -- _explore_internal takes (keywords, intent, options_json)
    -- All numeric/optional params go in JSON to work around DuckDB.NET type issues
    _explore_internal(
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
