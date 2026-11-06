using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.Application;
using HexTail.Elastic;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.Tests.Support;
using HexTail.ViewModels;

namespace HexTail.Tests.Ui;

public sealed class ElasticSetupTests
{
    [AvaloniaFact]
    public async Task RefreshingBoundChoicesInTheSetupFlowPreservesMappingsAndSaveTracksPrerequisites()
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
            DataViewFields = [new("@timestamp", "date", true), new("message", "text", true)],
        };
        client.DataViewHandler = id =>
            Task.FromResult(
                new ElasticDataView(
                    id,
                    id == "logs" ? "renamed-*" : "new-*",
                    "@timestamp",
                    client.DataViewFields
                )
            );
        var settings = new AppSettings { ElasticConnections = [connection] };
        var persistence = new TestPersistence();
        await persistence.SaveAsync(new AppConfig { Settings = settings });
        var state = new AppState(
            new LogSourceService(),
            persistence,
            settings,
            elastic: client,
            credentials: new InMemoryCredentialVault()
        );
        await using var owner = new MainWindowViewModel(state, startPolling: false);
        await owner.InitializeAsync();
        var window = new MainWindow(owner, registerNativePicker: false);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var setup = owner.ElasticSetup;
        setup.EditView("connection", "view");
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var editor = Assert.Single(owner.Settings.ElasticConnections);
        Assert.Same(editor, setup.Connection);
        var view = Assert.Single(editor.Views);
        Assert.Same(view, setup.View);
        Assert.Equal(["message"], view.ToSettings().OutputFields);
        var selectedChoice = editor.DataViews[0];

        await editor.TestConnectionCommand.Execute().FirstAsync();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Assert.Equal("logs", view.SelectedDataViewId);
        Assert.Equal("renamed-*", view.DataViewTitle);
        Assert.Same(selectedChoice, editor.DataViews[0]);
        Assert.Equal(["message"], view.ToSettings().OutputFields);
        Assert.StartsWith("Timestamp field: @timestamp", view.TimestampStatus);
        Assert.True(view.CanSave);
        view.TimeFieldName = null;
        Dispatcher.UIThread.RunJobs();
        Assert.False(view.CanSave);
        Assert.False(string.IsNullOrWhiteSpace(view.TimestampError));
        await setup.GoToStepCommand.Execute(setup.Steps[1]).FirstAsync();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.Contains(
            window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "renamed-*" && text.IsEffectivelyVisible
        );
        await owner.DisposeAsync();
        Dispatcher.UIThread.RunJobs();
        window.Close();
    }
}
