using RepoQL.Templating;
using RepoQL.Templating.Filters;

namespace RepoQL.Formats.Cpp;

public sealed class CppXRayGenerator
{
    private readonly LiquidTemplateRenderer _renderer = new(
        assembly: typeof(CppXRayGenerator).Assembly,
        resourceRoot: "RepoQL.Formats.Cpp.Templates",
        configure: StandardFilters.RegisterAll);

    public CppXRayOutput Generate(CppXRayModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var templateModel = new Dictionary<string, object?>
        {
            ["file_name"] = model.FileName,
            ["media_kind"] = model.MediaKind,
            ["line_count"] = model.LineCount,
            ["token_estimate"] = FormatTokenCount(model.TokenCount),
            ["primary_namespace"] = model.PrimaryNamespace ?? string.Empty,
            ["top_level_types"] = string.Join(", ", model.TopLevelTypes.Take(3)),
            ["top_level_functions"] = string.Join(", ", model.TopLevelFunctions.Take(6)),
            ["type_count"] = model.TopLevelTypes.Count,
            ["function_count"] = model.TopLevelFunctions.Count,
            ["structure_lines"] = model.StructureLines,
            ["macro_warning"] = model.MacroWarning ?? string.Empty
        };

        var headline = _renderer.RenderAsync("explore/headline", templateModel).GetAwaiter().GetResult();
        var summary = _renderer.RenderAsync("explore/summary", templateModel).GetAwaiter().GetResult();
        var structure = _renderer.RenderAsync("explore/structure", templateModel).GetAwaiter().GetResult();

        return new CppXRayOutput(
            Headline: headline?.Trim(),
            Summary: summary?.Trim(),
            Structure: structure?.Trim());
    }

    private static string FormatTokenCount(int? tokenCount)
    {
        if (!tokenCount.HasValue)
        {
            return string.Empty;
        }

        if (tokenCount.Value >= 1000)
        {
            return $"~{tokenCount.Value / 1000d:0.#}k tok";
        }

        return $"~{tokenCount.Value} tok";
    }
}

public sealed record CppXRayModel(
    string FileName,
    string MediaKind,
    int LineCount,
    int? TokenCount,
    string? PrimaryNamespace,
    IReadOnlyList<string> TopLevelTypes,
    IReadOnlyList<string> TopLevelFunctions,
    IReadOnlyList<string> StructureLines,
    string? MacroWarning = null);

public sealed record CppXRayOutput(string? Headline, string? Summary, string? Structure);
