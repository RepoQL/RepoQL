namespace RepoQL.Contracts.Models;

public class Records
{
    public required Artifact[] Artifacts { get; init; } = [];
    public Node[] Nodes { get; init; } = [];
    public Span[] Spans { get; init; } = [];
    public Edge[] Edges { get; init; } = [];
    public Annotation[] Annotations { get; init; } = [];
    public string[] AnnotationSources { get; init; } = [];
}
