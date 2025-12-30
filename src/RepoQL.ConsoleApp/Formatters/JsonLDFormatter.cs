using System.Buffers;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using RepoQL.ConsoleApp.Commands;
using RepoQL.Contracts;

namespace RepoQL.ConsoleApp.Formatters;

public class JsonLDFormatter : IResultFormatter
{
    public ResultFormat Format => ResultFormat.JsonLine;

    public Task<string[]> FormatAsync(RawQueryResponse result, int maxRows = 100, long? totalRowCount = null, CancellationToken cancellationToken = default)
    {
        var cols = result.Columns?.ToArray() ?? Array.Empty<ColumnSchema>();
        var rows = result.Rows?.ToArray() ?? Array.Empty<RowData>();
        var take = Math.Min(rows.Length, maxRows);
        var lines = new List<string>(take);

        for (int r = 0; r < take; r++)
        {
            var abw = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(abw, new JsonWriterOptions { Indented = false }))
            {
                writer.WriteStartObject();
                for (int c = 0; c < cols.Length; c++)
                {
                    writer.WritePropertyName(cols[c].Name);
                    var val = c < rows[r].Values.Count ? rows[r].Values[c] : Value.ForNull();
                    WriteJsonFromValue(writer, val);
                }
                writer.WriteEndObject();
                writer.Flush();
            }
            lines.Add(System.Text.Encoding.UTF8.GetString(abw.WrittenSpan));
        }

        return Task.FromResult(lines.ToArray());
    }

    private static void WriteJsonFromValue(Utf8JsonWriter writer, Value v)
    {
        switch (v.KindCase)
        {
            case Value.KindOneofCase.NullValue:
                writer.WriteNullValue();
                break;
            case Value.KindOneofCase.BoolValue:
                writer.WriteBooleanValue(v.BoolValue);
                break;
            case Value.KindOneofCase.NumberValue:
                writer.WriteNumberValue(v.NumberValue);
                break;
            case Value.KindOneofCase.StringValue:
                writer.WriteStringValue(v.StringValue ?? string.Empty);
                break;
            case Value.KindOneofCase.StructValue:
                writer.WriteStartObject();
                foreach (var kv in v.StructValue.Fields)
                {
                    writer.WritePropertyName(kv.Key);
                    WriteJsonFromValue(writer, kv.Value);
                }
                writer.WriteEndObject();
                break;
            case Value.KindOneofCase.ListValue:
                writer.WriteStartArray();
                foreach (var item in v.ListValue.Values)
                    WriteJsonFromValue(writer, item);
                writer.WriteEndArray();
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }
}
