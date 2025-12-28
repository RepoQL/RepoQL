namespace RepoQL.Data.DuckDB.UdfFramework;

/// <summary>
/// Marks a class as containing UDF methods.
/// Classes with this attribute will be discovered and their UDF methods registered.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class UdfClassAttribute : Attribute { }

/// <summary>
/// Marks a method as a scalar UDF returning a single value per row.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class ScalarUdfAttribute : Attribute
{
    /// <summary>Internal UDF name registered with DuckDB (e.g., "_xray_internal").</summary>
    public string Name { get; }

    /// <summary>SQL macro name for user-facing API (e.g., "xray"). If null, no macro is generated.</summary>
    public string? MacroName { get; init; }

    /// <summary>If true, DuckDB can optimize calls (no side effects). Default false.</summary>
    public bool IsPure { get; init; } = false;

    /// <summary>Documentation for the UDF.</summary>
    public string? Description { get; init; }

    public ScalarUdfAttribute(string name) => Name = name;
}

/// <summary>
/// Marks a method as a table-returning UDF (returns rows via json_each expansion).
/// The method should return IEnumerable&lt;T&gt; where T is a record/class with properties.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class StructuredUdfAttribute : Attribute
{
    /// <summary>Internal UDF name registered with DuckDB (e.g., "_xray_structured_internal").</summary>
    public string Name { get; }

    /// <summary>SQL table macro name (e.g., "xray_structured"). If null, no macro is generated.</summary>
    public string? MacroName { get; init; }

    /// <summary>Documentation for the UDF.</summary>
    public string? Description { get; init; }

    public StructuredUdfAttribute(string name) => Name = name;
}

/// <summary>
/// Marks a parameter with a SQL default value for macro generation.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public class UdfDefaultAttribute : Attribute
{
    /// <summary>SQL literal for the default value (e.g., "1000", "'Find'", "NULL").</summary>
    public string SqlDefault { get; }

    public UdfDefaultAttribute(string sqlDefault) => SqlDefault = sqlDefault;
}
