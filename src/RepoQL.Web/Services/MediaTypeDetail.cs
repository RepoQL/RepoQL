namespace RepoQL.Web.Services;

public sealed record MediaTypeDetail(
    string MediaLabel,
    long FileCount,
    long WithHeadline,
    long WithSummary,
    long WithStructure,
    int XRayCoverage,
    string SampleUri);