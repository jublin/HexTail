using HexTail.Elastic;

namespace HexTail.Tests.Elastic;

public sealed class DiscoverUrlImportTests
{
    private const string Modern =
        "https://kibana.example.com:5601/app/discover#/?_g=(filters:!(),time:(from:now-15m,to:now))"
        + "&_a=(columns:!(service,message),dataSource:(dataViewId:abc-123,type:dataView),"
        + "query:(language:kuery,query:'service : checkout'),sort:!(!('@timestamp',desc)))";

    [Fact]
    public void ReadsDataViewColumnsAndASimpleQuery()
    {
        Assert.True(DiscoverUrlImport.TryParse(Modern, out var import));

        Assert.Equal("https://kibana.example.com:5601", import!.KibanaOrigin);
        Assert.Equal("abc-123", import.DataViewId);
        Assert.Equal(["service", "message"], import.Columns);
        Assert.Equal("service", import.FilterField);
        Assert.Equal("checkout", import.FilterValue);
        Assert.Contains("sort", import.Ignored);
        Assert.Contains("time range", import.Ignored);
    }

    [Fact]
    public void ReadsTheLegacyIndexParameterAndQuotedValues()
    {
        var url =
            "https://k.example.com/app/discover#/?_a=(columns:!(a),index:'legacy-id',"
            + "query:(language:kuery,query:'host.name : \"web 1\"'))";

        Assert.True(DiscoverUrlImport.TryParse(url, out var import));

        Assert.Equal("legacy-id", import!.DataViewId);
        Assert.Equal("host.name", import.FilterField);
        Assert.Equal("web 1", import.FilterValue);
    }

    [Fact]
    public void ComplexQueriesAreIgnoredNotGuessed()
    {
        var url = "https://k/app/discover#/?_a=(query:(language:kuery,query:'a : 1 and b : 2'))";

        Assert.True(DiscoverUrlImport.TryParse(url, out var import));

        Assert.Null(import!.FilterField);
        Assert.Null(import.FilterValue);
        Assert.Contains("query", import.Ignored);
    }

    [Fact]
    public void ReadsPercentEncodedStateAndNumericDataViewIds()
    {
        var url =
            "https://k.example.com/s/team/app/discover#/?_a=%28columns%3A%21%28a%2Cb%29%2CdataSource%3A%28dataViewId%3A42%29"
            + "%2Cquery%3A%28language%3Akuery%2Cquery%3A%27level%20%3A%20error%27%29%29";

        Assert.True(DiscoverUrlImport.TryParse(url, out var import));

        Assert.Equal("https://k.example.com/s/team", import!.KibanaOrigin);
        Assert.Equal("42", import.DataViewId);
        Assert.Equal(["a", "b"], import.Columns);
        Assert.Equal("level", import.FilterField);
        Assert.Equal("error", import.FilterValue);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("https://example.com/no-fragment")]
    [InlineData("https://k/app/discover#/?nothing=here")]
    public void RejectsUrlsWithoutDiscoverState(string url) =>
        Assert.False(DiscoverUrlImport.TryParse(url, out _));
}
