-- LLM-powered extraction of relevant code snippets.
-- Takes JSON array of results and caller's intent.
-- Returns a markdown report with code snippets and synthesis.
-- The LLM has access to a read_uri tool to fetch actual code content.
--
-- Example:
--   SELECT llm_extract(
--       (SELECT json_group_array(json_object(
--           'uri', uri, 'headline', headline, 'structure', structure
--       )) FROM search('authentication', k := 10)),
--       'How does authentication work in this codebase?'
--   );
--
CREATE OR REPLACE MACRO llm_extract(json_data, intent) AS (
    _llm_extract_internal(json_data, intent)
);
