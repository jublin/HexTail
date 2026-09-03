using HexTail.Domain;
using HexTail.Persistence;
using HexTail.Tailing;

namespace HexTail.Application;

public sealed class FileTabState : IAsyncDisposable
{
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
        Buffer.Changed += OnBufferChanged;
    }

    public LogSourceDescriptor Source { get; }
    public string Id => Source.Id;
    public string Path => Source.LocalPath ?? string.Empty;
    public FileBuffer Buffer { get; }
    public ILogParser Parser { get; }
    public List<Search> Searches { get; } = [];
    public bool FollowAll { get; set; } = true;
    public List<bool> FollowSearches { get; } = [];
    public bool ShowContext { get; set; }
    public int? SelectedLine { get; set; }
    public int? ExpandedLine { get; set; }
    public int ContextAbove { get; set; } = 3;
    public int ContextBelow { get; set; } = 10;
    public string ElasticFrom { get; set; } = "now-5m";
    public string ElasticTo { get; set; } = "now";
    public string? Error { get; internal set; }
    public ILogTailer Tailer { get; }
    public string DisplayName => Source.DisplayName;

    public IReadOnlyList<Line> ContextLines =>
        SelectedLine is int selected && selected >= 0 && selected < Buffer.Count
            ? Buffer.GetContextWindow(selected, ContextAbove, ContextBelow)
            : [];

    public void AddSearch(Search search)
    {
        Searches.Add(search);
        FollowSearches.Add(true);
        Buffer.AddSearch(search);
    }

    public bool RemoveSearch(Search search)
    {
        if (search.IsGlobalLabel)
            return false;
        var index = Searches.IndexOf(search);
        if (index < 0)
            return false;

        Searches.RemoveAt(index);
        FollowSearches.RemoveAt(index);
        return Buffer.RemoveSearch(search);
    }

    public void SyncGlobalLabelSearches(IEnumerable<GlobalLabel> labels)
    {
        for (var index = Searches.Count - 1; index >= 0; index--)
        {
            if (!Searches[index].IsGlobalLabel)
                continue;
            Buffer.RemoveSearch(Searches[index]);
            Searches.RemoveAt(index);
            FollowSearches.RemoveAt(index);
        }

        foreach (var label in labels.Where(label => label.ShowInOpenFile))
            try
            {
                AddSearch(
                    new Search(
                        new CompiledQuery(label.Text, CompiledQuery.DetectMode(label.Text), false),
                        label.Color,
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
        if (change.Cleared || SelectedLine is null)
            SelectedLine = null;
        else if (change.RolledOutCount > 0)
        {
            SelectedLine -= change.RolledOutCount;
            if (SelectedLine < 0)
                SelectedLine = null;
        }

        if (change.Cleared || ExpandedLine is null)
            ExpandedLine = null;
        else if (change.RolledOutCount > 0)
        {
            ExpandedLine -= change.RolledOutCount;
            if (ExpandedLine < 0)
                ExpandedLine = null;
        }
    }
}
