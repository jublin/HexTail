using HexTailSharp.Domain;

namespace HexTailSharp.Tests.Domain;

public class FileBufferTests
{
    private static Line L(string text) => new(text);

    [Fact]
    public void Append_AddsLinesInOrder()
    {
        var buffer = new FileBuffer();

        buffer.Append(L("one"));
        buffer.Append([L("two"), L("three")]);

        Assert.Equal(3, buffer.Count);
        Assert.Equal("one", buffer[0].Raw);
        Assert.Equal("two", buffer[1].Raw);
        Assert.Equal("three", buffer[2].Raw);
    }

    [Fact]
    public void DefaultMaxLines_Is100000()
    {
        Assert.Equal(100_000, new FileBuffer().MaxLines);
    }

    [Fact]
    public void Append_ExceedingCap_RollsOutOldestLines()
    {
        var buffer = new FileBuffer(maxLines: 3);

        buffer.Append([L("a"), L("b"), L("c")]);
        buffer.Append([L("d"), L("e")]);

        Assert.Equal(3, buffer.Count);
        Assert.Equal(["c", "d", "e"], buffer.Lines.Select(l => l.Raw));
    }

    [Fact]
    public void Append_BatchLargerThanCap_KeepsNewestLines()
    {
        var buffer = new FileBuffer(maxLines: 2);

        buffer.Append([L("a"), L("b"), L("c"), L("d")]);

        Assert.Equal(2, buffer.Count);
        Assert.Equal(["c", "d"], buffer.Lines.Select(l => l.Raw));
    }

    [Fact]
    public void Rollover_RebasesSearchResults_AndDropsRolledOutIndices()
    {
        var buffer = new FileBuffer(maxLines: 4);
        buffer.Append([L("error a"), L("b"), L("error c"), L("d")]);
        var search = new Search(new CompiledQuery("error", MatchMode.Literal, caseSensitive: true), "red", buffer);
        buffer.AddSearch(search);
        Assert.Equal([0, 2], search.Results);

        buffer.Append([L("e"), L("error f")]); // rolls out indices 0 and 1

        // Old 0 dropped, old 2 -> 0, new match at 3.
        Assert.Equal([0, 3], search.Results);
        Assert.Equal("error c", buffer[0].Raw);
    }

    [Fact]
    public void Rollover_DropsAllResults_WhenAllMatchesRollOut()
    {
        var buffer = new FileBuffer(maxLines: 2);
        buffer.Append([L("error a"), L("error b")]);
        var search = new Search(new CompiledQuery("error", MatchMode.Literal, caseSensitive: true), "red", buffer);
        buffer.AddSearch(search);

        buffer.Append([L("c"), L("d")]);

        Assert.Empty(search.Results);
    }

    [Fact]
    public void Clear_EmptiesBufferAndSearchResults()
    {
        var buffer = new FileBuffer();
        buffer.Append([L("error a"), L("b")]);
        var search = new Search(new CompiledQuery("error", MatchMode.Literal, caseSensitive: true), "red", buffer);
        buffer.AddSearch(search);

        buffer.Clear();

        Assert.Equal(0, buffer.Count);
        Assert.Empty(search.Results);
    }

    [Fact]
    public void Clear_ThenAppend_RescansFromZero()
    {
        var buffer = new FileBuffer();
        buffer.Append(L("error a"));
        var search = new Search(new CompiledQuery("error", MatchMode.Literal, caseSensitive: true), "red", buffer);
        buffer.AddSearch(search);

        buffer.Clear();
        buffer.Append([L("x"), L("error b")]);

        Assert.Equal([1], search.Results);
    }

    [Fact]
    public void Changed_FiresOnAppend_WithCounts()
    {
        var buffer = new FileBuffer(maxLines: 2);
        var changes = new List<BufferChange>();
        buffer.Changed += c => changes.Add(c);

        buffer.Append([L("a"), L("b"), L("c")]);

        var change = Assert.Single(changes);
        Assert.Equal(3, change.AppendedCount);
        Assert.Equal(1, change.RolledOutCount);
        Assert.False(change.Cleared);
    }

    [Fact]
    public void Changed_FiresOnClear()
    {
        var buffer = new FileBuffer();
        buffer.Append(L("a"));
        BufferChange? change = null;
        buffer.Changed += c => change = c;

        buffer.Clear();

        Assert.NotNull(change);
        Assert.True(change.Value.Cleared);
    }

    [Fact]
    public void GetContextWindow_ReturnsSurroundingLines()
    {
        var buffer = new FileBuffer();
        buffer.Append(Enumerable.Range(0, 10).Select(i => L($"line{i}")));

        var window = buffer.GetContextWindow(index: 5, above: 2, below: 3);

        Assert.Equal(["line3", "line4", "line5", "line6", "line7", "line8"], window.Select(l => l.Raw));
    }

    [Fact]
    public void GetContextWindow_ClampsAtBufferBounds()
    {
        var buffer = new FileBuffer();
        buffer.Append([L("a"), L("b"), L("c")]);

        var atStart = buffer.GetContextWindow(index: 0, above: 5, below: 1);
        var atEnd = buffer.GetContextWindow(index: 2, above: 1, below: 10);

        Assert.Equal(["a", "b"], atStart.Select(l => l.Raw));
        Assert.Equal(["b", "c"], atEnd.Select(l => l.Raw));
    }

    [Fact]
    public void GetContextWindow_InvalidIndex_Throws()
    {
        var buffer = new FileBuffer();
        buffer.Append(L("a"));

        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.GetContextWindow(1, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.GetContextWindow(-1, 1, 1));
    }

    [Fact]
    public void RemovedSearch_NoLongerScans()
    {
        var buffer = new FileBuffer();
        var search = new Search(new CompiledQuery("error", MatchMode.Literal, caseSensitive: true), "red", buffer);
        buffer.AddSearch(search);
        buffer.RemoveSearch(search);

        buffer.Append(L("error later"));

        Assert.Empty(search.Results);
    }
}
