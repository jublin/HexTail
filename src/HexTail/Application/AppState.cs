using HexTail.Domain;
using HexTail.Elastic;
using HexTail.Persistence;
using HexTail.Security;
using HexTail.Tailing;

namespace HexTail.Application;

public sealed class AppState : IAsyncDisposable
{
    private readonly LogSourceService _tailers;
    private readonly IAppPersistence _persistence;
    private readonly object _gate = new();
    private readonly ICredentialVault _credentials;
    private readonly IElasticApiClient _elastic;
    private readonly ElasticHealthMonitor _health;
    private readonly CancellationTokenSource _healthStop = new();
    private readonly SemaphoreSlim _healthSignal = new(0, 1);
    private readonly List<FileTabState> _files = [];
    public const int MaxRecent = 10;
    private readonly List<string> _recentFiles = [];
    private readonly List<string> _recentElasticSourceIds = [];
    private readonly List<SavedFilter> _savedFilters = [];
    private readonly List<StreamSettings> _streamDefs = [];

    // Open stream tabs (also in _files) and, per member tab id, the open streams that merge it.
    // Both are guarded by _gate; the drain looks members up here instead of scanning every stream.
    private readonly List<FileTabState> _streamTabs = [];
    private Dictionary<string, StreamState[]> _memberStreams = new(StringComparer.Ordinal);
    private AppSettings _settings;
    private int _nextFileId;
    private Task? _healthLoop;
    private int _disposed;
    private SourceLines? _pendingBatch;
    private int _pendingBatchOffset;
    internal const int MaxLinesPerDrain = 10_000;

    /// <summary>Test seam: how long a stream holds lines back for slower members. Read when a stream opens.</summary>
    internal TimeSpan StreamHold { get; set; } = StreamMerger.DefaultHold;

    /// <summary>Test seam: the clock streams use for line arrival times. Read when a stream opens.</summary>
    internal Func<DateTimeOffset> StreamClock { get; set; } = () => DateTimeOffset.UtcNow;
    private const int MaxEventsPerDrain = 128;

    public AppState(
        LogSourceService tailers,
        IAppPersistence persistence,
        AppSettings? settings = null,
        ICredentialVault? credentials = null,
        IElasticApiClient? elastic = null
    )
    {
        _tailers = tailers;
        _persistence = persistence;
        _credentials = credentials ?? new OsCredentialVault();
        _settings = NormalizeSettings(settings ?? new AppSettings());
        _elastic = elastic ?? new ElasticApiClient(new HttpClient(), Now);
        _health = new ElasticHealthMonitor(_elastic, _credentials, Now);
        _health.Changed += NotifyChanged;
    }

    public IReadOnlyList<FileTabState> Files
    {
        get
        {
            lock (_gate)
                return _files.ToArray();
        }
    }

    /// <summary>The saved stream definitions, open or not.</summary>
    public IReadOnlyList<StreamSettings> Streams
    {
        get
        {
            lock (_gate)
                return _streamDefs.ToArray();
        }
    }

    public IReadOnlyList<string> RecentFiles
    {
        get
        {
            lock (_gate)
                return _recentFiles.ToArray();
        }
    }

    public IReadOnlyList<SavedFilter> SavedFilters
    {
        get
        {
            lock (_gate)
                return [.. _savedFilters];
        }
    }

    public IReadOnlyList<string> RecentElasticSourceIds
    {
        get
        {
            lock (_gate)
                return _recentElasticSourceIds.ToArray();
        }
    }

    public FileTabState? SelectedFile { get; private set; }
    public AppWindowState Window { get; private set; } = new();
    public AppSettings Settings => _settings;
    public event Action? Changed;

    internal string? GetElasticSecret(string connectionId) => _credentials.Get(connectionId);

    public async ValueTask SaveElasticConnectionAsync(
        ElasticConnectionSettings connection,
        string? secret,
        CancellationToken cancellationToken = default
    )
    {
        var previousSettings = _settings;
        var previous = previousSettings.ElasticConnections.FirstOrDefault(item =>
            item.Id == connection.Id
        );
        var previousSecret = previous is null ? null : _credentials.Get(connection.Id);
        connection = NormalizeElasticConnections([connection]).Single();
        // A blank secret keeps the stored one only for the same login method on the same servers;
        // otherwise it stays blank and the validation below asks for a new one.
        secret =
            !string.IsNullOrWhiteSpace(secret) ? secret
            : StoredSecretApplies(previous, connection) ? previousSecret
            : null;
        ValidateElasticConnection(connection, secret);
        var authenticated = connection.AuthMode is ElasticAuthMode.Basic or ElasticAuthMode.ApiKey;
        if (authenticated)
            _credentials.Set(connection.Id, secret!);
        try
        {
            lock (_gate)
                _settings = NormalizeSettings(
                    previousSettings with
                    {
                        ElasticConnections = previousSettings
                            .ElasticConnections.Where(item => item.Id != connection.Id)
                            .Append(connection)
                            .ToList(),
                    }
                );
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
                _settings = previousSettings;
            if (authenticated)
            {
                if (previousSecret is null)
                    _credentials.Delete(connection.Id);
                else
                    _credentials.Set(connection.Id, previousSecret);
            }
            throw;
        }
        if (!authenticated && previous is not null)
            _credentials.Delete(connection.Id);
        await ReloadElasticConnectionAsync(connection, previous, secret ?? string.Empty)
            .ConfigureAwait(false);
        NotifyChanged();
        SignalHealthCheck();
    }

    private async ValueTask ReloadElasticConnectionAsync(
        ElasticConnectionSettings connection,
        ElasticConnectionSettings? previous,
        string secret
    )
    {
        var previousIds =
            previous
                ?.Views.SelectMany(view => view.Sources)
                .Select(source => source.Id)
                .ToHashSet(StringComparer.Ordinal)
            ?? [];
        var tabs = Files
            .Where(tab => tab.Source.Kind == LogSourceKind.Elastic && previousIds.Contains(tab.Id))
            .ToArray();
        foreach (var tab in tabs)
        {
            var match = connection
                .Views.SelectMany(view => view.Sources.Select(source => (view, source)))
                .FirstOrDefault(item => item.source.Id == tab.Id);
            if (match.source is null)
            {
                await CloseFileAsync(tab, save: false).ConfigureAwait(false);
                continue;
            }
            var replacement = _tailers.CreateElastic(
                connection,
                match.view,
                match.source,
                secret,
                _elastic,
                Now,
                maxInitialLines: _settings.ElasticInitialLines
            );
            replacement.SetTimeRange(
                tab.ElasticResolvedFrom?.ToString("O") ?? tab.ElasticFrom,
                tab.ElasticTo
            );
            var old = tab.Tailer;
            await old.DisposeAsync().ConfigureAwait(false);
            var installed = false;
            lock (_gate)
            {
                if (_files.Contains(tab))
                {
                    installed = true;
                    tab.Tailer = replacement;
                    tab.Source = LogSourceDescriptor.Elastic(connection, match.view, match.source);
                    tab.Buffer.Clear();
                    MarkStreamsForRebuild(tab.Id);
                    tab.Error = null;
                    tab.ElasticLoading = true;
                    tab.ElasticInitialLimitReached = null;
                    tab.ElasticResolvedTo = null;
                    replacement.Start();
                }
            }
            if (!installed)
                await replacement.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask RemoveElasticConnectionAsync(
        string connectionId,
        CancellationToken cancellationToken = default
    )
    {
        var previous = _settings;
        var sourceIds = previous
            .ElasticConnections.Where(connection => connection.Id == connectionId)
            .SelectMany(connection => connection.Views)
            .SelectMany(view => view.Sources)
            .Select(source => source.Id)
            .ToHashSet(StringComparer.Ordinal);
        (FileTabState Tab, int Index)[] removed;
        FileTabState? previousSelection;
        lock (_gate)
        {
            previousSelection = SelectedFile;
            removed = _files
                .Select((tab, index) => (Tab: tab, Index: index))
                .Where(item =>
                    item.Tab.Source.Kind == LogSourceKind.Elastic && sourceIds.Contains(item.Tab.Id)
                )
                .ToArray();
            foreach (var item in removed)
            {
                _files.Remove(item.Tab);
                ForgetStreamLinks(item.Tab);
            }
            if (removed.Any(item => item.Tab == SelectedFile))
                SelectedFile = _files.FirstOrDefault();
            _settings = NormalizeSettings(
                previous with
                {
                    ElasticConnections = previous
                        .ElasticConnections.Where(item => item.Id != connectionId)
                        .ToList(),
                }
            );
        }
        try
        {
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                _settings = previous;
                foreach (var item in removed)
                {
                    _files.Insert(Math.Min(item.Index, _files.Count), item.Tab);
                    AttachToStreams(item.Tab);
                }
                SelectedFile = previousSelection;
            }
            throw;
        }
        foreach (var item in removed)
            await item.Tab.DisposeAsync().ConfigureAwait(false);
        try
        {
            _credentials.Delete(connectionId);
        }
        finally
        {
            NotifyChanged();
            SignalHealthCheck();
        }
    }

    public Task<IReadOnlyList<ElasticDataViewSummary>> GetDataViewsAsync(
        ElasticConnectionSettings connection,
        string? secret = null,
        CancellationToken cancellationToken = default
    ) =>
        _elastic.GetDataViewsAsync(
            connection,
            string.IsNullOrWhiteSpace(secret) ? SecretFor(connection) : secret,
            cancellationToken
        );

    public Task CheckElasticsearchAsync(
        ElasticConnectionSettings connection,
        string? secret = null,
        CancellationToken cancellationToken = default
    ) =>
        _elastic.CheckElasticsearchAsync(
            connection,
            string.IsNullOrWhiteSpace(secret) ? SecretFor(connection) : secret,
            cancellationToken
        );

    public Task<ElasticDataView> GetDataViewAsync(
        ElasticConnectionSettings connection,
        string dataViewId,
        string? secret = null,
        CancellationToken cancellationToken = default
    ) =>
        _elastic.GetDataViewAsync(
            connection,
            string.IsNullOrWhiteSpace(secret) ? SecretFor(connection) : secret,
            dataViewId,
            cancellationToken
        );

    public Task<long> CountAsync(
        ElasticConnectionSettings connection,
        string dataViewTitle,
        string timeFieldName,
        string filterField,
        string filterValue,
        DateTimeOffset from,
        DateTimeOffset to,
        string? secret = null,
        CancellationToken cancellationToken = default
    ) =>
        _elastic.CountAsync(
            connection,
            string.IsNullOrWhiteSpace(secret) ? SecretFor(connection) : secret,
            dataViewTitle,
            timeFieldName,
            filterField,
            filterValue,
            from,
            to,
            cancellationToken
        );

    public Task<IReadOnlyList<ElasticHit>> PreviewAsync(
        ElasticConnectionSettings connection,
        string dataViewTitle,
        string timeFieldName,
        string? filterField,
        string? filterValue,
        IReadOnlyList<string> outputFields,
        int size,
        string? secret = null,
        CancellationToken cancellationToken = default
    ) =>
        _elastic.PreviewAsync(
            connection,
            string.IsNullOrWhiteSpace(secret) ? SecretFor(connection) : secret,
            dataViewTitle,
            timeFieldName,
            filterField,
            filterValue,
            outputFields,
            size,
            cancellationToken
        );

    private static void Remember(List<string> list, string value, StringComparer comparer)
    {
        list.RemoveAll(item => comparer.Equals(item, value));
        list.Insert(0, value);
        if (list.Count > MaxRecent)
            list.RemoveRange(MaxRecent, list.Count - MaxRecent);
    }

    public async ValueTask RestoreAsync(CancellationToken cancellationToken = default)
    {
        var config = await _persistence.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (config is null)
            return;

        lock (_gate)
        {
            _settings = NormalizeSettings(config.Settings ?? new AppSettings());
            Window = config.Window ?? new AppWindowState();
            _savedFilters.Clear();
            foreach (var filter in config.SavedFilters ?? [])
                UpsertSavedFilter(filter?.Name, filter?.Rules, filter?.Levels ?? LogLevels.All);
            _streamDefs.Clear();
            _streamDefs.AddRange(StreamSettings.Normalize(config.Streams));
        }
        foreach (var persisted in config.OpenFiles)
        {
            var tab = await OpenFileAsync(
                    persisted.Path,
                    save: false,
                    cancellationToken,
                    remember: false
                )
                .ConfigureAwait(false);
            tab.FollowAll = persisted.FollowAll;
            tab.SetRules(NormalizeRules(persisted.Rules, RuleScope.Source));
            tab.Levels = NormalizeLevels(persisted.Levels);
            tab.SelectedLine = persisted.SelectedLine;
            tab.Columns = ColumnLayout.FromPersisted(
                persisted.Columns,
                persisted.ComposedRows,
                persisted.TimeDisplay
            );

            // Global-rule searches were added when the tab opened; the persisted lists only hold user
            // searches, so each follow flag is applied to the search it was saved with.
            for (var i = 0; i < persisted.Searches.Count; i++)
            {
                var search = persisted.Searches[i];
                try
                {
                    var active = new Search(
                        new CompiledQuery(search.Query, search.Mode, search.CaseSensitive),
                        search.Color,
                        tab.Buffer
                    );
                    tab.AddSearch(active);
                    if (i < persisted.FollowSearches.Count)
                        tab.FollowSearches[^1] = persisted.FollowSearches[i];
                }
                catch (ArgumentException)
                {
                    // A stale invalid regex must not prevent the rest of the session from loading.
                }
            }
        }
        foreach (var persisted in config.OpenElasticTabs)
        {
            try
            {
                var tab = await OpenElasticSourceAsync(
                        persisted.SourceId,
                        save: false,
                        cancellationToken,
                        persisted.From,
                        persisted.To,
                        persisted.InputZone,
                        remember: false
                    )
                    .ConfigureAwait(false);
                tab.FollowAll = persisted.FollowAll;
                tab.SetRules(NormalizeRules(persisted.Rules, RuleScope.Source));
                tab.Levels = NormalizeLevels(persisted.Levels);
                tab.Columns = ColumnLayout.FromPersisted(
                    persisted.Columns,
                    persisted.ComposedRows,
                    persisted.TimeDisplay
                );
                for (var i = 0; i < persisted.Searches.Count; i++)
                {
                    var search = persisted.Searches[i];
                    try
                    {
                        tab.AddSearch(
                            new Search(
                                new CompiledQuery(search.Query, search.Mode, search.CaseSensitive),
                                search.Color,
                                tab.Buffer
                            )
                        );
                        if (i < persisted.FollowSearches.Count)
                            tab.FollowSearches[^1] = persisted.FollowSearches[i];
                    }
                    catch (ArgumentException) { }
                }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        // Streams come last: their members (files and Elastic sources) must already be open.
        foreach (var persisted in config.OpenStreams ?? [])
            await RestoreStreamTabAsync(persisted, cancellationToken).ConfigureAwait(false);
        StartHealthLoop();
        if (_settings.ElasticConnections.Count > 0)
            SignalHealthCheck();

        lock (_gate)
        {
            _recentFiles.Clear();
            _recentFiles.AddRange(config.RecentFiles.Take(MaxRecent));
            _recentElasticSourceIds.Clear();
            _recentElasticSourceIds.AddRange(config.RecentElasticSourceIds.Take(MaxRecent));
        }

        lock (_gate)
        {
            if (config.SelectedFilePath is not null)
                SelectedFile = _files.FirstOrDefault(file =>
                    string.Equals(
                        file.Path,
                        config.SelectedFilePath,
                        StringComparison.OrdinalIgnoreCase
                    )
                );
            SelectedFile ??= _files.FirstOrDefault();
            if (config.SelectedElasticSourceId is not null)
                SelectedFile =
                    _files.FirstOrDefault(file =>
                        file.Source.ElasticSourceId == config.SelectedElasticSourceId
                    ) ?? SelectedFile;
            if (config.SelectedStreamId is not null)
                SelectedFile =
                    _streamTabs.FirstOrDefault(tab =>
                        tab.Stream!.Definition.Id == config.SelectedStreamId
                    ) ?? SelectedFile;
        }
        NotifyChanged();
    }

    private async ValueTask RestoreStreamTabAsync(
        PersistedStreamTab? persisted,
        CancellationToken cancellationToken
    )
    {
        if (persisted is null || string.IsNullOrWhiteSpace(persisted.StreamId))
            return;
        FileTabState? tab;
        try
        {
            tab = await OpenStreamAsync(persisted.StreamId, cancellationToken, save: false)
                .ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }
        if (tab?.Stream is not { } stream)
            return;
        lock (_gate)
        {
            stream.SetHiddenKeys(persisted.HiddenSources ?? []);
            stream.RangeFrom = string.IsNullOrWhiteSpace(persisted.From)
                ? "now-15m"
                : persisted.From;
            stream.RangeTo = string.IsNullOrWhiteSpace(persisted.To) ? "now" : persisted.To;
            stream.Live = persisted.Live;
        }
        tab.FollowAll = persisted.FollowAll;
        tab.SetRules(NormalizeRules(persisted.Rules, RuleScope.Source));
        tab.Levels = NormalizeLevels(persisted.Levels);
        tab.Columns = ColumnLayout.FromPersisted(
            persisted.Columns,
            persisted.ComposedRows,
            persisted.TimeDisplay
        );
        var searches = persisted.Searches ?? [];
        for (var i = 0; i < searches.Count; i++)
        {
            var search = searches[i];
            try
            {
                tab.AddSearch(
                    new Search(
                        new CompiledQuery(search.Query, search.Mode, search.CaseSensitive),
                        search.Color,
                        tab.Buffer
                    )
                );
                if (persisted.FollowSearches is { } follows && i < follows.Count)
                    tab.FollowSearches[^1] = follows[i];
            }
            catch (ArgumentException) { }
        }
    }

    public async ValueTask<FileTabState> OpenFileAsync(
        string path,
        bool save = true,
        CancellationToken cancellationToken = default,
        bool remember = true,
        bool ephemeral = false
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = System.IO.Path.GetFullPath(path);
        FileTabState? existing;
        FileTabState? tab = null;
        lock (_gate)
        {
            existing = _files.FirstOrDefault(file =>
                string.Equals(file.Path, fullPath, StringComparison.OrdinalIgnoreCase)
            );
            if (existing is not null)
                SelectedFile = existing;
            else
            {
                var id = $"file-{++_nextFileId}";
                var buffer = new FileBuffer(_settings.MaxLines);
                ILogParser parser = new TimestampingParser(
                    LogParserSelector.ForPath(fullPath),
                    () => NaiveZone(_settings.TimeZoneMode),
                    sourceId: id
                );
                var tailer = _tailers.StartFile(id, fullPath, parser);
                tab = new FileTabState(id, fullPath, buffer, parser, tailer)
                {
                    Error = File.Exists(fullPath) ? null : "File missing",
                    Ephemeral = ephemeral,
                };
                tab.SyncGlobalRuleSearches(_settings.Rules);
                _files.Add(tab);
                AttachToStreams(tab);
                SelectedFile = tab;
            }
            if (remember)
                Remember(_recentFiles, fullPath, StringComparer.OrdinalIgnoreCase);
        }

        NotifyChanged();
        if (existing is not null)
            return existing;

        var opened = tab!;
        if (save)
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        return opened;
    }

    public async ValueTask<FileTabState> OpenElasticSourceAsync(
        string sourceId,
        bool save = true,
        CancellationToken cancellationToken = default,
        string from = "now-5m",
        string to = "now",
        AppTimeZoneMode inputZone = AppTimeZoneMode.Utc,
        bool remember = true
    )
    {
        var match = _settings
            .ElasticConnections.SelectMany(connection =>
                connection.Views.SelectMany(view =>
                    view.Sources.Select(source => (connection, view, source))
                )
            )
            .FirstOrDefault(item => item.source.Id == sourceId);
        if (match.source is null)
            throw new ArgumentException("The Elastic source is not configured.", nameof(sourceId));
        lock (_gate)
        {
            var existing = _files.FirstOrDefault(file => file.Id == sourceId);
            if (existing is not null)
            {
                SelectedFile = existing;
                if (remember)
                    Remember(_recentElasticSourceIds, sourceId, StringComparer.Ordinal);
                return existing;
            }
        }
        var connection = match.connection;
        var view = match.view;
        if (
            string.IsNullOrWhiteSpace(view.DataViewTitle)
            || string.IsNullOrWhiteSpace(view.TimeFieldName)
            || string.IsNullOrWhiteSpace(view.ServerField)
        )
            throw new ArgumentException(
                "The Elastic source configuration is incomplete.",
                nameof(sourceId)
            );
        var secret = connection.AuthMode is ElasticAuthMode.Basic or ElasticAuthMode.ApiKey
            ? _credentials.Get(connection.Id)
                ?? throw new InvalidOperationException("The Elastic credential is unavailable.")
            : string.Empty;
        var tailer = _tailers.CreateElastic(
            connection,
            view,
            match.source,
            secret,
            _elastic,
            Now,
            maxInitialLines: _settings.ElasticInitialLines
        );
        var tab = new FileTabState(
            LogSourceDescriptor.Elastic(connection, view, match.source),
            new FileBuffer(_settings.MaxLines),
            new PlainTextParser(),
            tailer
        );
        if (tailer is ElasticTailer elasticTailer)
        {
            if (from != "now-5m" || to != "now")
                elasticTailer.SetTimeRange(from, to);
            tab.ElasticFrom = from;
            tab.ElasticTo = to;
            tab.ElasticInputZone = inputZone;
        }
        tab.SyncGlobalRuleSearches(_settings.Rules);
        lock (_gate)
        {
            _files.Add(tab);
            AttachToStreams(tab);
            SelectedFile = tab;
            if (remember)
                Remember(_recentElasticSourceIds, sourceId, StringComparer.Ordinal);
        }
        tailer.Start();
        NotifyChanged();
        if (save)
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        return tab;
    }

    public bool IsElasticSourceOpen(string sourceId) =>
        Files.Any(file => file.Source.ElasticSourceId == sourceId);

    internal void SetElasticTimeRange(FileTabState tab, string from, string to)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (tab.Source.Kind != LogSourceKind.Elastic || tab.Tailer is not ElasticTailer tailer)
            throw new ArgumentException("The selected tab is not an Elastic tab.", nameof(tab));
        tailer.SetTimeRange(from, to);
        tab.Buffer.Clear();
        lock (_gate)
            MarkStreamsForRebuild(tab.Id);
        tab.Error = null;
        tab.ElasticFrom = from.Trim();
        tab.ElasticTo = to.Trim();
        tab.ElasticLoading = true;
        tab.ElasticInitialLimitReached = null;
        tab.ElasticResolvedFrom = null;
        tab.ElasticResolvedTo = null;
        NotifyChanged();
    }

    public IReadOnlyDictionary<string, ElasticSourceHealth> ElasticSourceStatuses =>
        _health.Statuses;
    public bool HasElasticWarning => _health.HasWarning;

    public async ValueTask CloseFileAsync(
        FileTabState tab,
        CancellationToken cancellationToken = default,
        bool save = true
    )
    {
        lock (_gate)
        {
            if (!_files.Remove(tab))
                return;

            ForgetStreamLinks(tab);
            SelectedFile = _files.FirstOrDefault();
        }
        await tab.DisposeAsync().ConfigureAwait(false);
        NotifyChanged();
        SignalHealthCheck();
        if (save)
            await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---- Streams ---------------------------------------------------------------------------------------
    // A stream is a tab of its own kind whose lines come from the stream's open members. All stream state
    // is touched only under _gate: members feed their lines into each stream's merger from the drain, and
    // the drain appends whatever the merger releases. Nothing here awaits while holding the lock.

    /// <summary>"file:{full path}" or "elastic:{source id}": the stable identity of a stream member.</summary>
    internal static string MemberKey(FileTabState tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        return tab.Source.Kind switch
        {
            LogSourceKind.File => "file:" + tab.Path,
            LogSourceKind.Elastic => "elastic:" + (tab.Source.ElasticSourceId ?? tab.Id),
            _ => throw new ArgumentException(
                "A stream cannot contain another stream.",
                nameof(tab)
            ),
        };
    }

    public async ValueTask<StreamSettings> CreateStreamAsync(
        string name,
        IReadOnlyList<FileTabState> members,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(members);
        StreamSettings created;
        lock (_gate)
        {
            var list = BuildMembers(members, []);
            created = new StreamSettings(
                Guid.NewGuid().ToString("N"),
                string.IsNullOrWhiteSpace(name) ? StreamSettings.DefaultName : name.Trim(),
                list
            );
            _streamDefs.Add(created);
        }
        NotifyChanged();
        try
        {
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
                _streamDefs.Remove(created);
            NotifyChanged();
            throw;
        }
        return created;
    }

    public async ValueTask UpdateStreamAsync(
        string streamId,
        string name,
        IReadOnlyList<FileTabState> members,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(members);
        StreamSettings previous;
        lock (_gate)
        {
            var index = _streamDefs.FindIndex(item => item.Id == streamId);
            if (index < 0)
                throw new ArgumentException("The stream does not exist.", nameof(streamId));
            previous = _streamDefs[index];
            var updated = previous with
            {
                Name = string.IsNullOrWhiteSpace(name) ? StreamSettings.DefaultName : name.Trim(),
                Members = BuildMembers(members, previous.Members),
            };
            _streamDefs[index] = updated;
            ApplyDefinition(updated);
        }
        NotifyChanged();
        try
        {
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                var index = _streamDefs.FindIndex(item => item.Id == streamId);
                if (index >= 0)
                {
                    _streamDefs[index] = previous;
                    ApplyDefinition(previous);
                }
            }
            NotifyChanged();
            throw;
        }
    }

    public async ValueTask DeleteStreamAsync(
        string streamId,
        CancellationToken cancellationToken = default
    )
    {
        StreamSettings? removed;
        FileTabState? tab;
        int definitionIndex;
        int tabIndex = -1;
        FileTabState? previousSelection;
        lock (_gate)
        {
            definitionIndex = _streamDefs.FindIndex(item => item.Id == streamId);
            if (definitionIndex < 0)
                return;
            removed = _streamDefs[definitionIndex];
            _streamDefs.RemoveAt(definitionIndex);
            previousSelection = SelectedFile;
            tab = _streamTabs.FirstOrDefault(item => item.Stream!.Definition.Id == streamId);
            if (tab is not null)
            {
                tabIndex = _files.IndexOf(tab);
                _files.Remove(tab);
                ForgetStreamLinks(tab);
                if (ReferenceEquals(SelectedFile, tab))
                    SelectedFile = _files.FirstOrDefault();
            }
        }
        NotifyChanged();
        try
        {
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                _streamDefs.Insert(Math.Min(definitionIndex, _streamDefs.Count), removed);
                if (tab is not null)
                {
                    _files.Insert(Math.Min(Math.Max(tabIndex, 0), _files.Count), tab);
                    _streamTabs.Add(tab);
                    ReindexStreams();
                    SelectedFile = previousSelection;
                }
            }
            NotifyChanged();
            throw;
        }
        if (tab is not null)
            await tab.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the stream's tab and selects it. Members that are not open are opened first; one that cannot be
    /// opened (missing file, unconfigured Elastic source, unavailable credential) is simply listed as missing.
    /// </summary>
    public async ValueTask<FileTabState?> OpenStreamAsync(
        string streamId,
        CancellationToken cancellationToken = default,
        bool save = true
    )
    {
        StreamSettings? definition;
        lock (_gate)
            definition = _streamDefs.FirstOrDefault(item => item.Id == streamId);
        if (definition is null)
            return null;

        foreach (var member in definition.Members)
            await EnsureMemberOpenAsync(member.Key, cancellationToken).ConfigureAwait(false);

        FileTabState tab;
        var created = false;
        lock (_gate)
        {
            var existing = _streamTabs.FirstOrDefault(item =>
                item.Stream!.Definition.Id == streamId
            );
            if (existing is not null)
            {
                SelectedFile = existing;
                tab = existing;
            }
            else
            {
                // The definition may have been edited or deleted while members were opening.
                definition = _streamDefs.FirstOrDefault(item => item.Id == streamId);
                if (definition is null)
                    return null;
                var state = new StreamState(definition, StreamHold, StreamClock);
                state.SetMembers(definition, OpenMembersByKey());
                var descriptor = LogSourceDescriptor.Stream(definition);
                tab = new FileTabState(
                    descriptor,
                    new FileBuffer(_settings.MaxLines),
                    new PlainTextParser(),
                    new NullTailer(descriptor.Id, descriptor.DisplayName)
                )
                {
                    Stream = state,
                };
                tab.SyncGlobalRuleSearches(_settings.Rules);
                _files.Add(tab);
                _streamTabs.Add(tab);
                ReindexStreams();
                RebuildStream(tab);
                SelectedFile = tab;
                created = true;
            }
        }
        NotifyChanged();
        if (created && save)
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        return tab;
    }

    /// <summary>Hides the rows of the given members (tab ids) in a stream; rows of other members stay.</summary>
    public async ValueTask SetHiddenSourcesAsync(
        FileTabState stream,
        IReadOnlySet<string> hidden,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(hidden);
        lock (_gate)
        {
            if (!_files.Contains(stream) || stream.Stream is not { } state)
                return;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in hidden)
                if (state.KeyOf(id) is { } key)
                    keys.Add(key);
            // Members that are not open cannot be toggled by id; they keep their saved choice.
            foreach (var key in state.HiddenKeys)
                if (state.MissingMembers.Contains(key))
                    keys.Add(key);
            state.SetHiddenKeys(keys);
        }
        NotifyChanged();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask EnsureMemberOpenAsync(string key, CancellationToken cancellationToken)
    {
        lock (_gate)
            if (OpenMembersByKey().ContainsKey(key))
                return;
        try
        {
            if (key.StartsWith("file:", StringComparison.Ordinal))
            {
                var path = key[5..];
                if (File.Exists(path))
                    await OpenFileAsync(path, save: false, cancellationToken, remember: false)
                        .ConfigureAwait(false);
            }
            else if (key.StartsWith("elastic:", StringComparison.Ordinal))
                await OpenElasticSourceAsync(
                        key[8..],
                        save: false,
                        cancellationToken,
                        remember: false
                    )
                    .ConfigureAwait(false);
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Distinct, non-stream member tabs as definition members; colours of retained keys are kept. Under _gate.</summary>
    private List<StreamMember> BuildMembers(
        IReadOnlyList<FileTabState> tabs,
        IReadOnlyList<StreamMember> existing
    )
    {
        var keys = new List<string>();
        foreach (var tab in tabs)
        {
            if (tab is null || !_files.Contains(tab))
                throw new ArgumentException(
                    "Every stream member must be an open source.",
                    nameof(tabs)
                );
            var key = MemberKey(tab);
            if (!keys.Contains(key))
                keys.Add(key);
        }
        if (keys.Count < 2)
            throw new ArgumentException(
                "A stream needs at least two different sources.",
                nameof(tabs)
            );
        var used = new HashSet<string>(
            existing.Where(member => keys.Contains(member.Key)).Select(member => member.Color),
            StringComparer.OrdinalIgnoreCase
        );
        var result = new List<StreamMember>(keys.Count);
        foreach (var key in keys)
        {
            var color = existing.FirstOrDefault(member => member.Key == key)?.Color;
            if (string.IsNullOrWhiteSpace(color))
            {
                color =
                    StreamPalette.Colors.FirstOrDefault(candidate => !used.Contains(candidate))
                    ?? StreamPalette.ColorAt(result.Count);
                used.Add(color);
            }
            result.Add(new StreamMember(key, color));
        }
        return result;
    }

    /// <summary>Open File and Elastic tabs by member key. Under _gate.</summary>
    private Dictionary<string, FileTabState> OpenMembersByKey()
    {
        var map = new Dictionary<string, FileTabState>(StringComparer.OrdinalIgnoreCase);
        foreach (var tab in _files)
            if (tab.Source.Kind != LogSourceKind.Stream)
                map.TryAdd(MemberKey(tab), tab);
        return map;
    }

    /// <summary>A stream's definition changed: re-point an open tab at it and re-merge. Under _gate.</summary>
    private void ApplyDefinition(StreamSettings definition)
    {
        var tab = _streamTabs.FirstOrDefault(item => item.Stream!.Definition.Id == definition.Id);
        if (tab is null)
            return;
        tab.Source = LogSourceDescriptor.Stream(definition);
        tab.Stream!.SetMembers(definition, OpenMembersByKey());
        ReindexStreams();
        RebuildStream(tab);
    }

    /// <summary>Rebuilds the member-to-streams lookup the drain uses. Under _gate.</summary>
    private void ReindexStreams()
    {
        var map = new Dictionary<string, List<StreamState>>(StringComparer.Ordinal);
        foreach (var tab in _streamTabs)
        foreach (var id in tab.Stream!.MemberIds)
        {
            if (!map.TryGetValue(id, out var list))
                map[id] = list = [];
            list.Add(tab.Stream);
        }
        _memberStreams = map.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
    }

    /// <summary>A newly opened source joins every open stream that lists it, which then re-merges. Under _gate.</summary>
    private void AttachToStreams(FileTabState member)
    {
        if (_streamTabs.Count == 0 || member.Source.Kind == LogSourceKind.Stream)
            return;
        var key = MemberKey(member);
        var changed = false;
        Dictionary<string, FileTabState>? open = null;
        foreach (var tab in _streamTabs)
        {
            var state = tab.Stream!;
            if (
                state.MemberIds.Contains(member.Id)
                || !state.Definition.Members.Any(item =>
                    string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase)
                )
            )
                continue;
            state.SetMembers(state.Definition, open ??= OpenMembersByKey());
            changed = true;
        }
        if (changed)
            ReindexStreams();
    }

    /// <summary>
    /// A tab left the session. A stream tab is forgotten; a member is detached from its streams, where it
    /// shows as missing (the lines already merged stay; the stream is not rebuilt). Under _gate.
    /// </summary>
    private void ForgetStreamLinks(FileTabState tab)
    {
        if (tab.Stream is not null)
        {
            _streamTabs.Remove(tab);
            ReindexStreams();
            return;
        }
        if (_memberStreams.TryGetValue(tab.Id, out var streams))
        {
            foreach (var stream in streams)
                stream.DetachMember(tab);
            ReindexStreams();
        }
    }

    /// <summary>The member's buffer was cleared outside the event loop: its streams must re-merge. Under _gate.</summary>
    private void MarkStreamsForRebuild(string memberId)
    {
        if (_memberStreams.TryGetValue(memberId, out var streams))
            foreach (var stream in streams)
                stream.NeedsRebuild = true;
    }

    /// <summary>Clears the stream's buffer and re-merges every open member's current lines in one pass. Under _gate.</summary>
    private static void RebuildStream(FileTabState tab)
    {
        var state = tab.Stream!;
        state.NeedsRebuild = false;
        state.Merger.Reset();
        tab.Buffer.Clear();
        foreach (var member in state.MemberTabs)
            state.Merger.Add(member.Id, member.Buffer.Lines);
        tab.Buffer.Append(state.Merger.FlushAll());
    }

    public Search AddSearch(
        FileTabState tab,
        string query,
        MatchMode mode,
        bool caseSensitive,
        string color
    )
    {
        Search search;
        lock (_gate)
        {
            if (!_files.Contains(tab))
                throw new InvalidOperationException("The file is not open.");
            search = new Search(new CompiledQuery(query, mode, caseSensitive), color, tab.Buffer);
            tab.AddSearch(search);
        }
        NotifyChanged();
        return search;
    }

    public async ValueTask<bool> RemoveSearchAsync(
        FileTabState tab,
        Search search,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(search);
        bool removed;
        lock (_gate)
            removed = _files.Contains(tab) && tab.RemoveSearch(search);
        if (!removed)
            return false;

        NotifyChanged();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public void SelectFile(FileTabState? tab)
    {
        lock (_gate)
        {
            if (tab is null || _files.Contains(tab))
                SelectedFile = tab;
        }
        NotifyChanged();
    }

    public async ValueTask SetFollowAllAsync(
        FileTabState tab,
        bool value,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(tab);
        lock (_gate)
        {
            if (!_files.Contains(tab))
                return;
            tab.FollowAll = value;
        }
        NotifyChanged();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask AddRuleAsync(
        FileTabState? tab,
        ViewRule rule,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(rule);
        await EditRulesAsync(
                tab,
                rule.Scope,
                rules => RuleEditor.Add(rules, rule),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async ValueTask RemoveRuleAsync(
        FileTabState? tab,
        ViewRule rule,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(rule);
        await EditRulesAsync(
                tab,
                rule.Scope,
                rules => RuleEditor.Remove(rules, rule),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async ValueTask ReplaceRuleAsync(
        FileTabState? tab,
        ViewRule old,
        ViewRule updated,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(updated);
        if (old.Scope == updated.Scope)
        {
            await EditRulesAsync(
                    tab,
                    old.Scope,
                    rules => RuleEditor.Replace(rules, old, updated),
                    cancellationToken
                )
                .ConfigureAwait(false);
            return;
        }
        // A Source scope on either side needs a tab that is open in this state; without one
        // half the move would silently no-op (losing or duplicating the rule), so do nothing.
        if (old.Scope == RuleScope.Source || updated.Scope == RuleScope.Source)
        {
            lock (_gate)
            {
                if (tab is null || !_files.Contains(tab))
                    return;
            }
        }
        // Add first so a failure part-way leaves a duplicate rather than a lost rule.
        await EditRulesAsync(
                tab,
                updated.Scope,
                rules => RuleEditor.Add(rules, updated),
                cancellationToken
            )
            .ConfigureAwait(false);
        await EditRulesAsync(
                tab,
                old.Scope,
                rules => RuleEditor.Remove(rules, old),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    // Callers are expected on the UI thread; the read-modify-write below is not atomic.
    private async ValueTask EditRulesAsync(
        FileTabState? tab,
        RuleScope scope,
        Func<IReadOnlyList<ViewRule>, List<ViewRule>> edit,
        CancellationToken cancellationToken
    )
    {
        if (scope == RuleScope.Global)
        {
            await UpdateSettingsAsync(
                    Settings with
                    {
                        Rules = edit(Settings.Rules),
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);
            return;
        }
        if (scope == RuleScope.Source && tab is not null)
            await SetSourceRulesAsync(tab, edit(tab.Rules), cancellationToken)
                .ConfigureAwait(false);
    }

    public async ValueTask SetSourceRulesAsync(
        FileTabState tab,
        IEnumerable<ViewRule> rules,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(rules);
        lock (_gate)
        {
            if (!_files.Contains(tab))
                return;
            tab.SetRules(NormalizeRules(rules, RuleScope.Source));
        }
        NotifyChanged();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stores the source rules and level mask under a name, replacing a filter of the same name (ignoring case).</summary>
    public async ValueTask SaveFilterAsync(
        string name,
        IEnumerable<ViewRule> rules,
        LogLevels levels,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(rules);
        lock (_gate)
            UpsertSavedFilter(name, rules, levels);
        NotifyChanged();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DeleteSavedFilterAsync(
        string name,
        CancellationToken cancellationToken = default
    )
    {
        int removed;
        lock (_gate)
            removed = _savedFilters.RemoveAll(filter =>
                string.Equals(filter.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase)
            );
        if (removed == 0)
            return;
        NotifyChanged();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Merges a saved filter into a source: new rules are appended and the level mask is replaced unless it is All.</summary>
    public async ValueTask ApplySavedFilterAsync(
        FileTabState? tab,
        string name,
        CancellationToken cancellationToken = default
    )
    {
        if (tab is null)
            return;
        SavedFilter? filter;
        List<ViewRule> merged;
        lock (_gate)
        {
            filter = _savedFilters.FirstOrDefault(item =>
                string.Equals(item.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase)
            );
            if (filter is null || !_files.Contains(tab))
                return;
            merged = [.. tab.Rules, .. filter.Rules.Where(rule => !tab.Rules.Contains(rule))];
        }
        await SetSourceRulesAsync(tab, merged, cancellationToken).ConfigureAwait(false);
        if (filter.Levels != LogLevels.All)
            await SetLevelsAsync(tab, filter.Levels, cancellationToken).ConfigureAwait(false);
    }

    // Callers hold _gate.
    private void UpsertSavedFilter(string? name, IEnumerable<ViewRule>? rules, LogLevels levels)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return;
        var filter = new SavedFilter
        {
            Name = trimmed,
            Rules = NormalizeRules(rules, RuleScope.Source),
            Levels = NormalizeLevels(levels),
        };
        var index = _savedFilters.FindIndex(item =>
            string.Equals(item.Name, trimmed, StringComparison.OrdinalIgnoreCase)
        );
        if (index >= 0)
            _savedFilters[index] = filter;
        else
            _savedFilters.Add(filter);
    }

    public async ValueTask SetLevelsAsync(
        FileTabState tab,
        LogLevels levels,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(tab);
        lock (_gate)
        {
            if (!_files.Contains(tab))
                return;
            tab.Levels = NormalizeLevels(levels);
        }
        NotifyChanged();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces a source's column layout and saves the session. A failed save restores the previous
    /// layout (and tells the views) before the error is rethrown.
    /// </summary>
    public async ValueTask SetColumnLayoutAsync(
        FileTabState tab,
        ColumnLayout layout,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(layout);
        ColumnLayout? previous;
        lock (_gate)
        {
            if (!_files.Contains(tab))
                return;
            previous = tab.Columns;
            tab.Columns = layout;
        }
        NotifyChanged();
        try
        {
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Only undo our own change: a later overlapping call may have replaced the layout since.
            bool restored;
            lock (_gate)
            {
                restored = ReferenceEquals(tab.Columns, layout);
                if (restored)
                    tab.Columns = previous;
            }
            if (restored)
                NotifyChanged();
            throw;
        }
    }

    internal static LogLevels NormalizeLevels(LogLevels levels)
    {
        var known = levels & LogLevels.All;
        return known == LogLevels.None ? LogLevels.All : known;
    }

    public async ValueTask SetSearchFollowAsync(
        FileTabState tab,
        Search search,
        bool value,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(search);
        lock (_gate)
        {
            if (!_files.Contains(tab))
                return;

            var index = tab.Searches.IndexOf(search);
            if (index < 0 || index >= tab.FollowSearches.Count)
                return;

            tab.FollowSearches[index] = value;
        }
        NotifyChanged();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public void SelectLine(FileTabState tab, Line? line)
    {
        ArgumentNullException.ThrowIfNull(tab);
        lock (_gate)
        {
            if (!_files.Contains(tab))
                return;

            var index = line is null ? -1 : FindLineIndex(tab, line);
            tab.SelectedLine = index >= 0 ? index : null;
        }
        NotifyChanged();
    }

    public async ValueTask UpdateSettingsAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            _settings = NormalizeSettings(settings);
            foreach (var file in _files)
                file.SyncGlobalRuleSearches(_settings.Rules);
        }
        NotifyChanged();
        SignalHealthCheck();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public void SetWindowState(AppWindowState window)
    {
        ArgumentNullException.ThrowIfNull(window);
        lock (_gate)
            Window = window;
    }

    public void ToggleBookmark(FileTabState tab, Line line)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(line);
        lock (_gate)
        {
            if (!_files.Contains(tab))
                return;

            var index = FindLineIndex(tab, line);
            if (index < 0)
                return;
            tab.ToggleBookmarkAt(index);
        }
        NotifyChanged();
    }

    private static int FindLineIndex(FileTabState tab, Line line)
    {
        for (var index = 0; index < tab.Buffer.Count; index++)
            if (ReferenceEquals(tab.Buffer[index], line))
                return index;
        return -1;
    }

    public bool DrainTailerEvents()
    {
        // ponytail: one lock for the bounded UI drain; split per source if draining causes contention.
        lock (_gate)
        {
            var changed = false;
            var pendingLines = new Dictionary<string, List<Line>>(StringComparer.Ordinal);

            void FlushLines(string sourceId)
            {
                if (!pendingLines.Remove(sourceId, out var lines) || lines.Count == 0)
                    return;
                var tab = _files.FirstOrDefault(file => file.Id == sourceId);
                if (tab is null)
                    return;
                tab.Buffer.Append(lines);
                // Feed every open stream that merges this member (a stream awaiting a rebuild re-reads
                // the member buffers instead, so it skips the incremental add).
                if (_memberStreams.TryGetValue(sourceId, out var streams))
                    foreach (var stream in streams)
                    {
                        if (stream.NoteDelivery(sourceId))
                            stream.NeedsRebuild = true;
                        if (!stream.NeedsRebuild)
                            stream.Merger.Add(sourceId, lines);
                    }
            }

            var remainingLines = MaxLinesPerDrain;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            for (var events = 0; events < MaxEventsPerDrain && remainingLines > 0; events++)
            {
                if (
                    events > 0
                    && System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 8
                )
                    break;
                SourceEvent sourceEvent;
                if (_pendingBatch is not null)
                    sourceEvent = _pendingBatch;
                else if (!_tailers.Events.TryRead(out sourceEvent!))
                    break;
                var tab = _files.FirstOrDefault(file => file.Id == sourceEvent.SourceId);
                if (
                    tab is null
                    || (
                        tab.Tailer is ElasticTailer elastic
                        && (
                            sourceEvent.InstanceId != elastic.InstanceId
                            || sourceEvent.Generation != elastic.Generation
                        )
                    )
                )
                {
                    _pendingBatch = null;
                    _pendingBatchOffset = 0;
                    continue;
                }

                switch (sourceEvent)
                {
                    case SourceLines newLines:
                        tab.Error = null;
                        if (!pendingLines.TryGetValue(sourceEvent.SourceId, out var lines))
                            pendingLines[sourceEvent.SourceId] = lines = [];
                        var count = Math.Min(
                            remainingLines,
                            newLines.Lines.Count - _pendingBatchOffset
                        );
                        for (var index = 0; index < count; index++)
                            lines.Add(newLines.Lines[_pendingBatchOffset + index]);
                        remainingLines -= count;
                        _pendingBatchOffset += count;
                        if (_pendingBatchOffset < newLines.Lines.Count)
                            _pendingBatch = newLines;
                        else
                        {
                            _pendingBatch = null;
                            _pendingBatchOffset = 0;
                        }
                        break;
                    case SourceReset:
                        FlushLines(sourceEvent.SourceId);
                        tab.Error = null;
                        tab.Buffer.Clear();
                        MarkStreamsForRebuild(sourceEvent.SourceId);
                        break;
                    case SourceRangeLoaded loaded:
                        FlushLines(sourceEvent.SourceId);
                        tab.ElasticLoading = false;
                        tab.Error = null;
                        if (loaded.Initial || tab.ElasticResolvedFrom is null)
                            tab.ElasticResolvedFrom = loaded.From;
                        if (loaded.Initial)
                            tab.ElasticInitialLimitReached = loaded.InitialLimitReached;
                        tab.ElasticResolvedTo = loaded.To;
                        break;
                    case SourceError error:
                        tab.ElasticLoading = false;
                        FlushLines(sourceEvent.SourceId);
                        tab.Error = $"Source error: {error.Message}";
                        break;
                    case SourceRecovered:
                        FlushLines(sourceEvent.SourceId);
                        tab.Error = null;
                        break;
                }

                changed = true;
            }

            foreach (var sourceId in pendingLines.Keys.ToArray())
                FlushLines(sourceId);

            // One release per open stream per drain, events or not, so the hold expires on idle ticks.
            for (var index = 0; index < _streamTabs.Count; index++)
            {
                var streamTab = _streamTabs[index];
                var state = streamTab.Stream!;
                if (state.NeedsRebuild)
                {
                    RebuildStream(streamTab);
                    changed = true;
                    continue;
                }
                var released = state.Merger.Release();
                if (released.Count > 0)
                {
                    streamTab.Buffer.Append(released);
                    changed = true;
                }
            }

            if (changed)
                NotifyChanged();

            return changed;
        }
    }

    public async ValueTask SaveAsync(CancellationToken cancellationToken = default)
    {
        AppConfig config;
        lock (_gate)
        {
            var sourceIds = _settings
                .ElasticConnections.SelectMany(connection => connection.Views)
                .SelectMany(view => view.Sources)
                .Select(source => source.Id)
                .ToHashSet(StringComparer.Ordinal);
            // Sources removed from the settings must not keep occupying recent slots.
            _recentElasticSourceIds.RemoveAll(id => !sourceIds.Contains(id));
            config = new AppConfig
            {
                RecentFiles = [.. _recentFiles],
                RecentElasticSourceIds = [.. _recentElasticSourceIds],
                OpenFiles = _files
                    .Where(tab => tab.Source.Kind == LogSourceKind.File && !tab.Ephemeral)
                    .Select(tab => new PersistedFileTab
                    {
                        Path = tab.Path,
                        FollowAll = tab.FollowAll,
                        FollowSearches = tab
                            .Searches.Select((search, index) => (search, index))
                            .Where(item => !item.search.IsGlobalRule)
                            .Select(item => tab.FollowSearches[item.index])
                            .ToList(),
                        Rules = [.. tab.Rules],
                        Levels = tab.Levels,
                        Columns = tab.Columns?.ToPersisted(),
                        ComposedRows = tab.Columns?.ComposedRows ?? false,
                        TimeDisplay = tab.Columns?.TimeDisplay ?? TimeDisplay.Absolute,
                        SelectedLine = tab.SelectedLine,
                        Searches = tab
                            .Searches.Where(search => !search.IsGlobalRule)
                            .Select(search => new PersistedSearch
                            {
                                Query = search.Query.Query,
                                Mode = search.Query.Mode,
                                CaseSensitive = search.Query.CaseSensitive,
                                Color = search.Color,
                            })
                            .ToList(),
                    })
                    .ToList(),
                OpenElasticTabs = _files
                    .Where(tab =>
                        tab.Source.Kind == LogSourceKind.Elastic && sourceIds.Contains(tab.Id)
                    )
                    .Select(tab => new PersistedElasticTab
                    {
                        SourceId = tab.Source.ElasticSourceId!,
                        From = tab.ElasticFrom,
                        To = tab.ElasticTo,
                        InputZone = tab.ElasticInputZone,
                        FollowAll = tab.FollowAll,
                        FollowSearches = tab
                            .Searches.Select((search, index) => (search, index))
                            .Where(item => !item.search.IsGlobalRule)
                            .Select(item => tab.FollowSearches[item.index])
                            .ToList(),
                        Rules = [.. tab.Rules],
                        Levels = tab.Levels,
                        Columns = tab.Columns?.ToPersisted(),
                        ComposedRows = tab.Columns?.ComposedRows ?? false,
                        TimeDisplay = tab.Columns?.TimeDisplay ?? TimeDisplay.Absolute,
                        SelectedLine = tab.SelectedLine,
                        Searches = tab
                            .Searches.Where(search => !search.IsGlobalRule)
                            .Select(search => new PersistedSearch
                            {
                                Query = search.Query.Query,
                                Mode = search.Query.Mode,
                                CaseSensitive = search.Query.CaseSensitive,
                                Color = search.Color,
                            })
                            .ToList(),
                    })
                    .ToList(),
                Streams = [.. _streamDefs],
                OpenStreams = _streamTabs
                    .Select(tab => new PersistedStreamTab
                    {
                        StreamId = tab.Stream!.Definition.Id,
                        FollowAll = tab.FollowAll,
                        FollowSearches = tab
                            .Searches.Select((search, index) => (search, index))
                            .Where(item => !item.search.IsGlobalRule)
                            .Select(item => tab.FollowSearches[item.index])
                            .ToList(),
                        Rules = [.. tab.Rules],
                        Levels = tab.Levels,
                        Columns = tab.Columns?.ToPersisted(),
                        ComposedRows = tab.Columns?.ComposedRows ?? false,
                        TimeDisplay = tab.Columns?.TimeDisplay ?? TimeDisplay.Absolute,
                        HiddenSources = [.. tab.Stream.HiddenKeys],
                        From = tab.Stream.RangeFrom,
                        To = tab.Stream.RangeTo,
                        Live = tab.Stream.Live,
                        Searches = tab
                            .Searches.Where(search => !search.IsGlobalRule)
                            .Select(search => new PersistedSearch
                            {
                                Query = search.Query.Query,
                                Mode = search.Query.Mode,
                                CaseSensitive = search.Query.CaseSensitive,
                                Color = search.Color,
                            })
                            .ToList(),
                    })
                    .ToList(),
                SelectedStreamId = SelectedFile?.Stream?.Definition.Id,
                SelectedFilePath =
                    SelectedFile is { Ephemeral: false } selected
                    && selected.Source.Kind == LogSourceKind.File
                        ? selected.Path
                        : null,
                SelectedElasticSourceId =
                    SelectedFile?.Source.ElasticSourceId is { } selectedId
                    && sourceIds.Contains(selectedId)
                        ? selectedId
                        : null,
                Window = Window,
                Settings = _settings,
                SavedFilters = [.. _savedFilters],
            };
        }
        await _persistence.SaveAsync(config, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await SaveAsync().ConfigureAwait(false);
        FileTabState[] files;
        lock (_gate)
            files = _files.ToArray();
        foreach (var file in files)
            await file.DisposeAsync().ConfigureAwait(false);
        await _tailers.DisposeAsync().ConfigureAwait(false);
        _health.Changed -= NotifyChanged;
        _healthStop.Cancel();
        SignalHealthCheck();
        if (_healthLoop is not null)
        {
            try
            {
                await _healthLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_healthStop.IsCancellationRequested) { }
        }
        await _health.DisposeAsync().ConfigureAwait(false);
        _healthSignal.Dispose();
        _healthStop.Dispose();
    }

    private void NotifyChanged() => Changed?.Invoke();

    private void SignalHealthCheck()
    {
        if (_healthSignal.CurrentCount == 0)
            _healthSignal.Release();
    }

    private void StartHealthLoop()
    {
        if (_healthLoop is not null)
            return;
        _healthLoop = Task.Run(
            async () =>
            {
                while (!_healthStop.IsCancellationRequested)
                {
                    await _healthSignal
                        .WaitAsync(TimeSpan.FromSeconds(30), _healthStop.Token)
                        .ConfigureAwait(false);
                    if (_settings.ElasticConnections.Count > 0)
                        await _health
                            .CheckOnceAsync(_settings, _healthStop.Token)
                            .ConfigureAwait(false);
                }
            },
            _healthStop.Token
        );
    }

    private static AppSettings NormalizeSettings(AppSettings settings)
    {
        return new AppSettings
        {
            MaxLines = Math.Clamp(settings.MaxLines, 1_000, 1_000_000),
            ContextAbove = Math.Clamp(settings.ContextAbove, 0, 50),
            ContextBelow = Math.Clamp(settings.ContextBelow, 0, 50),
            ElasticInitialLines = Math.Clamp(settings.ElasticInitialLines, 100, 100_000),
            Rules = NormalizeRules(settings.Rules, RuleScope.Global),
            Theme = ThemeCatalog.Normalize(settings.Theme),
            Density = Enum.IsDefined(settings.Density) ? settings.Density : UiDensity.Comfortable,
            LogFontSize = Enum.IsDefined(settings.LogFontSize)
                ? settings.LogFontSize
                : LogFontSize.Medium,
            SettingsMenuAlignment = SettingsMenuAlignment.Right,
            TimeZoneMode = Enum.IsDefined(settings.TimeZoneMode)
                ? settings.TimeZoneMode
                : AppTimeZoneMode.Local,
            ElasticConnections = NormalizeElasticConnections(settings.ElasticConnections),
        };
    }

    internal static List<ViewRule> NormalizeRules(IEnumerable<ViewRule>? rules, RuleScope scope) =>
        (rules ?? [])
            .OfType<ViewRule>()
            .Where(rule => !string.IsNullOrWhiteSpace(rule.Pattern))
            .Select(rule =>
                rule with
                {
                    Pattern = rule.Pattern.Trim(),
                    Color = NormalizeColor(rule.Color),
                    Scope = scope,
                    Mode = Enum.IsDefined(rule.Mode)
                        ? rule.Mode
                        : CompiledQuery.DetectMode(rule.Pattern),
                    Action = Enum.IsDefined(rule.Action) ? rule.Action : RuleAction.Highlight,
                    // Only global rules own a view tab; pinning source rules is not supported yet.
                    PinnedAsTab =
                        scope == RuleScope.Global
                        && rule.PinnedAsTab
                        && rule.Action != RuleAction.Hide,
                }
            )
            .DistinctBy(rule =>
                (
                    rule.CaseSensitive ? rule.Pattern : rule.Pattern.ToUpperInvariant(),
                    rule.CaseSensitive,
                    rule.Mode,
                    rule.Action
                )
            )
            .ToList();

    private static List<ElasticConnectionSettings> NormalizeElasticConnections(
        IEnumerable<ElasticConnectionSettings>? connections
    ) =>
        (connections ?? [])
            .Where(connection =>
                connection is not null && !string.IsNullOrWhiteSpace(connection.Id)
            )
            .GroupBy(connection => connection.Id.Trim(), StringComparer.Ordinal)
            .Select(group =>
            {
                var connection = group.First();
                var views = NormalizeElasticViews(connection);
                var sources = (connection.Sources ?? [])
                    .Where(source => source is not null && !string.IsNullOrWhiteSpace(source.Id))
                    .GroupBy(source => source.Id.Trim(), StringComparer.Ordinal)
                    .Select(sourceGroup =>
                    {
                        var source = sourceGroup.First();
                        return source with
                        {
                            Id = source.Id.Trim(),
                            ServerValue = source.ServerValue.Trim(),
                            NamespaceValue = source.NamespaceValue.Trim(),
                        };
                    })
                    .ToList();
                return connection with
                {
                    Id = connection.Id.Trim(),
                    Name = connection.Name.Trim(),
                    KibanaUrl = connection.KibanaUrl.Trim(),
                    ElasticsearchUrl = connection.ElasticsearchUrl.Trim(),
                    Username = connection.Username?.Trim(),
                    DataViewId = connection.DataViewId?.Trim(),
                    DataViewTitle = connection.DataViewTitle?.Trim(),
                    TimeFieldName = connection.TimeFieldName?.Trim(),
                    ServerField = connection.ServerField?.Trim(),
                    NamespaceField = connection.NamespaceField?.Trim(),
                    OutputFields = (connection.OutputFields ?? [])
                        .Select(field => field.Trim())
                        .Where(field => field.Length > 0)
                        .Distinct(StringComparer.Ordinal)
                        .ToList(),
                    Sources = sources,
                    Views = views,
                };
            })
            .ToList();

    // A connection saved by the setup flow before any view exists legitimately has no views.
    private static bool HasLegacyViewFields(ElasticConnectionSettings connection) =>
        !string.IsNullOrWhiteSpace(connection.DataViewId)
        || !string.IsNullOrWhiteSpace(connection.DataViewTitle)
        || !string.IsNullOrWhiteSpace(connection.TimeFieldName)
        || !string.IsNullOrWhiteSpace(connection.ServerField)
        || !string.IsNullOrWhiteSpace(connection.NamespaceField)
        || connection.OutputFields is { Count: > 0 }
        || connection.Sources is { Count: > 0 };

    private static List<ElasticViewSettings> NormalizeElasticViews(
        ElasticConnectionSettings connection
    )
    {
        var views = (connection.Views ?? [])
            .Where(view => view is not null && !string.IsNullOrWhiteSpace(view.Id))
            .GroupBy(view => view.Id.Trim(), StringComparer.Ordinal)
            .Select(group => NormalizeElasticView(group.First()))
            .ToList();
        if (views.Count > 0 || !HasLegacyViewFields(connection))
            return views;

        return
        [
            NormalizeElasticView(
                new ElasticViewSettings
                {
                    Id = connection.Id,
                    Name = connection.DataViewTitle ?? "View",
                    DataViewId = connection.DataViewId,
                    DataViewTitle = connection.DataViewTitle,
                    TimeFieldName = connection.TimeFieldName,
                    ServerField = connection.ServerField,
                    NamespaceField = connection.NamespaceField,
                    OutputFields = connection.OutputFields,
                    Sources = connection.Sources,
                }
            ),
        ];
    }

    private static ElasticViewSettings NormalizeElasticView(ElasticViewSettings view)
    {
        var sources = (view.Sources ?? [])
            .Where(source => source is not null && !string.IsNullOrWhiteSpace(source.Id))
            .GroupBy(source => source.Id.Trim(), StringComparer.Ordinal)
            .Select(group =>
            {
                var source = group.First();
                return source with
                {
                    Id = source.Id.Trim(),
                    ServerValue = source.ServerValue.Trim(),
                    NamespaceValue = source.NamespaceValue.Trim(),
                };
            })
            .ToList();
        return view with
        {
            Id = view.Id.Trim(),
            Name = string.IsNullOrWhiteSpace(view.Name)
                ? view.DataViewTitle?.Trim() ?? "View"
                : view.Name.Trim(),
            DataViewId = view.DataViewId?.Trim(),
            DataViewTitle = view.DataViewTitle?.Trim(),
            TimeFieldName = view.TimeFieldName?.Trim(),
            ServerField = view.ServerField?.Trim(),
            NamespaceField = view.NamespaceField?.Trim(),
            OutputFields = (view.OutputFields ?? [])
                .Select(field => field.Trim())
                .Where(field => field.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            Sources = sources,
        };
    }

    private static string NormalizeColor(string? color) =>
        color is { Length: 4 or 7 } && color[0] == '#' && color[1..].All(Uri.IsHexDigit)
            ? color
            : "#f59e0b";

    // The stored secret was typed for one login method on one pair of servers. Sending it with
    // another method (a password as an API key) or to another host would hand it to the wrong party.
    private static bool StoredSecretApplies(
        ElasticConnectionSettings? stored,
        ElasticConnectionSettings connection
    ) =>
        stored is { AuthMode: ElasticAuthMode.Basic or ElasticAuthMode.ApiKey }
        && stored.AuthMode == connection.AuthMode
        && ElasticUrls.SameOrigin(stored.KibanaUrl, connection.KibanaUrl)
        && ElasticUrls.SameOrigin(stored.ElasticsearchUrl, connection.ElasticsearchUrl);

    private string? SecretFor(ElasticConnectionSettings connection)
    {
        if (connection.AuthMode is not (ElasticAuthMode.Basic or ElasticAuthMode.ApiKey))
            return null;
        var stored = _settings.ElasticConnections.FirstOrDefault(item => item.Id == connection.Id);
        return StoredSecretApplies(stored, connection) ? _credentials.Get(connection.Id) : null;
    }

    private static void ValidateElasticConnection(
        ElasticConnectionSettings connection,
        string? secret
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!Guid.TryParse(connection.Id, out _) && string.IsNullOrWhiteSpace(connection.Id))
            throw new ArgumentException("A connection ID is required.", nameof(connection));
        if (
            !Uri.TryCreate(connection.KibanaUrl, UriKind.Absolute, out var kibana)
            || kibana.Scheme is not ("http" or "https")
        )
            throw new ArgumentException(
                "Kibana URL must be absolute HTTP or HTTPS.",
                nameof(connection)
            );
        if (kibana.UserInfo.Length > 0)
            throw new ArgumentException(
                "Kibana URL must be absolute HTTP or HTTPS without a user name or password.",
                nameof(connection)
            );
        if (
            !Uri.TryCreate(connection.ElasticsearchUrl, UriKind.Absolute, out var elastic)
            || elastic.Scheme is not ("http" or "https")
        )
            throw new ArgumentException(
                "Elasticsearch URL must be absolute HTTP or HTTPS.",
                nameof(connection)
            );
        if (elastic.UserInfo.Length > 0)
            throw new ArgumentException(
                "Elasticsearch URL must be absolute HTTP or HTTPS without a user name or password.",
                nameof(connection)
            );
        if (
            connection.AuthMode == ElasticAuthMode.Basic
            && (string.IsNullOrWhiteSpace(connection.Username) || string.IsNullOrWhiteSpace(secret))
        )
            throw new ArgumentException(
                "Basic authentication requires a username and secret.",
                nameof(connection)
            );
        if (connection.AuthMode == ElasticAuthMode.ApiKey && string.IsNullOrWhiteSpace(secret))
            throw new ArgumentException(
                "API-key authentication requires a secret.",
                nameof(connection)
            );
        foreach (var view in connection.Views)
        {
            if (
                string.IsNullOrWhiteSpace(view.Name)
                || string.IsNullOrWhiteSpace(view.DataViewTitle)
                || string.IsNullOrWhiteSpace(view.TimeFieldName)
                || string.IsNullOrWhiteSpace(view.ServerField)
                || view.OutputFields.Count == 0
            )
                throw new ArgumentException(
                    "The Elastic view and field mappings are incomplete.",
                    nameof(connection)
                );
            var values = view.Sources.Select(source => source.ServerValue.Trim()).ToArray();
            if (values.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException(
                    "Elastic source values cannot be blank.",
                    nameof(connection)
                );
            if (values.Distinct(StringComparer.Ordinal).Count() != values.Length)
                throw new ArgumentException(
                    "Elastic source values must be unique.",
                    nameof(connection)
                );
        }
    }

    private static TimeZoneInfo NaiveZone(AppTimeZoneMode mode) =>
        mode == AppTimeZoneMode.Local ? TimeZoneInfo.Local : TimeZoneInfo.Utc;

    private DateTimeOffset Now() =>
        _settings.TimeZoneMode == AppTimeZoneMode.Local
            ? DateTimeOffset.Now
            : DateTimeOffset.UtcNow;
}

public static class LogParserSelector
{
    public static ILogParser ForPath(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        return string.Equals(extension, ".logfmt", StringComparison.OrdinalIgnoreCase)
                ? new LogfmtParser()
            : string.Equals(extension, ".jsonl", StringComparison.OrdinalIgnoreCase)
                ? new JsonlParser()
            : new PlainTextParser();
    }
}
