using System.Security.Cryptography;

namespace RepoQL.DevHarness.Proxy;

/// <summary>
/// Purpose: Generates unique harness request ids for correlating tool calls and responses.
/// Complexity: Keeps timestamp + randomness formatting in one place to avoid divergence.
/// </summary>
internal static class HarnessRequestId
{
    public static string Create()
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
        var random = RandomNumberGenerator.GetBytes(2);
        var suffix = Convert.ToHexString(random).ToLowerInvariant();
        return $"req_{timestamp}_{suffix}";
    }
}
