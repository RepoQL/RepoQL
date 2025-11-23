namespace RepoQL.Web.Services;

internal sealed record MediaTypeDetail(
    string MediaLabel,
    long FileCount,
    long WithHeadline,
    long WithSummary,
    long WithStructure,
    int XRayCoverage,
    string SampleUri);
