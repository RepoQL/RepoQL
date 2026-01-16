using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
/// <remarks>
/// UDF classes in the UdfImplementations namespace are preserved during IL trimming
/// via ILLink.Descriptors.xml. To add a new UDF class, just create it with [UdfClass]
/// attribute - no additional configuration needed.
/// </remarks>
public class UdfRegistry
{
    private static readonly ActivitySource ActivitySource = new("RepoQL.UdfFramework");

    private readonly IServiceProvider? _serviceProvider;
    private readonly ILogger<UdfRegistry> _logger;
    private readonly List<UdfRegistration> _registrations = new();

    public UdfRegistry(IServiceProvider? serviceProvider, ILogger<UdfRegistry>? logger = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger ?? NullLogger<UdfRegistry>.Instance;
    }

    /// <summary>
    /// Creates an instance of a UDF class. Uses DI if available, otherwise falls back
    /// to Activator.CreateInstance for types with parameterless constructors.
    /// </summary>
    private object CreateUdfInstance(Type classType)
    {
        if (_serviceProvider != null)
        {
            using var scope = _serviceProvider.CreateScope();
            return ActivatorUtilities.CreateInstance(scope.ServiceProvider, classType);
        }

        // No DI - try parameterless constructor
        var instance = Activator.CreateInstance(classType);
        if (instance == null)
        {
            throw new InvalidOperationException(
                $"UDF class '{classType.Name}' requires constructor dependencies but no IServiceProvider was provided. " +
                "Pass serviceProvider to DuckDbDataStore constructor.");
        }
        return instance;
    }

    /// <summary>
    /// Discover and register all UDFs from loaded assemblies.
    /// Types are preserved during trimming via ILLink.Descriptors.xml.
    /// </summary>
    [RequiresUnreferencedCode("UDF discovery uses reflection. Types are preserved via ILLink.Descriptors.xml.")]
    public void DiscoverAndRegister(DuckDBConnection connection)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).ToList();
        _logger.LogDebug("[UdfRegistry] Scanning {Count} assemblies for UDF classes", assemblies.Count);

        var udfClasses = assemblies
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch { return []; }
            })
            .Where(t => t.GetCustomAttribute<UdfClassAttribute>() != null)
            .ToList();

        _logger.LogInformation("[UdfRegistry] Found {Count} UDF classes", udfClasses.Count);

        foreach (var type in udfClasses)
        {
            RegisterUdfClass(connection, type);
        }

        _logger.LogInformation("[UdfRegistry] Registered {Count} UDFs from framework", _registrations.Count);
    }

    /// <summary>
    /// Register all UDF methods from a single class.
    /// </summary>
    [RequiresUnreferencedCode("UDF registration uses reflection.")]
    private void RegisterUdfClass(DuckDBConnection connection, Type type)
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
        // DuckDB.NET requires at least one parameter for UDFs.
        // For "parameterless" functions, use a dummy param with [UdfDefault("''")].
        if (parameters.Length == 0)
        {
            throw new InvalidOperationException(
                $"UDF '{name}': DuckDB.NET doesn't support parameterless UDFs. " +
                $"Add a dummy parameter with [UdfDefault(\"''\")].");
        }

        RegisterParameterizedUdf(conn, name, classType, method, parameters, isPure, isStructured);
    }

    private void RegisterParameterizedUdf(DuckDBConnection conn, string name, Type classType,
                                           MethodInfo method, ParameterInfo[] parameters, bool isPure, bool isStructured)
    {
        // DuckDB.NET requires matching type parameters to actual SQL parameter count.
        // Route to appropriate overload based on method parameter count.
        // For 5+ params, we use JSON packing (macro handles this).
        var paramCount = parameters.Length;

        if (paramCount == 1)
        {
            RegisterUdf1Param(conn, name, classType, method, parameters, isPure, isStructured);
        }
        else if (paramCount == 2)
        {
            RegisterUdf2Params(conn, name, classType, method, parameters, isPure, isStructured);
        }
        else if (paramCount == 3)
        {
            RegisterUdf3Params(conn, name, classType, method, parameters, isPure, isStructured);
        }
        else if (paramCount == 4)
        {
            RegisterUdf4Params(conn, name, classType, method, parameters, isPure, isStructured);
        }
        else
        {
            // For 5+ params, use 3 DuckDB params: first 2 direct, 3rd is JSON for rest
            // This maintains backward compatibility with macros that pack params as JSON
            RegisterUdf3ParamsWithJson(conn, name, classType, method, parameters, isPure, isStructured);
        }
    }

    /// <summary>
    /// Drops a UDF if it exists, ensuring clean re-registration.
    /// Used for idempotent registration - prevents errors when schema init retries
    /// and ensures new versions replace old implementations.
    /// </summary>
    private static void DropUdfIfExists(DuckDBConnection conn, string name)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP FUNCTION IF EXISTS \"{name}\"";
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // Ignore errors - function may not exist or DROP may not be supported
        }
    }

    private void RegisterUdf1Param(DuckDBConnection conn, string name, Type classType,
                                    MethodInfo method, ParameterInfo[] parameters, bool isPure, bool isStructured)
    {
        DropUdfIfExists(conn, name);

        conn.RegisterScalarFunction<string, string>(
            name,
            (readers, writer, n) =>
            {
                using var activity = ActivitySource.StartActivity($"udf.{name}", ActivityKind.Internal);
                activity?.SetTag("udf.name", name);
                activity?.SetTag("udf.row_count", n);

                try
                {
                    var instance = CreateUdfInstance(classType);
                    var args = new object?[parameters.Length];

                    for (ulong i = 0; i < n; i++)
                    {
                        Array.Clear(args);

                        // Read param and convert to target type
                        if (parameters.Length > 0 && readers.Count > 0 && readers[0].IsValid(i))
                        {
                            var strValue = readers[0].GetValue<string>(i);
                            args[0] = ConvertStringValue(strValue, parameters[0].ParameterType);
                        }

                        // Apply defaults for any null args
                        ApplyDefaults(args, parameters);

                        var result = method.Invoke(instance, args);

                        // Write result
                        if (isStructured && result is System.Collections.IEnumerable enumerable)
                        {
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
                }
                catch (TargetInvocationException ex) when (ex.InnerException != null)
                {
                    // UDF callbacks are [UnmanagedCallersOnly] - cannot throw. Write error JSON.
                    _logger.LogError(ex.InnerException, "UDF '{Name}' failed", name);
                    var errorJson = $"[{{\"__udf_error__\":\"{UdfHelpers.EscapeJsonString(ex.InnerException.Message)}\"}}]";
                    for (ulong i = 0; i < n; i++) writer.WriteValue(errorJson, i);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "UDF '{Name}' failed", name);
                    var errorJson = $"[{{\"__udf_error__\":\"{UdfHelpers.EscapeJsonString(ex.Message)}\"}}]";
                    for (ulong i = 0; i < n; i++) writer.WriteValue(errorJson, i);
                }
            },
            isPureFunction: isPure
        );
    }

    private void RegisterUdf2Params(DuckDBConnection conn, string name, Type classType,
                                     MethodInfo method, ParameterInfo[] parameters, bool isPure, bool isStructured)
    {
        DropUdfIfExists(conn, name);

        conn.RegisterScalarFunction<string, string, string>(
            name,
            (readers, writer, n) =>
            {
                using var activity = ActivitySource.StartActivity($"udf.{name}", ActivityKind.Internal);
                activity?.SetTag("udf.name", name);
                activity?.SetTag("udf.row_count", n);

                try
                {
                    var instance = CreateUdfInstance(classType);
                    var args = new object?[parameters.Length];

                    for (ulong i = 0; i < n; i++)
                    {
                        Array.Clear(args);

                        // Read params and convert to target types
                        if (parameters.Length > 0 && readers.Count > 0 && readers[0].IsValid(i))
                        {
                            var strValue = readers[0].GetValue<string>(i);
                            args[0] = ConvertStringValue(strValue, parameters[0].ParameterType);
                        }
                        if (parameters.Length > 1 && readers.Count > 1 && readers[1].IsValid(i))
                        {
                            var strValue = readers[1].GetValue<string>(i);
                            args[1] = ConvertStringValue(strValue, parameters[1].ParameterType);
                        }

                        // Apply defaults for any null args
                        ApplyDefaults(args, parameters);

                        var result = method.Invoke(instance, args);

                        // Write result
                        if (isStructured && result is System.Collections.IEnumerable enumerable)
                        {
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
                }
                catch (TargetInvocationException ex) when (ex.InnerException != null)
                {
                    _logger.LogError(ex.InnerException, "UDF '{Name}' failed", name);
                    var errorJson = $"[{{\"__udf_error__\":\"{UdfHelpers.EscapeJsonString(ex.InnerException.Message)}\"}}]";
                    for (ulong i = 0; i < n; i++) writer.WriteValue(errorJson, i);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "UDF '{Name}' failed", name);
                    var errorJson = $"[{{\"__udf_error__\":\"{UdfHelpers.EscapeJsonString(ex.Message)}\"}}]";
                    for (ulong i = 0; i < n; i++) writer.WriteValue(errorJson, i);
                }
            },
            isPureFunction: isPure
        );
    }

    private void RegisterUdf3Params(DuckDBConnection conn, string name, Type classType,
                                     MethodInfo method, ParameterInfo[] parameters, bool isPure, bool isStructured)
    {
        DropUdfIfExists(conn, name);

        conn.RegisterScalarFunction<string, string, string, string>(
            name,
            (readers, writer, n) =>
            {
                using var activity = ActivitySource.StartActivity($"udf.{name}", ActivityKind.Internal);
                activity?.SetTag("udf.name", name);
                activity?.SetTag("udf.row_count", n);

                _logger.LogDebug("[UDF3] {Name} called with {Count} rows", name, n);

                try
                {
                    _logger.LogDebug("[UDF3] {Name} creating instance of {Type}", name, classType.Name);
                    var instance = CreateUdfInstance(classType);
                    _logger.LogDebug("[UDF3] {Name} instance created successfully", name);
                    var args = new object?[parameters.Length];

                    for (ulong i = 0; i < n; i++)
                    {
                        Array.Clear(args);

                        // Read all 3 params directly from DuckDB
                        if (readers.Count > 0 && readers[0].IsValid(i))
                        {
                            var strValue = readers[0].GetValue<string>(i);
                            args[0] = ConvertStringValue(strValue, parameters[0].ParameterType);
                        }
                        if (readers.Count > 1 && readers[1].IsValid(i))
                        {
                            var strValue = readers[1].GetValue<string>(i);
                            args[1] = ConvertStringValue(strValue, parameters[1].ParameterType);
                        }
                        if (readers.Count > 2 && readers[2].IsValid(i))
                        {
                            var strValue = readers[2].GetValue<string>(i);
                            args[2] = ConvertStringValue(strValue, parameters[2].ParameterType);
                        }

                        // Apply [UdfDefault] values for any remaining null args
                        ApplyDefaults(args, parameters);

                        _logger.LogDebug("[UDF3] {Name} invoking method with args: [{Args}]", name, string.Join(", ", args.Select(a => a?.ToString() ?? "null")));
                        var result = method.Invoke(instance, args);
                        _logger.LogDebug("[UDF3] {Name} method returned type: {ResultType}", name, result?.GetType().Name ?? "null");

                        // Write result
                        if (isStructured && result is System.Collections.IEnumerable enumerable)
                        {
                            _logger.LogDebug("[UDF3] {Name} serializing enumerable", name);
                            var jsonArray = SerializeEnumerable(enumerable);
                            _logger.LogDebug("[UDF3] {Name} serialized: {JsonLen} chars, first 100: {Preview}", name, jsonArray?.Length ?? 0, jsonArray?.Substring(0, Math.Min(100, jsonArray?.Length ?? 0)));
                            writer.WriteValue(jsonArray, i);
                        }
                        else if (result is string str)
                        {
                            writer.WriteValue(str, i);
                        }
                        else if (result is null)
                        {
                            _logger.LogWarning("[UDF3] {Name} returned null from method.Invoke", name);
                            writer.WriteNull(i);
                        }
                        else
                        {
                            writer.WriteValue(result.ToString() ?? "", i);
                        }
                    }
                    _logger.LogDebug("[UDF3] {Name} completed processing all rows", name);
                }
                catch (TargetInvocationException ex) when (ex.InnerException != null)
                {
                    _logger.LogError(ex.InnerException, "[UDF3] {Name} failed with TargetInvocationException", name);
                    var errorJson = $"[{{\"__udf_error__\":\"{UdfHelpers.EscapeJsonString(ex.InnerException.Message)}\"}}]";
                    for (ulong i = 0; i < n; i++) writer.WriteValue(errorJson, i);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[UDF3] {Name} failed with exception", name);
                    var errorJson = $"[{{\"__udf_error__\":\"{UdfHelpers.EscapeJsonString(ex.Message)}\"}}]";
                    for (ulong i = 0; i < n; i++) writer.WriteValue(errorJson, i);
                }
            },
            isPureFunction: isPure
        );
    }

    private void RegisterUdf4Params(DuckDBConnection conn, string name, Type classType,
                                     MethodInfo method, ParameterInfo[] parameters, bool isPure, bool isStructured)
    {
        DropUdfIfExists(conn, name);

        conn.RegisterScalarFunction<string, string, string, string, string>(
            name,
            (readers, writer, n) =>
            {
                using var activity = ActivitySource.StartActivity($"udf.{name}", ActivityKind.Internal);
                activity?.SetTag("udf.name", name);
                activity?.SetTag("udf.row_count", n);

                try
                {
                    var instance = CreateUdfInstance(classType);
                    var args = new object?[parameters.Length];

                    for (ulong i = 0; i < n; i++)
                    {
                        Array.Clear(args);

                        // Read all 4 params directly from DuckDB
                        if (readers.Count > 0 && readers[0].IsValid(i))
                        {
                            var strValue = readers[0].GetValue<string>(i);
                            args[0] = ConvertStringValue(strValue, parameters[0].ParameterType);
                        }
                        if (readers.Count > 1 && readers[1].IsValid(i))
                        {
                            var strValue = readers[1].GetValue<string>(i);
                            args[1] = ConvertStringValue(strValue, parameters[1].ParameterType);
                        }
                        if (readers.Count > 2 && readers[2].IsValid(i))
                        {
                            var strValue = readers[2].GetValue<string>(i);
                            args[2] = ConvertStringValue(strValue, parameters[2].ParameterType);
                        }
                        if (readers.Count > 3 && readers[3].IsValid(i))
                        {
                            var strValue = readers[3].GetValue<string>(i);
                            args[3] = ConvertStringValue(strValue, parameters[3].ParameterType);
                        }

                        // Apply [UdfDefault] values for any remaining null args
                        ApplyDefaults(args, parameters);

                        var result = method.Invoke(instance, args);

                        // Write result
                        if (isStructured && result is System.Collections.IEnumerable enumerable)
                        {
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
                }
                catch (TargetInvocationException ex) when (ex.InnerException != null)
                {
                    _logger.LogError(ex.InnerException, "UDF '{Name}' failed", name);
                    var errorJson = $"[{{\"__udf_error__\":\"{UdfHelpers.EscapeJsonString(ex.InnerException.Message)}\"}}]";
                    for (ulong i = 0; i < n; i++) writer.WriteValue(errorJson, i);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "UDF '{Name}' failed", name);
                    var errorJson = $"[{{\"__udf_error__\":\"{UdfHelpers.EscapeJsonString(ex.Message)}\"}}]";
                    for (ulong i = 0; i < n; i++) writer.WriteValue(errorJson, i);
                }
            },
            isPureFunction: isPure
        );
    }

    /// <summary>
    /// Registers a UDF with 5+ method parameters using 3 DuckDB params:
    /// first 2 direct, 3rd is JSON containing params 3+.
    /// This maintains backward compatibility with SQL macros that pack params as JSON.
    /// </summary>
    private void RegisterUdf3ParamsWithJson(DuckDBConnection conn, string name, Type classType,
                                             MethodInfo method, ParameterInfo[] parameters, bool isPure, bool isStructured)
    {
        DropUdfIfExists(conn, name);

        // Use 3 DuckDB params: first 2 direct, 3rd is JSON for params 3+
        conn.RegisterScalarFunction<string, string, string, string>(
            name,
            (readers, writer, n) =>
            {
                using var activity = ActivitySource.StartActivity($"udf.{name}", ActivityKind.Internal);
                activity?.SetTag("udf.name", name);
                activity?.SetTag("udf.row_count", n);

                try
                {
                    var instance = CreateUdfInstance(classType);
                    var args = new object?[parameters.Length];

                    for (ulong i = 0; i < n; i++)
                    {
                        Array.Clear(args);

                        // First 2 params directly from DuckDB
                        for (int p = 0; p < 2 && p < parameters.Length; p++)
                        {
                            if (readers.Count > p && readers[p].IsValid(i))
                            {
                                var strValue = readers[p].GetValue<string>(i);
                                args[p] = ConvertStringValue(strValue, parameters[p].ParameterType);
                            }
                        }

                        // Params 3+ from JSON (3rd DuckDB param)
                        if (parameters.Length > 2 && readers.Count > 2 && readers[2].IsValid(i))
                        {
                            var json = readers[2].GetValue<string>(i);
                            if (!string.IsNullOrWhiteSpace(json))
                            {
                                try
                                {
                                    using var doc = JsonDocument.Parse(json);
                                    var root = doc.RootElement;

                                    for (int p = 2; p < parameters.Length; p++)
                                    {
                                        var param = parameters[p];
                                        var paramName = param.Name!;

                                        if (root.TryGetProperty(paramName, out var prop) && prop.ValueKind != JsonValueKind.Null)
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

                        // Apply [UdfDefault] values for any remaining null args
                        ApplyDefaults(args, parameters);

                        var result = method.Invoke(instance, args);

                        // Write result
                        if (isStructured && result is System.Collections.IEnumerable enumerable)
                        {
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
                }
                catch (TargetInvocationException ex) when (ex.InnerException != null)
                {
                    _logger.LogError(ex.InnerException, "UDF '{Name}' failed", name);
                    var errorJson = $"[{{\"__udf_error__\":\"{UdfHelpers.EscapeJsonString(ex.InnerException.Message)}\"}}]";
                    for (ulong i = 0; i < n; i++) writer.WriteValue(errorJson, i);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "UDF '{Name}' failed", name);
                    var errorJson = $"[{{\"__udf_error__\":\"{UdfHelpers.EscapeJsonString(ex.Message)}\"}}]";
                    for (ulong i = 0; i < n; i++) writer.WriteValue(errorJson, i);
                }
            },
            isPureFunction: isPure
        );
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

    /// <summary>
    /// Convert a string value (from VARCHAR) to the target parameter type.
    /// Used for the first 2 UDF parameters which are passed as VARCHAR.
    /// Handles DuckDB's boolean representations: "true"/"false", "TRUE"/"FALSE", "1"/"0".
    /// </summary>
    private static object? ConvertStringValue(string? value, Type targetType)
    {
        if (value is null)
            return null;

        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        try
        {
            if (underlying == typeof(string))
                return value;

            if (underlying == typeof(int))
                return int.TryParse(value, out var i) ? i : null;

            if (underlying == typeof(long))
                return long.TryParse(value, out var l) ? l : null;

            if (underlying == typeof(double))
                return double.TryParse(value, out var d) ? d : null;

            if (underlying == typeof(bool))
            {
                // bool.TryParse handles "true"/"false" case-insensitively
                if (bool.TryParse(value, out var b))
                    return b;
                // DuckDB might also send "1"/"0"
                if (value == "1") return true;
                if (value == "0") return false;
                return null;
            }

            return value;
        }
        catch
        {
            // Any conversion error returns null, letting ApplyDefaults handle it
            return null;
        }
    }

    private static string SerializeEnumerable(System.Collections.IEnumerable enumerable)
    {
        // Force immediate enumeration to catch exceptions from yield return methods.
        // C# iterators defer exception throwing until enumeration, so we materialize
        // the list here to ensure exceptions propagate properly.
        //
        // IMPORTANT: UDF callbacks run in unmanaged context ([UnmanagedCallersOnly]),
        // so exceptions cannot propagate safely. We catch them here and return a
        // special error JSON that the SQL layer can detect and handle.
        List<object> items;
        try
        {
            items = enumerable.Cast<object>().ToList();
        }
        catch (Exception ex)
        {
            // Return error as JSON that can be detected by SQL macros
            return $"[{{\"__udf_error__\":\"{UdfHelpers.EscapeJsonString(ex.Message)}\"}}]";
        }
        return UdfHelpers.SerializeToJsonArray(items);
    }

    /// <summary>
    /// Apply [UdfDefault] values for any remaining null args with non-nullable types.
    /// </summary>
    private static void ApplyDefaults(object?[] args, ParameterInfo[] parameters)
    {
        for (int i = 0; i < parameters.Length; i++)
        {
            if (args[i] != null) continue;

            var param = parameters[i];
            var paramType = param.ParameterType;

            // For nullable types, null is fine
            if (!paramType.IsValueType || Nullable.GetUnderlyingType(paramType) != null)
                continue;

            // For non-nullable value types, we need a default
            var defaultAttr = param.GetCustomAttribute<UdfDefaultAttribute>();
            if (defaultAttr != null)
            {
                args[i] = ParseSqlDefault(defaultAttr.SqlDefault, paramType);
            }
            else
            {
                // Use type's default value as fallback
                args[i] = Activator.CreateInstance(paramType);
            }
        }
    }

    /// <summary>
    /// Parse a SQL default value literal to a C# value.
    /// </summary>
    private static object? ParseSqlDefault(string sqlDefault, Type targetType)
    {
        // Remove quotes for string defaults like "'Find'" -> "Find"
        var trimmed = sqlDefault.Trim();

        // Handle NULL
        if (trimmed.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            return null;

        // Handle quoted strings
        if (trimmed.StartsWith("'") && trimmed.EndsWith("'"))
        {
            var unquoted = trimmed[1..^1].Replace("''", "'"); // SQL escaping
            return ConvertToType(unquoted, targetType);
        }

        // Handle numeric/boolean literals
        return ConvertToType(trimmed, targetType);
    }

    private static object? ConvertToType(string value, Type targetType)
    {
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        return underlying switch
        {
            Type t when t == typeof(string) => value,
            Type t when t == typeof(int) => int.TryParse(value, out var i) ? i : 0,
            Type t when t == typeof(long) => long.TryParse(value, out var l) ? l : 0L,
            Type t when t == typeof(double) => double.TryParse(value, out var d) ? d : 0.0,
            Type t when t == typeof(float) => float.TryParse(value, out var f) ? f : 0f,
            Type t when t == typeof(bool) => value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                                              value.Equals("1", StringComparison.Ordinal),
            _ => value
        };
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

            // First 2 params go direct (cast to VARCHAR since all UDFs use string params),
            // rest go in JSON
            if (udfArgs.Count < 2)
                udfArgs.Add($"{paramName}::VARCHAR");
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
