using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Formats.Docx.Surface;
using RepoQL.Templating;
using RepoQL.Templating.Filters;
using A = DocumentFormat.OpenXml.Drawing;
using V = DocumentFormat.OpenXml.Vml;

namespace RepoQL.Formats.Docx;

/// <summary>
/// Loads and materializes Word OpenXML documents into graph records.
///
/// Purpose: Extract text and heading structure from .docx/.docm/.dotx files so
/// agents can discover and navigate sections by heading.
///
/// Complexity: Handles OpenXML paragraph traversal, heading style inheritance,
/// tracked changes final-state extraction, and graph materialization.
/// </summary>
public sealed partial class DocxLoader : IFormatLoader, IFormatMaterializer
{
    internal const string StateMetadataKey = "docx.state";
    private const string DocxAnnotationSource = "repoql.formats.docx";

    private static readonly Regex HeadingStyleRegex = new(
        "^Heading(?<level>[1-9])$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ILogger<DocxLoader> _logger;
    private readonly LiquidTemplateRenderer _renderer = new(
        assembly: typeof(DocxLoader).Assembly,
        resourceRoot: "RepoQL.Formats.Docx.Templates",
        configure: StandardFilters.RegisterAll);

    public DocxLoader(ILogger<DocxLoader>? logger = null)
    {
        _logger = logger ?? NullLogger<DocxLoader>.Instance;
    }

    public bool Supports(SemanticMediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);

        if (DocxMediaTypes.IsSupportedKind(mediaType.Kind))
            return true;

        return DocxMediaTypes.TryResolveBySubtype(mediaType.Subtype, out _);
    }

    public Task<bool> CanLoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var extension = Path.GetExtension(artifact.File.Name);
        if (DocxMediaTypes.TryResolveByExtension(extension, out var extensionType))
        {
            artifact.MediaType = extensionType;
            return Task.FromResult(true);
        }

        if (artifact.MediaType is null)
            return Task.FromResult(false);

        if (DocxMediaTypes.IsSupportedKind(artifact.MediaType.Kind))
            return Task.FromResult(true);

        if (DocxMediaTypes.TryResolveBySubtype(artifact.MediaType.Subtype, out var subtypeType))
        {
            artifact.MediaType = artifact.MediaType.WithKind(subtypeType!.Kind);
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public async Task<DocumentModel> LoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.RepoUri is null)
            throw new InvalidOperationException("RepoUri required to load DOCX.");

        var mediaType = artifact.MediaType
                        ?? (DocxMediaTypes.TryResolveByExtension(Path.GetExtension(artifact.File.Name), out var resolved)
                            ? resolved!
                            : DocxMediaTypes.Document);

        await using var inputStream = artifact.File.CreateReadStream();
        using var memoryStream = new MemoryStream();
        await inputStream.CopyToAsync(memoryStream, cancellationToken).ConfigureAwait(false);
        var bytes = memoryStream.ToArray();
        var digest = ContentDigest.FromBytes(bytes);
        var size = bytes.Length;

        if (IsEncryptedOfficeContainer(bytes))
        {
            throw new InvalidDataException(
                "The Word document appears to be password-protected/encrypted and cannot be indexed.");
        }

        DocumentSurface surface;
        try
        {
            memoryStream.Position = 0;
            using var document = WordprocessingDocument.Open(memoryStream, false);
            surface = ParseDocument(document);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"Failed to open '{artifact.File.Name}' as a valid OpenXML Word document.",
                ex);
        }

        var state = new DocxDocumentState
        {
            Surface = surface,
            Digest = digest,
            Size = size,
            MediaType = mediaType,
            StoreUri = artifact.RepoUri.ToString()
        };

        var metadata = new Dictionary<string, object?>
        {
            [StateMetadataKey] = state
        };

        return new DocumentModel(artifact.RepoUri, mediaType, surface.BodyText, metadata: metadata);
    }

    public Records Materialize(DocumentModel document)
    {
        if (document.GetMetadataOrDefault<DocxDocumentState>(StateMetadataKey) is not { } state)
            throw new InvalidOperationException("DOCX document missing state metadata.");

        string? headline = null;
        string? summary = null;
        string? structure = null;
        var tokenCount = TokenEstimator.EstimateTokensSafe(state.Surface.BodyText);
        var fileName = GetFileName(document.Uri);
        var displayTitle = string.IsNullOrWhiteSpace(state.Surface.Properties.Title)
            ? fileName
            : state.Surface.Properties.Title;

        try
        {
            var model = BuildExploreModel(state, fileName, tokenCount);

            summary = _renderer.RenderAsync("explore/summary", model).GetAwaiter().GetResult();
            structure = _renderer.RenderAsync("explore/structure", model).GetAwaiter().GetResult();
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
            Text = state.Surface.BodyText,
            StoreUri = state.StoreUri,
            Headline = headline,
            Summary = summary,
            Structure = structure,
            TokenCount = tokenCount
        };

        var now = DateTimeOffset.UtcNow;
        var trackedAuthors = new JsonArray(
            state.Surface.TrackedChangeAuthors
                .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
                .Select(author => (JsonNode)JsonValue.Create(author)!)
                .ToArray());

        var customProperties = new JsonObject();
        foreach (var (key, value) in state.Surface.Properties.CustomProperties
                     .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
        {
            customProperties[key] = value;
        }

        var docNode = new Node
        {
            Id = state.Surface.DocumentId,
            Kind = DocxNodeKinds.Document,
            Uri = document.Uri,
            ArtifactId = artifact.Id,
            Props = new JsonObject
            {
                ["media_type"] = state.MediaType.ToString(),
                ["byte_size"] = state.Size,
                ["title"] = displayTitle,
                ["author"] = state.Surface.Properties.Author,
                ["created"] = state.Surface.Properties.Created,
                ["modified"] = state.Surface.Properties.Modified,
                ["last_modified_by"] = state.Surface.Properties.LastModifiedBy,
                ["description"] = state.Surface.Properties.Description,
                ["subject"] = state.Surface.Properties.Subject,
                ["keywords"] = state.Surface.Properties.Keywords,
                ["application"] = state.Surface.Properties.Application,
                ["custom_properties"] = customProperties,
                ["header_text"] = state.Surface.HeaderText,
                ["footer_text"] = state.Surface.FooterText,
                ["page_count"] = state.Surface.Stats.PageCount,
                ["word_count"] = state.Surface.Stats.WordCount,
                ["paragraph_count"] = state.Surface.Stats.ParagraphCount,
                ["heading_count"] = state.Surface.Headings.Count,
                ["table_count"] = state.Surface.Tables.Count,
                ["image_count"] = state.Surface.Images.Count,
                ["comment_count"] = state.Surface.Comments.Count,
                ["open_comment_count"] = state.Surface.OpenCommentCount,
                ["form_field_count"] = state.Surface.FormFieldCount,
                ["has_tracked_changes"] = state.Surface.HasTrackedChanges,
                ["tracked_change_count"] = state.Surface.TrackedChangeCount,
                ["tracked_change_authors"] = trackedAuthors
            },
            CreatedAt = now,
            UpdatedAt = now
        };

        var nodes = new List<Node> { docNode };
        var spans = new List<Span>();
        var edges = new List<Edge>();
        var compositionOrder = new List<(int StartChar, Guid NodeId)>();
        var commentNodeIds = new List<Guid>();
        var annotations = new List<Annotation>();

        for (var index = 0; index < state.Surface.Headings.Count; index++)
        {
            var heading = state.Surface.Headings[index];
            var startChar = heading.StartChar;
            var endChar = index + 1 < state.Surface.Headings.Count
                ? state.Surface.Headings[index + 1].StartChar
                : document.Text.Length;
            endChar = Math.Max(startChar, endChar);

            var mapped = document.LineMap.GetSpan(startChar, endChar);

            var span = new Span
            {
                Id = heading.SpanId,
                DocumentId = docNode.Id,
                StartLine = mapped.StartLine,
                StartColumn = mapped.StartColumn,
                EndLine = mapped.EndLine,
                EndColumn = mapped.EndColumn,
                StartByte = CalculateUtf8Bytes(document.Text, mapped.StartChar),
                EndByte = CalculateUtf8Bytes(document.Text, mapped.EndChar)
            };
            spans.Add(span);

            var headingNode = new Node
            {
                Id = heading.NodeId,
                Kind = DocxNodeKinds.Heading,
                Uri = RepoUri.FromSymbol(document.Uri.Container, heading.Symbol, mapped.StartLine, mapped.EndLine),
                SpanId = heading.SpanId,
                Props = new JsonObject
                {
                    ["level"] = heading.Level,
                    ["text"] = heading.Text,
                    ["paragraph_index"] = heading.ParagraphIndex,
                    ["symbol"] = heading.Symbol
                },
                Headline = $"H{heading.Level} · {heading.Text}",
                CreatedAt = now,
                UpdatedAt = now
            };
            nodes.Add(headingNode);
            compositionOrder.Add((heading.StartChar, headingNode.Id));
        }

        foreach (var table in state.Surface.Tables)
        {
            var endChar = Math.Min(document.Text.Length, Math.Max(table.StartChar, table.EndChar));
            var mapped = document.LineMap.GetSpan(table.StartChar, endChar);

            var tableSpan = new Span
            {
                Id = table.SpanId,
                DocumentId = docNode.Id,
                StartLine = mapped.StartLine,
                StartColumn = mapped.StartColumn,
                EndLine = mapped.EndLine,
                EndColumn = mapped.EndColumn,
                StartByte = CalculateUtf8Bytes(document.Text, mapped.StartChar),
                EndByte = CalculateUtf8Bytes(document.Text, mapped.EndChar)
            };
            spans.Add(tableSpan);

            var tableColumnNames = new JsonArray(
                table.ColumnNames
                    .Select(name => (JsonNode)JsonValue.Create(name)!)
                    .ToArray());

            var tableNode = new Node
            {
                Id = table.NodeId,
                Kind = DocxNodeKinds.Table,
                Uri = RepoUri.FromSymbol(document.Uri.Container, table.Symbol, mapped.StartLine, mapped.EndLine),
                SpanId = table.SpanId,
                Props = new JsonObject
                {
                    ["row_count"] = table.RowCount,
                    ["col_count"] = table.ColCount,
                    ["column_names"] = tableColumnNames,
                    ["has_header"] = table.HasHeader,
                    ["symbol"] = table.Symbol
                },
                Headline = BuildTableHeadline(table),
                CreatedAt = now,
                UpdatedAt = now
            };
            nodes.Add(tableNode);
            compositionOrder.Add((table.StartChar, tableNode.Id));
        }

        foreach (var image in state.Surface.Images)
        {
            var safeStart = Math.Clamp(image.StartChar, 0, document.Text.Length);
            var safeEnd = Math.Clamp(Math.Max(image.StartChar, image.EndChar), safeStart, document.Text.Length);
            var mapped = document.LineMap.GetSpan(safeStart, safeEnd);

            var imageSpan = new Span
            {
                Id = image.SpanId,
                DocumentId = docNode.Id,
                StartLine = mapped.StartLine,
                StartColumn = mapped.StartColumn,
                EndLine = mapped.EndLine,
                EndColumn = mapped.EndColumn,
                StartByte = CalculateUtf8Bytes(document.Text, mapped.StartChar),
                EndByte = CalculateUtf8Bytes(document.Text, mapped.EndChar)
            };
            spans.Add(imageSpan);

            var imageNode = new Node
            {
                Id = image.NodeId,
                Kind = DocxNodeKinds.Image,
                Uri = RepoUri.FromLines(document.Uri.Container, mapped.StartLine, mapped.EndLine),
                SpanId = image.SpanId,
                Props = new JsonObject
                {
                    ["alt_text"] = image.AltText,
                    ["caption"] = image.Caption,
                    ["content_type"] = image.ContentType,
                    ["is_embedded"] = image.IsEmbedded,
                    ["missing"] = image.IsMissing,
                    ["paragraph_index"] = image.ParagraphIndex
                },
                Headline = string.IsNullOrWhiteSpace(image.AltText)
                    ? "Image"
                    : $"Image · {image.AltText}",
                CreatedAt = now,
                UpdatedAt = now
            };
            nodes.Add(imageNode);
            compositionOrder.Add((image.StartChar, imageNode.Id));

            if (!string.IsNullOrWhiteSpace(image.AltText) || !string.IsNullOrWhiteSpace(image.Caption))
                continue;

            try
            {
                annotations.Add(new Annotation
                {
                    Kind = "lint",
                    Severity = "warning",
                    Source = DocxAnnotationSource,
                    RuleId = "docx.image-no-alt",
                    Message = $"Image at paragraph {image.ParagraphIndex} is missing alt text and caption.",
                    ScopeDocumentId = docNode.Id,
                    TargetNodeId = imageNode.Id,
                    TargetSpanId = image.SpanId,
                    TargetUri = imageNode.Uri,
                    Data = new JsonObject
                    {
                        ["paragraph_index"] = image.ParagraphIndex
                    },
                    CreatedAt = now
                });
            }
            catch
            {
                // Diagnostics are best-effort and must never block indexing.
            }
        }

        foreach (var comment in state.Surface.Comments)
        {
            var anchorStart = comment.AnchorStartParagraph;
            var anchorEnd = comment.AnchorEndParagraph ?? anchorStart;
            RepoUri? commentUri = null;
            if (anchorStart is > 0)
            {
                var startLine = Math.Clamp(anchorStart.Value, 1, Math.Max(1, document.LineMap.LineCount));
                var endLine = Math.Clamp(anchorEnd ?? anchorStart.Value, startLine, Math.Max(1, document.LineMap.LineCount));
                commentUri = RepoUri.FromLines(document.Uri.Container, startLine, endLine);
            }

            var commentNode = new Node
            {
                Id = comment.NodeId,
                Kind = DocxNodeKinds.Comment,
                Uri = commentUri,
                Props = new JsonObject
                {
                    ["id"] = comment.Id,
                    ["author"] = comment.Author,
                    ["date"] = comment.Date,
                    ["text"] = comment.Text,
                    ["anchor_start_paragraph"] = comment.AnchorStartParagraph,
                    ["anchor_end_paragraph"] = comment.AnchorEndParagraph,
                    ["resolved"] = comment.Resolved
                },
                Headline = string.IsNullOrWhiteSpace(comment.Author)
                    ? "Comment"
                    : $"Comment · {comment.Author}",
                CreatedAt = now,
                UpdatedAt = now
            };
            nodes.Add(commentNode);
            commentNodeIds.Add(commentNode.Id);
        }

        var ordinal = 0;
        foreach (var composition in compositionOrder.OrderBy(c => c.StartChar))
        {
            edges.Add(CreateHasPart(docNode.Id, composition.NodeId, docNode.Id, ordinal++, now));
        }
        foreach (var commentNodeId in commentNodeIds)
        {
            edges.Add(CreateHasPart(docNode.Id, commentNodeId, docNode.Id, ordinal++, now));
        }

        foreach (var hyperlink in state.Surface.Hyperlinks)
        {
            if (!hyperlink.IsExternal || string.IsNullOrWhiteSpace(hyperlink.TargetUrl))
                continue;

            try
            {
                edges.Add(new Edge
                {
                    Id = Guid.NewGuid(),
                    SrcId = docNode.Id,
                    DstUri = RepoUri.Parse(hyperlink.TargetUrl),
                    Type = "REFERS_TO",
                    IsComposition = false,
                    ScopeDocumentId = docNode.Id,
                    Props = new JsonObject
                    {
                        ["display_text"] = hyperlink.DisplayText
                    },
                    CreatedAt = now
                });
            }
            catch
            {
                // Skip malformed hyperlink targets.
            }
        }

        return new Records
        {
            Artifacts = [artifact],
            Nodes = [.. nodes],
            Spans = [.. spans],
            Edges = [.. edges],
            Annotations = [.. annotations]
        };
    }

    private DocumentSurface ParseDocument(WordprocessingDocument document)
    {
        var mainPart = document.MainDocumentPart
                       ?? throw new InvalidDataException("Word document is missing MainDocumentPart.");
        var body = mainPart.Document?.Body
                   ?? throw new InvalidDataException("Word document is missing document body.");

        string? title = null;
        string? author = null;
        DateTimeOffset? created = null;
        DateTimeOffset? modified = null;
        string? lastModifiedBy = null;
        string? description = null;
        string? subject = null;
        string? keywords = null;
        try
        {
            (title, author, created, modified, lastModifiedBy, description, subject, keywords) = TryGetCoreProperties(document);
        }
        catch (Exception ex)
        {
            LogSkippedProperty(ex, "core_properties");
        }

        int? pageCount = null;
        int? wordCount = null;
        int? paragraphCountFromProperties = null;
        string? application = null;
        try
        {
            (pageCount, wordCount, paragraphCountFromProperties, application) = TryGetExtendedStats(document);
        }
        catch (Exception ex)
        {
            LogSkippedProperty(ex, "extended_properties");
        }

        IReadOnlyDictionary<string, string?> customProperties =
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            customProperties = TryGetCustomProperties(document);
        }
        catch (Exception ex)
        {
            LogSkippedProperty(ex, "custom_properties");
        }

        Dictionary<string, string?> styleInheritance;
        try
        {
            styleInheritance = BuildStyleInheritance(mainPart);
        }
        catch (Exception ex)
        {
            styleInheritance = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            LogStyleInheritanceResolutionFailed(ex);
        }

        var headingSymbols = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tableSymbols = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var headings = new List<HeadingInfo>();
        var tables = new List<TableInfo>();
        var images = new List<ImageInfo>();
        var trackedChanges = new TrackedChangesState();
        var builder = new StringBuilder();
        IReadOnlyList<FootnoteInfo> footnotes = [];
        var footnoteIds = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            footnotes = ExtractFootnotes(mainPart);
            foreach (var footnote in footnotes)
                footnoteIds.Add(footnote.Id);
        }
        catch (Exception ex)
        {
            LogSkippedFootnotes(ex);
        }

        IReadOnlyList<EndnoteInfo> endnotes = [];
        var endnoteIds = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            endnotes = ExtractEndnotes(mainPart);
            foreach (var endnote in endnotes)
                endnoteIds.Add(endnote.Id);
        }
        catch (Exception ex)
        {
            LogSkippedEndnotes(ex);
        }

        IReadOnlyList<HyperlinkInfo> hyperlinks = [];
        try
        {
            hyperlinks = ExtractHyperlinks(body, mainPart);
        }
        catch (Exception ex)
        {
            LogSkippedHyperlinks(ex);
        }

        string? headerText = null;
        string? footerText = null;
        try
        {
            (headerText, footerText) = ExtractHeaderFooterText(mainPart);
        }
        catch (Exception ex)
        {
            LogSkippedHeaderFooterMetadata(ex);
        }

        var formFieldCount = 0;
        try
        {
            formFieldCount = CountFormFields(body);
        }
        catch (Exception ex)
        {
            LogSkippedProperty(ex, "form_field_count");
        }

        var paragraphIndex = 0;
        var outputLine = 1;
        var charOffset = 0;
        var tableIndex = 0;
        var bodyElements = body.ChildElements.ToList();

        // If no paragraphs use heading styles, fall back to heuristic detection
        // (bold + short + font size) which handles the common case of manually-formatted documents.
        Dictionary<int, int>? heuristicHeadings = null;
        {
            var hasStyleHeadings = bodyElements
                .OfType<Paragraph>()
                .Any(p => ResolveHeadingLevel(p, styleInheritance).HasValue);

            if (!hasStyleHeadings)
                heuristicHeadings = DetectHeuristicHeadings(bodyElements);
        }

        for (var elementIndex = 0; elementIndex < bodyElements.Count; elementIndex++)
        {
            var element = bodyElements[elementIndex];
            switch (element)
            {
                case Paragraph paragraph:
                {
                    paragraphIndex++;

                    try
                    {
                        CollectTrackedChanges(paragraph, trackedChanges);
                        IReadOnlyList<ImageInfo> paragraphImages = [];
                        try
                        {
                            var adjacentParagraph = elementIndex + 1 < bodyElements.Count
                                ? bodyElements[elementIndex + 1] as Paragraph
                                : null;
                            paragraphImages = ExtractParagraphImages(paragraph, adjacentParagraph, mainPart, paragraphIndex);
                        }
                        catch (Exception ex)
                        {
                            LogSkippedImageExtraction(ex, paragraphIndex);
                        }

                        var rawText = ExtractParagraphText(
                            paragraph,
                            paragraphImages,
                            footnoteIds,
                            endnoteIds,
                            out var imageMarkerOffsets);
                        var text = rawText.Trim();
                        var adjustedOffsets = AdjustOffsetsForTrim(rawText, text, imageMarkerOffsets);
                        var headingLevel = ResolveHeadingLevel(paragraph, styleInheritance)
                            ?? (heuristicHeadings?.TryGetValue(paragraphIndex, out var hLevel) == true ? hLevel : null);
                        var isHeading = headingLevel.HasValue && !string.IsNullOrWhiteSpace(text);

                        var lineText = isHeading
                            ? $"{new string('#', headingLevel!.Value)} {text}"
                            : text;

                        var startChar = AppendOutputLine(builder, lineText, ref charOffset, ref outputLine);

                        if (isHeading)
                        {
                            var symbolBase = BuildHeadingSymbol(text);
                            var symbol = MakeUniqueSymbol(symbolBase, headingSymbols);
                            headings.Add(new HeadingInfo(
                                NodeId: Guid.NewGuid(),
                                SpanId: Guid.NewGuid(),
                                Level: headingLevel!.Value,
                                Text: text,
                                ParagraphIndex: paragraphIndex,
                                Symbol: symbol,
                                OutputLine: outputLine,
                                StartChar: startChar));
                        }

                        for (var imageIndex = 0; imageIndex < paragraphImages.Count; imageIndex++)
                        {
                            var image = paragraphImages[imageIndex];
                            var marker = BuildImageMarker(image.AltText);
                            var markerOffset = imageIndex < adjustedOffsets.Count
                                ? adjustedOffsets[imageIndex]
                                : Math.Max(0, lineText.Length - marker.Length);
                            markerOffset = Math.Clamp(markerOffset, 0, Math.Max(0, lineText.Length));

                            images.Add(image with
                            {
                                OutputLine = outputLine,
                                StartChar = startChar + markerOffset,
                                EndChar = startChar + Math.Min(lineText.Length, markerOffset + marker.Length)
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSkippedParagraph(ex, paragraphIndex);
                    }

                    break;
                }

                case Table table:
                {
                    tableIndex++;

                    try
                    {
                        var tableInfo = ParseTableInfo(table, tableIndex, tableSymbols);
                        if (tableInfo.IsLayout)
                            break;

                        var marker = BuildTableMarker(tableInfo);
                        var startChar = AppendOutputLine(builder, marker, ref charOffset, ref outputLine);

                        tables.Add(tableInfo with
                        {
                            OutputLine = outputLine,
                            StartChar = startChar,
                            EndChar = startChar + marker.Length
                        });
                    }
                    catch (Exception ex)
                    {
                        LogSkippedTable(ex, tableIndex);
                    }

                    break;
                }
            }
        }

        AppendDelimitedSection(
            builder,
            ref charOffset,
            ref outputLine,
            "Footnotes:",
            footnotes
                .Select(footnote => $"[{footnote.Id}] {footnote.Text}")
                .ToList());

        AppendDelimitedSection(
            builder,
            ref charOffset,
            ref outputLine,
            "Endnotes:",
            endnotes
                .Select(endnote => $"[*{endnote.Id}] {endnote.Text}")
                .ToList());

        IReadOnlyList<CommentInfo> comments = [];
        try
        {
            comments = ExtractComments(mainPart, body);
        }
        catch (Exception ex)
        {
            LogSkippedComments(ex);
        }

        return new DocumentSurface
        {
            DocumentId = Guid.NewGuid(),
            Properties = new DocumentProperties
            {
                Title = title,
                Author = author,
                Created = created,
                Modified = modified,
                LastModifiedBy = lastModifiedBy,
                Description = description,
                Subject = subject,
                Keywords = keywords,
                Application = application,
                CustomProperties = customProperties
            },
            Headings = headings,
            Tables = tables,
            Images = images,
            Comments = comments,
            Footnotes = footnotes,
            Endnotes = endnotes,
            Hyperlinks = hyperlinks,
            HeaderText = headerText,
            FooterText = footerText,
            BodyText = builder.ToString(),
            Stats = new DocumentStats
            {
                PageCount = pageCount,
                WordCount = wordCount,
                ParagraphCount = paragraphCountFromProperties ?? paragraphIndex
            },
            HasTrackedChanges = trackedChanges.Count > 0,
            TrackedChangeCount = trackedChanges.Count,
            TrackedChangeAuthors = trackedChanges.Authors.OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList(),
            ContentControlCount = formFieldCount
        };
    }

    private static Dictionary<string, object?> BuildExploreModel(DocxDocumentState state, string fileName, int? tokenCount)
    {
        var title = string.IsNullOrWhiteSpace(state.Surface.Properties.Title) ? null : state.Surface.Properties.Title;
        var displayTitle = title ?? fileName;

        var minimumLevel = state.Surface.Headings.Count > 0
            ? state.Surface.Headings.Min(h => h.Level)
            : 1;

        var topLevelHeadings = state.Surface.Headings
            .Where(h => h.Level == minimumLevel)
            .Select(h => h.Text)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        var headingModel = state.Surface.Headings
            .Select(h => new Dictionary<string, object?>
            {
                ["level"] = h.Level,
                ["text"] = h.Text,
                ["symbol"] = h.Symbol,
                ["marker"] = new string('#', h.Level)
            })
            .ToList();

        var tableModel = state.Surface.Tables
            .Select(t => new Dictionary<string, object?>
            {
                ["row_count"] = t.RowCount,
                ["col_count"] = t.ColCount,
                ["column_names"] = t.ColumnNames,
                ["has_header"] = t.HasHeader,
                ["symbol"] = t.Symbol,
                ["start_char"] = t.StartChar
            })
            .ToList();

        var structureLines = BuildStructureLines(state.Surface, minimumLevel);

        return new Dictionary<string, object?>
        {
            ["file_name"] = fileName,
            ["display_title"] = displayTitle,
            ["media_kind"] = state.MediaType.Kind ?? "docx.document",
            ["page_count"] = state.Surface.Stats.PageCount,
            ["word_count"] = state.Surface.Stats.WordCount,
            ["paragraph_count"] = state.Surface.Stats.ParagraphCount,
            ["token_count"] = tokenCount ?? 0,
            ["heading_count"] = state.Surface.Headings.Count,
            ["table_count"] = state.Surface.Tables.Count,
            ["image_count"] = state.Surface.Images.Count,
            ["comment_count"] = state.Surface.Comments.Count,
            ["open_comment_count"] = state.Surface.OpenCommentCount,
            ["has_tracked_changes"] = state.Surface.HasTrackedChanges,
            ["form_field_count"] = state.Surface.FormFieldCount,
            ["top_level_headings"] = topLevelHeadings,
            ["headings"] = headingModel,
            ["tables"] = tableModel,
            ["structure_lines"] = structureLines
        };
    }

    private static List<string> BuildStructureLines(DocumentSurface surface, int minimumHeadingLevel)
    {
        var lines = new List<(int StartChar, string Text)>();

        foreach (var heading in surface.Headings)
        {
            var indentSize = ((heading.Level - minimumHeadingLevel) * 2) + 2;
            var indent = new string(' ', Math.Max(2, indentSize));
            lines.Add((heading.StartChar, $"{indent}{new string('#', heading.Level)} {heading.Text}"));
        }

        var orderedHeadings = surface.Headings.OrderBy(h => h.StartChar).ToList();
        foreach (var table in surface.Tables)
        {
            var containingHeading = orderedHeadings.LastOrDefault(h => h.StartChar <= table.StartChar);
            var indentSize = containingHeading is null
                ? 2
                : ((containingHeading.Level - minimumHeadingLevel) * 2) + 4;
            var indent = new string(' ', Math.Max(2, indentSize));
            var label = BuildTableLabel(table.ColumnNames);
            var dimensions = $"({table.ColCount} cols x {table.RowCount} rows)";
            var line = string.IsNullOrWhiteSpace(label)
                ? $"{indent}Table: {dimensions}"
                : $"{indent}Table: {label} {dimensions}";

            lines.Add((table.StartChar, line));
        }

        foreach (var image in surface.Images)
        {
            var containingHeading = orderedHeadings.LastOrDefault(h => h.StartChar <= image.StartChar);
            var indentSize = containingHeading is null
                ? 2
                : ((containingHeading.Level - minimumHeadingLevel) * 2) + 4;
            var indent = new string(' ', Math.Max(2, indentSize));
            var label = BuildImageMarker(image.AltText);
            if (!string.IsNullOrWhiteSpace(image.Caption))
                label = $"{label} ({image.Caption})";

            lines.Add((image.StartChar, $"{indent}{label}"));
        }

        return lines
            .OrderBy(line => line.StartChar)
            .Select(line => line.Text)
            .ToList();
    }

    private static TableInfo ParseTableInfo(Table table, int tableIndex, IDictionary<string, int> tableSymbols)
    {
        var rows = table.Elements<TableRow>().ToList();
        if (rows.Count == 0)
            throw new InvalidDataException("Table contains no rows.");

        var headerRowIndex = rows.FindIndex(IsStyledHeaderRow);
        var hasStyledHeader = headerRowIndex >= 0;

        if (headerRowIndex < 0 && ShouldUseHeuristicHeader(rows))
            headerRowIndex = 0;

        var hasHeader = headerRowIndex >= 0;

        var mutableRows = new List<List<MutableCell?>>();
        var activeVerticalMerges = new Dictionary<int, MutableCell>();
        var maxColumns = table.GetFirstChild<TableGrid>()?.Elements<GridColumn>().Count() ?? 0;

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var rowCells = new List<MutableCell?>();
            var colCursor = 0;
            MutableCell? lastAnchorInRow = null;

            foreach (var cell in row.Elements<TableCell>())
            {
                var cellProperties = cell.TableCellProperties;
                var gridSpanRaw = (int?)cellProperties?.GridSpan?.Val?.Value;
                if (gridSpanRaw is < 1)
                    throw new InvalidDataException("Table cell has invalid grid span.");

                var gridSpan = gridSpanRaw ?? 1;
                var horizontalMerge = GetMergeState(cellProperties?.HorizontalMerge);
                var verticalMerge = GetMergeState(cellProperties?.VerticalMerge);

                if (horizontalMerge == MergeState.Continue)
                {
                    if (lastAnchorInRow is not null)
                    {
                        lastAnchorInRow.ColSpan += gridSpan;
                        if (verticalMerge == MergeState.Restart)
                            RegisterVerticalMerge(activeVerticalMerges, lastAnchorInRow, colCursor, gridSpan);
                        else if (verticalMerge == MergeState.Continue)
                            ExtendVerticalMerge(activeVerticalMerges, colCursor, gridSpan);
                    }

                    AppendNullCells(rowCells, gridSpan);
                    colCursor += gridSpan;
                    continue;
                }

                if (verticalMerge == MergeState.Continue)
                {
                    ExtendVerticalMerge(activeVerticalMerges, colCursor, gridSpan);
                    AppendNullCells(rowCells, gridSpan);
                    colCursor += gridSpan;
                    lastAnchorInRow = null;
                    continue;
                }

                var cellText = ExtractTableCellText(cell, includeNestedTables: true).Trim();
                var anchorCell = new MutableCell(cellText)
                {
                    RowSpan = 1,
                    ColSpan = gridSpan
                };

                rowCells.Add(anchorCell);
                AppendNullCells(rowCells, gridSpan - 1);

                if (verticalMerge == MergeState.Restart)
                    RegisterVerticalMerge(activeVerticalMerges, anchorCell, colCursor, gridSpan);
                else
                    ClearVerticalMergeColumns(activeVerticalMerges, colCursor, gridSpan);

                colCursor += gridSpan;
                lastAnchorInRow = anchorCell;
            }

            maxColumns = Math.Max(maxColumns, colCursor);
            mutableRows.Add(rowCells);
        }

        foreach (var rowCells in mutableRows)
        {
            while (rowCells.Count < maxColumns)
                rowCells.Add(null);
        }

        var cells = mutableRows
            .Select(row => (IReadOnlyList<CellInfo?>)row
                .Select(cell => cell is null
                    ? null
                    : new CellInfo
                    {
                        Text = cell.Text,
                        RowSpan = Math.Max(1, cell.RowSpan),
                        ColSpan = Math.Max(1, cell.ColSpan)
                    })
                .ToList())
            .ToList();

        var columnNames = new List<string>();
        if (hasHeader)
        {
            foreach (var cell in cells[headerRowIndex])
            {
                if (cell is null)
                    continue;
                if (string.IsNullOrWhiteSpace(cell.Text))
                    continue;

                columnNames.Add(cell.Text.Trim());
            }
        }

        var rowCount = rows.Count;
        var colCount = Math.Max(1, maxColumns);
        var isLayout = IsLayoutTable(table, colCount, hasHeader, hasStyledHeader);

        var symbolSeed = columnNames.Count > 0
            ? string.Join(" ", columnNames)
            : $"Table {tableIndex}";

        var symbolBase = BuildHeadingSymbol(symbolSeed);
        if (string.Equals(symbolBase, "Heading", StringComparison.Ordinal))
            symbolBase = "Table";
        var symbol = MakeUniqueSymbol(symbolBase, tableSymbols);

        return new TableInfo
        {
            NodeId = Guid.NewGuid(),
            SpanId = Guid.NewGuid(),
            RowCount = rowCount,
            ColCount = colCount,
            HasHeader = hasHeader,
            ColumnNames = columnNames,
            Cells = cells,
            IsLayout = isLayout,
            Symbol = symbol,
            OutputLine = 0,
            StartChar = 0,
            EndChar = 0
        };
    }

    private static bool IsLayoutTable(Table table, int colCount, bool hasHeader, bool hasStyledHeader)
    {
        if (colCount != 1)
            return false;
        if (hasHeader || hasStyledHeader)
            return false;

        return !HasVisibleBorders(table);
    }

    private static bool HasVisibleBorders(Table table)
    {
        var borders = table.GetFirstChild<TableProperties>()?.GetFirstChild<TableBorders>();
        if (borders is null)
            return false;

        foreach (var child in borders.ChildElements)
        {
            if (child is not BorderType border)
                continue;

            var value = border.Val?.Value;
            if (value is null)
                return true;

            if (value != BorderValues.Nil && value != BorderValues.None)
                return true;
        }

        return false;
    }

    private static bool IsStyledHeaderRow(TableRow row)
        => row.TableRowProperties?.GetFirstChild<TableHeader>() is not null;

    private static bool ShouldUseHeuristicHeader(IReadOnlyList<TableRow> rows)
    {
        if (rows.Count < 2)
            return false;

        var firstRowTexts = rows[0]
            .Elements<TableCell>()
            .Select(cell => ExtractTableCellText(cell, includeNestedTables: false).Trim())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();

        if (firstRowTexts.Count == 0)
            return false;

        var firstSignature = BuildRowFormattingSignature(rows[0]);

        foreach (var row in rows.Skip(1))
        {
            var rowTexts = row
                .Elements<TableCell>()
                .Select(cell => ExtractTableCellText(cell, includeNestedTables: false).Trim())
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToList();

            if (rowTexts.Count == 0)
                continue;

            var rowSignature = BuildRowFormattingSignature(row);
            if (!string.Equals(firstSignature, rowSignature, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string BuildRowFormattingSignature(TableRow row)
    {
        var hasBold = row.Descendants<Bold>().Any(IsEnabledOnOffValue);
        var hasItalic = row.Descendants<Italic>().Any(IsEnabledOnOffValue);
        var hasCellShading = row.Descendants<Shading>().Any();
        var paragraphStyles = row.Descendants<ParagraphStyleId>()
            .Select(style => style.Val?.Value)
            .Where(style => !string.IsNullOrWhiteSpace(style))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(style => style, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return $"bold={hasBold};italic={hasItalic};shade={hasCellShading};styles={string.Join(',', paragraphStyles)}";
    }

    private static bool IsEnabledOnOffValue(OnOffType value)
        => value.Val?.Value is null or true;

    private static string ExtractTableCellText(TableCell cell, bool includeNestedTables)
    {
        var parts = new List<string>();

        foreach (var child in cell.ChildElements)
        {
            switch (child)
            {
                case Paragraph paragraph:
                {
                    var text = ExtractParagraphText(paragraph).Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                        parts.Add(text);
                    break;
                }
                case Table nestedTable when includeNestedTables:
                {
                    var nestedText = ExtractNestedTableText(nestedTable);
                    if (!string.IsNullOrWhiteSpace(nestedText))
                        parts.Add(nestedText);
                    break;
                }
            }
        }

        return string.Join(" ", parts);
    }

    private static string ExtractNestedTableText(Table table)
    {
        var parts = new List<string>();

        foreach (var row in table.Elements<TableRow>())
        {
            foreach (var cell in row.Elements<TableCell>())
            {
                var text = ExtractTableCellText(cell, includeNestedTables: false).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    parts.Add(text);
            }
        }

        return string.Join(" ", parts);
    }

    private static void RegisterVerticalMerge(IDictionary<int, MutableCell> activeVerticalMerges, MutableCell cell, int startCol, int colSpan)
    {
        for (var index = 0; index < colSpan; index++)
            activeVerticalMerges[startCol + index] = cell;
    }

    private static void ExtendVerticalMerge(IReadOnlyDictionary<int, MutableCell> activeVerticalMerges, int startCol, int colSpan)
    {
        var touched = new HashSet<MutableCell>();

        for (var index = 0; index < colSpan; index++)
        {
            if (!activeVerticalMerges.TryGetValue(startCol + index, out var cell))
                continue;

            if (touched.Add(cell))
                cell.RowSpan++;
        }
    }

    private static void ClearVerticalMergeColumns(IDictionary<int, MutableCell> activeVerticalMerges, int startCol, int colSpan)
    {
        for (var index = 0; index < colSpan; index++)
            activeVerticalMerges.Remove(startCol + index);
    }

    private static void AppendNullCells(ICollection<MutableCell?> rowCells, int count)
    {
        for (var index = 0; index < count; index++)
            rowCells.Add(null);
    }

    private static MergeState GetMergeState(HorizontalMerge? merge)
    {
        if (merge is null)
            return MergeState.None;

        return merge.Val?.Value == MergedCellValues.Restart
            ? MergeState.Restart
            : MergeState.Continue;
    }

    private static MergeState GetMergeState(VerticalMerge? merge)
    {
        if (merge is null)
            return MergeState.None;

        return merge.Val?.Value == MergedCellValues.Restart
            ? MergeState.Restart
            : MergeState.Continue;
    }

    private static string BuildTableMarker(TableInfo table)
    {
        var label = BuildTableLabel(table.ColumnNames);
        var dimensions = $"({table.ColCount} cols x {table.RowCount} rows)";

        return string.IsNullOrWhiteSpace(label)
            ? $"[Table: {dimensions}]"
            : $"[Table: {label} {dimensions}]";
    }

    private static string BuildTableLabel(IReadOnlyList<string> columnNames)
    {
        var names = columnNames
            .Select(name => name.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        if (names.Count == 0)
            return string.Empty;

        if (names.Count <= 4)
            return string.Join(", ", names);

        return $"{string.Join(", ", names.Take(4))}, ...";
    }

    private static string BuildTableHeadline(TableInfo table)
    {
        var label = BuildTableLabel(table.ColumnNames);
        var dimensions = $"{table.ColCount}x{table.RowCount}";

        return string.IsNullOrWhiteSpace(label)
            ? $"Table · {dimensions}"
            : $"Table · {label} · {dimensions}";
    }

    private static int AppendOutputLine(StringBuilder builder, string lineText, ref int charOffset, ref int outputLine)
    {
        if (builder.Length > 0)
        {
            builder.Append('\n');
            charOffset++;
            outputLine++;
        }

        var startChar = charOffset;
        builder.Append(lineText);
        charOffset += lineText.Length;
        return startChar;
    }

    private static void AppendDelimitedSection(
        StringBuilder builder,
        ref int charOffset,
        ref int outputLine,
        string sectionTitle,
        IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
            return;

        AppendOutputLine(builder, "---", ref charOffset, ref outputLine);
        AppendOutputLine(builder, sectionTitle, ref charOffset, ref outputLine);

        foreach (var line in lines)
            AppendOutputLine(builder, line, ref charOffset, ref outputLine);
    }

    private static (
        string? Title,
        string? Author,
        DateTimeOffset? Created,
        DateTimeOffset? Modified,
        string? LastModifiedBy,
        string? Description,
        string? Subject,
        string? Keywords) TryGetCoreProperties(WordprocessingDocument document)
    {
        var packageProperties = document.PackageProperties;
        return (
            Title: NormalizePropertyValue(packageProperties?.Title),
            Author: NormalizePropertyValue(packageProperties?.Creator),
            Created: packageProperties?.Created,
            Modified: packageProperties?.Modified,
            LastModifiedBy: NormalizePropertyValue(packageProperties?.LastModifiedBy),
            Description: NormalizePropertyValue(packageProperties?.Description),
            Subject: NormalizePropertyValue(packageProperties?.Subject),
            Keywords: NormalizePropertyValue(packageProperties?.Keywords));
    }

    private static (int? PageCount, int? WordCount, int? ParagraphCount, string? Application) TryGetExtendedStats(WordprocessingDocument document)
    {
        var properties = document.ExtendedFilePropertiesPart?.Properties;
        var pageCount = TryParseNullableInt(properties?.Pages?.Text);
        var wordCount = TryParseNullableInt(properties?.Words?.Text);
        var paragraphCount = TryParseNullableInt(properties?.Paragraphs?.Text);
        var application = NormalizePropertyValue(properties?.Application?.Text);
        return (pageCount, wordCount, paragraphCount, application);
    }

    private static IReadOnlyDictionary<string, string?> TryGetCustomProperties(WordprocessingDocument document)
    {
        var customProperties = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var customPartProperties = document.CustomFilePropertiesPart?.Properties;
        if (customPartProperties is null)
            return customProperties;

        foreach (var customProperty in customPartProperties.Elements<DocumentFormat.OpenXml.CustomProperties.CustomDocumentProperty>())
        {
            var propertyName = NormalizePropertyValue(customProperty.Name?.Value);
            if (string.IsNullOrWhiteSpace(propertyName))
                continue;

            var valueElement = customProperty.ChildElements.FirstOrDefault();
            var value = NormalizePropertyValue(valueElement?.InnerText);
            customProperties[propertyName] = value;
        }

        return customProperties;
    }

    private static int CountFormFields(Body body)
        => body.Descendants<SdtBlock>().Count() + body.Descendants<SdtRun>().Count();

    private static string? NormalizePropertyValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim();
    }

    private static int? TryParseNullableInt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static Dictionary<string, string?> BuildStyleInheritance(MainDocumentPart mainPart)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var styles = mainPart.StyleDefinitionsPart?.Styles;
        if (styles is null)
            return result;

        foreach (var style in styles.Elements<Style>())
        {
            var styleId = style.StyleId?.Value;
            if (string.IsNullOrWhiteSpace(styleId))
                continue;
            var basedOn = style.BasedOn?.Val?.Value;
            result[styleId] = basedOn;
        }

        return result;
    }

    private static int? ResolveHeadingLevel(Paragraph paragraph, IReadOnlyDictionary<string, string?> styleInheritance)
    {
        var currentStyleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        var directLevel = TryParseHeadingLevel(currentStyleId);
        if (directLevel.HasValue)
            return directLevel.Value;

        if (string.IsNullOrWhiteSpace(currentStyleId))
            return null;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidate = currentStyleId;
        while (!string.IsNullOrWhiteSpace(candidate) && visited.Add(candidate))
        {
            if (!styleInheritance.TryGetValue(candidate, out candidate))
                break;

            var inheritedLevel = TryParseHeadingLevel(candidate);
            if (inheritedLevel.HasValue)
                return inheritedLevel.Value;
        }

        return null;
    }

    private static int? TryParseHeadingLevel(string? styleId)
    {
        if (string.IsNullOrWhiteSpace(styleId))
            return null;

        var match = HeadingStyleRegex.Match(styleId);
        if (!match.Success)
            return null;

        return int.Parse(match.Groups["level"].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// When no paragraph uses a heading style, detect headings heuristically:
    /// bold, short (&lt;120 chars), non-list paragraphs. Level assigned by
    /// distinct font sizes (largest = H1) or all H1 if sizes are uniform.
    /// </summary>
    private static Dictionary<int, int> DetectHeuristicHeadings(List<OpenXmlElement> bodyElements)
    {
        const int maxHeadingLength = 120;

        var candidates = new List<(int ParagraphIndex, int? HalfPointSize)>();
        var paragraphIndex = 0;

        foreach (var element in bodyElements)
        {
            if (element is not Paragraph paragraph)
                continue;

            paragraphIndex++;
            var text = ExtractParagraphText(paragraph).Trim();
            if (string.IsNullOrWhiteSpace(text) || text.Length > maxHeadingLength)
                continue;

            // Skip list items — they have numbering properties
            if (paragraph.ParagraphProperties?.NumberingProperties is not null)
                continue;

            var runs = paragraph.Elements<Run>().ToList();
            if (runs.Count == 0)
                continue;

            if (!IsParagraphBold(runs))
                continue;

            candidates.Add((paragraphIndex, GetParagraphFontSize(runs)));
        }

        if (candidates.Count == 0)
            return new Dictionary<int, int>();

        // Assign levels from distinct font sizes (largest = H1)
        var distinctSizes = candidates
            .Where(c => c.HalfPointSize.HasValue)
            .Select(c => c.HalfPointSize!.Value)
            .Distinct()
            .OrderByDescending(s => s)
            .ToList();

        var result = new Dictionary<int, int>();
        foreach (var (idx, size) in candidates)
        {
            int level;
            if (distinctSizes.Count > 1 && size.HasValue)
            {
                level = distinctSizes.IndexOf(size.Value) + 1;
                level = Math.Clamp(level, 1, 6);
            }
            else
            {
                level = 1;
            }

            result[idx] = level;
        }

        return result;
    }

    private static bool IsParagraphBold(List<Run> runs)
    {
        int boldChars = 0, totalChars = 0;
        foreach (var run in runs)
        {
            var text = run.InnerText;
            if (string.IsNullOrEmpty(text))
                continue;

            totalChars += text.Length;
            var bold = run.RunProperties?.Bold;
            if (bold is not null && IsEnabledOnOffValue(bold))
                boldChars += text.Length;
        }

        return totalChars > 0 && boldChars * 5 >= totalChars * 4; // >= 80%
    }

    private static int? GetParagraphFontSize(List<Run> runs)
    {
        // Return the most common font size across runs, in half-points.
        var sizes = new List<int>();
        foreach (var run in runs)
        {
            var sz = run.RunProperties?.FontSize?.Val?.Value;
            if (sz is not null && int.TryParse(sz, NumberStyles.Integer, CultureInfo.InvariantCulture, out var halfPoints))
                sizes.Add(halfPoints);
        }

        if (sizes.Count == 0)
            return null;

        return sizes
            .GroupBy(s => s)
            .OrderByDescending(g => g.Count())
            .First().Key;
    }

    private static string ExtractParagraphText(Paragraph paragraph)
    {
        var builder = new StringBuilder();
        foreach (var child in paragraph.ChildElements)
        {
            AppendVisibleText(
                child,
                builder,
                isDeleted: false,
                images: null,
                markerOffsets: null,
                footnoteIds: null,
                endnoteIds: null,
                includeFieldCodes: false);
        }

        return builder.ToString();
    }

    private static string ExtractParagraphText(
        Paragraph paragraph,
        IReadOnlyList<ImageInfo> images,
        IReadOnlySet<string> footnoteIds,
        IReadOnlySet<string> endnoteIds,
        out List<int> markerOffsets)
    {
        markerOffsets = new List<int>(images.Count);
        var builder = new StringBuilder();
        var imageQueue = new Queue<ImageInfo>(images);
        foreach (var child in paragraph.ChildElements)
        {
            AppendVisibleText(
                child,
                builder,
                isDeleted: false,
                imageQueue,
                markerOffsets,
                footnoteIds,
                endnoteIds,
                includeFieldCodes: false);
        }

        while (imageQueue.Count > 0)
        {
            AppendImageMarker(builder, imageQueue.Dequeue(), markerOffsets);
        }

        return builder.ToString();
    }

    private static void AppendVisibleText(
        OpenXmlElement element,
        StringBuilder builder,
        bool isDeleted,
        Queue<ImageInfo>? images,
        ICollection<int>? markerOffsets,
        IReadOnlySet<string>? footnoteIds,
        IReadOnlySet<string>? endnoteIds,
        bool includeFieldCodes)
    {
        var deleted = isDeleted || IsDeletedElement(element);
        if (deleted)
            return;

        if (TryAppendReferenceMarker(builder, element, footnoteIds, endnoteIds))
            return;

        if (includeFieldCodes && TryAppendFieldCodePlaceholder(builder, element))
            return;

        if (images is not null && images.Count > 0 && IsImageMarkerElement(element))
        {
            AppendImageMarker(builder, images.Dequeue(), markerOffsets);
            return;
        }

        switch (element)
        {
            case Text text:
                builder.Append(text.Text);
                break;
            case TabChar:
                builder.Append('\t');
                break;
            case Break:
                builder.Append(' ');
                break;
        }

        foreach (var child in element.ChildElements)
        {
            AppendVisibleText(child, builder, deleted, images, markerOffsets, footnoteIds, endnoteIds, includeFieldCodes);
        }
    }

    private static bool IsImageMarkerElement(OpenXmlElement element)
        => element is Drawing
           || element is Picture
           || string.Equals(element.LocalName, "pict", StringComparison.OrdinalIgnoreCase);

    private static bool TryAppendReferenceMarker(
        StringBuilder builder,
        OpenXmlElement element,
        IReadOnlySet<string>? footnoteIds,
        IReadOnlySet<string>? endnoteIds)
    {
        if (element is FootnoteReference)
        {
            var id = GetAttributeByLocalName(element, "id");
            if (id is not null && footnoteIds?.Contains(id) == true)
                builder.Append($"[^{id}]");
            return true;
        }

        if (element is EndnoteReference)
        {
            var id = GetAttributeByLocalName(element, "id");
            if (id is not null && endnoteIds?.Contains(id) == true)
                builder.Append($"[*{id}]");
            return true;
        }

        return false;
    }

    private static bool TryAppendFieldCodePlaceholder(StringBuilder builder, OpenXmlElement element)
    {
        string? fieldInstruction = null;
        switch (element)
        {
            case FieldCode fieldCode:
                fieldInstruction = fieldCode.Text;
                break;
            case SimpleField simpleField:
                fieldInstruction = NormalizePropertyValue(simpleField.Instruction?.Value)
                                   ?? GetAttributeByLocalName(simpleField, "instr");
                break;
            default:
                return false;
        }

        var fieldName = ExtractFieldInstructionName(fieldInstruction);
        if (fieldName is null)
            return false;

        builder.Append('{');
        builder.Append(fieldName);
        builder.Append('}');
        return true;
    }

    private static string? ExtractFieldInstructionName(string? instruction)
    {
        if (string.IsNullOrWhiteSpace(instruction))
            return null;

        var trimmed = instruction.Trim();
        if (trimmed.Length == 0)
            return null;

        var firstToken = trimmed
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return firstToken is null ? null : firstToken.ToUpperInvariant();
    }

    private static void AppendImageMarker(StringBuilder builder, ImageInfo image, ICollection<int>? markerOffsets)
    {
        var marker = BuildImageMarker(image.AltText);
        markerOffsets?.Add(builder.Length);
        builder.Append(marker);
    }

    private static string BuildImageMarker(string? altText)
    {
        if (string.IsNullOrWhiteSpace(altText))
            return "[Image]";

        return $"[Image: {altText.Trim()}]";
    }

    private static List<int> AdjustOffsetsForTrim(string rawText, string trimmedText, IReadOnlyList<int> offsets)
    {
        if (offsets.Count == 0)
            return [];

        if (string.Equals(rawText, trimmedText, StringComparison.Ordinal))
            return offsets.ToList();

        var leadingTrim = rawText.Length - rawText.TrimStart().Length;
        var adjusted = new List<int>(offsets.Count);
        foreach (var offset in offsets)
        {
            var shifted = offset - leadingTrim;
            if (shifted < 0)
                shifted = 0;
            if (shifted > trimmedText.Length)
                shifted = trimmedText.Length;
            adjusted.Add(shifted);
        }

        return adjusted;
    }

    private static bool IsDeletedElement(OpenXmlElement element)
    {
        if (element is DeletedRun or DeletedText)
            return true;

        return string.Equals(element.LocalName, "del", StringComparison.OrdinalIgnoreCase)
               || string.Equals(element.LocalName, "delText", StringComparison.OrdinalIgnoreCase);
    }

    private static void CollectTrackedChanges(Paragraph paragraph, TrackedChangesState tracked)
    {
        const string wordMlNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        foreach (var change in paragraph.Descendants())
        {
            var localName = change.LocalName;
            if (!string.Equals(localName, "ins", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(localName, "del", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            tracked.Count++;
            var author = change.GetAttribute("author", wordMlNamespace).Value;
            if (!string.IsNullOrWhiteSpace(author))
                tracked.Authors.Add(author.Trim());
        }
    }

    private static IReadOnlyList<ImageInfo> ExtractParagraphImages(
        Paragraph paragraph,
        Paragraph? adjacentParagraph,
        MainDocumentPart mainPart,
        int paragraphIndex)
    {
        var images = new List<ImageInfo>();
        var caption = TryDetectImageCaption(adjacentParagraph);

        foreach (var element in paragraph.Descendants())
        {
            switch (element)
            {
                case Drawing drawing:
                {
                    var image = TryExtractImageInfo(drawing, mainPart, paragraphIndex, caption);
                    if (image is not null)
                        images.Add(image);
                    break;
                }

                case Picture picture:
                {
                    var image = TryExtractImageInfo(picture, mainPart, paragraphIndex, caption);
                    if (image is not null)
                        images.Add(image);
                    break;
                }
            }
        }

        return images;
    }

    private static ImageInfo? TryExtractImageInfo(
        Drawing drawing,
        MainDocumentPart mainPart,
        int paragraphIndex,
        string? caption)
    {
        var blip = drawing.Descendants<A.Blip>().FirstOrDefault();
        if (blip is null)
            return null;

        var altText = TryGetDrawingAltText(drawing);
        var contentType = ResolveImageContentType(mainPart, blip.Embed?.Value, blip.Link?.Value, out var isEmbedded, out var isMissing);

        return new ImageInfo
        {
            NodeId = Guid.NewGuid(),
            SpanId = Guid.NewGuid(),
            AltText = altText,
            Caption = caption,
            ContentType = contentType,
            IsEmbedded = isEmbedded,
            IsMissing = isMissing,
            ParagraphIndex = paragraphIndex,
            OutputLine = 0,
            StartChar = 0,
            EndChar = 0
        };
    }

    private static ImageInfo? TryExtractImageInfo(
        Picture picture,
        MainDocumentPart mainPart,
        int paragraphIndex,
        string? caption)
    {
        var imageData = picture.Descendants<V.ImageData>().FirstOrDefault();
        if (imageData is null)
            return null;

        var relationshipId = NormalizePropertyValue(imageData.RelationshipId?.Value);
        var contentType = ResolveImageContentType(mainPart, relationshipId, null, out var isEmbedded, out var isMissing);
        var altText = NormalizePropertyValue(imageData.Title?.Value);

        return new ImageInfo
        {
            NodeId = Guid.NewGuid(),
            SpanId = Guid.NewGuid(),
            AltText = altText,
            Caption = caption,
            ContentType = contentType,
            IsEmbedded = isEmbedded,
            IsMissing = isMissing,
            ParagraphIndex = paragraphIndex,
            OutputLine = 0,
            StartChar = 0,
            EndChar = 0
        };
    }

    private static string? TryGetDrawingAltText(Drawing drawing)
    {
        var docProperties = drawing.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties>().FirstOrDefault();
        var altText = NormalizePropertyValue(docProperties?.Description?.Value)
                      ?? NormalizePropertyValue(docProperties?.Title?.Value);
        if (!string.IsNullOrWhiteSpace(altText))
            return altText;

        var pictureProperties = drawing.Descendants<DocumentFormat.OpenXml.Drawing.Pictures.NonVisualDrawingProperties>().FirstOrDefault();
        return NormalizePropertyValue(pictureProperties?.Description?.Value)
               ?? NormalizePropertyValue(pictureProperties?.Title?.Value);
    }

    private static string? ResolveImageContentType(
        MainDocumentPart mainPart,
        string? embedRelationshipId,
        string? linkRelationshipId,
        out bool isEmbedded,
        out bool isMissing)
    {
        if (!string.IsNullOrWhiteSpace(embedRelationshipId))
        {
            isEmbedded = true;
            try
            {
                var part = mainPart.GetPartById(embedRelationshipId);
                if (part is ImagePart imagePart)
                {
                    isMissing = false;
                    return imagePart.ContentType;
                }

                isMissing = true;
                return part.ContentType;
            }
            catch
            {
                isMissing = true;
                return null;
            }
        }

        if (!string.IsNullOrWhiteSpace(linkRelationshipId))
        {
            isEmbedded = false;
            isMissing = !mainPart.ExternalRelationships.Any(rel =>
                string.Equals(rel.Id, linkRelationshipId, StringComparison.Ordinal));
            return null;
        }

        isEmbedded = true;
        isMissing = true;
        return null;
    }

    private static string? TryDetectImageCaption(Paragraph? adjacentParagraph)
    {
        if (adjacentParagraph is null)
            return null;

        var captionCandidate = ExtractParagraphText(adjacentParagraph).Trim();
        if (string.IsNullOrWhiteSpace(captionCandidate))
            return null;

        var paragraphStyleId = adjacentParagraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (string.Equals(paragraphStyleId, "Caption", StringComparison.OrdinalIgnoreCase))
            return captionCandidate;

        if (ContainsSequenceField(adjacentParagraph))
            return captionCandidate;

        return null;
    }

    private static bool ContainsSequenceField(Paragraph paragraph)
    {
        const string wordMlNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        if (paragraph.Descendants<FieldCode>().Any(fieldCode => ContainsSequenceInstruction(fieldCode.Text)))
            return true;

        return paragraph.Descendants<SimpleField>().Any(field =>
            ContainsSequenceInstruction(field.GetAttribute("instr", wordMlNamespace).Value));
    }

    private static bool ContainsSequenceInstruction(string? instruction)
        => !string.IsNullOrWhiteSpace(instruction)
           && instruction.Contains("SEQ", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<FootnoteInfo> ExtractFootnotes(MainDocumentPart mainPart)
    {
        var footnotes = mainPart.FootnotesPart?.Footnotes;
        if (footnotes is null)
            return [];

        var extracted = new List<FootnoteInfo>();
        foreach (var footnote in footnotes.Elements<Footnote>())
        {
            if (IsSystemGeneratedNote(footnote))
                continue;

            var id = GetAttributeByLocalName(footnote, "id");
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var text = ExtractStoryText(footnote);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            extracted.Add(new FootnoteInfo
            {
                Id = id,
                Text = text
            });
        }

        return extracted;
    }

    private static IReadOnlyList<EndnoteInfo> ExtractEndnotes(MainDocumentPart mainPart)
    {
        var endnotes = mainPart.EndnotesPart?.Endnotes;
        if (endnotes is null)
            return [];

        var extracted = new List<EndnoteInfo>();
        foreach (var endnote in endnotes.Elements<Endnote>())
        {
            if (IsSystemGeneratedNote(endnote))
                continue;

            var id = GetAttributeByLocalName(endnote, "id");
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var text = ExtractStoryText(endnote);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            extracted.Add(new EndnoteInfo
            {
                Id = id,
                Text = text
            });
        }

        return extracted;
    }

    private static bool IsSystemGeneratedNote(OpenXmlElement note)
    {
        var type = GetAttributeByLocalName(note, "type");
        if (string.IsNullOrWhiteSpace(type))
            return false;

        return string.Equals(type, "separator", StringComparison.OrdinalIgnoreCase)
               || string.Equals(type, "continuationSeparator", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<HyperlinkInfo> ExtractHyperlinks(Body body, MainDocumentPart mainPart)
    {
        var links = new List<HyperlinkInfo>();
        foreach (var hyperlink in body.Descendants<Hyperlink>())
        {
            var displayText = ExtractElementText(hyperlink, includeFieldCodes: false).Trim();
            var relationshipId = NormalizePropertyValue(hyperlink.Id?.Value)
                                 ?? GetAttributeByLocalName(hyperlink, "id");
            var bookmarkName = NormalizePropertyValue(hyperlink.Anchor?.Value);

            string? targetUrl = null;
            var isExternal = false;
            if (!string.IsNullOrWhiteSpace(relationshipId))
            {
                var relationship = mainPart.HyperlinkRelationships.FirstOrDefault(rel =>
                    string.Equals(rel.Id, relationshipId, StringComparison.Ordinal));
                if (relationship?.Uri is not null)
                {
                    targetUrl = relationship.Uri.ToString();
                    isExternal = true;
                }
            }

            if (string.IsNullOrWhiteSpace(displayText)
                && string.IsNullOrWhiteSpace(targetUrl)
                && string.IsNullOrWhiteSpace(bookmarkName))
            {
                continue;
            }

            links.Add(new HyperlinkInfo
            {
                DisplayText = displayText,
                TargetUrl = targetUrl,
                IsExternal = isExternal,
                BookmarkName = bookmarkName
            });
        }

        return links;
    }

    private static (string? HeaderText, string? FooterText) ExtractHeaderFooterText(MainDocumentPart mainPart)
    {
        var headerText = mainPart.HeaderParts
            .Select(part => part.Header)
            .Select(ExtractStoryText)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));

        var footerText = mainPart.FooterParts
            .Select(part => part.Footer)
            .Select(ExtractStoryText)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));

        return (headerText, footerText);
    }

    private static string ExtractStoryText(OpenXmlElement root)
    {
        var paragraphs = root
            .Descendants<Paragraph>()
            .Select(paragraph => ExtractElementText(paragraph, includeFieldCodes: true).Trim())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();

        if (paragraphs.Count == 0)
            return string.Empty;

        return string.Join(" ", paragraphs);
    }

    private static string ExtractElementText(OpenXmlElement element, bool includeFieldCodes)
    {
        var builder = new StringBuilder();
        foreach (var child in element.ChildElements)
        {
            AppendVisibleText(
                child,
                builder,
                isDeleted: false,
                images: null,
                markerOffsets: null,
                footnoteIds: null,
                endnoteIds: null,
                includeFieldCodes: includeFieldCodes);
        }

        return builder.ToString();
    }

    private IReadOnlyList<CommentInfo> ExtractComments(MainDocumentPart mainPart, Body body)
    {
        var commentsPart = mainPart.WordprocessingCommentsPart;
        var commentsElement = commentsPart?.Comments;
        if (commentsElement is null)
            return [];

        var anchors = BuildCommentAnchors(body);
        IReadOnlyDictionary<string, bool> resolvedByParaId = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var hasResolvedCommentState = false;

        try
        {
            (resolvedByParaId, hasResolvedCommentState) = TryExtractResolvedCommentState(mainPart);
        }
        catch (Exception ex)
        {
            LogCommentsResolvedStateFailed(ex);
        }

        var comments = new List<CommentInfo>();
        var ordinal = 0;
        foreach (var comment in commentsElement.Elements<Comment>())
        {
            var commentId = GetAttributeByLocalName(comment, "id") ?? $"comment_{ordinal}";
            var paraId = GetAttributeByLocalName(comment, "paraId");
            var resolved = hasResolvedCommentState
                           && !string.IsNullOrWhiteSpace(paraId)
                           && resolvedByParaId.TryGetValue(paraId, out var done)
                           && done;

            var text = string.Join(" ",
                comment.Elements<Paragraph>()
                    .Select(paragraph => ExtractParagraphText(paragraph).Trim())
                    .Where(paragraphText => !string.IsNullOrWhiteSpace(paragraphText)));
            if (string.IsNullOrWhiteSpace(text))
                text = NormalizePropertyValue(comment.InnerText) ?? string.Empty;

            anchors.TryGetValue(commentId, out var anchor);
            comments.Add(new CommentInfo
            {
                NodeId = Guid.NewGuid(),
                Id = commentId,
                Author = NormalizePropertyValue(comment.Author),
                Date = comment.Date?.Value,
                Text = text,
                AnchorStartParagraph = anchor.StartParagraph,
                AnchorEndParagraph = anchor.EndParagraph,
                Resolved = resolved
            });
            ordinal++;
        }

        return comments;
    }

    private static IReadOnlyDictionary<string, (int? StartParagraph, int? EndParagraph)> BuildCommentAnchors(Body body)
    {
        var working = new Dictionary<string, CommentAnchorState>(StringComparer.Ordinal);
        var paragraphIndex = 0;

        foreach (var paragraph in body.Descendants<Paragraph>())
        {
            paragraphIndex++;

            foreach (var rangeStart in paragraph.Descendants<CommentRangeStart>())
            {
                var commentId = GetAttributeByLocalName(rangeStart, "id");
                if (string.IsNullOrWhiteSpace(commentId))
                    continue;

                if (!working.TryGetValue(commentId, out var state))
                {
                    state = new CommentAnchorState();
                    working[commentId] = state;
                }

                state.StartParagraph ??= paragraphIndex;
            }

            foreach (var rangeEnd in paragraph.Descendants<CommentRangeEnd>())
            {
                var commentId = GetAttributeByLocalName(rangeEnd, "id");
                if (string.IsNullOrWhiteSpace(commentId))
                    continue;

                if (!working.TryGetValue(commentId, out var state))
                {
                    state = new CommentAnchorState();
                    working[commentId] = state;
                }

                state.EndParagraph ??= paragraphIndex;
            }

            foreach (var reference in paragraph.Descendants<CommentReference>())
            {
                var commentId = GetAttributeByLocalName(reference, "id");
                if (string.IsNullOrWhiteSpace(commentId))
                    continue;

                if (!working.TryGetValue(commentId, out var state))
                {
                    state = new CommentAnchorState();
                    working[commentId] = state;
                }

                state.ReferenceParagraph ??= paragraphIndex;
            }
        }

        return working.ToDictionary(
            kvp => kvp.Key,
            kvp =>
            {
                var start = kvp.Value.StartParagraph ?? kvp.Value.ReferenceParagraph;
                var end = kvp.Value.EndParagraph ?? start;
                return (start, end);
            },
            StringComparer.Ordinal);
    }

    private static (IReadOnlyDictionary<string, bool> ResolvedByParaId, bool HasResolvedStatePart) TryExtractResolvedCommentState(MainDocumentPart mainPart)
    {
        var resolvedByParaId = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var hasResolvedStatePart = false;

        if (mainPart.WordprocessingCommentsExPart?.CommentsEx is { } commentsEx)
        {
            hasResolvedStatePart = true;
            foreach (var commentEx in commentsEx.Elements<DocumentFormat.OpenXml.Office2013.Word.CommentEx>())
            {
                var paraId = NormalizePropertyValue(commentEx.ParaId?.Value);
                if (string.IsNullOrWhiteSpace(paraId))
                    continue;

                resolvedByParaId[paraId] = commentEx.Done?.Value ?? false;
            }
        }

        var commentsExtensiblePart = mainPart.WordCommentsExtensiblePart;
        if (commentsExtensiblePart is null)
            return (resolvedByParaId, hasResolvedStatePart);

        hasResolvedStatePart = true;
        using var stream = commentsExtensiblePart.GetStream(FileMode.Open, FileAccess.Read);
        var xml = XDocument.Load(stream);
        foreach (var element in xml.Descendants().Where(node =>
                     string.Equals(node.Name.LocalName, "commentExtensible", StringComparison.OrdinalIgnoreCase)))
        {
            var paraId = NormalizePropertyValue(GetAttributeByLocalName(element, "paraId"));
            var doneRaw = GetAttributeByLocalName(element, "done");
            if (string.IsNullOrWhiteSpace(paraId) || doneRaw is null)
                continue;

            if (!TryParseBooleanLike(doneRaw, out var done))
                continue;

            resolvedByParaId[paraId] = done;
        }

        return (resolvedByParaId, hasResolvedStatePart);
    }

    private static bool TryParseBooleanLike(string value, out bool parsed)
    {
        if (string.Equals(value, "1", StringComparison.Ordinal)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase))
        {
            parsed = true;
            return true;
        }

        if (string.Equals(value, "0", StringComparison.Ordinal)
            || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "off", StringComparison.OrdinalIgnoreCase))
        {
            parsed = false;
            return true;
        }

        parsed = false;
        return false;
    }

    private static string? GetAttributeByLocalName(OpenXmlElement element, string localName)
    {
        foreach (var attribute in element.GetAttributes())
        {
            if (string.Equals(attribute.LocalName, localName, StringComparison.OrdinalIgnoreCase))
                return NormalizePropertyValue(attribute.Value);
        }

        return null;
    }

    private static string? GetAttributeByLocalName(XElement element, string localName)
    {
        var attribute = element.Attributes().FirstOrDefault(attr =>
            string.Equals(attr.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));
        return NormalizePropertyValue(attribute?.Value);
    }

    private static string BuildHeadingSymbol(string text)
    {
        var matches = Regex.Matches(text, "[\\p{L}\\p{Nd}]+");
        if (matches.Count == 0)
            return "Heading";

        var builder = new StringBuilder();
        foreach (Match match in matches)
        {
            var value = match.Value;
            if (value.Length == 0)
                continue;

            builder.Append(char.ToUpperInvariant(value[0]));
            if (value.Length > 1)
                builder.Append(value.AsSpan(1));
        }

        return builder.Length > 0 ? builder.ToString() : "Heading";
    }

    private static string MakeUniqueSymbol(string symbolBase, IDictionary<string, int> symbolCounts)
    {
        if (!symbolCounts.TryGetValue(symbolBase, out var count))
        {
            symbolCounts[symbolBase] = 1;
            return symbolBase;
        }

        count++;
        symbolCounts[symbolBase] = count;
        return $"{symbolBase}_{count}";
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

    private static long CalculateUtf8Bytes(string text, int chars)
        => Encoding.UTF8.GetByteCount(text.AsSpan(0, Math.Min(text.Length, chars)));

    private static bool IsEncryptedOfficeContainer(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> compoundFileHeader = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
        return bytes.Length >= compoundFileHeader.Length
               && bytes[..compoundFileHeader.Length].SequenceEqual(compoundFileHeader);
    }

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
        var slash = absolutePath.LastIndexOf('/') >= 0
            ? absolutePath[(absolutePath.LastIndexOf('/') + 1)..]
            : absolutePath;
        return string.IsNullOrEmpty(slash) ? uri.AbsoluteUri : slash;
    }

    private sealed class CommentAnchorState
    {
        public int? StartParagraph { get; set; }
        public int? EndParagraph { get; set; }
        public int? ReferenceParagraph { get; set; }
    }

    private sealed class TrackedChangesState
    {
        public int Count { get; set; }
        public HashSet<string> Authors { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class MutableCell(string text)
    {
        public string Text { get; } = text;
        public int RowSpan { get; set; }
        public int ColSpan { get; set; }
    }

    private enum MergeState
    {
        None,
        Restart,
        Continue
    }

    [LoggerMessage(LogLevel.Warning, "DOCX paragraph {ParagraphIndex} could not be parsed and was skipped.")]
    partial void LogSkippedParagraph(Exception ex, int paragraphIndex);

    [LoggerMessage(LogLevel.Warning, "DOCX table {TableIndex} could not be parsed and was skipped.")]
    partial void LogSkippedTable(Exception ex, int tableIndex);

    [LoggerMessage(LogLevel.Warning, "DOCX image extraction failed for paragraph {ParagraphIndex}; continuing without image metadata.")]
    partial void LogSkippedImageExtraction(Exception ex, int paragraphIndex);

    [LoggerMessage(LogLevel.Warning, "DOCX footnotes part could not be parsed; continuing without footnotes.")]
    partial void LogSkippedFootnotes(Exception ex);

    [LoggerMessage(LogLevel.Warning, "DOCX endnotes part could not be parsed; continuing without endnotes.")]
    partial void LogSkippedEndnotes(Exception ex);

    [LoggerMessage(LogLevel.Warning, "DOCX hyperlink extraction failed; continuing without hyperlink metadata.")]
    partial void LogSkippedHyperlinks(Exception ex);

    [LoggerMessage(LogLevel.Warning, "DOCX header/footer extraction failed; continuing without header/footer metadata.")]
    partial void LogSkippedHeaderFooterMetadata(Exception ex);

    [LoggerMessage(LogLevel.Warning, "DOCX comments part could not be parsed; continuing without comments.")]
    partial void LogSkippedComments(Exception ex);

    [LoggerMessage(LogLevel.Warning, "DOCX comments resolved-state extraction failed; treating comments as unresolved.")]
    partial void LogCommentsResolvedStateFailed(Exception ex);

    [LoggerMessage(LogLevel.Warning, "DOCX style inheritance resolution failed; using direct heading style matching.")]
    partial void LogStyleInheritanceResolutionFailed(Exception ex);

    [LoggerMessage(LogLevel.Warning, "DOCX metadata property '{PropertyName}' could not be extracted.")]
    partial void LogSkippedProperty(Exception ex, string propertyName);
}
