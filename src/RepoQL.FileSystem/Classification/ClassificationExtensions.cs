using System.Collections.Immutable;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;

namespace RepoQL.FileSystem.Classification;

public static class ClassificationExtensions
{
    private static readonly ImmutableDictionary<string, SemanticMediaType> ExtensionMap = BuildExtensionMap();
    private static readonly ImmutableDictionary<string, SemanticMediaType> FileNameMap = BuildFileNameMap();
    private static readonly (string Suffix, string MediaType)[] CompoundExtensions = BuildCompoundExtensionMap();

    public static SemanticMediaType? GuessMediaTypeFromNamingConvention(this IFileInfo fileInfo)
    {
        var name = fileInfo.Name;

        foreach (var (suffix, mapped) in CompoundExtensions)
        {
            if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) 
                continue;
            return SemanticMediaType.Parse(mapped);
        }

        if (FileNameMap.TryGetValue(name, out var mediaType))
            return mediaType;

        var extension = Path.GetExtension(name);
        if (!string.IsNullOrEmpty(extension) && ExtensionMap.TryGetValue(extension, out mediaType))
            return mediaType;
        
        return null;
    }

    private static ImmutableDictionary<string, SemanticMediaType> BuildExtensionMap()
    {
        var builder = ImmutableDictionary.CreateBuilder<string, SemanticMediaType>(StringComparer.OrdinalIgnoreCase);

        // Source code and headers
        Add("text/plain;kind=code.c", ".c", ".h");
        Add("text/plain;kind=code.cpp", ".cpp", ".cc", ".cxx");
        Add("text/plain;kind=code.cpp-header", ".hpp", ".hh", ".hxx");
        Add("text/plain;kind=code.csharp", ".cs");
        Add("text/plain;kind=code.visualbasic", ".vb");
        Add("text/plain;kind=code.fsharp", ".fs", ".fsx");
        Add("text/plain;kind=code.java", ".java");
        Add("text/plain;kind=code.kotlin", ".kt", ".kts");
        Add("text/plain;kind=code.groovy", ".groovy");
        Add("text/plain;kind=code.scala", ".scala");
        Add("text/plain;kind=code.go", ".go");
        Add("text/plain;kind=code.rust", ".rs");
        Add("text/plain;kind=code.swift", ".swift");
        Add("text/plain;kind=code.objc", ".m");
        Add("text/plain;kind=code.objcpp", ".mm");
        Add("text/plain;kind=code.python", ".py", ".pyi");
        Add("text/plain;kind=code.ruby", ".rb", ".erb", ".rhtml");
        Add("text/plain;kind=code.javascript", ".js", ".mjs", ".cjs", ".jsx");
        Add("text/plain;kind=code.typescript", ".ts", ".tsx", ".d.ts");
        Add("text/plain;kind=code.coffeescript", ".coffee");
        Add("text/plain;kind=code.php", ".php", ".phtml");
        Add("text/plain;kind=code.perl", ".pl", ".pm", ".t");
        Add("text/plain;kind=code.r", ".r", ".rmd");
        Add("text/plain;kind=code.julia", ".jl");
        Add("text/plain;kind=code.lua", ".lua");
        Add("text/plain;kind=code.shell", ".sh", ".bash", ".zsh", ".fish", ".nu");
        Add("text/plain;kind=code.powershell", ".ps1", ".psm1");
        Add("text/plain;kind=code.batch", ".bat", ".cmd");
        Add("text/plain;kind=code.clojure", ".clj", ".cljs", ".cljc");
        Add("text/plain;kind=data.edn", ".edn");
        Add("text/plain;kind=code.elm", ".elm");
        Add("text/plain;kind=code.elixir", ".ex", ".exs", ".eex", ".heex");
        Add("text/plain;kind=code.erlang", ".erl", ".hrl");
        Add("text/plain;kind=code.nim", ".nim");
        Add("text/plain;kind=config.nimble", ".nimble");
        Add("text/plain;kind=code.ocaml", ".ml", ".mli", ".mll", ".mly");
        Add("text/plain;kind=code.haskell", ".hs", ".lhs");
        Add("text/plain;kind=code.agda", ".agda");
        Add("text/plain;kind=code.idris", ".idr");
        Add("text/plain;kind=code.fortran", ".f90", ".f95", ".f03", ".f", ".for");
        Add("text/plain;kind=code.pascal", ".pas", ".dpr");
        Add("text/plain;kind=code.ada", ".ada", ".adb", ".ads");
        Add("text/plain;kind=code.verilog", ".v", ".sv", ".svh");
        Add("text/plain;kind=code.vhdl", ".vhdl", ".vhd");
        Add("text/plain;kind=code.assembly", ".asm", ".s", ".S", ".nasm");
        Add("application/wasm", ".wasm");
        Add("text/plain;kind=code.wast", ".wast");
        Add("text/plain;kind=code.zig", ".zig");
        Add("text/plain;kind=code.dart", ".dart");
        Add("text/plain;kind=code.gdscript", ".gd");
        Add("text/plain;kind=code.haxe", ".hx", ".hxsl");
        Add("text/plain;kind=project.haxe", ".hxproj");
        Add("text/plain;kind=code.opencl", ".cl");
        Add("text/plain;kind=code.cuda", ".cu", ".cuh");
        Add("text/plain;kind=code.metal", ".metal");
        Add("text/plain;kind=code.glsl", ".glsl", ".vert", ".frag", ".tesc", ".tese", ".geom", ".comp");
        Add("text/plain;kind=code.wgsl", ".wgsl");
        Add("text/plain;kind=unity.material", ".mat");
        Add("text/plain;kind=unity.shader", ".shader");
        Add("application/vnd.unity", ".unity");
        Add("text/plain;kind=ux.markup", ".ux");
        Add("text/plain;kind=code.qml", ".qml");
        Add("application/xml;kind=qt.resource", ".qrc");
        Add("text/plain;kind=code.qt-script", ".qs");
        Add("text/plain;kind=build.qmake", ".pro", ".pri");

        // Project and build descriptors
        Add("application/xml;kind=dotnet.csproj", ".csproj");
        Add("application/xml;kind=project.visualbasic", ".vbproj");
        Add("application/xml;kind=project.fsharp", ".fsproj");
        Add("text/plain;kind=dotnet.sln", ".sln", ".slnf");
        Add("application/xml;kind=project.msbuild", ".proj", ".props", ".targets", ".msbuildproj");
        Add("application/xml;kind=project.vcxproj", ".vcxproj", ".filters", ".vcproj");
        Add("application/xcode-project", ".xcodeproj", ".xcworkspace");
        Add("text/plain;kind=config.xcode", ".pbxproj", ".xcconfig");
        Add("application/xml;kind=config.apple-plist", ".plist");
        Add("text/plain;kind=i18n.apple-strings", ".strings");
        Add("application/xml;kind=ios.storyboard", ".storyboard", ".xib");
        Add("application/json;kind=swiftpm.config", ".swiftpm");
        Add("text/plain;kind=build.gradle", ".gradle");
        Add("application/xml;kind=build.maven", ".pom");
        Add("application/xml;kind=project.intellij", ".iml", ".ipr", ".iws");
        Add("text/plain;kind=build.bazel-module", ".bazel");
        Add("text/plain;kind=code.starlark", ".bzl", ".starlark");
        Add("text/html", ".html", ".htm", ".xhtml", ".shtml", ".jsp", ".asp", ".aspx");
        Add("text/html;kind=template.razor", ".cshtml", ".vbhtml", ".razor");
        Add("text/plain;kind=template.vue", ".vue");
        Add("text/plain;kind=template.svelte", ".svelte");
        Add("text/plain;kind=template.astro", ".astro");
        Add("text/plain;kind=template.marko", ".marko");
        Add("text/plain;kind=template.thymeleaf", ".thymeleaf");
        Add("text/plain;kind=template.twig", ".twig");
        Add("text/plain;kind=template.handlebars", ".hbs", ".handlebars");
        Add("text/plain;kind=template.mustache", ".mustache");
        Add("text/plain;kind=template.nunjucks", ".njk");
        Add("text/plain;kind=template.ejs", ".ejs");
        Add("text/plain;kind=template.pug", ".pug", ".jade");
        Add("text/plain;kind=template.haml", ".haml");
        Add("text/plain;kind=template.slim", ".slim");
        Add("text/plain;kind=template.liquid", ".liquid");
        Add("text/plain;kind=template.dust", ".dust");
        Add("text/plain;kind=template.gotemplate", ".gotmpl");
        Add("text/plain;kind=template.freemarker", ".ftl");
        Add("text/plain;kind=template.generic", ".tmpl", ".tpl", ".eta", ".soy", ".latte", ".mako", ".mjml", ".tmpl", ".tpl");
        Add("text/plain;kind=template.jinja", ".j2", ".jinja");
        Add("application/xslt+xml", ".xsl", ".xslt");
        Add("text/css", ".css", ".scss", ".sass", ".less", ".styl", ".pcss", ".postcss", ".sss");
        Add("application/json;kind=source-map", ".map");
        Add("font/woff", ".woff");

        // Documentation and knowledge
        Add("text/markdown;kind=markdown.doc", ".md", ".markdown", ".mdown", ".mdx");
        Add("text/plain;kind=docs.rst", ".rst");
        Add("text/plain;kind=docs.asciidoc", ".adoc", ".asciidoc");
        Add("text/plain;kind=docs.textile", ".textile");
        Add("text/plain;kind=docs.wiki", ".wiki", ".creole");
        Add("text/plain;kind=docs.org", ".org");
        Add("application/x-ipynb+json", ".ipynb");
        Add("text/x-tex", ".tex", ".latex", ".ltx", ".sty", ".cls", ".toc");
        Add("text/plain;kind=docs.bibtex", ".bib", ".bibtex");
        Add("text/plain;kind=logs.latex", ".log");
        Add("application/pdf", ".pdf");
        Add("application/rtf", ".rtf");
        Add("application/msword", ".doc");
        Add("application/vnd.ms-excel", ".xls");
        Add("application/vnd.ms-powerpoint", ".ppt");
        Add("application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".docx");
        Add("application/vnd.openxmlformats-officedocument.presentationml.presentation", ".pptx");
        Add("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx");
        Add("application/vnd.oasis.opendocument.presentation", ".odp");
        Add("application/vnd.oasis.opendocument.text", ".odt");
        Add("application/vnd.oasis.opendocument.spreadsheet", ".ods");
        Add("application/vnd.oasis.opendocument.text-flat-xml", ".fodt");
        Add("application/vnd.oasis.opendocument.presentation-flat-xml", ".fodp");
        Add("application/vnd.oasis.opendocument.graphics-flat-xml", ".fodg");
        Add("application/xml;kind=diagram.drawio", ".drawio", ".dio");
        Add("application/vnd.ms-visio.drawing", ".vsdx");
        Add("application/xmind", ".xmind");
        Add("text/plain;kind=diagram.plantuml", ".puml", ".wsd", ".iuml", ".pu", ".uml");
        Add("text/plain;kind=diagram.mermaid", ".mermaid");
        Add("text/csv", ".csv");
        Add("text/tab-separated-values", ".tsv");
        Add("text/plain;kind=data.psv", ".psv");

        // Structured data and configs
        Add("application/json", ".json", ".json5", ".jsonc", ".jsonl", ".ndjson", ".geojson", ".topojson");
        Add("application/bson", ".bson");
        Add("text/plain;kind=config.hjson", ".hjson");
        Add("text/plain;kind=config.ron", ".ron");
        Add("application/yaml", ".yaml", ".yml", ".syaml");
        Add("application/toml", ".toml");
        Add("text/plain;kind=config.ini", ".ini", ".cfg", ".conf", ".cnf", ".ini.dist", ".cfg.example");
        Add("text/plain;kind=config.properties", ".properties", ".prop");
        Add("text/plain;kind=config.env", ".dotenv", ".env", ".envrc", ".rc");
        Add("text/plain;kind=config.editorconfig", ".editorconfig");
        Add("text/plain;kind=config.git", ".gitattributes", ".gitignore", ".gitmodules", ".mailmap", ".gitmessage", ".gitconfig", ".git-blame-ignore-revs");
        Add("text/plain;kind=config.ignore", ".dockerignore", ".npmignore", ".eslintignore", ".helmignore", ".vscodeignore", ".bazelignore");
        Add("application/json;kind=config.eslint", ".eslintrc", ".eslintrc.json", ".eslintrc.yaml");
        Add("application/json;kind=config.prettier", ".prettierrc", ".prettierrc.json", ".prettierrc.yaml");
        Add("application/json;kind=config.stylelint", ".stylelintrc");
        Add("application/json;kind=config.babel", ".babelrc", ".babelrc.json");
        Add("text/plain;kind=config.browserslist", ".browserslistrc");
        Add("application/json;kind=config.commitlint", ".commitlintrc", ".commitlintrc.json");
        Add("application/json;kind=config.lintstaged", ".lintstagedrc", ".lintstagedrc.json");
        Add("application/json;kind=config.renovate", ".renovaterc");
        Add("text/plain;kind=config.husky", ".huskyrc");
        Add("text/plain;kind=config.npm", ".npmrc");
        Add("text/plain;kind=config.yarn", ".yarnrc", ".yarnrc.yml");
        Add("text/plain;kind=config.pnpm", ".pnpmfile.cjs");
        Add("application/json;kind=config.vscode", ".code-workspace", ".code-snippets");
        Add("application/json;kind=config.sublime", ".sublime-project", ".sublime-workspace");
        Add("application/xml;kind=package.nuspec", ".nuspec");
        Add("application/zip;kind=package.nupkg", ".nupkg", ".snupkg");
        Add("text/plain;kind=config.paket", ".paket");
        Add("text/plain;kind=config.paket-lock", ".paket.lock");
        Add("text/plain;kind=code.csharp-script", ".csx", ".cake");
        Add("text/plain;kind=build.sbt", ".sbt");
        Add("application/xml;kind=build.ivy", ".ivy", ".ivysettings");
        Add("text/xml;kind=project.codeblocks", ".cbp");
        Add("text/plain;kind=build.cmake", ".cmake");
        Add("text/plain;kind=build.qbs", ".qbs");
        Add("text/plain;kind=build.ninja", ".ninja");
        Add("text/plain;kind=build.meson", ".meson");
        Add("text/plain;kind=build.meson-wrap", ".wrap");
        Add("text/plain;kind=build.make", ".build", ".mk", ".am");
        Add("text/plain;kind=build.autoconf", ".ac");
        Add("text/plain;kind=build.m4", ".m4");
        Add("text/plain;kind=script.configure", ".configure");
        Add("text/plain;kind=config.gradle", ".gradlerc");
        Add("text/plain;kind=build.buck", ".buck");
        Add("text/plain;kind=config.buck", ".buckconfig");
        Add("text/plain;kind=config.bazelrc", ".bazelrc");
        Add("text/plain;kind=config.clang-format", ".clang-format", ".clangd", ".ccls");
        Add("text/plain;kind=config.clang-tidy", ".clang-tidy");

        // Schemas, queries, and data
        Add("text/plain;kind=schema.protobuf", ".proto");
        Add("text/plain;kind=schema.avdl", ".avdl");
        Add("application/json;kind=schema.avro", ".avsc");
        Add("application/graphql", ".graphql", ".gql");
        Add("text/plain;kind=query.sql", ".sql", ".psql", ".pgsql", ".mysql", ".dsql", ".hql", ".cql", ".sparql", ".cypher", ".gremlin", ".mongo");
        Add("application/vnd.sqlite3", ".sqlite");
        Add("application/octet-stream;kind=data.db", ".db");
        Add("text/plain;kind=schema.dbml", ".dbml");
        Add("text/plain;kind=schema.prisma", ".prisma");
        Add("text/xml;kind=project.db", ".dbproj");
        Add("application/parquet", ".parquet");
        Add("application/octet-stream;kind=data.orc", ".orc");
        Add("application/octet-stream;kind=data.feather", ".feather");
        Add("application/octet-stream;kind=data.arrow", ".arrow");
        Add("application/avro", ".avro");
        Add("application/msgpack", ".msgpack");
        Add("application/cbor", ".cbor");
        Add("application/vnd.ubjson", ".ubjson");
        Add("application/ion", ".ion");
        Add("text/plain;kind=config.hcl", ".hcl", ".hcl2", ".nomad", ".pkr.hcl");
        Add("text/plain;kind=config.terraform", ".tf", ".tfvars");
        Add("text/plain;kind=config.cue", ".cue");
        Add("text/plain;kind=config.dhall", ".dhall");
        Add("text/plain;kind=config.sample", ".sample");
        Add("text/plain;kind=config.version", ".bazelversion");
        Add("text/plain;kind=policy.rego", ".rego");
        Add("text/plain;kind=policy.generic", ".policy");
        Add("text/plain;kind=config.lock", ".lock");
        Add("text/plain;kind=config.pip", ".pipfile");
        Add("text/plain;kind=config.tox", ".tox");
        Add("text/plain;kind=config.flake8", ".flake8");
        Add("text/plain;kind=config.pylint", ".pylintrc");
        Add("text/plain;kind=config.coverage", ".coverage", ".coveragerc");
        Add("text/plain;kind=config.bandit", ".bandit");
        Add("text/plain;kind=config.codeowners", ".codeowners");
        Add("text/plain;kind=config.include", ".include");
        Add("text/plain;kind=linker.module-definition", ".def", ".exp");
        Add("application/octet-stream;kind=binary.library", ".lib", ".a", ".so", ".dll", ".dylib");
        Add("application/octet-stream;kind=binary.executable", ".exe");
        Add("application/octet-stream;kind=binary.debug-symbols", ".pdb");
        Add("application/xml;kind=docs.xmldoc", ".xml");
        Add("application/xml;kind=config.xml", ".config");
        Add("text/plain", ".txt");
        Add("application/octet-stream;kind=ml.onnx", ".onnx");
        Add("application/zip", ".zip");
        Add("application/zip;kind=package.android-archive", ".aar");

        // Images and design assets
        Add("image/png", ".png");
        Add("image/jpeg", ".jpg", ".jpeg");
        Add("image/gif", ".gif");
        Add("image/bmp", ".bmp");
        Add("image/tiff", ".tiff");
        Add("image/webp", ".webp");
        Add("image/avif", ".avif");
        Add("image/heic", ".heic");
        Add("image/vnd.microsoft.icon", ".ico");
        Add("image/icns", ".icns");
        Add("image/svg+xml", ".svg");
        Add("application/postscript", ".eps", ".ai");
        Add("image/vnd.adobe.photoshop", ".psd");
        Add("application/vnd.adobe.xd+zip", ".xd");
        Add("application/vnd.sketch", ".sketch");
        Add("application/vnd.figma", ".fig");
        Add("model/stl", ".stl");
        Add("model/obj", ".obj");
        Add("application/vnd.autodesk.fbx", ".fbx");
        Add("model/vnd.collada+xml", ".dae");
        Add("application/vnd.blender", ".blend");
        Add("model/gltf-binary", ".glb");
        Add("model/gltf+json", ".gltf");
        Add("model/3ds", ".3ds");
        Add("model/vnd.usdz+zip", ".usdz");
        Add("application/octet-stream;kind=asset.unreal", ".uasset");
        Add("application/zip;kind=package.unity", ".unitypackage");
        Add("application/octet-stream;kind=asset.unity", ".prefab");

        // Audio and video
        Add("audio/wav", ".wav");
        Add("audio/mpeg", ".mp3");
        Add("audio/flac", ".flac");
        Add("audio/ogg", ".ogg");
        Add("audio/mp4", ".m4a");
        Add("audio/aac", ".aac");
        Add("audio/opus", ".opus");
        Add("audio/midi", ".mid");
        Add("audio/aiff", ".aiff");
        Add("audio/mod", ".mod");
        Add("audio/xm", ".xm");
        Add("audio/it", ".it");
        Add("audio/s3m", ".s3m");
        Add("video/mp4", ".mp4");
        Add("video/quicktime", ".mov");
        Add("video/x-matroska", ".mkv");
        Add("video/webm", ".webm");
        Add("video/x-msvideo", ".avi");
        Add("video/x-flv", ".flv");
        Add("video/x-ms-wmv", ".wmv");

        return builder.ToImmutable();

        void Add(string mediaType, params string[] extensions)
        {
            var parsed = SemanticMediaType.Parse(mediaType);
            foreach (var ext in extensions)
            {
                var normalized = ext.StartsWith('.') ? ext : "." + ext;
                builder[normalized] = parsed;
            }
        }
    }

    private static ImmutableDictionary<string, SemanticMediaType> BuildFileNameMap()
    {
        var builder = ImmutableDictionary.CreateBuilder<string, SemanticMediaType>(StringComparer.OrdinalIgnoreCase);

        Add("BUILD", "text/plain;kind=build.bazel");
        Add("WORKSPACE", "text/plain;kind=build.bazel-workspace");
        Add("Makefile", "text/plain;kind=build.make");
        Add("CMakeLists.txt", "text/plain;kind=build.cmake");
        Add("Dockerfile", "text/plain;kind=container.dockerfile");
        Add("Package.swift", "text/plain;kind=code.swift-package-manifest");
        Add("gradlew", "text/plain;kind=script.gradle-wrapper");
        Add("gradlew.bat", "text/plain;kind=script.gradle-wrapper");
        Add("package-lock.json", "application/json;kind=config.npm-lock");
        Add("yarn.lock", "text/plain;kind=config.yarn-lock");
        Add("requirements.txt", "text/plain;kind=config.requirements");
        Add("setup.py", "text/plain;kind=config.python-setup");
        Add("setup.cfg", "text/plain;kind=config.python-setupcfg");
        Add("isort.cfg", "text/plain;kind=config.isort");
        Add("tsc", "text/plain;kind=code.shell");
        Add("tsserver", "text/plain;kind=code.shell");

        return builder.ToImmutable();

        void Add(string fileName, string mediaType)
        {
            builder[fileName] = SemanticMediaType.Parse(mediaType);
        }
    }

    private static (string Suffix, string MediaType)[] BuildCompoundExtensionMap()
    {
        var list = new List<(string, string)>();

        void Add(string suffix, string mediaType) => list.Add((suffix, mediaType));

        Add(".settings.gradle.kts", "text/plain;kind=build.gradle-kotlin");
        Add(".settings.gradle", "text/plain;kind=build.gradle");
        Add(".gradle.kts", "text/plain;kind=build.gradle-kotlin");
        Add(".package.swift", "text/plain;kind=code.swift-package-manifest");
        Add(".tsv.gz", "application/gzip;kind=data.tsv");
        Add(".csv.gz", "application/gzip;kind=data.csv");
        Add(".tfvars.json", "application/json;kind=config.terraform");
        Add(".pulumi.yaml", "application/yaml;kind=config.pulumi");
        Add(".yaml.tpl", "application/yaml;kind=template.yaml");
        Add(".yml.j2", "application/yaml;kind=template.yaml");
        Add(".json.tpl", "application/json;kind=template.json");
        Add(".json.j2", "application/json;kind=template.json");
        Add(".tpl.yml", "application/yaml;kind=template.yaml");
        Add(".pnpm-lock.yaml", "application/yaml;kind=config.pnpm-lock");
        Add(".pnpm-workspace.yaml", "application/yaml;kind=config.pnpm-workspace");
        Add(".composer.lock", "text/plain;kind=config.composer-lock");
        Add(".poetry.lock", "text/plain;kind=config.poetry-lock");
        Add(".pipfile.lock", "text/plain;kind=config.pip-lock");
        Add(".constraints.txt", "text/plain;kind=config.requirements");
        Add(".pyproject.toml", "application/toml;kind=config.python-project");
        Add(".noxfile.py", "text/plain;kind=config.nox");
        Add(".ruff.toml", "application/toml;kind=config.ruff");
        Add(".mypy.ini", "text/plain;kind=config.mypy");
        Add(".pytest.ini", "text/plain;kind=config.pytest");
        Add(".nose.cfg", "text/plain;kind=config.nose");
        Add(".safety-policy.yml", "application/yaml;kind=config.safety");
        Add(".dependabot.yml", "application/yaml;kind=config.dependabot");
        Add(".codeql.yml", "application/yaml;kind=config.codeql");
        Add(".kustomization.yaml", "application/yaml;kind=config.kustomize");
        Add(".skaffold.yaml", "application/yaml;kind=config.skaffold");
        Add(".terraform.lock.hcl", "text/plain;kind=config.terraform-lock");
        Add(".terragrunt.hcl", "text/plain;kind=config.terragrunt");
        Add(".compile_commands.json", "application/json;kind=config.compile-commands");
        Add(".gradle.properties", "text/plain;kind=config.gradle-properties");
        Add(".turbo.json", "application/json;kind=config.turbo");
        Add(".nx.json", "application/json;kind=config.nx");
        Add(".angular.json", "application/json;kind=config.angular");
        Add(".renovate.json", "application/json;kind=config.renovate");
        Add(".code-workspace", "application/json;kind=config.vscode");
        Add(".code-snippets", "application/json;kind=config.vscode-snippets");
        Add(".sublime-project", "application/json;kind=config.sublime");
        Add(".sublime-workspace", "application/json;kind=config.sublime");

        list.Sort((a, b) => b.Item1.Length.CompareTo(a.Item1.Length));
        return [..list];
    }
}