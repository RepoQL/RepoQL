using AwesomeAssertions;
using RepoQL.Formats.Csv.Analysis;
using RepoQL.Formats.Csv.Surface;

namespace RepoQL.Formats.Csv.Tests;

public sealed class ColumnTypeInferrerTests
{
    [Test]
    [DisplayName("Detects header when first row is strings and data has numbers")]
    public void Infer_DetectsHeaderWithStringThenNumericData()
    {
        IReadOnlyList<IReadOnlyList<string>> rows =
        [
            ["name", "age"],
            ["Alice", "30"],
            ["Bob", "25"]
        ];

        var result = ColumnTypeInferrer.Infer(rows);

        result.HasHeader.Should().BeTrue();
        result.Columns.Should().HaveCount(2);
        result.Columns[0].Name.Should().Be("name");
        result.Columns[1].Name.Should().Be("age");
    }

    [Test]
    [DisplayName("No header when all rows look like data")]
    public void Infer_DoesNotDetectHeaderWhenRowsLookUniformlyDataLike()
    {
        IReadOnlyList<IReadOnlyList<string>> rows =
        [
            ["1", "2"],
            ["3", "4"],
            ["5", "6"]
        ];

        var result = ColumnTypeInferrer.Infer(rows);

        result.HasHeader.Should().BeFalse();
    }

    [Test]
    [DisplayName("Infers integer column")]
    public void Infer_InfersIntegerType()
    {
        IReadOnlyList<IReadOnlyList<string>> rows =
        [
            ["id"],
            ["1"],
            ["2"],
            ["3"],
            ["4"],
            ["5"]
        ];

        var result = ColumnTypeInferrer.Infer(rows);

        result.Columns[0].DataType.Should().Be(CsvColumnType.Integer);
    }

    [Test]
    [DisplayName("Infers float column")]
    public void Infer_InfersFloatType()
    {
        IReadOnlyList<IReadOnlyList<string>> rows =
        [
            ["1.5"],
            ["2.3"],
            ["3.7"]
        ];

        var result = ColumnTypeInferrer.Infer(rows);

        result.Columns[0].DataType.Should().Be(CsvColumnType.Float);
    }

    [Test]
    [DisplayName("Infers boolean column")]
    public void Infer_InfersBooleanType()
    {
        IReadOnlyList<IReadOnlyList<string>> rows =
        [
            ["true"],
            ["false"],
            ["true"],
            ["false"]
        ];

        var result = ColumnTypeInferrer.Infer(rows);

        result.Columns[0].DataType.Should().Be(CsvColumnType.Boolean);
    }

    [Test]
    [DisplayName("Infers date column")]
    public void Infer_InfersDateType()
    {
        IReadOnlyList<IReadOnlyList<string>> rows =
        [
            ["2024-01-01"],
            ["2024-02-15"],
            ["2024-03-20"]
        ];

        var result = ColumnTypeInferrer.Infer(rows);

        result.Columns[0].DataType.Should().Be(CsvColumnType.Date);
    }

    [Test]
    [DisplayName("Infers varchar for mixed types")]
    public void Infer_FallsBackToVarcharForMixedTypes()
    {
        IReadOnlyList<IReadOnlyList<string>> rows =
        [
            ["hello"],
            ["42"],
            ["true"]
        ];

        var result = ColumnTypeInferrer.Infer(rows);

        result.Columns[0].DataType.Should().Be(CsvColumnType.Varchar);
    }

    [Test]
    [DisplayName("Tracks min and max for numeric columns")]
    public void Infer_TracksNumericMinMax()
    {
        IReadOnlyList<IReadOnlyList<string>> rows =
        [
            ["10"],
            ["50"],
            ["30"]
        ];

        var result = ColumnTypeInferrer.Infer(rows);

        result.Columns[0].MinValue.Should().Be("10");
        result.Columns[0].MaxValue.Should().Be("50");
    }

    [Test]
    [DisplayName("Collects up to five sample values")]
    public void Infer_LimitsSampleValuesToFive()
    {
        IReadOnlyList<IReadOnlyList<string>> rows =
        [
            ["alpha_1"],
            ["alpha_2"],
            ["alpha_3"],
            ["alpha_4"],
            ["alpha_5"],
            ["alpha_6"],
            ["alpha_7"],
            ["alpha_8"],
            ["alpha_9"],
            ["alpha_10"]
        ];

        var result = ColumnTypeInferrer.Infer(rows);

        result.Columns[0].SampleValues.Count.Should().BeLessThanOrEqualTo(5);
    }

    [Test]
    [DisplayName("Generates synthetic column names when no header")]
    public void Infer_GeneratesSyntheticColumnNamesWithoutHeader()
    {
        IReadOnlyList<IReadOnlyList<string>> rows =
        [
            ["1", "2"],
            ["3", "4"]
        ];

        var result = ColumnTypeInferrer.Infer(rows);

        result.HasHeader.Should().BeFalse();
        result.Columns[0].Name.Should().Be("column_1");
        result.Columns[1].Name.Should().Be("column_2");
    }

    [Test]
    [DisplayName("Estimates tokens per column")]
    public void Infer_EstimatesColumnTokens()
    {
        IReadOnlyList<IReadOnlyList<string>> rows =
        [
            ["id"],
            ["1001"],
            ["1002"],
            ["1003"]
        ];

        var result = ColumnTypeInferrer.Infer(rows);

        result.Columns[0].EstimatedTokens.Should().BeGreaterThan(0);
    }

    [Test]
    [DisplayName("Handles empty column values gracefully")]
    public void Infer_HandlesSparseColumns()
    {
        IReadOnlyList<IReadOnlyList<string>> rows =
        [
            ["name", "age"],
            ["Alice", ""],
            ["", "30"],
            ["Bob", ""]
        ];

        var result = ColumnTypeInferrer.Infer(rows);

        result.HasHeader.Should().BeTrue();
        result.Columns.Should().HaveCount(2);
        result.Columns[0].NonEmptyCount.Should().Be(2);
        result.Columns[1].NonEmptyCount.Should().Be(1);
    }
}
