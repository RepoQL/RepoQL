namespace RepoQL.Formats.Pdf.Surface;

internal sealed record FormFieldInfo
{
    public required Guid NodeId { get; init; }
    public required Guid SpanId { get; init; }
    public required string FieldName { get; init; }
    public required string FieldType { get; init; }
    public string? Value { get; init; }
    public int? Page { get; init; }
}
