using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using HexTail.Application;
using HexTail.Tests.Support;

namespace HexTail.Tests.Ui;

public sealed class StreamTabDisplayTests
{
    [AvaloniaFact]
    public async Task StreamTab_IsSelectableAndItsMergedRowsDisplayWithTheOrdinaryLogView()
    {
        var first = Path.GetTempFileName();
        var second = Path.GetTempFileName();
        await File.WriteAllLinesAsync(
            first,
            ["2026-01-01 10:00:01 INFO a1", "2026-01-01 10:00:03 INFO a3"],
            TestContext.Current.CancellationToken
        );
        await File.WriteAllLinesAsync(
            second,
            ["2026-01-01 10:00:02 INFO b2", "2026-01-01 10:00:04 INFO b4"],
            TestContext.Current.CancellationToken
        );
        try
        {
            var window = TestWindow.CreatePolling(out var viewModel);
            window.Show();
            await viewModel.OpenPathsCommand.Execute([first, second]);
            await UiWait.Until(() =>
            {
                viewModel.State.DrainTailerEvents();
                return viewModel.State.Files.All(file => file.Buffer.Count == 2);
            });
            var members = viewModel.State.Files.ToArray();
            var made = await viewModel.State.CreateStreamAsync(
                "Both",
                members,
                TestContext.Current.CancellationToken
            );

            var tab = await viewModel.State.OpenStreamAsync(
                made.Id,
                TestContext.Current.CancellationToken
            );

            await UiWait.Until(() => ReferenceEquals(viewModel.SelectedFile?.Model, tab));
            var view = viewModel.SelectedFile!.SelectedView;
            await UiWait.Until(() =>
            {
                view.Sync();
                return view.Lines.Count == 4;
            });
            Assert.Equal(["a1", "b2", "a3", "b4"], view.Lines.Select(line => line.Line.Raw[^2..]));
            // The tab strip lists plain files only; the stream tab is reachable through selection.
            Assert.DoesNotContain(viewModel.SelectedFile, viewModel.FileTabs);
            Assert.Equal(2, viewModel.FileTabs.Count);
            Assert.Contains(viewModel.SelectedFile, viewModel.Files);
            Assert.Equal(LogSourceKind.Stream, viewModel.SelectedFile.Model.Source.Kind);
            window.Close();
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }
}
