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

    private static DiscoverImport Import(string url)
    {
        Assert.True(DiscoverUrlImport.TryParse(url, out var import));
        return import!;
    }

    private static string WithApp(string app, string? global = null) =>
        "https://k.example.com/app/discover#/?"
        + (global is null ? "" : $"_g={global}&")
        + $"_a={app}";

    [Theory]
    [InlineData("a : b~")]
    [InlineData("svc : check*")]
    [InlineData("time : 12:30")]
    [InlineData("a : foo\\:bar")]
    [InlineData("NOT a : b")]
    [InlineData("a : and")]
    [InlineData("a : OR")]
    [InlineData("a : not")]
    [InlineData("a : b?")]
    [InlineData("a : b^2")]
    public void KqlClausesWithSpecialSyntaxAreIgnored(string kql)
    {
        var import = Import(WithApp($"(query:(language:kuery,query:'{kql}'))"));

        Assert.Null(import.FilterField);
        Assert.Null(import.FilterValue);
        Assert.Contains("query", import.Ignored);
    }

    [Fact]
    public void NonKqlLanguageIsIgnored()
    {
        var import = Import(WithApp("(query:(language:lucene,query:'service : checkout'))"));

        Assert.Null(import.FilterField);
        Assert.Contains("query", import.Ignored);
    }

    [Fact]
    public void MissingLanguageIsTreatedAsKql()
    {
        var import = Import(WithApp("(query:(query:'service : checkout'))"));

        Assert.Equal("service", import.FilterField);
        Assert.Equal("checkout", import.FilterValue);
        Assert.DoesNotContain("query", import.Ignored);
    }

    [Fact]
    public void NonEmptyGlobalFiltersAreReported()
    {
        var import = Import(
            WithApp(
                "(columns:!(a))",
                "(filters:!((meta:(key:a),query:(match_phrase:(a:b)))),time:(from:now-1h,to:now))"
            )
        );

        Assert.Contains("filters", import.Ignored);
    }

    [Fact]
    public void EmptyGlobalFiltersAreNotReported()
    {
        var import = Import(WithApp("(columns:!(a))", "(filters:!())"));

        Assert.DoesNotContain("filters", import.Ignored);
    }

    [Fact]
    public void FiltersAreListedOnceWhenGlobalAndAppBothHaveThem()
    {
        var import = Import(WithApp("(filters:!((meta:(key:a))))", "(filters:!((meta:(key:b))))"));

        Assert.Single(import.Ignored, "filters");
    }

    [Fact]
    public void ActiveRefreshIntervalIsReported()
    {
        var import = Import(WithApp("(columns:!(a))", "(refreshInterval:(pause:!f,value:5000))"));

        Assert.Contains("refresh interval", import.Ignored);
    }

    [Fact]
    public void PausedRefreshIntervalIsNotReported()
    {
        var import = Import(WithApp("(columns:!(a))", "(refreshInterval:(pause:!t,value:5000))"));

        Assert.DoesNotContain("refresh interval", import.Ignored);
    }

    [Fact]
    public void SavedSearchUrlsAreReported()
    {
        var import = Import("https://k.example.com/app/discover#/view/abc?_a=(columns:!(a))");

        Assert.Contains("saved search", import.Ignored);
    }

    [Fact]
    public void PlainDiscoverUrlsAreNotSavedSearches()
    {
        Assert.DoesNotContain("saved search", Import(WithApp("(columns:!(a))")).Ignored);
    }

    [Fact]
    public void EsqlDataSourceIsReportedWithoutADataView()
    {
        var import = Import(
            WithApp(
                "(columns:!(a,b),dataSource:(type:esql,dataViewId:abc),index:legacy,"
                    + "query:(esql:'from logs | limit 10'))"
            )
        );

        Assert.Contains("ES|QL", import.Ignored);
        Assert.Null(import.DataViewId);
        Assert.Null(import.FilterField);
        Assert.DoesNotContain("query", import.Ignored);
        Assert.Equal(["a", "b"], import.Columns);
    }

    [Fact]
    public void EsqlQueryKeyAloneIsReported()
    {
        var import = Import(WithApp("(query:(esql:'from logs'))"));

        Assert.Contains("ES|QL", import.Ignored);
        Assert.Null(import.DataViewId);
    }

    [Fact]
    public void UnknownAppStateKeysAreReportedOnce()
    {
        var import = Import(WithApp("(columns:!(a),weird:1,other:2)"));

        Assert.Single(import.Ignored, "other state");
    }

    [Fact]
    public void KnownAppStateKeysAreNotOtherState()
    {
        var import = Import(
            WithApp(
                "(columns:!(a),dataSource:(dataViewId:x),index:x,query:(language:kuery,query:''),"
                    + "sort:!(),filters:!(),interval:auto,hideChart:!t,grid:(),"
                    + "hideAggregatedPreview:!t,viewMode:documents,rowHeight:3,rowsPerPage:100,"
                    + "breakdownField:a,sampleSize:500,density:compact,headerRowHeight:2)"
            )
        );

        Assert.DoesNotContain("other state", import.Ignored);
    }

    [Fact]
    public void NumericLookingDataViewIdsKeepTheirText()
    {
        Assert.Equal("00123", Import(WithApp("(index:00123)")).DataViewId);
        Assert.Equal("42", Import(WithApp("(index:42)")).DataViewId);
    }

    [Fact]
    public void DeeplyNestedStateIsRejectedWithoutCrashing()
    {
        foreach (var unit in new[] { "!(", "(a:" })
        {
            var url = WithApp(string.Concat(Enumerable.Repeat(unit, 10_000)));

            Assert.False(DiscoverUrlImport.TryParse(url, out _));
        }
    }

    [Fact]
    public void OriginDropsUserInfo()
    {
        var import = Import(
            "https://user:secret@k.example.com:5601/app/discover#/?_a=(columns:!(a))"
        );

        Assert.Equal("https://k.example.com:5601", import.KibanaOrigin);
    }

    [Fact]
    public void BasePathEndsAtTheLastAppSegment()
    {
        var import = Import("https://k.example.com/s/app/app/discover#/?_a=(columns:!(a))");

        Assert.Equal("https://k.example.com/s/app", import.KibanaOrigin);
    }

    [Fact]
    public void OriginHasNoBasePathWithoutAnAppSegment()
    {
        var import = Import("https://k.example.com/discover#/?_a=(columns:!(a))");

        Assert.Equal("https://k.example.com", import.KibanaOrigin);
    }

    [Fact]
    public void FallsBackToTheQueryStringWhenTheFragmentHasNoState()
    {
        var import = Import("https://k.example.com/app/discover?_a=(columns:!(a,b))#/?other=1");

        Assert.Equal(["a", "b"], import.Columns);
    }

    [Fact]
    public void ReadsQueryStringStateWithoutAFragment()
    {
        var import = Import("https://k.example.com/app/discover?_a=(columns:!(a))");

        Assert.Equal(["a"], import.Columns);
    }
}
