namespace HexTail.Domain;

/// <summary>
/// Merges lines from several sources ("members") into one timestamp-ordered stream while the
/// sources keep delivering in separate, unsynchronised batches.
/// <para>
/// Each member owns a FIFO queue. <see cref="Release"/> computes a watermark - the smallest
/// "latest timestamp seen" over members that delivered within the <c>hold</c> window - and
/// releases every queue head whose key is at or below it, or that has been pending for longer
/// than <c>hold</c> of wall-clock time (so a slow or lagging member can delay, never block,
/// the others). Release order is (timestamp, member index, arrival). A line released with a
/// timestamp older than one already released is reported by <see cref="IsLate"/>.
/// </para>
/// <para>
/// Cost: <see cref="Add"/> is O(lines); <see cref="Release"/> is O(members + released lines
/// x log members); queues never shift on dequeue. <b>Not thread-safe</b>: the caller must hold
/// its own lock around every call (AppState does).
/// </para>
/// </summary>
public sealed class StreamMerger
{
    public static readonly TimeSpan DefaultHold = TimeSpan.FromSeconds(5);

    private const int MaxLateLines = 10_000;

    private readonly Member[] _members;
    private readonly Dictionary<string, int> _indexById;
    private readonly long _holdTicks;
    private readonly Func<DateTimeOffset> _now;
    private readonly int[] _heap;

    // Reference-equality set of released late lines, trimmed to the most recent MaxLateLines.
    private readonly HashSet<Line> _late = new(ReferenceEqualityComparer.Instance);
    private readonly Queue<Line> _lateOrder = new();

    private long _seq;
    private long _lastReleasedTicks;
    private bool _hasReleased;

    public StreamMerger(IReadOnlyList<string> memberIds, TimeSpan hold, Func<DateTimeOffset> now)
    {
        ArgumentNullException.ThrowIfNull(memberIds);
        ArgumentNullException.ThrowIfNull(now);
        _holdTicks = hold.Ticks;
        _now = now;
        _indexById = new Dictionary<string, int>(memberIds.Count, StringComparer.Ordinal);
        var members = new List<Member>(memberIds.Count);
        foreach (var id in memberIds)
        {
            if (_indexById.TryAdd(id, members.Count))
            {
                members.Add(new Member());
            }
        }
        _members = [.. members];
        _heap = new int[_members.Length];
    }

    /// <summary>
    /// Timestamp of the newest line released so far (UTC offset), or <c>null</c> before the
    /// first release.
    /// </summary>
    public DateTimeOffset? LastReleased =>
        _hasReleased ? new DateTimeOffset(_lastReleasedTicks, TimeSpan.Zero) : null;

    /// <summary>Queues lines from one member in arrival order. Unknown member ids are ignored.</summary>
    public void Add(string memberId, IReadOnlyList<Line> lines)
    {
        if (!_indexById.TryGetValue(memberId, out var index) || lines.Count == 0)
        {
            return;
        }
        var member = _members[index];
        var nowTicks = _now().UtcTicks;
        member.Reserve(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var key =
                line.Timestamp is { } ts ? ts.UtcTicks
                : member.HasKey ? member.LastKey
                : nowTicks;
            member.Push(new Entry(line, key, _seq++, nowTicks));
        }
        member.LastArrival = nowTicks;
    }

    /// <summary>Returns lines that are ready, in release order (may be empty). Never returns a line twice.</summary>
    public IReadOnlyList<Line> Release()
    {
        var nowTicks = _now().UtcTicks;

        var watermark = long.MaxValue;
        var pending = false;
        for (var i = 0; i < _members.Length; i++)
        {
            var m = _members[i];
            if (m.Count > 0)
            {
                pending = true;
            }
            if (m.HasKey && nowTicks - m.LastArrival <= _holdTicks && m.LatestSeen < watermark)
            {
                watermark = m.LatestSeen;
            }
        }
        if (!pending)
        {
            return [];
        }

        var result = new List<Line>();
        var heapSize = 0;
        for (var i = 0; i < _members.Length; i++)
        {
            if (IsReady(_members[i], watermark, nowTicks))
            {
                heapSize = HeapPush(heapSize, i);
            }
        }

        while (heapSize > 0)
        {
            var mi = _heap[0];
            var m = _members[mi];
            var entry = m.Pop();
            result.Add(entry.Line);
            MarkReleased(entry, markLate: true);

            if (IsReady(m, watermark, nowTicks))
            {
                HeapSiftDown(heapSize, 0); // same member, new head
            }
            else
            {
                heapSize = HeapPopRoot(heapSize);
            }
        }
        return result;
    }

    /// <summary>
    /// Releases everything pending in timestamp order (used for the initial merge and rebuilds).
    /// Lines are never flagged late here.
    /// </summary>
    public IReadOnlyList<Line> FlushAll()
    {
        var total = 0;
        for (var i = 0; i < _members.Length; i++)
        {
            total += _members[i].Count;
        }
        if (total == 0)
        {
            return [];
        }

        var heapSize = 0;
        for (var i = 0; i < _members.Length; i++)
        {
            var m = _members[i];
            if (m.Count == 0)
            {
                continue;
            }
            m.SortIfNeeded();
            heapSize = HeapPush(heapSize, i);
        }

        var result = new List<Line>(total);
        while (heapSize > 0)
        {
            var m = _members[_heap[0]];
            var entry = m.Pop();
            result.Add(entry.Line);
            MarkReleased(entry, markLate: false);

            if (m.Count > 0)
            {
                HeapSiftDown(heapSize, 0);
            }
            else
            {
                heapSize = HeapPopRoot(heapSize);
            }
        }
        return result;
    }

    /// <summary>Forgets pending lines and all state (used before a rebuild).</summary>
    public void Reset()
    {
        foreach (var m in _members)
        {
            m.Clear();
        }
        _late.Clear();
        _lateOrder.Clear();
        _seq = 0;
        _hasReleased = false;
        _lastReleasedTicks = 0;
    }

    /// <summary>
    /// True for lines released (by <see cref="Release"/>) with a timestamp older than the last
    /// released timestamp at release time. Remembers the most recent 10,000 late lines.
    /// </summary>
    public bool IsLate(Line line) => _late.Contains(line);

    private bool IsReady(Member m, long watermark, long nowTicks)
    {
        if (m.Count == 0)
        {
            return false;
        }
        ref readonly var head = ref m.Head;
        return head.Key <= watermark || nowTicks - head.Arrival > _holdTicks;
    }

    private void MarkReleased(in Entry entry, bool markLate)
    {
        if (_hasReleased && entry.Key < _lastReleasedTicks)
        {
            if (markLate && _late.Add(entry.Line))
            {
                _lateOrder.Enqueue(entry.Line);
                if (_lateOrder.Count > MaxLateLines)
                {
                    _late.Remove(_lateOrder.Dequeue());
                }
            }
        }
        else
        {
            _lastReleasedTicks = entry.Key;
            _hasReleased = true;
        }
    }

    // ---- tiny binary min-heap of member indices, ordered by (head key, member index) ----

    private bool HeapLess(int a, int b)
    {
        var ka = _members[a].Head.Key;
        var kb = _members[b].Head.Key;
        return ka < kb || (ka == kb && a < b);
    }

    private int HeapPush(int size, int member)
    {
        var i = size;
        _heap[i] = member;
        while (i > 0)
        {
            var parent = (i - 1) >> 1;
            if (!HeapLess(_heap[i], _heap[parent]))
            {
                break;
            }
            (_heap[i], _heap[parent]) = (_heap[parent], _heap[i]);
            i = parent;
        }
        return size + 1;
    }

    private int HeapPopRoot(int size)
    {
        size--;
        if (size > 0)
        {
            _heap[0] = _heap[size];
            HeapSiftDown(size, 0);
        }
        return size;
    }

    private void HeapSiftDown(int size, int i)
    {
        while (true)
        {
            var left = (i * 2) + 1;
            if (left >= size)
            {
                return;
            }
            var smallest = left;
            var right = left + 1;
            if (right < size && HeapLess(_heap[right], _heap[left]))
            {
                smallest = right;
            }
            if (!HeapLess(_heap[smallest], _heap[i]))
            {
                return;
            }
            (_heap[i], _heap[smallest]) = (_heap[smallest], _heap[i]);
            i = smallest;
        }
    }

    private readonly struct Entry(Line line, long key, long seq, long arrival)
    {
        public readonly Line Line = line;
        public readonly long Key = key;
        public readonly long Seq = seq;
        public readonly long Arrival = arrival;
    }

    private sealed class EntryComparer : IComparer<Entry>
    {
        public static readonly EntryComparer Instance = new();

        public int Compare(Entry x, Entry y)
        {
            var c = x.Key.CompareTo(y.Key);
            return c != 0 ? c : x.Seq.CompareTo(y.Seq);
        }
    }

    /// <summary>One member's FIFO queue (array with head/tail indices, no shifting on dequeue) and clocks.</summary>
    private sealed class Member
    {
        private const int ShrinkAbove = 1 << 16;

        private Entry[] _items = [];
        private int _head;
        private int _tail;

        public int Count => _tail - _head;

        public ref readonly Entry Head => ref _items[_head];

        /// <summary>Key of the most recently queued line; valid when <see cref="HasKey"/>.</summary>
        public long LastKey;

        public long LatestSeen;

        public long LastArrival;

        public bool HasKey;

        public void Reserve(int extra)
        {
            if (_tail + extra <= _items.Length)
            {
                return;
            }
            var count = Count;
            if (count + extra <= _items.Length && _head >= _items.Length / 2)
            {
                Array.Copy(_items, _head, _items, 0, count);
                Array.Clear(_items, count, _tail - count);
            }
            else
            {
                var grown = new Entry[Math.Max(Math.Max(16, _items.Length * 2), count + extra)];
                Array.Copy(_items, _head, grown, 0, count);
                _items = grown;
            }
            _head = 0;
            _tail = count;
        }

        public void Push(in Entry entry)
        {
            _items[_tail++] = entry;
            LastKey = entry.Key;
            if (!HasKey || entry.Key > LatestSeen)
            {
                LatestSeen = entry.Key;
            }
            HasKey = true;
        }

        public Entry Pop()
        {
            var entry = _items[_head];
            _items[_head] = default; // drop the Line reference
            _head++;
            if (_head == _tail)
            {
                _head = _tail = 0;
                if (_items.Length > ShrinkAbove)
                {
                    _items = [];
                }
            }
            return entry;
        }

        /// <summary>Stable-sorts the queue by key, but only when it is not already ordered.</summary>
        public void SortIfNeeded()
        {
            for (var i = _head + 1; i < _tail; i++)
            {
                if (_items[i].Key < _items[i - 1].Key)
                {
                    // Seq is unique, so ordering by (key, seq) is a stable sort by key.
                    Array.Sort(_items, _head, _tail - _head, EntryComparer.Instance);
                    return;
                }
            }
        }

        public void Clear()
        {
            _items = [];
            _head = _tail = 0;
            LastKey = LatestSeen = LastArrival = 0;
            HasKey = false;
        }
    }
}
