using System.Collections.Specialized;
using System.Reactive.Concurrency;
using Avalonia.Collections;
using HexTail.Application;
using HexTail.Domain;
using HexTail.Tailing;
using HexTail.Tests.Support;
using HexTail.ViewModels;
using ReactiveUI;
using ReactiveUI.Reactive;
using ReactiveUI.Reactive.Builder;

namespace HexTail.Tests.ViewModels;

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
        var rows = new AvaloniaList<Line>();
        var original = rows;

        LogViewViewModel.SyncCollection(rows, [new Line("one"), new Line("two")]);

        Assert.Same(original, rows);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void SyncCollection_UnchangedTailDoesNotRaiseReset()
    {
        var line = new Line("one");
        var rows = new AvaloniaList<Line> { line };
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
        var rows = new AvaloniaList<Line> { first };

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
        var rows = new AvaloniaList<Line> { old };

        LogViewViewModel.SyncCollection(rows, [replacement], resetItems: true);

        Assert.Single(rows);
        Assert.Same(replacement, rows[0]);
    }

    [Fact]
    public void SyncCollection_CappedRolloverRemovesHeadAndAppendsTailWithoutReset()
    {
        var buffer = new FileBuffer(maxLines: 3);
        buffer.Append([new Line("one"), new Line("two"), new Line("three")]);
        var rows = new AvaloniaList<Line>(buffer.Lines);
        var changes = new List<NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, args) => changes.Add(args.Action);

        buffer.Append([new Line("four"), new Line("five")]);
        LogViewViewModel.SyncCollection(rows, buffer.Lines);

        Assert.Equal(buffer.Lines, rows);
        Assert.Equal(
            [NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Add],
            changes
        );
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
    }

    [Fact]
    public async Task LogRowsAppendAsOneCollectionChange()
    {
        RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();
        var path = Path.GetTempFileName();
        try
        {
            await using var viewModel = new MainWindowViewModel(
                new AppState(new LogSourceService(), new TestPersistence()),
                scheduler: ImmediateScheduler.Instance,
                startPolling: false
            );
            var tab = await viewModel.State.OpenFileAsync(path, save: false);
            var file = new FileTabViewModel(viewModel, tab);
            var changes = 0;
            file.Views[0].Lines.CollectionChanged += (_, _) => changes++;

            file.Model.Buffer.Append(
                Enumerable.Range(0, 10_000).Select(index => new Line($"line {index}"))
            );
            file.SyncViews();

            Assert.Equal(10_000, file.Views[0].Lines.Count);
            Assert.Equal(1, changes);

            var firstRow = file.Views[0].Lines[0];
            var refreshes = 0;
            firstRow.PropertyChanged += (_, _) => refreshes++;
            file.SyncViews();

            Assert.Equal(0, refreshes);

            changes = 0;
            file.Model.Buffer.Append(
                Enumerable.Range(10_000, 1_000).Select(index => new Line($"line {index}"))
            );
            file.SyncViews();

            Assert.Equal(11_000, file.Views[0].Lines.Count);
            Assert.Equal(1, changes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AddingSearchPreservesTheAllViewRows()
    {
        RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();
        var path = Path.GetTempFileName();
        try
        {
            await using var viewModel = new MainWindowViewModel(
                new AppState(new LogSourceService(), new TestPersistence()),
                scheduler: ImmediateScheduler.Instance,
                startPolling: false
            );
            var tab = await viewModel.State.OpenFileAsync(path, save: false);
            var file = new FileTabViewModel(viewModel, tab);
            file.Model.Buffer.Append(
                Enumerable.Range(0, 10_000).Select(index => new Line($"line {index}"))
            );
            file.SyncViews();
            var allView = file.Views[0];
            var firstRow = allView.Lines[0];
            var refreshes = 0;
            firstRow.PropertyChanged += (_, _) => refreshes++;

            viewModel.State.AddSearch(
                file.Model,
                "line",
                MatchMode.Literal,
                caseSensitive: false,
                "#00ff00"
            );
            file.SyncViews();

            Assert.Same(allView, file.Views[0]);
            Assert.Same(firstRow, file.Views[0].Lines[0]);
            Assert.Equal(0, refreshes);
            Assert.Empty(file.Views[1].Lines);

            file.SelectedViewIndex = 1;
            file.SyncViews();
            Assert.Equal(10_000, file.Views[1].Lines.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task InactiveFileSync_DoesNotNotifyOrMaterializeRows()
    {
        RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();
        var firstPath = Path.GetTempFileName();
        var secondPath = Path.GetTempFileName();
        try
        {
            await using var owner = new MainWindowViewModel(
                new AppState(new LogSourceService(), new TestPersistence()),
                scheduler: ImmediateScheduler.Instance,
                startPolling: false
            );
            var firstTab = await owner.State.OpenFileAsync(firstPath, save: false);
            await owner.State.OpenFileAsync(secondPath, save: false);
            var first = new FileTabViewModel(owner, firstTab);
            var propertyChanges = 0;
            var viewPropertyChanges = 0;
            first.PropertyChanged += (_, _) => propertyChanges++;
            first.Views[0].PropertyChanged += (_, _) => viewPropertyChanges++;

            firstTab.Buffer.Append(new Line("new line"));
            first.SyncViews(loadRows: false);

            Assert.Equal(0, propertyChanges);
            Assert.Equal(0, viewPropertyChanges);
            Assert.Empty(first.Views[0].Lines);
        }
        finally
        {
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }
}
