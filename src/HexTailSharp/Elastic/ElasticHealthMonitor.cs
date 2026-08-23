using HexTailSharp.Persistence;
using HexTailSharp.Security;

namespace HexTailSharp.Elastic;

public sealed class ElasticHealthMonitor : IAsyncDisposable
{
    private readonly IElasticApiClient _client;
    private readonly ICredentialVault _vault;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, ElasticSourceHealth> _statuses = new(
        StringComparer.Ordinal
    );

    public ElasticHealthMonitor(
        IElasticApiClient client,
        ICredentialVault vault,
        Func<DateTimeOffset>? now = null
    )
    {
        _client = client;
        _vault = vault;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public IReadOnlyDictionary<string, ElasticSourceHealth> Statuses =>
        new Dictionary<string, ElasticSourceHealth>(_statuses);
    public bool HasWarning =>
        _statuses.Values.Any(status => status.Status != ElasticConnectionStatus.Connected);
    public event Action? Changed;

    public async Task CheckOnceAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default
    )
    {
        var checking = settings
            .ElasticConnections.SelectMany(connection =>
                connection.Views.SelectMany(view => view.Sources)
            )
            .GroupBy(source => source.Id, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => new ElasticSourceHealth(
                    group.Key,
                    ElasticConnectionStatus.Checking,
                    "Checking",
                    _now()
                ),
                StringComparer.Ordinal
            );
        Publish(checking);
        var next = new Dictionary<string, ElasticSourceHealth>(StringComparer.Ordinal);
        foreach (var connection in settings.ElasticConnections)
        {
            foreach (var view in connection.Views)
            {
                Exception? connectionError = null;
                try
                {
                    var secret = connection.AuthMode
                        is ElasticAuthMode.Basic
                            or ElasticAuthMode.ApiKey
                        ? _vault.Get(connection.Id)
                        : null;
                    if (
                        string.IsNullOrWhiteSpace(view.DataViewId)
                        || string.IsNullOrWhiteSpace(view.DataViewTitle)
                        || string.IsNullOrWhiteSpace(view.TimeFieldName)
                        || string.IsNullOrWhiteSpace(view.ServerField)
                        || string.IsNullOrWhiteSpace(view.NamespaceField)
                        || view.OutputFields.Count == 0
                    )
                        throw new ArgumentException("The Elastic view is incomplete.");
                    await _client.GetDataViewAsync(
                        connection,
                        secret,
                        view.DataViewId,
                        cancellationToken
                    );
                }
                catch (ElasticUnauthorizedException exception)
                {
                    connectionError = exception;
                }
                catch (ElasticTransientException exception)
                {
                    connectionError = exception;
                }
                catch (Exception exception)
                {
                    connectionError = exception;
                }
                foreach (var source in view.Sources)
                {
                    var status = connectionError switch
                    {
                        ElasticUnauthorizedException => ElasticConnectionStatus.Unauthorized,
                        ElasticTransientException => ElasticConnectionStatus.Unreachable,
                        ArgumentException => ElasticConnectionStatus.Misconfigured,
                        not null => ElasticConnectionStatus.Unreachable,
                        _ => ElasticConnectionStatus.Connected,
                    };
                    var message = connectionError?.Message ?? "Connected";
                    if (connectionError is null)
                    {
                        var secret = connection.AuthMode
                            is ElasticAuthMode.Basic
                                or ElasticAuthMode.ApiKey
                            ? _vault.Get(connection.Id)
                            : null;
                        try
                        {
                            await _client.CheckHealthAsync(
                                connection,
                                secret,
                                new ElasticSearchRequest(
                                    view.DataViewTitle!,
                                    view.TimeFieldName!,
                                    _now().AddMinutes(-1),
                                    _now(),
                                    view.ServerField!,
                                    source.ServerValue,
                                    view.NamespaceField!,
                                    source.NamespaceValue,
                                    []
                                ),
                                cancellationToken
                            );
                        }
                        catch (ElasticUnauthorizedException exception)
                        {
                            status = ElasticConnectionStatus.Unauthorized;
                            message = exception.Message;
                        }
                        catch (ElasticTransientException exception)
                        {
                            status = ElasticConnectionStatus.Unreachable;
                            message = exception.Message;
                        }
                        catch (Exception exception)
                        {
                            status = ElasticConnectionStatus.Unreachable;
                            message = exception.Message;
                        }
                    }
                    next[source.Id] = new ElasticSourceHealth(source.Id, status, message, _now());
                }
            }
        }
        Publish(next);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void Publish(IReadOnlyDictionary<string, ElasticSourceHealth> next)
    {
        var changed =
            _statuses.Count != next.Count
            || next.Any(item =>
                !_statuses.TryGetValue(item.Key, out var previous)
                || previous.Status != item.Value.Status
                || previous.Message != item.Value.Message
            );
        if (!changed)
        {
            foreach (var item in next)
                _statuses[item.Key] = item.Value;
            return;
        }
        _statuses.Clear();
        foreach (var item in next)
            _statuses[item.Key] = item.Value;
        Changed?.Invoke();
    }
}
