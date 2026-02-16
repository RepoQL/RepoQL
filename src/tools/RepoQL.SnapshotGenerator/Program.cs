using System.Reflection;
using RepoQL.Contracts.Snapshots;
using RepoQL.SnapshotGenerator;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: RepoQL.SnapshotGenerator <docs-directory> <output-json-path> [--version <ver>]");
    return 1;
}

var docsDirectory = args[0];
var outputPath = args[1];

var version = "unknown";
for (var i = 2; i < args.Length - 1; i++)
{
    if (string.Equals(args[i], "--version", StringComparison.OrdinalIgnoreCase))
    {
        version = args[i + 1];
        break;
    }
}

if (version == "unknown")
{
    version = typeof(SnapshotGeneratorCore).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "1.0.0";
}

try
{
    var manifest = await SnapshotGeneratorCore.GenerateAsync(docsDirectory, version);

    var outputDir = Path.GetDirectoryName(outputPath);
    if (!string.IsNullOrEmpty(outputDir))
        Directory.CreateDirectory(outputDir);

    await using var stream = File.Create(outputPath);
    SnapshotSerializer.Serialize(stream, manifest);

    Console.WriteLine($"Generated snapshot: {manifest.Documents.Count} documents, version {version}");
    Console.WriteLine($"Output: {outputPath} ({new FileInfo(outputPath).Length:N0} bytes)");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Snapshot generation failed: {ex.Message}");
    return 1;
}
