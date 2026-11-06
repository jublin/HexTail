using System.Text.Json;
using HexTail.Elastic;
using HexTail.Persistence;

namespace HexTail.Tests.Support;

internal sealed class FakeElasticApiClient : IElasticApiClient
{
    public Func<ElasticSearchRequest, Task<ElasticSearchPage>>? SearchHandler { get; set; }
    public Func<string, Task<ElasticDataView>>? DataViewHandler { get; set; }
    public Func<Task<IReadOnlyList<ElasticDataViewSummary>>>? DataViewsHandler { get; set; }
    public Func<(string Field, string Value), Task<long>>? CountHandler { get; set; }
    public Func<
        IReadOnlyList<string>,
        Task<IReadOnlyList<ElasticHit>>
    >? PreviewHandler { get; set; }
    public List<(string Field, string Value)> Counts { get; } = [];
    public Queue<ElasticSearchPage> Pages { get; } = [];
    public List<ElasticSearchRequest> Searches { get; } = [];
    public List<string> ClosedPitIds { get; } = [];
    public string PitId { get; set; } = "pit-1";
    public Exception? DataViewError { get; set; }
    public Exception? ElasticsearchError { get; set; }
    public Exception? HealthError { get; set; }
    public IReadOnlyList<ElasticDataViewSummary> DataViews { get; set; } = [];
    public IReadOnlyList<ElasticDataViewField> DataViewFields { get; set; } = [];
    public List<string?> DataViewSecrets { get; } = [];

    public Task<IReadOnlyList<ElasticDataViewSummary>> GetDataViewsAsync(
        ElasticConnectionSettings c,
        string? s,
        CancellationToken t = default
    )
    {
        DataViewSecrets.Add(s);
        return DataViewsHandler?.Invoke() ?? Task.FromResult(DataViews);
    }

    public Task CheckElasticsearchAsync(
        ElasticConnectionSettings c,
        string? s,
        CancellationToken t = default
    )
    {
        if (ElasticsearchError is not null)
            throw ElasticsearchError;
        return Task.CompletedTask;
    }

    public Task<ElasticDataView> GetDataViewAsync(
        ElasticConnectionSettings c,
        string? s,
        string id,
        CancellationToken t = default
    )
    {
        if (DataViewHandler is not null)
            return DataViewHandler(id);
        if (DataViewError is not null)
            throw DataViewError;
        var view = c.Views.FirstOrDefault();
        return Task.FromResult(
            new ElasticDataView(
                id,
                view?.DataViewTitle ?? c.DataViewTitle!,
                view?.TimeFieldName ?? c.TimeFieldName,
                DataViewFields
            )
        );
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
        return SearchHandler?.Invoke(request) ?? Task.FromResult(Pages.Dequeue());
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

    public Task<long> CountAsync(
        ElasticConnectionSettings c,
        string? s,
        string title,
        string timeField,
        string field,
        string value,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken t = default
    )
    {
        Counts.Add((field, value));
        return CountHandler?.Invoke((field, value)) ?? Task.FromResult(0L);
    }

    public Task<IReadOnlyList<ElasticHit>> PreviewAsync(
        ElasticConnectionSettings c,
        string? s,
        string title,
        string timeField,
        string? field,
        string? value,
        IReadOnlyList<string> outputFields,
        int size,
        CancellationToken t = default
    ) => PreviewHandler?.Invoke(outputFields) ?? Task.FromResult<IReadOnlyList<ElasticHit>>([]);
}
