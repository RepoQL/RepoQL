using AwesomeAssertions;
using RepoQL.Formats.Csv.Analysis;

namespace RepoQL.Formats.Csv.Tests;

public sealed class DelimiterDetectorTests
{
    [Test]
    [DisplayName("Detects comma delimiter from standard CSV")]
    public void Detect_FindsCommaDelimiter()
    {
        var result = DelimiterDetector.Detect("a,b,c\n1,2,3\n4,5,6");

        result.Delimiter.Should().Be(',');
        result.FieldCount.Should().Be(3);
    }

    [Test]
    [DisplayName("Detects tab delimiter from TSV")]
    public void Detect_FindsTabDelimiter()
    {
        var result = DelimiterDetector.Detect("a\tb\tc\n1\t2\t3\n4\t5\t6");

        result.Delimiter.Should().Be('\t');
        result.FieldCount.Should().Be(3);
    }

    [Test]
    [DisplayName("Detects pipe delimiter")]
    public void Detect_FindsPipeDelimiter()
    {
        var result = DelimiterDetector.Detect("a|b|c\n1|2|3\n4|5|6");

        result.Delimiter.Should().Be('|');
        result.FieldCount.Should().Be(3);
    }

    [Test]
    [DisplayName("Detects semicolon delimiter")]
    public void Detect_FindsSemicolonDelimiter()
    {
        var result = DelimiterDetector.Detect("a;b;c\n1;2;3\n4;5;6");

        result.Delimiter.Should().Be(';');
        result.FieldCount.Should().Be(3);
    }

    [Test]
    [DisplayName("Handles quoted fields with embedded commas")]
    public void Detect_HandlesQuotedCommas()
    {
        var result = DelimiterDetector.Detect("\"a,b\",c,d\n1,2,3\n4,5,6");

        result.Delimiter.Should().Be(',');
        result.FieldCount.Should().Be(3);
    }

    [Test]
    [DisplayName("Handles quoted fields with embedded quotes")]
    public void Detect_HandlesEscapedQuotes()
    {
        var result = DelimiterDetector.Detect("\"a\"\"b\",c\n1,2");

        result.Delimiter.Should().Be(',');
        result.FieldCount.Should().Be(2);
    }

    [Test]
    [DisplayName("Returns fallback for single-column file")]
    public void Detect_FallsBackForSingleColumnInput()
    {
        var result = DelimiterDetector.Detect("header\nval1\nval2");

        result.Delimiter.Should().Be(',');
        result.FieldCount.Should().Be(1);
        result.Consistency.Should().Be(0f);
    }

    [Test]
    [DisplayName("Returns fallback for empty text")]
    public void Detect_FallsBackForEmptyText()
    {
        var result = DelimiterDetector.Detect(string.Empty);

        result.Delimiter.Should().Be(',');
        result.FieldCount.Should().Be(1);
        result.Consistency.Should().Be(0f);
    }

    [Test]
    [DisplayName("ParseFields handles simple line")]
    public void ParseFields_SplitsSimpleLine()
    {
        var fields = DelimiterDetector.ParseFields("a,b,c", ',');

        fields.Should().Equal(["a", "b", "c"]);
    }

    [Test]
    [DisplayName("ParseFields handles quoted field")]
    public void ParseFields_PreservesQuotedField()
    {
        var fields = DelimiterDetector.ParseFields("\"hello, world\",b", ',');

        fields.Should().Equal(["hello, world", "b"]);
    }
}
