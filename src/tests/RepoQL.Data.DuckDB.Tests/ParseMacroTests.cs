using AwesomeAssertions;

namespace RepoQL.Data.DuckDB.Tests;

public sealed class ParseMacroTests : IDisposable
{
    private readonly DuckDbDataStore _store = TestServiceCollectionExtensions.CreateTestDataStore();

    public void Dispose()
    {
        _store.Dispose();
    }

    [Test]
    public async Task Parse_Csv_ReturnsTypedRows()
    {
        var rows = _store.Query("""
            SELECT id, name, active
            FROM parse('id,name,active
            1,Alice,true
            2,Bob,false')
            ORDER BY id
            """);

        rows.Should().HaveCount(2);
        Convert.ToInt32(rows[0]["id"]).Should().Be(1);
        rows[0]["name"]?.ToString().Should().Be("Alice");
        Convert.ToBoolean(rows[0]["active"]).Should().BeTrue();
        Convert.ToInt32(rows[1]["id"]).Should().Be(2);
        rows[1]["name"]?.ToString().Should().Be("Bob");
        Convert.ToBoolean(rows[1]["active"]).Should().BeFalse();
    }

    [Test]
    public async Task Parse_Jsonl_ReturnsRows()
    {
        var rows = _store.Query("""
            SELECT id, name
            FROM parse('{"id":1,"name":"Alice"}
            {"id":2,"name":"Bob"}')
            ORDER BY id
            """);

        rows.Should().HaveCount(2);
        Convert.ToInt32(rows[0]["id"]).Should().Be(1);
        rows[0]["name"]?.ToString().Should().Be("Alice");
        Convert.ToInt32(rows[1]["id"]).Should().Be(2);
        rows[1]["name"]?.ToString().Should().Be("Bob");
    }

    [Test]
    public async Task Parse_NestedJsonEnvelope_UnwrapsToBestArray()
    {
        var rows = _store.Query("""
            SELECT id, name
            FROM parse('{
              "data": {
                "actor": {
                  "accounts": [
                    {"id": 1418123, "name": "Church Community Builder"},
                    {"id": 2673924, "name": "Pushpay Holdings Limited"}
                  ]
                }
              }
            }')
            ORDER BY id
            """);

        rows.Should().HaveCount(2);
        Convert.ToInt32(rows[0]["id"]).Should().Be(1418123);
        rows[0]["name"]?.ToString().Should().Be("Church Community Builder");
        Convert.ToInt32(rows[1]["id"]).Should().Be(2673924);
        rows[1]["name"]?.ToString().Should().Be("Pushpay Holdings Limited");
    }

    [Test]
    public async Task Parse_StringifiedResultEnvelope_UnwrapsNestedObject()
    {
        var rows = _store.Query("""
            SELECT start_time_ms, end_time_ms
            FROM parse('{
              "result": "{\"data\":{\"start_time_ms\":1772743737590,\"end_time_ms\":1772765337590},\"errors\":null,\"warnings\":null}"
            }')
            """);

        rows.Should().HaveCount(1);
        Convert.ToInt64(rows[0]["start_time_ms"]).Should().Be(1772743737590);
        Convert.ToInt64(rows[0]["end_time_ms"]).Should().Be(1772765337590);
    }

    [Test]
    public async Task Parse_Yaml_ReturnsTypedColumns()
    {
        var rows = _store.Query("""
            SELECT server, port, enabled
            FROM parse('server: localhost
            port: 8080
            enabled: true')
            """);

        rows.Should().HaveCount(1);
        rows[0]["server"]?.ToString().Should().Be("localhost");
        Convert.ToInt32(rows[0]["port"]).Should().Be(8080);
        Convert.ToBoolean(rows[0]["enabled"]).Should().BeTrue();
    }

    [Test]
    public async Task Parse_PlainText_FallsBackToTextColumn()
    {
        var rows = _store.Query("""
            SELECT text
            FROM parse('just plain output that is not structured')
            """);

        rows.Should().HaveCount(1);
        rows[0]["text"]?.ToString().Should().Contain("just plain output");
    }

    [Test]
    public async Task ConvertToJson_ReturnsNormalizedJson()
    {
        var rows = _store.Query("""
            SELECT
                convert_to_json('{"id":1,"name":"Alice"}', 'true') AS json_output
            """);

        rows.Should().HaveCount(1);
        rows[0]["json_output"]?.ToString().Should().Contain("\"id\":1");
        rows[0]["json_output"]?.ToString().Should().Contain("\"name\":\"Alice\"");
    }
}
