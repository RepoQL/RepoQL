using Google.Protobuf.WellKnownTypes;
using RepoQL.Contracts;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Purpose: Convert transport-agnostic TabularResult to gRPC RawQueryResponse proto messages.
/// Complexity: Mechanical mapping — columns, rows, and TabularValue discriminated union to proto Value.
/// </summary>
internal static class TabularResultExtensions
{
    public static RawQueryResponse ToProto(this TabularResult tabular)
    {
        var response = new RawQueryResponse { RowCount = tabular.RowCount };

        foreach (var column in tabular.Columns)
            response.Columns.Add(new ColumnSchema { Name = column.Name, DbType = column.DbType });

        foreach (var row in tabular.Rows)
        {
            var protoRow = new RowData();
            foreach (var cell in row.Values)
                protoRow.Values.Add(ToProtoValue(cell));
            response.Rows.Add(protoRow);
        }

        return response;
    }

    private static Value ToProtoValue(TabularValue cell) => cell switch
    {
        TabularValue.NullValue => Value.ForNull(),
        TabularValue.BoolValue b => Value.ForBool(b.Value),
        TabularValue.NumberValue n => Value.ForNumber(n.Value),
        TabularValue.StringValue s => Value.ForString(s.Value),
        _ => Value.ForNull()
    };
}
