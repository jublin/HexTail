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
    private readonly ElasticViewSettings _view;
    private readonly ElasticSourceSettings _source;
    private readonly string _secret;
    private readonly IElasticApiClient _client;
    private readonly ChannelWriter<SourceEvent> _events;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<string> _idsAtCursor = new(StringComparer.Ordinal);
    private readonly int _maxInitialLines;
    private string _fromExpression = $"now-{InitialLookback.TotalMinutes:0}m";
    private string _toExpression = "now";
    private Task? _completion;
    private DateTimeOffset? _cursorTimestamp;
    private int _disposed;

    internal ElasticTailer(
        ElasticConnectionSettings connection,
        ElasticViewSettings view,
        ElasticSourceSettings source,
        string secret,
        IElasticApiClient client,
        ChannelWriter<SourceEvent> events,
        Func<DateTimeOffset>? now = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TailerOptions? options = null
    )
    {
        _connection = connection;
        _view = view;
        _source = source;
        _secret = secret;
        _client = client;
        _events = events;
        _utcNow = now ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
        _maxInitialLines = Math.Max(1, options?.MaxInitialLines ?? 10_000);
        SourceId = source.Id;
        DisplayName = source.DisplayName;
    }

    public string SourceId { get; }
    public string DisplayName { get; }
    public Task Completion => _completion ?? Task.CompletedTask;

    internal void Start() => _completion = Task.Run(RunAsync);

    internal async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var toInclusive = ParseTime(_toExpression, _utcNow());
        var fromInclusive = _cursorTimestamp ?? ParseTime(_fromExpression, toInclusive);
        var initialRead = _cursorTimestamp is null;
        Log(
            $"source={SourceId} poll dataView={_view.DataViewTitle} "
                + $"from={fromInclusive:O} to={toInclusive:O}"
        );
        var pitId = await _client.OpenPitAsync(
            _connection,
            _secret,
            _view.DataViewTitle!,
            cancellationToken
        );
        var accepted = new List<Domain.Line>(initialRead ? _maxInitialLines : PageSize);
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
                        _view.DataViewTitle!,
                        _view.TimeFieldName!,
                        fromInclusive,
                        toInclusive,
                        _view.ServerField!,
                        _source.ServerValue,
                        _view.OutputFields,
                        searchAfter,
                        SortDescending: initialRead
                    ),
                    cancellationToken
                );
                pitId = page.PitId;
                Log($"source={SourceId} search page hits={page.Hits.Count}");
                foreach (var hit in page.Hits)
                {
                    if (_cursorTimestamp == hit.Timestamp && _idsAtCursor.Contains(hit.Id))
                        continue;
                    if (_cursorTimestamp is null || hit.Timestamp > _cursorTimestamp)
                    {
                        _cursorTimestamp = hit.Timestamp;
                        _idsAtCursor.Clear();
                    }
                    if (_cursorTimestamp == hit.Timestamp)
                        _idsAtCursor.Add(hit.Id);
                    accepted.Add(hit.Line);
                    if (initialRead && accepted.Count == _maxInitialLines)
                        break;
                }
                if (
                    (initialRead && accepted.Count >= _maxInitialLines)
                    || page.Hits.Count < PageSize
                )
                    break;
                searchAfter = page.Hits[^1].SortValues;
            }
            if (accepted.Count > 0)
            {
                if (initialRead)
                    accepted.Reverse();
                Log($"source={SourceId} emitting lines={accepted.Count}");
                _events.TryWrite(new SourceLines(SourceId, accepted));
            }
        }
        finally
        {
            try
            {
                await _client.ClosePitAsync(_connection, _secret, pitId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
    }

    internal void SetTimeRange(string from, string to)
    {
        var now = _utcNow();
        _ = ParseTime(from, now);
        _ = ParseTime(to, now);
        _fromExpression = from.Trim();
        _toExpression = to.Trim();
        _cursorTimestamp = null;
        _idsAtCursor.Clear();
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
        var transientAttempt = 0;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(_stop.Token).ConfigureAwait(false);
                if (reportedError)
                    _events.TryWrite(new SourceRecovered(SourceId));
                reportedError = false;
                transientAttempt = 0;
                await _delay(PollInterval, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                break;
            }
            catch (ElasticUnauthorizedException exception)
            {
                Log($"source={SourceId} unauthorized: {exception.Message}");
                ReportError(exception.Message, ref reportedError);
                await _delay(UnauthorizedDelay, _stop.Token).ConfigureAwait(false);
            }
            catch (ElasticTransientException exception)
            {
                Log($"source={SourceId} transient failure: {exception.Message}");
                ReportError(exception.Message, ref reportedError);
                var seconds = Math.Min(30, 1 << Math.Min(transientAttempt++, 4));
                await _delay(TimeSpan.FromSeconds(seconds), _stop.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Log($"source={SourceId} failure: {exception.Message}");
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

    private static void Log(string message) =>
        Console.Error.WriteLine($"[Elastic] {DateTimeOffset.UtcNow:O} {message}");

    private static DateTimeOffset ParseTime(string expression, DateTimeOffset now)
    {
        var value = expression.Trim();
        if (string.Equals(value, "now", StringComparison.OrdinalIgnoreCase))
            return now;
        if (value.StartsWith("now-", StringComparison.OrdinalIgnoreCase))
        {
            var relative = value[4..];
            if (relative.Length > 1 && double.TryParse(relative[..^1], out var amount))
            {
                var duration = relative[^1] switch
                {
                    's' => TimeSpan.FromSeconds(amount),
                    'm' => TimeSpan.FromMinutes(amount),
                    'h' => TimeSpan.FromHours(amount),
                    'd' => TimeSpan.FromDays(amount),
                    _ => TimeSpan.MinValue,
                };
                if (duration != TimeSpan.MinValue)
                    return now - duration;
            }
        }
        if (DateTimeOffset.TryParse(value, out var timestamp))
            return timestamp;
        throw new ArgumentException(
            $"Invalid Elastic time expression '{expression}'. Use now, now-5m, or an ISO timestamp."
        );
    }
}
