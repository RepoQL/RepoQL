using DuckDB.NET.Data;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using RepoQL.Contracts;
using RepoQL.Metrics;

#pragma warning disable DuckDBNET001

namespace RepoQL.Data.DuckDB;

/// <summary>
///     Registers repository URI and semantic media type scalar UDFs for DuckDB.NET.
///     All functions are pure and null-safe. Use once per connection.
/// </summary>
public static class RepositoryUserDefinedFunctions
{
    /// <summary>
    ///     Registers all scalar UDFs on the provided open connection.
    /// </summary>
    public static void RegisterAll(
        DuckDBConnection connection,
        IndexingMetrics metrics,
        RepoQL.Contracts.Embeddings.IEmbeddingProvider? embeddingProvider = null)
    {
        if (ScalarFunctionExists(connection, "repository_uri_container"))
        {
            return;
        }

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

        // embed_text_json(text) -> JSON string of float[] or NULL if provider disabled
        connection.RegisterScalarFunction<string, string>(
            "embed_text_json",
            (readers, writer, n) =>
            {
                var r = readers[0];
                for (ulong i = 0; i < n; i++)
                {
                    try
                    {
                        if (embeddingProvider is null || !embeddingProvider.Enabled || !r.IsValid(i))
                        {
                            writer.WriteNull(i);
                            continue;
                        }
                        var text = r.GetValue<string>(i) ?? string.Empty;
                        // Synchronous wait acceptable in vectorized UDF for small N; provider should be fast
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var vec = embeddingProvider.EmbedAsync(text).GetAwaiter().GetResult();
                        sw.Stop();
                        if (vec is null)
                        {
                            writer.WriteNull(i);
                            metrics.EmbedErrors.Add(1, new TagList { { "source", "query-udf" }, { "model", embeddingProvider.Model }, { "dim", embeddingProvider.Dimension } });
                            metrics.EmbedRequests.Add(1, new TagList { { "source", "query-udf" }, { "model", embeddingProvider.Model }, { "dim", embeddingProvider.Dimension }, { "status", "error" } });
                            metrics.EmbedDuration.Record(sw.Elapsed.TotalMilliseconds, new TagList { { "source", "query-udf" }, { "model", embeddingProvider.Model }, { "dim", embeddingProvider.Dimension }, { "status", "error" } });
                            continue;
                        }
                        var json = SerializeFloatArray(vec);
                        writer.WriteValue(json, i);
                        metrics.EmbedRequests.Add(1, new TagList { { "source", "query-udf" }, { "model", embeddingProvider.Model }, { "dim", embeddingProvider.Dimension }, { "status", "ok" } });
                        metrics.EmbedDuration.Record(sw.Elapsed.TotalMilliseconds, new TagList { { "source", "query-udf" }, { "model", embeddingProvider.Model }, { "dim", embeddingProvider.Dimension }, { "status", "ok" } });
                    }
                    catch
                    {
                        writer.WriteNull(i);
                        if (embeddingProvider is not null)
                        {
                            metrics.EmbedErrors.Add(1, new TagList { { "source", "query-udf" }, { "model", embeddingProvider.Model }, { "dim", embeddingProvider.Dimension } });
                            metrics.EmbedRequests.Add(1, new TagList { { "source", "query-udf" }, { "model", embeddingProvider.Model }, { "dim", embeddingProvider.Dimension }, { "status", "exception" } });
                            metrics.EmbedDuration.Record(0, new TagList { { "source", "query-udf" }, { "model", embeddingProvider.Model }, { "dim", embeddingProvider.Dimension }, { "status", "exception" } });
                        }
                    }
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

    private static string SerializeFloatArray(float[] vec)
    {
        using var ms = new MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(ms))
        {
            w.WriteStartArray();
            for (var i = 0; i < vec.Length; i++)
            {
                w.WriteNumberValue(vec[i]);
            }
            w.WriteEndArray();
            w.Flush();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
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
