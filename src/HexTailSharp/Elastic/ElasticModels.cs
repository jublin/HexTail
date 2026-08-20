using System.Text.Json;
using HexTailSharp.Domain;
using HexTailSharp.Persistence;

namespace HexTailSharp.Elastic;

public sealed record ElasticDataViewSummary(string Id, string Title);

public sealed record ElasticDataView(
    string Id,
    string Title,
    string? TimeFieldName,
    IReadOnlyList<ElasticDataViewField> Fields
);

public sealed record ElasticDataViewField(string Name, string Type, bool Searchable);

public sealed record ElasticSearchRequest(
    string DataViewTitle,
    string TimeFieldName,
    DateTimeOffset FromInclusive,
    DateTimeOffset ToInclusive,
    string ServerField,
    string ServerValue,
    string NamespaceField,
    string NamespaceValue,
    IReadOnlyList<string> OutputFields,
    IReadOnlyList<JsonElement>? SearchAfter = null
);

public sealed record ElasticHit(
    string Id,
    DateTimeOffset Timestamp,
    Line Line,
    IReadOnlyList<JsonElement> SortValues
);

public sealed record ElasticSearchPage(string PitId, IReadOnlyList<ElasticHit> Hits);

public class ElasticHttpException(int statusCode, string reason)
    : Exception($"Elastic request failed ({statusCode}): {reason}")
{
    public int StatusCode { get; } = statusCode;
}

public sealed class ElasticUnauthorizedException(int statusCode, string reason)
    : ElasticHttpException(statusCode, reason);

public sealed class ElasticTransientException(int statusCode, string reason)
    : ElasticHttpException(statusCode, reason);
