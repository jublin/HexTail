using System.Text.Json;
using HexTailSharp.Elastic;

namespace HexTailSharp.Tests.Elastic;

public sealed class ElasticDocumentMapperTests
{
    [Fact]
    public void Map_FlattensSourceAndFieldsWithOutputOrder()
    {
        using var source = JsonDocument.Parse(
            """{"message":"ready","service":{"name":"api"},"tags":["a","b"],"empty":null}"""
        );
        using var fields = JsonDocument.Parse("""{"service.name.keyword":["gateway"]}""");

        var line = ElasticDocumentMapper.Map(
            source.RootElement,
            fields.RootElement,
            ["service.name.keyword", "message", "missing"]
        );

        Assert.Equal("gateway ready", line.Raw);
        Assert.Equal("gateway", line.ParsedFields!["service.name.keyword"]);
        Assert.Equal("api", line.ParsedFields["service.name"]);
        Assert.Equal("[\"a\",\"b\"]", line.ParsedFields["tags"]);
        Assert.DoesNotContain("empty", line.ParsedFields.Keys);
    }
}
