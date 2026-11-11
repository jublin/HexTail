using HexTail.Domain;
using HexTail.Elastic;
using HexTail.Persistence;

namespace HexTail.Application;

/// <summary>
/// The merge state behind one open stream tab: its definition, the watermark merger, which of its
/// members are currently open, and per-tab view settings (hidden sources, time range).
/// <para>
/// Mutation happens only under AppState's lock (the merger is not thread-safe). The member snapshot is
/// immutable and swapped atomically, so views can read colours, names and missing members without
/// locking; <see cref="IsLate"/> takes the same lock as the drain, so it is safe from any thread.
/// </para>
/// </summary>
public sealed class StreamState
{
    private readonly TimeSpan _hold;
    private readonly Func<DateTimeOffset> _clock;
    private readonly HashSet<string> _hiddenKeys = new(StreamMember.KeyComparer);
    private readonly object _gate;

    // Copy-on-write: every member that was ever open in this stream, so rows from a member that has
    // since closed keep their colour and name. Replaced whole, so readers never see a half-written map.
    private volatile Dictionary<string, (string Color, FileTabState? Tab)> _everSeen = new(
        StringComparer.Ordinal
    );
    private Snapshot _snapshot = Snapshot.Empty;

    // Members that are still loading (just opened, reset, or an Elastic range in flight): the lines they
    // deliver are merged by re-merging the stream once the load is complete, so a source that is still
    // loading does not leave late rows behind. The value is true once a member delivered lines meanwhile.
    private readonly Dictionary<string, bool> _loading = new(StringComparer.Ordinal);

    internal StreamState(
        StreamSettings definition,
        TimeSpan hold,
        Func<DateTimeOffset> clock,
        object? gate = null
    )
    {
        _gate = gate ?? new object();
        Definition = definition;
        _hold = hold;
        _clock = clock;
        Merger = new StreamMerger([], hold, clock);
    }

    public StreamSettings Definition { get; internal set; }

    /// <summary>The watermark merger. Replaced when the set of open members changes. AppState's lock guards it.</summary>
    public StreamMerger Merger { get; private set; }

    /// <summary>Tab ids of the open members, in member order.</summary>
    public IReadOnlyList<string> MemberIds => _snapshot.MemberIds;

    /// <summary>Keys of members that are not open (missing file, unknown or unopened Elastic source).</summary>
    public IReadOnlyList<string> MissingMembers => _snapshot.Missing;

    /// <summary>Tab ids whose rows are hidden by the per-source toggles. Replaced, never mutated.</summary>
    public IReadOnlySet<string> HiddenSources => _snapshot.HiddenIds;

    /// <summary>The stream's one time range: its expressions (<c>now-15m</c>, <c>now</c> or ISO timestamps).</summary>
    public string RangeFrom { get; private set; } = "now-15m";

    public string RangeTo { get; private set; } = "now";

    /// <summary>True (the default) when the range does not filter the rows of file members.</summary>
    public bool Live { get; private set; } = true;

    // The range resolved to instants for the per-line comparison; null when the range does not filter.
    // Replaced whole, so a filter pass reads it once. Relative expressions are resolved when the range is
    // applied or restored and again on every stream rebuild (a member reset), not on every drain.
    private volatile ResolvedRange? _resolved;

    /// <summary>
    /// Stores the stream's range and resolves it. <paramref name="live"/> drops the file filter (the
    /// expressions are kept so the editor still shows the last range).
    /// </summary>
    internal void SetRange(string from, string to, bool live)
    {
        RangeFrom = string.IsNullOrWhiteSpace(from) ? "now-15m" : from.Trim();
        RangeTo = string.IsNullOrWhiteSpace(to) ? "now" : to.Trim();
        Live = live;
        ResolveRange();
    }

    /// <summary>Re-resolves relative bounds against the clock. Called on apply, restore and rebuild.</summary>
    internal void ResolveRange()
    {
        if (Live)
        {
            _resolved = null;
            return;
        }
        try
        {
            var now = _clock();
            var from = ElasticTailer.ParseTime(RangeFrom, now);
            // "now" is an open end: rows that arrive after the range was applied keep showing.
            DateTimeOffset? to = RangeTo.Equals("now", StringComparison.OrdinalIgnoreCase)
                ? null
                : ElasticTailer.ParseTime(RangeTo, now);
            _resolved = new ResolvedRange(from, to);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            // A damaged saved range filters nothing rather than hiding every row.
            _resolved = null;
        }
    }

    /// <summary>
    /// The per-line test that keeps a row visible under the stream's range, or null when nothing is
    /// filtered. Rows without a timestamp and rows of Elastic members (which already come from the queried
    /// window) always pass. Built once per filter pass; the hot path is two comparisons per timestamped line.
    /// </summary>
    internal Func<Line, bool>? CreateRangePredicate()
    {
        var range = _resolved;
        if (range is null)
            return null;
        HashSet<string>? elastic = null;
        foreach (var (id, seen) in _everSeen)
            if (seen.Tab?.Source.Kind == LogSourceKind.Elastic)
                (elastic ??= new HashSet<string>(StringComparer.Ordinal)).Add(id);
        var from = range.From;
        var to = range.To;
        return line =>
        {
            if (line.Timestamp is not { } time)
                return true;
            if (time >= from && (to is not { } end || time <= end))
                return true;
            return elastic is not null && line.SourceId is { } id && elastic.Contains(id);
        };
    }

    /// <summary>The number of Elastic members that are open right now.</summary>
    public int OpenElasticMemberCount
    {
        get
        {
            var count = 0;
            foreach (var tab in _snapshot.Tabs)
                if (tab.Source.Kind == LogSourceKind.Elastic)
                    count++;
            return count;
        }
    }

    private sealed record ResolvedRange(DateTimeOffset From, DateTimeOffset? To);

    /// <summary>True when any open member carries parsed fields, so the stream offers column mode.</summary>
    public bool AnyMemberStructured
    {
        get
        {
            foreach (var tab in _snapshot.Tabs)
                if (tab.IsStructured)
                    return true;
            return false;
        }
    }

    /// <summary>True once the member set changed in a way that needs the next drain to re-merge every member.</summary>
    internal bool NeedsRebuild { get; set; }

    internal IReadOnlyList<FileTabState> MemberTabs => _snapshot.Tabs;

    /// <summary>The member colour of a line's <c>SourceId</c> (also for members that have since closed).</summary>
    public string ColorFor(string? sourceId) =>
        sourceId is not null && _everSeen.TryGetValue(sourceId, out var seen)
            ? seen.Color
            : Fallback;

    /// <summary>The member's display name, or the source id itself when the member is not known.</summary>
    public string DisplayNameFor(string? sourceId) =>
        sourceId is null ? string.Empty
        : _everSeen.TryGetValue(sourceId, out var seen) && seen.Tab is { } tab ? tab.DisplayName
        : sourceId;

    /// <summary>
    /// True when the line was released out of order. Takes AppState's lock (the merger's late set is
    /// mutated by the drain), so it is safe from any thread; it is uncontended on the UI thread that
    /// also drains, and the lock is reentrant if called from inside a drain.
    /// </summary>
    public bool IsLate(Line line)
    {
        lock (_gate)
            return Merger.IsLate(line);
    }

    internal const string Fallback = "#94a3b8";

    internal IReadOnlyCollection<string> HiddenKeys => _hiddenKeys;

    /// <summary>
    /// A loading member delivered lines. Returns true when the stream must re-merge now: a file member's
    /// load is complete once the last piece of its batch is in (<paramref name="moreComing"/> is false); an
    /// Elastic member keeps loading until its range-loaded event (<see cref="NoteLoaded"/>), because its
    /// rows arrive as several pages. Until then the stream merges the lines as they come. Each arming is
    /// consumed exactly once, so it re-arms only on a reset or a member change and never loops.
    /// </summary>
    internal bool NoteDelivery(string memberId, bool moreComing)
    {
        if (_loading.Count == 0 || !_loading.ContainsKey(memberId))
            return false;
        if (moreComing || IsElastic(memberId))
        {
            _loading[memberId] = true;
            return false;
        }
        _loading.Remove(memberId);
        return true;
    }

    /// <summary>
    /// A loading member finished loading (a file's initial read or reload, an Elastic range or its error).
    /// Returns true when the member delivered lines while loading, which the stream has not re-merged yet.
    /// A member that loaded nothing is simply quiet: nothing to re-merge, and its first line later is an
    /// ordinary incremental add.
    /// </summary>
    internal bool NoteLoaded(string memberId) =>
        _loading.Remove(memberId, out var delivered) && delivered;

    /// <summary>True while any member of this stream is still loading.</summary>
    internal bool AnyMemberLoading => _loading.Count > 0;

    /// <summary>
    /// The row to select once the stream has settled after a restore (every member loaded and the stream
    /// re-merged), because each rebuild clears the selection. Applied once; see AppState.
    /// </summary>
    internal int? PendingSelection { get; set; }

    private bool IsElastic(string memberId) =>
        _everSeen.TryGetValue(memberId, out var seen)
        && seen.Tab?.Source.Kind == LogSourceKind.Elastic;

    /// <summary>
    /// A member was reset (its buffer cleared) and reloads asynchronously: the rebuild that follows the
    /// reset runs while the member is still empty and releases the other members' lines past the ones about
    /// to be reloaded, so the stream re-merges once the reload is in. Re-arms only on a reset.
    /// </summary>
    internal void AwaitReload(string memberId)
    {
        if (_snapshot.MemberIds.Contains(memberId))
            _loading[memberId] = false;
    }

    /// <summary>The key of the open member behind a tab id, or null.</summary>
    internal string? KeyOf(string sourceId)
    {
        var snapshot = _snapshot;
        for (var i = 0; i < snapshot.MemberIds.Count; i++)
            if (snapshot.MemberIds[i] == sourceId)
                return snapshot.Keys[i];
        return null;
    }

    /// <summary>
    /// Points the stream at the member tabs that are open right now (<paramref name="open"/> maps member
    /// key to tab). Creates a fresh merger for the new member set and flags a rebuild.
    /// </summary>
    internal void SetMembers(
        StreamSettings definition,
        IReadOnlyDictionary<string, FileTabState> open
    )
    {
        Definition = definition;
        var ids = new List<string>();
        var keys = new List<string>();
        var tabs = new List<FileTabState>();
        var missing = new List<string>();
        var seen = new Dictionary<string, (string Color, FileTabState? Tab)>(
            _everSeen,
            StringComparer.Ordinal
        );
        foreach (var member in definition.Members)
        {
            if (open.TryGetValue(member.Key, out var tab))
            {
                // Keys that differ only in case resolve to one tab: it is one member.
                if (ids.Contains(tab.Id))
                    continue;
                ids.Add(tab.Id);
                keys.Add(member.Key);
                tabs.Add(tab);
                seen[tab.Id] = (member.Color, tab);
            }
            else
                missing.Add(member.Key);
        }
        _everSeen = seen;
        // Only members that are loading are armed; a member that is simply quiet is not, so its first
        // line much later does not rebuild the stream (which would clear selection, bookmarks, late marks).
        _loading.Clear();
        foreach (var tab in tabs)
            if (tab.IsLoading)
                _loading[tab.Id] = false;
        _hiddenKeys.IntersectWith(definition.Members.Select(member => member.Key));
        _snapshot = new Snapshot(ids, keys, tabs, missing, HiddenIdsFor(ids, keys));
        Merger = new StreamMerger(ids, _hold, _clock);
        NeedsRebuild = true;
    }

    /// <summary>
    /// A member tab closed: it moves to the missing list but the merger keeps running (the closed
    /// member simply stops delivering, and idle members never hold the watermark back).
    /// </summary>
    internal void DetachMember(FileTabState tab)
    {
        var index = -1;
        for (var i = 0; i < _snapshot.Tabs.Count; i++)
            if (ReferenceEquals(_snapshot.Tabs[i], tab))
                index = i;
        if (index < 0)
            return;
        var ids = _snapshot.MemberIds.Where((_, i) => i != index).ToList();
        var keys = _snapshot.Keys.Where((_, i) => i != index).ToList();
        var tabs = _snapshot.Tabs.Where((_, i) => i != index).ToList();
        var missing = new List<string>(_snapshot.Missing) { _snapshot.Keys[index] };
        // Keep the member-order of the definition for the missing list.
        missing.Sort(
            (a, b) =>
                Definition.Members.FindIndex(m => StreamMember.KeyComparer.Equals(m.Key, a))
                - Definition.Members.FindIndex(m => StreamMember.KeyComparer.Equals(m.Key, b))
        );
        _snapshot = new Snapshot(ids, keys, tabs, missing, HiddenIdsFor(ids, keys));
    }

    internal void SetHiddenKeys(IEnumerable<string> keys)
    {
        _hiddenKeys.Clear();
        foreach (var key in keys)
            if (Definition.Members.Any(member => StreamMember.KeyComparer.Equals(member.Key, key)))
                _hiddenKeys.Add(key);
        _snapshot = _snapshot with
        {
            HiddenIds = HiddenIdsFor(_snapshot.MemberIds, _snapshot.Keys),
        };
    }

    private HashSet<string> HiddenIdsFor(IReadOnlyList<string> ids, IReadOnlyList<string> keys)
    {
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < ids.Count; i++)
            if (_hiddenKeys.Contains(keys[i]))
                hidden.Add(ids[i]);
        return hidden;
    }

    private sealed record Snapshot(
        IReadOnlyList<string> MemberIds,
        IReadOnlyList<string> Keys,
        IReadOnlyList<FileTabState> Tabs,
        IReadOnlyList<string> Missing,
        HashSet<string> HiddenIds
    )
    {
        public static readonly Snapshot Empty = new([], [], [], [], []);
    }
}
