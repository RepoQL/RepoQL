using System.Text;
using AwesomeAssertions;

namespace RepoQL.Formats.Json.Tests;

public sealed class JsonNormalizerTests
{
    [Test]
    [DisplayName("Strict JSON without comments leaves bytes unchanged")]
    public void StripComments_StrictJson_LeavesBytesUnchanged()
    {
        const string json = """
        {
          "name": "repoql",
          "emoji": "😀",
          "nested": { "enabled": true }
        }
        """;

        var input = Encoding.UTF8.GetBytes(json);
        var baseline = input.ToArray();

        JsonNormalizer.StripComments(input);

        input.Should().Equal(baseline);
    }

    [Test]
    [DisplayName("Line comments are replaced with spaces up to LF")]
    public void StripComments_LineComment_ReplacesCommentBytesWithSpaces()
    {
        var input = Encoding.UTF8.GetBytes("1//abc\n2");
        var expected = Encoding.UTF8.GetBytes("1     \n2");

        JsonNormalizer.StripComments(input);

        input.Should().Equal(expected);
    }

    [Test]
    [DisplayName("Block comments are replaced with spaces including delimiters")]
    public void StripComments_BlockComment_ReplacesCommentBytesWithSpaces()
    {
        var input = Encoding.UTF8.GetBytes("1/*abc*/2");
        var expected = Encoding.UTF8.GetBytes("1       2");

        JsonNormalizer.StripComments(input);

        input.Should().Equal(expected);
    }

    [Test]
    [DisplayName("Multi-line block comments preserve LF and CR positions")]
    public void StripComments_MultiLineBlockComment_PreservesLineBreakPositions()
    {
        var input = Encoding.UTF8.GetBytes("1/*a\nb\r\nc*/2");
        var baselineLf = FindBytePositions(input, 0x0A);
        var baselineCr = FindBytePositions(input, 0x0D);

        JsonNormalizer.StripComments(input);

        FindBytePositions(input, 0x0A).Should().Equal(baselineLf);
        FindBytePositions(input, 0x0D).Should().Equal(baselineCr);
    }

    [Test]
    [DisplayName("Comment-like sequences inside strings are preserved")]
    public void StripComments_CommentSyntaxInsideStrings_Preserved()
    {
        const string json = """
        {
          "url": "http://example.com",
          "pattern": "/* not a comment */",
          "tail": "value // still string"
        }
        """;

        var input = Encoding.UTF8.GetBytes(json);
        var baseline = input.ToArray();

        JsonNormalizer.StripComments(input);

        input.Should().Equal(baseline);
    }

    [Test]
    [DisplayName("Escaped quote and escaped backslash before quote are handled correctly")]
    public void StripComments_EscapedQuoteAndBackslashQuote_HandledCorrectly()
    {
        const string json = """
        {
          "value": "keep // inside with escaped quote \" and pair \\",
          "next": 1 // strip this
        }
        """;

        var input = Encoding.UTF8.GetBytes(json);
        var insidePattern = Encoding.UTF8.GetBytes("// inside");
        var insideStart = IndexOfSequence(input, insidePattern);
        insideStart.Should().BeGreaterThan(0);
        var insideSnapshot = input.Skip(insideStart).Take(insidePattern.Length).ToArray();

        var commentStart = IndexOfSequence(input, Encoding.UTF8.GetBytes("// strip this"));
        commentStart.Should().BeGreaterThan(0);

        JsonNormalizer.StripComments(input);

        input.Skip(insideStart).Take(insidePattern.Length).Should().Equal(insideSnapshot);

        var lineFeedIndex = Array.IndexOf(input, (byte)0x0A, commentStart);
        lineFeedIndex.Should().BeGreaterThan(commentStart);
        for (var index = commentStart; index < lineFeedIndex; index++)
        {
            input[index].Should().Be(0x20);
        }
    }

    [Test]
    [Arguments("{\"a\":1}")]
    [Arguments("{\"a\":1 // c\n}")]
    [Arguments("{/*x*/\"a\":\"😀\"}")]
    [DisplayName("StripComments(string) returns UTF-8 bytes with identical length to input")]
    public void StripComments_StringOverload_PreservesUtf8Length(string text)
    {
        var normalized = JsonNormalizer.StripComments(text);

        normalized.Length.Should().Be(Encoding.UTF8.GetByteCount(text));
    }

    [Test]
    [DisplayName("Multi-byte UTF-8 bytes inside comments are replaced byte-by-byte with spaces")]
    public void StripComments_MultiByteUtf8InComment_ReplacesEveryByteWithSpace()
    {
        const string json = """
        {
          // café 😀
          "name": "repoql"
        }
        """;

        var input = Encoding.UTF8.GetBytes(json);
        var baselineLength = input.Length;
        var commentStart = IndexOfSequence(input, Encoding.UTF8.GetBytes("//"));
        commentStart.Should().BeGreaterThan(0);

        var lineFeedIndex = Array.IndexOf(input, (byte)0x0A, commentStart);
        lineFeedIndex.Should().BeGreaterThan(commentStart);

        JsonNormalizer.StripComments(input);

        input.Length.Should().Be(baselineLength);
        for (var index = commentStart; index < lineFeedIndex; index++)
        {
            input[index].Should().Be(0x20);
        }
    }

    [Test]
    [DisplayName("Unterminated block comments are replaced through EOF")]
    public void StripComments_UnterminatedBlockComment_ReplacesThroughEndOfFile()
    {
        var input = Encoding.UTF8.GetBytes("1/* unterminated 😀");

        JsonNormalizer.StripComments(input);

        input[0].Should().Be((byte)'1');
        for (var index = 1; index < input.Length; index++)
        {
            input[index].Should().Be(0x20);
        }
    }

    [Test]
    [DisplayName("BOM at start is preserved and bytes after BOM are normalized")]
    public void StripComments_Bom_PreservedAndProcessingStartsAfterBom()
    {
        var input = new byte[]
        {
            0xEF, 0xBB, 0xBF,
            (byte)'/', (byte)'/', (byte)'x',
            0x0A,
            (byte)'{', (byte)'}'
        };

        JsonNormalizer.StripComments(input);

        input[0].Should().Be(0xEF);
        input[1].Should().Be(0xBB);
        input[2].Should().Be(0xBF);
        input[3].Should().Be(0x20);
        input[4].Should().Be(0x20);
        input[5].Should().Be(0x20);
        input[6].Should().Be(0x0A);
        input[7].Should().Be((byte)'{');
        input[8].Should().Be((byte)'}');
    }

    private static int[] FindBytePositions(byte[] bytes, byte target)
    {
        var positions = new List<int>();

        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] == target)
            {
                positions.Add(index);
            }
        }

        return [.. positions];
    }

    private static int IndexOfSequence(byte[] source, byte[] pattern)
    {
        if (pattern.Length == 0)
        {
            return 0;
        }

        for (var index = 0; index <= source.Length - pattern.Length; index++)
        {
            var matched = true;
            for (var offset = 0; offset < pattern.Length; offset++)
            {
                if (source[index + offset] != pattern[offset])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return index;
            }
        }

        return -1;
    }
}

