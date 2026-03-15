using AwesomeAssertions;
using Google.Protobuf.WellKnownTypes;
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
        response.Rows[0].Values[0].KindCase.Should().Be(Value.KindOneofCase.StringValue);
        response.Rows[0].Values[0].StringValue.Should().Be("hello");
    }

    [Test]
    public void MapToResponse_NumberScalar_ReturnsSingleResultColumn()
    {
        var response = JsonResultMapper.MapToResponse("42");

        response.Columns.Should().HaveCount(1);
        response.Columns[0].Name.Should().Be("result");
        response.Rows.Should().HaveCount(1);
        response.Rows[0].Values[0].KindCase.Should().Be(Value.KindOneofCase.NumberValue);
        response.Rows[0].Values[0].NumberValue.Should().Be(42);
    }

    [Test]
    public void MapToResponse_BoolScalar_ReturnsSingleResultColumn()
    {
        var response = JsonResultMapper.MapToResponse("true");

        response.Columns.Should().HaveCount(1);
        response.Columns[0].Name.Should().Be("result");
        response.Rows.Should().HaveCount(1);
        response.Rows[0].Values[0].KindCase.Should().Be(Value.KindOneofCase.BoolValue);
        response.Rows[0].Values[0].BoolValue.Should().BeTrue();
    }

    [Test]
    public void MapToResponse_Object_ReturnsPropertyValuePairs()
    {
        var response = JsonResultMapper.MapToResponse("""{"a":1,"b":"x"}""");

        response.Columns.Select(column => column.Name).Should().Equal("property", "value");
        response.Rows.Should().HaveCount(2);
        response.RowCount.Should().Be(2);

        response.Rows[0].Values[0].StringValue.Should().Be("a");
        response.Rows[0].Values[1].NumberValue.Should().Be(1);
        response.Rows[1].Values[0].StringValue.Should().Be("b");
        response.Rows[1].Values[1].StringValue.Should().Be("x");
    }

    [Test]
    public void MapToResponse_ArrayOfObjects_ReturnsColumnarData()
    {
        var response = JsonResultMapper.MapToResponse("""[{"a":1,"b":2},{"a":3,"b":4}]""");

        response.Columns.Select(column => column.Name).Should().Equal("a", "b");
        response.Rows.Should().HaveCount(2);
        response.RowCount.Should().Be(2);

        response.Rows[0].Values.Select(value => value.NumberValue).Should().Equal(1, 2);
        response.Rows[1].Values.Select(value => value.NumberValue).Should().Equal(3, 4);
    }

    [Test]
    public void MapToResponse_SingleElementArray_TreatedAsObject()
    {
        var response = JsonResultMapper.MapToResponse("""[{"a":1,"b":"x"}]""");

        response.Columns.Select(column => column.Name).Should().Equal("property", "value");
        response.Rows.Should().HaveCount(2);
        response.RowCount.Should().Be(2);
        response.Rows[0].Values[0].StringValue.Should().Be("a");
        response.Rows[1].Values[0].StringValue.Should().Be("b");
    }

    [Test]
    public void MapToResponse_ArrayOfScalars_ReturnsValueColumn()
    {
        var response = JsonResultMapper.MapToResponse("[1,2,3]");

        response.Columns.Should().HaveCount(1);
        response.Columns[0].Name.Should().Be("value");
        response.Rows.Should().HaveCount(3);
        response.RowCount.Should().Be(3);
        response.Rows.Select(row => row.Values[0].NumberValue).Should().Equal(1, 2, 3);
    }

    [Test]
    public void MapToResponse_NestedObject_SerializesNestedAsJson()
    {
        var response = JsonResultMapper.MapToResponse("""{"a":{"nested":1},"b":[1,2]}""");

        response.Columns.Select(column => column.Name).Should().Equal("property", "value");
        response.Rows.Should().HaveCount(2);

        response.Rows[0].Values[0].StringValue.Should().Be("a");
        response.Rows[0].Values[1].StringValue.Should().Be("""{"nested":1}""");
        response.Rows[1].Values[0].StringValue.Should().Be("b");
        response.Rows[1].Values[1].StringValue.Should().Be("[1,2]");
    }

    private static void AssertEmptyResponse(RawQueryResponse response)
    {
        response.Columns.Should().BeEmpty();
        response.Rows.Should().BeEmpty();
        response.RowCount.Should().Be(0);
    }
}
