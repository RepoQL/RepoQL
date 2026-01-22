using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.Formats.PHP;

namespace RepoQL.Formats.PHP.Tests;

public sealed class PHPLoaderTests
{
    [Test]
    [DisplayName("Recognizes .php and .phtml extensions")]
    public async Task CanLoadAsync_RecognizesPhpExtensions()
    {
        var scope = CreateLoader();

        using var php = CreateArtifact("sample.php", "<?php class Foo {}");
        using var phtml = CreateArtifact("sample.phtml", "<?php echo 'hello'; ?>");
        using var inc = CreateArtifact("sample.inc", "<?php function helper() {}");

        (await scope.Loader.CanLoadAsync(php.Artifact)).Should().BeTrue();
        php.Artifact.MediaType!.Kind.Should().Be("code.php");

        (await scope.Loader.CanLoadAsync(phtml.Artifact)).Should().BeTrue();
        phtml.Artifact.MediaType!.Kind.Should().Be("code.php.template");

        (await scope.Loader.CanLoadAsync(inc.Artifact)).Should().BeTrue();
        inc.Artifact.MediaType!.Kind.Should().Be("code.php");
    }

    [Test]
    [DisplayName("Parses class with methods and properties")]
    public async Task LoadAndMaterialize_EmitsClassWithMembers()
    {
        var scope = CreateLoader();
        const string source = """
        <?php
        namespace App\Services;

        class UserService {
            private string $name;
            public function findById(int $id): ?User {
                return null;
            }
            public function create(array $data): User {
                return new User();
            }
        }
        """;

        using var art = CreateArtifact("UserService.php", source);
        var document = await scope.Loader.LoadAsync(art.Artifact);
        var records = scope.Loader.Materialize(document);

        records.Artifacts.Should().HaveCount(1);
        records.Nodes.Should().NotBeEmpty();
        records.Spans.Should().NotBeEmpty();

        var docNode = records.Nodes.First(n => n.Kind == "document");
        docNode.Should().NotBeNull();

        var classNodes = records.Nodes.Where(n => n.Kind == "php.class").ToList();
        classNodes.Should().HaveCount(1);
        classNodes[0].Props["name"]!.ToString().Should().Be("UserService");

        var methodNodes = records.Nodes.Where(n => n.Kind == "php.method").ToList();
        methodNodes.Should().HaveCount(2);
        methodNodes.Select(m => m.Props["name"]!.ToString()).Should().BeEquivalentTo(["findById", "create"]);

        var propNodes = records.Nodes.Where(n => n.Kind == "php.property").ToList();
        propNodes.Should().HaveCount(1);
        propNodes[0].Props["name"]!.ToString().Should().Be("$name");
    }

    [Test]
    [DisplayName("Parses interface with methods")]
    public async Task LoadAndMaterialize_EmitsInterface()
    {
        var scope = CreateLoader();
        const string source = """
        <?php
        namespace App\Contracts;

        interface UserRepositoryInterface {
            public function find(int $id): ?User;
            public function save(User $user): void;
        }
        """;

        using var art = CreateArtifact("UserRepositoryInterface.php", source);
        var document = await scope.Loader.LoadAsync(art.Artifact);
        var records = scope.Loader.Materialize(document);

        var interfaceNodes = records.Nodes.Where(n => n.Kind == "php.interface").ToList();
        interfaceNodes.Should().HaveCount(1);
        interfaceNodes[0].Props["name"]!.ToString().Should().Be("UserRepositoryInterface");

        var methodNodes = records.Nodes.Where(n => n.Kind == "php.method").ToList();
        methodNodes.Should().HaveCount(2);
    }

    [Test]
    [DisplayName("Parses trait with methods")]
    public async Task LoadAndMaterialize_EmitsTrait()
    {
        var scope = CreateLoader();
        const string source = """
        <?php
        trait Loggable {
            public function log(string $message): void {
                echo $message;
            }
        }
        """;

        using var art = CreateArtifact("Loggable.php", source);
        var document = await scope.Loader.LoadAsync(art.Artifact);
        var records = scope.Loader.Materialize(document);

        var traitNodes = records.Nodes.Where(n => n.Kind == "php.trait").ToList();
        traitNodes.Should().HaveCount(1);
        traitNodes[0].Props["name"]!.ToString().Should().Be("Loggable");

        var methodNodes = records.Nodes.Where(n => n.Kind == "php.method").ToList();
        methodNodes.Should().HaveCount(1);
        methodNodes[0].Props["name"]!.ToString().Should().Be("log");
    }

    [Test]
    [DisplayName("Parses PHP 8.1 enum with cases")]
    public async Task LoadAndMaterialize_EmitsEnum()
    {
        var scope = CreateLoader();
        const string source = """
        <?php
        enum Status: string {
            case Pending = 'pending';
            case Active = 'active';
            case Inactive = 'inactive';
        }
        """;

        using var art = CreateArtifact("Status.php", source);
        var document = await scope.Loader.LoadAsync(art.Artifact);
        var records = scope.Loader.Materialize(document);

        var enumNodes = records.Nodes.Where(n => n.Kind == "php.enum").ToList();
        enumNodes.Should().HaveCount(1);
        enumNodes[0].Props["name"]!.ToString().Should().Be("Status");

        var caseNodes = records.Nodes.Where(n => n.Kind == "php.enum_case").ToList();
        caseNodes.Should().HaveCount(3);
        caseNodes.Select(c => c.Props["name"]!.ToString()).Should().BeEquivalentTo(["Pending", "Active", "Inactive"]);
    }

    [Test]
    [DisplayName("Parses standalone functions")]
    public async Task LoadAndMaterialize_EmitsFunctions()
    {
        var scope = CreateLoader();
        const string source = """
        <?php
        function calculateTotal(array $items): float {
            return array_sum($items);
        }

        function formatCurrency(float $amount, string $currency = 'USD'): string {
            return $currency . ' ' . number_format($amount, 2);
        }
        """;

        using var art = CreateArtifact("helpers.php", source);
        var document = await scope.Loader.LoadAsync(art.Artifact);
        var records = scope.Loader.Materialize(document);

        var funcNodes = records.Nodes.Where(n => n.Kind == "php.function").ToList();
        funcNodes.Should().HaveCount(2);
        funcNodes.Select(f => f.Props["name"]!.ToString()).Should().BeEquivalentTo(["calculateTotal", "formatCurrency"]);
    }

    [Test]
    [DisplayName("Creates HAS_PART edges for composition")]
    public async Task Materialize_CreatesCompositionEdges()
    {
        var scope = CreateLoader();
        const string source = """
        <?php
        class Service {
            public function execute(): void {}
        }
        """;

        using var art = CreateArtifact("Service.php", source);
        var document = await scope.Loader.LoadAsync(art.Artifact);
        var records = scope.Loader.Materialize(document);

        var hasPartEdges = records.Edges.Where(e => e.Type == "HAS_PART").ToList();
        hasPartEdges.Should().NotBeEmpty();
        hasPartEdges.All(e => e.IsComposition).Should().BeTrue("HAS_PART edges should be composition edges");
    }

    [Test]
    [DisplayName("Creates EXTENDS edge for class inheritance")]
    public async Task Materialize_CreatesExtendsEdge()
    {
        var scope = CreateLoader();
        const string source = """
        <?php
        class BaseService {}
        class UserService extends BaseService {}
        """;

        using var art = CreateArtifact("UserService.php", source);
        var document = await scope.Loader.LoadAsync(art.Artifact);
        var records = scope.Loader.Materialize(document);

        var extendsEdges = records.Edges.Where(e => e.Type == "EXTENDS").ToList();
        extendsEdges.Should().HaveCount(1);
        extendsEdges[0].IsComposition.Should().BeFalse();
        extendsEdges[0].Props!["target"]!.ToString().Should().Be("BaseService");
    }

    [Test]
    [DisplayName("Creates IMPLEMENTS edges for interfaces")]
    public async Task Materialize_CreatesImplementsEdges()
    {
        var scope = CreateLoader();
        const string source = """
        <?php
        interface Countable {}
        interface Serializable {}
        class Collection implements Countable, Serializable {}
        """;

        using var art = CreateArtifact("Collection.php", source);
        var document = await scope.Loader.LoadAsync(art.Artifact);
        var records = scope.Loader.Materialize(document);

        var implementsEdges = records.Edges.Where(e => e.Type == "IMPLEMENTS").ToList();
        implementsEdges.Should().HaveCount(2);
        implementsEdges.All(e => !e.IsComposition).Should().BeTrue();
    }

    [Test]
    [DisplayName("Creates USES_TRAIT edges")]
    public async Task Materialize_CreatesUsesTraitEdges()
    {
        var scope = CreateLoader();
        const string source = """
        <?php
        trait Loggable {}
        trait Cacheable {}
        class Service {
            use Loggable, Cacheable;
        }
        """;

        using var art = CreateArtifact("Service.php", source);
        var document = await scope.Loader.LoadAsync(art.Artifact);
        var records = scope.Loader.Materialize(document);

        var usesTraitEdges = records.Edges.Where(e => e.Type == "USES_TRAIT").ToList();
        usesTraitEdges.Should().HaveCount(2);
    }

    [Test]
    [DisplayName("Generates X-ray headline with method names")]
    public async Task Materialize_GeneratesExploreHeadline()
    {
        var scope = CreateLoader();
        const string source = """
        <?php
        namespace App;
        class UserService {
            public function find(): void {}
            public function create(): void {}
        }
        """;

        using var art = CreateArtifact("UserService.php", source);
        var document = await scope.Loader.LoadAsync(art.Artifact);
        var records = scope.Loader.Materialize(document);

        var artifact = records.Artifacts[0];
        artifact.Headline.Should().NotBeNullOrEmpty();
        artifact.Headline.Should().Contain("UserService");
        artifact.Headline.Should().Contain("find");
        artifact.Headline.Should().Contain("create");
    }

    [Test]
    [DisplayName("Generates X-ray structure without truncation")]
    public async Task Materialize_GeneratesExploreStructure()
    {
        var scope = CreateLoader();
        const string source = """
        <?php
        namespace App\Services;
        class UserService {
            public function findById(int $id): ?User {}
            public function create(array $data): User {}
            private function validate(array $data): void {}
        }
        """;

        using var art = CreateArtifact("UserService.php", source);
        var document = await scope.Loader.LoadAsync(art.Artifact);
        var records = scope.Loader.Materialize(document);

        var artifact = records.Artifacts[0];
        artifact.Structure.Should().NotBeNullOrEmpty();
        artifact.Structure.Should().Contain("namespace App\\Services");
        artifact.Structure.Should().Contain("class UserService");
        artifact.Structure.Should().Contain("findById");
        artifact.Structure.Should().Contain("create");
        artifact.Structure.Should().Contain("validate");
    }

    [Test]
    [DisplayName("Handles abstract and final class modifiers")]
    public async Task LoadAndMaterialize_HandlesClassModifiers()
    {
        var scope = CreateLoader();
        const string source = """
        <?php
        abstract class BaseHandler {
            abstract public function handle(): void;
        }
        final class ConcreteHandler extends BaseHandler {
            public function handle(): void {}
        }
        """;

        using var art = CreateArtifact("Handler.php", source);
        var document = await scope.Loader.LoadAsync(art.Artifact);
        var records = scope.Loader.Materialize(document);

        var classNodes = records.Nodes.Where(n => n.Kind == "php.class").ToList();
        classNodes.Should().HaveCount(2);

        var abstractClass = classNodes.First(c => c.Props["name"]!.ToString() == "BaseHandler");
        abstractClass.Props["is_abstract"]?.GetValue<bool>().Should().BeTrue();

        var finalClass = classNodes.First(c => c.Props["name"]!.ToString() == "ConcreteHandler");
        finalClass.Props["is_final"]?.GetValue<bool>().Should().BeTrue();
    }

    [Test]
    [DisplayName("Creates spans with correct line numbers")]
    public async Task Materialize_CreatesSpansWithLineNumbers()
    {
        var scope = CreateLoader();
        const string source = """
        <?php
        class Service {
            public function execute(): void {}
        }
        """;

        using var art = CreateArtifact("Service.php", source);
        var document = await scope.Loader.LoadAsync(art.Artifact);
        var records = scope.Loader.Materialize(document);

        records.Spans.Should().NotBeEmpty();
        records.Spans.All(s => s.StartLine >= 1).Should().BeTrue("line numbers should be 1-based");
        records.Spans.All(s => s.EndLine >= s.StartLine).Should().BeTrue("end line should be >= start line");
        records.Spans.All(s => s.EndByte > s.StartByte).Should().BeTrue("end byte should be > start byte");
    }

    [Test]
    [DisplayName("ANTLR client parses namespace correctly")]
    public void AntlrClient_ParsesNamespace()
    {
        var client = new PHPAntlrClient();
        var result = client.Parse("<?php\nnamespace App\\Services;\nclass Foo {}");

        result.Namespace.Should().NotBeNull();
        result.Namespace.Should().Be(@"App\Services");
        result.Classes.Should().HaveCount(1);
        result.Classes[0].Namespace.Should().Be(@"App\Services");
    }

    private static LoaderScope CreateLoader()
    {
        var loader = new PHPLoader();
        return new LoaderScope(loader);
    }

    private static ArtifactScope CreateArtifact(string fileName, string content)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"repoql_php_{Guid.NewGuid():N}");
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

    private sealed class LoaderScope
    {
        public LoaderScope(PHPLoader loader)
        {
            Loader = loader;
        }

        public PHPLoader Loader { get; }
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
