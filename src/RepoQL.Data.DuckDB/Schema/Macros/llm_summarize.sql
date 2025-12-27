-- LLM-powered summarization of query results.
-- Takes JSON array of results, caller's intent, and optional token limit.
-- Returns a text summary addressing the caller's intent.
--
-- Example:
--   SELECT llm_summarize(
--       (SELECT json_group_array(json_object(
--           'uri', uri, 'headline', headline, 'summary', summary
--       )) FROM search('authentication', k := 10)),
--       'Find JWT validation patterns'
--   );
--
CREATE OR REPLACE MACRO llm_summarize(json_data, intent, max_tokens := 500) AS (
    _llm_summarize_internal(json_data, intent, max_tokens)
);
