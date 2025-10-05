using System.CommandLine;
using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using RepoQL.Contracts;
using Spectre.Console;

namespace RepoQL.Cli.Commands;

internal static class QueryCommand
{
    public static Command Build()
    {
        var cmd = new Command("query", "Execute a raw SQL query against the RepoQL database");
        var sqlArg = new Argument<string[]>("sql", () => [], "SQL text (optional if --file or stdin used)");
        cmd.AddArgument(sqlArg);

        var fileOpt = new Option<string?>("--file", "Read SQL from file path");
        var streamOpt = new Option<bool>("--stream", () => false, "Stream results instead of loading all rows");
        var limitOpt = new Option<int>("--limit", () => 0, "Optional row limit (0 = no limit)");
        var jsonOpt = new Option<bool>("--json", () => false, "Emit JSON output (array for unary; NDJSON when --stream)");

        cmd.AddOption(fileOpt);
        cmd.AddOption(streamOpt);
        cmd.AddOption(limitOpt);
        cmd.AddOption(jsonOpt);

        cmd.SetHandler(async (string[] sqlParts, string? file, bool stream, int limit, bool json) =>
        {
            var sql = await ResolveSqlAsync(sqlParts, file).ConfigureAwait(false);
            await using var client = RepoQlClient.Create(new RepoQlClientOptions());

            if (stream)
            {
                if (json)
                {
                    // NDJSON (one JSON object per line)
                    await foreach (var row in client.ExecuteRawQueryStreamAsync(sql, rowLimit: limit))
                    {
                        if (row.Columns.Count > 0)
                        {
                            using var ms = new MemoryStream();
                            await using var w = new Utf8JsonWriter(ms);
                            w.WriteStartObject();
                            w.WritePropertyName("columns");
                            w.WriteStartArray();
                            foreach (var c in row.Columns)
                            {
                                w.WriteStartObject();
                                w.WriteString("name", c.Name);
                                w.WriteString("db_type", c.DbType);
                                w.WriteEndObject();
                            }
                            w.WriteEndArray();
                            w.WriteEndObject();
                            w.Flush();
                            Console.Out.WriteLine(Encoding.UTF8.GetString(ms.ToArray()));
                        }

                        using var rms = new MemoryStream();
                        using (var w = new Utf8JsonWriter(rms))
                        {
                            w.WriteStartObject();
                            w.WritePropertyName("row");
                            WriteValuesArray(w, row.Row.Values);
                            w.WriteEndObject();
                        }
                        Console.Out.WriteLine(Encoding.UTF8.GetString(rms.ToArray()));
                    }
                    return;
                }

                // Text streaming: header once then tab-separated rows
                var headerPrinted = false;
                await foreach (var row in client.ExecuteRawQueryStreamAsync(sql, rowLimit: limit))
                {
                    if (!headerPrinted && row.Columns.Count > 0)
                    {
                        Console.WriteLine(string.Join('\t', row.Columns.Select(c => c.Name)));
                        headerPrinted = true;
                    }
                    Console.WriteLine(string.Join('\t', row.Row.Values.Select(ValueToString)));
                }
                return;
            }

            // Unary (non-streaming)
            var result = await client.ExecuteRawQueryAsync(sql, rowLimit: limit).ConfigureAwait(false);
            if (json)
            {
                using var output = Console.OpenStandardOutput();
                using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
                writer.WriteStartObject();
                writer.WritePropertyName("columns");
                writer.WriteStartArray();
                foreach (var c in result.Columns)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", c.Name);
                    writer.WriteString("db_type", c.DbType);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WritePropertyName("rows");
                writer.WriteStartArray();
                foreach (var r in result.Rows)
                    WriteValuesArray(writer, r.Values);
                writer.WriteEndArray();
                writer.WriteNumber("row_count", result.RowCount);
                writer.WriteBoolean("truncated", result.Truncated);
                writer.WriteEndObject();
                await writer.FlushAsync();
                return;
            }

            // Spectre table output
            var table = new Table().Border(TableBorder.Minimal);
            foreach (var c in result.Columns)
                table.AddColumn(new TableColumn(c.Name));
            foreach (var r in result.Rows)
                table.AddRow(r.Values.Select(ValueToString).Select(Markup.Escape).ToArray());

            AnsiConsole.Write(table);
            if (result.Truncated)
                AnsiConsole.MarkupLine("[dim](truncated)[/]");
        },
        sqlArg, fileOpt, streamOpt, limitOpt, jsonOpt);

        return cmd;
    }

    private static async Task<string> ResolveSqlAsync(string[] sqlParts, string? file)
    {
        if (!string.IsNullOrWhiteSpace(file))
            return await File.ReadAllTextAsync(file!).ConfigureAwait(false);
        if (sqlParts is { Length: > 0 })
            return string.Join(' ', sqlParts);
        if (!Console.IsInputRedirected)
            throw new InvalidOperationException("No SQL provided. Pass text, --file, or pipe via STDIN.");
        using var sr = new StreamReader(Console.OpenStandardInput());
        return await sr.ReadToEndAsync().ConfigureAwait(false);
    }

    private static void WriteValuesArray(Utf8JsonWriter w, Google.Protobuf.Collections.RepeatedField<Value> values)
    {
        w.WriteStartArray();
        foreach (var v in values)
            WriteValue(w, v);
        w.WriteEndArray();
    }

    private static void WriteValue(Utf8JsonWriter w, Value v)
    {
        switch (v.KindCase)
        {
            case Value.KindOneofCase.NullValue: w.WriteNullValue(); break;
            case Value.KindOneofCase.BoolValue: w.WriteBooleanValue(v.BoolValue); break;
            case Value.KindOneofCase.NumberValue: w.WriteNumberValue(v.NumberValue); break;
            case Value.KindOneofCase.StringValue: w.WriteStringValue(v.StringValue ?? string.Empty); break;
            case Value.KindOneofCase.StructValue:
                w.WriteStartObject();
                foreach (var kv in v.StructValue!.Fields)
                {
                    w.WritePropertyName(kv.Key);
                    WriteValue(w, kv.Value);
                }
                w.WriteEndObject();
                break;
            case Value.KindOneofCase.ListValue:
                WriteValuesArray(w, v.ListValue!.Values);
                break;
            default: w.WriteNullValue(); break;
        }
    }

    private static string ValueToString(Value v) =>
        v.KindCase switch
        {
            Value.KindOneofCase.NullValue => "",
            Value.KindOneofCase.BoolValue => v.BoolValue ? "true" : "false",
            Value.KindOneofCase.NumberValue => v.NumberValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Value.KindOneofCase.StringValue => v.StringValue ?? string.Empty,
            Value.KindOneofCase.StructValue => "{…}",
            Value.KindOneofCase.ListValue => "[…]",
            _ => string.Empty
        };
}
