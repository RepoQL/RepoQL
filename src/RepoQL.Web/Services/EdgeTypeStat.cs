namespace RepoQL.Web.Services;

public sealed record EdgeTypeStat(
    string Type,
    long Count,
    double Percentage);