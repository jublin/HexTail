using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.Application;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.Tests.Support;
using HexTail.ViewModels;

namespace HexTail.Tests.Ui;

public sealed class ElasticSetupTests
{
    [AvaloniaFact]
    public async Task RefreshingBoundChoicesPreservesMappingsAndSaveTracksPrerequisites()
    {
        var connection = new ElasticConnectionSettings
        {
            Id = "connection",
            Name = "Ops",
            KibanaUrl = "https://kibana/",
            ElasticsearchUrl = "https://elastic/",
            Views =
            [
                new ElasticViewSettings
                {
                    Id = "view",
                    Name = "Logs",
                    DataViewId = "logs",
                    DataViewTitle = "logs-*",
                    TimeFieldName = "@timestamp",
                    ServerField = "server",
                    OutputFields = ["message"],
                    Sources = [new ElasticSourceSettings { Id = "source", ServerValue = "api" }],
                },
            ],
        };
        var client = new FakeElasticApiClient
        {
            DataViews = [new("logs", "renamed-*"), new("new", "new-*")],
        };
        var settings = new AppSettings { ElasticConnections = [connection] };
        var persistence = new TestPersistence();
        await persistence.SaveAsync(new AppConfig { Settings = settings });
        var state = new AppState(new LogSourceService(), persistence, settings, elastic: client);
        await using var owner = new MainWindowViewModel(state, startPolling: false);
        await owner.InitializeAsync();
        owner.Settings.SectionIndex = 2;
        owner.SettingsOpen = true;
        var window = new MainWindow(owner, registerNativePicker: false);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var editor = Assert.Single(owner.Settings.ElasticConnections);
        var view = Assert.Single(editor.Views);
        Assert.Equal(["message"], view.ToSettings().OutputFields);
        var selectedChoice = editor.DataViews[0];

        await editor.TestConnectionCommand.Execute().FirstAsync();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Assert.Equal("logs", view.SelectedDataViewId);
        Assert.Equal("renamed-*", view.DataViewTitle);
        Assert.Same(selectedChoice, editor.DataViews[0]);
        Assert.Equal(["message"], view.ToSettings().OutputFields);
        Assert.Contains(
            window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "renamed-*"
        );
        Assert.Contains(
            window.GetVisualDescendants().OfType<TextBlock>(),
            text =>
                text.Text?.StartsWith("Timestamp field: @timestamp", StringComparison.Ordinal)
                    is true
        );
        var save = Assert.Single(
            window.GetVisualDescendants().OfType<Button>(),
            button => ReferenceEquals(button.Command, editor.SaveCommand)
        );
        Assert.True(save.IsEnabled);
        view.TimeFieldName = null;
        Dispatcher.UIThread.RunJobs();
        Assert.False(save.IsEnabled);
        Assert.Contains(
            window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == view.TimestampError && !string.IsNullOrWhiteSpace(text.Text)
        );
        await owner.DisposeAsync();
        Dispatcher.UIThread.RunJobs();
        window.Close();
    }
}
