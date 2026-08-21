using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HexTailSharp.Persistence;

namespace HexTailSharp.Elastic;

public sealed class ElasticApiClient : IElasticApiClient
{
    private readonly HttpClient httpClient;
    private readonly Func<DateTimeOffset> now;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ElasticApiClient(HttpClient httpClient, Func<DateTimeOffset>? now = null)
    {
        this.httpClient = httpClient;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<ElasticDataViewSummary>> GetDataViewsAsync(
        ElasticConnectionSettings connection,
        string? secret,
        CancellationToken cancellationToken = default
    )
    {
        using var document = await SendAsync(
            connection.KibanaUrl,
            connection,
            secret,
            HttpMethod.Get,
            "api/data_views",
            null,
            cancellationToken
        );
        return document
            .RootElement.GetProperty("data_view")
            .EnumerateArray()
            .Select(item => new ElasticDataViewSummary(
                item.GetProperty("id").GetString()!,
                item.GetProperty("title").GetString()!
            ))
            .ToArray();
    }

    public async Task<ElasticDataView> GetDataViewAsync(
        ElasticConnectionSettings connection,
        string? secret,
        string dataViewId,
        CancellationToken cancellationToken = default
    )
    {
        var path = $"api/data_views/data_view/{Uri.EscapeDataString(dataViewId)}";
        using var document = await SendAsync(
            connection.KibanaUrl,
            connection,
            secret,
            HttpMethod.Get,
            path,
            null,
            cancellationToken
        );
        var root = document.RootElement.TryGetProperty("data_view", out var dataView)
            ? dataView
            : document.RootElement;
        var fields =
            root.TryGetProperty("fields", out var fieldObject)
            && fieldObject.ValueKind == JsonValueKind.Object
                ? fieldObject
                    .EnumerateObject()
                    .Select(property => new ElasticDataViewField(
                        property.Name,
                        property.Value.TryGetProperty("type", out var type)
                            ? type.GetString() ?? string.Empty
                            : string.Empty,
                        !property.Value.TryGetProperty("searchable", out var searchable)
                            || searchable.GetBoolean()
                    ))
                    .ToArray()
                : [];
        return new ElasticDataView(
            root.GetProperty("id").GetString() ?? dataViewId,
            root.TryGetProperty("title", out var title)
                ? title.GetString() ?? string.Empty
                : root.GetProperty("name").GetString() ?? string.Empty,
            root.TryGetProperty("timeFieldName", out var time) ? time.GetString() : null,
            fields
        );
    }

    public async Task<string> OpenPitAsync(
        ElasticConnectionSettings connection,
        string? secret,
        string dataViewTitle,
        CancellationToken cancellationToken = default
    )
    {
        var path = $"{Uri.EscapeDataString(dataViewTitle)}/_pit?keep_alive=1m";
        using var document = await SendAsync(
            connection.ElasticsearchUrl,
            connection,
            secret,
            HttpMethod.Post,
            path,
            null,
            cancellationToken
        );
        return document.RootElement.GetProperty("id").GetString()!;
    }

    public async Task<ElasticSearchPage> SearchAsync(
        ElasticConnectionSettings connection,
        string? secret,
        string pitId,
        ElasticSearchRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var filters = new List<object>
        {
            new
            {
                term = new Dictionary<string, string>
                {
                    [request.ServerField] = request.ServerValue,
                },
            },
            new
            {
                term = new Dictionary<string, string>
                {
                    [request.NamespaceField] = request.NamespaceValue,
                },
            },
            new
            {
                range = new Dictionary<string, object>
                {
                    [request.TimeFieldName] = new
                    {
                        gte = request.FromInclusive,
                        lte = request.ToInclusive,
                    },
                },
            },
        };
        var body = new Dictionary<string, object?>
        {
            ["size"] = 1000,
            ["track_total_hits"] = false,
            ["pit"] = new { id = pitId, keep_alive = "1m" },
            ["query"] = new { @bool = new { filter = filters } },
            ["sort"] = new object[]
            {
                new Dictionary<string, string> { [request.TimeFieldName] = "asc" },
                "_shard_doc",
            },
            ["_source"] = true,
            ["fields"] = request.OutputFields,
        };
        if (request.SearchAfter is { Count: > 0 })
            body["search_after"] = request.SearchAfter;
        using var document = await SendAsync(
            connection.ElasticsearchUrl,
            connection,
            secret,
            HttpMethod.Post,
            "_search",
            body,
            cancellationToken
        );
        var root = document.RootElement;
        var hits = root.GetProperty("hits")
            .GetProperty("hits")
            .EnumerateArray()
            .Select(hit =>
            {
                var source = hit.TryGetProperty("_source", out var sourceElement)
                    ? sourceElement
                    : default;
                var fields = hit.TryGetProperty("fields", out var fieldsElement)
                    ? fieldsElement
                    : default;
                var timestamp = source.GetProperty(request.TimeFieldName).GetDateTimeOffset();
                var sort = hit.GetProperty("sort")
                    .EnumerateArray()
                    .Select(value => value.Clone())
                    .ToArray();
                return new ElasticHit(
                    hit.GetProperty("_id").GetString()!,
                    timestamp,
                    ElasticDocumentMapper.Map(source, fields, request.OutputFields),
                    sort
                );
            })
            .ToArray();
        var pit = root.TryGetProperty("pit_id", out var pitElement)
            ? pitElement.GetString() ?? pitId
            : pitId;
        return new ElasticSearchPage(pit, hits);
    }

    public async Task ClosePitAsync(
        ElasticConnectionSettings connection,
        string? secret,
        string pitId,
        CancellationToken cancellationToken = default
    )
    {
        using var document = await SendAsync(
            connection.ElasticsearchUrl,
            connection,
            secret,
            HttpMethod.Delete,
            "_pit",
            new { id = pitId },
            cancellationToken
        );
    }

    public async Task CheckHealthAsync(
        ElasticConnectionSettings connection,
        string? secret,
        ElasticSearchRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var body = new
        {
            size = 0,
            track_total_hits = false,
            query = new
            {
                @bool = new
                {
                    filter = new object[]
                    {
                        new
                        {
                            term = new Dictionary<string, string>
                            {
                                [request.ServerField] = request.ServerValue,
                            },
                        },
                        new
                        {
                            term = new Dictionary<string, string>
                            {
                                [request.NamespaceField] = request.NamespaceValue,
                            },
                        },
                    },
                },
            },
        };
        using var document = await SendAsync(
            connection.ElasticsearchUrl,
            connection,
            secret,
            HttpMethod.Post,
            "_search",
            body,
            cancellationToken
        );
    }

    private async Task<JsonDocument> SendAsync(
        string baseUrl,
        ElasticConnectionSettings connection,
        string? secret,
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken
    )
    {
        using var request = new HttpRequestMessage(
            method,
            new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), path)
        );
        Log($"{method} {request.RequestUri} auth={connection.AuthMode}");
        AddAuthorization(request, connection, secret);
        if (body is not null)
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions),
                Encoding.UTF8,
                "application/json"
            );
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            Log($"{method} {request.RequestUri} transport failure: {exception.Message}");
            throw new ElasticTransientException(0, exception.Message);
        }
        using (response)
        {
            var text = await response
                .Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            Log(
                $"{method} {request.RequestUri} -> {(int)response.StatusCode} "
                    + $"{response.Content.Headers.ContentType?.MediaType ?? "unknown"} "
                    + $"bytes={text.Length}"
            );
            if (!response.IsSuccessStatusCode)
            {
                var reason = string.IsNullOrWhiteSpace(text)
                    ? response.ReasonPhrase ?? "Unknown error"
                    : text;
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    if (
                        response.StatusCode == HttpStatusCode.Unauthorized
                        && connection.AuthMode == ElasticAuthMode.Anonymous
                    )
                        reason =
                            "The Elastic server rejected anonymous access (401). "
                            + "Grant anonymous access to the requested Kibana/Elasticsearch APIs "
                            + "or choose Basic authentication/API key."
                            + (string.IsNullOrWhiteSpace(text) ? string.Empty : $" {text}");
                    throw new ElasticUnauthorizedException((int)response.StatusCode, reason);
                }
                if (
                    (int)response.StatusCode == 408
                    || response.StatusCode == HttpStatusCode.TooManyRequests
                    || (int)response.StatusCode >= 500
                )
                    throw new ElasticTransientException((int)response.StatusCode, reason);
                throw new ElasticHttpException((int)response.StatusCode, reason);
            }
            try
            {
                return JsonDocument.Parse(text);
            }
            catch (JsonException)
            {
                var contentType = response.Content.Headers.ContentType?.MediaType ?? "unknown";
                Log($"{method} {request.RequestUri} returned non-JSON content");
                throw new ElasticHttpException(
                    (int)response.StatusCode,
                    $"{request.Method} {request.RequestUri} returned {contentType} instead of JSON. Check that the "
                        + "Elasticsearch URL points to Elasticsearch and the Kibana URL points "
                        + "to Kibana, including any reverse-proxy path."
                );
            }
        }
    }

    private void Log(string message) => Console.Error.WriteLine($"[Elastic] {now():O} {message}");

    private static void AddAuthorization(
        HttpRequestMessage request,
        ElasticConnectionSettings connection,
        string? secret
    )
    {
        request.Headers.Authorization = connection.AuthMode switch
        {
            ElasticAuthMode.Anonymous => null,
            ElasticAuthMode.Basic when connection.Username is not null && secret is not null =>
                new AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(
                        Encoding.UTF8.GetBytes($"{connection.Username}:{secret}")
                    )
                ),
            ElasticAuthMode.ApiKey when !string.IsNullOrWhiteSpace(secret) =>
                new AuthenticationHeaderValue("ApiKey", NormalizeApiKey(secret)),
            _ => throw new InvalidOperationException("Elastic authentication is incomplete."),
        };
    }

    private static string NormalizeApiKey(string secret) =>
        secret.Contains(':', StringComparison.Ordinal)
            ? Convert.ToBase64String(Encoding.UTF8.GetBytes(secret))
            : secret.Trim();
}
