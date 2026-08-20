namespace HexTailSharp.Persistence;

public enum ElasticAuthMode
{
    Anonymous,
    Basic,
    ApiKey,
}

public sealed record ElasticConnectionSettings
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string KibanaUrl { get; init; }
    public required string ElasticsearchUrl { get; init; }
    public ElasticAuthMode AuthMode { get; init; }
    public string? Username { get; init; }
    public string? DataViewId { get; init; }
    public string? DataViewTitle { get; init; }
    public string? TimeFieldName { get; init; }
    public string? ServerField { get; init; }
    public string? NamespaceField { get; init; }
    public List<string> OutputFields { get; init; } = [];
    public List<ElasticSourceSettings> Sources { get; init; } = [];
}

public sealed record ElasticSourceSettings
{
    public required string Id { get; init; }
    public required string ServerValue { get; init; }
    public required string NamespaceValue { get; init; }
    public string DisplayName => $"{ServerValue}-{NamespaceValue}";
}
