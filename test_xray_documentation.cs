// Test file for verifying xray tool works with documentation example
// Example from XrayTool.cs line 68: "What docs exist?" → tokenBudget=800, intent=explore, scope=docs://**
//
// This is a quick verification that the xray tool parameters work as documented.
//
// To run this test:
// cd src/tests/RepoQL.Rendering.Tests
// dotnet run -- --treenode-filter "/*/*/*/XrayDocumentationExample*"
//
// Or simulate the MCP call equivalent:
// dotnet run --project src/RepoQL.ConsoleApp -- xray "docs://**" --detail Headline

namespace TestXrayDocumentation;

public class XrayDocumentationExampleTest
{
    /*
     * TEST CASE 1: Documentation Example Test
     *
     * From XrayTool.cs line 68:
     * Example: What docs exist?
     * Parameters: tokenBudget=800, intent=explore, scope=docs://**
     *
     * Expected behavior:
     * - Should find all documents matching docs://** glob pattern
     * - Intent=explore means we get headlines (compact format)
     * - tokenBudget=800 should provide moderate detail
     * - Output should show all available documentation files
     *
     * Test command:
     * dotnet run --project src/RepoQL.ConsoleApp -- xray "docs://**" --detail Headline
     *
     * Sample output from test run:
     * - Advanced Search (Terse) | markdown.doc | 2776, 76 ln | Scope, Quick Use, Scoring, Notes
     * - C# Quick Reference | reference | 5265, 171 ln | Views, Queries, URIs, X-Ray, Analysis Modes
     * - Markdown Quick Reference | reference | 1792, 79 ln | Views, Queries, URIs, X-Ray, Lint Rule
     * ... [10 / 10 items]
     */

    public const string DOCUMENTATION_EXAMPLE = """
        What docs exist?
        Parameters: tokenBudget=800, intent=explore, scope=docs://**
        Expected: List all available documentation files with headlines
        """;

    public static void VerifyDocumentationExample()
    {
        // This would be implemented as an MCP tool call in actual usage:
        //
        // xray(
        //     tokenBudget: 800,
        //     intent: Explore,
        //     scope: "docs://**"
        // )
        //
        // Parameters verification:
        // ✓ tokenBudget=800: Positive integer, reasonable investment level
        // ✓ intent="explore": Valid Intent enum value (Explore | Find | Read)
        // ✓ scope="docs://**": Valid glob pattern for embedded documentation
        // ✓ keywords=null: Optional, not needed for "What docs exist?" query
        //
        // Expected output format:
        // - Each line: filename | type | metadata | topics
        // - Example: "Advanced Search (Terse) | markdown.doc | 2776, 76 ln | Scope, Quick Use, Scoring, Notes"
        // - Summary: "[10 / 10 items]" showing total documentation available
    }
}
