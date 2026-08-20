using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reactive.Concurrency;
using HexTailSharp.Application;
using HexTailSharp.Domain;
using HexTailSharp.Tailing;
using HexTailSharp.Tests.Support;
using HexTailSharp.ViewModels;
using ReactiveUI;
using ReactiveUI.Reactive;
using ReactiveUI.Reactive.Builder;

namespace HexTailSharp.Tests.ViewModels;

public sealed class WorkspaceViewModelTests
{
    [Fact]
    public async Task SetShowContext_PersistsOnce()
    {
        RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();
        var persistence = new TestPersistence();
        var state = new AppState(new LogSourceService(), persistence);
        await using var viewModel = new MainWindowViewModel(
            state,
            scheduler: ImmediateScheduler.Instance
        );
        var path = Path.GetTempFileName();
        try
        {
            await state.OpenFileAsync(path);
            var file = Assert.Single(viewModel.Files);
            var savesBeforeToggle = persistence.SaveCount;

            await viewModel.SetShowContextAsync(file, true);

            Assert.Equal(savesBeforeToggle + 1, persistence.SaveCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

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

    [Fact]
    public void SyncCollection_CappedRolloverRemovesHeadAndAppendsTailWithoutReset()
    {
        var buffer = new FileBuffer(maxLines: 3);
        buffer.Append([new Line("one"), new Line("two"), new Line("three")]);
        var rows = new ObservableCollection<Line>(buffer.Lines);
        var changes = new List<NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, args) => changes.Add(args.Action);

        buffer.Append([new Line("four"), new Line("five")]);
        LogViewViewModel.SyncCollection(rows, buffer.Lines);

        Assert.Equal(buffer.Lines, rows);
        Assert.Equal(
            [
                NotifyCollectionChangedAction.Remove,
                NotifyCollectionChangedAction.Remove,
                NotifyCollectionChangedAction.Add,
                NotifyCollectionChangedAction.Add,
            ],
            changes
        );
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
    }
}
