using System.Collections.Concurrent;

namespace RepoQL.ConsoleApp.Host;

internal static class LeaseRegistry
{
    internal sealed record LeaseEntry(string ClientId, DateTime LastBeatUtc);

    private static readonly ConcurrentDictionary<string, LeaseEntry> Leases = new(StringComparer.OrdinalIgnoreCase);

    public static int Count => Leases.Count;
    public static void Upsert(string clientId, DateTime beatUtc)
        => Leases.AddOrUpdate(clientId, new LeaseEntry(clientId, beatUtc), (_, _) => new LeaseEntry(clientId, beatUtc));
    public static void Remove(string clientId) => Leases.TryRemove(clientId, out _);
    public static IEnumerable<LeaseEntry> Snapshot() => Leases.Values.ToArray();
}