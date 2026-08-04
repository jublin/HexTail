using HexTailSharp.Domain;
using HexTailSharp.Persistence;
using HexTailSharp.Tailing;

namespace HexTailSharp.Application;

public sealed class AppState : IAsyncDisposable
{
    private readonly TailerService _tailers;
    private readonly IAppPersistence _persistence;
    private readonly List<FileTabState> _files = [];
    private AppSettings _settings;
    private int _nextFileId;

    public AppState(
        TailerService tailers,
        IAppPersistence persistence,
        AppSettings? settings = null
    )
    {
        _tailers = tailers;
        _persistence = persistence;
        _settings = NormalizeSettings(settings ?? new AppSettings());
    }

    public IReadOnlyList<FileTabState> Files => _files;
    public FileTabState? SelectedFile { get; private set; }
    public AppWindowState Window { get; private set; } = new();
    public AppSettings Settings => _settings;
    public event Action? Changed;

    public async ValueTask RestoreAsync(CancellationToken cancellationToken = default)
    {
        var config = await _persistence.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (config is null)
            return;

        _settings = NormalizeSettings(config.Settings ?? new AppSettings());
        Window = config.Window ?? new AppWindowState();
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

        if (config.SelectedFilePath is not null)
            SelectedFile = _files.FirstOrDefault(file =>
                string.Equals(
                    file.Path,
                    config.SelectedFilePath,
                    StringComparison.OrdinalIgnoreCase
                )
            );
        SelectedFile ??= _files.FirstOrDefault();
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
        var existing = _files.FirstOrDefault(file =>
            string.Equals(file.Path, fullPath, StringComparison.OrdinalIgnoreCase)
        );
        if (existing is not null)
        {
            SelectedFile = existing;
            NotifyChanged();
            return existing;
        }

        var id = $"file-{++_nextFileId}";
        var buffer = new FileBuffer(_settings.MaxLines);
        var parser = LogParserSelector.ForPath(fullPath);
        var tailer = _tailers.StartTailer(id, fullPath);
        var tab = new FileTabState(id, fullPath, buffer, parser, tailer)
        {
            ContextAbove = _settings.ContextAbove,
            ContextBelow = _settings.ContextBelow,
            Error = File.Exists(fullPath) ? null : "File missing",
        };
        _files.Add(tab);
        SelectedFile = tab;
        NotifyChanged();
        if (save)
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        return tab;
    }

    public async ValueTask CloseFileAsync(
        FileTabState tab,
        CancellationToken cancellationToken = default
    )
    {
        if (!_files.Remove(tab))
            return;

        await tab.DisposeAsync().ConfigureAwait(false);
        SelectedFile = _files.FirstOrDefault();
        NotifyChanged();
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
        var search = new Search(new CompiledQuery(query, mode, caseSensitive), color, tab.Buffer);
        tab.AddSearch(search);
        NotifyChanged();
        return search;
    }

    public void SelectFile(FileTabState? tab)
    {
        if (tab is null || _files.Contains(tab))
            SelectedFile = tab;
        NotifyChanged();
    }

    public async ValueTask SetFollowAllAsync(
        FileTabState tab,
        bool value,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (!_files.Contains(tab) || tab.FollowAll == value)
            return;

        tab.FollowAll = value;
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
        if (!_files.Contains(tab) || tab.ShowContext == value)
            return;

        tab.ShowContext = value;
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
        var index = tab.Searches.IndexOf(search);
        if (index < 0 || index >= tab.FollowSearches.Count || tab.FollowSearches[index] == value)
            return;

        tab.FollowSearches[index] = value;
        NotifyChanged();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public void SelectLine(FileTabState tab, Line? line)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (!_files.Contains(tab))
            return;

        tab.SelectedLine = line is null ? null : FindLineIndex(tab, line);
        NotifyChanged();
    }

    public void ToggleExpanded(FileTabState tab, Line line)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(line);
        if (!_files.Contains(tab))
            return;

        var index = FindLineIndex(tab, line);
        if (index < 0)
            return;

        tab.ExpandedLine = tab.ExpandedLine == index ? null : index;
        NotifyChanged();
    }

    public async ValueTask UpdateSettingsAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = NormalizeSettings(settings);
        NotifyChanged();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public void SetWindowState(AppWindowState window)
    {
        ArgumentNullException.ThrowIfNull(window);
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
        while (_tailers.Events.TryRead(out var tailerEvent))
        {
            var tab = _files.FirstOrDefault(file => file.Id == tailerEvent.FileId);
            if (tab is null)
                continue;

            switch (tailerEvent)
            {
                case NewLines newLines:
                    tab.Error = null;
                    tab.Buffer.Append(newLines.Lines.Select(tab.Parser.Parse));
                    break;
                case FileRotated:
                case FileTruncated:
                    tab.Error = null;
                    tab.Buffer.Clear();
                    break;
                case TailerError error:
                    tab.Error = $"Tailer error: {error.Message}";
                    break;
                case TailerRecovered:
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
        var config = new AppConfig
        {
            OpenFiles = _files
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
            SelectedFilePath = SelectedFile?.Path,
            Window = Window,
            Settings = _settings,
        };
        await _persistence.SaveAsync(config, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await SaveAsync().ConfigureAwait(false);
        foreach (var file in _files)
            await file.DisposeAsync().ConfigureAwait(false);
        await _tailers.DisposeAsync().ConfigureAwait(false);
    }

    private void NotifyChanged() => Changed?.Invoke();

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
            SettingsMenuAlignment = Enum.IsDefined(settings.SettingsMenuAlignment)
                ? settings.SettingsMenuAlignment
                : SettingsMenuAlignment.Right,
        };
    }

    private static string NormalizeColor(string? color) =>
        color is { Length: 4 or 7 } && color[0] == '#' && color[1..].All(Uri.IsHexDigit)
            ? color
            : "#f59e0b";
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
