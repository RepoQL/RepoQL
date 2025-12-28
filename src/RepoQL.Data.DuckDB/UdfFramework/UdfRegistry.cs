using System.Reflection;
using System.Text;
using System.Text.Json;
using DuckDB.NET.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable DuckDBNET001

namespace RepoQL.Data.DuckDB.UdfFramework;

/// <summary>
/// Discovers, registers, and manages UDFs marked with framework attributes.
/// </summary>
public class UdfRegistry
{
    private readonly IServiceProvider? _serviceProvider;
    private readonly ILogger<UdfRegistry> _logger;
    private readonly List<UdfRegistration> _registrations = new();

    public UdfRegistry(IServiceProvider? serviceProvider, ILogger<UdfRegistry>? logger = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger ?? NullLogger<UdfRegistry>.Instance;
    }

    /// <summary>
    /// Discover and register all UDFs from loaded assemblies.
    /// </summary>
    public void DiscoverAndRegister(DuckDBConnection connection)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).ToList();
        _logger.LogDebug("[UdfRegistry] Scanning {Count} assemblies for UDF classes", assemblies.Count);

        var udfClasses = assemblies
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch { return Array.Empty<Type>(); }
            })
            .Where(t => t.GetCustomAttribute<UdfClassAttribute>() != null)
            .ToList();

        _logger.LogInformation("[UdfRegistry] Found {Count} UDF classes", udfClasses.Count);

        foreach (var type in udfClasses)
        {
            _logger.LogInformation("[UdfRegistry] Registering UDF class: {TypeName}", type.FullName);

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                var scalarAttr = method.GetCustomAttribute<ScalarUdfAttribute>();
                if (scalarAttr != null)
                {
                    RegisterScalarUdf(connection, type, method, scalarAttr);
                    continue;
                }

                var structuredAttr = method.GetCustomAttribute<StructuredUdfAttribute>();
                if (structuredAttr != null)
                {
                    RegisterStructuredUdf(connection, type, method, structuredAttr);
                }
            }
        }

        _logger.LogInformation("[UdfRegistry] Registered {Count} UDFs from framework", _registrations.Count);
    }

    private void RegisterScalarUdf(DuckDBConnection conn, Type classType, MethodInfo method, ScalarUdfAttribute attr)
    {
        var parameters = method.GetParameters();
        _logger.LogDebug("[UdfRegistry] Registering scalar UDF: {Name} with {ParamCount} parameters",
            attr.Name, parameters.Length);

        RegisterWithDuckDb(conn, attr.Name, classType, method, parameters, attr.IsPure, isStructured: false);
        _registrations.Add(new UdfRegistration(attr, classType, method, parameters));
    }

    private void RegisterStructuredUdf(DuckDBConnection conn, Type classType, MethodInfo method, StructuredUdfAttribute attr)
    {
        var parameters = method.GetParameters();
        _logger.LogDebug("[UdfRegistry] Registering structured UDF: {Name} with {ParamCount} parameters",
            attr.Name, parameters.Length);

        RegisterWithDuckDb(conn, attr.Name, classType, method, parameters, isPure: false, isStructured: true);
        _registrations.Add(new UdfRegistration(attr, classType, method, parameters));
    }

    private void RegisterWithDuckDb(DuckDBConnection conn, string name, Type classType,
                                     MethodInfo method, ParameterInfo[] parameters, bool isPure, bool isStructured)
    {
        // DuckDB.NET limitation: max 3 type parameters (3 inputs or 2 inputs + 1 output for scalar)
        // We always use 3 string parameters: (required1, required2_or_intent, optionsJson)
        // Macro packs additional parameters into optionsJson

        conn.RegisterScalarFunction<string, string, string, string>(
            name,
            (readers, writer, n) =>
            {
                if (_serviceProvider is null)
                    throw new InvalidOperationException($"UDF '{name}': IServiceProvider required. Pass serviceProvider to DuckDbDataStore constructor.");

                // Create scoped instance once per batch for efficiency
                using var scope = _serviceProvider.CreateScope();
                object instance;
                try
                {
                    instance = ActivatorUtilities.CreateInstance(scope.ServiceProvider, classType);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"UDF '{name}': Failed to create instance of {classType.Name}: {ex.Message}", ex);
                }

                for (ulong i = 0; i < n; i++)
                {
                    try
                    {
                        // Read parameters with auto NULL handling
                        var args = ReadParameters(readers, parameters, i);

                        // Invoke method
                        var result = method.Invoke(instance, args);

                        // Write result
                        if (isStructured && result is System.Collections.IEnumerable enumerable)
                        {
                            // Serialize to JSON array for json_each expansion
                            var jsonArray = SerializeEnumerable(enumerable);
                            writer.WriteValue(jsonArray, i);
                        }
                        else if (result is string str)
                        {
                            writer.WriteValue(str, i);
                        }
                        else if (result is null)
                        {
                            writer.WriteNull(i);
                        }
                        else
                        {
                            writer.WriteValue(result.ToString() ?? "", i);
                        }
                    }
                    catch (TargetInvocationException ex) when (ex.InnerException != null)
                    {
                        // Throw DuckDB exception with UDF context
                        throw new InvalidOperationException($"UDF '{name}': {ex.InnerException.Message}", ex.InnerException);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"UDF '{name}': {ex.Message}", ex);
                    }
                }
            },
            isPureFunction: isPure
        );
    }

    private static object?[] ReadParameters(dynamic readers, ParameterInfo[] parameters, ulong rowIndex)
    {
        var args = new object?[parameters.Length];
        int readerCount = readers.Count;

        // DuckDB.NET limitation: max 3 params. First 2 are direct, 3rd is JSON options.
        // Method signature is the contract - param names beyond index 1 are extracted from JSON.

        // Read first 2 params directly from DuckDB
        for (int p = 0; p < Math.Min(2, parameters.Length); p++)
        {
            if (p < readerCount)
            {
                dynamic reader = readers[p];
                args[p] = reader.IsValid(rowIndex) ? reader.GetValue<string>(rowIndex) : null;
            }
        }

        // If we have more than 2 params, deserialize from JSON options (3rd DuckDB param)
        if (parameters.Length > 2 && readerCount > 2)
        {
            dynamic optionsReader = readers[2];
            if (optionsReader.IsValid(rowIndex))
            {
                var json = optionsReader.GetValue<string>(rowIndex);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        // Extract values for params 2+ by name
                        for (int p = 2; p < parameters.Length; p++)
                        {
                            var param = parameters[p];
                            var paramName = param.Name!;
                            JsonElement prop;

                            // Try original name first, then snake_case
                            if (root.TryGetProperty(paramName, out prop) && prop.ValueKind != JsonValueKind.Null)
                            {
                                args[p] = ConvertJsonValue(prop, param.ParameterType);
                            }
                            else
                            {
                                var snakeName = UdfHelpers.ToSnakeCase(paramName);
                                if (snakeName != paramName && root.TryGetProperty(snakeName, out prop) && prop.ValueKind != JsonValueKind.Null)
                                {
                                    args[p] = ConvertJsonValue(prop, param.ParameterType);
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Ignore JSON parse errors - leave params as null/default
                    }
                }
            }
        }

        return args;
    }

    private static object? ConvertJsonValue(JsonElement element, Type targetType)
    {
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        return underlying switch
        {
            Type t when t == typeof(string) => element.GetString(),
            Type t when t == typeof(int) => element.TryGetInt32(out var i) ? i : null,
            Type t when t == typeof(long) => element.TryGetInt64(out var l) ? l : null,
            Type t when t == typeof(double) => element.TryGetDouble(out var d) ? d : null,
            Type t when t == typeof(bool) => element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False
                ? element.GetBoolean() : null,
            _ => element.GetString()
        };
    }

    private static string SerializeEnumerable(System.Collections.IEnumerable enumerable)
    {
        var items = enumerable.Cast<object>();
        return UdfHelpers.SerializeToJsonArray(items);
    }

    /// <summary>
    /// Generate SQL macros for all registered UDFs that have MacroName set.
    /// </summary>
    public string GenerateMacrosSql()
    {
        var sb = new StringBuilder();

        foreach (var reg in _registrations)
        {
            var macroName = GetMacroName(reg.Attribute);
            if (macroName == null) continue;

            sb.AppendLine(GenerateMacro(reg, macroName));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string? GetMacroName(Attribute attr) => attr switch
    {
        ScalarUdfAttribute s => s.MacroName,
        StructuredUdfAttribute s => s.MacroName,
        _ => null
    };

    private static string GetUdfName(Attribute attr) => attr switch
    {
        ScalarUdfAttribute s => s.Name,
        StructuredUdfAttribute s => s.Name,
        _ => throw new InvalidOperationException("Unknown UDF attribute type")
    };

    private string GenerateMacro(UdfRegistration reg, string macroName)
    {
        var udfName = GetUdfName(reg.Attribute);
        var parameters = reg.Parameters;
        var isTable = reg.Attribute is StructuredUdfAttribute;

        // Build parameter list with defaults
        var paramDefs = new List<string>();
        var udfArgs = new List<string>();
        var jsonFields = new List<string>();

        foreach (var p in parameters)
        {
            var defaultAttr = p.GetCustomAttribute<UdfDefaultAttribute>();
            var paramName = p.Name!;

            if (defaultAttr != null)
                paramDefs.Add($"{paramName} := {defaultAttr.SqlDefault}");
            else
                paramDefs.Add(paramName);

            // First 2 params go direct, rest go in JSON
            if (udfArgs.Count < 2)
                udfArgs.Add(paramName);
            else
                jsonFields.Add($"'{paramName}', {paramName}");
        }

        // If we have JSON fields, add the json_object
        if (jsonFields.Count > 0)
            udfArgs.Add($"json_object({string.Join(", ", jsonFields)})");

        var udfCall = $"{udfName}({string.Join(", ", udfArgs)})";

        if (isTable)
        {
            // Table macro: expand JSON array via json_each, extract columns
            var columns = UdfHelpers.GetColumnsFromType(reg.Method.ReturnType).ToList();
            var selectCols = string.Join(",\n        ",
                columns.Select(c => $"j.value->>'{c.JsonName}' AS {c.SqlName}"));

            return $@"CREATE OR REPLACE MACRO {macroName}({string.Join(", ", paramDefs)}) AS TABLE (
    SELECT
        {selectCols}
    FROM json_each({udfCall}) AS j
    WHERE j.type = 'OBJECT'
);";
        }

        return $@"CREATE OR REPLACE MACRO {macroName}({string.Join(", ", paramDefs)}) AS (
    {udfCall}
);";
    }

    /// <summary>
    /// Get all registered UDFs.
    /// </summary>
    public IReadOnlyList<UdfRegistration> GetRegistrations() => _registrations.AsReadOnly();
}

/// <summary>
/// Metadata about a registered UDF.
/// </summary>
public record UdfRegistration(Attribute Attribute, Type ClassType, MethodInfo Method, ParameterInfo[] Parameters);
