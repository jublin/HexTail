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
        FileLoading = source.Kind == LogSourceKind.File;
        Buffer.Changed += OnBufferChanged;
    }

    /// <summary>Non-null only for stream tabs: the merge state behind this tab.</summary>
    public StreamState? Stream { get; internal init; }

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
    /// <remarks>
    /// Guarded by an internal lock inside this class. External readers (tests) must not enumerate it while
    /// the buffer can change off-thread (an Elastic connection replacement clears it); use <see cref="IsBookmarked"/>.
    /// </remarks>
    public SortedSet<int> Bookmarks { get; } = [];

    private readonly object _bookmarksGate = new();

    /// <summary>Bumped whenever <see cref="Bookmarks"/> changes, so rows re-render their glyph.</summary>
    public int BookmarksVersion { get; private set; }

    public bool IsBookmarked(Line line)
    {
        lock (_bookmarksGate)
        {
            foreach (var index in Bookmarks)
                if (index >= 0 && index < Buffer.Count && ReferenceEquals(Buffer[index], line))
                    return true;
            return false;
        }
    }

    internal void ToggleBookmarkAt(int index)
    {
        lock (_bookmarksGate)
        {
            if (!Bookmarks.Remove(index))
                Bookmarks.Add(index);
            BookmarksVersion++;
        }
    }

    public AppTimeZoneMode ElasticInputZone { get; set; } = AppTimeZoneMode.Utc;
    public string ElasticFrom { get; set; } = "now-5m";
    public string ElasticTo { get; set; } = "now";
    public bool ElasticLoading { get; internal set; }

    /// <summary>
    /// True for a file source until its initial read (or the reload after a reset) is complete, which its
    /// tailer reports with a SourceLoaded event, whether it delivered lines or found none. A stream re-merges once when a member's load completes.
    /// </summary>
    internal bool FileLoading { get; set; }

    /// <summary>The source is still loading: an Elastic range or a file's initial read or reload.</summary>
    internal bool IsLoading => ElasticLoading || FileLoading;
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

    private ColumnLayout? _columns;

    /// <summary>The user's column layout for this source; null means the defaults for its kind.</summary>
    public ColumnLayout? Columns
    {
        get => _columns;
        set
        {
            _columns = value;
            ColumnsVersion++;
        }
    }

    /// <summary>Bumped on every change of <see cref="Columns"/>, so views re-render their rows.</summary>
    public int ColumnsVersion { get; private set; }

    /// <summary>The layout to render: the user's, or the defaults (Elastic sources add the view's output fields).</summary>
    public ColumnLayout EffectiveColumns(
        IReadOnlyList<string>? elasticOutputFields,
        double fontSize = ColumnLayout.DefaultFontSize
    ) => Columns ?? ColumnLayout.DefaultFor(Source, elasticOutputFields, fontSize);

    /// <summary>
    /// True when rows render as columns: JSONL and logfmt files, every Elastic source, and every stream
    /// (the Source column keeps members distinguishable by text, not only colour; a plain line shows its
    /// raw text as the message).
    /// </summary>
    public bool IsStructured =>
        Source.Kind is LogSourceKind.Elastic or LogSourceKind.Stream
        || (Parser is TimestampingParser wrapped ? wrapped.Inner : Parser)
            is JsonlParser
                or LogfmtParser;

    /// <summary>
    /// The parsed-field names seen in the last <paramref name="sample"/> lines, sorted, at most
    /// <paramref name="cap"/> of them. Feeds the "add column" picker.
    /// </summary>
    public IReadOnlyList<string> KnownFields(int sample = 500, int cap = 100)
    {
        HashSet<string> keys = new(StringComparer.Ordinal);
        var lines = Buffer.Lines;
        var end = lines.Count;
        for (var index = end - 1; index >= Math.Max(0, end - sample); index--)
        {
            Line line;
            try
            {
                line = lines[index];
            }
            catch (ArgumentOutOfRangeException)
            {
                continue; // the buffer shrank under us (e.g. a connection replacement cleared it)
            }
            if (line.ParsedFields is { Count: > 0 } fields)
                foreach (var key in fields.Keys)
                    keys.Add(key);
        }
        return [.. keys.Order(StringComparer.Ordinal).Take(cap)];
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
        lock (_bookmarksGate)
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
                    var dropped = shifted.Count < Bookmarks.Count;
                    Bookmarks.Clear();
                    Bookmarks.UnionWith(shifted);
                    // An index-only shift keeps the same lines bookmarked (rows compare by Line reference).
                    if (dropped)
                        BookmarksVersion++;
                }
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
