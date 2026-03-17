using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.FileSystem;
using RepoQL.FileSystem.Abstractions;
using RepoQL.Indexing.FileSystems;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Analysis;
using RepoQL.Indexing.Indexing.Pipelines.Classification;
using RepoQL.Indexing.Indexing.Pipelines.Parsing;

namespace RepoQL.Indexing.Hosting;

public sealed record DocumentPreviewRequest(RepoUri Uri, byte[]? Content = null, string? FileName = null, string? MediaTypeHint = null);

public sealed record DocumentPreviewStage(string Stage, TimeSpan Duration, PipelineResult Status, string? Error);

public sealed record DocumentPreviewResult(
    bool Success,
    string? Error,
    string? MediaType,
    string? DigestHex,
    Records? Records,
    IReadOnlyList<DocumentPreviewStage> Stages);

/// <summary>
/// Executes the hot-path pipeline for a single artifact without writing to the database.
/// </summary>
public sealed class DocumentPreviewService(
    CompositeFileSystem fileSystem,
    ClassificationPipeline classification,
    ParsingPipeline parsing,
    SingleFileAnalysisPipeline singleFile,
    ILogger<DocumentPreviewService>? logger = null)
{
    private readonly CompositeFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly ClassificationPipeline _classification = classification ?? throw new ArgumentNullException(nameof(classification));
    private readonly ParsingPipeline _parsing = parsing ?? throw new ArgumentNullException(nameof(parsing));
    private readonly SingleFileAnalysisPipeline _singleFile = singleFile ?? throw new ArgumentNullException(nameof(singleFile));
    private readonly ILogger<DocumentPreviewService> _logger = logger ?? NullLogger<DocumentPreviewService>.Instance;

    public async Task<DocumentPreviewResult> PreviewAsync(DocumentPreviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stages = new List<DocumentPreviewStage>(capacity: 3);
        try
        {
            var item = await CreateIndexItemAsync(request, cancellationToken).ConfigureAwait(false);
            var mediaTypeHint = TryParseMediaType(request.MediaTypeHint);
            if (mediaTypeHint is not null)
                item.MediaType = mediaTypeHint;

            var classificationStage = await RunStageAsync(
                "Classification",
                item,
                ct => _classification.ProcessItemAsync(item, ct),
                cancellationToken).ConfigureAwait(false);
            stages.Add(classificationStage);
            if (!StageSuccessful(classificationStage))
            {
                return Failure(classificationStage.Error ?? "Classification failed.", stages);
            }

            item.MediaType ??= item.RawArtifact.ProvisionalMediaType.Value;
            if (item.MediaType is null)
            {
                return Failure("Media type could not be determined.", stages);
            }

            var parsingStage = await RunStageAsync(
                "Parsing",
                item,
                ct => _parsing.ProcessItemAsync(item, ct),
                cancellationToken).ConfigureAwait(false);
            stages.Add(parsingStage);
            if (!StageSuccessful(parsingStage))
            {
                return Failure(parsingStage.Error ?? "Parsing failed.", stages);
            }

            var analysisStage = await RunStageAsync(
                "SingleFileAnalysis",
                item,
                ct => _singleFile.ProcessItemAsync(item, ct),
                cancellationToken).ConfigureAwait(false);
            stages.Add(analysisStage);
            if (!StageSuccessful(analysisStage))
            {
                return Failure(analysisStage.Error ?? "Single-file analysis failed.", stages);
            }

            var digestHex = await item.RawArtifact.Digest.WithCancellation(cancellationToken).ConfigureAwait(false);
            item.DigestHex = digestHex;
            var combinedRecords = BuildPreviewRecords(item);

            return new DocumentPreviewResult(
                true,
                null,
                item.MediaType?.ToString(),
                digestHex,
                combinedRecords,
                stages);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Preview failed for {Uri}", request.Uri);
            return Failure(ex.Message, stages);
        }
    }

    private static bool StageSuccessful(DocumentPreviewStage stage)
        => stage.Status == PipelineResult.Success;

    private DocumentPreviewResult Failure(string? message, IReadOnlyList<DocumentPreviewStage> stages)
        => new(false, message, null, null, null, stages);

    private async Task<IndexItem> CreateIndexItemAsync(DocumentPreviewRequest request, CancellationToken cancellationToken)
    {
        RawArtifact artifact;
        if (request.Content is { Length: > 0 })
        {
            artifact = CreateUploadedArtifact(request);
        }
        else
        {
            var store = _fileSystem.Resolve(request.Uri);
            var file = store.GetFile(request.Uri);
            if (!file.Exists)
                throw new FileNotFoundException($"{request.Uri} was not found in any mounted file system.");
            artifact = new RawArtifact(file, store);
        }

        // Ensure we force length evaluation for uploaded content
        _ = artifact.Length;
        return new IndexItem(artifact, IndexItemOptions.Always);
    }

    private RawArtifact CreateUploadedArtifact(DocumentPreviewRequest request)
    {
        var fileName = string.IsNullOrWhiteSpace(request.FileName)
            ? Path.GetFileName(request.Uri.AbsolutePath)
            : request.FileName!;
        var file = new UploadedFileInfo(fileName, request.Content!, DateTimeOffset.UtcNow);
        var fs = new SingleFileVirtualFileSystem(request.Uri, file);
        return new RawArtifact(file, fs);
    }

    private static SemanticMediaType? TryParseMediaType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return SemanticMediaType.TryParse(value, out var parsed) ? parsed : null;
    }

    private static Records? BuildPreviewRecords(IndexItem item)
    {
        if (item.Records is null)
            return null;

        var existingAnnotations = item.Records.Annotations ?? Array.Empty<Annotation>();
        var analyzerAnnotations = item.AnnotationsList.Count > 0
            ? item.AnnotationsList.ToArray()
            : Array.Empty<Annotation>();

        var combinedAnnotations = existingAnnotations.Length == 0
            ? analyzerAnnotations
            : analyzerAnnotations.Length == 0
                ? existingAnnotations
                : [.. existingAnnotations, .. analyzerAnnotations];

        return new Records
        {
            Artifacts = item.Records.Artifacts,
            Nodes = item.Records.Nodes,
            Spans = item.Records.Spans,
            Edges = item.Records.Edges,
            Annotations = combinedAnnotations,
            AnnotationSources = item.Records.AnnotationSources
        };
    }

    private async Task<DocumentPreviewStage> RunStageAsync(
        string name,
        IndexItem item,
        Func<CancellationToken, Task<PipelineResult>> runner,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        PipelineResult status;
        string? error = null;
        try
        {
            item.ClearFailureDetail();
            status = await runner(cancellationToken).ConfigureAwait(false);
            if (status == PipelineResult.Error && !string.IsNullOrEmpty(item.FailureDetail))
                error = item.FailureDetail;
        }
        catch (Exception ex)
        {
            status = PipelineResult.Error;
            error = ex.Message;
        }
        finally
        {
            stopwatch.Stop();
        }

        return new DocumentPreviewStage(name, stopwatch.Elapsed, status, error);
    }

    private sealed class SingleFileVirtualFileSystem : IVirtualFileSystem
    {
        private readonly RepoUri _uri;
        private readonly IFileInfo _file;

        public SingleFileVirtualFileSystem(RepoUri uri, IFileInfo file)
        {
            _uri = uri ?? throw new ArgumentNullException(nameof(uri));
            _file = file ?? throw new ArgumentNullException(nameof(file));
        }

        public string Scheme => _uri.Scheme;

        public async IAsyncEnumerable<IFileInfo> EnumerateAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return _file;
            await Task.CompletedTask;
        }

        public IFileInfo GetFile(RepoUri uri)
        {
            return string.Equals(uri.AbsoluteUri, _uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase)
                ? _file
                : new NotFoundFileInfo(uri.AbsoluteUri);
        }

        public RepoUri GetUri(IFileInfo file) => _uri;

        public IFileSystemWatcher Watch() => new NoOpWatcher();
    }

    private sealed class NoOpWatcher : FileSystemWatcherBase
    {
        protected override Task OnStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        protected override Task OnStopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class UploadedFileInfo : IFileInfo
    {
        private readonly byte[] _content;

        public UploadedFileInfo(string name, byte[] content, DateTimeOffset lastModified)
        {
            Name = string.IsNullOrWhiteSpace(name) ? "preview" : name;
            _content = content ?? Array.Empty<byte>();
            LastModified = lastModified;
        }

        public bool Exists => true;
        public long Length => _content.LongLength;
        public string? PhysicalPath => null;
        public string Name { get; }
        public DateTimeOffset LastModified { get; }
        public bool IsDirectory => false;
        public Stream CreateReadStream() => new MemoryStream(_content, writable: false);
    }
}
