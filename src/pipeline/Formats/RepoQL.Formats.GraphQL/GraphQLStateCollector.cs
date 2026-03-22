using GraphQLParser.AST;

namespace RepoQL.Formats.GraphQL;

internal sealed class GraphQLStateCollector
{
    private const int MaxTopLevelFields = 8;

    public GraphQLOperationInfo CreateOperation(GraphQLOperationDefinition operation)
    {
        var nodeId = Guid.NewGuid();
        var spanId = Guid.NewGuid();
        var topFields = new List<string>();
        var fragments = new List<GraphQLFragmentUsage>();
        var variableUsages = new List<GraphQLVariableUsage>();
        CollectSelectionDetails(operation.SelectionSet, topFields, fragments, variableUsages, isRoot: true);

        var variables = new List<GraphQLVariableInfo>();
        if (operation.Variables is { Count: > 0 })
        {
            foreach (var definition in operation.Variables.Items)
            {
                if (definition is null) continue;
                var name = NameOf(definition.Variable?.Name);
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                variables.Add(new GraphQLVariableInfo(
                    name!,
                    RenderType(definition.Type),
                    definition.Type is GraphQLNonNullType,
                    definition.DefaultValue is not null,
                    SpanOf(definition)));

                CollectValueVariables(definition.DefaultValue, variableUsages);
                CollectDirectiveVariables(definition.Directives, variableUsages);
            }
        }

        CollectDirectiveVariables(operation.Directives, variableUsages);

        var operationName = NameOf(operation.Name);
        var kind = operation.Operation switch
        {
            OperationType.Mutation => GraphQLOperationKind.Mutation,
            OperationType.Subscription => GraphQLOperationKind.Subscription,
            OperationType.Query when string.IsNullOrWhiteSpace(operationName) => GraphQLOperationKind.Anonymous,
            OperationType.Query => GraphQLOperationKind.Query,
            _ => string.IsNullOrWhiteSpace(operationName) ? GraphQLOperationKind.Anonymous : GraphQLOperationKind.Query
        };

        return new GraphQLOperationInfo(
            NodeId: nodeId,
            SpanId: spanId,
            Name: operationName,
            Kind: kind,
            Variables: variables,
            TopLevelFields: topFields,
            FragmentUsages: fragments,
            VariableUsages: variableUsages,
            DirectiveCount: operation.Directives?.Count ?? 0,
            Span: SpanOf(operation));
    }

    public GraphQLFragmentInfo CreateFragment(GraphQLFragmentDefinition fragment)
    {
        var nodeId = Guid.NewGuid();
        var spanId = Guid.NewGuid();
        var fragments = new List<GraphQLFragmentUsage>();
        var dummyFields = new List<string>();
        var variables = new List<GraphQLVariableUsage>();
        CollectSelectionDetails(fragment.SelectionSet, dummyFields, fragments, variables, isRoot: false);
        CollectDirectiveVariables(fragment.Directives, variables);

        return new GraphQLFragmentInfo(
            NodeId: nodeId,
            SpanId: spanId,
            Name: NameOf(fragment.FragmentName?.Name) ?? string.Empty,
            TypeCondition: fragment.TypeCondition?.Type.Name.Value.ToString(),
            FragmentUsages: fragments,
            DirectiveCount: fragment.Directives?.Count ?? 0,
            Span: SpanOf(fragment));
    }

    public GraphQLTypeInfo CreateType(GraphQLObjectTypeDefinition definition)
    {
        var implements = definition.Interfaces?.Items?
            .Select(i => NameOf(i.Name))
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Select(static s => s!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();

        var fields = definition.Fields?.Select(CreateFieldInfo).ToArray() ?? Array.Empty<GraphQLFieldInfo>();

        return new GraphQLTypeInfo(
            NodeId: Guid.NewGuid(),
            SpanId: Guid.NewGuid(),
            Kind: GraphQLTypeKind.Object,
            Name: NameOf(definition.Name) ?? string.Empty,
            Implements: implements,
            Fields: fields,
            EnumValues: Array.Empty<GraphQLEnumValueInfo>(),
            UnionMembers: Array.Empty<string>(),
            Span: SpanOf(definition),
            HasDescription: HasText(definition.Description));
    }

    public GraphQLTypeInfo CreateType(GraphQLInterfaceTypeDefinition definition)
    {
        var implements = definition.Interfaces?.Items?
            .Select(i => NameOf(i.Name))
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Select(static s => s!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();

        var fields = definition.Fields?.Select(CreateFieldInfo).ToArray() ?? Array.Empty<GraphQLFieldInfo>();

        return new GraphQLTypeInfo(
            NodeId: Guid.NewGuid(),
            SpanId: Guid.NewGuid(),
            Kind: GraphQLTypeKind.Interface,
            Name: NameOf(definition.Name) ?? string.Empty,
            Implements: implements,
            Fields: fields,
            EnumValues: Array.Empty<GraphQLEnumValueInfo>(),
            UnionMembers: Array.Empty<string>(),
            Span: SpanOf(definition),
            HasDescription: HasText(definition.Description));
    }

    public GraphQLTypeInfo CreateType(GraphQLInputObjectTypeDefinition definition)
    {
        var fields = definition.Fields?.Select(CreateFieldInfo).ToArray() ?? Array.Empty<GraphQLFieldInfo>();

        return new GraphQLTypeInfo(
            NodeId: Guid.NewGuid(),
            SpanId: Guid.NewGuid(),
            Kind: GraphQLTypeKind.InputObject,
            Name: NameOf(definition.Name) ?? string.Empty,
            Implements: Array.Empty<string>(),
            Fields: fields,
            EnumValues: Array.Empty<GraphQLEnumValueInfo>(),
            UnionMembers: Array.Empty<string>(),
            Span: SpanOf(definition),
            HasDescription: HasText(definition.Description));
    }

    public GraphQLTypeInfo CreateType(GraphQLEnumTypeDefinition definition)
    {
        var values = new List<GraphQLEnumValueInfo>();
        if (definition.Values is { Count: > 0 })
        {
            foreach (var value in definition.Values)
            {
                if (value is null) continue;
                var deprecated = TryGetDeprecation(value.Directives, out var reason);
                values.Add(new GraphQLEnumValueInfo(
                    NodeId: Guid.NewGuid(),
                    SpanId: Guid.NewGuid(),
                    Name: NameOf(value.Name) ?? string.Empty,
                    IsDeprecated: deprecated,
                    DeprecationReason: reason,
                    Span: SpanOf(value),
                    HasDescription: HasText(value.Description)));
            }
        }

        return new GraphQLTypeInfo(
            NodeId: Guid.NewGuid(),
            SpanId: Guid.NewGuid(),
            Kind: GraphQLTypeKind.Enum,
            Name: NameOf(definition.Name) ?? string.Empty,
            Implements: Array.Empty<string>(),
            Fields: Array.Empty<GraphQLFieldInfo>(),
            EnumValues: values,
            UnionMembers: Array.Empty<string>(),
            Span: SpanOf(definition),
            HasDescription: HasText(definition.Description));
    }

    public GraphQLTypeInfo CreateType(GraphQLUnionTypeDefinition definition)
    {
        var members = definition.Types?.Items?
            .Select(t => NameOf(t.Name))
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Select(static s => s!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();

        return new GraphQLTypeInfo(
            NodeId: Guid.NewGuid(),
            SpanId: Guid.NewGuid(),
            Kind: GraphQLTypeKind.Union,
            Name: NameOf(definition.Name) ?? string.Empty,
            Implements: Array.Empty<string>(),
            Fields: Array.Empty<GraphQLFieldInfo>(),
            EnumValues: Array.Empty<GraphQLEnumValueInfo>(),
            UnionMembers: members,
            Span: SpanOf(definition),
            HasDescription: HasText(definition.Description));
    }

    public GraphQLTypeInfo CreateType(GraphQLScalarTypeDefinition definition)
        => new(
            NodeId: Guid.NewGuid(),
            SpanId: Guid.NewGuid(),
            Kind: GraphQLTypeKind.Scalar,
            Name: NameOf(definition.Name) ?? string.Empty,
            Implements: Array.Empty<string>(),
            Fields: Array.Empty<GraphQLFieldInfo>(),
            EnumValues: Array.Empty<GraphQLEnumValueInfo>(),
            UnionMembers: Array.Empty<string>(),
            Span: SpanOf(definition),
            HasDescription: HasText(definition.Description));

    public GraphQLTypeInfo CreateType(GraphQLObjectTypeExtension extension)
    {
        var implements = extension.Interfaces?.Items?
            .Select(i => NameOf(i.Name))
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Select(static s => s!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();

        var fields = extension.Fields?.Items?.Select(CreateFieldInfo).ToArray() ?? Array.Empty<GraphQLFieldInfo>();

        return new GraphQLTypeInfo(
            NodeId: Guid.NewGuid(),
            SpanId: Guid.NewGuid(),
            Kind: GraphQLTypeKind.Object,
            Name: NameOf(extension.Name) ?? string.Empty,
            Implements: implements,
            Fields: fields,
            EnumValues: Array.Empty<GraphQLEnumValueInfo>(),
            UnionMembers: Array.Empty<string>(),
            Span: SpanOf(extension),
            HasDescription: false);
    }

    public GraphQLTypeInfo CreateType(GraphQLInterfaceTypeExtension extension)
    {
        var implements = extension.Interfaces?.Items?
            .Select(i => NameOf(i.Name))
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Select(static s => s!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();

        var fields = extension.Fields?.Items?.Select(CreateFieldInfo).ToArray() ?? Array.Empty<GraphQLFieldInfo>();

        return new GraphQLTypeInfo(
            NodeId: Guid.NewGuid(),
            SpanId: Guid.NewGuid(),
            Kind: GraphQLTypeKind.Interface,
            Name: NameOf(extension.Name) ?? string.Empty,
            Implements: implements,
            Fields: fields,
            EnumValues: Array.Empty<GraphQLEnumValueInfo>(),
            UnionMembers: Array.Empty<string>(),
            Span: SpanOf(extension),
            HasDescription: false);
    }

    public GraphQLTypeInfo CreateType(GraphQLInputObjectTypeExtension extension)
    {
        var fields = extension.Fields?.Items?.Select(CreateFieldInfo).ToArray() ?? Array.Empty<GraphQLFieldInfo>();

        return new GraphQLTypeInfo(
            NodeId: Guid.NewGuid(),
            SpanId: Guid.NewGuid(),
            Kind: GraphQLTypeKind.InputObject,
            Name: NameOf(extension.Name) ?? string.Empty,
            Implements: Array.Empty<string>(),
            Fields: fields,
            EnumValues: Array.Empty<GraphQLEnumValueInfo>(),
            UnionMembers: Array.Empty<string>(),
            Span: SpanOf(extension),
            HasDescription: false);
    }

    public GraphQLTypeInfo CreateType(GraphQLEnumTypeExtension extension)
    {
        var values = new List<GraphQLEnumValueInfo>();
        if (extension.Values?.Items is { Count: > 0 })
        {
            foreach (var value in extension.Values.Items)
            {
                if (value is null) continue;
                var deprecated = TryGetDeprecation(value.Directives, out var reason);
                values.Add(new GraphQLEnumValueInfo(
                    NodeId: Guid.NewGuid(),
                    SpanId: Guid.NewGuid(),
                    Name: NameOf(value.Name) ?? string.Empty,
                    IsDeprecated: deprecated,
                    DeprecationReason: reason,
                    Span: SpanOf(value),
                    HasDescription: false));
            }
        }

        return new GraphQLTypeInfo(
            NodeId: Guid.NewGuid(),
            SpanId: Guid.NewGuid(),
            Kind: GraphQLTypeKind.Enum,
            Name: NameOf(extension.Name) ?? string.Empty,
            Implements: Array.Empty<string>(),
            Fields: Array.Empty<GraphQLFieldInfo>(),
            EnumValues: values,
            UnionMembers: Array.Empty<string>(),
            Span: SpanOf(extension),
            HasDescription: false);
    }

    public GraphQLTypeInfo CreateType(GraphQLUnionTypeExtension extension)
    {
        var members = extension.Types?.Items?
            .Select(t => NameOf(t.Name))
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Select(static s => s!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();

        return new GraphQLTypeInfo(
            NodeId: Guid.NewGuid(),
            SpanId: Guid.NewGuid(),
            Kind: GraphQLTypeKind.Union,
            Name: NameOf(extension.Name) ?? string.Empty,
            Implements: Array.Empty<string>(),
            Fields: Array.Empty<GraphQLFieldInfo>(),
            EnumValues: Array.Empty<GraphQLEnumValueInfo>(),
            UnionMembers: members,
            Span: SpanOf(extension),
            HasDescription: false);
    }

    public GraphQLTypeInfo CreateType(GraphQLScalarTypeExtension extension)
        => new(
            NodeId: Guid.NewGuid(),
            SpanId: Guid.NewGuid(),
            Kind: GraphQLTypeKind.Scalar,
            Name: NameOf(extension.Name) ?? string.Empty,
            Implements: Array.Empty<string>(),
            Fields: Array.Empty<GraphQLFieldInfo>(),
            EnumValues: Array.Empty<GraphQLEnumValueInfo>(),
            UnionMembers: Array.Empty<string>(),
            Span: SpanOf(extension),
            HasDescription: false);

    public GraphQLDirectiveInfo CreateDirective(GraphQLDirectiveDefinition definition)
    {
        var locations = definition.Locations?.Items?
            .Select(loc => loc.ToString())
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .ToArray() ?? Array.Empty<string>();

        return new GraphQLDirectiveInfo(
            NodeId: Guid.NewGuid(),
            SpanId: Guid.NewGuid(),
            Name: NameOf(definition.Name) ?? string.Empty,
            IsRepeatable: definition.Repeatable,
            Locations: locations,
            ArgumentCount: definition.Arguments?.Count ?? 0,
            Span: SpanOf(definition),
            HasDescription: HasText(definition.Description));
    }

    private void CollectSelectionDetails(
        GraphQLSelectionSet? selectionSet,
        List<string> topLevelFields,
        List<GraphQLFragmentUsage> fragments,
        List<GraphQLVariableUsage> variables,
        bool isRoot)
    {
        if (selectionSet?.Selections is null) return;
        foreach (var selection in selectionSet.Selections)
        {
            switch (selection)
            {
                case GraphQLField field:
                {
                    if (isRoot && topLevelFields.Count < MaxTopLevelFields)
                    {
                        var fieldName = NameOf(field.Alias?.Name) ?? NameOf(field.Name);
                        if (!string.IsNullOrWhiteSpace(fieldName) &&
                            !topLevelFields.Contains(fieldName!, StringComparer.OrdinalIgnoreCase))
                        {
                            topLevelFields.Add(fieldName!);
                        }
                    }

                    CollectArguments(field.Arguments, variables);
                    CollectDirectiveVariables(field.Directives, variables);
                    CollectSelectionDetails(field.SelectionSet, topLevelFields, fragments, variables, false);
                    break;
                }

                case GraphQLFragmentSpread spread:
                {
                    var name = NameOf(spread.FragmentName?.Name);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        fragments.Add(new GraphQLFragmentUsage(Guid.NewGuid(), name!, SpanOf(spread)));
                    }
                    CollectDirectiveVariables(spread.Directives, variables);
                    break;
                }

                case GraphQLInlineFragment inline:
                {
                    CollectDirectiveVariables(inline.Directives, variables);
                    CollectSelectionDetails(inline.SelectionSet, topLevelFields, fragments, variables, false);
                    break;
                }
            }
        }
    }

    private void CollectArguments(GraphQLArguments? arguments, List<GraphQLVariableUsage> variables)
    {
        if (arguments?.Items is null) return;
        foreach (var argument in arguments.Items)
        {
            CollectValueVariables(argument.Value, variables);
        }
    }

    private void CollectDirectiveVariables(GraphQLDirectives? directives, List<GraphQLVariableUsage> variables)
    {
        if (directives?.Items is null) return;
        foreach (var directive in directives.Items)
        {
            CollectArguments(directive.Arguments, variables);
        }
    }

    private void CollectValueVariables(GraphQLValue? value, List<GraphQLVariableUsage> variables)
    {
        switch (value)
        {
            case null:
                return;
            case GraphQLVariable variable:
            {
                var name = NameOf(variable.Name);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    variables.Add(new GraphQLVariableUsage(Guid.NewGuid(), name!, SpanOf(variable)));
                }
                break;
            }
            case GraphQLListValue list:
            {
                if (list.Values is null) return;
                foreach (var item in list.Values)
                    CollectValueVariables(item, variables);
                break;
            }
            case GraphQLObjectValue obj:
            {
                if (obj.Fields is null) return;
                foreach (var field in obj.Fields)
                    CollectValueVariables(field.Value, variables);
                break;
            }
        }
    }

    private static bool TryGetDeprecation(GraphQLDirectives? directives, out string? reason)
    {
        reason = null;
        if (directives?.Items is null) return false;
        foreach (var directive in directives.Items)
        {
            if (!string.Equals(NameOf(directive.Name), "deprecated", StringComparison.OrdinalIgnoreCase))
                continue;

            if (directive.Arguments?.Items != null)
            {
                foreach (var argument in directive.Arguments.Items)
                {
                    if (!string.Equals(NameOf(argument.Name), "reason", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (argument.Value is GraphQLStringValue str)
                    {
                        reason = str.Value.ToString();
                        break;
                    }
                }
            }

            return true;
        }

        return false;
    }

    private static GraphQLFieldInfo CreateFieldInfo(GraphQLFieldDefinition field)
    {
        var deprecated = TryGetDeprecation(field.Directives, out var reason);
        return new GraphQLFieldInfo(
            NodeId: Guid.NewGuid(),
            SpanId: Guid.NewGuid(),
            Name: NameOf(field.Name) ?? string.Empty,
            Type: RenderType(field.Type),
            ArgumentCount: field.Arguments?.Count ?? 0,
            IsDeprecated: deprecated,
            DeprecationReason: reason,
            HasDescription: HasText(field.Description),
            Span: SpanOf(field));
    }

    private static GraphQLFieldInfo CreateFieldInfo(GraphQLInputValueDefinition field)
    {
        var deprecated = TryGetDeprecation(field.Directives, out var reason);
        return new GraphQLFieldInfo(
            NodeId: Guid.NewGuid(),
            SpanId: Guid.NewGuid(),
            Name: NameOf(field.Name) ?? string.Empty,
            Type: RenderType(field.Type),
            ArgumentCount: 0,
            IsDeprecated: deprecated,
            DeprecationReason: reason,
            HasDescription: HasText(field.Description),
            Span: SpanOf(field));
    }

    private static string RenderType(GraphQLType type) => type switch
    {
        GraphQLNonNullType nonNull => RenderType(nonNull.Type) + "!",
        GraphQLListType list => "[" + RenderType(list.Type) + "]",
        GraphQLNamedType named => NameOf(named.Name) ?? string.Empty,
        _ => type.GetType().Name
    };

    private static GraphQLSpan SpanOf(ASTNode node)
    {
        var location = node.Location;
        return new GraphQLSpan(location.Start, location.End);
    }

    private static string? NameOf(GraphQLName? name)
        => name is null
            ? null
            : name.StringValue ?? name.Value.ToString();

    private static bool HasText(GraphQLDescription? description)
        => description is not null && !string.IsNullOrWhiteSpace(description.Value.ToString());
}
