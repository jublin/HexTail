using System.Net;
using System.Text;
using System.Text.Json;
using HexTail.Elastic;
using HexTail.Persistence;
using HexTail.Tests.Support;

namespace HexTail.Tests.Elastic;

public sealed class ElasticApiClientTests
{
    [Fact]
    public async Task Client_UsesSpacesPathAndBasicAuthorization()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(
            HttpStatusCode.OK
        )
        {
            Content = new StringContent(
                """{"data_view":[{"id":"v1","title":"logs-*"}]}""",
                Encoding.UTF8,
                "application/json"
            ),
        });
        var client = new ElasticApiClient(new HttpClient(handler));
        var connection = new ElasticConnectionSettings
        {
            Id = "c1",
            Name = "ops",
            KibanaUrl = "https://kibana.example/s/ops/",
            ElasticsearchUrl = "https://elastic.example/",
            AuthMode = ElasticAuthMode.Basic,
            Username = "reader",
        };

        var views = await client.GetDataViewsAsync(connection, "secret");

        Assert.Equal("/s/ops/api/data_views", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal(
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("reader:secret")),
            handler.Requests[0].Headers.Authorization!.ToString()
        );
        Assert.Equal("logs-*", Assert.Single(views).Title);
    }

    [Fact]
    public async Task Client_ChecksElasticsearchRootEndpoint()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(
            HttpStatusCode.OK
        )
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        var client = new ElasticApiClient(new HttpClient(handler));
        var connection = new ElasticConnectionSettings
        {
            Id = "c1",
            Name = "ops",
            KibanaUrl = "https://kibana.example/",
            ElasticsearchUrl = "https://elastic.example/",
        };

        await client.CheckElasticsearchAsync(connection, null);

        Assert.Equal("https://elastic.example/", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Client_UsesMatchPhraseForConfiguredIdentityFields()
    {
        string? requestBody = null;
        var handler = new RecordingHttpMessageHandler(request =>
        {
            requestBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"hits\":{\"hits\":[]}}",
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        });
        var client = new ElasticApiClient(new HttpClient(handler));
        var connection = new ElasticConnectionSettings
        {
            Id = "c1",
            Name = "ops",
            KibanaUrl = "https://kibana.example/",
            ElasticsearchUrl = "https://elastic.example/",
        };
        var request = new ElasticSearchRequest(
            "logs-*",
            "@timestamp",
            DateTimeOffset.Parse("2026-08-25T12:00:00Z"),
            DateTimeOffset.Parse("2026-08-25T12:05:00Z"),
            "service.name",
            "api",
            ["message"],
            SortDescending: true
        );

        await client.SearchAsync(connection, null, "pit-1", request);

        using var body = JsonDocument.Parse(requestBody!);
        var filters = body
            .RootElement.GetProperty("query")
            .GetProperty("bool")
            .GetProperty("filter");
        Assert.Equal(2, filters.GetArrayLength());
        Assert.Equal("match_phrase", filters[0].EnumerateObject().Single().Name);
        Assert.Equal("range", filters[1].EnumerateObject().Single().Name);
        Assert.Equal(
            "desc",
            body.RootElement.GetProperty("sort")[0].GetProperty("@timestamp").GetString()
        );
    }

    [Fact]
    public async Task Client_PreservesNonDefaultPortAndEncodesApiKeyPair()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(
            HttpStatusCode.OK
        )
        {
            Content = new StringContent("{\"data_view\":[]}", Encoding.UTF8, "application/json"),
        });
        var client = new ElasticApiClient(new HttpClient(handler));
        var connection = new ElasticConnectionSettings
        {
            Id = "c1",
            Name = "ops",
            KibanaUrl = "http://127.0.0.1:5602/",
            ElasticsearchUrl = "http://127.0.0.1:9202/",
            AuthMode = ElasticAuthMode.ApiKey,
        };

        await client.GetDataViewsAsync(connection, "api-id:api-secret");

        Assert.Equal(5602, handler.Requests[0].RequestUri!.Port);
        Assert.Equal(
            "ApiKey " + Convert.ToBase64String(Encoding.UTF8.GetBytes("api-id:api-secret")),
            handler.Requests[0].Headers.Authorization!.ToString()
        );
    }

    [Fact]
    public async Task Client_ParsesKibana811WrappedDataViewFields()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(
            HttpStatusCode.OK
        )
        {
            Content = new StringContent(
                """
                {
                  "data_view": {
                    "id": "view-1",
                    "title": "logs-*",
                    "timeFieldName": "@timestamp",
                    "fields": {
                      "ident": { "type": "string", "searchable": true },
                      "jsonmessage.service": { "type": "string", "searchable": true }
                    }
                  }
                }
                """,
                Encoding.UTF8,
                "application/json"
            ),
        });
        var client = new ElasticApiClient(new HttpClient(handler));
        var connection = new ElasticConnectionSettings
        {
            Id = "c1",
            Name = "ops",
            KibanaUrl = "https://kibana/",
            ElasticsearchUrl = "https://elastic/",
        };

        var view = await client.GetDataViewAsync(connection, null, "view-1");

        Assert.Equal("logs-*", view.Title);
        Assert.Equal("@timestamp", view.TimeFieldName);
        Assert.Equal(["ident", "jsonmessage.service"], view.Fields.Select(field => field.Name));
    }

    [Fact]
    public async Task Client_MapsUnauthorizedAndTransientResponses()
    {
        var unauthorized = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(
            HttpStatusCode.Unauthorized
        ));
        var client = new ElasticApiClient(new HttpClient(unauthorized));
        var connection = new ElasticConnectionSettings
        {
            Id = "c1",
            Name = "ops",
            KibanaUrl = "https://kibana/",
            ElasticsearchUrl = "https://elastic/",
        };

        await Assert.ThrowsAsync<ElasticUnauthorizedException>(() =>
            client.GetDataViewsAsync(connection, null)
        );
    }

    [Fact]
    public async Task Client_AnonymousDoesNotSendAuthorizationHeader()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(
            HttpStatusCode.OK
        )
        {
            Content = new StringContent("{\"data_view\":[]}"),
        });
        var client = new ElasticApiClient(new HttpClient(handler));
        var connection = new ElasticConnectionSettings
        {
            Id = "c1",
            Name = "public",
            KibanaUrl = "https://kibana/",
            ElasticsearchUrl = "https://elastic/",
            AuthMode = ElasticAuthMode.Anonymous,
        };

        await client.GetDataViewsAsync(connection, null);

        Assert.Null(handler.Requests[0].Headers.Authorization);
    }

    [Fact]
    public async Task Client_ReportsHtmlInsteadOfJson()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(
            HttpStatusCode.OK
        )
        {
            Content = new StringContent("<html>Sign in</html>", Encoding.UTF8, "text/html"),
        });
        var client = new ElasticApiClient(new HttpClient(handler));
        var connection = new ElasticConnectionSettings
        {
            Id = "c1",
            Name = "ops",
            KibanaUrl = "https://kibana/",
            ElasticsearchUrl = "https://elastic/",
        };

        var exception = await Assert.ThrowsAsync<ElasticHttpException>(() =>
            client.GetDataViewsAsync(connection, null)
        );

        Assert.Contains("returned text/html instead of JSON", exception.Message);
        Assert.Contains("https://kibana/api/data_views", exception.Message);
        Assert.Contains("Elasticsearch URL", exception.Message);
    }

    private static ElasticConnectionSettings CountPreviewConnection() =>
        new()
        {
            Id = "c1",
            Name = "ops",
            KibanaUrl = "https://kibana.example/",
            ElasticsearchUrl = "https://elastic.example/",
        };

    [Fact]
    public async Task CountAsync_PostsFilteredCountAndReturnsCount()
    {
        string? requestBody = null;
        var handler = new RecordingHttpMessageHandler(request =>
        {
            requestBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"count": 3200}""",
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        });
        var client = new ElasticApiClient(new HttpClient(handler));
        var from = DateTimeOffset.Parse("2026-08-25T12:00:00Z");
        var to = DateTimeOffset.Parse("2026-08-25T12:05:00Z");

        var count = await client.CountAsync(
            CountPreviewConnection(),
            null,
            "logs-*,metrics-*",
            "@timestamp",
            "service.name",
            "api",
            from,
            to,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(3200, count);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("elastic.example", sent.RequestUri!.Host);
        Assert.EndsWith("/_count", sent.RequestUri.AbsolutePath);
        // Elasticsearch decodes the path segment, so it must round-trip to the original pattern.
        Assert.Equal(
            "/logs-*,metrics-*/_count",
            Uri.UnescapeDataString(sent.RequestUri.AbsolutePath)
        );
        using var body = JsonDocument.Parse(requestBody!);
        var filters = body
            .RootElement.GetProperty("query")
            .GetProperty("bool")
            .GetProperty("filter");
        Assert.Equal(2, filters.GetArrayLength());
        Assert.Equal(
            "api",
            filters[0].GetProperty("match_phrase").GetProperty("service.name").GetString()
        );
        var range = filters[1].GetProperty("range").GetProperty("@timestamp");
        Assert.Equal(from, range.GetProperty("gte").GetDateTimeOffset());
        Assert.Equal(to, range.GetProperty("lte").GetDateTimeOffset());
    }

    [Fact]
    public async Task CountAsync_MapsUnauthorizedResponse()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(
            HttpStatusCode.Unauthorized
        ));
        var client = new ElasticApiClient(new HttpClient(handler));

        await Assert.ThrowsAsync<ElasticUnauthorizedException>(() =>
            client.CountAsync(
                CountPreviewConnection(),
                null,
                "logs-*",
                "@timestamp",
                "service.name",
                "api",
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddMinutes(1),
                TestContext.Current.CancellationToken
            )
        );
    }

    private const string PreviewResponse = """
        {
          "hits": {
            "hits": [
              {
                "_id": "doc-1",
                "_source": { "@timestamp": "2026-08-25T12:00:00Z", "message": "hello", "service": { "name": "api" } },
                "fields": { "level": ["warn"] },
                "sort": [1787659200000]
              }
            ]
          }
        }
        """;

    [Fact]
    public async Task PreviewAsync_SearchesWithoutPitAndMapsHits()
    {
        string? requestBody = null;
        var handler = new RecordingHttpMessageHandler(request =>
        {
            requestBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(PreviewResponse, Encoding.UTF8, "application/json"),
            };
        });
        var client = new ElasticApiClient(new HttpClient(handler));

        var hits = await client.PreviewAsync(
            CountPreviewConnection(),
            null,
            "logs-*,metrics-*",
            "@timestamp",
            "service.name",
            "api",
            ["level", "message"],
            25,
            TestContext.Current.CancellationToken
        );

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.EndsWith("/_search", sent.RequestUri!.AbsolutePath);
        Assert.Equal(
            "/logs-*,metrics-*/_search",
            Uri.UnescapeDataString(sent.RequestUri.AbsolutePath)
        );
        using var body = JsonDocument.Parse(requestBody!);
        Assert.Equal(25, body.RootElement.GetProperty("size").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("pit", out _));
        Assert.Equal(
            "desc",
            body.RootElement.GetProperty("sort")[0].GetProperty("@timestamp").GetString()
        );
        Assert.Equal(
            ["level", "message"],
            body.RootElement.GetProperty("fields").EnumerateArray().Select(e => e.GetString())
        );
        var filters = body
            .RootElement.GetProperty("query")
            .GetProperty("bool")
            .GetProperty("filter");
        var filter = Assert.Single(filters.EnumerateArray());
        Assert.Equal(
            "api",
            filter.GetProperty("match_phrase").GetProperty("service.name").GetString()
        );
        var hit = Assert.Single(hits);
        Assert.Equal("doc-1", hit.Id);
        Assert.Equal(DateTimeOffset.Parse("2026-08-25T12:00:00Z"), hit.Timestamp);
        Assert.Equal("warn hello", hit.Line.Raw);
        Assert.Equal("api", hit.Line.ParsedFields!["service.name"]);
        Assert.Equal("hello", hit.Line.ParsedFields["message"]);
        Assert.Equal(hit.Timestamp, hit.Line.Timestamp);
        Assert.False(hit.Line.TimestampInherited);
    }

    [Fact]
    public async Task SearchAsync_StampsEachLineWithItsHitTimestamp()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(
            HttpStatusCode.OK
        )
        {
            Content = new StringContent(PreviewResponse, Encoding.UTF8, "application/json"),
        });
        var client = new ElasticApiClient(new HttpClient(handler));
        var request = new ElasticSearchRequest(
            "logs-*",
            "@timestamp",
            DateTimeOffset.Parse("2026-08-25T12:00:00Z"),
            DateTimeOffset.Parse("2026-08-25T12:05:00Z"),
            "service.name",
            "api",
            ["message"]
        );

        var page = await client.SearchAsync(
            CountPreviewConnection(),
            null,
            "pit-1",
            request,
            TestContext.Current.CancellationToken
        );

        var hit = Assert.Single(page.Hits);
        Assert.Equal(DateTimeOffset.Parse("2026-08-25T12:00:00Z"), hit.Line.Timestamp);
        Assert.Equal(hit.Timestamp, hit.Line.Timestamp);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("service.name", null)]
    [InlineData("", "api")]
    [InlineData("  ", "  ")]
    public async Task PreviewAsync_WithoutCompleteFilter_SendsNoMatchPhrase(
        string? field,
        string? value
    )
    {
        string? requestBody = null;
        var handler = new RecordingHttpMessageHandler(request =>
        {
            requestBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(PreviewResponse, Encoding.UTF8, "application/json"),
            };
        });
        var client = new ElasticApiClient(new HttpClient(handler));

        await client.PreviewAsync(
            CountPreviewConnection(),
            null,
            "logs-*",
            "@timestamp",
            field,
            value,
            ["message"],
            10,
            TestContext.Current.CancellationToken
        );

        Assert.DoesNotContain("match_phrase", requestBody);
        using var body = JsonDocument.Parse(requestBody!);
        Assert.Equal(
            0,
            body.RootElement.GetProperty("query")
                .GetProperty("bool")
                .GetProperty("filter")
                .GetArrayLength()
        );
    }
}
