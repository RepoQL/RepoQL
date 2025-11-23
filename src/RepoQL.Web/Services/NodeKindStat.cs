namespace RepoQL.Web.Services;

internal sealed record NodeKindStat(
    string Kind,
    long Count,
    double Percentage,
    string SampleUri);
