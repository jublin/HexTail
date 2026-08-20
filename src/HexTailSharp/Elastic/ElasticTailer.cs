using System.Threading.Channels;
using HexTailSharp.Persistence;
using HexTailSharp.Security;
using HexTailSharp.Tailing;

namespace HexTailSharp.Elastic;

internal sealed class ElasticTailer : ILogTailer
{
    internal static readonly TimeSpan InitialLookback = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan UnauthorizedDelay = TimeSpan.FromSeconds(30);
    internal const int PageSize = 1_000;

    private readonly ElasticConnectionSettings _connection;
    private readonly ElasticSourceSettings _source;
    private readonly string _secret;
    private readonly IElasticApiClient _client;
    private readonly ChannelWriter<SourceEvent> _events;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<string> _idsAtCursor = new(StringComparer.Ordinal);
    private Task? _completion;
    private DateTimeOffset? _cursorTimestamp;
    private int _disposed;

    internal ElasticTailer(
        ElasticConnectionSettings connection,
        ElasticSourceSettings source,
        string secret,
        IElasticApiClient client,
        ChannelWriter<SourceEvent> events,
        Func<DateTimeOffset>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null
    )
    {
        _connection = connection;
        _source = source;
        _secret = secret;
        _client = client;
        _events = events;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
        SourceId = source.Id;
        DisplayName = source.DisplayName;
    }

    public string SourceId { get; }
    public string DisplayName { get; }
    public Task Completion => _completion ?? Task.CompletedTask;

    internal void Start() => _completion = Task.Run(RunAsync);

    internal async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var toInclusive = _utcNow();
        var fromInclusive = _cursorTimestamp ?? toInclusive - InitialLookback;
        var pitId = await _client.OpenPitAsync(
            _connection,
            _secret,
            _connection.DataViewTitle!,
            cancellationToken
        );
        var accepted = new List<Domain.Line>();
        try
        {
            IReadOnlyList<System.Text.Json.JsonElement>? searchAfter = null;
            while (true)
            {
                var page = await _client.SearchAsync(
                    _connection,
                    _secret,
                    pitId,
                    new ElasticSearchRequest(
                        _connection.DataViewTitle!,
                        _connection.TimeFieldName!,
                        fromInclusive,
                        toInclusive,
                        _connection.ServerField!,
                        _source.ServerValue,
                        _connection.NamespaceField!,
                        _source.NamespaceValue,
                        _connection.OutputFields,
                        searchAfter
                    ),
                    cancellationToken
                );
                pitId = page.PitId;
                foreach (var hit in page.Hits)
                {
                    if (_cursorTimestamp == hit.Timestamp && _idsAtCursor.Contains(hit.Id))
                        continue;
                    if (_cursorTimestamp is null || hit.Timestamp > _cursorTimestamp)
                    {
                        _cursorTimestamp = hit.Timestamp;
                        _idsAtCursor.Clear();
                    }
                    _idsAtCursor.Add(hit.Id);
                    accepted.Add(hit.Line);
                }
                if (page.Hits.Count < PageSize)
                    break;
                searchAfter = page.Hits[^1].SortValues;
            }
            if (accepted.Count > 0)
                _events.TryWrite(new SourceLines(SourceId, accepted));
        }
        finally
        {
            await _client.ClosePitAsync(_connection, _secret, pitId, CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stop.Cancel();
        if (_completion is not null)
        {
            try
            {
                await _completion.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }
        _stop.Dispose();
    }

    private async Task RunAsync()
    {
        var reportedError = false;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(_stop.Token).ConfigureAwait(false);
                if (reportedError)
                    _events.TryWrite(new SourceRecovered(SourceId));
                reportedError = false;
                await _delay(PollInterval, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                break;
            }
            catch (ElasticUnauthorizedException exception)
            {
                ReportError(exception.Message, ref reportedError);
                await _delay(UnauthorizedDelay, _stop.Token).ConfigureAwait(false);
            }
            catch (ElasticTransientException exception)
            {
                ReportError(exception.Message, ref reportedError);
                await _delay(TimeSpan.FromSeconds(2), _stop.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                ReportError(exception.Message, ref reportedError);
                await _delay(PollInterval, _stop.Token).ConfigureAwait(false);
            }
        }
    }

    private void ReportError(string message, ref bool reportedError)
    {
        if (!reportedError)
            _events.TryWrite(new SourceError(SourceId, message));
        reportedError = true;
    }
}
