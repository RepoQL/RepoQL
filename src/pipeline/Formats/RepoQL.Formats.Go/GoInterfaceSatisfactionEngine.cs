namespace RepoQL.Formats.Go;

/// <summary>
/// Purpose: Compute Go interface satisfaction from package-level type/member/embed data.
///
/// Complexity: Builds recursive method sets for interfaces and structs (with embedding,
/// shadowing, pointer/value method rules, and cycle handling), then produces IMPLEMENTS matches.
/// </summary>
public static class GoInterfaceSatisfactionEngine
{
    private static readonly IReadOnlyList<StdlibInterfaceDefinition> StdlibInterfaces =
    [
        new StdlibInterfaceDefinition("error", [new MethodSignature("Error", 0)]),
        new StdlibInterfaceDefinition("fmt.Stringer", [new MethodSignature("String", 0)]),
        new StdlibInterfaceDefinition("io.Reader", [new MethodSignature("Read", 1)]),
        new StdlibInterfaceDefinition("io.Writer", [new MethodSignature("Write", 1)]),
        new StdlibInterfaceDefinition("io.Closer", [new MethodSignature("Close", 0)]),
        new StdlibInterfaceDefinition("sort.Interface",
        [
            new MethodSignature("Len", 0),
            new MethodSignature("Less", 2),
            new MethodSignature("Swap", 2)
        ])
    ];

    public static GoInterfaceSatisfactionResult Compute(
        string packageName,
        IReadOnlyList<GoTypeSnapshot> packageTypes,
        IReadOnlyList<GoMethodSnapshot> packageMethods,
        IReadOnlyList<GoEmbeddingSnapshot> packageEmbeddings,
        IReadOnlyCollection<Guid> candidateTypeIds)
    {
        var context = new ComputationContext(
            packageName,
            packageTypes ?? [],
            packageMethods ?? [],
            packageEmbeddings ?? []);

        return context.Compute(candidateTypeIds ?? []);
    }

    private sealed class ComputationContext
    {
        private readonly string _packageName;
        private readonly Dictionary<Guid, GoTypeSnapshot> _typesById = new();
        private readonly Dictionary<string, GoTypeSnapshot> _typesByQualifiedName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, GoTypeSnapshot> _typesBySimpleName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<MethodSignature>> _methodsByDeclaringType = new(StringComparer.Ordinal);
        private readonly Dictionary<Guid, List<string>> _embeddedTargetsByTypeId = new();
        private readonly Dictionary<Guid, Dictionary<string, MethodSignature>> _interfaceMethodSets = new();
        private readonly Dictionary<Guid, StructMethodSets> _structMethodSets = new();
        private readonly Dictionary<string, GoInterfaceSatisfactionDiagnostic> _diagnostics = new(StringComparer.Ordinal);

        public ComputationContext(
            string packageName,
            IReadOnlyList<GoTypeSnapshot> packageTypes,
            IReadOnlyList<GoMethodSnapshot> packageMethods,
            IReadOnlyList<GoEmbeddingSnapshot> packageEmbeddings)
        {
            _packageName = packageName ?? string.Empty;

            foreach (var type in packageTypes)
            {
                if (type.Id == Guid.Empty)
                {
                    continue;
                }

                _typesById[type.Id] = type;

                if (!string.IsNullOrWhiteSpace(type.QualifiedName))
                {
                    _typesByQualifiedName.TryAdd(type.QualifiedName, type);
                }

                if (!string.IsNullOrWhiteSpace(type.Name))
                {
                    _typesBySimpleName.TryAdd(type.Name, type);
                }
            }

            foreach (var method in packageMethods)
            {
                if (string.IsNullOrWhiteSpace(method.Name) || string.IsNullOrWhiteSpace(method.DeclaringType))
                {
                    continue;
                }

                if (!_methodsByDeclaringType.TryGetValue(method.DeclaringType, out var signatures))
                {
                    signatures = [];
                    _methodsByDeclaringType[method.DeclaringType] = signatures;
                }

                signatures.Add(MethodSignature.From(method.Name, method.Parameters, method.IsPointerReceiver));
            }

            foreach (var embedding in packageEmbeddings)
            {
                if (embedding.SourceTypeId == Guid.Empty || string.IsNullOrWhiteSpace(embedding.Target))
                {
                    continue;
                }

                if (!_embeddedTargetsByTypeId.TryGetValue(embedding.SourceTypeId, out var targets))
                {
                    targets = [];
                    _embeddedTargetsByTypeId[embedding.SourceTypeId] = targets;
                }

                targets.Add(embedding.Target);
            }
        }

        public GoInterfaceSatisfactionResult Compute(IReadOnlyCollection<Guid> candidateTypeIds)
        {
            var implementations = new Dictionary<string, GoInterfaceImplementation>(StringComparer.Ordinal);
            var interfaces = _typesById.Values.Where(t => string.Equals(t.Kind, "interface", StringComparison.Ordinal)).ToArray();
            var uniqueCandidates = new HashSet<Guid>(candidateTypeIds);

            foreach (var candidateTypeId in uniqueCandidates)
            {
                if (!_typesById.TryGetValue(candidateTypeId, out var type) ||
                    !string.Equals(type.Kind, "struct", StringComparison.Ordinal))
                {
                    continue;
                }

                var structMethods = GetStructMethodSet(type.Id, new HashSet<Guid>());

                foreach (var iface in interfaces)
                {
                    var interfaceMethods = GetInterfaceMethodSet(iface.Id, new HashSet<Guid>());
                    if (structMethods.Value.Count < interfaceMethods.Count &&
                        structMethods.Pointer.Count < interfaceMethods.Count)
                    {
                        continue;
                    }

                    if (Satisfies(structMethods.Value, interfaceMethods))
                    {
                        AddImplementation(
                            implementations,
                            new GoInterfaceImplementation(
                                TypeNodeId: type.Id,
                                TypeQualifiedName: type.QualifiedName,
                                InterfaceNodeId: iface.Id,
                                InterfaceQualifiedName: iface.QualifiedName,
                                ReceiverKind: "value",
                                IsStdlib: false));
                    }
                    else if (Satisfies(structMethods.Pointer, interfaceMethods))
                    {
                        AddImplementation(
                            implementations,
                            new GoInterfaceImplementation(
                                TypeNodeId: type.Id,
                                TypeQualifiedName: type.QualifiedName,
                                InterfaceNodeId: iface.Id,
                                InterfaceQualifiedName: iface.QualifiedName,
                                ReceiverKind: "pointer",
                                IsStdlib: false));
                    }
                }

                foreach (var stdlibInterface in StdlibInterfaces)
                {
                    if (structMethods.Value.Count < stdlibInterface.Methods.Count &&
                        structMethods.Pointer.Count < stdlibInterface.Methods.Count)
                    {
                        continue;
                    }

                    if (Satisfies(structMethods.Value, stdlibInterface.Methods))
                    {
                        AddImplementation(
                            implementations,
                            new GoInterfaceImplementation(
                                TypeNodeId: type.Id,
                                TypeQualifiedName: type.QualifiedName,
                                InterfaceNodeId: null,
                                InterfaceQualifiedName: stdlibInterface.QualifiedName,
                                ReceiverKind: "value",
                                IsStdlib: true));
                    }
                    else if (Satisfies(structMethods.Pointer, stdlibInterface.Methods))
                    {
                        AddImplementation(
                            implementations,
                            new GoInterfaceImplementation(
                                TypeNodeId: type.Id,
                                TypeQualifiedName: type.QualifiedName,
                                InterfaceNodeId: null,
                                InterfaceQualifiedName: stdlibInterface.QualifiedName,
                                ReceiverKind: "pointer",
                                IsStdlib: true));
                    }
                }
            }

            return new GoInterfaceSatisfactionResult(implementations.Values.ToArray(), _diagnostics.Values.ToArray());
        }

        private Dictionary<string, MethodSignature> GetInterfaceMethodSet(Guid interfaceTypeId, HashSet<Guid> active)
        {
            if (_interfaceMethodSets.TryGetValue(interfaceTypeId, out var cached))
            {
                return cached;
            }

            if (!_typesById.TryGetValue(interfaceTypeId, out var interfaceType) ||
                !string.Equals(interfaceType.Kind, "interface", StringComparison.Ordinal))
            {
                return new Dictionary<string, MethodSignature>(StringComparer.Ordinal);
            }

            if (!active.Add(interfaceTypeId))
            {
                AddDiagnostic(
                    $"interface-cycle:{interfaceTypeId:N}",
                    "warning",
                    "go.interface_satisfaction.cycle",
                    $"Interface embedding cycle detected for {interfaceType.QualifiedName}.");
                return new Dictionary<string, MethodSignature>(StringComparer.Ordinal);
            }

            try
            {
                var methods = new Dictionary<string, MethodSignature>(StringComparer.Ordinal);
                foreach (var method in GetDeclaredMethods(interfaceType.QualifiedName))
                {
                    TryAddByName(methods, method);
                }

                if (_embeddedTargetsByTypeId.TryGetValue(interfaceTypeId, out var embeddedTargets))
                {
                    foreach (var embeddedTarget in embeddedTargets)
                    {
                        var embeddedInterface = ResolveEmbeddedType(embeddedTarget);
                        if (embeddedInterface is null)
                        {
                            AddDiagnostic(
                                $"interface-unresolved:{interfaceTypeId:N}:{embeddedTarget}",
                                "warning",
                                "go.interface_satisfaction.unresolved_interface",
                                $"Unable to resolve embedded interface '{embeddedTarget}' in {interfaceType.QualifiedName}.");
                            continue;
                        }

                        if (!string.Equals(embeddedInterface.Kind, "interface", StringComparison.Ordinal))
                        {
                            AddDiagnostic(
                                $"interface-noninterface:{interfaceTypeId:N}:{embeddedTarget}",
                                "warning",
                                "go.interface_satisfaction.invalid_embed",
                                $"Embedded target '{embeddedTarget}' in {interfaceType.QualifiedName} is not an interface.");
                            continue;
                        }

                        var embeddedMethods = GetInterfaceMethodSet(embeddedInterface.Id, active);
                        foreach (var embeddedMethod in embeddedMethods.Values)
                        {
                            TryAddByName(methods, embeddedMethod);
                        }
                    }
                }

                _interfaceMethodSets[interfaceTypeId] = methods;
                return methods;
            }
            finally
            {
                active.Remove(interfaceTypeId);
            }
        }

        private StructMethodSets GetStructMethodSet(Guid structTypeId, HashSet<Guid> active)
        {
            if (_structMethodSets.TryGetValue(structTypeId, out var cached))
            {
                return cached;
            }

            if (!_typesById.TryGetValue(structTypeId, out var structType) ||
                !string.Equals(structType.Kind, "struct", StringComparison.Ordinal))
            {
                return new StructMethodSets();
            }

            if (!active.Add(structTypeId))
            {
                AddDiagnostic(
                    $"struct-cycle:{structTypeId:N}",
                    "warning",
                    "go.interface_satisfaction.cycle",
                    $"Struct embedding cycle detected for {structType.QualifiedName}.");
                return new StructMethodSets();
            }

            try
            {
                var sets = new StructMethodSets();

                foreach (var method in GetDeclaredMethods(structType.QualifiedName))
                {
                    if (method.IsPointerReceiver)
                    {
                        TryAddByName(sets.Pointer, method);
                    }
                    else
                    {
                        TryAddByName(sets.Value, method);
                        TryAddByName(sets.Pointer, method);
                    }
                }

                var directMethodNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var method in GetDeclaredMethods(structType.QualifiedName))
                {
                    if (!string.IsNullOrWhiteSpace(method.Name))
                    {
                        directMethodNames.Add(method.Name);
                    }
                }

                if (_embeddedTargetsByTypeId.TryGetValue(structTypeId, out var embeddedTargets))
                {
                    foreach (var embeddedTarget in embeddedTargets)
                    {
                        var embeddedType = ResolveEmbeddedType(embeddedTarget);
                        if (embeddedType is null)
                        {
                            AddDiagnostic(
                                $"struct-unresolved:{structTypeId:N}:{embeddedTarget}",
                                "warning",
                                "go.interface_satisfaction.unresolved_embed",
                                $"Unable to resolve embedded type '{embeddedTarget}' in {structType.QualifiedName}.");
                            continue;
                        }

                        if (string.Equals(embeddedType.Kind, "struct", StringComparison.Ordinal))
                        {
                            var embeddedSets = GetStructMethodSet(embeddedType.Id, active);
                            Promote(embeddedSets.Value, sets.Value, directMethodNames);
                            Promote(embeddedSets.Value, sets.Pointer, directMethodNames);
                            Promote(embeddedSets.Pointer, sets.Pointer, directMethodNames);
                            continue;
                        }

                        if (string.Equals(embeddedType.Kind, "interface", StringComparison.Ordinal))
                        {
                            var embeddedMethods = GetInterfaceMethodSet(embeddedType.Id, new HashSet<Guid>());
                            Promote(embeddedMethods, sets.Value, directMethodNames);
                            Promote(embeddedMethods, sets.Pointer, directMethodNames);
                            continue;
                        }

                        AddDiagnostic(
                            $"struct-unsupported:{structTypeId:N}:{embeddedTarget}",
                            "warning",
                            "go.interface_satisfaction.invalid_embed",
                            $"Embedded type '{embeddedTarget}' in {structType.QualifiedName} is not supported for method promotion.");
                    }
                }

                _structMethodSets[structTypeId] = sets;
                return sets;
            }
            finally
            {
                active.Remove(structTypeId);
            }
        }

        private IEnumerable<MethodSignature> GetDeclaredMethods(string qualifiedName)
        {
            if (string.IsNullOrWhiteSpace(qualifiedName))
            {
                return [];
            }

            return _methodsByDeclaringType.TryGetValue(qualifiedName, out var methods)
                ? methods
                : [];
        }

        private GoTypeSnapshot? ResolveEmbeddedType(string target)
        {
            var normalized = NormalizeEmbeddedTarget(target);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            if (_typesByQualifiedName.TryGetValue(normalized, out var byQualified))
            {
                return byQualified;
            }

            if (!normalized.Contains('.', StringComparison.Ordinal))
            {
                return _typesBySimpleName.TryGetValue(normalized, out var bySimple) ? bySimple : null;
            }

            var separatorIndex = normalized.LastIndexOf('.');
            if (separatorIndex <= 0 || separatorIndex >= normalized.Length - 1)
            {
                return null;
            }

            var qualifier = normalized[..separatorIndex];
            var simpleName = normalized[(separatorIndex + 1)..];
            if (!string.Equals(qualifier, _packageName, StringComparison.Ordinal))
            {
                return null;
            }

            return _typesBySimpleName.TryGetValue(simpleName, out var localType) ? localType : null;
        }

        private void AddDiagnostic(string key, string severity, string ruleId, string message)
        {
            _diagnostics.TryAdd(key, new GoInterfaceSatisfactionDiagnostic(severity, ruleId, message));
        }

        private static bool Satisfies(
            IReadOnlyDictionary<string, MethodSignature> typeMethods,
            IReadOnlyDictionary<string, MethodSignature> interfaceMethods)
        {
            foreach (var required in interfaceMethods.Values)
            {
                if (!typeMethods.TryGetValue(required.Name, out var actual))
                {
                    return false;
                }

                if (required.ParameterCount.HasValue &&
                    actual.ParameterCount.HasValue &&
                    required.ParameterCount.Value != actual.ParameterCount.Value)
                {
                    return false;
                }
            }

            return true;
        }

        private static void Promote(
            IReadOnlyDictionary<string, MethodSignature> source,
            IDictionary<string, MethodSignature> destination,
            ISet<string> shadowedNames)
        {
            foreach (var method in source.Values)
            {
                if (shadowedNames.Contains(method.Name))
                {
                    continue;
                }

                TryAddByName(destination, method);
            }
        }

        private static void TryAddByName(IDictionary<string, MethodSignature> methods, MethodSignature method)
        {
            if (string.IsNullOrWhiteSpace(method.Name))
            {
                return;
            }

            methods.TryAdd(method.Name, method);
        }

        private static void AddImplementation(
            IDictionary<string, GoInterfaceImplementation> implementations,
            GoInterfaceImplementation implementation)
        {
            var key = $"{implementation.TypeNodeId:N}|{implementation.InterfaceQualifiedName}|{implementation.ReceiverKind}|{implementation.IsStdlib}";
            implementations.TryAdd(key, implementation);
        }
    }

    private static string NormalizeEmbeddedTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return string.Empty;
        }

        var normalized = target.Trim();

        while (normalized.StartsWith('*'))
        {
            normalized = normalized[1..].Trim();
        }

        while (normalized.StartsWith('~'))
        {
            normalized = normalized[1..].Trim();
        }

        while (normalized.StartsWith('(') && normalized.EndsWith(')') && normalized.Length > 2)
        {
            normalized = normalized[1..^1].Trim();
        }

        var genericIndex = normalized.IndexOf('[', StringComparison.Ordinal);
        if (genericIndex >= 0)
        {
            normalized = normalized[..genericIndex].Trim();
        }

        return normalized;
    }

    private static int? TryGetParameterCount(string? parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters))
        {
            return 0;
        }

        var text = parameters.Trim();
        if (text.Length == 0 || string.Equals(text, "()", StringComparison.Ordinal))
        {
            return 0;
        }

        if (text.StartsWith('(') && text.EndsWith(')') && text.Length >= 2)
        {
            text = text[1..^1].Trim();
        }

        if (text.Length == 0)
        {
            return 0;
        }

        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var commaCount = 0;

        foreach (var ch in text)
        {
            switch (ch)
            {
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    parenDepth--;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    bracketDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    braceDepth--;
                    break;
                case ',' when parenDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                    commaCount++;
                    break;
            }

            if (parenDepth < 0 || bracketDepth < 0 || braceDepth < 0)
            {
                return null;
            }
        }

        if (parenDepth != 0 || bracketDepth != 0 || braceDepth != 0)
        {
            return null;
        }

        return commaCount + 1;
    }

    private sealed class StructMethodSets
    {
        public Dictionary<string, MethodSignature> Value { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, MethodSignature> Pointer { get; } = new(StringComparer.Ordinal);
    }

    private sealed record StdlibInterfaceDefinition(
        string QualifiedName,
        IReadOnlyDictionary<string, MethodSignature> Methods)
    {
        public StdlibInterfaceDefinition(string qualifiedName, IReadOnlyList<MethodSignature> methods)
            : this(
                qualifiedName,
                methods
                    .GroupBy(m => m.Name, StringComparer.Ordinal)
                    .Select(g => g.First())
                    .ToDictionary(m => m.Name, m => m, StringComparer.Ordinal))
        {
        }
    }

    private readonly record struct MethodSignature(string Name, int? ParameterCount, bool IsPointerReceiver = false)
    {
        public static MethodSignature From(string name, string? parameters, bool isPointerReceiver)
            => new(name.Trim(), TryGetParameterCount(parameters), isPointerReceiver);
    }
}

public sealed record GoTypeSnapshot(Guid Id, string Name, string QualifiedName, string Kind);

public sealed record GoMethodSnapshot(string Name, string DeclaringType, bool IsPointerReceiver, string? Parameters);

public sealed record GoEmbeddingSnapshot(Guid SourceTypeId, string Target);

public sealed record GoInterfaceImplementation(
    Guid TypeNodeId,
    string TypeQualifiedName,
    Guid? InterfaceNodeId,
    string InterfaceQualifiedName,
    string ReceiverKind,
    bool IsStdlib);

public sealed record GoInterfaceSatisfactionDiagnostic(string Severity, string RuleId, string Message);

public sealed record GoInterfaceSatisfactionResult(
    IReadOnlyList<GoInterfaceImplementation> Implementations,
    IReadOnlyList<GoInterfaceSatisfactionDiagnostic> Diagnostics);
