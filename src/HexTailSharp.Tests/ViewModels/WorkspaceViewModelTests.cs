using System.Collections.ObjectModel;
using HexTailSharp.Domain;
using HexTailSharp.ViewModels;

namespace HexTailSharp.Tests.ViewModels;

public sealed class WorkspaceViewModelTests
{
    [Fact]
    public void SyncCollection_AppendRetainsCollectionIdentity()
    {
        var rows = new ObservableCollection<Line>();
        var original = rows;

        LogViewViewModel.SyncCollection(rows, [new Line("one"), new Line("two")]);

        Assert.Same(original, rows);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void SyncCollection_UnchangedTailDoesNotRaiseReset()
    {
        var line = new Line("one");
        var rows = new ObservableCollection<Line> { line };
        var changes = 0;
        rows.CollectionChanged += (_, _) => changes++;

        LogViewViewModel.SyncCollection(rows, [line]);

        Assert.Equal(0, changes);
    }

    [Fact]
    public void SyncCollection_AppendKeepsExistingRowsAndCollection()
    {
        var first = new Line("first");
        var second = new Line("second");
        var rows = new ObservableCollection<Line> { first };

        LogViewViewModel.SyncCollection(rows, [first, second]);

        Assert.Equal(2, rows.Count);
        Assert.Same(first, rows[0]);
        Assert.Same(second, rows[1]);
    }

    [Fact]
    public void SyncCollection_ResetReplacesRows()
    {
        var old = new Line("old");
        var replacement = new Line("replacement");
        var rows = new ObservableCollection<Line> { old };

        LogViewViewModel.SyncCollection(rows, [replacement], resetItems: true);

        Assert.Single(rows);
        Assert.Same(replacement, rows[0]);
    }
}
