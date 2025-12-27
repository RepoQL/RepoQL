namespace RepoQL.LLM.Client;

/// <summary>
/// Prompt templates for LLM operations on repository data.
/// </summary>
public static class LlmPromptTemplates
{
    /// <summary>
    /// Builds a prompt for summarizing query results.
    /// </summary>
    public static string BuildSummarizePrompt(string toonData, string intent, int maxTokens)
    {
        return $"""
            You are analyzing query results from a code repository database.

            ## Caller's Intent
            {intent}

            ## Query Results (TOON format - compact tabular notation)
            ```
            {toonData}
            ```

            ## Instructions
            Provide a concise summary (~{maxTokens} tokens max) that:
            1. Directly addresses what the caller hoped to find
            2. Highlights the most relevant findings from the data
            3. Notes any gaps or limitations in what was found
            4. Uses specific URIs/names from the results as references

            Keep the summary actionable and grounded in the actual data.
            Do not use markdown headers. Write in clear, flowing prose.
            """;
    }

    /// <summary>
    /// Builds a prompt for extracting relevant snippets (no tools version).
    /// </summary>
    public static string BuildExtractPrompt(string toonData, string intent)
    {
        return $"""
            You are analyzing query results from a code repository database to extract relevant findings.

            ## Caller's Intent
            {intent}

            ## Query Results (TOON format - compact tabular notation)
            ```
            {toonData}
            ```

            ## Instructions
            Review the query results and produce a markdown report highlighting the most relevant items.

            ## Output Format
            For each relevant finding:

            <uri>
            ```
            <relevant code or structure from the data>
            ```
            <brief explanation if not self-evident>

            End with a synthesis paragraph summarizing the key findings (only if multiple findings and synthesis adds value).

            Focus on quality over quantity. Use the actual data provided.
            """;
    }

    /// <summary>
    /// Builds a prompt for extracting relevant snippets with tool access.
    /// </summary>
    public static string BuildExtractPromptWithTools(string toonData, string intent)
    {
        return $"""
            You are analyzing query results from a code repository database to extract relevant code snippets.

            ## Caller's Intent
            {intent}

            ## Query Results (TOON format - compact tabular notation)
            ```
            {toonData}
            ```

            ## Instructions
            1. Review the query results to identify items relevant to the caller's intent
            2. Use the `read_uri` tool to fetch actual code content for promising items
               - The tool accepts a URI (e.g., `file:///src/Auth.cs` or `file:///src/Auth.cs#line=42`)
               - Optionally specify `context_lines` (default 5) for lines around the target
            3. After gathering code, produce a markdown report

            ## Output Format
            For each relevant finding, output:

            <uri>#line=<start>,<end>
            ```<language>
            <code snippet>
            ```
            <brief explanation if not self-evident>

            End with a synthesis paragraph summarizing the key findings (only if there are multiple findings and a synthesis would add value).

            Focus on the most relevant items. Quality over quantity.
            """;
    }
}
