namespace RepoQL.Web.Services;

public sealed record NodeKindStat(
    string Kind,
    long Count,
    double Percentage,
    string SampleUri);