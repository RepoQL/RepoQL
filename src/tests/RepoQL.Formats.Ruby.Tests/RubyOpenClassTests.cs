using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.Contracts.Analysis;
using RepoQL.Data.DuckDB;
using RepoQL.Formats.Ruby;
using RepoQL.Testing.Scaffolding;

namespace RepoQL.Formats.Ruby.Tests;

public sealed class RubyOpenClassTests
{
    [Test]
    public async Task Materialize_MarksWithinFileReopeningWhenSuperclassOnlyOnPrimaryDefinition()
    {
        using var loader = new RubyLoader();
        const string source = """
            class Account < ApplicationRecord
              def origin
              end
            end

            class Account
              def reopened
              end
            end
            """;
        using var artifactScope = CreateArtifact("account.rb", source);

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var accountNodes = records.Nodes
            .Where(n => n.Kind == "rb.type" && n.Props["qualified_name"]!.ToString() == "Account")
            .ToArray();
        accountNodes.Should().HaveCount(2);

        var primary = accountNodes.Single(n => n.Props["extends"]?.ToString() == "ApplicationRecord");
        primary.Props["is_reopening"]!.ToString().Should().Be("false");

        var reopening = accountNodes.Single(n => n.Props["extends"] is null);
        reopening.Props["is_reopening"]!.ToString().Should().Be("true");
    }

    [Test]
    public async Task Materialize_DefaultsToFalseWhenReopeningIsUncertain()
    {
        using var loader = new RubyLoader();
        const string source = """
            class Payment
              def first
              end
            end

            class Payment
              def second
              end
            end
            """;
        using var artifactScope = CreateArtifact("payment.rb", source);

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var paymentNodes = records.Nodes
            .Where(n => n.Kind == "rb.type" && n.Props["qualified_name"]!.ToString() == "Payment")
            .ToArray();
        paymentNodes.Should().HaveCount(2);
        paymentNodes.Should().OnlyContain(n => n.Props["is_reopening"]!.ToString() == "false");
    }

    [Test]
    public async Task RubyTypesView_AggregatesDistributedDefinitionsAcrossFiles()
    {
        await using var repo = await CreateRubyRepoAsync();
        repo.AddOrUpdateText("open_class_part1.rb", ReadFixture("open_class_part1.rb"));
        repo.AddOrUpdateText("open_class_part2.rb", ReadFixture("open_class_part2.rb"));
        await repo.IndexAsync();

        var rows = repo.Store.RawQuery(
            """
            SELECT qualified_name, type_kind, extends, definition_count, defined_in, origin_file
            FROM ruby_types
            WHERE qualified_name = 'OpenClass'
            """).ToArray();

        rows.Should().ContainSingle();
        var row = rows[0];
        row["qualified_name"]!.ToString().Should().Be("OpenClass");
        row["type_kind"]!.ToString().Should().Be("class");
        row["extends"]!.ToString().Should().Be("BaseRecord");
        row["definition_count"]!.ToString().Should().Be("2");
        row["defined_in"].Should().BeAssignableTo<IEnumerable<string>>();
        var definedIn = ((IEnumerable<string>)row["defined_in"]!).ToArray();
        definedIn.Should().Contain(uri => uri.Contains("open_class_part1.rb", StringComparison.Ordinal));
        definedIn.Should().Contain(uri => uri.Contains("open_class_part2.rb", StringComparison.Ordinal));
        row["origin_file"]!.ToString().Should().Contain("open_class_part1.rb");
    }

    private static async Task<IndexedRepoBuilder> CreateRubyRepoAsync()
    {
        return await IndexedRepoBuilder.CreateAsync(options =>
        {
            options.EnableWatching = false;
            options.RunFullScanOnStartup = false;
            options.DeleteDatabaseOnDispose = true;

            var loader = new RubyLoader();
            options.AddFormat(new FormatDescriptor(
                RubyMediaTypes.Ruby,
                loader,
                NoOpFormatAnalyzer.Instance,
                loader,
                ["rb"]));
            options.AddSchemaProvider(loader);
        });
    }

    private static string ReadFixture(string fileName)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));

    private static ArtifactScope CreateArtifact(string fileName, string content)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"repoql_ruby_open_class_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, fileName);
        File.WriteAllText(filePath, content, Encoding.UTF8);

        var provider = new PhysicalFileProvider(tempDir);
        return new ArtifactScope(
            new DiscoveredArtifact
            {
                File = provider.GetFileInfo(fileName),
                RepoUri = RepoUri.Parse($"file:///{fileName}")
            },
            tempDir,
            provider);
    }

    private sealed class ArtifactScope(DiscoveredArtifact artifact, string tempDir, IFileProvider provider) : IDisposable
    {
        public DiscoveredArtifact Artifact { get; } = artifact;
        private readonly string _tempDir = tempDir;
        private readonly IFileProvider _provider = provider;

        public void Dispose()
        {
            (_provider as IDisposable)?.Dispose();
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
    }

    private sealed class NoOpFormatAnalyzer : IFormatAnalyzer
    {
        public static NoOpFormatAnalyzer Instance { get; } = new();

        public bool Supports(SemanticMediaType mediaType) => true;

        public async IAsyncEnumerable<AnalysisResult> AnalyzeAsync(
            DocumentModel document,
            AnalyzerContext context,
            CancellationToken cancellationToken = default)
        {
            _ = document;
            _ = context;
            _ = cancellationToken;
            await Task.CompletedTask;
            yield break;
        }
    }
}
