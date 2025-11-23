namespace RepoQL.Web.Services;

internal sealed record StatsOverview(
    long TotalFiles,
    long TotalNodes,
    long TotalEdges,
    long TotalAnnotations,
    IReadOnlyList<MediaSlice> MediaBreakdown,
    IReadOnlyList<AnnotationSummary> AnnotationBreakdown);
