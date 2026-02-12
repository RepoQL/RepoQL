namespace RepoQL.Commands;

/// <summary>
/// Marks a class as containing command handler methods.
/// Classes with this attribute are discovered via assembly scanning.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class CommandClassAttribute : Attribute;

/// <summary>
/// Marks a method as a command handler invoked via <c>::name</c> syntax.
/// Name supports dot-separated hierarchy (e.g., "mcp.newrelic.auth").
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class CommandAttribute : Attribute
{
    /// <summary>Command name without the <c>::</c> prefix. Dots create hierarchy.</summary>
    public string Name { get; }

    /// <summary>One-line description shown in help and subcommand listings.</summary>
    public string? Description { get; init; }

    public CommandAttribute(string name) => Name = name;
}

/// <summary>
/// Describes a command parameter for help text generation.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class CommandParamAttribute : Attribute
{
    /// <summary>Human-readable description of the parameter.</summary>
    public string Description { get; }

    public CommandParamAttribute(string description) => Description = description;
}
