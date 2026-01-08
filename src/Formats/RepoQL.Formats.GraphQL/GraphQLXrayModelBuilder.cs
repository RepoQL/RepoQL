using System.Globalization;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;

namespace RepoQL.Formats.GraphQL;

internal static class GraphQLXrayModelBuilder
{
    private const int SummaryDirectiveLimit = 3;
    private const int StructureFieldLimit = 12;
    private const int StructureVariableLimit = 8;
    private const int StructureEnumLimit = 12;
    private const int StructureUnionLimit = 12;

    public static Dictionary<string, object?> Build(DocumentModel document, GraphQLDocumentState state)
    {
        return new Dictionary<string, object?>
        {
            ["file_name"] = GetFileName(document.Uri),
            ["media_kind"] = state.MediaType.Kind ?? string.Empty,
            ["media_base"] = $"{state.MediaType.Type}/{state.MediaType.Subtype}",
            ["size_bytes"] = state.Size,
            ["token_count"] = TokenEstimator.EstimateTokensSafe(document.Text),
            ["stats"] = BuildStats(state),
            ["operations"] = BuildOperations(state),
            ["fragments"] = BuildFragments(state),
            ["types"] = BuildTypes(state),
            ["directives"] = BuildDirectives(state)
        };
    }

    private static Dictionary<string, object?> BuildStats(GraphQLDocumentState state)
    {
        return new Dictionary<string, object?>
        {
            ["operation_counts"] = BuildOperationCounts(state),
            ["fragment_count"] = state.Fragments.Count,
            ["type_counts"] = BuildTypeCounts(state),
            ["directive_count"] = state.Directives.Count,
            ["has_schema"] = state.HasSchemaDefinition
        };
    }

    private static List<Dictionary<string, object?>> BuildOperationCounts(GraphQLDocumentState state)
    {
        var list = new List<Dictionary<string, object?>>(3);
        if (state.Counts.QueryCount > 0)
            list.Add(NewCount("queries", state.Counts.QueryCount));
        if (state.Counts.MutationCount > 0)
            list.Add(NewCount("mutations", state.Counts.MutationCount));
        if (state.Counts.SubscriptionCount > 0)
            list.Add(NewCount("subscriptions", state.Counts.SubscriptionCount));
        return list;

        static Dictionary<string, object?> NewCount(string label, int count)
            => new()
            {
                ["label"] = label,
                ["count"] = count
            };
    }

    private static List<Dictionary<string, object?>> BuildTypeCounts(GraphQLDocumentState state)
    {
        var list = new List<Dictionary<string, object?>>(6);
        if (state.Counts.ObjectTypeCount > 0)
            list.Add(NewCount("object", state.Counts.ObjectTypeCount));
        if (state.Counts.InterfaceTypeCount > 0)
            list.Add(NewCount("interface", state.Counts.InterfaceTypeCount));
        if (state.Counts.InputTypeCount > 0)
            list.Add(NewCount("input", state.Counts.InputTypeCount));
        if (state.Counts.EnumTypeCount > 0)
            list.Add(NewCount("enum", state.Counts.EnumTypeCount));
        if (state.Counts.UnionTypeCount > 0)
            list.Add(NewCount("union", state.Counts.UnionTypeCount));
        if (state.Counts.ScalarTypeCount > 0)
            list.Add(NewCount("scalar", state.Counts.ScalarTypeCount));
        return list;

        static Dictionary<string, object?> NewCount(string label, int count)
            => new()
            {
                ["label"] = label,
                ["count"] = count
            };
    }

    private static List<Dictionary<string, object?>> BuildOperations(GraphQLDocumentState state)
    {
        var operations = new List<Dictionary<string, object?>>(state.Operations.Count);
        foreach (var operation in state.Operations.OrderBy(o => o.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            var displayName = string.IsNullOrWhiteSpace(operation.Name) ? "(anonymous)" : operation.Name!;
            var topFields = operation.TopLevelFields.Take(StructureFieldLimit).ToList();
            var topFieldsTruncated = operation.TopLevelFields.Count > topFields.Count;

            var variables = operation.Variables.Take(StructureVariableLimit).Select(v => new Dictionary<string, object?>
            {
                ["name"] = v.Name,
                ["type"] = v.Type,
                ["is_required"] = v.IsNonNull,
                ["has_default"] = v.HasDefaultValue
            }).ToList();
            var variablesTruncated = operation.Variables.Count > variables.Count;

            operations.Add(new Dictionary<string, object?>
            {
                ["name"] = operation.Name ?? string.Empty,
                ["display_name"] = displayName,
                ["kind"] = (operation.Kind == GraphQLOperationKind.Anonymous ? GraphQLOperationKind.Query : operation.Kind).ToString().ToLowerInvariant(),
                ["variable_count"] = operation.Variables.Count,
                ["variables"] = variables,
                ["variables_truncated"] = variablesTruncated,
                ["fragment_count"] = operation.FragmentUsages.Count,
                ["directive_count"] = operation.DirectiveCount,
                ["top_fields"] = topFields,
                ["top_fields_truncated"] = topFieldsTruncated
            });
        }

        return operations;
    }

    private static List<Dictionary<string, object?>> BuildFragments(GraphQLDocumentState state)
    {
        return state.Fragments
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new Dictionary<string, object?>
            {
                ["name"] = f.Name,
                ["type_condition"] = f.TypeCondition,
                ["usage_count"] = f.FragmentUsages.Count
            })
            .ToList();
    }

    private static List<Dictionary<string, object?>> BuildTypes(GraphQLDocumentState state)
    {
        var list = new List<Dictionary<string, object?>>(state.Types.Count);
        foreach (var type in state.Types.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            var fields = type.Fields.Take(StructureFieldLimit).Select(field => new Dictionary<string, object?>
            {
                ["name"] = field.Name,
                ["type"] = field.Type,
                ["is_deprecated"] = field.IsDeprecated,
                ["deprecation_reason"] = field.DeprecationReason
            }).ToList();
            var fieldsTruncated = type.Fields.Count > fields.Count;

            var enumValues = type.EnumValues.Take(StructureEnumLimit).Select(value => new Dictionary<string, object?>
            {
                ["name"] = value.Name,
                ["is_deprecated"] = value.IsDeprecated,
                ["deprecation_reason"] = value.DeprecationReason
            }).ToList();
            var enumValuesTruncated = type.EnumValues.Count > enumValues.Count;

            var unionMembers = type.UnionMembers.Take(StructureUnionLimit).ToList();
            var unionMembersTruncated = type.UnionMembers.Count > unionMembers.Count;

            list.Add(new Dictionary<string, object?>
            {
                ["name"] = type.Name,
                ["kind"] = type.Kind.ToString().ToLowerInvariant(),
                ["implements"] = type.Implements,
                ["field_count"] = type.Fields.Count,
                ["fields"] = fields,
                ["fields_truncated"] = fieldsTruncated,
                ["enum_value_count"] = type.EnumValues.Count,
                ["enum_values"] = enumValues,
                ["enum_values_truncated"] = enumValuesTruncated,
                ["union_member_count"] = type.UnionMembers.Count,
                ["union_members"] = unionMembers,
                ["union_members_truncated"] = unionMembersTruncated
            });
        }
        return list;
    }

    private static List<Dictionary<string, object?>> BuildDirectives(GraphQLDocumentState state)
    {
        return state.Directives
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d => new Dictionary<string, object?>
            {
                ["name"] = d.Name,
                ["repeatable"] = d.IsRepeatable,
                ["locations"] = d.Locations,
                ["argument_count"] = d.ArgumentCount
            })
            .Take(SummaryDirectiveLimit * 5) // keep structure manageable even for large schema
            .ToList();
    }

    private static string GetFileName(RepoUri uri)
    {
        try
        {
            if (uri.IsFile)
            {
                var localPath = uri.LocalPath;
                if (!string.IsNullOrEmpty(localPath))
                    return Path.GetFileName(localPath);
            }
        }
        catch
        {
            // ignore and fall back to absolute path parsing
        }

        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        var idx = path.LastIndexOf('/');
        return idx >= 0 ? path[(idx + 1)..] : path;
    }

    private static string FormatBytes(long bytes)
    {
        const long KB = 1024;
        const long MB = KB * 1024;
        const long GB = MB * 1024;
        if (bytes >= GB) return string.Format(CultureInfo.InvariantCulture, "{0:0.##} GB", bytes / (double)GB);
        if (bytes >= MB) return string.Format(CultureInfo.InvariantCulture, "{0:0.##} MB", bytes / (double)MB);
        if (bytes >= KB) return string.Format(CultureInfo.InvariantCulture, "{0:0.##} KB", bytes / (double)KB);
        return string.Format(CultureInfo.InvariantCulture, "{0} B", bytes);
    }
}
