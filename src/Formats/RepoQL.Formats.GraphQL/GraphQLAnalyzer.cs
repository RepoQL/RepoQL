using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using RepoQL.Contracts;
using RepoQL.Contracts.Analysis;

namespace RepoQL.Formats.GraphQL;

public sealed class GraphQLAnalyzer : IFormatAnalyzer
{
    private const string Source = "RepoQL.GraphQL";
    private const string NamedOperationRuleId = "graphql/named-operation";
    private const string UnusedFragmentRuleId = "graphql/unused-fragment";
    private const string UndefinedFragmentRuleId = "graphql/undefined-fragment";
    private const string UndefinedVariableRuleId = "graphql/undefined-variable";
    private const string MissingDescriptionRuleId = "graphql/missing-description";
    private const string DuplicateDefinitionRuleId = "graphql/duplicate-definition";

    public bool Supports(SemanticMediaType mediaType)
        => string.Equals(mediaType.Kind, "graphql.doc", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<AnalysisResult> AnalyzeAsync(
        DocumentModel document,
        AnalyzerContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(context);

        if (!Supports(document.MediaType))
            yield break;

        var state = document.GetMetadataOrDefault<GraphQLDocumentState>(GraphQLLoader.StateMetadataKey);
        if (state is null)
            yield break;

        var settings = context.Settings;

        // Rule: named operations
        var namedOpSeverity = settings.GetRule(NamedOperationRuleId).Severity;
        if (namedOpSeverity != AnalysisSeverity.None)
        {
            var multipleOps = state.Operations.Count > 1;
            foreach (var operation in state.Operations)
            {
                if (cancellationToken.IsCancellationRequested)
                    yield break;

                var requiresName = multipleOps || operation.Kind is GraphQLOperationKind.Mutation or GraphQLOperationKind.Subscription;
                if (!requiresName || !string.IsNullOrWhiteSpace(operation.Name))
                    continue;

                yield return CreateResult(
                    NamedOperationRuleId,
                    namedOpSeverity,
                    "Operation should be named.",
                    operation.NodeId,
                    operation.SpanId,
                    document,
                    new JsonObject
                    {
                        ["kind"] = operation.Kind.ToString().ToLowerInvariant()
                    });
            }
        }

        // Rule: unused fragments
        var unusedFragmentSeverity = settings.GetRule(UnusedFragmentRuleId).Severity;
        if (unusedFragmentSeverity != AnalysisSeverity.None && state.Fragments.Count > 0)
        {
            var fragmentLookup = state.Fragments.ToDictionary(f => f.Name, StringComparer.Ordinal);
            var usedFragments = new HashSet<string>(StringComparer.Ordinal);

            foreach (var operation in state.Operations)
            {
                foreach (var usage in operation.FragmentUsages)
                {
                    if (!string.IsNullOrWhiteSpace(usage.Name))
                        usedFragments.Add(usage.Name);
                }
            }

            // Resolve transitive usage
            var queue = new Queue<string>(usedFragments);
            while (queue.Count > 0)
            {
                var name = queue.Dequeue();
                if (!fragmentLookup.TryGetValue(name, out var fragment))
                    continue;
                foreach (var usage in fragment.FragmentUsages)
                {
                    if (string.IsNullOrWhiteSpace(usage.Name)) continue;
                    if (usedFragments.Add(usage.Name))
                        queue.Enqueue(usage.Name);
                }
            }

            foreach (var fragment in state.Fragments)
            {
                if (cancellationToken.IsCancellationRequested)
                    yield break;

                if (usedFragments.Contains(fragment.Name))
                    continue;

                yield return CreateResult(
                    UnusedFragmentRuleId,
                    unusedFragmentSeverity,
                    $"Fragment '{fragment.Name}' is never used by any operation or fragment.",
                    fragment.NodeId,
                    fragment.SpanId,
                    document,
                    new JsonObject { ["name"] = fragment.Name });
            }
        }

        // Rule: undefined fragments
        var undefinedFragmentSeverity = settings.GetRule(UndefinedFragmentRuleId).Severity;
        if (undefinedFragmentSeverity != AnalysisSeverity.None)
        {
            var fragmentNames = new HashSet<string>(state.Fragments.Select(f => f.Name), StringComparer.Ordinal);

            foreach (var operation in state.Operations)
            {
                foreach (var usage in operation.FragmentUsages)
                {
                    if (cancellationToken.IsCancellationRequested)
                        yield break;

                    if (fragmentNames.Contains(usage.Name))
                        continue;

                    yield return CreateResult(
                        UndefinedFragmentRuleId,
                        undefinedFragmentSeverity,
                        $"Fragment '{usage.Name}' is not defined.",
                        operation.NodeId,
                        usage.UsageSpanId,
                        document,
                        new JsonObject { ["name"] = usage.Name });
                }
            }

            foreach (var fragment in state.Fragments)
            {
                foreach (var usage in fragment.FragmentUsages)
                {
                    if (cancellationToken.IsCancellationRequested)
                        yield break;

                    if (fragmentNames.Contains(usage.Name))
                        continue;

                    yield return CreateResult(
                        UndefinedFragmentRuleId,
                        undefinedFragmentSeverity,
                        $"Fragment '{usage.Name}' is not defined.",
                        fragment.NodeId,
                        usage.UsageSpanId,
                        document,
                        new JsonObject { ["name"] = usage.Name });
                }
            }
        }

        // Rule: undefined variables
        var undefinedVariableSeverity = settings.GetRule(UndefinedVariableRuleId).Severity;
        if (undefinedVariableSeverity != AnalysisSeverity.None)
        {
            foreach (var operation in state.Operations)
            {
                if (cancellationToken.IsCancellationRequested)
                    yield break;

                var definitions = new HashSet<string>(
                    operation.Variables.Select(v => v.Name),
                    StringComparer.Ordinal);

                foreach (var usage in operation.VariableUsages)
                {
                    if (definitions.Contains(usage.Name))
                        continue;

                    yield return CreateResult(
                        UndefinedVariableRuleId,
                        undefinedVariableSeverity,
                        $"Variable '${usage.Name}' is not defined by operation '{operation.Name ?? "(anonymous)"}'.",
                        operation.NodeId,
                        usage.UsageSpanId,
                        document,
                        new JsonObject { ["name"] = usage.Name });
                }
            }
        }

        // Rule: missing description
        var missingDescriptionSeverity = settings.GetRule(MissingDescriptionRuleId).Severity;
        if (missingDescriptionSeverity != AnalysisSeverity.None)
        {
            foreach (var type in state.Types)
            {
                if (cancellationToken.IsCancellationRequested)
                    yield break;

                if (!type.Name.StartsWith("__", StringComparison.Ordinal)) // skip introspection
                {
                    if (!type.HasDescription)
                    {
                        yield return CreateResult(
                            MissingDescriptionRuleId,
                            missingDescriptionSeverity,
                            $"Type '{type.Name}' is missing a description.",
                            type.NodeId,
                            type.SpanId,
                            document,
                            new JsonObject { ["type"] = type.Name });
                    }
                }

                if (type.Kind is GraphQLTypeKind.Object or GraphQLTypeKind.Interface or GraphQLTypeKind.InputObject)
                {
                    foreach (var field in type.Fields)
                    {
                        if (field.HasDescription)
                            continue;

                        yield return CreateResult(
                            MissingDescriptionRuleId,
                            missingDescriptionSeverity,
                            $"Field '{type.Name}.{field.Name}' is missing a description.",
                            field.NodeId,
                            field.SpanId,
                            document,
                            new JsonObject
                            {
                                ["type"] = type.Name,
                                ["field"] = field.Name
                            });
                    }
                }

                if (type.Kind == GraphQLTypeKind.Enum)
                {
                    foreach (var value in type.EnumValues)
                    {
                        if (value.HasDescription)
                            continue;

                        yield return CreateResult(
                            MissingDescriptionRuleId,
                            missingDescriptionSeverity,
                            $"Enum value '{type.Name}.{value.Name}' is missing a description.",
                            value.NodeId,
                            value.SpanId,
                            document,
                            new JsonObject
                            {
                                ["type"] = type.Name,
                                ["field"] = value.Name
                            });
                    }
                }
            }
        }

        // Rule: duplicate definitions
        var duplicateSeverity = settings.GetRule(DuplicateDefinitionRuleId).Severity;
        if (duplicateSeverity != AnalysisSeverity.None)
        {
            foreach (var result in FindDuplicates(state))
            {
                if (cancellationToken.IsCancellationRequested)
                    yield break;

                yield return CreateResult(
                    DuplicateDefinitionRuleId,
                    duplicateSeverity,
                    result.Message,
                    result.NodeId,
                    result.SpanId,
                    document,
                    new JsonObject { ["name"] = result.Name, ["kind"] = result.Kind });
            }
        }

        await Task.CompletedTask;
    }

    private static AnalysisResult CreateResult(
        string ruleId,
        AnalysisSeverity severity,
        string message,
        Guid nodeId,
        Guid spanId,
        DocumentModel document,
        JsonObject? data = null)
        => new()
        {
            SemanticKey = $"{document.Uri}#rule:{ruleId}@node:{nodeId}",
            RuleId = ruleId,
            Source = Source,
            Kind = "lint",
            Severity = severity,
            Message = message,
            Data = data,
            Target = new AnalysisTarget
            {
                NodeId = nodeId,
                SpanId = spanId,
                TargetUri = document.Uri
            }
        };

    private static IEnumerable<DuplicateRecord> FindDuplicates(GraphQLDocumentState state)
    {
        foreach (var duplicate in FindDuplicatesCore(state.Operations.Where(o => !string.IsNullOrWhiteSpace(o.Name)), o => o.Name!, "operation"))
            yield return duplicate with { Message = $"Operation '{duplicate.Name}' is defined multiple times." };

        foreach (var duplicate in FindDuplicatesCore(state.Fragments, f => f.Name, "fragment"))
            yield return duplicate with { Message = $"Fragment '{duplicate.Name}' is defined multiple times." };

        foreach (var duplicate in FindDuplicatesCore(state.Types, t => t.Name, "type"))
            yield return duplicate with { Message = $"Type '{duplicate.Name}' is defined multiple times." };

        foreach (var duplicate in FindDuplicatesCore(state.Directives, d => d.Name, "directive"))
            yield return duplicate with { Message = $"Directive '{duplicate.Name}' is defined multiple times." };
    }

    private static IEnumerable<DuplicateRecord> FindDuplicatesCore<T>(
        IEnumerable<T> items,
        Func<T, string> nameSelector,
        string kind)
        where T : class
    {
        var groups = items
            .GroupBy(nameSelector, StringComparer.Ordinal)
            .Where(g => g.Count() > 1);

        foreach (var group in groups)
        {
            foreach (var item in group.Skip(1))
            {
                if (item is GraphQLOperationInfo op)
                    yield return new DuplicateRecord(group.Key, kind, op.NodeId, op.SpanId, string.Empty);
                else if (item is GraphQLFragmentInfo frag)
                    yield return new DuplicateRecord(group.Key, kind, frag.NodeId, frag.SpanId, string.Empty);
                else if (item is GraphQLTypeInfo type)
                    yield return new DuplicateRecord(group.Key, kind, type.NodeId, type.SpanId, string.Empty);
                else if (item is GraphQLDirectiveInfo directive)
                    yield return new DuplicateRecord(group.Key, kind, directive.NodeId, directive.SpanId, string.Empty);
            }
        }
    }

    private sealed record DuplicateRecord(string Name, string Kind, Guid NodeId, Guid SpanId, string Message);

    public IAsyncEnumerable<AnalysisResult> AnalyzeEmbeddedAsync(EmbeddedFragment fragment, AnalyzerContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        ArgumentNullException.ThrowIfNull(context);
        return EmptyAsync();

        static async IAsyncEnumerable<AnalysisResult> EmptyAsync([EnumeratorCancellation] CancellationToken _ = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
