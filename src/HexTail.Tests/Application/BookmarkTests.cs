using HexTail.Application;
using HexTail.Domain;
using HexTail.Tailing;

namespace HexTail.Tests.Application;

public sealed class BookmarkTests
{
    private sealed class NullTailer : ILogTailer
    {
        public string SourceId => "t";
        public string DisplayName => "t";
        public Task Completion => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static FileTabState Tab(int maxLines) =>
        new("t", "t.log", new FileBuffer(maxLines), new PlainTextParser(), new NullTailer());

    [Fact]
    public void ToggleAddsAndRemovesAndBumpsTheVersion()
    {
        var tab = Tab(10);
        tab.Buffer.Append([new Line("a"), new Line("b"), new Line("c")]);
        var version = tab.BookmarksVersion;

        tab.ToggleBookmarkAt(2);
        Assert.Equal([2], tab.Bookmarks);
        Assert.True(tab.IsBookmarked(tab.Buffer[2]));
        Assert.False(tab.IsBookmarked(tab.Buffer[0]));
        Assert.True(tab.BookmarksVersion > version);

        tab.ToggleBookmarkAt(2);
        Assert.Empty(tab.Bookmarks);
    }

    [Fact]
    public void BookmarksShiftWhenOldLinesRollOutAndDropWhenTheirLineDoes()
    {
        var tab = Tab(3);
        tab.Buffer.Append([new Line("a"), new Line("b"), new Line("c")]);
        tab.ToggleBookmarkAt(0);
        tab.ToggleBookmarkAt(2);

        tab.Buffer.Append([new Line("d"), new Line("e")]); // "a" and "b" roll out

        Assert.Equal([0], tab.Bookmarks); // "c" moved from index 2 to 0; the bookmark on "a" is gone
        Assert.True(tab.IsBookmarked(tab.Buffer[0]));
        Assert.Equal("c", tab.Buffer[0].Raw);
    }

    [Fact]
    public void ClearingTheBufferClearsTheBookmarks()
    {
        var tab = Tab(10);
        tab.Buffer.Append([new Line("a")]);
        tab.ToggleBookmarkAt(0);
        var version = tab.BookmarksVersion;

        tab.Buffer.Clear();

        Assert.Empty(tab.Bookmarks);
        Assert.True(tab.BookmarksVersion > version);
    }
}
