namespace RepoQL.Contracts.Models;

public class Records
{
    public static Records Empty => new()
    {
        Artifacts = Array.Empty<Artifact>(),
        Nodes = Array.Empty<Node>(),
        Spans = Array.Empty<Span>(),
        Edges = Array.Empty<Edge>(),
        Annotations = Array.Empty<Annotation>(),
        AnnotationSources = Array.Empty<string>()
    };

    public required Artifact[] Artifacts { get; init; } = [];
    public Node[] Nodes { get; init; } = [];
    public Span[] Spans { get; init; } = [];
    public Edge[] Edges { get; init; } = [];
    public Annotation[] Annotations { get; init; } = [];
    public string[] AnnotationSources { get; init; } = [];
}
