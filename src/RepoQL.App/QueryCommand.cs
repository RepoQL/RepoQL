using RepoQL.Contracts;
using Spectre.Console;
using Spectre.Console.Cli;

internal sealed class QueryCommand : Command<QuerySettings>
{
    public override int Execute(CommandContext context, QuerySettings settings)
    {
        // Resolve target repository and honor --repo when creating the client
        var repo = ProgramHelpers.ResolveRepo(settings.Repo);
        var client = ProgramHelpers.CreateClient(repo);
        var result = client.ExecuteRawQueryAsync(settings.Sql).GetAwaiter().GetResult();
        if (result.RowCount == 0)
        {
            AnsiConsole.MarkupLine("[grey]No rows.[/]");
            return 0;
        }
        var table = new Table().Border(TableBorder.Rounded);
        foreach (var c in result.Columns) table.AddColumn(c.Name);
        foreach (var r in result.Rows)
            table.AddRow(r.Values.Select(v => v.KindCase switch
            {
                Google.Protobuf.WellKnownTypes.Value.KindOneofCase.NullValue => "",
                Google.Protobuf.WellKnownTypes.Value.KindOneofCase.BoolValue => v.BoolValue ? "true" : "false",
                Google.Protobuf.WellKnownTypes.Value.KindOneofCase.NumberValue => v.NumberValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Google.Protobuf.WellKnownTypes.Value.KindOneofCase.StringValue => v.StringValue ?? string.Empty,
                Google.Protobuf.WellKnownTypes.Value.KindOneofCase.StructValue => "{…}",
                Google.Protobuf.WellKnownTypes.Value.KindOneofCase.ListValue => "[…]",
                _ => string.Empty
            }).Select(Markup.Escape).ToArray());
        AnsiConsole.Write(table);
        client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return 0;
    }
}
