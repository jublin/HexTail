using HexTailSharp.Domain;
using HexTailSharp.Persistence;
using HexTailSharp.Tailing;

namespace HexTailSharp.Application;

public sealed class AppState : IAsyncDisposable
{
    private readonly TailerService _tailers;
    private readonly IAppPersistence _persistence;
    private readonly List<FileTabState> _files = [];
    private readonly AppSettings _settings;
    private int _nextFileId;

    public AppState(TailerService tailers, IAppPersistence persistence, AppSettings? settings = null)
    {
        _tailers = tailers;
        _persistence = persistence;
        _settings = settings ?? new AppSettings();
    }

    public IReadOnlyList<FileTabState> Files => _files;
    public FileTabState? SelectedFile { get; private set; }
    public AppWindowState Window { get; private set; } = new();
    public event Action? Changed;

    public async ValueTask RestoreAsync(CancellationToken cancellationToken = default)
    {
        var config = await _persistence.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (config is null)
            return;

        Window = config.Window;
        foreach (var persisted in config.OpenFiles)
        {
            var tab = await OpenFileAsync(persisted.Path, save: false, cancellationToken).ConfigureAwait(false);
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
                        tab.Buffer);
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
            SelectedFile = _files.FirstOrDefault(file => string.Equals(file.Path, config.SelectedFilePath, StringComparison.OrdinalIgnoreCase));
        SelectedFile ??= _files.FirstOrDefault();
        NotifyChanged();
    }

    public async ValueTask<FileTabState> OpenFileAsync(
        string path,
        bool save = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = System.IO.Path.GetFullPath(path);
        var existing = _files.FirstOrDefault(file => string.Equals(file.Path, fullPath, StringComparison.OrdinalIgnoreCase));
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

    public async ValueTask CloseFileAsync(FileTabState tab, CancellationToken cancellationToken = default)
    {
        if (!_files.Remove(tab))
            return;

        await tab.DisposeAsync().ConfigureAwait(false);
        SelectedFile = _files.FirstOrDefault();
        NotifyChanged();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public Search AddSearch(FileTabState tab, string query, MatchMode mode, bool caseSensitive, string color)
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

    public void DrainTailerEvents()
    {
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
            }
        }

        NotifyChanged();
    }

    public async ValueTask SaveAsync(CancellationToken cancellationToken = default)
    {
        var config = new AppConfig
        {
            OpenFiles = _files.Select(tab => new PersistedFileTab
            {
                Path = tab.Path,
                FollowAll = tab.FollowAll,
                FollowSearches = [.. tab.FollowSearches],
                ShowContext = tab.ShowContext,
                SelectedLine = tab.SelectedLine,
                ContextAbove = tab.ContextAbove,
                ContextBelow = tab.ContextBelow,
                Searches = tab.Searches.Select(search => new PersistedSearch
                {
                    Query = search.Query.Query,
                    Mode = search.Query.Mode,
                    CaseSensitive = search.Query.CaseSensitive,
                    Color = search.Color,
                }).ToList(),
            }).ToList(),
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
}

public static class LogParserSelector
{
    public static ILogParser ForPath(string path) =>
        string.Equals(System.IO.Path.GetExtension(path), ".logfmt", StringComparison.OrdinalIgnoreCase)
            ? new LogfmtParser()
            : new PlainTextParser();
}
