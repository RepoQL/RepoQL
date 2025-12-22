using AwesomeAssertions;
using Google.Protobuf.WellKnownTypes;
using RepoQL.ConsoleApp.Commands;
using RepoQL.ConsoleApp.Formatters;
using RepoQL.Contracts;

namespace RepoQL.Cli.Tests;

public sealed class ToonFormatterTests
{
    private readonly ToonFormatter _formatter = new();

    [Test]
    [DisplayName("Format property returns Toon")]
    public void Format_ReturnsToon()
    {
        _formatter.Format.Should().Be(ResultFormat.Toon);
    }

    [Test]
    [DisplayName("Empty result returns empty array")]
    public async Task FormatAsync_EmptyResult_ReturnsEmptyArray()
    {
        var response = new RawQueryResponse();
        var result = await _formatter.FormatAsync(response);
        result.Should().BeEmpty();
    }

    [Test]
    [DisplayName("Single column renders values without header")]
    public async Task FormatAsync_SingleColumn_ValuesOnly()
    {
        var response = CreateResponse(
            ["name"],
            [["Alice"], ["Bob"], ["Carol"]]);

        var result = await _formatter.FormatAsync(response);

        result.Should().HaveCount(3);
        result[0].Should().Be("Alice");
        result[1].Should().Be("Bob");
        result[2].Should().Be("Carol");
    }

    [Test]
    [DisplayName("Multi-column renders TOON tabular format")]
    public async Task FormatAsync_MultiColumn_TabularFormat()
    {
        var response = CreateResponse(
            ["id", "name", "active"],
            [
                [1, "Alice", true],
                [2, "Bob", false]
            ]);

        var result = await _formatter.FormatAsync(response);

        result.Should().HaveCount(3);
        result[0].Should().Be("[2]{id,name,active}:");
        result[1].Should().Be("1,Alice,true");
        result[2].Should().Be("2,Bob,false");
    }

    [Test]
    [DisplayName("Two-column with URI uses key-value format (inline)")]
    public async Task FormatAsync_TwoColumnUri_InlineFormat()
    {
        var response = CreateResponse(
            ["uri", "headline"],
            [
                ["file:///src/Foo.cs#line=42", "class Foo"],
                ["file:///src/Bar.cs#line=10", "void Bar()"]
            ]);

        var result = await _formatter.FormatAsync(response);

        result.Should().HaveCount(2);
        result[0].Should().Be("file:///src/Foo.cs#line=42: class Foo");
        result[1].Should().Be("file:///src/Bar.cs#line=10: void Bar()");
    }

    [Test]
    [DisplayName("Two-column with URI uses block format for long values")]
    public async Task FormatAsync_TwoColumnUri_BlockFormat_LongValue()
    {
        var longValue = new string('x', 100); // > 80 chars
        var response = CreateResponse(
            ["uri", "content"],
            [["file:///src/Foo.cs", longValue]]);

        var result = await _formatter.FormatAsync(response);

        result.Should().HaveCount(2);
        result[0].Should().Be("file:///src/Foo.cs:");
        result[1].Should().Be($"  {longValue}");
    }

    [Test]
    [DisplayName("Two-column with URI uses block format for multiline values")]
    public async Task FormatAsync_TwoColumnUri_BlockFormat_Multiline()
    {
        var response = CreateResponse(
            ["uri", "content"],
            [["file:///src/Foo.cs", "line1\nline2\nline3"]]);

        var result = await _formatter.FormatAsync(response);

        result.Should().HaveCount(4);
        result[0].Should().Be("file:///src/Foo.cs:");
        result[1].Should().Be("  line1");
        result[2].Should().Be("  line2");
        result[3].Should().Be("  line3");
    }

    [Test]
    [DisplayName("Null values render as null")]
    public async Task FormatAsync_NullValues_RenderAsNull()
    {
        var response = CreateResponse(
            ["name"],
            [[null]]);

        var result = await _formatter.FormatAsync(response);

        result.Should().HaveCount(1);
        result[0].Should().Be("null");
    }

    [Test]
    [DisplayName("Boolean values render as true/false")]
    public async Task FormatAsync_BooleanValues_RenderCorrectly()
    {
        var response = CreateResponse(
            ["active"],
            [[true], [false]]);

        var result = await _formatter.FormatAsync(response);

        result[0].Should().Be("true");
        result[1].Should().Be("false");
    }

    [Test]
    [DisplayName("Numbers render in canonical form")]
    public async Task FormatAsync_Numbers_CanonicalForm()
    {
        var response = CreateResponse(
            ["value"],
            [[42.0], [3.14], [100.0]]);

        var result = await _formatter.FormatAsync(response);

        result[0].Should().Be("42");  // No .0
        result[1].Should().Be("3.14");
        result[2].Should().Be("100");
    }

    [Test]
    [DisplayName("Strings with commas are quoted")]
    public async Task FormatAsync_StringWithComma_IsQuoted()
    {
        var response = CreateResponse(
            ["id", "name"],
            [[1, "Smith, John"]]);

        var result = await _formatter.FormatAsync(response);

        result[1].Should().Be("1,\"Smith, John\"");
    }

    [Test]
    [DisplayName("Strings matching true/false/null are quoted")]
    public async Task FormatAsync_ReservedWords_AreQuoted()
    {
        var response = CreateResponse(
            ["value"],
            [["true"], ["false"], ["null"]]);

        var result = await _formatter.FormatAsync(response);

        result[0].Should().Be("\"true\"");
        result[1].Should().Be("\"false\"");
        result[2].Should().Be("\"null\"");
    }

    [Test]
    [DisplayName("Numeric-looking strings are quoted")]
    public async Task FormatAsync_NumericLookingStrings_AreQuoted()
    {
        var response = CreateResponse(
            ["value"],
            [["123"], ["45.67"], ["007"]]);

        var result = await _formatter.FormatAsync(response);

        result[0].Should().Be("\"123\"");
        result[1].Should().Be("\"45.67\"");
        result[2].Should().Be("\"007\"");
    }

    [Test]
    [DisplayName("Strings with quotes are escaped")]
    public async Task FormatAsync_StringWithQuotes_IsEscaped()
    {
        var response = CreateResponse(
            ["value"],
            [["He said \"hello\""]]);

        var result = await _formatter.FormatAsync(response);

        result[0].Should().Be("\"He said \\\"hello\\\"\"");
    }

    [Test]
    [DisplayName("Strings with backslashes are escaped")]
    public async Task FormatAsync_StringWithBackslash_IsEscaped()
    {
        var response = CreateResponse(
            ["path"],
            [["C:\\Users\\test"]]);

        var result = await _formatter.FormatAsync(response);

        result[0].Should().Be("\"C:\\\\Users\\\\test\"");
    }

    [Test]
    [DisplayName("Strings with newlines are escaped")]
    public async Task FormatAsync_StringWithNewline_IsEscaped()
    {
        var response = CreateResponse(
            ["id", "text"],
            [[1, "line1\nline2"]]);

        var result = await _formatter.FormatAsync(response);

        result[1].Should().Be("1,\"line1\\nline2\"");
    }

    [Test]
    [DisplayName("Empty strings are quoted")]
    public async Task FormatAsync_EmptyString_IsQuoted()
    {
        var response = CreateResponse(
            ["value"],
            [[""]]);

        var result = await _formatter.FormatAsync(response);

        result[0].Should().Be("\"\"");
    }

    [Test]
    [DisplayName("Strings with leading/trailing spaces are quoted")]
    public async Task FormatAsync_StringWithSpaces_IsQuoted()
    {
        var response = CreateResponse(
            ["value"],
            [[" padded "]]);

        var result = await _formatter.FormatAsync(response);

        result[0].Should().Be("\" padded \"");
    }

    [Test]
    [DisplayName("Strings starting with hyphen are quoted")]
    public async Task FormatAsync_StringStartingWithHyphen_IsQuoted()
    {
        var response = CreateResponse(
            ["flag"],
            [["-verbose"]]);

        var result = await _formatter.FormatAsync(response);

        result[0].Should().Be("\"-verbose\"");
    }

    [Test]
    [DisplayName("Struct values render as inline JSON")]
    public async Task FormatAsync_StructValue_RendersAsJson()
    {
        var structValue = new Struct();
        structValue.Fields["key"] = Value.ForString("value");
        structValue.Fields["num"] = Value.ForNumber(42);

        var response = new RawQueryResponse();
        response.Columns.Add(new ColumnSchema { Name = "data" });
        var row = new RowData();
        row.Values.Add(Value.ForStruct(structValue));
        response.Rows.Add(row);

        var result = await _formatter.FormatAsync(response);

        result[0].Should().Contain("\"key\":\"value\"");
        result[0].Should().Contain("\"num\":42");
    }

    [Test]
    [DisplayName("List values render as inline JSON array")]
    public async Task FormatAsync_ListValue_RendersAsJsonArray()
    {
        var response = new RawQueryResponse();
        response.Columns.Add(new ColumnSchema { Name = "items" });
        var row = new RowData();
        row.Values.Add(Value.ForList(Value.ForNumber(1), Value.ForNumber(2), Value.ForNumber(3)));
        response.Rows.Add(row);

        var result = await _formatter.FormatAsync(response);

        result[0].Should().Be("[1,2,3]");
    }

    [Test]
    [DisplayName("maxRows parameter limits output")]
    public async Task FormatAsync_MaxRows_LimitsOutput()
    {
        var response = CreateResponse(
            ["name"],
            [["A"], ["B"], ["C"], ["D"], ["E"]]);

        var result = await _formatter.FormatAsync(response, maxRows: 3);

        result.Should().HaveCount(3);
        result[0].Should().Be("A");
        result[1].Should().Be("B");
        result[2].Should().Be("C");
    }

    [Test]
    [DisplayName("URI column detected regardless of position")]
    public async Task FormatAsync_UriColumnSecond_StillDetected()
    {
        var response = CreateResponse(
            ["headline", "uri"],
            [["class Foo", "file:///src/Foo.cs"]]);

        var result = await _formatter.FormatAsync(response);

        result.Should().HaveCount(1);
        result[0].Should().Be("file:///src/Foo.cs: class Foo");
    }

    [Test]
    [DisplayName("Column containing 'uri' substring is detected")]
    public async Task FormatAsync_UriSubstring_Detected()
    {
        var response = CreateResponse(
            ["artifact_uri", "name"],
            [["file:///src/Foo.cs", "Foo"]]);

        var result = await _formatter.FormatAsync(response);

        result[0].Should().Be("file:///src/Foo.cs: Foo");
    }

    // Helper to create test responses
    private static RawQueryResponse CreateResponse(string[] columnNames, object?[][] rows)
    {
        var response = new RawQueryResponse();

        foreach (var name in columnNames)
            response.Columns.Add(new ColumnSchema { Name = name });

        foreach (var row in rows)
        {
            var rowData = new RowData();
            foreach (var value in row)
            {
                rowData.Values.Add(value switch
                {
                    null => Value.ForNull(),
                    bool b => Value.ForBool(b),
                    int i => Value.ForNumber(i),
                    double d => Value.ForNumber(d),
                    string s => Value.ForString(s),
                    _ => Value.ForNull()
                });
            }
            response.Rows.Add(rowData);
        }

        return response;
    }
}
