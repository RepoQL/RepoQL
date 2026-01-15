using System.Reflection;
using System.Text;
using System.Text.Json;

namespace RepoQL.Data.DuckDB.UdfFramework;

/// <summary>
/// Helper utilities for UDF implementations.
/// </summary>
public static class UdfHelpers
{
    /// <summary>
    /// Serialize an enumerable of objects to a JSON array string for structured UDFs.
    /// </summary>
    public static string SerializeToJsonArray<T>(IEnumerable<T> items)
    {
        var sb = new StringBuilder();
        sb.Append('[');
        bool first = true;

        foreach (var item in items)
        {
            if (!first) sb.Append(',');
            first = false;
            SerializeObject(sb, item);
        }

        sb.Append(']');
        return sb.ToString();
    }

    private static void SerializeObject<T>(StringBuilder sb, T item)
    {
        if (item is null)
        {
            sb.Append("null");
            return;
        }

        sb.Append('{');
        var type = item.GetType();
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        bool first = true;

        foreach (var prop in properties)
        {
            if (!first) sb.Append(',');
            first = false;

            var jsonName = ToSnakeCase(prop.Name);
            var value = prop.GetValue(item);

            sb.Append('"');
            sb.Append(jsonName);
            sb.Append("\":");
            SerializeValue(sb, value);
        }

        sb.Append('}');
    }

    private static void SerializeValue(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                break;
            case string s:
                sb.Append('"');
                sb.Append(EscapeJsonString(s));
                sb.Append('"');
                break;
            case int i:
                sb.Append(i);
                break;
            case long l:
                sb.Append(l);
                break;
            case double d:
                sb.Append(d.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
            case float f:
                sb.Append(f.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case Enum e:
                sb.Append('"');
                sb.Append(e.ToString());
                sb.Append('"');
                break;
            case DateTimeOffset dto:
                // Format for DuckDB TIMESTAMPTZ: YYYY-MM-DD HH:MM:SS±HH:MM
                sb.Append('"');
                sb.Append(dto.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append(dto.Offset >= TimeSpan.Zero ? "+" : "");
                sb.Append(dto.Offset.ToString(@"hh\:mm"));
                sb.Append('"');
                break;
            case DateTime dt:
                // Format for DuckDB TIMESTAMP: YYYY-MM-DD HH:MM:SS
                sb.Append('"');
                sb.Append(dt.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append('"');
                break;
            default:
                sb.Append('"');
                sb.Append(EscapeJsonString(value.ToString() ?? ""));
                sb.Append('"');
                break;
        }
    }

    /// <summary>
    /// Escape a string for JSON output, handling all control characters.
    /// </summary>
    public static string EscapeJsonString(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 32)
                        sb.Append($"\\u{(int)c:X4}");
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Convert PascalCase to snake_case for JSON property names.
    /// </summary>
    public static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        var sb = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Parse a JSON options object into named parameters.
    /// </summary>
    public static T? ParseJsonOption<T>(string? json, string key, T? defaultValue = default)
    {
        if (string.IsNullOrWhiteSpace(json)) return defaultValue;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(key, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Null) return defaultValue;

                if (typeof(T) == typeof(int))
                    return (T)(object)prop.GetInt32();
                if (typeof(T) == typeof(string))
                    return (T)(object)(prop.GetString() ?? "");
                if (typeof(T) == typeof(bool))
                    return (T)(object)prop.GetBoolean();
            }
        }
        catch
        {
            // Ignore parse errors, return default
        }

        return defaultValue;
    }

    /// <summary>
    /// Get column metadata from a return type's properties.
    /// </summary>
    public static IEnumerable<ColumnInfo> GetColumnsFromType(Type type)
    {
        // Handle IEnumerable<T> - get the element type
        var elementType = type;
        if (type.IsGenericType)
        {
            var genericDef = type.GetGenericTypeDefinition();
            if (genericDef == typeof(IEnumerable<>) ||
                type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
            {
                elementType = type.GetGenericArguments().FirstOrDefault() ?? type;
            }
        }

        var properties = elementType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var prop in properties)
        {
            yield return new ColumnInfo(
                JsonName: ToSnakeCase(prop.Name),
                SqlName: ToSnakeCase(prop.Name),
                ClrType: prop.PropertyType
            );
        }
    }
}

/// <summary>
/// Metadata about a column in a structured UDF result.
/// </summary>
public record ColumnInfo(string JsonName, string SqlName, Type ClrType);
