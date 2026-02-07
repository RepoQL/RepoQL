using System.Security.Cryptography;

namespace RepoQL.DevHarness.Proxy;

/// <summary>
/// Purpose: Generates unique harness session ids for tracking a harness lifetime.
/// Complexity: Keeps timestamp + randomness formatting in one place to avoid divergence.
/// </summary>
internal static class HarnessSessionId
{
    public static string Create()
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
        var random = RandomNumberGenerator.GetBytes(2);
        var suffix = Convert.ToHexString(random).ToLowerInvariant();
        return $"sess_{timestamp}_{suffix}";
    }
}
