namespace RepoQL.Web.Services;

internal sealed record HostStatusSnapshot(bool IsAvailable, string Message, DateTimeOffset UpdatedAt)
{
    public static HostStatusSnapshot Offline(string message)
        => new(false, message, DateTimeOffset.UtcNow);

    public static HostStatusSnapshot Online(string message)
        => new(true, message, DateTimeOffset.UtcNow);
}
