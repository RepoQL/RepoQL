using System.Text.Json.Nodes;
using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;
using RepoQL.Data.DuckDB;
using Artifact = RepoQL.Contracts.Models.Artifact;

namespace RepoQL.Formats.Cpp.Tests;

public sealed class CppSchemaProviderTests
{
    [Test]
    public void SchemaProvider_WhenEnabled_ReturnsCppViewsSql()
    {
        var provider = new CppSchemaProvider(enableViews: true);

        var scripts = provider.GetSchemaScripts().ToList();

        scripts.Should().ContainSingle(s => s.Identifier == "cpp_views");
        scripts[0].Sql.Should().Contain("CREATE OR REPLACE VIEW cpp_classes");
        scripts[0].Sql.Should().Contain("CREATE OR REPLACE VIEW cpp_functions");
        scripts[0].Sql.Should().Contain("CREATE OR REPLACE VIEW cpp_includes");
        scripts[0].Sql.Should().Contain("CREATE OR REPLACE VIEW cpp_templates");
        scripts[0].Sql.Should().Contain("CREATE OR REPLACE VIEW cpp_enums");
        scripts[0].Sql.Should().Contain("CREATE OR REPLACE VIEW cpp_macro_invocations");
        scripts[0].Sql.Should().Contain("CREATE OR REPLACE VIEW cpp_namespace_members");
    }

    [Test]
    public void SchemaProvider_WhenEnabled_RegistersCppViewsInDuckDb()
    {
        var provider = new CppSchemaProvider(enableViews: true);
        var scripts = provider.GetSchemaScripts().ToArray();

        using var db = new DuckDbDataStore(formatSchemaScripts: scripts);
        var views = db.Read(
            "SELECT view_name FROM duckdb_views() WHERE view_name LIKE 'cpp_%' ORDER BY view_name",
            r => r.GetString(0));

        views.Should().Contain("cpp_classes");
        views.Should().Contain("cpp_functions");
        views.Should().Contain("cpp_includes");
        views.Should().Contain("cpp_templates");
        views.Should().Contain("cpp_enums");
        views.Should().Contain("cpp_macro_invocations");
        views.Should().Contain("cpp_namespace_members");
    }

    [Test]
    public void SharedFunctionsView_IncludesCppMemberAndCppFunctionKinds()
    {
        using var db = new DuckDbDataStore();
        var uri = RepoUri.Parse("file:///src/example.cpp");
        db.IndexArtifact(uri, CreateCppParsedArtifact(uri));

        var rows = db.Read(
            "SELECT name, function_kind, lang FROM functions WHERE lang = 'cpp' ORDER BY name",
            r => new
            {
                Name = r.GetString(0),
                Kind = r.GetString(1),
                Lang = r.GetString(2)
            });

        rows.Should().Contain(r => r.Name == "connect" && r.Kind == "method" && r.Lang == "cpp");
        rows.Should().Contain(r => r.Name == "helper" && r.Kind == "function" && r.Lang == "cpp");
    }

    private static ParsedArtifact CreateCppParsedArtifact(RepoUri uri)
    {
        var artifactId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        var functionId = Guid.NewGuid();
        var memberSpanId = Guid.NewGuid();
        var functionSpanId = Guid.NewGuid();

        return new ParsedArtifact
        {
            Artifact = new Artifact
            {
                Id = artifactId,
                Digest = $"sha256:{Guid.NewGuid():N}",
                Size = 120,
                MediaType = SemanticMediaType.Create("text", "plain").WithKind("code.cpp"),
                Text = "namespace net { class Pool { public: void connect(); }; int helper() { return 0; } }"
            },
            DocumentNode = new Node
            {
                Id = documentId,
                Kind = "document",
                Uri = uri,
                ArtifactId = artifactId,
                Headline = "example.cpp",
                Props = new JsonObject
                {
                    ["language"] = "cpp"
                }
            },
            Children =
            [
                new Node
                {
                    Id = memberId,
                    Kind = "cpp.member",
                    Uri = RepoUri.FromSymbol(uri.Container, "net::Pool::connect", 1, 1),
                    SpanId = memberSpanId,
                    Props = new JsonObject
                    {
                        ["name"] = "connect",
                        ["qualified_name"] = "net::Pool::connect",
                        ["kind"] = "method",
                        ["declaring_type"] = "Pool",
                        ["accessibility"] = "public",
                        ["signature"] = "void connect()",
                        ["parameters"] = new JsonArray()
                    },
                    Headline = "method net::Pool::connect"
                },
                new Node
                {
                    Id = functionId,
                    Kind = "cpp.function",
                    Uri = RepoUri.FromSymbol(uri.Container, "net::helper", 1, 1),
                    SpanId = functionSpanId,
                    Props = new JsonObject
                    {
                        ["name"] = "helper",
                        ["qualified_name"] = "net::helper",
                        ["kind"] = "function",
                        ["signature"] = "int helper()",
                        ["return_type"] = "int",
                        ["parameters"] = new JsonArray()
                    },
                    Headline = "function net::helper"
                }
            ],
            Spans =
            [
                new Span
                {
                    Id = memberSpanId,
                    DocumentId = documentId,
                    StartLine = 1,
                    EndLine = 1,
                    StartColumn = 1,
                    EndColumn = 1
                },
                new Span
                {
                    Id = functionSpanId,
                    DocumentId = documentId,
                    StartLine = 1,
                    EndLine = 1,
                    StartColumn = 1,
                    EndColumn = 1
                }
            ],
            Edges =
            [
                new Edge
                {
                    SrcId = documentId,
                    DstId = memberId,
                    Type = "HAS_PART",
                    IsComposition = true,
                    Ordinal = 0
                },
                new Edge
                {
                    SrcId = documentId,
                    DstId = functionId,
                    Type = "HAS_PART",
                    IsComposition = true,
                    Ordinal = 1
                }
            ]
        };
    }
}
