using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.Application;
using HexTail.Elastic;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.Tests.Support;
using HexTail.ViewModels;
using HexTail.Views;

namespace HexTail.Tests.Ui;

public sealed class ElasticSetupPageTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public required MainWindowViewModel Owner { get; init; }
        public required MainWindow Window { get; init; }
        public required FakeElasticApiClient Client { get; init; }
        public required TestPersistence Persistence { get; init; }

        public ElasticSetupViewModel Setup => Owner.ElasticSetup;
        public ElasticSetupPage Page =>
            Window.GetVisualDescendants().OfType<ElasticSetupPage>().Single();

        public T Find<T>(string name)
            where T : Control =>
            Page.FindControl<T>(name) ?? throw new Xunit.Sdk.XunitException(name);

        public void Pump()
        {
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
        }

        public async ValueTask DisposeAsync()
        {
            await Owner.DisposeAsync();
            Dispatcher.UIThread.RunJobs();
            Window.Close();
        }
    }

    private static async Task<Fixture> CreateAsync()
    {
        var client = new FakeElasticApiClient
        {
            DataViews = [new("logs", "logs-*"), new("metrics", "Metrics-*"), new("apm", "apm-*")],
            DataViewFields =
            [
                new("@timestamp", "date", true),
                new("message", "text", true),
                new("service", "keyword", true),
            ],
        };
        client.DataViewHandler = id =>
            Task.FromResult(
                new ElasticDataView(id, $"{id}-*", "@timestamp", client.DataViewFields)
            );
        var persistence = new TestPersistence();
        var owner = new MainWindowViewModel(
            new AppState(
                new LogSourceService(),
                persistence,
                credentials: new InMemoryCredentialVault(),
                elastic: client
            ),
            scheduler: ImmediateScheduler.Instance,
            startPolling: false
        );
        await owner.InitializeAsync();
        owner.ElasticSetup.AutoTestDelay = TimeSpan.FromMilliseconds(40);
        var window = new MainWindow(owner, registerNativePicker: false);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return new Fixture
        {
            Owner = owner,
            Window = window,
            Client = client,
            Persistence = persistence,
        };
    }

    private static async Task PassConnectionTestAsync(Fixture fixture)
    {
        var connection = fixture.Setup.Connection!;
        connection.KibanaUrl = "https://kibana.example/";
        connection.ElasticsearchUrl = "https://elastic.example/";
        await UiWait.Until(() => fixture.Setup.CanGoNext);
        fixture.Pump();
    }

    private static string Encode(string payload) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload));

    [AvaloniaFact]
    public async Task StartingTheFlowShowsThePageAndCloseOrEscapeHidesIt()
    {
        await using var fixture = await CreateAsync();
        var emptyStateButton = fixture
            .Window.GetVisualDescendants()
            .OfType<Button>()
            .First(button => button.Name == "OpenEmptyFileButton");
        Assert.True(emptyStateButton.IsEffectivelyVisible);
        Assert.False(fixture.Page.IsEffectivelyVisible);

        fixture.Setup.StartNew();
        fixture.Pump();

        Assert.True(fixture.Page.IsEffectivelyVisible);
        Assert.False(emptyStateButton.IsEffectivelyVisible);

        fixture.Find<Button>("SetupCloseButton").Command!.Execute(null);
        await UiWait.Until(() => !fixture.Setup.IsOpen);
        fixture.Pump();
        Assert.False(fixture.Page.IsEffectivelyVisible);
        Assert.True(emptyStateButton.IsEffectivelyVisible);

        fixture.Setup.StartNew();
        fixture.Pump();
        Assert.True(fixture.Page.IsEffectivelyVisible);
        Assert.True(fixture.Find<Button>("SetupCloseButton").Focus());
        fixture.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await UiWait.Until(() => !fixture.Setup.IsOpen);
        fixture.Pump();
        Assert.False(fixture.Page.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task ThePageReplacesAnOpenLogInsteadOfSittingOnTopOfIt()
    {
        var path = Path.GetTempFileName();
        try
        {
            await using var fixture = await CreateAsync();
            await fixture.Owner.OpenPathsCommand.Execute([path]);
            await UiWait.Until(() => fixture.Owner.HasFile);
            fixture.Pump();
            var logViews = fixture.Window.GetVisualDescendants().OfType<LogView>().ToArray();
            Assert.NotEmpty(logViews);
            Assert.Contains(logViews, view => view.IsEffectivelyVisible);

            fixture.Setup.StartNew();
            fixture.Pump();

            Assert.True(fixture.Page.IsEffectivelyVisible);
            Assert.All(logViews, view => Assert.False(view.IsEffectivelyVisible));

            fixture.Setup.Close();
            fixture.Pump();
            Assert.Contains(logViews, view => view.IsEffectivelyVisible);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task EscapeLeavesThePageAloneWhileAPopupOrThePaletteIsOpen()
    {
        await using var fixture = await CreateAsync();
        fixture.Setup.StartNew();
        fixture.Pump();

        var auth = fixture.Find<ComboBox>("AuthModeBox");
        Assert.True(auth.Focus());
        auth.IsDropDownOpen = true;
        fixture.Pump();
        fixture.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        fixture.Pump();
        Assert.True(fixture.Setup.IsOpen);
        Assert.False(auth.IsDropDownOpen);

        fixture.Owner.Palette.Open();
        fixture.Pump();
        fixture.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        fixture.Pump();
        Assert.True(fixture.Setup.IsOpen);
        Assert.False(fixture.Owner.Palette.IsOpen);

        // With nothing else open a plain Escape closes it.
        Assert.True(fixture.Find<Button>("SetupCloseButton").Focus());
        fixture.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await UiWait.Until(() => !fixture.Setup.IsOpen);
    }

    [AvaloniaFact]
    public async Task TheStepListShowsFiveStepsAndNextWaitsForAPassingTest()
    {
        await using var fixture = await CreateAsync();
        fixture.Setup.StartNew();
        fixture.Pump();

        var list = fixture.Find<ItemsControl>("SetupStepList");
        var buttons = list.GetVisualDescendants().OfType<Button>().ToArray();
        Assert.Equal(5, buttons.Length);
        Assert.True(fixture.Setup.Steps[0].IsCurrent);
        Assert.Equal(
            ["Connection", "Data view", "Filter", "Columns", "Name & open"],
            buttons
                .Select(button => button.DataContext)
                .Cast<ElasticSetupStepViewModel>()
                .Select(step => step.Title)
        );
        Assert.Contains("current", buttons[0].Classes);
        Assert.All(buttons.Skip(1), button => Assert.DoesNotContain("current", button.Classes));
        Assert.All(buttons.Skip(1), button => Assert.False(button.IsEffectivelyEnabled));

        var next = fixture.Find<Button>("SetupNextButton");
        var back = fixture.Find<Button>("SetupBackButton");
        Assert.False(next.IsEffectivelyEnabled);
        Assert.False(back.IsEffectivelyEnabled);

        fixture.Setup.AutoTestDelay = TimeSpan.FromMinutes(10);
        fixture.Setup.Connection!.KibanaUrl = "https://kibana.example/";
        fixture.Setup.Connection.ElasticsearchUrl = "https://elastic.example/";
        fixture.Pump();
        Assert.False(next.IsEffectivelyEnabled);
        await fixture.Setup.Connection.TestConnectionCommand.Execute().FirstAsync();
        fixture.Pump();

        Assert.True(next.IsEffectivelyEnabled);
        Assert.True(buttons[1].IsEffectivelyEnabled);
        Assert.Equal("Next", next.Content);

        next.Command!.Execute(null);
        await UiWait.Until(() => fixture.Setup.Step == ElasticSetupStep.DataView);
        fixture.Pump();
        Assert.True(buttons[0].Classes.Contains("done") || fixture.Setup.Steps[0].IsDone);
        Assert.True(back.IsEffectivelyEnabled);
        Assert.True(fixture.Find<TextBox>("DataViewSearchBox").IsEffectivelyVisible);
        Assert.False(fixture.Find<TextBox>("CloudIdBox").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task ACloudIdFillsTheUrlsAndAnInvalidOneShowsAnError()
    {
        await using var fixture = await CreateAsync();
        fixture.Setup.StartNew();
        fixture.Pump();
        var cloudId = fixture.Find<TextBox>("CloudIdBox");
        var kibana = fixture.Find<TextBox>("KibanaUrlBox");
        var elasticsearch = fixture.Find<TextBox>("ElasticsearchUrlBox");
        var error = fixture.Find<TextBlock>("CloudIdError");
        Assert.False(error.IsEffectivelyVisible);

        cloudId.Text = "not a cloud id";
        fixture.Pump();
        Assert.True(error.IsEffectivelyVisible);
        Assert.False(string.IsNullOrWhiteSpace(error.Text));
        Assert.Equal(string.Empty, kibana.Text);

        cloudId.Text = $"my-deployment:{Encode("us-east-1.aws.found.io$es123$kb456")}";
        fixture.Pump();

        Assert.Equal("https://kb456.us-east-1.aws.found.io", kibana.Text);
        Assert.Equal("https://es123.us-east-1.aws.found.io", elasticsearch.Text);
        Assert.False(error.IsEffectivelyVisible);
        Assert.Equal("https://kb456.us-east-1.aws.found.io", fixture.Setup.Connection!.KibanaUrl);
    }

    [AvaloniaFact]
    public async Task TheLoginFieldsFollowTheLoginMethod()
    {
        await using var fixture = await CreateAsync();
        fixture.Setup.StartNew();
        fixture.Pump();
        var username = fixture.Find<TextBox>("UsernameBox");
        var secret = fixture.Find<TextBox>("SecretBox");
        Assert.False(username.IsEffectivelyVisible);
        Assert.False(secret.IsEffectivelyVisible);
        Assert.Equal('●', secret.PasswordChar);

        fixture.Setup.Connection!.AuthMode = ElasticAuthMode.Basic;
        fixture.Pump();
        Assert.True(username.IsEffectivelyVisible);
        Assert.True(secret.IsEffectivelyVisible);

        fixture.Setup.Connection.AuthMode = ElasticAuthMode.ApiKey;
        fixture.Pump();
        Assert.False(username.IsEffectivelyVisible);
        Assert.True(secret.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task EndpointRowsReportEachPartAndEditingAUrlResetsThem()
    {
        await using var fixture = await CreateAsync();
        fixture.Setup.StartNew();
        fixture.Pump();
        var results = fixture.Find<ItemsControl>("EndpointResultsList");
        string[] Texts() =>
            results
                .GetVisualDescendants()
                .OfType<TextBlock>()
                .Select(text => text.Text ?? string.Empty)
                .Where(text => text.Length > 0)
                .ToArray();

        Assert.Equal(
            ["Kibana", "Not tested", "Elasticsearch", "Not tested", "Credentials", "Not tested"],
            Texts()
        );

        // Typing into the box runs the debounced automatic test.
        fixture.Find<TextBox>("KibanaUrlBox").Text = "https://kibana.example/";
        fixture.Find<TextBox>("ElasticsearchUrlBox").Text = "https://elastic.example/";
        await UiWait.Until(() => fixture.Setup.Connection!.EndpointsOk);
        fixture.Pump();

        var texts = Texts();
        Assert.Equal(["Kibana", "Elasticsearch", "Credentials"], texts.Where((_, i) => i % 2 == 0));
        Assert.Contains("✓ 3 data views", texts);
        Assert.Contains("✓ reachable", texts);
        Assert.Contains(texts, text => text.Contains("anonymous", StringComparison.Ordinal));
        Assert.True(fixture.Find<Button>("SetupNextButton").IsEffectivelyEnabled);

        fixture.Client.ElasticsearchError = new HttpRequestException("connection refused");
        fixture.Find<TextBox>("ElasticsearchUrlBox").Text = "https://elastic.example/other";
        fixture.Pump();
        Assert.Equal(
            ["Kibana", "Not tested", "Elasticsearch", "Not tested", "Credentials", "Not tested"],
            Texts()
        );
        Assert.False(fixture.Find<Button>("SetupNextButton").IsEffectivelyEnabled);

        await UiWait.Until(() => fixture.Setup.Connection!.EndpointResults[1].IsFailed);
        fixture.Pump();
        Assert.Contains(Texts(), text => text.StartsWith("✗", StringComparison.Ordinal));
        Assert.False(fixture.Find<Button>("SetupNextButton").IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public async Task TheTestButtonRunsTheConnectionTest()
    {
        await using var fixture = await CreateAsync();
        fixture.Setup.StartNew();
        fixture.Setup.AutoTestDelay = TimeSpan.FromMinutes(10);
        fixture.Pump();
        fixture.Find<TextBox>("KibanaUrlBox").Text = "https://kibana.example/";
        fixture.Find<TextBox>("ElasticsearchUrlBox").Text = "https://elastic.example/";
        fixture.Pump();
        Assert.Empty(fixture.Client.DataViewSecrets);

        var test = fixture.Find<Button>("TestConnectionButton");
        Assert.True(test.IsEffectivelyEnabled);
        test.Command!.Execute(null);

        await UiWait.Until(() => fixture.Setup.Connection!.EndpointsOk);
        Assert.Single(fixture.Client.DataViewSecrets);
    }

    [AvaloniaFact]
    public async Task TheDataViewStepFiltersSelectsAndLoadsBeforeNext()
    {
        await using var fixture = await CreateAsync();
        fixture.Setup.StartNew();
        fixture.Pump();
        await PassConnectionTestAsync(fixture);
        fixture.Find<Button>("SetupNextButton").Command!.Execute(null);
        await UiWait.Until(() => fixture.Setup.Step == ElasticSetupStep.DataView);
        fixture.Pump();

        var search = fixture.Find<TextBox>("DataViewSearchBox");
        var list = fixture.Find<ListBox>("DataViewList");
        var next = fixture.Find<Button>("SetupNextButton");
        Assert.Equal(3, list.ItemCount);
        Assert.False(next.IsEffectivelyEnabled);

        search.Text = "METRIC";
        fixture.Pump();
        Assert.Equal(1, list.ItemCount);
        search.Text = "-*";
        fixture.Pump();
        Assert.Equal(3, list.ItemCount);
        search.Text = "mEtRiCs";
        fixture.Pump();
        var choice = Assert.IsType<ElasticDataViewChoiceViewModel>(Assert.Single(list.Items));
        Assert.Equal("Metrics-*", choice.Title);

        list.SelectedItem = choice;
        fixture.Pump();
        await UiWait.Until(() => fixture.Setup.View!.IsSelectionResolved);
        fixture.Pump();

        Assert.Equal("metrics", fixture.Setup.View!.SelectedDataViewId);
        Assert.True(next.IsEffectivelyEnabled);
        var timestamp = list.GetVisualDescendants()
            .OfType<TextBlock>()
            .Single(text =>
                text.Text?.StartsWith("Timestamp field: @timestamp", StringComparison.Ordinal)
                    is true
            );
        Assert.True(timestamp.IsEffectivelyVisible);
        var item = timestamp.FindAncestorOfType<ListBoxItem>()!;
        Assert.True(item.IsSelected);
        Assert.Contains(
            fixture.Page.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "Data-view metadata loaded." && text.IsEffectivelyVisible
        );
    }

    [AvaloniaFact]
    public async Task TheDataViewStepShowsALoadFailureAndKeepsNextDisabled()
    {
        await using var fixture = await CreateAsync();
        fixture.Setup.StartNew();
        await PassConnectionTestAsync(fixture);
        fixture.Find<Button>("SetupNextButton").Command!.Execute(null);
        await UiWait.Until(() => fixture.Setup.Step == ElasticSetupStep.DataView);
        fixture.Pump();
        fixture.Client.DataViewHandler = _ => throw new IOException("kibana is down");

        fixture.Find<ListBox>("DataViewList").SelectedItem = fixture.Setup.FilteredDataViews[0];
        await UiWait.Until(() => fixture.Setup.View!.Error is not null);
        fixture.Pump();

        Assert.False(fixture.Find<Button>("SetupNextButton").IsEffectivelyEnabled);
        Assert.Contains(
            fixture.Page.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "kibana is down" && text.IsEffectivelyVisible
        );
    }

    [AvaloniaFact]
    public async Task ASetupErrorIsShownNextToTheButtons()
    {
        await using var fixture = await CreateAsync();
        fixture.Setup.StartNew();
        fixture.Pump();
        var error = fixture.Find<TextBlock>("SetupError");
        Assert.False(error.IsEffectivelyVisible);

        await PassConnectionTestAsync(fixture);
        fixture.Persistence.SaveError = new IOException("disk is full");
        fixture.Find<Button>("SetupNextButton").Command!.Execute(null);
        await UiWait.Until(() => fixture.Setup.Error is not null);
        fixture.Pump();

        Assert.True(error.IsEffectivelyVisible);
        Assert.Contains("disk is full", error.Text);
        fixture.Persistence.SaveError = null;
    }

    [AvaloniaFact]
    public async Task EveryInteractiveControlOfThePageHasAnAccessibleName()
    {
        await using var fixture = await CreateAsync();
        fixture.Setup.StartNew();
        fixture.Pump();
        var names = new List<string>();

        void Check(string stage)
        {
            fixture.Pump();
            var controls = fixture
                .Page.GetVisualDescendants()
                .OfType<Control>()
                // Parts of a control's own template are not separate controls for the user.
                .Where(control => control.TemplatedParent is null)
                .Where(control =>
                    control
                        is Button
                            or TextBox
                            or ComboBox
                            or ListBox
                            or AutoCompleteBox
                            or CheckBox
                )
                .ToArray();
            Assert.NotEmpty(controls);
            foreach (var control in controls)
            {
                var name = AutomationProperties.GetName(control);
                Assert.False(
                    string.IsNullOrWhiteSpace(name),
                    $"{stage}: {control.GetType().Name} {control.Name} has no AutomationProperties.Name"
                );
                names.Add(name!);
            }
        }

        fixture.Setup.Connection!.AuthMode = ElasticAuthMode.Basic;
        Check("connection step");
        fixture.Setup.Connection.AuthMode = ElasticAuthMode.Anonymous;
        await PassConnectionTestAsync(fixture);
        fixture.Find<Button>("SetupNextButton").Command!.Execute(null);
        await UiWait.Until(() => fixture.Setup.Step == ElasticSetupStep.DataView);
        Check("data view step");
        fixture.Find<ListBox>("DataViewList").SelectedItem = fixture.Setup.FilteredDataViews[0];
        await UiWait.Until(() => fixture.Setup.View!.IsSelectionResolved);
        Check("data view step with a selection");

        Assert.Contains("Cloud ID", names);
        Assert.Contains("Search data views", names);
    }

    [AvaloniaFact]
    public async Task ThePageFitsTheMinimumWindow()
    {
        await using var fixture = await CreateAsync();
        fixture.Window.Width = 720;
        fixture.Window.Height = 720;
        fixture.Setup.StartNew();
        fixture.Setup.Connection!.AuthMode = ElasticAuthMode.Basic;
        fixture.Pump();

        var page = fixture.Page;
        Assert.True(page.Bounds.Width <= 720.5);
        foreach (var name in new[] { "SetupNextButton", "SetupBackButton", "SetupCloseButton" })
        {
            var button = fixture.Find<Button>(name);
            var topLeft = button.TranslatePoint(default, fixture.Window)!.Value;
            Assert.True(topLeft.X >= 0 && topLeft.X + button.Bounds.Width <= 720.5, name);
            Assert.True(topLeft.Y >= 0 && topLeft.Y + button.Bounds.Height <= 720.5, name);
        }
    }
}
