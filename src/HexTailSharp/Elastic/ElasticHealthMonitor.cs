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
        var next = new Dictionary<string, ElasticSourceHealth>(StringComparer.Ordinal);
        foreach (var connection in settings.ElasticConnections)
        {
            ElasticDataView? view = null;
            Exception? connectionError = null;
            try
            {
                var secret = connection.AuthMode is ElasticAuthMode.Basic or ElasticAuthMode.ApiKey
                    ? _vault.Get(connection.Id)
                    : null;
                if (
                    string.IsNullOrWhiteSpace(connection.DataViewId)
                    || string.IsNullOrWhiteSpace(connection.TimeFieldName)
                    || string.IsNullOrWhiteSpace(connection.ServerField)
                    || string.IsNullOrWhiteSpace(connection.NamespaceField)
                )
                    throw new ArgumentException("The Elastic connection is incomplete.");
                view = await _client.GetDataViewAsync(
                    connection,
                    secret,
                    connection.DataViewId,
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
            foreach (var source in connection.Sources)
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
                                connection.DataViewTitle!,
                                connection.TimeFieldName!,
                                _now().AddMinutes(-1),
                                _now(),
                                connection.ServerField!,
                                source.ServerValue,
                                connection.NamespaceField!,
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
        if (!_statuses.OrderBy(item => item.Key).SequenceEqual(next.OrderBy(item => item.Key)))
        {
            _statuses.Clear();
            foreach (var item in next)
                _statuses[item.Key] = item.Value;
            Changed?.Invoke();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
