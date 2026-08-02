using HexTailSharp.Domain;
using HexTailSharp.Tailing;

namespace HexTailSharp.Application;

public sealed class FileTabState : IAsyncDisposable
{
    internal FileTabState(string id, string path, FileBuffer buffer, ILogParser parser, IFileTailer tailer)
    {
        Id = id;
        Path = path;
        Buffer = buffer;
        Parser = parser;
        Tailer = tailer;
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
    public int ContextAbove { get; set; } = 3;
    public int ContextBelow { get; set; } = 10;
    public string? Error { get; internal set; }
    public IFileTailer Tailer { get; }
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

    public async ValueTask DisposeAsync() => await Tailer.DisposeAsync().ConfigureAwait(false);
}
