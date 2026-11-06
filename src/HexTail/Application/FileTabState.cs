using HexTail.Domain;
using HexTail.Persistence;
using HexTail.Tailing;

namespace HexTail.Application;

public sealed class FileTabState : IAsyncDisposable
{
    private IReadOnlyList<ViewRule>? _effectiveGlobal;
    private IReadOnlyList<ViewRule>? _effectiveSource;
    private RuleSet _effectiveRules = RuleSet.Empty;

    internal FileTabState(
        string id,
        string path,
        FileBuffer buffer,
        ILogParser parser,
        ILogTailer tailer
    )
        : this(
            new LogSourceDescriptor(
                id,
                LogSourceKind.File,
                System.IO.Path.GetFileName(path),
                path,
                path
            ),
            buffer,
            parser,
            tailer
        ) { }

    internal FileTabState(
        LogSourceDescriptor source,
        FileBuffer buffer,
        ILogParser parser,
        ILogTailer tailer
    )
    {
        Source = source;
        Buffer = buffer;
        Parser = parser;
        Tailer = tailer;
        ElasticLoading = source.Kind == LogSourceKind.Elastic;
        Buffer.Changed += OnBufferChanged;
    }

    public LogSourceDescriptor Source { get; internal set; }
    public string Id => Source.Id;
    public string Path => Source.LocalPath ?? string.Empty;
    public FileBuffer Buffer { get; }
    public ILogParser Parser { get; }
    public List<Search> Searches { get; } = [];
    public bool FollowAll { get; set; } = true;
    public List<bool> FollowSearches { get; } = [];
    public LogLevels Levels { get; set; } = LogLevels.All;
    public int? SelectedLine { get; set; }

    /// <summary>Buffer indices of bookmarked rows. In-session only; never saved to the session file.</summary>
    public SortedSet<int> Bookmarks { get; } = [];

    /// <summary>Bumped whenever <see cref="Bookmarks"/> changes, so rows re-render their glyph.</summary>
    public int BookmarksVersion { get; private set; }

    public bool IsBookmarked(Line line)
    {
        foreach (var index in Bookmarks)
            if (index >= 0 && index < Buffer.Count && ReferenceEquals(Buffer[index], line))
                return true;
        return false;
    }

    internal void ToggleBookmarkAt(int index)
    {
        if (!Bookmarks.Remove(index))
            Bookmarks.Add(index);
        BookmarksVersion++;
    }

    public AppTimeZoneMode ElasticInputZone { get; set; } = AppTimeZoneMode.Utc;
    public string ElasticFrom { get; set; } = "now-5m";
    public string ElasticTo { get; set; } = "now";
    public bool ElasticLoading { get; internal set; }
    public int? ElasticInitialLimitReached { get; internal set; }
    public DateTimeOffset? ElasticResolvedFrom { get; internal set; }
    public DateTimeOffset? ElasticResolvedTo { get; internal set; }
    public string? Error { get; internal set; }

    /// <summary>A tab backed by a temporary file the app owns; it is never saved to the session.</summary>
    public bool Ephemeral { get; internal init; }
    public ILogTailer Tailer { get; internal set; }
    public string DisplayName => Source.DisplayName;

    public void AddSearch(Search search)
    {
        Searches.Add(search);
        FollowSearches.Add(true);
        Buffer.AddSearch(search);
    }

    public bool RemoveSearch(Search search)
    {
        if (search.IsGlobalRule)
            return false;
        var index = Searches.IndexOf(search);
        if (index < 0)
            return false;

        Searches.RemoveAt(index);
        FollowSearches.RemoveAt(index);
        return Buffer.RemoveSearch(search);
    }

    /// <summary>Source-scope rules for this open source.</summary>
    public IReadOnlyList<ViewRule> Rules { get; private set; } = [];

    /// <summary>Bumped whenever <see cref="Rules"/> is replaced, so views re-render highlights.</summary>
    public int RulesVersion { get; private set; }

    public void SetRules(IEnumerable<ViewRule> rules)
    {
        Rules = rules.ToList();
        RulesVersion++;
    }

    /// <summary>Global + source rules. UI thread only (it caches the last combination).</summary>
    public RuleSet EffectiveRules(AppSettings settings)
    {
        // RestoreAsync may call SetRules off the UI thread: read each list once.
        var global = settings.Rules;
        var source = Rules;
        if (
            !ReferenceEquals(_effectiveGlobal, global) || !ReferenceEquals(_effectiveSource, source)
        )
        {
            _effectiveRules = RuleSet.Combine(global, source);
            _effectiveGlobal = global;
            _effectiveSource = source;
        }
        return _effectiveRules;
    }

    public void SyncGlobalRuleSearches(IEnumerable<ViewRule> rules)
    {
        for (var index = Searches.Count - 1; index >= 0; index--)
        {
            if (!Searches[index].IsGlobalRule)
                continue;
            Buffer.RemoveSearch(Searches[index]);
            Searches.RemoveAt(index);
            FollowSearches.RemoveAt(index);
        }

        foreach (
            var rule in rules.Where(rule =>
                rule.Enabled && rule.PinnedAsTab && rule.Action != RuleAction.Hide
            )
        )
            try
            {
                AddSearch(
                    new Search(
                        new CompiledQuery(rule.Pattern, rule.Mode, rule.CaseSensitive),
                        rule.Color,
                        Buffer,
                        true
                    )
                );
            }
            catch (ArgumentException) { }
    }

    public async ValueTask DisposeAsync()
    {
        Buffer.Changed -= OnBufferChanged;
        await Tailer.DisposeAsync().ConfigureAwait(false);
    }

    private void OnBufferChanged(BufferChange change)
    {
        if (Bookmarks.Count > 0)
        {
            if (change.Cleared)
            {
                Bookmarks.Clear();
                BookmarksVersion++;
            }
            else if (change.RolledOutCount > 0)
            {
                var shifted = Bookmarks
                    .Select(index => index - change.RolledOutCount)
                    .Where(index => index >= 0)
                    .ToList();
                Bookmarks.Clear();
                Bookmarks.UnionWith(shifted);
                BookmarksVersion++;
            }
        }

        if (change.Cleared || SelectedLine is null)
            SelectedLine = null;
        else if (change.RolledOutCount > 0)
        {
            SelectedLine -= change.RolledOutCount;
            if (SelectedLine < 0)
                SelectedLine = null;
        }
    }
}
