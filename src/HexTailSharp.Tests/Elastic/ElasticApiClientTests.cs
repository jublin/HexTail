using System.Net;
using System.Text;
using HexTailSharp.Elastic;
using HexTailSharp.Persistence;
using HexTailSharp.Tests.Support;

namespace HexTailSharp.Tests.Elastic;

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
}
