using System.Text.Json;
using HexTail.Persistence;

namespace HexTail.Elastic;

public interface IElasticApiClient
{
    Task<IReadOnlyList<ElasticDataViewSummary>> GetDataViewsAsync(
        ElasticConnectionSettings connection,
        string? secret,
        CancellationToken cancellationToken = default
    );

    Task CheckElasticsearchAsync(
        ElasticConnectionSettings connection,
        string? secret,
        CancellationToken cancellationToken = default
    );

    Task<ElasticDataView> GetDataViewAsync(
        ElasticConnectionSettings connection,
        string? secret,
        string dataViewId,
        CancellationToken cancellationToken = default
    );

    Task<string> OpenPitAsync(
        ElasticConnectionSettings connection,
        string? secret,
        string dataViewTitle,
        CancellationToken cancellationToken = default
    );

    Task<ElasticSearchPage> SearchAsync(
        ElasticConnectionSettings connection,
        string? secret,
        string pitId,
        ElasticSearchRequest request,
        CancellationToken cancellationToken = default
    );

    Task ClosePitAsync(
        ElasticConnectionSettings connection,
        string? secret,
        string pitId,
        CancellationToken cancellationToken = default
    );

    Task CheckHealthAsync(
        ElasticConnectionSettings connection,
        string? secret,
        ElasticSearchRequest request,
        CancellationToken cancellationToken = default
    );

    Task<long> CountAsync(
        ElasticConnectionSettings connection,
        string? secret,
        string dataViewTitle,
        string timeFieldName,
        string filterField,
        string filterValue,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<ElasticHit>> PreviewAsync(
        ElasticConnectionSettings connection,
        string? secret,
        string dataViewTitle,
        string timeFieldName,
        string? filterField,
        string? filterValue,
        IReadOnlyList<string> outputFields,
        int size,
        CancellationToken cancellationToken = default
    );
}
