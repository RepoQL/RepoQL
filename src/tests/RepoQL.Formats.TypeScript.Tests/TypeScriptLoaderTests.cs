using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.Formats.TypeScript;

namespace RepoQL.Formats.TypeScript.Tests;

internal sealed class TypeScriptLoaderTests
{
    [Test]
    public async Task CanLoadAsync_RecognizesTsAndJsExtensions()
    {
        using var scope = CreateLoader();

        using var ts = CreateArtifact("sample.ts", "export const x = 1;");
        using var tsx = CreateArtifact("sample.tsx", "export default function App(){ return <div/>; }");
        using var js = CreateArtifact("sample.js", "module.exports = 1;");
        using var jsx = CreateArtifact("sample.jsx", "export const C = () => <span/>;");

        (await scope.Loader.CanLoadAsync(ts.Artifact)).Should().BeTrue();
        ts.Artifact.MediaType!.Kind.Should().Be("code.typescript");

        (await scope.Loader.CanLoadAsync(tsx.Artifact)).Should().BeTrue();
        tsx.Artifact.MediaType!.Kind.Should().Be("code.typescript.react");

        (await scope.Loader.CanLoadAsync(js.Artifact)).Should().BeTrue();
        js.Artifact.MediaType!.Kind.Should().Be("code.javascript");

        (await scope.Loader.CanLoadAsync(jsx.Artifact)).Should().BeTrue();
        jsx.Artifact.MediaType!.Kind.Should().Be("code.javascript.react");
    }

    [Test]
    public async Task LoadAndMaterialize_EmitsDocumentAndDeclarations()
    {
        using var scope = CreateLoader();
        const string source = """
        import fs from "fs";
        export interface User { id: string }
        export class Service { ping() {} }
        const local = 1;
        """;

        using var art = CreateArtifact("model.ts", source);
        var document = await scope.Loader.LoadAsync(art.Artifact);
        var parse = GetParse(document);
        parse.Diagnostics.Should().BeEmpty();
        parse.Declarations.Count.Should().BeGreaterThan(0, "top-level declarations should be discovered");

        var records = scope.Loader.Materialize(document);
        records.Artifacts.Should().HaveCount(1);
        records.Nodes.Should().NotBeEmpty();
        records.Spans.Should().NotBeEmpty();

        var docNode = records.Nodes.First(n => n.Kind == "document");
        docNode.Props["media_type"]!.ToString().Should().Contain("code.typescript");

        var declKinds = records.Nodes.Where(n => n.Kind.StartsWith("ts_decl_")).Select(n => n.Kind).ToList();
        declKinds.Should().Contain("ts_decl_interface");
        declKinds.Should().Contain("ts_decl_class");
    }

    [Test]
    public async Task LoadAsync_ReportsDiagnostics_ForInvalidSource()
    {
        using var scope = CreateLoader();
        using var art = CreateArtifact("broken.ts", "export const = ");

        var document = await scope.Loader.LoadAsync(art.Artifact);
        var parse = GetParse(document);

        parse.Diagnostics.Should().NotBeEmpty();
        // Materialization should still succeed even with diagnostics.
        var records = scope.Loader.Materialize(document);
        records.Artifacts.Should().HaveCount(1);
    }

    [Test]
    public async Task Parse_ImportExportVariants_CapturedInParseResult()
    {
        using var scope = CreateLoader();
        const string source = """
        import fs from "fs";
        import { readFile } from "fs";
        import * as path from "path";
        import "./side";
        export * from "./side";
        export default function Main() {}
        """;
        using var art = CreateArtifact("imports.ts", source);

        var document = await scope.Loader.LoadAsync(art.Artifact);
        var parse = GetParse(document);

        parse.Diagnostics.Should().BeEmpty();
        parse.Imports.Select(i => i.ImportStyle).Should().BeEquivalentTo(new[]
        {
            "default", "named", "namespace", "sideEffect"
        });

        parse.Exports.Should().NotBeEmpty();
        parse.Exports.Any(e => e.ExportKind == "reexport" && e.Name == "*").Should().BeTrue();
        parse.Declarations.Any(d => d.ExportKind == "default" && d.Name == "Main").Should().BeTrue();
    }

    [Test]
    public async Task ReactDetection_JsxFile_MarksComponent()
    {
        using var scope = CreateLoader();
        const string source = """
        export function Widget() { return <div>hi</div>; }
        """;
        using var art = CreateArtifact("Widget.jsx", source);

        var document = await scope.Loader.LoadAsync(art.Artifact);
        var parse = GetParse(document);
        parse.Diagnostics.Should().BeEmpty();

        parse.Declarations.Any(d => d.IsComponent && d.Name == "Widget").Should().BeTrue();
    }

    [Test]
    public async Task ReactDetection_PascalWithoutJsx_NotComponent()
    {
        using var scope = CreateLoader();
        const string source = "export function Widget() { return 1; }";
        using var art = CreateArtifact("Widget.ts", source);

        var document = await scope.Loader.LoadAsync(art.Artifact);
        var parse = GetParse(document);

        parse.Declarations.Any(d => d.Name == "Widget" && d.IsComponent).Should().BeFalse();
    }

    [Test]
    public async Task Members_HaveSpans()
    {
        using var scope = CreateLoader();
        const string source = """
        export class Service { ping() {} field = 1; }
        export interface IUser { id: string; }
        export enum E { A }
        """;
        using var art = CreateArtifact("members.ts", source);

        var document = await scope.Loader.LoadAsync(art.Artifact);
        var parse = GetParse(document);
        parse.Diagnostics.Should().BeEmpty();

        var members = parse.Declarations.SelectMany(d => d.Members).ToList();
        members.Should().NotBeEmpty();
        members.All(m => m.Span.End > m.Span.Start).Should().BeTrue();
    }

    [Test]
    public async Task LoadAsync_FlagsReactComponentInTsx()
    {
        using var scope = CreateLoader();
        const string source = """
        import React from "react";
        export function Button() { return <button>Click</button>; }
        """;

        using var art = CreateArtifact("Button.tsx", source);
        var document = await scope.Loader.LoadAsync(art.Artifact);
        var parse = GetParse(document);
        parse.Diagnostics.Should().BeEmpty();
        parse.Declarations.Should().NotBeEmpty();

        var records = scope.Loader.Materialize(document);
        var components = records.Nodes.Where(n =>
            n.Kind == "ts_decl_function" &&
            n.Props.TryGetPropertyValue("is_component", out var flag) &&
            flag?.GetValue<bool>() == true).ToList();

        components.Should().HaveCount(1);
        components[0].Props["name"]!.ToString().Should().Be("Button");
    }

    private static TypeScriptParseResult GetParse(DocumentModel document)
    {
        document.SyntaxTree.Should().NotBeNull("syntax tree should be populated");
        return (TypeScriptParseResult)document.SyntaxTree!;
    }

    private static LoaderScope CreateLoader()
    {
        var nodeClient = new TypeScriptNodeClient();
        var loader = new TypeScriptLoader(nodeClient);
        return new LoaderScope(loader, nodeClient);
    }

    private static ArtifactScope CreateArtifact(string fileName, string content)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"repoql_ts_{Guid.NewGuid():N}");
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
        public LoaderScope(TypeScriptLoader loader, TypeScriptNodeClient client)
        {
            Loader = loader;
            Client = client;
        }

        public TypeScriptLoader Loader { get; }
        private TypeScriptNodeClient Client { get; }

        public void Dispose()
        {
            Client.Dispose();
        }
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
