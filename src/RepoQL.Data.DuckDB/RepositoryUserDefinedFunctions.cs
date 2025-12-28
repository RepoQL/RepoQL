using DuckDB.NET.Data;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using RepoQL.Contracts;
using RepoQL.Xray;

#pragma warning disable DuckDBNET001

namespace RepoQL.Data.DuckDB;

/// <summary>
///     Registers repository URI and semantic media type scalar UDFs for DuckDB.NET.
///     All functions are pure and null-safe. Use once per connection.
/// </summary>
public static class RepositoryUserDefinedFunctions
{
    // Static holder for the embedding provider - avoids closure issues with DuckDB UDFs
    private static RepoQL.Contracts.Embeddings.IEmbeddingProvider? _embeddingProvider;

    // Static holder for external tool caller - enables calling external services from SQL UDFs
    private static Func<string, string, string?, string>? _externalToolCaller;

    // Static holders for LLM callbacks - enables LLM-powered UDFs
    private static Func<string, string, int, string>? _llmSummarizeCaller;
    private static Func<string, string, string>? _llmExtractCaller;

    /// <summary>
    /// Sets an external tool caller function for use by _mcp_call_internal UDF.
    /// Signature: (serverName, toolName, paramsJson) => resultJson
    /// </summary>
    public static void SetExternalToolCaller(Func<string, string, string?, string>? caller)
        => _externalToolCaller = caller;

    /// <summary>
    /// Sets the LLM summarize caller function for use by _llm_summarize_internal UDF.
    /// Signature: (jsonData, intent, maxTokens) => summary
    /// </summary>
    public static void SetLlmSummarizeCaller(Func<string, string, int, string>? caller)
        => _llmSummarizeCaller = caller;

    /// <summary>
    /// Sets the LLM extract caller function for use by _llm_extract_internal UDF.
    /// Signature: (jsonData, intent) => markdownReport
    /// </summary>
    public static void SetLlmExtractCaller(Func<string, string, string>? caller)
        => _llmExtractCaller = caller;

    /// <summary>
    ///     Registers all scalar UDFs on the provided open connection.
    /// </summary>
    public static void RegisterAll(
        DuckDBConnection connection,
        RepoQL.Contracts.Embeddings.IEmbeddingProvider? embeddingProvider)
    {
        // Always update the static provider reference (may be called multiple times with different providers)
        if (embeddingProvider is not null)
            _embeddingProvider = embeddingProvider;

        if (ScalarFunctionExists(connection, "repository_uri_container"))
            return;

        // Record query-time embedding provider state for diagnostics
        RepoQL.Contracts.Diagnostics.IndexingDiagnostics.SetQueryEmbeddingProvider(
            embeddingProvider?.GetType().Name,
            embeddingProvider?.Enabled ?? false,
            embeddingProvider?.Model);

        // ------------------- Repository URI helpers -------------------

        connection.RegisterScalarFunction<string, string>(
            "repository_uri_container",
            (readers, writer, rowCount) =>
            {
                var r0 = readers[0];
                for (ulong rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    if (!r0.IsValid(rowIndex))
                    {
                        writer.WriteNull(rowIndex);
                        continue;
                    }

                    var uri = r0.GetValue<string>(rowIndex);
                    if (string.IsNullOrWhiteSpace(uri))
                    {
                        writer.WriteNull(rowIndex);
                        continue;
                    }

                    var hash = uri.IndexOf('#');
                    writer.WriteValue(hash < 0 ? uri : uri[..hash], rowIndex);
                }
            },
            true
        );

        connection.RegisterScalarFunction<string, string>(
            "repository_uri_fragment",
            (readers, writer, rowCount) =>
            {
                var r0 = readers[0];
                for (ulong rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    if (!r0.IsValid(rowIndex))
                    {
                        writer.WriteNull(rowIndex);
                        continue;
                    }

                    var uri = r0.GetValue<string>(rowIndex);
                    if (string.IsNullOrWhiteSpace(uri))
                    {
                        writer.WriteNull(rowIndex);
                        continue;
                    }

                    var hash = uri.IndexOf('#');
                    if (hash < 0 || hash == uri.Length - 1)
                    {
                        writer.WriteNull(rowIndex);
                        continue;
                    }

                    writer.WriteValue(uri[(hash + 1)..], rowIndex);
                }
            },
            true
        );

        connection.RegisterScalarFunction<string, string, string>(
            "repository_uri_join",
            (readers, writer, rowCount) =>
            {
                var baseReader = readers[0];
                var fragReader = readers[1];
                for (ulong rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    if (!baseReader.IsValid(rowIndex))
                    {
                        writer.WriteNull(rowIndex);
                        continue;
                    }

                    var container = baseReader.GetValue<string>(rowIndex);
                    if (string.IsNullOrWhiteSpace(container))
                    {
                        writer.WriteNull(rowIndex);
                        continue;
                    }

                    var hasFragment = fragReader.IsValid(rowIndex);
                    var fragment = hasFragment ? fragReader.GetValue<string>(rowIndex) : null;

                    if (string.IsNullOrEmpty(fragment))
                    {
                        writer.WriteValue(container, rowIndex);
                    }
                    else
                    {
                        writer.WriteValue(container + "#" + fragment, rowIndex);
                    }
                }
            },
            true
        );

        connection.RegisterScalarFunction<string, string>(
            "repository_uri_fragment_kind",
            (readers, writer, rowCount) =>
            {
                var r0 = readers[0];
                for (ulong rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    var frag = r0.IsValid(rowIndex) ? ExtractFragment(r0.GetValue<string>(rowIndex)) : null;
                    var kind = "empty";
                    if (!string.IsNullOrEmpty(frag))
                    {
                        if (frag.StartsWith("/")) kind = "json_pointer";
                        else if (frag.StartsWith("line=", StringComparison.OrdinalIgnoreCase)) kind = "line";
                        else if (frag.StartsWith("char=", StringComparison.OrdinalIgnoreCase)) kind = "char";
                        else if (frag.Contains('=')) kind = "parameters";
                        else kind = "anchor";
                    }

                    writer.WriteValue(kind, rowIndex);
                }
            },
            true
        );

        connection.RegisterScalarFunction<string, int>(
            "repository_uri_line_start",
            (readers, writer, rowCount) =>
            {
                var r0 = readers[0];
                for (ulong rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    var frag = r0.IsValid(rowIndex) ? ExtractFragment(r0.GetValue<string>(rowIndex)) : null;
                    var payload = frag is null ? null : ExtractKeyPayload(frag, "line", "line=");
                    if (payload is null)
                    {
                        writer.WriteNull(rowIndex);
                        continue;
                    }

                    var parts = payload.Split(',', 2);
                    if (int.TryParse(parts[0], out var start))
                        writer.WriteValue(start, rowIndex);
                    else
                        writer.WriteNull(rowIndex);
                }
            }
        );

        connection.RegisterScalarFunction<string, int>(
            "repository_uri_line_end",
            (readers, writer, rowCount) =>
            {
                var r0 = readers[0];
                for (ulong rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    var frag = r0.IsValid(rowIndex) ? ExtractFragment(r0.GetValue<string>(rowIndex)) : null;
                    var payload = frag is null ? null : ExtractKeyPayload(frag, "line", "line=");
                    if (payload is null)
                    {
                        writer.WriteNull(rowIndex);
                        continue;
                    }

                    var parts = payload.Split(',', 2);
                    if (parts.Length < 2 || !int.TryParse(parts[1], out var end))
                        writer.WriteNull(rowIndex);
                    else
                        writer.WriteValue(end, rowIndex);
                }
            }
        );

        connection.RegisterScalarFunction<string, string>(
            "repository_uri_json_pointer",
            (readers, writer, rowCount) =>
            {
                var r0 = readers[0];
                for (ulong rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    var frag = r0.IsValid(rowIndex) ? ExtractFragment(r0.GetValue<string>(rowIndex)) : null;
                    if (!string.IsNullOrEmpty(frag) && frag.StartsWith("/"))
                        writer.WriteValue(frag, rowIndex);
                    else
                        writer.WriteNull(rowIndex);
                }
            }
        );

        connection.RegisterScalarFunction<string, string>(
            "repository_uri_anchor",
            (readers, writer, rowCount) =>
            {
                var r0 = readers[0];
                for (ulong rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    var frag = r0.IsValid(rowIndex) ? ExtractFragment(r0.GetValue<string>(rowIndex)) : null;
                    if (string.IsNullOrEmpty(frag) ||
                        frag.StartsWith('/') ||
                        frag.StartsWith("line=", StringComparison.OrdinalIgnoreCase) ||
                        frag.StartsWith("char=", StringComparison.OrdinalIgnoreCase) ||
                        frag.Contains('='))
                    {
                        writer.WriteNull(rowIndex);
                    }
                    else
                    {
                        writer.WriteValue(frag, rowIndex);
                    }
                }
            }
        );

        connection.RegisterScalarFunction<string, string>(
            "repository_uri_symbol",
            (readers, writer, rowCount) =>
            {
                var r0 = readers[0];
                for (ulong rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    var frag = r0.IsValid(rowIndex) ? ExtractFragment(r0.GetValue<string>(rowIndex)) : null;
                    var payload = frag is null ? null : ExtractKeyPayload(frag, "symbol", "symbol=");
                    if (payload is null)
                    {
                        writer.WriteNull(rowIndex);
                        continue;
                    }

                    try
                    {
                        writer.WriteValue(System.Uri.UnescapeDataString(payload), rowIndex);
                    }
                    catch
                    {
                        writer.WriteValue(payload, rowIndex);
                    }
                }
            },
            isPureFunction: true
        );

        connection.RegisterScalarFunction<string, string, bool, string, bool>(
            "repoql_glob_match",
            (readers, writer, rowCount) =>
            {
                var uriReader = readers[0];
                var patternReader = readers[1];
                var ignoreReader = readers[2];
                var defaultSchemeReader = readers[3];

                for (ulong rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    var uri = uriReader.IsValid(rowIndex) ? uriReader.GetValue<string>(rowIndex) : null;
                    var pattern = patternReader.IsValid(rowIndex) ? patternReader.GetValue<string>(rowIndex) : null;
                    var ignoreCase = ignoreReader.IsValid(rowIndex) ? ignoreReader.GetValue<bool>(rowIndex) : true;
                    var defaultScheme = defaultSchemeReader.IsValid(rowIndex)
                        ? defaultSchemeReader.GetValue<string>(rowIndex)
                        : null;

                    var matched = RepoUriGlobMatcher.IsMatch(uri, pattern, ignoreCase, defaultScheme);
                    if (matched is null)
                    {
                        writer.WriteNull(rowIndex);
                    }
                    else
                    {
                        writer.WriteValue(matched.Value, rowIndex);
                    }
                }
            },
            true
        );

        // ------------------- Fragment builders -------------------

        connection.RegisterScalarFunction<int, int, string>(
            "fragment_from_line_range",
            (readers, writer, rowCount) =>
            {
                var startReader = readers[0];
                var endReader = readers[1];
                for (ulong rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    int? start = startReader.IsValid(rowIndex) ? startReader.GetValue<int>(rowIndex) : null;
                    int? end = endReader.IsValid(rowIndex) ? endReader.GetValue<int>(rowIndex) : null;

                    if (start is null && end is null)
                    {
                        writer.WriteNull(rowIndex);
                        continue;
                    }

                    if (end is null) writer.WriteValue($"line={start}", rowIndex);
                    else if (start is null) writer.WriteValue($"line=,{end}", rowIndex);
                    else writer.WriteValue($"line={start},{end}", rowIndex);
                }
            }
        );

        connection.RegisterScalarFunction<long, long, string>(
            "fragment_from_char_range",
            (readers, writer, rowCount) =>
            {
                var startReader = readers[0];
                var endReader = readers[1];
                for (ulong rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    long? start = startReader.IsValid(rowIndex) ? startReader.GetValue<long>(rowIndex) : null;
                    long? end = endReader.IsValid(rowIndex) ? endReader.GetValue<long>(rowIndex) : null;

                    if (start is null && end is null)
                    {
                        writer.WriteNull(rowIndex);
                        continue;
                    }

                    if (end is null) writer.WriteValue($"char={start}", rowIndex);
                    else if (start is null) writer.WriteValue($"char=,{end}", rowIndex);
                    else writer.WriteValue($"char={start},{end}", rowIndex);
                }
            }
        );

        connection.RegisterScalarFunction<string, string>(
            "fragment_from_symbol",
            (readers, writer, rowCount) =>
            {
                var r0 = readers[0];
                for (ulong rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    if (!r0.IsValid(rowIndex))
                    {
                        writer.WriteNull(rowIndex);
                        continue;
                    }

                    var name = r0.GetValue<string>(rowIndex);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        writer.WriteNull(rowIndex);
                        continue;
                    }

                    writer.WriteValue("symbol=" + Uri.EscapeDataString(name), rowIndex);
                }
            }
        );

        // ------------------- Media type helpers -------------------

        connection.RegisterScalarFunction<string, string>(
            "media_type_base",
            (readers, writer, rowCount) =>
            {
                var r0 = readers[0];
                for (ulong rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    if (!r0.IsValid(rowIndex))
                    {
                        writer.WriteNull(rowIndex);
                        continue;
                    }

                    var s = r0.GetValue<string>(rowIndex);
                    if (string.IsNullOrWhiteSpace(s) || !SemanticMediaType.TryParse(s, out var mt))
                    {
                        writer.WriteNull(rowIndex);
                        continue;
                    }

                    writer.WriteValue($"{mt!.Type}/{mt.Subtype}", rowIndex);
                }
            }
        );

        connection.RegisterScalarFunction<string, string>(
            "media_type_kind",
            (readers, writer, rowCount) =>
            {
                var r0 = readers[0];
                for (ulong rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    if (!r0.IsValid(rowIndex))
                    {
                        writer.WriteNull(rowIndex);
                        continue;
                    }

                    var s = r0.GetValue<string>(rowIndex);
                    if (string.IsNullOrWhiteSpace(s) || !SemanticMediaType.TryParse(s, out var mt) || mt!.Kind is null)
                    {
                        writer.WriteNull(rowIndex);
                        continue;
                    }

                    writer.WriteValue(mt!.Kind!, rowIndex);
                }
            }
        );

        connection.RegisterScalarFunction<string, string>(
            "media_type_version",
            (readers, writer, rowCount) =>
            {
                var r0 = readers[0];
                for (ulong rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    if (!r0.IsValid(rowIndex))
                    {
                        writer.WriteNull(rowIndex);
                        continue;
                    }

                    var s = r0.GetValue<string>(rowIndex);
                    if (string.IsNullOrWhiteSpace(s) || !SemanticMediaType.TryParse(s, out var mt) ||
                        mt!.Version is null)
                    {
                        writer.WriteNull(rowIndex);
                        continue;
                    }

                    writer.WriteValue(mt!.Version!, rowIndex);
                }
            }
        );

        connection.RegisterScalarFunction<string, string, string, string>(
            "media_type_with_parameter",
            (readers, writer, rowCount) =>
            {
                var mtReader = readers[0];
                var keyReader = readers[1];
                var valReader = readers[2];
                for (ulong rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    if (!mtReader.IsValid(rowIndex) || !keyReader.IsValid(rowIndex))
                    {
                        writer.WriteNull(rowIndex);
                        continue;
                    }

                    var mtStr = mtReader.GetValue<string>(rowIndex);
                    var key = keyReader.GetValue<string>(rowIndex);
                    var value = valReader.IsValid(rowIndex) ? valReader.GetValue<string>(rowIndex) : null;

                    if (string.IsNullOrWhiteSpace(mtStr) || string.IsNullOrWhiteSpace(key) ||
                        !SemanticMediaType.TryParse(mtStr, out var mt))
                    {
                        writer.WriteNull(rowIndex);
                        continue;
                    }

                    writer.WriteValue(mt!.With(key, value).ToString(), rowIndex);
                }
            }
        );

        // ------------------- Snippet support UDFs -------------------

        // language_from_media_type_or_uri(media_type, uri) -> text?
        connection.RegisterScalarFunction<string, string, string>(
            "language_from_media_type_or_uri",
            (readers, writer, n) =>
            {
                var mtR = readers[0]; var uriR = readers[1];
                for (ulong i = 0; i < n; i++)
                {
                    string? lang = null;

                    // 1) Media type based
                    if (mtR.IsValid(i))
                    {
                        var mtStr = mtR.GetValue<string>(i);
                        if (!string.IsNullOrWhiteSpace(mtStr) && SemanticMediaType.TryParse(mtStr, out var mt))
                        {
                            var baseType = $"{mt!.Type}/{mt.Subtype}".ToLowerInvariant();
                            var subtype = mt.Subtype.ToLowerInvariant();
                            var kind = mt.Kind?.ToLowerInvariant();

                            // explicit kind wins
                            lang = kind switch
                            {
                                "csharp" or "cs" => "csharp",
                                "python" => "python",
                                "typescript" or "ts" => "ts",
                                "javascript" or "js" => "javascript",
                                "java" => "java",
                                "rust" => "rust",
                                "go" => "go",
                                "ruby" => "ruby",
                                "bash" or "shell" or "sh" => "bash",
                                "sql" => "sql",
                                "markdown" or "md" => "markdown",
                                "openapi" when baseType.Contains("yaml") => "yaml",
                                "openapi" when baseType.Contains("json") => "json",
                                _ => null
                            };

                            // infer from subtype (handles vendor x-…)
                            lang ??= subtype.Contains("csharp") ? "csharp" :
                                     subtype.Contains("typescript") ? "ts" :
                                     subtype.Contains("javascript") ? "javascript" :
                                     subtype.Contains("python") ? "python" :
                                     subtype.Contains("java") ? "java" :
                                     subtype.Contains("rust") ? "rust" :
                                     subtype.Contains("ruby") ? "ruby" :
                                     subtype.Contains("golang") || subtype == "go" ? "go" :
                                     subtype is "x-sh" or "x-shellscript" or "bash" ? "bash" :
                                     null;

                            // infer from base type
                            if (lang is null)
                            {
                                if (baseType.Contains("json")) lang = "json";
                                else if (baseType.Contains("yaml") || baseType.Contains("yml")) lang = "yaml";
                                else if (baseType.Contains("xml")) lang = "xml";
                                else if (baseType.Contains("markdown")) lang = "markdown";
                                else if (baseType is "text/x-csharp") lang = "csharp"; // specific test case
                            }
                        }
                    }

                    // 2) Extension fallback (container URI)
                    if (lang is null && uriR.IsValid(i))
                    {
                        var uri = uriR.GetValue<string>(i);
                        lang = TryLanguageFromPath(uri);
                    }

                    if (string.IsNullOrWhiteSpace(lang)) writer.WriteNull(i);
                    else writer.WriteValue(lang, i);
                }
            },
            isPureFunction: true
        );

        // line_for_byte_offset(text, byte_offset) -> int? (1-based)
        connection.RegisterScalarFunction<string, long, int>(
            "line_for_byte_offset",
            (readers, writer, n) =>
            {
                var tR = readers[0];
                var oR = readers[1];
                for (ulong i = 0; i < n; i++)
                {
                    if (!tR.IsValid(i) || !oR.IsValid(i))
                    {
                        writer.WriteNull(i);
                        continue;
                    }
                    var s = tR.GetValue<string>(i);
                    var off = oR.GetValue<long>(i);
                    if (off < 0)
                    {
                        writer.WriteNull(i);
                        continue;
                    }

                    var bytes = System.Text.Encoding.UTF8.GetBytes(s);
                    if (off > bytes.LongLength) off = bytes.LongLength;
                    var line = 1;
                    for (long j = 0; j < off; j++)
                        if (bytes[j] == (byte)'\n') line++;
                    writer.WriteValue(line, i);
                }
            },
            isPureFunction: true
        );

        // column_for_byte_offset(text, byte_offset) -> int? (1-based; CRLF-safe)
        connection.RegisterScalarFunction<string, long, int>(
            "column_for_byte_offset",
            (readers, writer, n) =>
            {
                var tR = readers[0]; var oR = readers[1];
                for (ulong i = 0; i < n; i++)
                {
                    if (!tR.IsValid(i) || !oR.IsValid(i)) { writer.WriteNull(i); continue; }

                    var s = tR.GetValue<string>(i);
                    var off = oR.GetValue<long>(i);
                    if (off < 0) { writer.WriteNull(i); continue; }

                    var bytes = Encoding.UTF8.GetBytes(s);
                    if (off > bytes.LongLength) off = bytes.LongLength;

                    // Find start of line (after last '\n' strictly before off)
                    long lastNl = -1;
                    for (long j = 0; j < off; j++) if (bytes[j] == (byte)'\n') lastNl = j;
                    var start = (int)(lastNl + 1);

                    // Walk UTF-8 code points up to 'off'
                    var pos = start;
                    var charsBefore = 0;
                    while (pos < off)
                    {
                        var span = bytes.AsSpan(pos, (int)(off - pos));
                        var status = Rune.DecodeFromUtf8(span, out _, out var consumed);
                        if (status != OperationStatus.Done || consumed <= 0)
                            break; // partial code point: do not advance into it
                        pos += consumed;
                        charsBefore++;
                    }
                    var atBoundary = (pos == off);

                    // If at a boundary: caret is before next code point → +1
                    // If inside a code point: caret is inside current glyph → do not +1
                    var col = atBoundary ? charsBefore + 1 : charsBefore;
                    if (col < 1) col = 1;

                    writer.WriteValue(col, i);
                }
            },
            isPureFunction: true
        );

        // binary_preview(storage_uri, max_bytes) -> text? (first N bytes hex+ASCII)
        connection.RegisterScalarFunction<string, int, string>(
            "binary_preview",
            (readers, writer, n) =>
            {
                var uR = readers[0];
                var mR = readers[1];
                for (ulong i = 0; i < n; i++)
                {
                    if (!uR.IsValid(i))
                    {
                        writer.WriteNull(i);
                        continue;
                    }
                    var u = uR.GetValue<string>(i);
                    var maxBytes = mR.IsValid(i) ? Math.Max(0, mR.GetValue<int>(i)) : 1024;
                    try
                    {
                        if (string.IsNullOrWhiteSpace(u) || !Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Scheme != "file")
                        {
                            writer.WriteNull(i);
                            continue;
                        }

                        var path = uri.LocalPath;
                        using var fs = System.IO.File.OpenRead(path);
                        var buf = new byte[Math.Min(maxBytes, (int)fs.Length)];
                        var read = fs.Read(buf, 0, buf.Length);
                        var sb = new System.Text.StringBuilder(read * 3);
                        for (var k = 0; k < read; k++)
                        {
                            if (k % 16 == 0) sb.AppendFormat("{0:X8}  ", k);
                            sb.AppendFormat("{0:X2} ", buf[k]);
                            if (k % 16 == 15 || k == read - 1)
                            {
                                var start = (k / 16) * 16;
                                sb.Append(" ");
                                for (var j = start; j <= k; j++)
                                {
                                    var c = buf[j] >= 32 && buf[j] <= 126 ? (char)buf[j] : '.';
                                    sb.Append(c);
                                }
                                sb.AppendLine();
                            }
                        }
                        writer.WriteValue(sb.ToString(), i);
                    }
                    catch
                    {
                        writer.WriteNull(i);
                    }
                }
            },
            isPureFunction: false // touches filesystem
        );

        // repository_uri_file_name(uri) -> text?
        connection.RegisterScalarFunction<string, string>(
            "repository_uri_file_name",
            (readers, writer, n) =>
            {
                var r0 = readers[0];
                for (ulong i = 0; i < n; i++)
                {
                    if (!r0.IsValid(i)) { writer.WriteNull(i); continue; }
                    var uri = r0.GetValue<string>(i);
                    if (string.IsNullOrWhiteSpace(uri)) { writer.WriteNull(i); continue; }
                    var hash = uri.IndexOf('#');
                    var container = hash >= 0 ? uri[..hash] : uri;
                    try
                    {
                        if (Uri.TryCreate(container, UriKind.Absolute, out var u))
                        {
                            var path = u.IsFile ? u.LocalPath : u.AbsolutePath;
                            var name = System.IO.Path.GetFileName(path);
                            writer.WriteValue(name, i);
                        }
                        else
                        {
                            var s = container.Replace('\\', '/');
                            var idx = s.LastIndexOf('/');
                            writer.WriteValue(idx >= 0 ? s[(idx + 1)..] : s, i);
                        }
                    }
                    catch { writer.WriteNull(i); }
                }
            },
            isPureFunction: true
        );

        // node_display_label(kind, properties_json) -> text?
        connection.RegisterScalarFunction<string, string, string>(
            "node_display_label",
            (readers, writer, n) =>
            {
                var kR = readers[0]; var pR = readers[1];
                for (ulong i = 0; i < n; i++)
                {
                    try
                    {
                        var kind = kR.IsValid(i) ? kR.GetValue<string>(i) : null;
                        var propsJson = pR.IsValid(i) ? pR.GetValue<string>(i) : null;
                        var label = (string?)null;
                        if (!string.IsNullOrWhiteSpace(propsJson))
                        {
                            var node = System.Text.Json.Nodes.JsonNode.Parse(propsJson) as System.Text.Json.Nodes.JsonObject;
                            if (node != null)
                            {
                                label = node["text"]?.GetValue<string?>()
                                     ?? node["name"]?.GetValue<string?>()
                                     ?? node["slug"]?.GetValue<string?>();
                            }
                        }
                        writer.WriteValue(label, i);
                    }
                    catch { writer.WriteNull(i); }
                }
            },
            isPureFunction: true
        );

        connection.RegisterScalarFunction<string, string, double>(
            "match_score",
            (readers, writer, n) =>
            {
                var patternReader = readers[0];
                var textReader = readers[1];
                for (ulong i = 0; i < n; i++)
                {
                    var pattern = patternReader.IsValid(i) ? patternReader.GetValue<string>(i) : null;
                    var text = textReader.IsValid(i) ? textReader.GetValue<string>(i) : null;
                    writer.WriteValue(ComputeMatchScore(pattern, text), i);
                }
            },
            isPureFunction: true
        );

        // embed_status() -> Key-value format showing runtime embedding provider state (no JSON, survives IL trimming)
        connection.RegisterScalarFunction<string>(
            "embed_status",
            (writer, n) =>
            {
                var providerType = _embeddingProvider?.GetType().Name ?? "null";
                var enabled = _embeddingProvider?.Enabled ?? false;
                var model = _embeddingProvider?.Model ?? "null";
                var dimension = _embeddingProvider?.Dimension ?? 0;

                var status = $"provider_type: {providerType}\nenabled: {enabled}\nmodel: {model}\ndimension: {dimension}";
                for (ulong i = 0; i < n; i++)
                    writer.WriteValue(status, i);
            },
            isPureFunction: false
        );

        // embed_text(text) -> array string for embedding (use ::FLOAT[] to cast)
        connection.RegisterScalarFunction<string, string>(
            "embed_text",
            (readers, writer, n) =>
            {
                if (_embeddingProvider is null || !_embeddingProvider.Enabled)
                {
                    for (ulong i = 0; i < n; i++)
                        writer.WriteNull(i);
                    return;
                }

                var r = readers[0];
                var textsToEmbed = new List<string>();
                var indexMap = new List<ulong>();

                for (ulong i = 0; i < n; i++)
                {
                    if (!r.IsValid(i)) continue;
                    var text = r.GetValue<string>(i);
                    if (string.IsNullOrEmpty(text)) continue;
                    textsToEmbed.Add(text);
                    indexMap.Add(i);
                }

                if (textsToEmbed.Count == 0)
                {
                    for (ulong i = 0; i < n; i++)
                        writer.WriteNull(i);
                    return;
                }

                float[]?[] vectors;
                try
                {
                    vectors = _embeddingProvider.EmbedBatchAsync(textsToEmbed, CancellationToken.None).GetAwaiter().GetResult();
                }
                catch
                {
                    for (ulong i = 0; i < n; i++)
                        writer.WriteNull(i);
                    return;
                }

                // Build a map of successful embeddings
                var resultMap = new Dictionary<ulong, string>();
                for (var batchIdx = 0; batchIdx < vectors.Length; batchIdx++)
                {
                    var vec = vectors[batchIdx];
                    if (vec is not null)
                        resultMap[indexMap[batchIdx]] = SerializeFloatArray(vec);
                }

                // Write each position exactly once
                for (ulong i = 0; i < n; i++)
                {
                    if (resultMap.TryGetValue(i, out var json))
                        writer.WriteValue(json, i);
                    else
                        writer.WriteNull(i);
                }
            },
            isPureFunction: false
        );

        // cosine_similarity_json(vecA, vecB) -> double (0..1), returns 0 if any invalid
        connection.RegisterScalarFunction<string, string, double>(
            "cosine_similarity_json",
            (readers, writer, n) =>
            {
                var aR = readers[0];
                var bR = readers[1];
                for (ulong i = 0; i < n; i++)
                {
                    try
                    {
                        if (!aR.IsValid(i) || !bR.IsValid(i)) { writer.WriteValue(0.0, i); continue; }
                        var aj = aR.GetValue<string>(i);
                        var bj = bR.GetValue<string>(i);
                        var av = ParseFloatArray(aj);
                        var bv = ParseFloatArray(bj);
                        if (av is null || bv is null || av.Length == 0 || bv.Length == 0 || av.Length != bv.Length)
                        {
                            writer.WriteValue(0.0, i);
                            continue;
                        }
                        var dot = 0.0;
                        var na = 0.0;
                        var nb = 0.0;
                        for (var k = 0; k < av.Length; k++)
                        {
                            var x = av[k];
                            var y = bv[k];
                            dot += x * y;
                            na += x * x;
                            nb += y * y;
                        }
                        var denom = Math.Sqrt(na) * Math.Sqrt(nb);
                        var cos = denom > 0 ? dot / denom : 0.0;
                        writer.WriteValue(cos, i);
                    }
                    catch { writer.WriteValue(0.0, i); }
                }
            },
            isPureFunction: true
        );

        // ------------------- Diagnostics UDF -------------------

        // indexing_diagnostics() -> Key-value format with indexing pipeline state (no JSON, survives IL trimming)
        connection.RegisterScalarFunction<string>(
            "indexing_diagnostics",
            (writer, n) =>
            {
                var text = RepoQL.Contracts.Diagnostics.IndexingDiagnostics.GetDiagnosticsText();
                for (ulong i = 0; i < n; i++)
                {
                    writer.WriteValue(text, i);
                }
            },
            isPureFunction: false // State changes between calls
        );

        // indexing_queue() -> JSON array of queued items
        // Usage: SELECT * FROM (SELECT unnest(from_json(indexing_queue(), '["json"]')) as item)
        // Or simpler: SELECT json_extract(value, '$.uri') as uri, ... FROM (SELECT unnest(indexing_queue()::json[]) as value)
        connection.RegisterScalarFunction<string>(
            "indexing_queue",
            (writer, n) =>
            {
                var items = RepoQL.Contracts.Diagnostics.IndexingDiagnostics.GetQueuedItems();
                var json = System.Text.Json.JsonSerializer.Serialize(items, new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower
                });
                for (ulong i = 0; i < n; i++)
                {
                    writer.WriteValue(json, i);
                }
            },
            isPureFunction: false
        );

        // ------------------- External Tool Integration UDF -------------------

        // _mcp_call_internal(server, tool, params_json) -> result_json
        // Internal UDF called by generated macros. Returns JSON result or error JSON.
        // The actual implementation is injected via SetExternalToolCaller().
        // IMPORTANT: params_json must be non-NULL (use '{}' for no params) - DuckDB skips UDF when all args are NULL
        // Usage: SELECT _mcp_call_internal('aspire-dashboard', 'list_resources', '{}')
        connection.RegisterScalarFunction<string, string, string, string>(
            "_mcp_call_internal",
            (readers, writer, n) =>
            {
                // Safely access readers - check bounds first
                if (readers.Count < 2)
                {
                    for (ulong i = 0; i < n; i++)
                        writer.WriteValue($"{{\"error\": \"Invalid reader count: {readers.Count}\"}}", i);
                    return;
                }

                var serverReader = readers[0];
                var toolReader = readers[1];
                // Third parameter may not exist if all calls pass NULL
                var paramsReader = readers.Count > 2 ? readers[2] : null;

                for (ulong i = 0; i < n; i++)
                {
                    try
                    {
                        // Validate required parameters
                        if (!serverReader.IsValid(i) || !toolReader.IsValid(i))
                        {
                            writer.WriteValue("{\"error\": \"Server and tool names are required\"}", i);
                            continue;
                        }

                        var server = serverReader.GetValue<string>(i);
                        var tool = toolReader.GetValue<string>(i);

                        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(tool))
                        {
                            writer.WriteValue("{\"error\": \"Server and tool names cannot be empty\"}", i);
                            continue;
                        }

                        // Check if external tool caller is configured
                        if (_externalToolCaller is null)
                        {
                            writer.WriteValue("{\"error\": \"External tool caller not configured\"}", i);
                            continue;
                        }

                        // Get optional parameters JSON (safely handle missing reader)
                        string? paramsJson = null;
                        if (paramsReader is not null && paramsReader.IsValid(i))
                        {
                            paramsJson = paramsReader.GetValue<string>(i);
                        }

                        // Call the external tool
                        var result = _externalToolCaller(server, tool, paramsJson);
                        // Ensure we never write null - DuckDB would convert to SQL NULL
                        writer.WriteValue(result ?? "{\"error\": \"Callback returned null\"}", i);
                    }
                    catch (Exception ex)
                    {
                        writer.WriteValue($"{{\"error\": \"{EscapeJsonString(ex.Message)}\"}}", i);
                    }
                }
            },
            isPureFunction: false // External side effects
        );

        // ------------------- LLM Integration UDFs -------------------

        // _llm_summarize_internal(json_data, intent, max_tokens) -> text
        // Internal UDF called by llm_summarize macro. Returns summary text.
        // The actual implementation is injected via SetLlmSummarizeCaller().
        connection.RegisterScalarFunction<string, string, int, string>(
            "_llm_summarize_internal",
            (readers, writer, n) =>
            {
                if (readers.Count < 3)
                {
                    for (ulong i = 0; i < n; i++)
                        writer.WriteValue("Error: Invalid reader count for _llm_summarize_internal", i);
                    return;
                }

                var jsonReader = readers[0];
                var intentReader = readers[1];
                var tokensReader = readers[2];

                for (ulong i = 0; i < n; i++)
                {
                    try
                    {
                        if (!jsonReader.IsValid(i) || !intentReader.IsValid(i))
                        {
                            writer.WriteValue("Error: json_data and intent are required", i);
                            continue;
                        }

                        if (_llmSummarizeCaller is null)
                        {
                            writer.WriteValue("LLM not configured (set OPENROUTER_API_KEY)", i);
                            continue;
                        }

                        var json = jsonReader.GetValue<string>(i);
                        var intent = intentReader.GetValue<string>(i);
                        var maxTokens = tokensReader.IsValid(i) ? tokensReader.GetValue<int>(i) : 500;

                        var result = _llmSummarizeCaller(json, intent, maxTokens);
                        writer.WriteValue(result ?? "No response from LLM", i);
                    }
                    catch (Exception ex)
                    {
                        writer.WriteValue($"Error: {ex.Message}", i);
                    }
                }
            },
            isPureFunction: false // External side effects (LLM API call)
        );

        // _llm_extract_internal(json_data, intent) -> markdown_text
        // Internal UDF called by llm_extract macro. Returns markdown report with snippets.
        // The actual implementation is injected via SetLlmExtractCaller().
        connection.RegisterScalarFunction<string, string, string>(
            "_llm_extract_internal",
            (readers, writer, n) =>
            {
                if (readers.Count < 2)
                {
                    for (ulong i = 0; i < n; i++)
                        writer.WriteValue("Error: Invalid reader count for _llm_extract_internal", i);
                    return;
                }

                var jsonReader = readers[0];
                var intentReader = readers[1];

                for (ulong i = 0; i < n; i++)
                {
                    try
                    {
                        if (!jsonReader.IsValid(i) || !intentReader.IsValid(i))
                        {
                            writer.WriteValue("Error: json_data and intent are required", i);
                            continue;
                        }

                        if (_llmExtractCaller is null)
                        {
                            writer.WriteValue("LLM not configured (set OPENROUTER_API_KEY)", i);
                            continue;
                        }

                        var json = jsonReader.GetValue<string>(i);
                        var intent = intentReader.GetValue<string>(i);

                        var result = _llmExtractCaller(json, intent);
                        writer.WriteValue(result ?? "No response from LLM", i);
                    }
                    catch (Exception ex)
                    {
                        writer.WriteValue($"Error: {ex.Message}", i);
                    }
                }
            },
            isPureFunction: false // External side effects (LLM API call)
        );

        // _xray_internal(keywords, intent, options_json) -> formatted_text
        // Internal UDF called by xray macro. Returns TOON-formatted xray output.
        // Resolves XrayOrchestrator from DI via DuckDbDataStore.GetService<T>().
        // options_json contains: tokens (int), scope, boost, penalize
        connection.RegisterScalarFunction<string, string, string, string>(
            "_xray_internal",
            (readers, writer, n) =>
            {
                for (ulong i = 0; i < n; i++)
                {
                    try
                    {
                        if (readers.Count < 2)
                        {
                            writer.WriteValue($"Error: Invalid reader count {readers.Count}", i);
                            continue;
                        }

                        var keywordsReader = readers[0];
                        var intentReader = readers[1];
                        var optionsReader = readers.Count > 2 ? readers[2] : null;

                        var keywords = keywordsReader.IsValid(i) ? keywordsReader.GetValue<string>(i) : "(no keywords)";
                        var intentStr = intentReader.IsValid(i) ? intentReader.GetValue<string>(i) : "Find";

                        // Parse options JSON for tokens, scope, boost, penalize
                        int tokens = 1000;

                        var orchestrator = DuckDbDataStore.GetService<XrayOrchestrator>();
                        if (orchestrator is null)
                        {
                            writer.WriteValue($"Error: XrayOrchestrator not available. keywords={keywords}, tokens={tokens}, intent={intentStr}", i);
                            continue;
                        }

                        // Parse options JSON for tokens, scope, boost, penalize
                        string? scope = null, boost = null, penalize = null;
                        if (optionsReader is not null && optionsReader.IsValid(i))
                        {
                            var optionsJson = optionsReader.GetValue<string>(i);
                            if (!string.IsNullOrWhiteSpace(optionsJson))
                            {
                                try
                                {
                                    var opts = System.Text.Json.JsonDocument.Parse(optionsJson);
                                    if (opts.RootElement.TryGetProperty("tokens", out var t) && t.TryGetInt32(out var tok)) tokens = tok;
                                    if (opts.RootElement.TryGetProperty("scope", out var s)) scope = s.GetString();
                                    if (opts.RootElement.TryGetProperty("boost", out var b)) boost = b.GetString();
                                    if (opts.RootElement.TryGetProperty("penalize", out var p)) penalize = p.GetString();
                                }
                                catch { /* Ignore JSON parse errors */ }
                            }
                        }

                        var intent = intentStr?.ToLowerInvariant() switch
                        {
                            "explore" => Intent.Explore,
                            "examine" => Intent.Examine,
                            _ => Intent.Find
                        };

                        var query = new XrayQuery(
                            TokenBudget: tokens,
                            Intent: intent,
                            Scope: scope,
                            Keywords: keywords,
                            Boost: boost,
                            Penalize: penalize,
                            Limit: null  // Let token budget + distribution decide
                        );

                        // Use a default IndexerStatus since we can't easily query diagnostics from within a UDF
                        var status = new IndexerStatus(0, true, true, 0);

                        var result = orchestrator.ExecuteAsync(query, status, CancellationToken.None)
                            .GetAwaiter().GetResult();

                        writer.WriteValue(result.RenderedOutput ?? "(empty result)", i);
                    }
                    catch (Exception ex)
                    {
                        writer.WriteValue($"Error: {ex.GetType().Name}: {ex.Message}", i);
                    }
                }
            },
            isPureFunction: false // External side effects (search engine calls)
        );

        // _xray_structured_internal(keywords, intent, options_json) -> JSON array
        // Internal UDF called by xray_structured table macro. Returns JSON array of results.
        // Resolves XrayOrchestrator from DI via DuckDbDataStore.GetService<T>().
        // options_json contains: tokens (int), scope, boost, penalize
        connection.RegisterScalarFunction<string, string, string, string>(
            "_xray_structured_internal",
            (readers, writer, n) =>
            {
                for (ulong i = 0; i < n; i++)
                {
                    try
                    {
                        if (readers.Count < 2)
                        {
                            writer.WriteValue("[]", i);
                            continue;
                        }

                        var keywordsReader = readers[0];
                        var intentReader = readers[1];
                        var optionsReader = readers.Count > 2 ? readers[2] : null;

                        var keywords = keywordsReader.IsValid(i) ? keywordsReader.GetValue<string>(i) : null;
                        var intentStr = intentReader.IsValid(i) ? intentReader.GetValue<string>(i) : "Find";

                        // Parse options JSON for tokens, scope, boost, penalize
                        int tokens = 1000;
                        string? scope = null, boost = null, penalize = null;
                        if (optionsReader is not null && optionsReader.IsValid(i))
                        {
                            var optionsJson = optionsReader.GetValue<string>(i);
                            if (!string.IsNullOrWhiteSpace(optionsJson))
                            {
                                try
                                {
                                    var opts = System.Text.Json.JsonDocument.Parse(optionsJson);
                                    if (opts.RootElement.TryGetProperty("tokens", out var t) && t.TryGetInt32(out var tok)) tokens = tok;
                                    if (opts.RootElement.TryGetProperty("scope", out var s)) scope = s.GetString();
                                    if (opts.RootElement.TryGetProperty("boost", out var b)) boost = b.GetString();
                                    if (opts.RootElement.TryGetProperty("penalize", out var p)) penalize = p.GetString();
                                }
                                catch { /* Ignore JSON parse errors */ }
                            }
                        }

                        var orchestrator = DuckDbDataStore.GetService<XrayOrchestrator>();
                        if (orchestrator is null)
                        {
                            writer.WriteValue("[]", i);
                            continue;
                        }

                        var intent = intentStr?.ToLowerInvariant() switch
                        {
                            "explore" => Intent.Explore,
                            "examine" => Intent.Examine,
                            _ => Intent.Find
                        };

                        var query = new XrayQuery(
                            TokenBudget: tokens,
                            Intent: intent,
                            Scope: scope,
                            Keywords: keywords,
                            Boost: boost,
                            Penalize: penalize,
                            Limit: null
                        );

                        var status = new IndexerStatus(0, true, true, 0);
                        var result = orchestrator.ExecuteAsync(query, status, CancellationToken.None)
                            .GetAwaiter().GetResult();

                        // Convert results to JSON array
                        var jsonArray = SerializeXrayResultsToJson(result.Results);
                        writer.WriteValue(jsonArray, i);
                    }
                    catch (Exception ex)
                    {
                        // Return empty array on error to avoid breaking queries
                        writer.WriteValue($"[]", i);
                    }
                }
            },
            isPureFunction: false
        );
    }

    /// <summary>
    /// Serialize xray results to a JSON array for structured output.
    /// Flattens the hierarchy - documents with their child objects as separate rows.
    /// </summary>
    private static string SerializeXrayResultsToJson(IReadOnlyList<XrayResult> results)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('[');
        bool first = true;

        void WriteResult(XrayResult r, string? parentUri, int depth)
        {
            if (!first) sb.Append(',');
            first = false;

            sb.Append('{');
            sb.Append($"\"uri\":\"{EscapeJsonString(r.Uri)}\",");
            sb.Append($"\"confidence\":{r.Confidence},");
            sb.Append($"\"kind\":{(r.Kind is null ? "null" : $"\"{EscapeJsonString(r.Kind)}\"")},");
            sb.Append($"\"headline\":{(r.Headline is null ? "null" : $"\"{EscapeJsonString(r.Headline)}\"")},");
            sb.Append($"\"structure\":{(r.Structure is null ? "null" : $"\"{EscapeJsonString(r.Structure)}\"")},");
            sb.Append($"\"snippet\":{(r.Snippet is null ? "null" : $"\"{EscapeJsonString(r.Snippet)}\"")},");
            sb.Append($"\"lang\":{(r.Lang is null ? "null" : $"\"{EscapeJsonString(r.Lang)}\"")},");
            sb.Append($"\"semantic_type\":{(r.SemanticType is null ? "null" : $"\"{EscapeJsonString(r.SemanticType)}\"")},");
            sb.Append($"\"parent_uri\":{(parentUri is null ? "null" : $"\"{EscapeJsonString(parentUri)}\"")},");
            sb.Append($"\"depth\":{depth}");
            sb.Append('}');

            // Recurse into children
            if (r.ChildObjects is { Count: > 0 })
            {
                foreach (var child in r.ChildObjects)
                {
                    WriteResult(child, r.Uri, depth + 1);
                }
            }
        }

        foreach (var r in results)
        {
            WriteResult(r, null, 0);
        }

        sb.Append(']');
        return sb.ToString();
    }

    private static string EscapeJsonString(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 32)
                        sb.Append($"\\u{(int)c:X4}");
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    // ------------------- private helpers -------------------

    private static double ComputeMatchScore(string? pattern, string? text)
    {
        var patSpan = (pattern ?? string.Empty).AsSpan();
        var txtSpan = (text ?? string.Empty).AsSpan();

        if (patSpan.Length == 0)
            return 1.0;
        if (txtSpan.Length == 0 || patSpan.Length > txtSpan.Length)
            return 0.0;

        int[]? rented = null;
        Span<int> positions = patSpan.Length <= 256
            ? stackalloc int[patSpan.Length]
            : (rented = ArrayPool<int>.Shared.Rent(patSpan.Length)).AsSpan(0, patSpan.Length);

        try
        {
            var matched = 0;
            var searchIndex = 0;
            for (var i = 0; i < patSpan.Length; i++)
            {
                var target = char.ToLowerInvariant(patSpan[i]);
                var found = false;
                for (; searchIndex < txtSpan.Length; searchIndex++)
                {
                    if (target == char.ToLowerInvariant(txtSpan[searchIndex]))
                    {
                        positions[matched++] = searchIndex++;
                        found = true;
                        break;
                    }
                }

                if (!found)
                    return 0.0;
            }

            var score = 0.0;
            var prev = -1;
            for (var i = 0; i < matched; i++)
            {
                var current = positions[i];
                var s = 1.0;
                if (prev >= 0)
                {
                    var gap = current - prev - 1;
                    s += gap == 0 ? 1.5 : -Math.Min(gap, 32) * 0.04;
                }

                if (IsBoundary(txtSpan, current)) s += 0.8;
                if (current == 0) s += 0.3;

                score += s;
                prev = current;
            }

            score -= Math.Max(0, txtSpan.Length - matched) * 0.005;
            return Math.Clamp(score / matched, 0.0, 5.0);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<int>.Shared.Return(rented);
            }
        }
    }

    private static float[]? ParseFloatArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
            var arr = new float[doc.RootElement.GetArrayLength()];
            var idx = 0;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                arr[idx++] = el.TryGetSingle(out var f) ? f : (float)el.GetDouble();
            }
            return arr;
        }
        catch { return null; }
    }

    /// <summary>
    /// Serializes float array to JSON array format [1.0,2.0,...].
    /// Use result::FLOAT[] in SQL to convert back.
    /// </summary>
    private static string SerializeFloatArray(float[] vec)
    {
        if (vec == null || vec.Length == 0) return "[]";

        var sb = new StringBuilder(vec.Length * 10 + 2); // Pre-size for efficiency
        sb.Append('[');
        for (var i = 0; i < vec.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(vec[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        sb.Append(']');
        return sb.ToString();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsBoundary(ReadOnlySpan<char> text, int index)
    {
        if (index == 0) return true;
        var prev = text[index - 1];
        var current = text[index];
        return IsSeparator(prev) || (char.IsLower(prev) && char.IsUpper(current));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSeparator(char c)
        => c is '/' or '\\' or '_' or '-' or ' ' or '.';

    private static string? TryLanguageFromPath(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return null;

        // Strip fragment and nested container (e.g., jar:…!/path)
        var s = uri;
        var hash = s.IndexOf('#');
        if (hash >= 0) s = s.Substring(0, hash);
        var bang = s.IndexOf('!');
        if (bang >= 0 && bang < s.Length - 1) s = s.Substring(bang + 1);

        string path;
        if (Uri.TryCreate(s, UriKind.Absolute, out var u))
            path = u.IsFile ? u.LocalPath : u.AbsolutePath;
        else
            path = s;

        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".cs" or ".csx" => "csharp",
            ".ts" or ".tsx" => "ts",
            ".js" or ".jsx" => "javascript",
            ".py" => "python",
            ".java" => "java",
            ".go" => "go",
            ".rs" => "rust",
            ".rb" => "ruby",
            ".c" or ".h" => "c",
            ".cpp" or ".cc" or ".cxx" or ".hpp" => "cpp",
            ".sql" => "sql",
            ".sh" or ".bash" => "bash",
            ".md" => "markdown",
            ".json" => "json",
            ".yaml" or ".yml" => "yaml",
            ".xml" => "xml",
            _ => null
        };
    }

    private static bool ScalarFunctionExists(DuckDBConnection connection, string functionName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM duckdb_functions() WHERE lower(function_name) = lower(?)";
        cmd.Parameters.Add(new DuckDBParameter { Value = functionName });
        var result = cmd.ExecuteScalar();
        if (result is long l) return l > 0;
        if (result is int i) return i > 0;
        if (result is decimal dec) return dec > 0;
        return Convert.ToInt64(result) > 0;
    }

    private static string? ExtractFragment(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return null;
        var hash = uri.IndexOf('#');
        if (hash < 0 || hash == uri.Length - 1) return null;
        return uri[(hash + 1)..];
    }

    private static string? ExtractKeyPayload(string fragment, string key, string prefix)
    {
        if (fragment.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return fragment[prefix.Length..];

        switch (fragment.IndexOf('='))
        {
            case >= 0:
                {
                    var pairs = fragment.Split('&', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var p in pairs)
                    {
                        var kv = p.Split('=', 2);
                        if (kv.Length == 0) continue;
                        if (kv[0].Equals(key, StringComparison.OrdinalIgnoreCase))
                            return kv.Length == 2 ? kv[1] : string.Empty;
                    }

                    break;
                }
        }

        return null;
    }
}
