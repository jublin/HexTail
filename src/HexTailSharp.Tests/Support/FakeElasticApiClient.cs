using System.Text.Json;
using HexTailSharp.Elastic;
using HexTailSharp.Persistence;

namespace HexTailSharp.Tests.Support;

internal sealed class FakeElasticApiClient : IElasticApiClient
{
    public Queue<ElasticSearchPage> Pages { get; } = [];
    public List<ElasticSearchRequest> Searches { get; } = [];
    public List<string> ClosedPitIds { get; } = [];
    public string PitId { get; set; } = "pit-1";
    public Exception? DataViewError { get; set; }
    public Exception? HealthError { get; set; }
    public IReadOnlyList<ElasticDataViewSummary> DataViews { get; set; } = [];

    public Task<IReadOnlyList<ElasticDataViewSummary>> GetDataViewsAsync(
        ElasticConnectionSettings c,
        string? s,
        CancellationToken t = default
    ) => Task.FromResult(DataViews);

    public Task<ElasticDataView> GetDataViewAsync(
        ElasticConnectionSettings c,
        string? s,
        string id,
        CancellationToken t = default
    )
    {
        if (DataViewError is not null)
            throw DataViewError;
        return Task.FromResult(new ElasticDataView(id, c.DataViewTitle!, c.TimeFieldName, []));
    }

    public Task<string> OpenPitAsync(
        ElasticConnectionSettings c,
        string? s,
        string title,
        CancellationToken t = default
    ) => Task.FromResult(PitId);

    public Task<ElasticSearchPage> SearchAsync(
        ElasticConnectionSettings c,
        string? s,
        string pit,
        ElasticSearchRequest request,
        CancellationToken t = default
    )
    {
        Searches.Add(request);
        return Task.FromResult(Pages.Dequeue());
    }

    public Task ClosePitAsync(
        ElasticConnectionSettings c,
        string? s,
        string pit,
        CancellationToken t = default
    )
    {
        ClosedPitIds.Add(pit);
        return Task.CompletedTask;
    }

    public Task CheckHealthAsync(
        ElasticConnectionSettings c,
        string? s,
        ElasticSearchRequest r,
        CancellationToken t = default
    )
    {
        if (HealthError is not null)
            throw HealthError;
        return Task.CompletedTask;
    }
}
