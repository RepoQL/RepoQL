using AwesomeAssertions;
using RepoQL.Contracts;

namespace RepoQL.Sandbox.Tests;

public sealed class JsonResultMapperTests
{
    [Test]
    public void MapToResponse_Null_ReturnsEmptyResponse()
    {
        var response = JsonResultMapper.MapToResponse(null);

        AssertEmptyResponse(response);
    }

    [Test]
    public void MapToResponse_EmptyString_ReturnsEmptyResponse()
    {
        var response = JsonResultMapper.MapToResponse(string.Empty);

        AssertEmptyResponse(response);
    }

    [Test]
    public void MapToResponse_StringScalar_ReturnsSingleResultColumn()
    {
        var response = JsonResultMapper.MapToResponse("\"hello\"");

        response.Columns.Should().HaveCount(1);
        response.Columns[0].Name.Should().Be("result");
        response.Rows.Should().HaveCount(1);
        response.RowCount.Should().Be(1);
        response.Rows[0].Values[0].Should().BeOfType<TabularValue.StringValue>()
            .Which.Value.Should().Be("hello");
    }

    [Test]
    public void MapToResponse_NumberScalar_ReturnsSingleResultColumn()
    {
        var response = JsonResultMapper.MapToResponse("42");

        response.Columns.Should().HaveCount(1);
        response.Columns[0].Name.Should().Be("result");
        response.Rows.Should().HaveCount(1);
        response.Rows[0].Values[0].Should().BeOfType<TabularValue.NumberValue>()
            .Which.Value.Should().Be(42);
    }

    [Test]
    public void MapToResponse_BoolScalar_ReturnsSingleResultColumn()
    {
        var response = JsonResultMapper.MapToResponse("true");

        response.Columns.Should().HaveCount(1);
        response.Columns[0].Name.Should().Be("result");
        response.Rows.Should().HaveCount(1);
        response.Rows[0].Values[0].Should().BeOfType<TabularValue.BoolValue>()
            .Which.Value.Should().BeTrue();
    }

    [Test]
    public void MapToResponse_Object_ReturnsPropertyValuePairs()
    {
        var response = JsonResultMapper.MapToResponse("""{"a":1,"b":"x"}""");

        response.Columns.Select(column => column.Name).Should().Equal("property", "value");
        response.Rows.Should().HaveCount(2);
        response.RowCount.Should().Be(2);

        GetString(response, 0, 0).Should().Be("a");
        GetNumber(response, 0, 1).Should().Be(1);
        GetString(response, 1, 0).Should().Be("b");
        GetString(response, 1, 1).Should().Be("x");
    }

    [Test]
    public void MapToResponse_ArrayOfObjects_ReturnsColumnarData()
    {
        var response = JsonResultMapper.MapToResponse("""[{"a":1,"b":2},{"a":3,"b":4}]""");

        response.Columns.Select(column => column.Name).Should().Equal("a", "b");
        response.Rows.Should().HaveCount(2);
        response.RowCount.Should().Be(2);

        response.Rows[0].Values.Select(GetNumberValue).Should().Equal(1, 2);
        response.Rows[1].Values.Select(GetNumberValue).Should().Equal(3, 4);
    }

    [Test]
    public void MapToResponse_SingleElementArray_TreatedAsObject()
    {
        var response = JsonResultMapper.MapToResponse("""[{"a":1,"b":"x"}]""");

        response.Columns.Select(column => column.Name).Should().Equal("property", "value");
        response.Rows.Should().HaveCount(2);
        response.RowCount.Should().Be(2);
        GetString(response, 0, 0).Should().Be("a");
        GetString(response, 1, 0).Should().Be("b");
    }

    [Test]
    public void MapToResponse_ArrayOfScalars_ReturnsValueColumn()
    {
        var response = JsonResultMapper.MapToResponse("[1,2,3]");

        response.Columns.Should().HaveCount(1);
        response.Columns[0].Name.Should().Be("value");
        response.Rows.Should().HaveCount(3);
        response.RowCount.Should().Be(3);
        response.Rows.Select(row => GetNumberValue(row.Values[0])).Should().Equal(1, 2, 3);
    }

    [Test]
    public void MapToResponse_NestedObject_SerializesNestedAsJson()
    {
        var response = JsonResultMapper.MapToResponse("""{"a":{"nested":1},"b":[1,2]}""");

        response.Columns.Select(column => column.Name).Should().Equal("property", "value");
        response.Rows.Should().HaveCount(2);

        GetString(response, 0, 0).Should().Be("a");
        GetString(response, 0, 1).Should().Be("""{"nested":1}""");
        GetString(response, 1, 0).Should().Be("b");
        GetString(response, 1, 1).Should().Be("[1,2]");
    }

    private static void AssertEmptyResponse(TabularResult response)
    {
        response.Columns.Should().BeEmpty();
        response.Rows.Should().BeEmpty();
        response.RowCount.Should().Be(0);
    }

    private static string GetString(TabularResult response, int row, int col)
        => ((TabularValue.StringValue)response.Rows[row].Values[col]).Value;

    private static double GetNumber(TabularResult response, int row, int col)
        => ((TabularValue.NumberValue)response.Rows[row].Values[col]).Value;

    private static double GetNumberValue(TabularValue cell)
        => ((TabularValue.NumberValue)cell).Value;
}
