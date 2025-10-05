using System.Text.Json;
using Google.Protobuf.WellKnownTypes;

namespace RepoQL.Host.Services;

internal static class ProtoJson
{
    public static Struct? ToStruct(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        return StructFromElement(doc.RootElement);
    }

    private static Struct StructFromElement(JsonElement el)
    {
        var s = new Struct();
        foreach (var p in el.EnumerateObject())
            s.Fields[p.Name] = ValueFromElement(p.Value);
        return s;
    }

    private static ListValue ListFromElement(JsonElement el)
    {
        var l = new ListValue();
        foreach (var it in el.EnumerateArray())
            l.Values.Add(ValueFromElement(it));
        return l;
    }

    private static Value ValueFromElement(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Object => new Value { StructValue = StructFromElement(el) },
            JsonValueKind.Array => new Value { ListValue = ListFromElement(el) },
            JsonValueKind.String => new Value { StringValue = el.GetString() ?? string.Empty },
            JsonValueKind.Number => new Value
            {
                NumberValue = el.TryGetInt64(out var i) ? i : el.GetDouble()
            },
            JsonValueKind.True => new Value { BoolValue = true },
            JsonValueKind.False => new Value { BoolValue = false },
            _ => new Value { NullValue = NullValue.NullValue }
        };
    }
}