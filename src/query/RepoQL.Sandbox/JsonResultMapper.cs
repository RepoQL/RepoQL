using System.Text.Json;
using RepoQL.Contracts;

namespace RepoQL.Sandbox;

/// <summary>
/// Purpose: Map JSON output from the WASM JS evaluator to transport-agnostic TabularResult records.
/// Complexity: Handles all JSON value types (scalar, object, array) with special vertical
/// key-value formatting for single objects. Produces the same column/row structure as SQL queries.
/// </summary>
public static class JsonResultMapper
{
    public static TabularResult MapToResponse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return EmptyResponse();

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        return root.ValueKind switch
        {
            JsonValueKind.Object => MapObject(root),
            JsonValueKind.Array => MapArray(root),
            _ => MapScalar("result", root)
        };
    }

    private static TabularResult MapArray(JsonElement array)
    {
        var items = array.EnumerateArray().ToArray();
        if (items.Length == 0)
            return EmptyResponse();

        if (items.Length == 1 && items[0].ValueKind == JsonValueKind.Object)
            return MapObject(items[0]);

        if (items.All(item => item.ValueKind == JsonValueKind.Object))
            return MapArrayOfObjects(items);

        if (items.All(IsScalar))
            return MapArrayOfScalars(items);

        return MapMixedArray(items);
    }

    private static TabularResult MapObject(JsonElement obj)
    {
        var response = CreateResponse(
            ("property", "VARCHAR"),
            ("value", "JSON"));

        foreach (var property in obj.EnumerateObject())
        {
            response.Rows.Add(CreateRow(
                TabularValue.ForString(property.Name),
                ToTabularValue(property.Value)));
        }

        response.RowCount = response.Rows.Count;
        return response;
    }

    private static TabularResult MapArrayOfObjects(JsonElement[] items)
    {
        var columns = items[0]
            .EnumerateObject()
            .Select(property => (property.Name, InferDbType(property.Value)))
            .ToArray();

        var response = CreateResponse(columns);

        foreach (var item in items)
        {
            var valuesByName = item.EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value);

            response.Rows.Add(CreateRow(columns.Select(column =>
                valuesByName.TryGetValue(column.Name, out var value)
                    ? ToTabularValue(value)
                    : TabularValue.Null)));
        }

        response.RowCount = response.Rows.Count;
        return response;
    }

    private static TabularResult MapArrayOfScalars(JsonElement[] items)
    {
        var response = CreateResponse(("value", InferDbType(items[0])));

        foreach (var item in items)
            response.Rows.Add(CreateRow(ToScalarValue(item)));

        response.RowCount = response.Rows.Count;
        return response;
    }

    private static TabularResult MapMixedArray(JsonElement[] items)
    {
        var response = CreateResponse(("value", "JSON"));

        foreach (var item in items)
            response.Rows.Add(CreateRow(TabularValue.ForString(item.GetRawText())));

        response.RowCount = response.Rows.Count;
        return response;
    }

    private static TabularResult MapScalar(string columnName, JsonElement scalar)
    {
        var response = CreateResponse((columnName, InferDbType(scalar)));
        response.Rows.Add(CreateRow(ToScalarValue(scalar)));
        response.RowCount = 1;
        return response;
    }

    private static TabularResult EmptyResponse() => new()
    {
        RowCount = 0
    };

    private static TabularResult CreateResponse(params (string Name, string DbType)[] columns)
    {
        var response = new TabularResult();
        foreach (var (name, dbType) in columns)
            response.Columns.Add(new TabularColumn(name, dbType));

        return response;
    }

    private static TabularRow CreateRow(params TabularValue[] values) => CreateRow((IEnumerable<TabularValue>)values);

    private static TabularRow CreateRow(IEnumerable<TabularValue> values)
    {
        var row = new TabularRow();
        row.Values.AddRange(values);
        return row;
    }

    private static bool IsScalar(JsonElement element) =>
        element.ValueKind is JsonValueKind.String
            or JsonValueKind.Number
            or JsonValueKind.True
            or JsonValueKind.False
            or JsonValueKind.Null;

    private static TabularValue ToTabularValue(JsonElement element) =>
        IsScalar(element)
            ? ToScalarValue(element)
            : TabularValue.ForString(element.GetRawText());

    private static TabularValue ToScalarValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null => TabularValue.Null,
        JsonValueKind.True => TabularValue.ForBool(true),
        JsonValueKind.False => TabularValue.ForBool(false),
        JsonValueKind.Number => element.TryGetInt64(out var integer)
            ? TabularValue.ForNumber(integer)
            : TabularValue.ForNumber(element.GetDouble()),
        JsonValueKind.String => TabularValue.ForString(element.GetString() ?? string.Empty),
        _ => TabularValue.ForString(element.GetRawText())
    };

    private static string InferDbType(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True or JsonValueKind.False => "BOOLEAN",
        JsonValueKind.Number => "DOUBLE",
        JsonValueKind.Null => "NULL",
        JsonValueKind.String => "VARCHAR",
        _ => "JSON"
    };
}
