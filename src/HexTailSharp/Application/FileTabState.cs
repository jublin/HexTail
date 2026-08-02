using HexTailSharp.Domain;
using HexTailSharp.Tailing;

namespace HexTailSharp.Application;

public sealed class FileTabState : IAsyncDisposable
{
    internal FileTabState(string id, string path, FileBuffer buffer, ILogParser parser, IFileTailer tailer, bool isSnapshot = false)
    {
        Id = id;
        Path = path;
        Buffer = buffer;
        Parser = parser;
        Tailer = tailer;
        IsSnapshot = isSnapshot;
        Buffer.Changed += OnBufferChanged;
    }

    public string Id { get; }
    public string Path { get; }
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
    public string? Error { get; internal set; }
    public IFileTailer Tailer { get; }
    public bool IsSnapshot { get; }
    public string DisplayName => System.IO.Path.GetFileName(Path) is { Length: > 0 } name ? name : Path;

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
        var index = Searches.IndexOf(search);
        if (index < 0)
            return false;

        Searches.RemoveAt(index);
        FollowSearches.RemoveAt(index);
        return Buffer.RemoveSearch(search);
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
