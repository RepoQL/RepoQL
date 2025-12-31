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
            Provide a concise summary (~{maxTokens} tokens max) that directly addresses the caller's intent.

            If nuances exist (things the caller might not know, tangential context), surface them. If the question isn't answerable from this data, say so concisely and note what's missing if known.

            YOU MUST cite every claim using URIs from the results. For line references use format: uri#line=N,M
            Correct: file:///src/Foo.cs#line=42,50
            Wrong: file:///src/Foo.cs#42,50 (missing "line=")

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

            If nuances exist (things the caller might not know, tangential context), surface them. If the question isn't answerable from this data, say so concisely and note what's missing if known.

            ## Output Format
            For each relevant finding:

            <uri>#line=N,M
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

            If nuances exist (things the caller might not know, tangential context), surface them. If the question isn't answerable from this data, say so concisely and note what's missing if known.

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
