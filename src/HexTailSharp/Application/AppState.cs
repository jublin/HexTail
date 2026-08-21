using HexTailSharp.Domain;
using HexTailSharp.Elastic;
using HexTailSharp.Persistence;
using HexTailSharp.Security;
using HexTailSharp.Tailing;

namespace HexTailSharp.Application;

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
    private AppSettings _settings;
    private int _nextFileId;
    private Task? _healthLoop;
    private int _disposed;

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
        _elastic = elastic ?? new ElasticApiClient(new HttpClient());
        _health = new ElasticHealthMonitor(_elastic, _credentials);
        _health.Changed += NotifyChanged;
        _settings = NormalizeSettings(settings ?? new AppSettings());
    }

    public IReadOnlyList<FileTabState> Files
    {
        get
        {
            lock (_gate)
                return _files.ToArray();
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
        secret = string.IsNullOrWhiteSpace(secret) ? previousSecret : secret;
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
        NotifyChanged();
        SignalHealthCheck();
    }

    public async ValueTask RemoveElasticConnectionAsync(
        string connectionId,
        CancellationToken cancellationToken = default
    )
    {
        var previous = _settings;
        lock (_gate)
            _settings = NormalizeSettings(
                previous with
                {
                    ElasticConnections = previous
                        .ElasticConnections.Where(item => item.Id != connectionId)
                        .ToList(),
                }
            );
        try
        {
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
                _settings = previous;
            throw;
        }
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

    public async ValueTask RestoreAsync(CancellationToken cancellationToken = default)
    {
        var config = await _persistence.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (config is null)
            return;

        lock (_gate)
        {
            _settings = NormalizeSettings(config.Settings ?? new AppSettings());
            Window = config.Window ?? new AppWindowState();
        }
        foreach (var persisted in config.OpenFiles)
        {
            var tab = await OpenFileAsync(persisted.Path, save: false, cancellationToken)
                .ConfigureAwait(false);
            tab.FollowAll = persisted.FollowAll;
            tab.ShowContext = persisted.ShowContext;
            tab.SelectedLine = persisted.SelectedLine;
            tab.ContextAbove = persisted.ContextAbove;
            tab.ContextBelow = persisted.ContextBelow;

            foreach (var search in persisted.Searches)
            {
                try
                {
                    var active = new Search(
                        new CompiledQuery(search.Query, search.Mode, search.CaseSensitive),
                        search.Color,
                        tab.Buffer
                    );
                    tab.AddSearch(active);
                }
                catch (ArgumentException)
                {
                    // A stale invalid regex must not prevent the rest of the session from loading.
                }
            }

            for (var i = 0; i < tab.FollowSearches.Count && i < persisted.FollowSearches.Count; i++)
                tab.FollowSearches[i] = persisted.FollowSearches[i];
        }
        foreach (var persisted in config.OpenElasticTabs)
        {
            try
            {
                var tab = await OpenElasticSourceAsync(
                        persisted.SourceId,
                        save: false,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                tab.FollowAll = persisted.FollowAll;
                tab.ShowContext = persisted.ShowContext;
                tab.ContextAbove = persisted.ContextAbove;
                tab.ContextBelow = persisted.ContextBelow;
                foreach (var search in persisted.Searches)
                    try
                    {
                        tab.AddSearch(
                            new Search(
                                new CompiledQuery(search.Query, search.Mode, search.CaseSensitive),
                                search.Color,
                                tab.Buffer
                            )
                        );
                    }
                    catch (ArgumentException) { }
                for (
                    var i = 0;
                    i < tab.FollowSearches.Count && i < persisted.FollowSearches.Count;
                    i++
                )
                    tab.FollowSearches[i] = persisted.FollowSearches[i];
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        StartHealthLoop();
        if (_settings.ElasticConnections.Count > 0)
            SignalHealthCheck();

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
        }
        NotifyChanged();
    }

    public async ValueTask<FileTabState> OpenFileAsync(
        string path,
        bool save = true,
        CancellationToken cancellationToken = default
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
                var parser = LogParserSelector.ForPath(fullPath);
                var tailer = _tailers.StartFile(id, fullPath, parser);
                tab = new FileTabState(id, fullPath, buffer, parser, tailer)
                {
                    ContextAbove = _settings.ContextAbove,
                    ContextBelow = _settings.ContextBelow,
                    Error = File.Exists(fullPath) ? null : "File missing",
                };
                _files.Add(tab);
                SelectedFile = tab;
            }
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
        CancellationToken cancellationToken = default
    )
    {
        var match = _settings
            .ElasticConnections.SelectMany(connection =>
                connection.Sources.Select(source => (connection, source))
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
                return existing;
            }
        }
        var connection = match.connection;
        if (
            string.IsNullOrWhiteSpace(connection.DataViewTitle)
            || string.IsNullOrWhiteSpace(connection.TimeFieldName)
            || string.IsNullOrWhiteSpace(connection.ServerField)
            || string.IsNullOrWhiteSpace(connection.NamespaceField)
        )
            throw new ArgumentException(
                "The Elastic source configuration is incomplete.",
                nameof(sourceId)
            );
        var secret = connection.AuthMode is ElasticAuthMode.Basic or ElasticAuthMode.ApiKey
            ? _credentials.Get(connection.Id)
                ?? throw new InvalidOperationException("The Elastic credential is unavailable.")
            : string.Empty;
        var tailer = _tailers.StartElastic(connection, match.source, secret, _elastic);
        var tab = new FileTabState(
            new LogSourceDescriptor(
                sourceId,
                LogSourceKind.Elastic,
                match.source.DisplayName,
                $"{match.connection.Name}: {match.source.DisplayName}",
                ElasticSourceId: sourceId
            ),
            new FileBuffer(_settings.MaxLines),
            new PlainTextParser(),
            tailer
        )
        {
            ContextAbove = _settings.ContextAbove,
            ContextBelow = _settings.ContextBelow,
        };
        lock (_gate)
        {
            _files.Add(tab);
            SelectedFile = tab;
        }
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
        tab.ElasticFrom = from.Trim();
        tab.ElasticTo = to.Trim();
    }

    public IReadOnlyDictionary<string, ElasticSourceHealth> ElasticSourceStatuses =>
        _health.Statuses;
    public bool HasElasticWarning => _health.HasWarning;

    public async ValueTask CloseFileAsync(
        FileTabState tab,
        CancellationToken cancellationToken = default
    )
    {
        lock (_gate)
        {
            if (!_files.Remove(tab))
                return;

            SelectedFile = _files.FirstOrDefault();
        }
        await tab.DisposeAsync().ConfigureAwait(false);
        NotifyChanged();
        SignalHealthCheck();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
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

    public async ValueTask SetShowContextAsync(
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
            tab.ShowContext = value;
        }
        NotifyChanged();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
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

    public void ToggleExpanded(FileTabState tab, Line line)
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

            tab.ExpandedLine = tab.ExpandedLine == index ? null : index;
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
            _settings = NormalizeSettings(settings);
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

    private static int FindLineIndex(FileTabState tab, Line line)
    {
        for (var index = 0; index < tab.Buffer.Count; index++)
            if (ReferenceEquals(tab.Buffer[index], line))
                return index;
        return -1;
    }

    public bool DrainTailerEvents()
    {
        var changed = false;
        while (_tailers.Events.TryRead(out var sourceEvent))
        {
            FileTabState? tab;
            lock (_gate)
                tab = _files.FirstOrDefault(file => file.Id == sourceEvent.SourceId);
            if (tab is null)
                continue;

            switch (sourceEvent)
            {
                case SourceLines newLines:
                    tab.Error = null;
                    tab.Buffer.Append(newLines.Lines);
                    break;
                case SourceReset:
                    tab.Error = null;
                    tab.Buffer.Clear();
                    break;
                case SourceError error:
                    tab.Error = $"Source error: {error.Message}";
                    break;
                case SourceRecovered:
                    tab.Error = null;
                    break;
            }

            changed = true;
        }

        if (changed)
            NotifyChanged();

        return changed;
    }

    public async ValueTask SaveAsync(CancellationToken cancellationToken = default)
    {
        AppConfig config;
        lock (_gate)
        {
            config = new AppConfig
            {
                OpenFiles = _files
                    .Where(tab => tab.Source.Kind == LogSourceKind.File)
                    .Select(tab => new PersistedFileTab
                    {
                        Path = tab.Path,
                        FollowAll = tab.FollowAll,
                        FollowSearches = [.. tab.FollowSearches],
                        ShowContext = tab.ShowContext,
                        SelectedLine = tab.SelectedLine,
                        ContextAbove = tab.ContextAbove,
                        ContextBelow = tab.ContextBelow,
                        Searches = tab
                            .Searches.Select(search => new PersistedSearch
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
                    .Where(tab => tab.Source.Kind == LogSourceKind.Elastic)
                    .Select(tab => new PersistedElasticTab
                    {
                        SourceId = tab.Source.ElasticSourceId!,
                        FollowAll = tab.FollowAll,
                        FollowSearches = [.. tab.FollowSearches],
                        ShowContext = tab.ShowContext,
                        SelectedLine = tab.SelectedLine,
                        ContextAbove = tab.ContextAbove,
                        ContextBelow = tab.ContextBelow,
                        Searches = tab
                            .Searches.Select(search => new PersistedSearch
                            {
                                Query = search.Query.Query,
                                Mode = search.Query.Mode,
                                CaseSensitive = search.Query.CaseSensitive,
                                Color = search.Color,
                            })
                            .ToList(),
                    })
                    .ToList(),
                SelectedFilePath =
                    SelectedFile?.Source.Kind == LogSourceKind.File ? SelectedFile.Path : null,
                SelectedElasticSourceId = SelectedFile?.Source.ElasticSourceId,
                Window = Window,
                Settings = _settings,
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
        var labels = (settings.GlobalLabels ?? [])
            .OfType<GlobalLabel>()
            .Where(label => !string.IsNullOrWhiteSpace(label.Text))
            .Select(label => new GlobalLabel
            {
                Text = label.Text.Trim(),
                Color = NormalizeColor(label.Color),
            })
            .DistinctBy(label => label.Text, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var exclusions = (settings.GlobalExcludeLabels ?? [])
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(label => label.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AppSettings
        {
            MaxLines = settings.MaxLines,
            ContextAbove = settings.ContextAbove,
            ContextBelow = settings.ContextBelow,
            GlobalLabels = labels,
            GlobalExcludeLabels = exclusions,
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

    private static List<ElasticViewSettings> NormalizeElasticViews(
        ElasticConnectionSettings connection
    )
    {
        var views = connection
            .Views.Where(view => view is not null && !string.IsNullOrWhiteSpace(view.Id))
            .GroupBy(view => view.Id.Trim(), StringComparer.Ordinal)
            .Select(group => NormalizeElasticView(group.First()))
            .ToList();
        if (views.Count > 0)
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

    private string? SecretFor(ElasticConnectionSettings connection) =>
        connection.AuthMode is ElasticAuthMode.Basic or ElasticAuthMode.ApiKey
            ? _credentials.Get(connection.Id)
            : null;

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
        if (
            !Uri.TryCreate(connection.ElasticsearchUrl, UriKind.Absolute, out var elastic)
            || elastic.Scheme is not ("http" or "https")
        )
            throw new ArgumentException(
                "Elasticsearch URL must be absolute HTTP or HTTPS.",
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
        if (
            string.IsNullOrWhiteSpace(connection.DataViewTitle)
            || string.IsNullOrWhiteSpace(connection.TimeFieldName)
            || string.IsNullOrWhiteSpace(connection.ServerField)
            || string.IsNullOrWhiteSpace(connection.NamespaceField)
            || connection.OutputFields.Count == 0
        )
            throw new ArgumentException(
                "The Elastic data view and field mappings are incomplete.",
                nameof(connection)
            );
        var pairs = connection
            .Sources.Select(source => (source.ServerValue.Trim(), source.NamespaceValue.Trim()))
            .ToArray();
        if (
            pairs.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Item1) || string.IsNullOrWhiteSpace(pair.Item2)
            )
        )
            throw new ArgumentException(
                "Elastic source values cannot be blank.",
                nameof(connection)
            );
        if (pairs.Distinct().Count() != pairs.Length)
            throw new ArgumentException(
                "Elastic source values must be unique.",
                nameof(connection)
            );
    }
}

public static class LogParserSelector
{
    public static ILogParser ForPath(string path) =>
        string.Equals(
            System.IO.Path.GetExtension(path),
            ".logfmt",
            StringComparison.OrdinalIgnoreCase
        )
            ? new LogfmtParser()
            : new PlainTextParser();
}
