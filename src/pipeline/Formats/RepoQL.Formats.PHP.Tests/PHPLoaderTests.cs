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
        using var scope = CreateLoader();

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
        using var scope = CreateLoader();
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

        var classNodes = records.Nodes.Where(n => n.Kind == "php.type" && n.Props["kind"]?.ToString() == "class").ToList();
        classNodes.Should().HaveCount(1);
        classNodes[0].Props["name"]!.ToString().Should().Be("UserService");

        var methodNodes = records.Nodes.Where(n => n.Kind == "php.member").ToList();
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
        using var scope = CreateLoader();
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

        var interfaceNodes = records.Nodes.Where(n => n.Kind == "php.type" && n.Props["kind"]?.ToString() == "interface").ToList();
        interfaceNodes.Should().HaveCount(1);
        interfaceNodes[0].Props["name"]!.ToString().Should().Be("UserRepositoryInterface");

        var methodNodes = records.Nodes.Where(n => n.Kind == "php.member").ToList();
        methodNodes.Should().HaveCount(2);
    }

    [Test]
    [DisplayName("Parses trait with methods")]
    public async Task LoadAndMaterialize_EmitsTrait()
    {
        using var scope = CreateLoader();
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

        var traitNodes = records.Nodes.Where(n => n.Kind == "php.type" && n.Props["kind"]?.ToString() == "trait").ToList();
        traitNodes.Should().HaveCount(1);
        traitNodes[0].Props["name"]!.ToString().Should().Be("Loggable");

        var methodNodes = records.Nodes.Where(n => n.Kind == "php.member").ToList();
        methodNodes.Should().HaveCount(1);
        methodNodes[0].Props["name"]!.ToString().Should().Be("log");
    }

    [Test]
    [DisplayName("Parses PHP 8.1 enum with cases")]
    public async Task LoadAndMaterialize_EmitsEnum()
    {
        using var scope = CreateLoader();
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

        var enumNodes = records.Nodes.Where(n => n.Kind == "php.type" && n.Props["kind"]?.ToString() == "enum").ToList();
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
        using var scope = CreateLoader();
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

        var funcNodes = records.Nodes.Where(n => n.Kind == "php.function" && n.Props["kind"]?.ToString() == "function").ToList();
        funcNodes.Should().HaveCount(2);
        funcNodes.Select(f => f.Props["name"]!.ToString()).Should().BeEquivalentTo(["calculateTotal", "formatCurrency"]);
    }

    [Test]
    [DisplayName("Creates HAS_PART edges for composition")]
    public async Task Materialize_CreatesCompositionEdges()
    {
        using var scope = CreateLoader();
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
        using var scope = CreateLoader();
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
        using var scope = CreateLoader();
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
        using var scope = CreateLoader();
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
        using var scope = CreateLoader();
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
        artifact.Headline.Should().Contain("ln, ~");
        artifact.Headline.Should().Contain("tok");
        artifact.Headline.Should().Contain("ns:App");
        artifact.Headline.Should().Contain("class UserService");
        artifact.Headline.Should().Contain("find");
        artifact.Headline.Should().Contain("create");
        artifact.Headline.Should().NotContain("code.php");
    }

    [Test]
    [DisplayName("Generates X-ray structure without truncation")]
    public async Task Materialize_GeneratesExploreStructure()
    {
        using var scope = CreateLoader();
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
        artifact.Structure.Should().NotContain("namespace ");
        artifact.Structure.Should().Contain("+ class UserService");
        artifact.Structure.Should().Contain("+?User findById(int $id)");
        artifact.Structure.Should().Contain("+User create(array $data)");
        artifact.Structure.Should().Contain("-void validate(array $data)");
        artifact.Structure.Should().Contain("#symbol=findById");
        artifact.Structure.Should().Contain("#symbol=create");
        artifact.Structure.Should().Contain("#symbol=validate");
    }

    [Test]
    [DisplayName("Includes class and interface constants in X-ray structure")]
    public async Task Materialize_GeneratesExploreStructure_WithConstants()
    {
        using var scope = CreateLoader();
        const string source = """
        <?php
        interface Flags {
            public const ENABLED = true;
        }
        class Service implements Flags {
            private const VERSION = 1;
        }
        """;

        using var art = CreateArtifact("Service.php", source);
        var document = await scope.Loader.LoadAsync(art.Artifact);
        var records = scope.Loader.Materialize(document);

        var artifact = records.Artifacts[0];
        artifact.Structure.Should().NotBeNullOrEmpty();
        artifact.Structure.Should().Contain("+const ENABLED    #symbol=ENABLED");
        artifact.Structure.Should().Contain("-const VERSION    #symbol=VERSION");
    }

    [Test]
    [DisplayName("Handles abstract and final class modifiers")]
    public async Task LoadAndMaterialize_HandlesClassModifiers()
    {
        using var scope = CreateLoader();
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

        var classNodes = records.Nodes.Where(n => n.Kind == "php.type" && n.Props["kind"]?.ToString() == "class").ToList();
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
        using var scope = CreateLoader();
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
    [DisplayName("Tree-sitter client parses namespace correctly")]
    public void TreeSitterClient_ParsesNamespace()
    {
        using var client = new TreeSitter.PhpTreeSitterClient();
        var result = client.Parse("<?php\nnamespace App\\Services;\nclass Foo {}");

        result.Namespace.Should().NotBeNull();
        result.Namespace.Should().Be(@"App\Services");
        result.Classes.Should().HaveCount(1);
        result.Classes[0].Namespace.Should().Be(@"App\Services");
    }

    [Test]
    [DisplayName("Parses enum with methods")]
    public async Task LoadAndMaterialize_EmitsEnumWithMethods()
    {
        using var scope = CreateLoader();
        const string source = """
        <?php
        enum Color: string {
            case Red = 'red';
            case Blue = 'blue';

            public function label(): string {
                return ucfirst($this->value);
            }
        }
        """;

        using var art = CreateArtifact("Color.php", source);
        var document = await scope.Loader.LoadAsync(art.Artifact);
        var records = scope.Loader.Materialize(document);

        var enumNodes = records.Nodes.Where(n => n.Kind == "php.type" && n.Props["kind"]?.ToString() == "enum").ToList();
        enumNodes.Should().HaveCount(1);
        enumNodes[0].Props["name"]!.ToString().Should().Be("Color");

        var caseNodes = records.Nodes.Where(n => n.Kind == "php.enum_case").ToList();
        caseNodes.Should().HaveCount(2);

        var methodNodes = records.Nodes.Where(n => n.Kind == "php.member").ToList();
        methodNodes.Should().HaveCount(1);
        methodNodes[0].Props["name"]!.ToString().Should().Be("label");
    }

    [Test]
    [DisplayName("Parses interface extending multiple interfaces")]
    public async Task LoadAndMaterialize_EmitsInterfaceMultipleExtends()
    {
        using var scope = CreateLoader();
        const string source = """
        <?php
        interface Readable {}
        interface Writable {}
        interface Stream extends Readable, Writable {
            public function close(): void;
        }
        """;

        using var art = CreateArtifact("Stream.php", source);
        var document = await scope.Loader.LoadAsync(art.Artifact);
        var records = scope.Loader.Materialize(document);

        var streamNode = records.Nodes.First(n => n.Kind == "php.type" && n.Props["name"]?.ToString() == "Stream");
        streamNode.Props["extends"]!.AsArray().Should().HaveCount(2);

        var extendsEdges = records.Edges.Where(e => e.Type == "EXTENDS").ToList();
        extendsEdges.Should().HaveCount(2);
    }

    [Test]
    [DisplayName("Handles PHP mixed with HTML")]
    public async Task LoadAndMaterialize_HandlesMixedHtmlPhp()
    {
        using var scope = CreateLoader();
        const string source = """
        <html>
        <body>
        <?php
        class Widget {
            public function render(): string { return ''; }
        }
        ?>
        <div>content</div>
        </body>
        </html>
        """;

        using var art = CreateArtifact("widget.phtml", source);
        var document = await scope.Loader.LoadAsync(art.Artifact);
        var records = scope.Loader.Materialize(document);

        var classNodes = records.Nodes.Where(n => n.Kind == "php.type" && n.Props["kind"]?.ToString() == "class").ToList();
        classNodes.Should().HaveCount(1);
        classNodes[0].Props["name"]!.ToString().Should().Be("Widget");

        var methodNodes = records.Nodes.Where(n => n.Kind == "php.member").ToList();
        methodNodes.Should().HaveCount(1);
    }

    [Test]
    [DisplayName("Handles files with parse errors gracefully")]
    public async Task LoadAndMaterialize_HandlesParseErrors()
    {
        using var scope = CreateLoader();
        const string source = """
        <?php
        class Valid {
            public function ok(): void {}
        }

        class Broken {
            public function missing_brace(): void {
        """;

        using var art = CreateArtifact("broken.php", source);
        var document = await scope.Loader.LoadAsync(art.Artifact);
        var records = scope.Loader.Materialize(document);

        // Should still produce partial results — one bad declaration doesn't break the rest
        records.Nodes.Should().NotBeEmpty();
        var classNodes = records.Nodes.Where(n => n.Kind == "php.type").ToList();
        classNodes.Should().NotBeEmpty("valid declarations should still be extracted");
    }

    [Test]
    [DisplayName("Handles empty PHP file")]
    public async Task LoadAndMaterialize_HandlesEmptyFile()
    {
        using var scope = CreateLoader();
        const string source = "<?php\n";

        using var art = CreateArtifact("empty.php", source);
        var document = await scope.Loader.LoadAsync(art.Artifact);
        var records = scope.Loader.Materialize(document);

        records.Artifacts.Should().HaveCount(1);
        var docNode = records.Nodes.FirstOrDefault(n => n.Kind == "document");
        docNode.Should().NotBeNull();
        records.Nodes.Where(n => n.Kind == "php.type").Should().BeEmpty();
    }

    [Test]
    [DisplayName("Handles abstract class with mixed abstract and concrete methods")]
    public async Task LoadAndMaterialize_HandlesAbstractClassMixedMethods()
    {
        using var scope = CreateLoader();
        const string source = """
        <?php
        abstract class Repository {
            abstract public function find(int $id): ?object;
            abstract protected function query(): string;
            public function findAll(): array { return []; }
        }
        """;

        using var art = CreateArtifact("Repository.php", source);
        var document = await scope.Loader.LoadAsync(art.Artifact);
        var records = scope.Loader.Materialize(document);

        var classNode = records.Nodes.First(n => n.Kind == "php.type");
        classNode.Props["is_abstract"]?.GetValue<bool>().Should().BeTrue();

        var methods = records.Nodes.Where(n => n.Kind == "php.member").ToList();
        methods.Should().HaveCount(3);

        var abstractMethods = methods.Where(m => m.Props["is_abstract"]?.GetValue<bool>() == true).ToList();
        abstractMethods.Should().HaveCount(2);
        abstractMethods.Select(m => m.Props["name"]!.ToString()).Should().BeEquivalentTo(["find", "query"]);
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

    private sealed class LoaderScope : IDisposable
    {
        public LoaderScope(PHPLoader loader)
        {
            Loader = loader;
        }

        public PHPLoader Loader { get; }

        public void Dispose() => Loader.Dispose();
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
