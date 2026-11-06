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
using Avalonia.Xaml.Interactivity;
using HexTail.Application;
using HexTail.Behaviors;
using HexTail.Domain;
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
        Assert.Contains("done", buttons[0].Classes);
        Assert.Contains("current", buttons[1].Classes);
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

    private static IInputElement? Focused(Fixture fixture) =>
        TopLevel.GetTopLevel(fixture.Page)!.FocusManager!.GetFocusedElement();

    private static async Task WalkToDataViewStepAsync(Fixture fixture)
    {
        await PassConnectionTestAsync(fixture);
        fixture.Find<Button>("SetupNextButton").Command!.Execute(null);
        await UiWait.Until(() => fixture.Setup.Step == ElasticSetupStep.DataView);
        fixture.Pump();
    }

    [AvaloniaFact]
    public async Task AStepButtonJumpsToItsStepThroughTheRealCommand()
    {
        await using var fixture = await CreateAsync();
        fixture.Setup.StartNew();
        fixture.Pump();
        await WalkToDataViewStepAsync(fixture);
        var buttons = fixture
            .Find<ItemsControl>("SetupStepList")
            .GetVisualDescendants()
            .OfType<Button>()
            .ToArray();
        Assert.Same(fixture.Setup.GoToStepCommand, buttons[0].Command);
        Assert.True(buttons[0].IsEffectivelyEnabled);
        Assert.False(buttons[2].IsEffectivelyEnabled);

        buttons[0].Command!.Execute(buttons[0].CommandParameter);
        await UiWait.Until(() => fixture.Setup.Step == ElasticSetupStep.Connection);
        fixture.Pump();

        Assert.True(fixture.Find<TextBox>("CloudIdBox").IsEffectivelyVisible);
        Assert.Contains("current", buttons[0].Classes);
        Assert.True(buttons[1].IsEffectivelyEnabled);

        buttons[1].Command!.Execute(buttons[1].CommandParameter);
        await UiWait.Until(() => fixture.Setup.Step == ElasticSetupStep.DataView);
        fixture.Pump();
        Assert.True(fixture.Find<TextBox>("DataViewSearchBox").IsEffectivelyVisible);
        Assert.Contains("done", buttons[0].Classes);
    }

    [AvaloniaFact]
    public async Task ASelectedDataViewSurvivesBeingFilteredOutAndComingBack()
    {
        await using var fixture = await CreateAsync();
        fixture.Setup.StartNew();
        fixture.Pump();
        await WalkToDataViewStepAsync(fixture);
        var search = fixture.Find<TextBox>("DataViewSearchBox");
        var list = fixture.Find<ListBox>("DataViewList");
        var metrics = fixture.Setup.FilteredDataViews.Single(choice => choice.Id == "metrics");
        // Select the way a click does, through the row, so the list's own binding stays in place.
        list.GetVisualDescendants()
            .OfType<ListBoxItem>()
            .Single(row => ReferenceEquals(row.DataContext, metrics))
            .IsSelected = true;
        await UiWait.Until(() => fixture.Setup.View!.IsSelectionResolved);
        fixture.Pump();
        Assert.Same(metrics, list.SelectedItem);

        search.Text = "apm";
        fixture.Pump();
        Assert.Equal(1, list.ItemCount);
        Assert.Equal("metrics", fixture.Setup.View!.SelectedDataViewId);
        Assert.True(fixture.Setup.CanGoNext);

        search.Text = string.Empty;
        fixture.Pump();
        for (
            var attempt = 0;
            attempt < 100 && !ReferenceEquals(list.SelectedItem, metrics);
            attempt++
        )
        {
            fixture.Pump();
            await Task.Delay(10);
        }
        Assert.True(
            ReferenceEquals(list.SelectedItem, metrics),
            $"selected={(list.SelectedItem as ElasticDataViewChoiceViewModel)?.Id ?? "null"} vm={fixture.Setup.View.SelectedDataViewId} items={string.Join(",", fixture.Setup.FilteredDataViews.Select(c => c.Id))} listItems={list.ItemCount}"
        );

        Assert.Equal(3, list.ItemCount);
        Assert.Equal("metrics", fixture.Setup.View.SelectedDataViewId);
        Assert.True(fixture.Setup.View.IsSelectionResolved);
        var item = list.GetVisualDescendants()
            .OfType<ListBoxItem>()
            .Single(item => ReferenceEquals(item.DataContext, metrics));
        Assert.True(item.IsSelected);
    }

    [AvaloniaFact]
    public async Task TheConnectionNameIsEnteredOnTheFirstStepAndMustNotBeBlank()
    {
        await using var fixture = await CreateAsync();
        fixture.Setup.AutoTestDelay = TimeSpan.FromMinutes(10);
        fixture.Setup.StartNew();
        fixture.Pump();
        var name = fixture.Find<TextBox>("ConnectionNameBox");
        var error = fixture.Find<TextBlock>("ConnectionNameError");
        Assert.Equal("Connection name", AutomationProperties.GetName(name));
        Assert.Equal("Name this server", name.PlaceholderText);
        Assert.True(error.IsEffectivelyVisible);
        Assert.Equal("Enter a connection name.", error.Text);

        name.Text = "Production";
        fixture.Pump();
        Assert.Equal("Production", fixture.Setup.Connection!.Name);
        Assert.False(error.IsEffectivelyVisible);

        fixture.Setup.Connection.KibanaUrl = "https://kibana.example/";
        fixture.Setup.Connection.ElasticsearchUrl = "https://elastic.example/";
        await fixture.Setup.Connection.TestConnectionCommand.Execute().FirstAsync();
        fixture.Pump();
        Assert.True(fixture.Find<Button>("SetupNextButton").IsEffectivelyEnabled);
        name.Text = string.Empty;
        fixture.Pump();
        Assert.True(error.IsEffectivelyVisible);
        Assert.False(fixture.Find<Button>("SetupNextButton").IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public async Task APassingTestFillsTheBlankNameBoxFromTheHost()
    {
        await using var fixture = await CreateAsync();
        fixture.Setup.StartNew();
        fixture.Pump();

        await PassConnectionTestAsync(fixture);

        Assert.Equal("elastic.example", fixture.Find<TextBox>("ConnectionNameBox").Text);
        Assert.False(fixture.Find<TextBlock>("ConnectionNameError").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task TheDataViewStepOffersARetryWhenTheMetadataFailedToLoad()
    {
        await using var fixture = await CreateAsync();
        fixture.Setup.StartNew();
        fixture.Pump();
        await WalkToDataViewStepAsync(fixture);
        var retry = fixture.Find<Button>("DataViewRetryButton");
        Assert.Equal("Retry loading data view", AutomationProperties.GetName(retry));
        Assert.False(retry.IsEffectivelyVisible);
        var original = fixture.Client.DataViewHandler!;
        fixture.Client.DataViewHandler = _ => throw new IOException("kibana is down");

        fixture.Find<ListBox>("DataViewList").SelectedItem = fixture.Setup.FilteredDataViews[0];
        await UiWait.Until(() => fixture.Setup.View!.Error is not null);
        fixture.Pump();

        Assert.True(retry.IsEffectivelyVisible);
        Assert.Contains(
            fixture.Page.GetVisualDescendants().OfType<TextBlock>(),
            text =>
                text.Text == "Metadata could not be loaded. Choose Retry."
                && text.IsEffectivelyVisible
        );
        Assert.DoesNotContain(
            fixture.Page.GetVisualDescendants().OfType<TextBlock>(),
            text =>
                text.Text?.Contains("Test connection to retry", StringComparison.Ordinal) is true
        );

        fixture.Client.DataViewHandler = original;
        retry.Command!.Execute(null);
        await UiWait.Until(() => fixture.Setup.View!.IsSelectionResolved);
        fixture.Pump();

        Assert.False(retry.IsEffectivelyVisible);
        Assert.True(fixture.Find<Button>("SetupNextButton").IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public async Task ButtonAccessibleNamesContainTheirVisibleText()
    {
        await using var fixture = await CreateAsync();
        fixture.Setup.StartNew();
        fixture.Pump();
        await WalkToDataViewStepAsync(fixture);
        fixture.Client.DataViewHandler = _ => throw new IOException("kibana is down");
        fixture.Find<ListBox>("DataViewList").SelectedItem = fixture.Setup.FilteredDataViews[0];
        await UiWait.Until(() => fixture.Setup.View!.Error is not null);
        fixture.Pump();

        var buttons = fixture
            .Page.GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button.TemplatedParent is null && button.Content is string)
            .ToArray();
        Assert.Contains(buttons, button => button.Name == "SetupBackButton");
        foreach (var button in buttons)
            Assert.Contains(
                (string)button.Content!,
                AutomationProperties.GetName(button),
                StringComparison.OrdinalIgnoreCase
            );
    }

    [AvaloniaFact]
    public async Task TheLoginMethodsAreShownAsPlainText()
    {
        await using var fixture = await CreateAsync();
        fixture.Setup.StartNew();
        fixture.Pump();
        var auth = fixture.Find<ComboBox>("AuthModeBox");

        string Shown() =>
            string.Join(
                "|",
                auth.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Where(text => text.IsEffectivelyVisible)
                    .Select(text => text.Text)
            );

        Assert.Contains("Anonymous", Shown());
        fixture.Setup.Connection!.AuthMode = ElasticAuthMode.Basic;
        fixture.Pump();
        Assert.Contains("Username and password", Shown());
        fixture.Setup.Connection.AuthMode = ElasticAuthMode.ApiKey;
        fixture.Pump();
        Assert.Contains("API key", Shown());

        // The drop-down list lives in a popup, so ask the control for its items instead.
        Assert.Equal(
            ["Anonymous", "Username and password", "API key"],
            auth.Items.Cast<ElasticAuthModeChoice>().Select(choice => choice.DisplayName)
        );
        auth.SelectedItem = auth.Items.Cast<ElasticAuthModeChoice>().First();
        fixture.Pump();
        Assert.Equal(ElasticAuthMode.Anonymous, fixture.Setup.Connection.AuthMode);
    }

    [AvaloniaFact]
    public async Task OpeningThePageMovesFocusIntoTheStartingStep()
    {
        await using var fixture = await CreateAsync();
        fixture.Setup.StartNew();
        fixture.Pump();
        await UiWait.Until(() =>
            ReferenceEquals(Focused(fixture), fixture.Find<TextBox>("ConnectionNameBox"))
        );
        await PassConnectionTestAsync(fixture);
        fixture.Find<Button>("SetupNextButton").Command!.Execute(null);
        await UiWait.Until(() => fixture.Setup.Step == ElasticSetupStep.DataView);
        var connectionId = fixture.Setup.Connection!.Id;

        fixture.Setup.Close();
        fixture.Pump();
        fixture.Setup.AddSource(connectionId);
        fixture.Pump();

        await UiWait.Until(() =>
            ReferenceEquals(Focused(fixture), fixture.Find<TextBox>("DataViewSearchBox"))
        );

        fixture.Setup.Close();
        fixture.Pump();
        fixture.Setup.EditConnection(connectionId);
        fixture.Pump();

        await UiWait.Until(() =>
            ReferenceEquals(Focused(fixture), fixture.Find<TextBox>("ConnectionNameBox"))
        );
    }

    private static async Task WalkToFilterStepAsync(Fixture fixture)
    {
        fixture.Setup.StartNew();
        fixture.Pump();
        await WalkToDataViewStepAsync(fixture);
        fixture.Find<ListBox>("DataViewList").SelectedItem = fixture.Setup.FilteredDataViews[0];
        await UiWait.Until(() => fixture.Setup.View!.IsSelectionResolved);
        fixture.Pump();
        fixture.Find<Button>("SetupNextButton").Command!.Execute(null);
        await UiWait.Until(() => fixture.Setup.Step == ElasticSetupStep.Filter);
        fixture.Pump();
    }

    private static async Task WalkToColumnsStepAsync(Fixture fixture, params string[] values)
    {
        await WalkToFilterStepAsync(fixture);
        var view = fixture.Setup.View!;
        view.ServerField = "service";
        foreach (var value in values)
            view.AddFilterValue(value);
        fixture.Pump();
        fixture.Find<Button>("SetupNextButton").Command!.Execute(null);
        await UiWait.Until(() => fixture.Setup.Step == ElasticSetupStep.Columns);
        fixture.Pump();
    }

    private static ElasticHit Hit(string raw) =>
        new(raw, DateTimeOffset.UtcNow, new Line(raw), Array.Empty<System.Text.Json.JsonElement>());

    private static void PressEnter(Fixture fixture, Control control)
    {
        Assert.True(control.Focus());
        fixture.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        fixture.Pump();
    }

    private static Border[] Chips(Fixture fixture) =>
        fixture
            .Find<ItemsControl>("FilterChips")
            .GetVisualDescendants()
            .OfType<Border>()
            .Where(border => border.Classes.Contains("filter-chip") && border.IsEffectivelyVisible)
            .ToArray();

    private static string[] TextsIn(Visual root) =>
        root.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(text => text.IsEffectivelyVisible && !string.IsNullOrEmpty(text.Text))
            .Select(text => text.Text!)
            .ToArray();

    [AvaloniaFact]
    public async Task TheFilterStepAddsValuesAsChipsWithTheirCounts()
    {
        await using var fixture = await CreateAsync();
        fixture.Client.DataViewFields =
        [
            new("@timestamp", "date", true),
            new("message", "text", true),
            new("service", "keyword", true),
            new("bytes", "long", false),
        ];
        fixture.Client.CountHandler = args => Task.FromResult(args.Value == "empty" ? 0L : 3200L);
        await WalkToFilterStepAsync(fixture);
        var view = fixture.Setup.View!;

        var combo = fixture.Find<ComboBox>("FilterFieldBox");
        var options = combo.Items.Cast<ElasticFieldOptionViewModel>().ToArray();
        Assert.Equal(["@timestamp", "message", "service"], options.Select(option => option.Name));
        combo.SelectedItem = options.Single(option => option.Name == "service");
        fixture.Pump();
        Assert.Equal("service", view.ServerField);
        Assert.Contains("service · keyword", TextsIn(combo));
        Assert.Empty(Chips(fixture));

        var input = fixture.Find<TextBox>("FilterValueInput");
        input.Text = "checkout";
        PressEnter(fixture, input);
        await UiWait.Until(() => view.Sources.Any(source => source.CountText.StartsWith('~')));
        fixture.Pump();

        var chip = Assert.Single(Chips(fixture));
        Assert.Contains("checkout", TextsIn(chip));
        Assert.Contains("~3.2k docs · 15 min", TextsIn(chip));
        Assert.Equal(string.Empty, input.Text);
        Assert.DoesNotContain("zero-count", chip.Classes);

        input.Text = "a,b,";
        fixture.Pump();
        await UiWait.Until(() => view.Sources.All(source => source.CountText.StartsWith('~')));
        fixture.Pump();
        Assert.Equal(["checkout", "a", "b"], Chips(fixture).Select(border => TextsIn(border)[0]));
        Assert.Equal(string.Empty, input.Text);

        // A duplicate is ignored with a short note.
        input.Text = "a";
        PressEnter(fixture, input);
        Assert.Equal(3, Chips(fixture).Length);
        Assert.Contains(
            fixture.Page.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Name == "FilterMessage" && text.IsEffectivelyVisible
        );

        input.Text = "empty";
        PressEnter(fixture, input);
        await UiWait.Until(() => view.Sources.Any(source => source.IsZeroCount));
        fixture.Pump();
        var zero = Chips(fixture).Single(border => border.Classes.Contains("zero-count"));
        Assert.Contains("0 docs · 15 min", TextsIn(zero));

        var remove = zero.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => button.IsEffectivelyVisible);
        Assert.Equal("Remove value empty", AutomationProperties.GetName(remove));
        remove.Command!.Execute(remove.CommandParameter);
        fixture.Pump();
        Assert.Equal(["checkout", "a", "b"], view.Sources.Select(source => source.ServerValue));
        Assert.Equal(3, Chips(fixture).Length);
    }

    [AvaloniaFact]
    public async Task TheFilterFieldSurvivesTheFieldListBeingReloaded()
    {
        await using var fixture = await CreateAsync();
        await WalkToFilterStepAsync(fixture);
        var view = fixture.Setup.View!;
        var combo = fixture.Find<ComboBox>("FilterFieldBox");
        combo.SelectedItem = combo
            .Items.Cast<ElasticFieldOptionViewModel>()
            .Single(option => option.Name == "service");
        fixture.Pump();
        Assert.Equal("service", view.ServerField);

        await view.RefreshFieldsCommand.Execute();
        await UiWait.Until(() => view.IsSelectionResolved && !view.IsLoading);
        fixture.Pump();
        await UiWait.Until(() =>
            (combo.SelectedItem as ElasticFieldOptionViewModel)?.Name == "service"
        );

        Assert.Equal("service", view.ServerField);
        Assert.Contains("service · keyword", TextsIn(combo));
    }

    [AvaloniaFact]
    public async Task TheLegacyNamespaceInputsLiveInTheAdvancedExpander()
    {
        await using var fixture = await CreateAsync();
        await WalkToFilterStepAsync(fixture);
        fixture.Setup.View!.AddFilterValue("api");
        fixture.Pump();

        var expander = fixture.Find<Expander>("AdvancedExpander");
        Assert.False(expander.IsExpanded);
        Assert.DoesNotContain(
            fixture.Page.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text?.Contains("not queried", StringComparison.OrdinalIgnoreCase) is true
        );
        Assert.DoesNotContain(
            fixture.Page.GetVisualDescendants().OfType<TextBox>(),
            box => box.Text?.Contains("not queried", StringComparison.OrdinalIgnoreCase) is true
        );
        expander.IsExpanded = true;
        fixture.Pump();
        var namespaceBox = fixture.Find<Control>("NamespaceFieldBox");
        Assert.True(namespaceBox.IsEffectivelyVisible);
        Assert.Contains(expander, namespaceBox.GetVisualAncestors());
        Assert.DoesNotContain(
            fixture.Page.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text?.Contains("not queried", StringComparison.OrdinalIgnoreCase) is true
        );
        fixture.Find<TextBox>("NamespaceFieldBox").Text = "tenant";
        fixture.Pump();
        Assert.Equal("tenant", fixture.Setup.View.NamespaceField);
    }

    [AvaloniaFact]
    public async Task TheColumnsStepTicksReordersAndPreviewsTheFields()
    {
        await using var fixture = await CreateAsync();
        var hits = Enumerable.Range(1, 7).Select(index => Hit($"row {index}")).ToArray();
        fixture.Client.PreviewHandler = _ => Task.FromResult<IReadOnlyList<ElasticHit>>(hits);
        await WalkToColumnsStepAsync(fixture, "api");
        var view = fixture.Setup.View!;
        var available = fixture.Find<ItemsControl>("AvailableFieldsList");
        var selected = fixture.Find<ListBox>("SelectedFieldsList");

        void Tick(string name)
        {
            var box = available
                .GetVisualDescendants()
                .OfType<CheckBox>()
                .First(check => AutomationProperties.GetName(check) == $"Display field {name}");
            box.IsChecked = true;
            fixture.Pump();
        }

        Tick("message");
        Tick("service");
        Tick("@timestamp");
        Assert.Equal(["message", "service", "@timestamp"], selected.Items.Cast<string>());
        Assert.DoesNotContain(
            available.GetVisualDescendants().OfType<CheckBox>(),
            check => check.IsChecked == true
        );

        selected.SelectedItem = "@timestamp";
        fixture.Pump();
        fixture.Find<Button>("MoveFieldUpButton").Command!.Execute(null);
        fixture.Pump();
        Assert.Equal(["message", "@timestamp", "service"], view.ToSettings().OutputFields);
        Assert.Equal(["message", "@timestamp", "service"], selected.Items.Cast<string>());
        Assert.Equal("@timestamp", fixture.Setup.SelectedColumn);
        Assert.Equal("@timestamp", selected.SelectedItem);
        fixture.Find<Button>("MoveFieldDownButton").Command!.Execute(null);
        fixture.Pump();
        Assert.Equal(["message", "service", "@timestamp"], view.ToSettings().OutputFields);
        Assert.Equal("@timestamp", selected.SelectedItem);

        Assert.Single(Interaction.GetBehaviors(selected).OfType<ListReorderBehavior>());

        await UiWait.Until(() => view.PreviewRows.Count == 5);
        fixture.Pump();
        var preview = fixture.Find<ItemsControl>("PreviewList");
        Assert.Equal(5, preview.ItemCount);
        Assert.Contains("row 1", TextsIn(preview));

        fixture.Client.PreviewHandler = _ => Task.FromResult<IReadOnlyList<ElasticHit>>([]);
        fixture.Find<Button>("RefreshPreviewButton").Command!.Execute(null);
        await UiWait.Until(() => view.PreviewMessage == "No documents returned.");
        fixture.Pump();
        Assert.Equal(0, preview.ItemCount);
        Assert.Contains(
            fixture.Page.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "No documents returned." && text.IsEffectivelyVisible
        );
    }

    [AvaloniaFact]
    public async Task TheColumnsStepRemovesASelectedFieldAndSearchesTheRest()
    {
        await using var fixture = await CreateAsync();
        await WalkToColumnsStepAsync(fixture, "api");
        var view = fixture.Setup.View!;
        view.Fields.Single(field => field.Name == "message").IsOutput = true;
        fixture.Pump();
        var selected = fixture.Find<ListBox>("SelectedFieldsList");
        var available = fixture.Find<ItemsControl>("AvailableFieldsList");

        var remove = selected
            .GetVisualDescendants()
            .OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == "Remove column message");
        remove.Command!.Execute(remove.CommandParameter);
        fixture.Pump();
        Assert.Empty(selected.Items.Cast<string>());
        Assert.False(view.Fields.Single(field => field.Name == "message").IsOutput);

        fixture.Find<TextBox>("FieldSearchBox").Text = "serv";
        await UiWait.Until(() => available.ItemCount == 1);
        fixture.Pump();
        Assert.Equal(
            "service",
            Assert.IsType<ElasticFieldOptionViewModel>(Assert.Single(available.Items)).Name
        );
    }

    [AvaloniaFact]
    public async Task TheFinishStepPrefillsTheNameAndSavesAndOpensTheSources()
    {
        await using var fixture = await CreateAsync();
        await WalkToColumnsStepAsync(fixture, "api", "web");
        var view = fixture.Setup.View!;
        view.Fields.Single(field => field.Name == "message").IsOutput = true;
        fixture.Find<Button>("SetupNextButton").Command!.Execute(null);
        await UiWait.Until(() => fixture.Setup.Step == ElasticSetupStep.Finish);
        fixture.Pump();

        var name = fixture.Find<TextBox>("ViewNameBox");
        Assert.Equal("logs-* · api", name.Text);
        Assert.Equal(view.SuggestedName, name.Text);
        var open = fixture.Find<CheckBox>("OpenNowCheck");
        Assert.True(open.IsChecked);
        var next = fixture.Find<Button>("SetupNextButton");
        Assert.Equal("Save & open", next.Content);
        var summary = string.Join(" | ", TextsIn(fixture.Find<Control>("SetupFinishPanel")));
        Assert.Contains("2 sources", summary);
        Assert.Contains("message", summary);

        open.IsChecked = false;
        fixture.Pump();
        Assert.Equal("Save", next.Content);
        open.IsChecked = true;
        fixture.Pump();
        Assert.Equal("Save & open", next.Content);

        name.Text = "Checkout logs";
        fixture.Pump();
        Assert.Equal("Checkout logs", view.Name);

        next.Command!.Execute(null);
        await UiWait.Until(() => !fixture.Setup.IsOpen);
        fixture.Pump();

        Assert.False(fixture.Page.IsEffectivelyVisible);
        var saved = Assert.Single(
            Assert.Single(fixture.Persistence.Config!.Settings.ElasticConnections).Views
        );
        Assert.Equal("Checkout logs", saved.Name);
        Assert.Equal(2, fixture.Owner.State.Files.Count);
    }

    [AvaloniaFact]
    public async Task TheLaterStepsHaveAccessibleNamesAndFitTheMinimumWindow()
    {
        await using var fixture = await CreateAsync();
        fixture.Window.Width = 720;
        fixture.Window.Height = 720;
        fixture.Client.PreviewHandler = _ =>
            Task.FromResult<IReadOnlyList<ElasticHit>>([Hit("one")]);
        await WalkToColumnsStepAsync(fixture, "api", "web");
        fixture.Setup.View!.Fields.Single(field => field.Name == "message").IsOutput = true;

        void Check(string stage)
        {
            fixture.Pump();
            foreach (
                var control in fixture
                    .Page.GetVisualDescendants()
                    .OfType<Control>()
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
            )
                Assert.False(
                    string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)),
                    $"{stage}: {control.GetType().Name} {control.Name} has no AutomationProperties.Name"
                );
            Assert.True(fixture.Page.Bounds.Width <= 720.5, stage);
            foreach (var name in new[] { "SetupNextButton", "SetupBackButton", "SetupCloseButton" })
            {
                var button = fixture.Find<Button>(name);
                var topLeft = button.TranslatePoint(default, fixture.Window)!.Value;
                Assert.True(
                    topLeft.X >= 0 && topLeft.X + button.Bounds.Width <= 720.5,
                    $"{stage} {name}"
                );
                Assert.True(
                    topLeft.Y >= 0 && topLeft.Y + button.Bounds.Height <= 720.5,
                    $"{stage} {name}"
                );
            }
        }

        Check("columns step");
        fixture.Find<Button>("SetupBackButton").Command!.Execute(null);
        await UiWait.Until(() => fixture.Setup.Step == ElasticSetupStep.Filter);
        Check("filter step");
        fixture.Setup.View.AddFilterValue("third");
        fixture.Find<Expander>("AdvancedExpander").IsExpanded = true;
        Check("filter step with chips");
        fixture.Find<Button>("SetupNextButton").Command!.Execute(null);
        await UiWait.Until(() => fixture.Setup.Step == ElasticSetupStep.Columns);
        fixture.Find<Button>("SetupNextButton").Command!.Execute(null);
        await UiWait.Until(() => fixture.Setup.Step == ElasticSetupStep.Finish);
        Check("finish step");
    }
}
