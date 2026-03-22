using System.Text.Json;

namespace RepoQL.Embedding.Writer.Tests;

public sealed class CompactionJobTests
{
    [Test]
    public async Task TryParseShardPath_ExtractsSourceAndModel()
    {
        var parsed = CompactionShardInfo.TryParse(
            "source=abc123/model=voyage-context-3/part-1700000000000.parquet",
            out var shard);

        await Assert.That(parsed).IsTrue();
        await Assert.That(shard.SourceHash).IsEqualTo("abc123");
        await Assert.That(shard.Model).IsEqualTo("voyage-context-3");
        await Assert.That(shard.GetPrefix()).IsEqualTo("source=abc123/model=voyage-context-3/");
    }

    [Test]
    [Arguments("")]
    [Arguments("source=abc123")]
    [Arguments("model=voyage-context-3/part-1.parquet")]
    [Arguments("source=/model=voyage-context-3/part-1.parquet")]
    public async Task TryParseShardPath_RejectsInvalidFormats(string path)
    {
        var parsed = CompactionShardInfo.TryParse(path, out _);

        await Assert.That(parsed).IsFalse();
    }

    [Test]
    public async Task LockJsonGeneration_UsesExpectedShape_AndStaleDetection()
    {
        var startedAt = new DateTimeOffset(2026, 02, 01, 12, 34, 56, TimeSpan.Zero);
        var lockFile = CompactionLockFile.Create("writer-1", startedAt);
        var payload = JsonSerializer.SerializeToUtf8Bytes(lockFile);
        using var document = JsonDocument.Parse(payload);

        await Assert.That(document.RootElement.GetProperty("instance").GetString()).IsEqualTo("writer-1");
        await Assert.That(document.RootElement.GetProperty("started_at").GetDateTimeOffset()).IsEqualTo(startedAt);
        await Assert.That(lockFile.IsStale(startedAt.AddMinutes(59), TimeSpan.FromHours(1))).IsFalse();
        await Assert.That(lockFile.IsStale(startedAt.AddHours(2), TimeSpan.FromHours(1))).IsTrue();
    }

    [Test]
    public async Task CalculateExpirationCutoff_SubtractsConfiguredTtl()
    {
        var now = new DateTimeOffset(2026, 03, 09, 08, 00, 00, TimeSpan.Zero);
        var cutoff = CompactionJob.CalculateExpirationCutoff(now, TimeSpan.FromDays(180));

        await Assert.That(cutoff).IsEqualTo(new DateTimeOffset(2025, 09, 10, 08, 00, 00, TimeSpan.Zero));
    }
}
