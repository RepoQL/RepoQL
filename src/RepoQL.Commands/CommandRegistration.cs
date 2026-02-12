using System.Reflection;

namespace RepoQL.Commands;

/// <summary>
/// Purpose: Metadata about a discovered command handler method.
/// Complexity: Record holding reflection info. UserParameters excludes CancellationToken.
/// </summary>
public sealed record CommandRegistration(
    CommandAttribute Attribute,
    Type ClassType,
    MethodInfo Method,
    ParameterInfo[] UserParameters,
    bool HasCancellationToken);
