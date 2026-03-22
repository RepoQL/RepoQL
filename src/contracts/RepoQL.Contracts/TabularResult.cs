namespace RepoQL.Contracts;

/// <summary>
/// Purpose: Transport-agnostic tabular result — decouples query/sandbox layers from gRPC proto types.
/// Complexity: Three records (result, column, row) plus a discriminated cell value. Mirrors the
/// RawQueryResponse proto shape so conversion is mechanical.
/// </summary>
public sealed class TabularResult
{
    public List<TabularColumn> Columns { get; } = [];
    public List<TabularRow> Rows { get; } = [];
    public long RowCount { get; set; }
}

public sealed record TabularColumn(string Name, string DbType);

public sealed class TabularRow
{
    public List<TabularValue> Values { get; } = [];
}

/// <summary>
/// A single cell value in a tabular row. Mirrors the subset of google.protobuf.Value
/// actually used by query results: null, bool, number, and string.
/// </summary>
public abstract record TabularValue
{
    public static TabularValue Null { get; } = new NullValue();
    public static TabularValue ForBool(bool value) => new BoolValue(value);
    public static TabularValue ForNumber(double value) => new NumberValue(value);
    public static TabularValue ForNumber(long value) => new NumberValue(value);
    public static TabularValue ForString(string value) => new StringValue(value);

    private TabularValue() { }

    public sealed record NullValue : TabularValue;
    public sealed record BoolValue(bool Value) : TabularValue;
    public sealed record NumberValue(double Value) : TabularValue;
    public sealed record StringValue(string Value) : TabularValue;
}
