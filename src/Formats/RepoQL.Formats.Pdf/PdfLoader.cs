using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Formats.Pdf.Surface;
using RepoQL.Formats.Pdf.TextExtraction;
using RepoQL.Templating;
using RepoQL.Templating.Filters;
using UglyToad.PdfPig;
using UglyToad.PdfPig.AcroForms;
using UglyToad.PdfPig.Actions;
using UglyToad.PdfPig.Annotations;
using UglyToad.PdfPig.Outline;
using PdfPigAnnotation = UglyToad.PdfPig.Annotations.Annotation;
using RepoAnnotation = RepoQL.Contracts.Models.Annotation;

namespace RepoQL.Formats.Pdf;

/// <summary>
/// Loads and materializes PDF files into graph records.
///
/// Purpose: Extract text and page metadata from PDFs so agents can discover,
/// search, and page-address PDF content.
///
/// Complexity: Medium — binary parsing, layout analysis fallbacks, two extraction
/// modes (single-open vs reopen-per-page), and page-aware byte offset tracking.
/// </summary>
public sealed partial class PdfLoader : IFormatLoader, IFormatMaterializer, IFormatSchemaProvider
{
    internal const string StateMetadataKey = "pdf.state";
    internal const string AnnotationSource = "repoql.formats.pdf";

    internal const long DefaultSingleOpenMaxBytes = 10 * 1024 * 1024;
    internal const int DefaultSingleOpenMaxPages = 100;
    internal const long DefaultMaxFileSizeBytes = 200 * 1024 * 1024;

    private readonly ILogger<PdfLoader> _logger;
    private readonly PdfTextExtractor _textExtractor;
    private readonly long _singleOpenMaxBytes;
    private readonly int _singleOpenMaxPages;
    private readonly long _maxFileSizeBytes;

    private readonly LiquidTemplateRenderer _renderer = new(
        assembly: typeof(PdfLoader).Assembly,
        resourceRoot: "RepoQL.Formats.Pdf.Templates",
        configure: StandardFilters.RegisterAll);

    public PdfLoader(
        PdfTextExtractor? textExtractor = null,
        ILogger<PdfLoader>? logger = null,
        long singleOpenMaxBytes = DefaultSingleOpenMaxBytes,
        int singleOpenMaxPages = DefaultSingleOpenMaxPages,
        long maxFileSizeBytes = DefaultMaxFileSizeBytes)
    {
        _textExtractor = textExtractor ?? new PdfTextExtractor();
        _logger = logger ?? NullLogger<PdfLoader>.Instance;
        _singleOpenMaxBytes = singleOpenMaxBytes;
        _singleOpenMaxPages = singleOpenMaxPages;
        _maxFileSizeBytes = maxFileSizeBytes;
    }

    public bool Supports(SemanticMediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        return PdfMediaTypes.IsPdf(mediaType);
    }

    public Task<bool> CanLoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        if (artifact.File.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            artifact.MediaType = PdfMediaTypes.Base;
            return Task.FromResult(true);
        }

        if (PdfMediaTypes.IsPdf(artifact.MediaType))
            return Task.FromResult(true);

        return Task.FromResult(false);
    }

    public async Task<DocumentModel> LoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.RepoUri is null)
            throw new InvalidOperationException("RepoUri required to load PDF.");

        await using var inputStream = artifact.File.CreateReadStream();
        using var memoryStream = new MemoryStream();
        await inputStream.CopyToAsync(memoryStream, cancellationToken).ConfigureAwait(false);
        var bytes = memoryStream.ToArray();
        var size = (long)bytes.Length;
        var digest = ContentDigest.FromBytes(bytes);

        if (size > _maxFileSizeBytes)
        {
            throw new InvalidDataException(
                $"PDF file '{artifact.File.Name}' is {size} bytes which exceeds the {_maxFileSizeBytes} byte safety limit.");
        }

        var documentId = Guid.NewGuid();
        IReadOnlyList<PageInfo> pages;
        IReadOnlyList<BookmarkInfo> bookmarks;
        IReadOnlyList<FormFieldInfo> formFields = [];
        IReadOnlyList<string> embeddedFileNames = [];
        PdfDocumentMetadata metadata;
        int pageCount;

        try
        {
            using var pdf = PdfDocument.Open(bytes);
            pageCount = pdf.NumberOfPages;
            pages = ExtractPageMetadata(pdf);
            metadata = ExtractMetadata(pdf);
            bookmarks = ExtractBookmarks(pdf, pageCount, artifact.File.Name);

            // Form extraction should not block indexing.
            try
            {
                if (pdf.TryGetForm(out var form))
                {
                    formFields = AcroFormExtensions
                        .GetFields(form)
                        .Select(field =>
                        {
                            var fieldValue = AcroFormExtensions.GetFieldValue(field).Value;
                            return new FormFieldInfo
                            {
                                NodeId = Guid.NewGuid(),
                                SpanId = Guid.NewGuid(),
                                FieldName = string.IsNullOrWhiteSpace(field.Information.PartialName)
                                    ? "unnamed"
                                    : field.Information.PartialName!,
                                FieldType = field.FieldType.ToString(),
                                Value = string.IsNullOrWhiteSpace(fieldValue) ? null : fieldValue,
                                Page = field.PageNumber
                            };
                        })
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                LogFormExtractionFailed(ex, artifact.File.Name);
            }

            // Embedded file detection should not block indexing.
            try
            {
                if (pdf.Advanced.TryGetEmbeddedFiles(out var embeddedFiles))
                {
                    embeddedFileNames = embeddedFiles
                        .Select(file => file.Name)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Select(name => name!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                LogEmbeddedFilesExtractionFailed(ex, artifact.File.Name);
            }
        }
        catch (Exception ex) when (IsPasswordProtected(ex))
        {
            throw new InvalidDataException(
                $"The PDF '{artifact.File.Name}' appears to be password-protected/encrypted and cannot be indexed.",
                ex);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"Failed to open '{artifact.File.Name}' as a valid PDF document.",
                ex);
        }

        var useSingleOpen = size < _singleOpenMaxBytes && pageCount < _singleOpenMaxPages;
        var extractionResults = _textExtractor.Extract(
            bytes,
            pageCount,
            reopenPerPage: !useSingleOpen,
            cancellationToken);
        var pageFeatures = ExtractPageFeatures(
            bytes,
            pageCount,
            reopenPerPage: !useSingleOpen,
            fileName: artifact.File.Name);

        var extractionByPage = extractionResults.ToDictionary(result => result.PageNumber);
        var finalPages = new List<PageInfo>(pages.Count);
        var pageTexts = new List<string>(pages.Count);

        foreach (var page in pages.OrderBy(p => p.Number))
        {
            if (!extractionByPage.TryGetValue(page.Number, out var extraction))
            {
                finalPages.Add(page);
                pageTexts.Add(string.Empty);
                continue;
            }

            finalPages.Add(page with
            {
                HasText = extraction.HasText,
                IsImageOnly = extraction.IsImageOnly
            });
            pageTexts.Add(extraction.Text);
        }

        var textPageCount = finalPages.Count(page => page.HasText);
        var imageOnlyPageCount = finalPages.Count(page => page.IsImageOnly);
        var resolvedMediaType = formFields.Count > 0
            ? PdfMediaTypes.Form
            : textPageCount == 0 && pageCount > 0
                ? PdfMediaTypes.Scan
                : PdfMediaTypes.Document;

        var assembled = PageTextAssembler.Assemble(pageTexts);
        var hasValues = formFields.Any(field => !string.IsNullOrWhiteSpace(field.Value));

        var surface = new PdfDocumentSurface
        {
            DocumentId = documentId,
            Metadata = metadata,
            Pages = finalPages,
            Bookmarks = bookmarks,
            FormFields = formFields,
            PdfAnnotations = pageFeatures.Annotations,
            Links = pageFeatures.Links,
            EmbeddedFileNames = embeddedFileNames,
            PageTexts = pageTexts,
            AssembledText = assembled,
            Stats = new PdfDocumentStats
            {
                PageCount = pageCount,
                TextPageCount = textPageCount,
                ImageOnlyPageCount = imageOnlyPageCount,
                HasBookmarks = bookmarks.Count > 0,
                BookmarkCount = bookmarks.Count,
                HasForm = formFields.Count > 0,
                FormFieldCount = formFields.Count,
                HasValues = hasValues,
                AnnotationCount = pageFeatures.AnnotationCount,
                LinkCount = pageFeatures.LinkCount,
                ImageCount = pageFeatures.ImageCount,
                PagesWithImages = pageFeatures.PagesWithImages,
                EmbeddedFileCount = embeddedFileNames.Count
            }
        };

        var state = new PdfDocumentState
        {
            Surface = surface,
            Digest = digest,
            Size = size,
            MediaType = resolvedMediaType,
            StoreUri = artifact.RepoUri.ToString()
        };

        var metadataBag = new Dictionary<string, object?>
        {
            [StateMetadataKey] = state
        };

        return new DocumentModel(artifact.RepoUri, resolvedMediaType, assembled.Text, metadata: metadataBag);
    }

    public Records Materialize(DocumentModel document)
    {
        if (document.GetMetadataOrDefault<PdfDocumentState>(StateMetadataKey) is not { } state)
            throw new InvalidOperationException("PDF document missing state metadata.");

        var assembled = state.Surface.AssembledText;
        var fileName = GetFileName(document.Uri);

        string? headline = null;
        string? summary = null;
        string? structure = null;
        var tokenCount = !string.IsNullOrWhiteSpace(assembled.Text)
            ? TokenEstimator.EstimateTokensSafe(assembled.Text)
            : null;

        try
        {
            var model = BuildExploreModel(state, fileName, tokenCount ?? 0);
            summary = _renderer.RenderAsync("explore/summary", model).GetAwaiter().GetResult();
            structure = _renderer.RenderAsync("explore/structure", model).GetAwaiter().GetResult();

            if (tokenCount is null)
            {
                var textForTokens = string.Join(
                    "\n",
                    new[] { summary, structure }.Where(value => !string.IsNullOrWhiteSpace(value)));
                tokenCount = TokenEstimator.EstimateTokensSafe(textForTokens);
            }

            model["token_count"] = tokenCount ?? 0;
            headline = _renderer.RenderAsync("explore/headline", model).GetAwaiter().GetResult();
        }
        catch
        {
            // X-ray templating is best-effort.
        }

        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            Digest = state.Digest,
            Size = state.Size,
            MediaType = state.MediaType,
            Text = assembled.Text,
            StoreUri = state.StoreUri,
            Headline = headline,
            Summary = summary,
            Structure = structure,
            TokenCount = tokenCount
        };

        var pageByteOffsetsJson = new JsonArray(
            assembled.PageByteOffsets
                .Select(offset => (JsonNode)new JsonArray(offset.Start, offset.End))
                .ToArray());

        var pageTokenCountsJson = new JsonArray(
            assembled.PageTokenCounts
                .Select(count => (JsonNode)JsonValue.Create(count)!)
                .ToArray());
        var embeddedFileNamesJson = new JsonArray(
            state.Surface.EmbeddedFileNames
                .Select(name => (JsonNode)JsonValue.Create(name)!)
                .ToArray());

        var now = DateTimeOffset.UtcNow;
        var documentNode = new Node
        {
            Id = state.Surface.DocumentId,
            Kind = PdfNodeKinds.Document,
            Uri = document.Uri,
            ArtifactId = artifact.Id,
            Headline = headline,
            Props = new JsonObject
            {
                ["media_type"] = state.MediaType.ToString(),
                ["byte_size"] = state.Size,
                ["title"] = state.Surface.Metadata.Title,
                ["author"] = state.Surface.Metadata.Author,
                ["subject"] = state.Surface.Metadata.Subject,
                ["keywords"] = state.Surface.Metadata.Keywords,
                ["creator"] = state.Surface.Metadata.Creator,
                ["producer"] = state.Surface.Metadata.Producer,
                ["created"] = state.Surface.Metadata.Created?.ToString("o"),
                ["modified"] = state.Surface.Metadata.Modified?.ToString("o"),
                ["version"] = state.Surface.Metadata.Version,
                ["page_count"] = state.Surface.Stats.PageCount,
                ["text_page_count"] = state.Surface.Stats.TextPageCount,
                ["image_only_page_count"] = state.Surface.Stats.ImageOnlyPageCount,
                ["has_bookmarks"] = state.Surface.Stats.HasBookmarks,
                ["bookmark_count"] = state.Surface.Stats.BookmarkCount,
                ["has_form"] = state.Surface.Stats.HasForm,
                ["form_field_count"] = state.Surface.Stats.FormFieldCount,
                ["has_values"] = state.Surface.Stats.HasValues,
                ["annotation_count"] = state.Surface.Stats.AnnotationCount,
                ["link_count"] = state.Surface.Stats.LinkCount,
                ["image_count"] = state.Surface.Stats.ImageCount,
                ["pages_with_images"] = state.Surface.Stats.PagesWithImages,
                ["embedded_file_count"] = state.Surface.Stats.EmbeddedFileCount,
                ["embedded_file_names"] = embeddedFileNamesJson,
                ["page_byte_offsets"] = pageByteOffsetsJson,
                ["page_token_counts"] = pageTokenCountsJson
            },
            CreatedAt = now,
            UpdatedAt = now
        };

        var nodes = new List<Node> { documentNode };
        var spans = new List<Span>();
        var edges = new List<Edge>();
        var annotations = new List<RepoAnnotation>();

        var bookmarkOrdinal = 0;
        var bookmarkRanges = CalculateBookmarkRanges(state.Surface.Bookmarks, state.Surface.Stats.PageCount);
        foreach (var (bookmark, endPage) in bookmarkRanges)
        {
            var startPage = Math.Clamp(bookmark.TargetPage, 1, Math.Max(1, state.Surface.Stats.PageCount));
            var clampedEndPage = Math.Clamp(endPage, startPage, Math.Max(startPage, state.Surface.Stats.PageCount));
            var startOffset = assembled.PageByteOffsets.ElementAtOrDefault(startPage - 1);
            var endOffset = assembled.PageByteOffsets.ElementAtOrDefault(clampedEndPage - 1);

            spans.Add(new Span
            {
                Id = bookmark.SpanId,
                DocumentId = state.Surface.DocumentId,
                StartLine = startPage,
                EndLine = clampedEndPage,
                StartByte = startOffset.Start,
                EndByte = endOffset.End
            });

            nodes.Add(new Node
            {
                Id = bookmark.NodeId,
                Kind = PdfNodeKinds.Bookmark,
                Uri = RepoUri.FromSymbol(document.Uri.Container, bookmark.Title, startPage, clampedEndPage),
                SpanId = bookmark.SpanId,
                Headline = bookmark.Title,
                Props = new JsonObject
                {
                    ["title"] = bookmark.Title,
                    ["level"] = bookmark.Level,
                    ["target_page"] = startPage
                },
                CreatedAt = now,
                UpdatedAt = now
            });

            edges.Add(CreateHasPart(state.Surface.DocumentId, bookmark.NodeId, state.Surface.DocumentId, bookmarkOrdinal++, now));
        }

        var formFieldOrdinal = bookmarkOrdinal;
        foreach (var formField in state.Surface.FormFields)
        {
            var page = formField.Page.HasValue
                ? Math.Clamp(formField.Page.Value, 1, Math.Max(1, state.Surface.Stats.PageCount))
                : 1;

            spans.Add(new Span
            {
                Id = formField.SpanId,
                DocumentId = state.Surface.DocumentId,
                StartLine = page,
                EndLine = page
            });

            nodes.Add(new Node
            {
                Id = formField.NodeId,
                Kind = PdfNodeKinds.FormField,
                SpanId = formField.SpanId,
                Props = new JsonObject
                {
                    ["field_name"] = formField.FieldName,
                    ["field_type"] = formField.FieldType,
                    ["value"] = formField.Value,
                    ["page"] = page
                },
                Headline = string.IsNullOrWhiteSpace(formField.FieldName)
                    ? "Form Field"
                    : $"Form Field · {formField.FieldName}",
                CreatedAt = now,
                UpdatedAt = now
            });

            edges.Add(CreateHasPart(state.Surface.DocumentId, formField.NodeId, state.Surface.DocumentId, formFieldOrdinal++, now));
        }

        foreach (var link in state.Surface.Links)
        {
            if (string.IsNullOrWhiteSpace(link.Url))
                continue;

            try
            {
                edges.Add(new Edge
                {
                    Id = Guid.NewGuid(),
                    SrcId = documentNode.Id,
                    Type = "REFERS_TO",
                    DstUri = RepoUri.Parse(link.Url),
                    ScopeDocumentId = documentNode.Id,
                    CreatedAt = now
                });
            }
            catch
            {
                // Skip malformed URLs.
            }
        }

        foreach (var pdfAnnotation in state.Surface.PdfAnnotations)
        {
            var page = Math.Clamp(pdfAnnotation.Page, 1, Math.Max(1, state.Surface.Stats.PageCount));
            var spanId = Guid.NewGuid();

            spans.Add(new Span
            {
                Id = spanId,
                DocumentId = documentNode.Id,
                StartLine = page,
                EndLine = page
            });

            annotations.Add(new RepoAnnotation
            {
                Id = Guid.NewGuid(),
                Kind = pdfAnnotation.AnnotationType,
                Severity = "info",
                Source = AnnotationSource,
                Message = string.IsNullOrWhiteSpace(pdfAnnotation.Content)
                    ? $"{pdfAnnotation.AnnotationType} on page {page}"
                    : pdfAnnotation.Content!,
                Data = new JsonObject
                {
                    ["annotation_type"] = pdfAnnotation.AnnotationType,
                    ["page"] = page,
                    ["author"] = pdfAnnotation.Author,
                    ["date"] = pdfAnnotation.Date
                },
                ScopeDocumentId = documentNode.Id,
                TargetSpanId = spanId,
                CreatedAt = now
            });
        }

        return new Records
        {
            Artifacts = [artifact],
            Nodes = [.. nodes],
            Spans = [.. spans],
            Edges = [.. edges],
            Annotations = [.. annotations],
            AnnotationSources = annotations.Count > 0 ? [AnnotationSource] : []
        };
    }

    private static IReadOnlyList<PageInfo> ExtractPageMetadata(PdfDocument document)
    {
        var pages = new List<PageInfo>(document.NumberOfPages);
        for (var pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
        {
            var page = document.GetPage(pageNumber);
            var letters = page.Letters;
            var hasLetters = letters.Count > 0;

            pages.Add(new PageInfo
            {
                Number = pageNumber,
                Width = (double)page.Width,
                Height = (double)page.Height,
                Rotation = page.Rotation.Value,
                HasText = false,
                IsImageOnly = !hasLetters
            });
        }

        return pages;
    }

    private PageFeatureExtraction ExtractPageFeatures(
        byte[] bytes,
        int pageCount,
        bool reopenPerPage,
        string fileName)
    {
        var annotations = new List<PdfAnnotationInfo>();
        var links = new List<PdfLinkInfo>();
        var imageCountsByPage = new Dictionary<int, int>();
        var annotationCount = 0;

        if (reopenPerPage)
        {
            for (var pageNumber = 1; pageNumber <= pageCount; pageNumber++)
            {
                try
                {
                    using var pdf = PdfDocument.Open(bytes);
                    var page = pdf.GetPage(pageNumber);
                    ExtractPageFeaturesFromPage(
                        page,
                        pageNumber,
                        fileName,
                        annotations,
                        links,
                        imageCountsByPage,
                        ref annotationCount);
                }
                catch (Exception ex)
                {
                    LogPageFeatureExtractionFailed(ex, fileName, pageNumber);
                }
            }
        }
        else
        {
            try
            {
                using var pdf = PdfDocument.Open(bytes);
                for (var pageNumber = 1; pageNumber <= pageCount; pageNumber++)
                {
                    try
                    {
                        var page = pdf.GetPage(pageNumber);
                        ExtractPageFeaturesFromPage(
                            page,
                            pageNumber,
                            fileName,
                            annotations,
                            links,
                            imageCountsByPage,
                            ref annotationCount);
                    }
                    catch (Exception ex)
                    {
                        LogPageFeatureExtractionFailed(ex, fileName, pageNumber);
                    }
                }
            }
            catch (Exception ex)
            {
                LogPageFeatureOpenFailed(ex, fileName);
            }
        }

        var imageCount = imageCountsByPage.Values.Sum();
        var pagesWithImages = imageCountsByPage.Count(static pair => pair.Value > 0);

        return new PageFeatureExtraction(
            Annotations: annotations,
            Links: links,
            AnnotationCount: annotationCount,
            LinkCount: links.Count,
            ImageCount: imageCount,
            PagesWithImages: pagesWithImages);
    }

    private void ExtractPageFeaturesFromPage(
        UglyToad.PdfPig.Content.Page page,
        int pageNumber,
        string fileName,
        List<PdfAnnotationInfo> annotations,
        List<PdfLinkInfo> links,
        IDictionary<int, int> imageCountsByPage,
        ref int annotationCount)
    {
        try
        {
            var imageCount = page.GetImages().Count();
            imageCountsByPage[pageNumber] = imageCount;
        }
        catch (Exception ex)
        {
            LogImageDetectionFailed(ex, fileName, pageNumber);
        }

        try
        {
            foreach (var annotation in page.GetAnnotations())
            {
                annotationCount++;

                switch (annotation.Type)
                {
                    case AnnotationType.Text:
                    case AnnotationType.FreeText:
                        annotations.Add(new PdfAnnotationInfo
                        {
                            AnnotationType = "comment",
                            Page = pageNumber,
                            Content = NullIfEmpty(annotation.Content),
                            Author = NullIfEmpty(annotation.Name),
                            Date = NullIfEmpty(annotation.ModifiedDate)
                        });
                        break;
                    case AnnotationType.Highlight:
                        annotations.Add(new PdfAnnotationInfo
                        {
                            AnnotationType = "highlight",
                            Page = pageNumber,
                            Content = NullIfEmpty(annotation.Content),
                            Author = NullIfEmpty(annotation.Name),
                            Date = NullIfEmpty(annotation.ModifiedDate)
                        });
                        break;
                    case AnnotationType.Stamp:
                        annotations.Add(new PdfAnnotationInfo
                        {
                            AnnotationType = "stamp",
                            Page = pageNumber,
                            Content = NullIfEmpty(annotation.Content ?? annotation.Name),
                            Author = NullIfEmpty(annotation.Name),
                            Date = NullIfEmpty(annotation.ModifiedDate)
                        });
                        break;
                    case AnnotationType.Link:
                        if (TryGetLinkUrl(annotation) is { Length: > 0 } url)
                        {
                            links.Add(new PdfLinkInfo
                            {
                                Page = pageNumber,
                                Url = url
                            });
                        }
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            LogAnnotationExtractionFailed(ex, fileName, pageNumber);
        }
    }

    private static string? TryGetLinkUrl(PdfPigAnnotation annotation)
    {
        if (annotation.Action is not UriAction uriAction)
            return null;

        return NullIfEmpty(uriAction.Uri);
    }

    private static PdfDocumentMetadata ExtractMetadata(PdfDocument document)
    {
        var info = document.Information;
        return new PdfDocumentMetadata
        {
            Title = NullIfEmpty(info.Title),
            Author = NullIfEmpty(info.Author),
            Subject = NullIfEmpty(info.Subject),
            Keywords = NullIfEmpty(info.Keywords),
            Creator = NullIfEmpty(info.Creator),
            Producer = NullIfEmpty(info.Producer),
            Created = TryParsePdfDate(info.CreationDate),
            Modified = TryParsePdfDate(info.ModifiedDate),
            Version = document.Version.ToString()
        };
    }

    /// <summary>
    /// Parses a PDF date string (e.g. "D:20250315120000+00'00'" or ISO 8601).
    /// Returns null if the string is empty or unparseable.
    /// </summary>
    private static DateTimeOffset? TryParsePdfDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        // Strip the "D:" prefix used in PDF date format
        var text = value.StartsWith("D:", StringComparison.Ordinal) ? value[2..] : value;

        if (DateTimeOffset.TryParse(text, out var result))
            return result;

        return null;
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool IsPasswordProtected(Exception ex)
    {
        var message = ex.Message;
        return message.Contains("password", StringComparison.OrdinalIgnoreCase)
               || message.Contains("encrypted", StringComparison.OrdinalIgnoreCase);
    }

    private IReadOnlyList<BookmarkInfo> ExtractBookmarks(PdfDocument document, int pageCount, string fileName)
    {
        try
        {
            if (!document.TryGetBookmarks(out var bookmarks, allowContainerNode: true) || bookmarks.Roots.Count == 0)
                return [];

            var flattened = new List<BookmarkInfo>();
            foreach (var root in bookmarks.Roots)
            {
                if (!TryBuildBookmarkTree(root, pageCount, currentLevel: 1, out var bookmark))
                    continue;

                flattened.Add(bookmark);
                flattened.AddRange(FlattenBookmarkChildren(bookmark.Children));
            }

            return flattened;
        }
        catch (Exception ex)
        {
            LogBookmarkExtractionFailed(ex, fileName);
            return [];
        }
    }

    private bool TryBuildBookmarkTree(BookmarkNode bookmarkNode, int pageCount, int currentLevel, out BookmarkInfo bookmark)
    {
        if (!TryResolveTargetPage(bookmarkNode, pageCount, out var targetPage))
        {
            LogBookmarkSkipped(bookmarkNode.Title);
            bookmark = null!;
            return false;
        }

        var children = new List<BookmarkInfo>();
        foreach (var child in bookmarkNode.Children)
        {
            if (TryBuildBookmarkTree(child, pageCount, currentLevel + 1, out var childBookmark))
                children.Add(childBookmark);
        }

        bookmark = new BookmarkInfo
        {
            NodeId = Guid.NewGuid(),
            SpanId = Guid.NewGuid(),
            Title = string.IsNullOrWhiteSpace(bookmarkNode.Title) ? $"Bookmark p{targetPage}" : bookmarkNode.Title.Trim(),
            Level = Math.Max(1, currentLevel),
            TargetPage = targetPage,
            Children = children
        };

        return true;
    }

    private static IEnumerable<BookmarkInfo> FlattenBookmarkChildren(IReadOnlyList<BookmarkInfo> children)
    {
        foreach (var child in children)
        {
            yield return child;
            foreach (var nested in FlattenBookmarkChildren(child.Children))
                yield return nested;
        }
    }

    private static bool TryResolveTargetPage(BookmarkNode bookmarkNode, int pageCount, out int targetPage)
    {
        if (bookmarkNode is DocumentBookmarkNode documentBookmark)
        {
            targetPage = Math.Clamp(documentBookmark.PageNumber, 1, Math.Max(1, pageCount));
            return true;
        }

        foreach (var child in bookmarkNode.Children)
        {
            if (TryResolveTargetPage(child, pageCount, out targetPage))
                return true;
        }

        targetPage = 0;
        return false;
    }

    private static IReadOnlyList<(BookmarkInfo Bookmark, int EndPage)> CalculateBookmarkRanges(
        IReadOnlyList<BookmarkInfo> bookmarks,
        int pageCount)
    {
        if (bookmarks.Count == 0)
            return [];

        var ranges = new List<(BookmarkInfo Bookmark, int EndPage)>(bookmarks.Count);
        var maxPage = Math.Max(1, pageCount);

        for (var i = 0; i < bookmarks.Count; i++)
        {
            var current = bookmarks[i];
            var startPage = Math.Clamp(current.TargetPage, 1, maxPage);
            var endPage = maxPage;

            for (var j = i + 1; j < bookmarks.Count; j++)
            {
                if (bookmarks[j].Level <= current.Level)
                {
                    endPage = Math.Clamp(bookmarks[j].TargetPage - 1, startPage, maxPage);
                    break;
                }
            }

            ranges.Add((current, endPage));
        }

        return ranges;
    }

    private static Dictionary<string, object?> BuildExploreModel(PdfDocumentState state, string fileName, int tokenCount)
    {
        var pageCount = state.Surface.Stats.PageCount;
        var hasText = state.Surface.Stats.TextPageCount > 0;
        var title = string.IsNullOrWhiteSpace(state.Surface.Metadata.Title)
            ? fileName
            : state.Surface.Metadata.Title;
        var topBookmarks = state.Surface.Bookmarks
            .Where(bookmark => bookmark.Level == 1)
            .Select(bookmark => bookmark.Title)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();

        var bookmarkRanges = CalculateBookmarkRanges(state.Surface.Bookmarks, state.Surface.Stats.PageCount);
        var bookmarkLines = bookmarkRanges
            .Select(range =>
            {
                var indent = new string(' ', Math.Max(2, range.Bookmark.Level * 2));
                var pageLabel = range.EndPage > range.Bookmark.TargetPage
                    ? $"p{range.Bookmark.TargetPage}-{range.EndPage}"
                    : $"p{range.Bookmark.TargetPage}";
                return $"{indent}- {range.Bookmark.Title} ({pageLabel})";
            })
            .ToList();

        return new Dictionary<string, object?>
        {
            ["file_name"] = fileName,
            ["display_title"] = title,
            ["kind"] = state.MediaType.Kind ?? "pdf.document",
            ["size_bytes"] = state.Size,
            ["token_count"] = tokenCount,
            ["page_count"] = pageCount,
            ["text_page_count"] = state.Surface.Stats.TextPageCount,
            ["image_only_page_count"] = state.Surface.Stats.ImageOnlyPageCount,
            ["has_bookmarks"] = state.Surface.Stats.HasBookmarks,
            ["bookmark_count"] = state.Surface.Stats.BookmarkCount,
            ["has_form"] = state.Surface.Stats.HasForm,
            ["form_field_count"] = state.Surface.Stats.FormFieldCount,
            ["has_values"] = state.Surface.Stats.HasValues,
            ["annotation_count"] = state.Surface.Stats.AnnotationCount,
            ["link_count"] = state.Surface.Stats.LinkCount,
            ["image_count"] = state.Surface.Stats.ImageCount,
            ["pages_with_images"] = state.Surface.Stats.PagesWithImages,
            ["embedded_file_count"] = state.Surface.Stats.EmbeddedFileCount,
            ["top_bookmarks"] = topBookmarks,
            ["bookmark_lines"] = bookmarkLines,
            ["is_scan"] = !hasText && pageCount > 0,
            ["author"] = state.Surface.Metadata.Author,
            ["producer"] = state.Surface.Metadata.Producer,
            ["version"] = state.Surface.Metadata.Version
        };
    }

    public IEnumerable<FormatSqlScript> GetSchemaScripts()
    {
        yield return new FormatSqlScript("pdf_views", PdfViewsSql.Value);
    }

    private static readonly Lazy<string> PdfViewsSql = new(() =>
        ReadEmbeddedResource("RepoQL.Formats.Pdf.Schema.pdf_views.sql"));

    private static string ReadEmbeddedResource(string resourceName)
    {
        using var stream = typeof(PdfLoader).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static Edge CreateHasPart(Guid sourceId, Guid destinationId, Guid scopeDocumentId, int ordinal, DateTimeOffset timestamp)
        => new()
        {
            Id = Guid.NewGuid(),
            SrcId = sourceId,
            DstId = destinationId,
            Type = "HAS_PART",
            IsComposition = true,
            Ordinal = ordinal,
            ScopeDocumentId = scopeDocumentId,
            CreatedAt = timestamp
        };

    private static string GetFileName(RepoUri uri)
    {
        try
        {
            if (uri.IsFile)
            {
                var localPath = uri.LocalPath;
                if (!string.IsNullOrEmpty(localPath))
                    return Path.GetFileName(localPath);
            }
        }
        catch
        {
            // Fall through to URI parsing.
        }

        var absolutePath = Uri.UnescapeDataString(uri.AbsolutePath);
        var lastSlash = absolutePath.LastIndexOf('/');
        var segment = lastSlash >= 0
            ? absolutePath[(lastSlash + 1)..]
            : absolutePath;
        return string.IsNullOrEmpty(segment) ? uri.AbsoluteUri : segment;
    }

    private sealed record PageFeatureExtraction(
        IReadOnlyList<PdfAnnotationInfo> Annotations,
        IReadOnlyList<PdfLinkInfo> Links,
        int AnnotationCount,
        int LinkCount,
        int ImageCount,
        int PagesWithImages);

    [LoggerMessage(LogLevel.Warning, "PDF metadata extraction failed for {Name}.")]
    partial void LogMetadataExtractionFailed(Exception ex, string name);

    [LoggerMessage(LogLevel.Warning, "PDF bookmark extraction failed for {Name}.")]
    partial void LogBookmarkExtractionFailed(Exception ex, string name);

    [LoggerMessage(LogLevel.Debug, "Skipping bookmark '{Title}' due to missing/invalid target page.")]
    partial void LogBookmarkSkipped(string title);

    [LoggerMessage(LogLevel.Warning, "PDF form extraction failed for {Name}.")]
    partial void LogFormExtractionFailed(Exception ex, string name);

    [LoggerMessage(LogLevel.Warning, "PDF embedded file extraction failed for {Name}.")]
    partial void LogEmbeddedFilesExtractionFailed(Exception ex, string name);

    [LoggerMessage(LogLevel.Warning, "PDF page feature extraction failed for {Name} page {Page}.")]
    partial void LogPageFeatureExtractionFailed(Exception ex, string name, int page);

    [LoggerMessage(LogLevel.Warning, "PDF page feature extraction failed to open document for {Name}.")]
    partial void LogPageFeatureOpenFailed(Exception ex, string name);

    [LoggerMessage(LogLevel.Warning, "PDF annotation extraction failed for {Name} page {Page}.")]
    partial void LogAnnotationExtractionFailed(Exception ex, string name, int page);

    [LoggerMessage(LogLevel.Warning, "PDF image detection failed for {Name} page {Page}.")]
    partial void LogImageDetectionFailed(Exception ex, string name, int page);
}
