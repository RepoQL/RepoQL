namespace RepoQL.Formats.Rust.Tests;

internal static class FixtureReader
{
    public static string Read(string fileName)
    {
        var assembly = typeof(FixtureReader).Assembly;
        var suffix = $".Fixtures.{fileName}";
        var resourceName = assembly
            .GetManifestResourceNames()
            .Single(name => name.EndsWith(suffix, StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing fixture resource: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
