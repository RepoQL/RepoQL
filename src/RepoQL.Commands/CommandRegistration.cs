using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace RepoQL.Commands;

/// <summary>
/// Purpose: Metadata about a discovered command handler method.
/// Complexity: Record holding reflection info. UserParameters excludes CancellationToken.
/// </summary>
public sealed record CommandRegistration(
    CommandAttribute Attribute,
    [property: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicMethods)] Type ClassType,
    MethodInfo Method,
    ParameterInfo[] UserParameters,
    bool HasCancellationToken);
