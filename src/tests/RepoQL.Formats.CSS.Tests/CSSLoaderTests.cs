using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.Formats.CSS;

namespace RepoQL.Formats.CSS.Tests;

public sealed class CSSLoaderTests
{
    [Test]
    [DisplayName("Recognizes .css, .scss, and .less extensions")]
    public async Task CanLoadAsync_RecognizesCSSExtensions()
    {
        var loader = new CSSLoader();

        using var css = CreateArtifact("styles.css", ".container { color: red; }");
        using var scss = CreateArtifact("theme.scss", "$color: blue;");
        using var less = CreateArtifact("vars.less", "@color: green;");

        (await loader.CanLoadAsync(css.Artifact)).Should().BeTrue();
        css.Artifact.MediaType!.Kind.Should().Be("code.css");

        (await loader.CanLoadAsync(scss.Artifact)).Should().BeTrue();
        scss.Artifact.MediaType!.Kind.Should().Be("code.scss");

        (await loader.CanLoadAsync(less.Artifact)).Should().BeTrue();
        less.Artifact.MediaType!.Kind.Should().Be("code.less");
    }

    [Test]
    [DisplayName("Parses CSS rulesets")]
    public async Task LoadAndMaterialize_EmitsRulesets()
    {
        var loader = new CSSLoader();
        const string source = """
        .container {
            padding: 1rem;
        }

        #header nav ul {
            list-style: none;
        }

        body, html {
            margin: 0;
        }
        """;

        using var art = CreateArtifact("styles.css", source);
        var document = await loader.LoadAsync(art.Artifact);
        var records = loader.Materialize(document);

        records.Artifacts.Should().HaveCount(1);
        records.Nodes.Should().NotBeEmpty();

        var rulesetNodes = records.Nodes.Where(n => n.Kind == CSSNodeKinds.Ruleset).ToList();
        rulesetNodes.Should().HaveCount(3);

        rulesetNodes[0].Headline.Should().Contain(".container");
        rulesetNodes[1].Headline.Should().Contain("#header");
    }

    [Test]
    [DisplayName("Parses @media queries")]
    public async Task LoadAndMaterialize_EmitsMediaRules()
    {
        var loader = new CSSLoader();
        const string source = """
        @media screen and (min-width: 768px) {
            .container {
                width: 750px;
            }
        }

        @media print {
            .no-print {
                display: none;
            }
        }
        """;

        using var art = CreateArtifact("responsive.css", source);
        var document = await loader.LoadAsync(art.Artifact);
        var records = loader.Materialize(document);

        var mediaNodes = records.Nodes.Where(n => n.Kind == CSSNodeKinds.Media).ToList();
        mediaNodes.Should().HaveCount(2);

        mediaNodes[0].Headline.Should().Contain("@media");
        mediaNodes[0].Headline.Should().Contain("screen");
    }

    [Test]
    [DisplayName("Parses @keyframes")]
    public async Task LoadAndMaterialize_EmitsKeyframes()
    {
        var loader = new CSSLoader();
        const string source = """
        @keyframes fadeIn {
            from { opacity: 0; }
            to { opacity: 1; }
        }

        @keyframes slideUp {
            0% { transform: translateY(100%); }
            100% { transform: translateY(0); }
        }
        """;

        using var art = CreateArtifact("animations.css", source);
        var document = await loader.LoadAsync(art.Artifact);
        var records = loader.Materialize(document);

        var keyframeNodes = records.Nodes.Where(n => n.Kind == CSSNodeKinds.Keyframes).ToList();
        keyframeNodes.Should().HaveCount(2);

        keyframeNodes[0].Headline.Should().Be("@keyframes fadeIn");
        keyframeNodes[1].Headline.Should().Be("@keyframes slideUp");
    }

    [Test]
    [DisplayName("Parses @import statements")]
    public async Task LoadAndMaterialize_EmitsImports()
    {
        var loader = new CSSLoader();
        const string source = """
        @import "variables.css";
        @import url('reset.css');

        .container {
            padding: 1rem;
        }
        """;

        using var art = CreateArtifact("main.css", source);
        var document = await loader.LoadAsync(art.Artifact);
        var records = loader.Materialize(document);

        var importNodes = records.Nodes.Where(n => n.Kind == CSSNodeKinds.Import).ToList();
        importNodes.Should().HaveCount(2);

        importNodes[0].Headline.Should().Contain("variables.css");
    }

    [Test]
    [DisplayName("Parses @font-face rules")]
    public async Task LoadAndMaterialize_EmitsFontFaces()
    {
        var loader = new CSSLoader();
        const string source = """
        @font-face {
            font-family: 'CustomFont';
            src: url('custom.woff2') format('woff2');
        }
        """;

        using var art = CreateArtifact("fonts.css", source);
        var document = await loader.LoadAsync(art.Artifact);
        var records = loader.Materialize(document);

        var fontFaceNodes = records.Nodes.Where(n => n.Kind == CSSNodeKinds.FontFace).ToList();
        fontFaceNodes.Should().HaveCount(1);
        fontFaceNodes[0].Headline.Should().Be("@font-face");
    }

    [Test]
    [DisplayName("Parses SCSS variables")]
    public async Task LoadAndMaterialize_EmitsSCSSVariables()
    {
        var loader = new CSSLoader();
        const string source = """
        $primary-color: #007bff;
        $font-size-base: 16px;
        $spacing: 1rem;

        .button {
            background: $primary-color;
        }
        """;

        using var art = CreateArtifact("variables.scss", source);
        await loader.CanLoadAsync(art.Artifact); // Set MediaType
        var document = await loader.LoadAsync(art.Artifact);
        var records = loader.Materialize(document);

        var variableNodes = records.Nodes.Where(n => n.Kind == CSSNodeKinds.Variable).ToList();
        variableNodes.Should().HaveCount(3);

        variableNodes[0].Headline.Should().Contain("$primary-color");
        variableNodes[1].Headline.Should().Contain("$font-size-base");
    }

    [Test]
    [DisplayName("Parses SCSS mixins")]
    public async Task LoadAndMaterialize_EmitsSCSSMixins()
    {
        var loader = new CSSLoader();
        const string source = """
        @mixin button-styles {
            padding: 0.5rem 1rem;
            border-radius: 4px;
        }

        @mixin flex-center {
            display: flex;
            justify-content: center;
            align-items: center;
        }

        .btn {
            @include button-styles;
        }
        """;

        using var art = CreateArtifact("mixins.scss", source);
        await loader.CanLoadAsync(art.Artifact); // Set MediaType
        var document = await loader.LoadAsync(art.Artifact);
        var records = loader.Materialize(document);

        var mixinNodes = records.Nodes.Where(n => n.Kind == CSSNodeKinds.Mixin).ToList();
        mixinNodes.Should().HaveCount(2);

        mixinNodes[0].Headline.Should().Be("@mixin button-styles");
        mixinNodes[1].Headline.Should().Be("@mixin flex-center");
    }

    [Test]
    [DisplayName("Parses SCSS @include")]
    public async Task LoadAndMaterialize_EmitsSCSSIncludes()
    {
        var loader = new CSSLoader();
        const string source = """
        @mixin button-styles {
            padding: 0.5rem;
        }

        .btn {
            @include button-styles;
        }

        .card {
            @include button-styles;
        }
        """;

        using var art = CreateArtifact("includes.scss", source);
        await loader.CanLoadAsync(art.Artifact); // Set MediaType
        var document = await loader.LoadAsync(art.Artifact);
        var records = loader.Materialize(document);

        var includeNodes = records.Nodes.Where(n => n.Kind == CSSNodeKinds.Include).ToList();
        includeNodes.Should().HaveCount(2);

        includeNodes[0].Headline.Should().Be("@include button-styles");
    }

    [Test]
    [DisplayName("Creates HAS_PART edges for composition")]
    public async Task Materialize_CreatesCompositionEdges()
    {
        var loader = new CSSLoader();
        const string source = """
        .container {
            padding: 1rem;
        }
        """;

        using var art = CreateArtifact("styles.css", source);
        var document = await loader.LoadAsync(art.Artifact);
        var records = loader.Materialize(document);

        var hasPartEdges = records.Edges.Where(e => e.Type == "HAS_PART").ToList();
        hasPartEdges.Should().NotBeEmpty();
        hasPartEdges.All(e => e.IsComposition).Should().BeTrue();
    }

    [Test]
    [DisplayName("Creates spans with correct line numbers")]
    public async Task Materialize_CreatesSpansWithLineNumbers()
    {
        var loader = new CSSLoader();
        const string source = """
        .container {
            padding: 1rem;
        }
        """;

        using var art = CreateArtifact("styles.css", source);
        var document = await loader.LoadAsync(art.Artifact);
        var records = loader.Materialize(document);

        records.Spans.Should().NotBeEmpty();
        records.Spans.All(s => s.StartLine >= 1).Should().BeTrue();
        records.Spans.All(s => s.EndLine >= s.StartLine).Should().BeTrue();
    }

    [Test]
    [DisplayName("Generates X-ray headline")]
    public async Task Materialize_GeneratesExploreHeadline()
    {
        var loader = new CSSLoader();
        const string source = """
        .container { padding: 1rem; }
        .header { margin: 0; }
        """;

        using var art = CreateArtifact("styles.css", source);
        var document = await loader.LoadAsync(art.Artifact);
        var records = loader.Materialize(document);

        var artifact = records.Artifacts[0];
        artifact.Headline.Should().NotBeNullOrEmpty();
        artifact.Headline.Should().Contain("styles.css");
    }

    [Test]
    [DisplayName("Generates X-ray structure")]
    public async Task Materialize_GeneratesExploreStructure()
    {
        var loader = new CSSLoader();
        const string source = """
        @import "variables.css";

        @media screen and (min-width: 768px) {
            .container { width: 750px; }
        }

        .header {
            background: #333;
        }

        @keyframes fadeIn {
            from { opacity: 0; }
            to { opacity: 1; }
        }
        """;

        using var art = CreateArtifact("styles.css", source);
        var document = await loader.LoadAsync(art.Artifact);
        var records = loader.Materialize(document);

        var artifact = records.Artifacts[0];
        artifact.Structure.Should().NotBeNullOrEmpty();
        artifact.Structure.Should().Contain("@media");
        artifact.Structure.Should().Contain("@keyframes fadeIn");
    }

    [Test]
    [DisplayName("ANTLR client parses basic CSS")]
    public void AntlrClient_ParsesBasicCSS()
    {
        var client = new CSSAntlrClient();
        var result = client.Parse("""
        .container {
            padding: 1rem;
        }
        """);

        result.Rulesets.Should().HaveCount(1);
        result.Rulesets[0].Selector.Should().Contain(".container");
    }

    [Test]
    [DisplayName("SCSS ANTLR client parses basic SCSS")]
    public void SCSSAntlrClient_ParsesBasicSCSS()
    {
        var client = new SCSSAntlrClient();
        var result = client.Parse("""
        $color: blue;

        @mixin test {
            color: $color;
        }

        .btn {
            @include test;
        }
        """);

        result.Variables.Should().HaveCount(1);
        result.Variables[0].Name.Should().Contain("$color");

        result.Mixins.Should().HaveCount(1);
        result.Mixins[0].Name.Should().Be("test");

        result.Includes.Should().HaveCount(1);
        result.Includes[0].Name.Should().Be("test");
    }

    private static ArtifactScope CreateArtifact(string fileName, string content)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"repoql_css_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, fileName);
        File.WriteAllText(tempPath, content, Encoding.UTF8);

        var provider = new PhysicalFileProvider(tempDir);

        var artifact = new DiscoveredArtifact
        {
            File = provider.GetFileInfo(fileName),
            RepoUri = RepoUri.Parse($"file:///{fileName}")
        };

        return new ArtifactScope(artifact, tempDir, provider);
    }

    private sealed class ArtifactScope : IDisposable
    {
        public ArtifactScope(DiscoveredArtifact artifact, string tempDir, IFileProvider provider)
        {
            Artifact = artifact;
            _tempDir = tempDir;
            _provider = provider;
        }

        public DiscoveredArtifact Artifact { get; }

        private readonly string _tempDir;
        private readonly IFileProvider _provider;

        public void Dispose()
        {
            try
            {
                (_provider as IDisposable)?.Dispose();
            }
            catch
            {
                // ignore
            }

            try
            {
                if (Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, recursive: true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
