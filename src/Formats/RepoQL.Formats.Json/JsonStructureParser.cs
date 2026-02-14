using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RepoQL.Formats.Json;

/// <summary>
/// Parses JSON into a structural key tree without allocating a DOM.
///
/// Purpose: Produce JSON Pointer paths, line spans, and lightweight shape metadata for downstream loaders.
///
/// Complexity: Traverses UTF-8 token streams with sampling, JSONL mode, pointer state tracking, and byte-domain line mapping.
/// </summary>
public sealed class JsonStructureParser
{
    private const int ScalarValueLimit = 100;

    private readonly record struct PathSegment(string? Name, bool IsArray, int ArrayIndex);
    private readonly record struct RootArrayParseResult(int? ArrayLength, bool WasSampled);

    private sealed class ParseAccumulator(int[] lineStarts, JsonParseOptions options)
    {
        public int[] LineStarts { get; } = lineStarts;
        public JsonParseOptions Options { get; } = options;
        public List<JsonKeyInfo> Keys { get; } = [];
        public int TotalKeyCount { get; set; }
        public int MaxDepth { get; set; }
        public int EligibleNodeCount { get; set; }
    }

    public JsonParseResult Parse(string text, JsonParseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        var utf8Bytes = Encoding.UTF8.GetBytes(text);
        return Parse(utf8Bytes, options);
    }

    public JsonParseResult Parse(ReadOnlySpan<byte> utf8Bytes, JsonParseOptions? options = null)
    {
        var parseOptions = NormalizeOptions(options ?? new JsonParseOptions());

        if (IsWhitespaceOnly(utf8Bytes))
        {
            return JsonParseResult.Empty;
        }

        var lineStarts = BuildLineStarts(utf8Bytes);

        return parseOptions.IsJsonl
            ? ParseJsonLines(utf8Bytes, lineStarts, parseOptions)
            : ParseJsonDocument(utf8Bytes, lineStarts, parseOptions);
    }

    private static JsonParseResult ParseJsonDocument(ReadOnlySpan<byte> utf8Bytes, int[] lineStarts, JsonParseOptions options)
    {
        var accumulator = new ParseAccumulator(lineStarts, options);
        var pathSegments = new List<PathSegment>(8);
        var reader = new Utf8JsonReader(utf8Bytes, isFinalBlock: true, state: default);

        if (!reader.Read())
        {
            return JsonParseResult.Empty;
        }

        JsonShape shape;
        int? arrayLength = null;

        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                var hasRootContainerValues = ParseObject(
                    ref reader,
                    pathSegments,
                    accumulator,
                    offsetBase: 0,
                    isRootObject: true);

                EnsureNoTrailingTokens(ref reader);
                shape = hasRootContainerValues ? JsonShape.NestedObject : JsonShape.FlatObject;
                break;

            case JsonTokenType.StartArray:
                var rootArrayResult = ParseRootArray(
                    ref reader,
                    utf8Bytes.Length,
                    pathSegments,
                    accumulator,
                    offsetBase: 0);

                if (!rootArrayResult.WasSampled)
                {
                    EnsureNoTrailingTokens(ref reader);
                }

                shape = JsonShape.Array;
                arrayLength = rootArrayResult.ArrayLength;
                break;

            case JsonTokenType.String:
            case JsonTokenType.Number:
            case JsonTokenType.True:
            case JsonTokenType.False:
            case JsonTokenType.Null:
                EnsureNoTrailingTokens(ref reader);
                shape = JsonShape.SingleValue;
                break;

            default:
                throw new JsonException($"Unexpected root token '{reader.TokenType}'.");
        }

        return new JsonParseResult
        {
            Shape = shape,
            Keys = accumulator.Keys,
            TotalKeyCount = accumulator.TotalKeyCount,
            MaxDepth = accumulator.MaxDepth,
            ArrayLength = arrayLength
        };
    }

    private static JsonParseResult ParseJsonLines(ReadOnlySpan<byte> utf8Bytes, int[] lineStarts, JsonParseOptions options)
    {
        var accumulator = new ParseAccumulator(lineStarts, options);
        var sampledLineCount = 0;
        long sampledLineBytes = 0;
        var arrayIndex = 0;
        var lineStart = 0;
        var sampled = false;

        for (var i = 0; i <= utf8Bytes.Length; i++)
        {
            var isLineBreak = i == utf8Bytes.Length || utf8Bytes[i] == 0x0A;
            if (!isLineBreak)
            {
                continue;
            }

            var line = utf8Bytes.Slice(lineStart, i - lineStart);
            if (line.Length > 0 && line[^1] == 0x0D)
            {
                line = line[..^1];
            }

            if (!IsWhitespaceOnly(line))
            {
                if (sampledLineCount >= options.MaxSampleRecords)
                {
                    sampled = true;
                    break;
                }

                sampledLineCount++;
                sampledLineBytes += line.Length;

                ParseJsonlLine(line, lineStart, arrayIndex, accumulator);
                arrayIndex++;
            }

            lineStart = i + 1;
        }

        var arrayLength = sampled
            ? EstimateCollectionLength(utf8Bytes.Length, sampledLineBytes, sampledLineCount)
            : arrayIndex;

        return new JsonParseResult
        {
            Shape = JsonShape.Array,
            Keys = accumulator.Keys,
            TotalKeyCount = accumulator.TotalKeyCount,
            MaxDepth = accumulator.MaxDepth,
            ArrayLength = arrayLength
        };
    }

    private static void ParseJsonlLine(
        ReadOnlySpan<byte> lineBytes,
        int offsetBase,
        int arrayIndex,
        ParseAccumulator accumulator)
    {
        var reader = new Utf8JsonReader(lineBytes, isFinalBlock: true, state: default);
        var pathSegments = new List<PathSegment>(4)
        {
            new(Name: null, IsArray: true, ArrayIndex: arrayIndex)
        };

        try
        {
            if (!reader.Read())
            {
                return;
            }

            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    _ = ParseObject(ref reader, pathSegments, accumulator, offsetBase, isRootObject: false);
                    break;

                case JsonTokenType.StartArray:
                    _ = ParseArray(ref reader, pathSegments, accumulator, offsetBase);
                    break;

                case JsonTokenType.String:
                case JsonTokenType.Number:
                case JsonTokenType.True:
                case JsonTokenType.False:
                case JsonTokenType.Null:
                    break;

                default:
                    throw new JsonException($"Unexpected JSONL token '{reader.TokenType}'.");
            }

            EnsureNoTrailingTokens(ref reader);
        }
        catch (JsonException)
        {
            // JSONL mode tolerates malformed sampled lines.
        }
    }

    private static bool ParseObject(
        ref Utf8JsonReader reader,
        List<PathSegment> pathSegments,
        ParseAccumulator accumulator,
        long offsetBase,
        bool isRootObject)
    {
        var hasRootContainerValues = false;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return hasRootContainerValues;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException($"Expected PropertyName, found '{reader.TokenType}'.");
            }

            var propertyName = reader.GetString() ?? string.Empty;
            var propertyStartOffset = offsetBase + reader.TokenStartIndex;

            pathSegments.Add(new PathSegment(propertyName, IsArray: false, ArrayIndex: -1));

            if (!reader.Read())
            {
                throw new JsonException("Unexpected end of JSON while reading property value.");
            }

            var valueKind = ToValueKind(reader.TokenType);
            if (isRootObject && IsContainerKind(valueKind))
            {
                hasRootContainerValues = true;
            }

            var depth = pathSegments.Count - 1;
            accumulator.TotalKeyCount++;
            accumulator.MaxDepth = Math.Max(accumulator.MaxDepth, depth);

            var startLine = ResolveLine(propertyStartOffset, accumulator.LineStarts);
            var isNodeEligible = IsNodeEligible(depth, valueKind, accumulator);

            int endLine;
            int estimatedTokens;
            string? scalarValue = null;
            int? arrayLength = null;

            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                {
                    var objectStart = offsetBase + reader.TokenStartIndex;
                    _ = ParseObject(ref reader, pathSegments, accumulator, offsetBase, isRootObject: false);
                    var objectEndExclusive = offsetBase + reader.BytesConsumed;
                    estimatedTokens = EstimateTokens(objectEndExclusive - objectStart);
                    endLine = ResolveLine(offsetBase + reader.TokenStartIndex, accumulator.LineStarts);
                    break;
                }

                case JsonTokenType.StartArray:
                {
                    var arrayStart = offsetBase + reader.TokenStartIndex;
                    arrayLength = ParseArray(ref reader, pathSegments, accumulator, offsetBase);
                    var arrayEndExclusive = offsetBase + reader.BytesConsumed;
                    estimatedTokens = EstimateTokens(arrayEndExclusive - arrayStart);
                    endLine = ResolveLine(offsetBase + reader.TokenStartIndex, accumulator.LineStarts);
                    break;
                }

                default:
                    endLine = ResolveLine(offsetBase + reader.TokenStartIndex, accumulator.LineStarts);
                    estimatedTokens = EstimateTokens(GetScalarByteLength(reader));
                    scalarValue = GetScalarValue(reader);
                    break;
            }

            accumulator.Keys.Add(new JsonKeyInfo
            {
                Path = BuildJsonPointer(pathSegments),
                Name = propertyName,
                Depth = depth,
                ValueKind = valueKind,
                StartLine = startLine,
                EndLine = endLine,
                EstimatedTokens = estimatedTokens,
                ScalarValue = scalarValue,
                ArrayLength = arrayLength,
                IsNodeEligible = isNodeEligible
            });

            pathSegments.RemoveAt(pathSegments.Count - 1);
        }

        throw new JsonException("Unexpected end of JSON object.");
    }

    private static int ParseArray(
        ref Utf8JsonReader reader,
        List<PathSegment> pathSegments,
        ParseAccumulator accumulator,
        long offsetBase)
    {
        var index = 0;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return index;
            }

            pathSegments.Add(new PathSegment(Name: null, IsArray: true, ArrayIndex: index));
            ParseValue(ref reader, pathSegments, accumulator, offsetBase);
            pathSegments.RemoveAt(pathSegments.Count - 1);
            index++;
        }

        throw new JsonException("Unexpected end of JSON array.");
    }

    private static RootArrayParseResult ParseRootArray(
        ref Utf8JsonReader reader,
        int totalBytes,
        List<PathSegment> pathSegments,
        ParseAccumulator accumulator,
        long offsetBase)
    {
        var sampledCount = 0;
        long sampledBytes = 0;
        var index = 0;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return new RootArrayParseResult(index, WasSampled: false);
            }

            if (sampledCount >= accumulator.Options.MaxSampleRecords)
            {
                var estimatedLength = EstimateCollectionLength(totalBytes, sampledBytes, sampledCount);
                return new RootArrayParseResult(estimatedLength, WasSampled: true);
            }

            var elementStart = offsetBase + reader.TokenStartIndex;

            pathSegments.Add(new PathSegment(Name: null, IsArray: true, ArrayIndex: index));
            ParseValue(ref reader, pathSegments, accumulator, offsetBase);
            pathSegments.RemoveAt(pathSegments.Count - 1);

            var elementEndExclusive = offsetBase + reader.BytesConsumed;
            sampledBytes += Math.Max(1, elementEndExclusive - elementStart);
            sampledCount++;
            index++;
        }

        throw new JsonException("Unexpected end of JSON array.");
    }

    private static void ParseValue(
        ref Utf8JsonReader reader,
        List<PathSegment> pathSegments,
        ParseAccumulator accumulator,
        long offsetBase)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                _ = ParseObject(ref reader, pathSegments, accumulator, offsetBase, isRootObject: false);
                break;

            case JsonTokenType.StartArray:
                _ = ParseArray(ref reader, pathSegments, accumulator, offsetBase);
                break;

            case JsonTokenType.String:
            case JsonTokenType.Number:
            case JsonTokenType.True:
            case JsonTokenType.False:
            case JsonTokenType.Null:
                break;

            default:
                throw new JsonException($"Unexpected token '{reader.TokenType}'.");
        }
    }

    private static JsonParseOptions NormalizeOptions(JsonParseOptions options)
    {
        var normalizedSample = options.MaxSampleRecords <= 0 ? 1 : options.MaxSampleRecords;
        var normalizedDepth = options.MaxNodeDepth < 0 ? 0 : options.MaxNodeDepth;
        var normalizedNodes = options.MaxNodes < 0 ? 0 : options.MaxNodes;

        if (normalizedSample == options.MaxSampleRecords
            && normalizedDepth == options.MaxNodeDepth
            && normalizedNodes == options.MaxNodes)
        {
            return options;
        }

        return options with
        {
            MaxSampleRecords = normalizedSample,
            MaxNodeDepth = normalizedDepth,
            MaxNodes = normalizedNodes
        };
    }

    private static int[] BuildLineStarts(ReadOnlySpan<byte> utf8Bytes)
    {
        var starts = new List<int>(Math.Max(4, utf8Bytes.Length / 32))
        {
            0
        };

        for (var i = 0; i < utf8Bytes.Length; i++)
        {
            if (utf8Bytes[i] == 0x0A)
            {
                starts.Add(i + 1);
            }
        }

        return [.. starts];
    }

    private static int ResolveLine(long byteOffset, int[] lineStarts)
    {
        if (lineStarts.Length == 0)
        {
            return 1;
        }

        var safeOffset = byteOffset switch
        {
            < 0 => 0,
            > int.MaxValue => int.MaxValue,
            _ => (int)byteOffset
        };

        var lineIndex = Array.BinarySearch(lineStarts, safeOffset);
        if (lineIndex >= 0)
        {
            return lineIndex + 1;
        }

        lineIndex = ~lineIndex - 1;
        if (lineIndex < 0)
        {
            lineIndex = 0;
        }

        return lineIndex + 1;
    }

    private static string BuildJsonPointer(List<PathSegment> pathSegments)
    {
        if (pathSegments.Count == 0)
        {
            return string.Empty;
        }

        var pointer = new StringBuilder(pathSegments.Count * 8);
        foreach (var segment in pathSegments)
        {
            pointer.Append('/');

            if (segment.IsArray)
            {
                pointer.Append(segment.ArrayIndex.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                pointer.Append(EscapePointerSegment(segment.Name ?? string.Empty));
            }
        }

        return pointer.ToString();
    }

    private static string EscapePointerSegment(string segment)
        => segment.Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);

    private static JsonValueKind ToValueKind(JsonTokenType tokenType)
    {
        return tokenType switch
        {
            JsonTokenType.StartObject => JsonValueKind.Object,
            JsonTokenType.StartArray => JsonValueKind.Array,
            JsonTokenType.String => JsonValueKind.String,
            JsonTokenType.Number => JsonValueKind.Number,
            JsonTokenType.True => JsonValueKind.True,
            JsonTokenType.False => JsonValueKind.False,
            JsonTokenType.Null => JsonValueKind.Null,
            _ => throw new JsonException($"Token '{tokenType}' is not a JSON value token.")
        };
    }

    private static bool IsContainerKind(JsonValueKind kind)
        => kind is JsonValueKind.Object or JsonValueKind.Array;

    private static bool IsNodeEligible(int depth, JsonValueKind valueKind, ParseAccumulator accumulator)
    {
        if (accumulator.EligibleNodeCount >= accumulator.Options.MaxNodes)
        {
            return false;
        }

        var isContainer = IsContainerKind(valueKind);
        var depthAllowsNode = depth < accumulator.Options.MaxNodeDepth
                              || (depth >= accumulator.Options.MaxNodeDepth && isContainer);

        if (!depthAllowsNode)
        {
            return false;
        }

        accumulator.EligibleNodeCount++;
        return true;
    }

    private static string? GetScalarValue(in Utf8JsonReader reader)
    {
        var value = reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => GetRawValueText(reader),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            _ => null
        };

        if (value is not null && value.Length > ScalarValueLimit)
        {
            return value[..ScalarValueLimit];
        }

        return value;
    }

    private static string GetRawValueText(in Utf8JsonReader reader)
    {
        if (reader.HasValueSequence)
        {
            var bytes = reader.ValueSequence.ToArray();
            return Encoding.UTF8.GetString(bytes);
        }

        return Encoding.UTF8.GetString(reader.ValueSpan);
    }

    private static long GetScalarByteLength(in Utf8JsonReader reader)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String or JsonTokenType.Number => reader.HasValueSequence
                ? reader.ValueSequence.Length
                : reader.ValueSpan.Length,
            JsonTokenType.True => 4,
            JsonTokenType.False => 5,
            JsonTokenType.Null => 4,
            _ => 0
        };
    }

    private static int EstimateTokens(long byteLength)
    {
        if (byteLength <= 0)
        {
            return 0;
        }

        var estimate = byteLength / 4;
        return estimate > int.MaxValue ? int.MaxValue : (int)estimate;
    }

    private static int EstimateCollectionLength(int totalBytes, long sampledBytes, int sampledCount)
    {
        if (sampledCount <= 0 || sampledBytes <= 0)
        {
            return sampledCount;
        }

        var averageItemBytes = sampledBytes / (double)sampledCount;
        if (averageItemBytes <= 0)
        {
            return sampledCount;
        }

        var estimate = (int)Math.Round(totalBytes / averageItemBytes, MidpointRounding.AwayFromZero);
        return Math.Max(sampledCount, estimate);
    }

    private static bool IsWhitespaceOnly(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (!IsWhitespace(value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsWhitespace(byte value)
        => value is 0x20 or 0x09 or 0x0A or 0x0D;

    private static void EnsureNoTrailingTokens(ref Utf8JsonReader reader)
    {
        if (reader.Read())
        {
            throw new JsonException("Additional content found after the root JSON value.");
        }
    }
}
