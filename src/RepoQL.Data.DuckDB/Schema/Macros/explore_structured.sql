-- explore_structured(keywords, tokens, intent, scope, boost, penalize) -> TABLE
-- Returns explore results as structured rows for custom processing.
-- Token budget controls result count via distribution analysis.
--
-- Columns:
--   uri, confidence, kind, headline, structure, snippet, lang, semantic_type, parent_uri, depth
--
-- Example:
--   SELECT * FROM explore_structured('embeddings', tokens := 1000);
--   SELECT uri, confidence FROM explore_structured('auth', tokens := 500) WHERE depth = 0;
--   SELECT uri, headline FROM explore_structured('error handling') WHERE kind IS NULL;
--
-- Custom JSON for LLM:
--   SELECT ask(
--     (SELECT to_json(list(t)) FROM (
--       SELECT uri, headline FROM explore_structured('auth', tokens := 500) WHERE depth = 0
--     ) t),
--     'Summarize authentication approach',
--     200
--   );
--
CREATE OR REPLACE MACRO explore_structured(
    keywords,
    tokens := 1000,
    intent := 'Find',
    scope := NULL,
    boost := NULL,
    penalize := NULL
) AS TABLE (
    SELECT
        j.value->>'uri' AS uri,
        CAST(j.value->>'confidence' AS INTEGER) AS confidence,
        j.value->>'kind' AS kind,
        j.value->>'headline' AS headline,
        j.value->>'structure' AS structure,
        j.value->>'snippet' AS snippet,
        j.value->>'lang' AS lang,
        j.value->>'semantic_type' AS semantic_type,
        j.value->>'parent_uri' AS parent_uri,
        CAST(j.value->>'depth' AS INTEGER) AS depth
    FROM json_each(
        _explore_structured_internal(
            keywords,
            intent,
            json_object(
                'tokens', tokens,
                'scope', scope,
                'boost', boost,
                'penalize', penalize
            )
        )
    ) AS j
    WHERE j.type = 'OBJECT'
);
